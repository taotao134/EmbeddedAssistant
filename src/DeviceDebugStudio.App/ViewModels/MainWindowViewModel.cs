using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceDebugStudio.Core.Profiles;
using DeviceDebugStudio.Core.Protocol;
using DeviceDebugStudio.Core.Sessions;
using DeviceDebugStudio.Core.Terminal;
using DeviceDebugStudio.Core.Transports;
using DeviceDebugStudio.Infrastructure.Import;
using DeviceDebugStudio.Infrastructure.Persistence;
using DeviceDebugStudio.Infrastructure.Programming;
using DeviceDebugStudio.Infrastructure.Transports;
using DeviceDebugStudio.Infrastructure.Updates;
using Serilog;
using Wpf.Ui.Appearance;

namespace DeviceDebugStudio.App.ViewModels;

public sealed record TransportOption(TransportKind Kind, string Name);
public sealed record WorkspaceModeOption(WorkspaceMode Mode, string Name);
public sealed record QuickCommandCategoryOption(QuickCommandCategory Category, string Name);
public sealed record TerminalSeparatorStyleOption(string Character, string Name)
{
    public string DisplayText => $"{Character}  {Name}";
}
public sealed record FramingModeOption(FramingMode Mode, string Name, string Description);

public partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaximumTransportDiagnosticEventsPerSecond = 64;
    private const int MaximumPendingTerminalPackets = 16_384;
    private const int MaximumPendingChartValues = 4096;
    private const int MaximumPendingFrames = 8192;
    private const int ChartValuesPerUiTick = 200;
    private const int TerminalEvictionBatchSize = 1000;
    private const int FrameEvictionBatchSize = 500;
    private const int FrameRecordLimit = 20_000;
    private static readonly IReadOnlyDictionary<string, string> EmptyVariables =
        new Dictionary<string, string>();

    private readonly IConfigurableDeviceProfileStore _profileStore;
    private readonly LegacyConfigImporter _legacyImporter;
    private readonly AppSettingsStore _appSettingsStore;
    private readonly DeviceProfileFileService _profileFileService;
    private readonly CaptureFileReader _captureFileReader;
    private readonly OnlineUpdateService _updateService;
    private readonly BleDiscoveryService _bleDiscovery;
    private readonly BleGattBrowserService _bleGattBrowser;
    private readonly ConcurrentQueue<TransportPacket> _pendingTerminal = new();
    private readonly List<TransportPacket> _serialTerminalBuffer = [];
    private readonly TerminalWaveSeparatorTracker _terminalWaveSeparatorTracker = new();
    private readonly AnsiTerminalSession _ansiTerminal = new();
    private readonly ConcurrentQueue<FrameRecordItem> _pendingFrames = new();
    private readonly ConcurrentQueue<double> _pendingChartValues = new();
    private readonly object _transportDiagnosticLogLock = new();
    private readonly SemaphoreSlim _captureConfigurationLock = new(1, 1);
    private readonly object _captureConfigurationGate = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _repeatCommands = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _parameterRepeatCommands = [];
    private readonly Dictionary<QuickCommandCategory, List<QuickCommandItemViewModel>> _quickCommandsByCategory = [];
    private readonly ConcurrentDictionary<Guid, string> _importedProfileSourcePaths = new();
    private readonly ConcurrentDictionary<Guid, byte> _deletedProfileIds = new();
    private CancellationTokenSource? _sendRepeatCancellation;
    private readonly object _decoderSync = new();
    private readonly object _frameCodecSync = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _profileSaveTimer;
    private readonly DispatcherTimer _appSettingsSaveTimer;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ModbusSlaveSimulator _modbusSlave = new();
    private CommunicationSession? _session;
    private CancellationTokenSource? _consumeCancellation;
    private Task? _consumeTask;
    private IFrameCodec _frameCodec = new RawFrameCodec();
    private FrameTemplate _frameTemplate = new();
    private List<FrameTemplate> _frameTemplates = [new()];
    private long _rxTotal;
    private long _txTotal;
    private long _lastRateBytes;
    private DateTimeOffset _lastRateTimestamp = DateTimeOffset.Now;
    private DateTimeOffset _transportDiagnosticWindowStart = DateTimeOffset.MinValue;
    private DateTimeOffset _nextPortRefresh = DateTimeOffset.Now.AddSeconds(2);
    private int _portRefreshRunning;
    private int _connectionWorkerRunning;
    private int _transportDiagnosticEventCount;
    private bool _transportDiagnosticSuppressionLogged;
    private long _lastDisplayDropCount;
    private long _terminalPacketDropCount;
    private long _chartValueDropCount;
    private long _frameDropCount;
    private long _lastTerminalDropLogCount;
    private long _lastChartDropLogCount;
    private long _lastFrameDropLogCount;
    private DateTimeOffset _lastQueueSampleTimestamp = DateTimeOffset.MinValue;
    private bool _manualDisconnect;
    private bool _connectionDesired;
    private bool _systemSuspended;
    private CancellationTokenSource? _connectionAttemptCancellation;
    private CancellationTokenSource? _baudRateHotSwitchCancellation;
    private Decoder? _receiveDecoder;
    private string _decoderEncodingName = string.Empty;
    private DateTimeOffset _lastFrameInput;
    private bool _idleGapFlushed = true;
    private bool _suppressProfileSelection;
    private bool _replacingQuickCommands;
    private bool _switchingQuickCommandCategory;
    private QuickCommandCategory _quickCommandCategoryBeforeSelectionChange;
    private bool _suppressSendModeConversion;
    private bool _loadingTerminalDisplaySettings;
    private DeviceProfile? _activeProfile;
    private Task _lastProfileSaveTask = Task.CompletedTask;
    private long _profileSaveRevision;
    private int _sendCommandAvailabilityRefreshPending;
    private Task _lastAppSettingsSaveTask = Task.CompletedTask;
    private Task _captureConfigurationTask = Task.CompletedTask;
    private bool _captureConfigurationStopping;
    private readonly SemaphoreSlim _appSettingsSaveLock = new(1, 1);
    private WorkspaceMode? _connectedWorkspaceMode;
    private int _disposed;

    public MainWindowViewModel(
        IConfigurableDeviceProfileStore profileStore,
        LegacyConfigImporter legacyImporter,
        AppSettingsStore appSettingsStore,
        DeviceProfileFileService profileFileService,
        CaptureFileReader captureFileReader,
        OnlineUpdateService updateService,
        BleDiscoveryService bleDiscovery,
        BleGattBrowserService bleGattBrowser)
    {
        _profileStore = profileStore;
        _legacyImporter = legacyImporter;
        _appSettingsStore = appSettingsStore;
        _profileFileService = profileFileService;
        _captureFileReader = captureFileReader;
        _updateService = updateService;
        _bleDiscovery = bleDiscovery;
        _bleGattBrowser = bleGattBrowser;
        TftpClient.PreferencesChanged += OnTftpPreferencesChanged;
        JLink.PreferencesChanged += OnJLinkPreferencesChanged;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        selectedTransportOption = TransportOptions[0];
        selectedWorkspaceMode = WorkspaceModes[0];
        selectedQuickCommandCategory = QuickCommandCategories[0];
        selectedQuickCommandSort = QuickCommandSortOptions[0];
        selectedQuickCommandDataFormat = QuickCommandDataFormats[0];
        selectedTerminalSeparatorStyle = TerminalSeparatorStyles[0];
        selectedEncodingName = "UTF-8";
        selectedLineEnding = "CRLF";
        selectedSendChecksum = ChecksumKind.None;
        selectedFramingMode = FramingMode.Raw;
        frameTemplateJson = SerializeTemplate(_frameTemplate);
        selectedModbusFunction = "03 读保持寄存器";

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        _uiTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _uiTimer.Tick += OnUiTimerTick;
        _uiTimer.Start();
        _profileSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(700)
        };
        _profileSaveTimer.Tick += async (_, _) =>
        {
            _profileSaveTimer.Stop();
            DeviceProfile? snapshot = CreateActiveProfileSnapshot();
            if (snapshot is null)
            {
                return;
            }

            Task saveTask = QueueProfileSave(snapshot);
            await ObserveProfileSaveAsync(saveTask).ConfigureAwait(true);
        };
        _appSettingsSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _appSettingsSaveTimer.Tick += async (_, _) =>
        {
            _appSettingsSaveTimer.Stop();
            _lastAppSettingsSaveTask = SaveAppSettingsAsync();
            await _lastAppSettingsSaveTask.ConfigureAwait(true);
        };
        QuickCommandsView = CollectionViewSource.GetDefaultView(QuickCommands);
        QuickCommandsView.Filter = FilterQuickCommand;
        QuickCommands.CollectionChanged += OnQuickCommandsCollectionChanged;
        ApplyQuickCommandSort();

        for (ushort address = 0; address < 32; address++)
        {
            ModbusRegisterItem item = new(address);
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ModbusRegisterItem.Value))
                {
                    _modbusSlave.SetHoldingRegister(item.Address, item.Value);
                }
            };
            ModbusRegisters.Add(item);
        }

    }

    public IReadOnlyList<TransportOption> TransportOptions { get; } =
    [
        new(TransportKind.Serial, "串口"),
        new(TransportKind.TcpClient, "TCP 客户端"),
        new(TransportKind.TcpServer, "TCP 服务器"),
        new(TransportKind.Udp, "UDP"),
        new(TransportKind.BleGatt, "BLE GATT")
    ];

    public IReadOnlyList<WorkspaceModeOption> WorkspaceModes { get; } =
    [
        new(WorkspaceMode.Serial, "串口"),
        new(WorkspaceMode.Network, "TCP / UDP"),
        new(WorkspaceMode.Bluetooth, "蓝牙"),
        new(WorkspaceMode.Modbus, "Modbus"),
        new(WorkspaceMode.Tftp, "TFTP"),
        new(WorkspaceMode.JLink, "J-Link 烧录"),
        new(WorkspaceMode.PowerShell, "PowerShell")
    ];

    public IReadOnlyList<QuickCommandCategoryOption> QuickCommandCategories { get; } =
    [
        new(QuickCommandCategory.Serial, "串口"),
        new(QuickCommandCategory.Tcp, "TCP / UDP"),
        new(QuickCommandCategory.Bluetooth, "蓝牙"),
        new(QuickCommandCategory.Modbus, "Modbus"),
        new(QuickCommandCategory.Sscom, "SSCOM")
    ];

    public ObservableCollection<TransportOption> AvailableTransportOptions { get; } = [];
    public IReadOnlyList<string> QuickCommandSortOptions { get; } = ["使用频率", "最近使用", "手动顺序"];
    public IReadOnlyList<string> QuickCommandDataFormats { get; } = ["按指令", "ASCII", "UTF-8", "GBK", "HEX"];
    public IReadOnlyList<TerminalSeparatorStyleOption> TerminalSeparatorStyles { get; } =
    [
        new("-", "短横线"),
        new("*", "星号"),
        new("/", "斜线"),
        new("#", "井号")
    ];

    public IReadOnlyList<int> BaudRates { get; } = [9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600, 1000000, 2000000];
    public IReadOnlyList<string> EncodingNames { get; } = ["ASCII", "UTF-8", "GBK"];
    public IReadOnlyList<string> LineEndings { get; } = ["None", "CR", "LF", "CRLF"];
    public IReadOnlyList<ChecksumKind> ChecksumKinds { get; } = Enum.GetValues<ChecksumKind>();
    public IReadOnlyList<FramingModeOption> FramingModes { get; } =
    [
        new(FramingMode.Raw, "原始数据块", "按收到的数据块直接成帧。"),
        new(FramingMode.Line, "按行", "遇到换行符结束一帧。"),
        new(FramingMode.Delimiter, "分隔符", "遇到指定 HEX 分隔符结束一帧。"),
        new(FramingMode.FixedLength, "固定长度", "每 N 个字节组成一帧。"),
        new(FramingMode.LengthField, "长度字段", "按帧内长度字段计算整帧。"),
        new(FramingMode.IdleGap, "空闲间隔", "接收间隔超过设定时间结束上一帧。")
    ];
    public IReadOnlyList<string> ModbusFunctions { get; } =
    [
        "01 读线圈", "02 读离散输入", "03 读保持寄存器", "04 读输入寄存器",
        "05 写单线圈", "06 写单寄存器", "0F 写多线圈", "10 写多寄存器"
    ];

    public string ApplicationVersionText => $"v{_updateService.CurrentVersion.ToString(3)}";

    public ObservableCollection<DeviceProfile> Profiles { get; } = [];
    public ObservableCollection<SerialPortInfo> SerialPorts { get; } = [];
    public ObservableCollection<BleDeviceInfo> BleDevices { get; } = [];
    public ObservableCollection<BleGattServiceInfo> GattServices { get; } = [];
    public RangeObservableCollection<TerminalRecordItem> TerminalRecords { get; } = [];
    public RangeObservableCollection<FrameRecordItem> FrameRecords { get; } = [];
    public RangeObservableCollection<QuickCommandItemViewModel> QuickCommands { get; } = [];
    public ObservableCollection<ColorPaletteItem> TerminalTextPalette { get; } = [];
    public ObservableCollection<ColorPaletteItem> TerminalBackgroundPalette { get; } = [];
    public ICollectionView QuickCommandsView { get; }
    public ObservableCollection<ModbusRegisterItem> ModbusRegisters { get; } = [];
    public TftpClientViewModel TftpClient { get; } = new();
    public JLinkProgrammerViewModel JLink { get; } = new();
    public PowerShellViewModel PowerShell { get; } = new();
    public AnsiTerminalSession AnsiTerminal => _ansiTerminal;
    public int SelectedProfileDeleteCount => SelectedProfile is null
        ? 0
        : GetSelectedProfileDeleteTargets(SelectedProfile).Length;

    public event Action<int>? RecordsAppended;
    public event Action<double>? ChartValueAdded;
    public event Action<UpdateCheckResult>? UpdateAvailable;
    public event Action? AnsiTerminalReset;

    [ObservableProperty]
    private DeviceProfile? selectedProfile;

    [ObservableProperty]
    private string profileName = "快速调试";

    [ObservableProperty]
    private string profileDirectory = AppPaths.ProfilesDirectory;

    [ObservableProperty]
    private WorkspaceModeOption selectedWorkspaceMode;

    [ObservableProperty]
    private int selectedWorkspaceTabIndex;

    [ObservableProperty]
    private TransportOption selectedTransportOption;

    [ObservableProperty]
    private string portName = string.Empty;

    [ObservableProperty]
    private int baudRate = 115200;

    [ObservableProperty]
    private int dataBits = 8;

    [ObservableProperty]
    private SerialParity serialParity;

    [ObservableProperty]
    private SerialStopBits serialStopBits = SerialStopBits.One;

    [ObservableProperty]
    private SerialHandshake serialHandshake;

    [ObservableProperty]
    private bool dtrEnable;

    [ObservableProperty]
    private bool rtsEnable;

    [ObservableProperty]
    private int receiveTimeoutMs = 20;

    [ObservableProperty]
    private string host = "127.0.0.1";

    [ObservableProperty]
    private int remotePort = 777;

    [ObservableProperty]
    private string localAddress = "0.0.0.0";

    [ObservableProperty]
    private int localPort = 777;

    [ObservableProperty]
    private bool udpBroadcast;

    [ObservableProperty]
    private string multicastAddress = string.Empty;

    [ObservableProperty]
    private BleDeviceInfo? selectedBleDevice;

    [ObservableProperty]
    private string bleDiscoveryNotice = string.Empty;

    [ObservableProperty]
    private string bleServiceUuid = string.Empty;

    [ObservableProperty]
    private string bleReadUuid = string.Empty;

    [ObservableProperty]
    private string bleWriteUuid = string.Empty;

    [ObservableProperty]
    private string bleNotifyUuid = string.Empty;

    [ObservableProperty]
    private bool bleWriteWithoutResponse;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string gitHubRepository = string.Empty;

    [ObservableProperty]
    private bool autoUpdateEnabled = true;

    [ObservableProperty]
    private bool debugLoggingEnabled;

    [ObservableProperty]
    private bool captureCommunication;

    [ObservableProperty]
    private bool isUpdateBusy;

    [ObservableProperty]
    private string updateStatusText = "未检查";

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private string statusText = "就绪";

    [ObservableProperty]
    private string rateText = "0 B/s";

    [ObservableProperty]
    private long receivedBytes;

    [ObservableProperty]
    private long sentBytes;

    [ObservableProperty]
    private string sendText = string.Empty;

    [ObservableProperty]
    private bool isSendRepeating;

    [ObservableProperty]
    private bool sendRepeatEnabled;

    [ObservableProperty]
    private int sendRepeatIntervalMs = 1000;

    [ObservableProperty]
    private bool sendAsHex;

    [ObservableProperty]
    private bool receiveAsHex;

    [ObservableProperty]
    private string selectedEncodingName;

    [ObservableProperty]
    private string selectedLineEnding;

    [ObservableProperty]
    private ChecksumKind selectedSendChecksum;

    [ObservableProperty]
    private bool checksumLittleEndian = true;

    [ObservableProperty]
    private bool isPaused;

    [ObservableProperty]
    private bool autoScroll = true;

    [ObservableProperty]
    private double terminalFontSize = 12;

    public double TerminalTimeColumnWidth { get; private set; } = AppSettings.DefaultTerminalTimeColumnWidth;
    public double TerminalDirectionColumnWidth { get; private set; } = AppSettings.DefaultTerminalDirectionColumnWidth;
    public double TerminalEndpointColumnWidth { get; private set; } = AppSettings.DefaultTerminalEndpointColumnWidth;
    public double TerminalSizeColumnWidth { get; private set; } = AppSettings.DefaultTerminalSizeColumnWidth;
    public double TerminalContentColumnWidth { get; private set; } = AppSettings.DefaultTerminalContentColumnWidth;
    public double FrameTimeColumnWidth { get; private set; } = AppSettings.DefaultFrameTimeColumnWidth;
    public double FrameLengthColumnWidth { get; private set; } = AppSettings.DefaultFrameLengthColumnWidth;
    public double FrameHexColumnWidth { get; private set; } = AppSettings.DefaultFrameHexColumnWidth;
    public double FrameSummaryColumnWidth { get; private set; } = AppSettings.DefaultFrameSummaryColumnWidth;

    [ObservableProperty]
    private string terminalTextColor = App.DefaultTerminalTextColor;

    [ObservableProperty]
    private string terminalBackgroundColor = App.DefaultTerminalBackgroundColor;

    [ObservableProperty]
    private bool terminalSeparatorEnabled;

    [ObservableProperty]
    private double terminalSeparatorIntervalMs = AppSettings.DefaultTerminalSeparatorIntervalMs;

    [ObservableProperty]
    private TerminalSeparatorStyleOption selectedTerminalSeparatorStyle;

    [ObservableProperty]
    private string terminalSeparatorColor = App.DefaultTerminalSeparatorColor;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private string quickCommandSearchText = string.Empty;

    [ObservableProperty]
    private QuickCommandCategoryOption selectedQuickCommandCategory;

    [ObservableProperty]
    private string selectedQuickCommandSort;

    [ObservableProperty]
    private string selectedQuickCommandDataFormat;

    [ObservableProperty]
    private QuickCommandItemViewModel? selectedQuickCommand;

    [ObservableProperty]
    private FramingMode selectedFramingMode;

    [ObservableProperty]
    private string delimiterHex = "0D 0A";

    [ObservableProperty]
    private int fixedFrameLength = 8;

    [ObservableProperty]
    private int lengthFieldOffset = 1;

    [ObservableProperty]
    private int lengthFieldSize = 1;

    [ObservableProperty]
    private int lengthAdjustment;

    [ObservableProperty]
    private int idleGapMs = 20;

    [ObservableProperty]
    private string frameTemplateJson;

    [ObservableProperty]
    private bool chartAutoExtract;

    [ObservableProperty]
    private string chartValuePattern = @"[-+]?\d+(?:\.\d+)?";

    [ObservableProperty]
    private int modbusUnitId = 1;

    [ObservableProperty]
    private string selectedModbusFunction;

    [ObservableProperty]
    private int modbusAddress;

    [ObservableProperty]
    private int modbusQuantityOrValue = 1;

    [ObservableProperty]
    private string modbusValues = string.Empty;

    [ObservableProperty]
    private int modbusTransactionId = 1;

    [ObservableProperty]
    private bool modbusSlaveEnabled;

    public string ConnectionButtonText => SelectedTransportKind == TransportKind.TcpServer
        ? IsConnected ? "停止监听" : _connectionDesired ? "正在监听…" : "开始监听"
        : IsConnected ? "断开" : _connectionDesired ? "正在连接…" : "连接";
    public string DiagnosticsDirectory => AppPaths.DiagnosticsDirectory;
    public string UpdateDiagnosticsDirectory => AppPaths.UpdateDiagnosticsDirectory;
    public string CaptureDirectory => AppPaths.CaptureDirectory;
    public string TerminalSeparatorFillText => new(TerminalSeparatorCharacter, 160);
    public string TerminalSeparatorPreviewText => BuildTerminalSeparatorLine(TerminalSeparatorIntervalMs, 10);
    private char TerminalSeparatorCharacter => string.IsNullOrEmpty(SelectedTerminalSeparatorStyle?.Character)
        ? AppSettings.DefaultTerminalSeparatorStyle[0]
        : SelectedTerminalSeparatorStyle.Character[0];

    public string BuildTerminalSeparatorLine(double gapMilliseconds, int fillLength = 28)
    {
        string fill = new(TerminalSeparatorCharacter, Math.Clamp(fillLength, 1, 80));
        return $"{fill}  {TerminalRecordItem.FormatSeparatorGap(gapMilliseconds)}  {fill}";
    }
    public string SelectedFramingModeDescription => FramingModes
        .FirstOrDefault(option => option.Mode == SelectedFramingMode)?.Description
        ?? "请选择一种分帧方式。";
    public string ConnectionSummary => SelectedTransportKind switch
    {
        TransportKind.Serial => $"{PortName} · {BaudRate}",
        TransportKind.TcpClient => $"{Host}:{RemotePort}",
        TransportKind.TcpServer => $"{LocalAddress}:{LocalPort}",
        TransportKind.Udp => $"{LocalPort} → {Host}:{RemotePort}",
        TransportKind.BleGatt => SelectedBleDevice?.DisplayName ?? "未选择 BLE 设备",
        _ => string.Empty
    };

    public bool IsSerialWorkspace => SelectedWorkspaceMode.Mode == WorkspaceMode.Serial;
    public bool IsNetworkWorkspace => SelectedWorkspaceMode.Mode == WorkspaceMode.Network;
    public bool IsBluetoothWorkspace => SelectedWorkspaceMode.Mode == WorkspaceMode.Bluetooth;
    public bool IsModbusWorkspace => SelectedWorkspaceMode.Mode == WorkspaceMode.Modbus;
    public bool IsTftpWorkspace => SelectedWorkspaceMode.Mode == WorkspaceMode.Tftp;
    public bool IsJLinkWorkspace => SelectedWorkspaceMode.Mode == WorkspaceMode.JLink;
    public bool IsPowerShellWorkspace => SelectedWorkspaceMode.Mode == WorkspaceMode.PowerShell;
    public bool IsFrameWorkspaceVisible => SelectedWorkspaceMode.Mode is WorkspaceMode.Serial or WorkspaceMode.Network;
    public bool IsChartWorkspaceVisible => SelectedWorkspaceMode.Mode is WorkspaceMode.Serial or WorkspaceMode.Network or WorkspaceMode.Bluetooth;
    public string TerminalTabHeader => IsModbusWorkspace
        ? "通信记录"
        : SelectedTransportKind switch
        {
            TransportKind.BleGatt => "BLE 终端",
            TransportKind.Serial => "串口终端",
            TransportKind.TcpClient or TransportKind.TcpServer or TransportKind.Udp => "网络终端",
            _ => "终端"
        };

    public async Task InitializeAsync(AppSettings? startupSettings = null)
    {
        AppSettings settings = startupSettings ?? await _appSettingsStore.LoadAsync().ConfigureAwait(true);
        try
        {
            _profileStore.SetDirectory(settings.ProfileDirectory);
        }
        catch (Exception)
        {
            _profileStore.SetDirectory(AppPaths.ProfilesDirectory);
        }
        ProfileDirectory = _profileStore.DirectoryPath;
        _importedProfileSourcePaths.Clear();
        foreach ((Guid profileId, string sourcePath) in settings.ImportedProfileSourcePaths)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                continue;
            }

            try
            {
                _importedProfileSourcePaths[profileId] = Path.GetFullPath(sourcePath);
            }
            catch (ArgumentException)
            {
                // 忽略历史设置中的无效路径，当前配置仍可保存到默认目录。
            }
        }
        ApplyTerminalDisplaySettings(settings);
        GitHubRepository = string.IsNullOrWhiteSpace(settings.GitHubRepository)
            ? AppSettings.DefaultGitHubRepository
            : settings.GitHubRepository.Trim();
        AutoUpdateEnabled = settings.AutoUpdateEnabled;
        DebugLoggingEnabled = settings.DebugLoggingEnabled;
        UpdateAvailableTransportOptions();
        await ReloadProfilesAsync(settings.SelectedProfileId).ConfigureAwait(true);
        if (Profiles.Count == 0)
        {
            DeviceProfile profile = new()
            {
                Name = "快速调试",
                WorkspaceMode = WorkspaceMode.Serial,
                Terminal = new TerminalPreferences { LineEnding = "CRLF" },
                CommandGroups =
                [
                    new QuickCommandGroup
                    {
                        Commands =
                        [
                            new QuickCommand { Name = "AT", Payload = "AT", LineEnding = "CRLF" },
                            new QuickCommand { Name = "状态查询", Payload = "$GETSTATUS", LineEnding = "CRLF" }
                        ]
                    }
                ]
            };
            await _profileStore.SaveAsync(profile).ConfigureAwait(true);
            await ReloadProfilesAsync(profile.Id).ConfigureAwait(true);
        }
        StartPortRefreshInBackground();
        _ = CheckForUpdatesInBackgroundAsync();
    }

    public async Task<UpdateCheckResult?> CheckForUpdatesAsync()
    {
        if (IsUpdateBusy)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(GitHubRepository))
        {
            UpdateStatusText = "未配置 GitHub 仓库";
            return null;
        }

        IsUpdateBusy = true;
        UpdateStatusText = "正在检查 GitHub 更新…";
        try
        {
            UpdateCheckResult result = await _updateService
                .CheckGitHubAsync(GitHubRepository)
                .ConfigureAwait(true);
            UpdateStatusText = result.IsUpdateAvailable
                ? $"发现新版本 {result.LatestVersion}"
                : $"当前已是最新版本 {result.CurrentVersion}";
            return result;
        }
        catch (Exception exception)
        {
            OnlineUpdateService.ArchiveFailure("检查 GitHub 更新", exception);
            UpdateStatusText = $"检查更新失败：{exception.Message}";
            return null;
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    public async Task<bool> DownloadAndApplyUpdateAsync(
        UpdateCheckResult result,
        IProgress<UpdateProgressInfo>? updateProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (!result.IsUpdateAvailable || IsUpdateBusy)
        {
            return false;
        }

        IsUpdateBusy = true;
        try
        {
            void ApplyProgress(UpdateProgressInfo info)
            {
                updateProgress?.Report(info);
                UpdateStatusText = info.Phase switch
                {
                    UpdateProgressPhase.Downloading => info.TotalBytes is > 0
                        ? $"正在下载 {result.LatestVersion}：{FormatByteSize(info.BytesDownloaded)} / {FormatByteSize(info.TotalBytes.Value)} ({info.Progress:P0})"
                        : $"正在下载 {result.LatestVersion}：{info.Progress:P0}",
                    UpdateProgressPhase.Verifying => $"正在校验更新包 {result.LatestVersion}…",
                    UpdateProgressPhase.PreparingToRestart => "更新已准备，程序即将重启",
                    _ => $"正在更新 {result.LatestVersion}…"
                };
            }

            ApplyProgress(new(
                UpdateProgressPhase.Downloading,
                0,
                0,
                result.Manifest.PackageSize));
            Progress<UpdateProgressInfo> detailedProgress = new(ApplyProgress);
            string packagePath = await _updateService
                .DownloadAsync(
                    result.Manifest,
                    cancellationToken: cancellationToken,
                    detailedProgress: detailedProgress)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            ApplyProgress(new(
                UpdateProgressPhase.PreparingToRestart,
                1,
                result.Manifest.PackageSize ?? 0,
                result.Manifest.PackageSize));
            _updateService.StartInstaller(packagePath);
            UpdateStatusText = "更新已准备，程序即将重启";
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            UpdateStatusText = "更新已取消";
            return false;
        }
        catch (Exception exception)
        {
            OnlineUpdateService.ArchiveFailure("下载或准备更新", exception);
            UpdateStatusText = $"更新失败：{exception.Message}";
            return false;
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    private static string FormatByteSize(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024d:0.0} MB";
        }

        if (bytes >= 1024)
        {
            return $"{bytes / 1024d:0.0} KB";
        }

        return $"{bytes:N0} B";
    }

    private async Task CheckForUpdatesInBackgroundAsync()
    {
        if (!AutoUpdateEnabled || string.IsNullOrWhiteSpace(GitHubRepository))
        {
            return;
        }

        UpdateCheckResult? result = await CheckForUpdatesAsync().ConfigureAwait(true);
        if (result?.IsUpdateAvailable == true)
        {
            UpdateAvailable?.Invoke(result);
        }
    }

    public async Task ImportLegacyAsync(string directory)
    {
        IsBusy = true;
        StatusText = "正在导入旧配置…";
        try
        {
            LegacyImportResult result = await _legacyImporter.ImportDirectoryAsync(directory).ConfigureAwait(true);
            await SaveImportedLegacyProfilesAsync(result).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            StatusText = $"导入失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ImportLegacyFilesAsync(IEnumerable<string> paths)
    {
        IsBusy = true;
        StatusText = "正在导入 SSCOM / NetAssist 文件…";
        try
        {
            LegacyImportResult result = await _legacyImporter.ImportFilesAsync(paths).ConfigureAwait(true);
            await SaveImportedLegacyProfilesAsync(result).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            StatusText = $"导入失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ImportDeviceProfilesAsync(IEnumerable<string> paths)
    {
        IsBusy = true;
        try
        {
            int count = 0;
            foreach (string path in paths)
            {
                string sourcePath = Path.GetFullPath(path);
                DeviceProfile profile = await _profileFileService.ImportAsync(sourcePath).ConfigureAwait(true);
                await _profileStore.SaveAsync(profile).ConfigureAwait(true);
                _importedProfileSourcePaths[profile.Id] = sourcePath;
                count++;
            }
            await ReloadProfilesAsync().ConfigureAwait(true);
            await SaveAppSettingsAsync().ConfigureAwait(true);
            StatusText = $"已导入 {count} 个设备配置到：{_profileStore.DirectoryPath}";
        }
        catch (Exception exception)
        {
            StatusText = $"设备配置导入失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public string BuildAiImportPrompt(string projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            throw new ArgumentException("工程目录不能为空。", nameof(projectDirectory));
        }

        string resolvedDirectory = Path.GetFullPath(projectDirectory.Trim());
        if (!Directory.Exists(resolvedDirectory))
        {
            throw new DirectoryNotFoundException($"工程目录不存在：{resolvedDirectory}");
        }

        return $$"""
            你是嵌入式通信协议分析和 DeviceDebugStudio 指令配置生成助手。

            当前工程根目录（由用户在上位机中选择）
            {{resolvedDirectory}}

            请在上述目录内自行递归搜索并分析嵌入式工程，不要假设固定盘符、固定工程名、固定源码文件名，也不要假设一定存在 proc_cmd.c、user_cmd.c 或已有串口 JSON 文件。

            任务目标：根据固件真实源码提取全部串口、TCP、UDP 或其他文本指令，生成一份可以直接导入 DeviceDebugStudio 的 JSON 文件。

            【必须自行搜索的内容】
            1. 递归检查 .c、.h、.cpp、.hpp 及工程配置文件。
            2. 搜索串口、TCP、UDP 接收入口、命令解析器、命令分发表、字符串比较、参数解析、printf/sprintf/snprintf、sscanf、strtok、argc/argv、$、AT、GET、SET 等关键内容。
            3. 沿着函数调用继续追踪实际命令处理函数、参数校验、状态分支、枚举表、宏定义和响应字符串。
            4. 不要只检查文件名中看起来像命令处理的文件，必须覆盖工程中所有实际注册或解析指令的代码。

            【协议真实性要求】
            1. 只收录固件源码中真实存在的指令，不得根据经验、函数名或设备名称臆造指令。
            2. 保留固件真实的命令字符串、前缀、大小写、分隔符、参数顺序和参数个数，不要重命名协议指令。
            3. 每个参数都要提取源码真实含义、单位、默认值、范围、格式和允许值。
            4. 对 0、1 或其他数字开关/枚举，必须依据源码分支、查表或宏定义写清楚真实含义。例如“0=关闭，1=开启”或“0=70MHz，1=720MHz”。禁止猜测；源码无法确认时标记为“源码未确认”。
            5. 同时记录成功响应、失败响应和错误原因；如果这些信息不能表达在 JSON 字段中，写入配套核对表。

            【DeviceDebugStudio JSON要求】
            1. 在当前工程根目录下创建 Docs 目录（不存在则创建）。
            2. 生成文件：Docs\DeviceDebugStudio_指令导入.json。
            3. 使用当前软件支持的 SchemaVersion、WorkspaceMode、Transport、Terminal、CommandGroups、Commands、Payload、Template、Variables、VariableSets、SelectedVariableSetId 等字段，不添加软件不支持的自定义字段。
            4. 默认 WorkspaceMode 使用 Serial，Transport 使用 serial；串口默认参数从工程配置、协议文档或源码中提取，无法确认时使用合理占位值并在核对表标记。
            5. 串口和 TCP/UDP 使用同一份 JSON。对于相同的文本指令，在 Serial 和 TCP/UDP 指令组中保持一致；如果某条指令只支持特定传输方式，按源码真实情况归组并在核对表说明。
            6. 无参数指令的 Payload 和 Template 使用固件中的完整命令。
            7. 有参数指令必须使用变量模板。例如实际默认命令为 $SETIFFREQ,720，应生成 Payload 为 "$SETIFFREQ,720"，Template 为 "$SETIFFREQ,${freq_mhz}"，并为 freq_mhz 建立可编辑变量。
            8. 使用软件支持的变量类型“数值”“枚举”“开关”。开关和枚举参数必须建立有明确中文含义的 VariableSets，并保存每个方案对应的真实发送值。数值参数必须保存源码或协议中的默认值。
            9. 不要把键盘快捷键 Shortcut 和指令参数方案混淆；指令参数放在 Variables 和 VariableSets 中。
            10. 必须保持 Payload 与 Template 解耦：Payload 是快捷指令栏直接编辑和发送的完整命令；Template 是展开编辑器中的方案模板。选择方案或修改方案变量只允许影响 Template 的方案预览和“使用此方案发送”，不得改写 Payload。
            11. 修改 Payload 中的参数不得改写 Template、Variables 或 VariableSets；即使无参数模板的初始 Payload 与 Template 相同，导入后也必须作为两个独立字段保存。
            12. 指令栏发送/快捷指令循环发送使用 Payload；方案编辑器发送/快捷参数循环发送使用 Template 结合 SelectedVariableSetId 对应 VariableSet 展开后的内容。

            【核对表】
            同时生成 Docs\DeviceDebugStudio_指令提取核对表.md，记录每条指令的：实际命令、参数、参数含义、0/1及枚举映射、默认值、单位、范围、响应、源码文件和行号。
            对源码无法确认的内容单独列出，不要猜测或静默省略。

            【生成后验证】
            1. 使用结构化 JSON 解析器验证 JSON 格式。
            2. 检查所有 Template 变量都存在于 Variables 或 VariableSets，且 SelectedVariableSetId 有效。
            3. 分别检查 Payload 的默认参数数量、顺序和固件解析逻辑，以及 Template 的变量顺序和方案展开结果；不要用方案值覆盖 Payload。
            4. 检查没有重复指令、遗漏指令、虚构指令或改变真实命名的指令。
            5. 检查串口和 TCP/UDP 指令组符合当前 DeviceDebugStudio 导入结构。

            最后汇报：扫描过的源码文件、生成的 JSON 路径、指令数量、参数方案数量、确认的开关/枚举含义、未确认问题以及 JSON 验证结果。
            """;
    }

    public async Task ExportSelectedProfileAsync(string path)
    {
        DeviceProfile? snapshot = CreateActiveProfileSnapshot();
        if (snapshot is null)
        {
            StatusText = "没有可导出的设备配置";
            return;
        }
        await _profileFileService.ExportAsync(snapshot, path).ConfigureAwait(true);
        StatusText = $"设备配置已导出：{path}";
    }

    public async Task OpenCaptureAsync(string path)
    {
        IsBusy = true;
        StatusText = "正在打开捕获数据库…";
        try
        {
            CaptureOpenResult result = await _captureFileReader.ReadAsync(path).ConfigureAwait(true);
            TerminalRecords.Clear();
            _terminalWaveSeparatorTracker.Reset();
            List<TerminalRecordItem> batch = new(1000);
            foreach (TransportPacket packet in result.Packets)
            {
                AppendFormattedPacket(batch, packet);
                if (batch.Count >= 1000)
                {
                    TerminalRecords.AddRange(batch);
                    batch.Clear();
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }
            }
            TerminalRecords.AddRange(batch);
            RecordsAppended?.Invoke(TerminalRecords.Count);
            StatusText = result.TotalPackets > result.Packets.Count
                ? $"已打开捕获：{path}，显示最后 {result.Packets.Count:N0}/{result.TotalPackets:N0} 条"
                : $"已打开捕获：{path}，共 {result.TotalPackets:N0} 条";
        }
        catch (Exception exception)
        {
            StatusText = $"捕获数据库打开失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ConfigureProfileDirectoryAsync(string directory)
    {
        DeviceProfile? active = CreateActiveProfileSnapshot();
        List<DeviceProfile> profiles = Profiles.ToList();
        if (active is not null)
        {
            int index = profiles.FindIndex(profile => profile.Id == active.Id);
            if (index >= 0)
            {
                profiles[index] = active;
            }
            else
            {
                profiles.Add(active);
            }
        }

        _profileStore.SetDirectory(directory);
        foreach (DeviceProfile profile in profiles)
        {
            await _profileStore.SaveAsync(profile).ConfigureAwait(true);
        }
        await SaveAppSettingsAsync().ConfigureAwait(true);
        ProfileDirectory = _profileStore.DirectoryPath;
        await ReloadProfilesAsync(active?.Id).ConfigureAwait(true);
        StatusText = $"设备配置保存位置已设为：{ProfileDirectory}";
    }

    public async Task SendFileAsync(string path, int chunkSize = 256, int intervalMs = 1, CancellationToken cancellationToken = default)
    {
        if (_session is null || !IsConnected)
        {
            StatusText = "请先建立连接";
            return;
        }

        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, chunkSize, FileOptions.Asynchronous);
        byte[] buffer = new byte[Math.Clamp(chunkSize, 1, 64 * 1024)];
        long sent = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await _session.SendAsync(buffer.AsMemory(0, count), cancellationToken: cancellationToken).ConfigureAwait(true);
            sent += count;
            StatusText = $"文件发送 {sent}/{stream.Length} 字节";
            if (intervalMs > 0)
            {
                await Task.Delay(intervalMs, cancellationToken).ConfigureAwait(true);
            }
        }
        StatusText = $"文件发送完成：{sent} 字节";
    }

    public Task<bool> SendAnsiTerminalTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Task.FromResult(false);
        }

        return SendAnsiTerminalBytesAsync(
            GetSelectedEncoding().GetBytes(text),
            "ANSI终端文本");
    }

    public async Task<bool> SendAnsiTerminalBytesAsync(
        ReadOnlyMemory<byte> data,
        string source = "ANSI终端按键",
        CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty || !CanUseConnectedSession || _session is not { } session)
        {
            return false;
        }

        byte[] payload = data.ToArray();
        try
        {
            await session.SendAsync(
                    payload,
                    cancellationToken: cancellationToken,
                    sentAsHex: false)
                .ConfigureAwait(true);
            LogTransportDiagnostic(
                PacketDirection.Send,
                source,
                session.Transport.DisplayName,
                payload);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException)
        {
            Log.Warning(exception, "ANSI终端发送失败 | 来源={Source}", source);
            return false;
        }
    }

    public string ExportTerminalText() => string.Join(
        Environment.NewLine,
        TerminalRecords
            .Where(item => !item.IsSeparator || TerminalSeparatorEnabled)
            .Select(item => item.IsSeparator
                ? BuildTerminalSeparatorLine(item.SeparatorGapMilliseconds)
                : $"{item.Timestamp:O}\t{item.DirectionText}\t{item.Endpoint}\t{item.GetDisplayContent(ReceiveAsHex)}"));

    public string ExportTerminalTableCsv()
    {
        StringBuilder builder = new();
        builder.AppendLine("时间,方向,端点,字节,内容");
        foreach (TerminalRecordItem item in TerminalRecords)
        {
            if (item.IsSeparator)
            {
                if (TerminalSeparatorEnabled)
                {
                    builder.Append(",,,,");
                    AppendCsvField(builder, BuildTerminalSeparatorLine(item.SeparatorGapMilliseconds));
                    builder.AppendLine();
                }
                continue;
            }

            AppendCsvField(builder, item.TimeText);
            builder.Append(',');
            AppendCsvField(builder, item.DirectionText);
            builder.Append(',');
            AppendCsvField(builder, item.Endpoint);
            builder.Append(',').Append(item.Size.ToString(CultureInfo.InvariantCulture)).Append(',');
            AppendCsvField(builder, item.GetDisplayContent(ReceiveAsHex));
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static void AppendCsvField(StringBuilder builder, string value) =>
        builder.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');

    [RelayCommand]
    private async Task RefreshPortsAsync()
    {
        if (Interlocked.Exchange(ref _portRefreshRunning, 1) != 0)
        {
            return;
        }
        await RefreshPortsInBackgroundAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ScanBleAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        BleDiscoveryNotice = string.Empty;
        StatusText = "正在扫描 BLE 广播…";
        try
        {
            IReadOnlyList<BleDeviceInfo> devices = await _bleDiscovery.ScanAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            BleDevices.Clear();
            foreach (BleDeviceInfo device in devices)
            {
                BleDevices.Add(device);
            }
            ulong preferredAddress = SelectedBleDevice?.Address
                ?? (_activeProfile?.Transport as BleGattTransportSettings)?.BluetoothAddress
                ?? 0;
            SelectedBleDevice = devices.FirstOrDefault(device => device.Address == preferredAddress)
                ?? devices.FirstOrDefault();

            List<string> notices = [];
            if (!string.IsNullOrWhiteSpace(_bleDiscovery.LastWatcherError))
            {
                notices.Add($"广播扫描异常：{_bleDiscovery.LastWatcherError}");
            }
            notices.Add($"广播 {_bleDiscovery.LastAdvertisementCount} 个，系统 BLE {_bleDiscovery.LastSystemCount} 个。");
            if (devices.Count == 0)
            {
                notices.Add("未发现 BLE 设备。请确认设备在广播，或已在 Windows 蓝牙设置中配对。");
            }
            else if (_bleDiscovery.LastAdvertisementCount == 0 && _bleDiscovery.LastSystemCount > 0)
            {
                notices.Add("当前仅来自 Windows 系统已关联 BLE 列表（设备可能已停止广播）。Windows 显示“电脑”只是 Appearance 分类，仍可尝试浏览 GATT。");
            }
            if (_bleDiscovery.LastClassicDeviceNames.Count > 0)
            {
                string classicNames = string.Join("、", _bleDiscovery.LastClassicDeviceNames);
                notices.Add($"另检测到经典蓝牙：{classicNames}。经典设备不能用 BLE GATT 连接。");
            }
            BleDiscoveryNotice = string.Join(" ", notices);
            StatusText = $"发现 {devices.Count} 个 BLE 设备（广播 {_bleDiscovery.LastAdvertisementCount} / 系统 {_bleDiscovery.LastSystemCount}）";
        }
        catch (Exception exception)
        {
            StatusText = $"BLE 扫描失败：{exception.Message}";
            BleDiscoveryNotice = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task BrowseGattAsync()
    {
        if (SelectedBleDevice is null)
        {
            StatusText = "请先选择 BLE 设备";
            return;
        }

        IsBusy = true;
        StatusText = "正在读取 GATT 服务…";
        try
        {
            IReadOnlyList<BleGattServiceInfo> services = await _bleGattBrowser.BrowseAsync(SelectedBleDevice.Address).ConfigureAwait(true);
            GattServices.Clear();
            foreach (BleGattServiceInfo service in services)
            {
                GattServices.Add(service);
            }
            StatusText = $"读取到 {services.Count} 个 GATT 服务";
        }
        catch (Exception exception)
        {
            StatusText = $"GATT 浏览失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ToggleConnection()
    {
        _connectionDesired = !_connectionDesired;
        _manualDisconnect = !_connectionDesired;
        if (!_connectionDesired)
        {
            CancelPendingBaudRateHotSwitch();
            SendRepeatEnabled = false;
            StopSendRepeat();
            _connectionAttemptCancellation?.Cancel();
            IsConnected = false;
        }
        OnPropertyChanged(nameof(ConnectionButtonText));
        EnsureConnectionStateWorker();
    }

    private bool CanUseConnectedSession => IsConnected
        && _session is { Transport.State: TransportState.Connected }
        && _connectedWorkspaceMode == SelectedWorkspaceMode.Mode;

    public bool CanSend => CanUseConnectedSession && !string.IsNullOrEmpty(SendText);

    private bool CanSendQuickCommand(QuickCommandItemViewModel? command) =>
        CanUseConnectedSession && command is not null && !string.IsNullOrEmpty(command.Payload);

    private bool CanSendQuickParameterCommand(QuickCommandItemViewModel? command) =>
        CanUseConnectedSession && command is not null && !string.IsNullOrEmpty(command.ResolvedPayload);

    private bool CanToggleQuickRepeat(QuickCommandItemViewModel? command) =>
        CanUseConnectedSession && command is not null && !string.IsNullOrEmpty(command.Payload);

    private bool CanToggleQuickParameterRepeat(QuickCommandItemViewModel? command) =>
        CanUseConnectedSession && command is not null && !string.IsNullOrEmpty(command.ResolvedPayload);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        try
        {
            Log.Information(
                "底部发送命令已触发 | 已连接={IsConnected} | 可发送={CanSend} | 文本长度={TextLength}",
                IsConnected,
                CanSend,
                SendText.Length);
            _ = await SendPayloadAsync(
                SendText,
                SendAsHex,
                SelectedLineEnding,
                SelectedSendChecksum,
                ChecksumLittleEndian,
                source: "底部发送").ConfigureAwait(true);
        }
        finally
        {
            RefreshSendCommandAvailability();
        }
    }

    private void UpdateSendRepeatState()
    {
        if (!SendRepeatEnabled || !CanSend)
        {
            StopSendRepeat();
            return;
        }

        if (_sendRepeatCancellation is null)
        {
            _ = RunSendRepeatAsync();
        }
    }

    private async Task RunSendRepeatAsync()
    {
        if (_sendRepeatCancellation is not null || !SendRepeatEnabled || !CanSend)
        {
            return;
        }

        CancellationTokenSource source = new();
        CancellationToken cancellationToken = source.Token;
        _sendRepeatCancellation = source;
        IsSendRepeating = true;
        int interval = Math.Clamp(SendRepeatIntervalMs, 1, 60_000);
        StatusText = $"已开始高性能循环发送，间隔 {interval} ms";

        bool failed = false;
        try
        {
            CommunicationSession session = _session ?? throw new InvalidOperationException("请先建立连接。 ");
            bool sentAsHex = SendAsHex;
            byte[] data = BuildSendPayload(
                SendText,
                sentAsHex,
                SelectedLineEnding,
                SelectedSendChecksum,
                ChecksumLittleEndian);
            if (data.Length == 0)
            {
                throw new InvalidOperationException("发送内容为空。 ");
            }

            Log.Information(
                "启动高性能循环发送 | 端点={Endpoint} | 间隔={Interval}ms | 字节={ByteCount}",
                session.Transport.DisplayName,
                interval,
                data.Length);
            await HighResolutionPeriodicTask.RunAsync(
                TimeSpan.FromMilliseconds(interval),
                token => SendPreparedRepeatIterationAsync(session, data, sentAsHex, token),
                cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or IOException or ArgumentException)
        {
            failed = true;
            SendRepeatEnabled = false;
            StatusText = $"循环发送失败：{exception.Message}";
            Log.Error(exception, "高性能循环发送失败");
        }
        finally
        {
            bool wasCanceled = cancellationToken.IsCancellationRequested;
            if (ReferenceEquals(_sendRepeatCancellation, source))
            {
                _sendRepeatCancellation = null;
                IsSendRepeating = false;
            }

            source.Dispose();
            if (wasCanceled && !failed && !SendRepeatEnabled)
            {
                StatusText = "已停止循环发送";
            }
            else if (!failed && !SendRepeatEnabled)
            {
                StatusText = "循环发送已停止";
            }

            if (SendRepeatEnabled && CanSend && !failed)
            {
                UpdateSendRepeatState();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanSendQuickCommand))]
    private async Task SendQuickCommandAsync(QuickCommandItemViewModel? command)
    {
        try
        {
            if (command is not null)
            {
                await SendQuickCommandCoreAsync(command).ConfigureAwait(true);
            }
        }
        finally
        {
            RefreshSendCommandAvailability();
        }
    }

    private async Task SendQuickCommandCoreAsync(QuickCommandItemViewModel command)
    {
        if (!QuickCommands.Contains(command))
        {
            return;
        }

        if (await SendQuickCommandPayloadAsync(command).ConfigureAwait(true))
        {
            command.RegisterUse();
            ScheduleProfileSave();
        }
    }

    private async Task<bool> SendQuickCommandPayloadAsync(QuickCommandItemViewModel command)
    {
        (bool isHex, Encoding encoding, string formatLabel) = ResolveQuickCommandDataFormat(command);
        return await SendPayloadAsync(
            command.Payload,
            isHex,
            command.LineEnding,
            command.Checksum,
            command.ChecksumLittleEndian,
            encoding,
            formatLabel,
            source: $"快捷指令:{command.Name}").ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanSendQuickParameterCommand))]
    private async Task SendQuickParameterCommandAsync(QuickCommandItemViewModel? command)
    {
        try
        {
            if (command is not null)
            {
                await SendQuickParameterCommandCoreAsync(command).ConfigureAwait(true);
            }
        }
        finally
        {
            RefreshSendCommandAvailability();
        }
    }

    private async Task SendQuickParameterCommandCoreAsync(QuickCommandItemViewModel command)
    {
        if (!QuickCommands.Contains(command))
        {
            return;
        }

        (bool isHex, Encoding encoding, string formatLabel) = ResolveQuickCommandDataFormat(command);
        if (await SendPayloadAsync(
                command.ResolvedPayload,
                isHex,
                command.LineEnding,
                command.Checksum,
                command.ChecksumLittleEndian,
                encoding,
                formatLabel,
                source: $"快捷参数:{command.Name}").ConfigureAwait(true))
        {
            command.RegisterUse();
            ScheduleProfileSave();
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleQuickRepeat), AllowConcurrentExecutions = true)]
    private async Task ToggleQuickRepeatAsync(QuickCommandItemViewModel? command)
    {
        if (command is null)
        {
            return;
        }

        if (_repeatCommands.Remove(command.Id, out CancellationTokenSource? existing))
        {
            CancelWithoutThrow(existing);
            command.IsRepeating = false;
            return;
        }

        CancellationTokenSource source = new();
        CancellationToken cancellationToken = source.Token;
        try
        {
            CommunicationSession session = _session ?? throw new InvalidOperationException("请先建立连接。 ");
            (bool isHex, Encoding encoding, _) = ResolveQuickCommandDataFormat(command);
            byte[] data = BuildSendPayload(
                command.Payload,
                isHex,
                command.LineEnding,
                command.Checksum,
                command.ChecksumLittleEndian,
                encoding);
            if (data.Length == 0)
            {
                throw new InvalidOperationException("发送内容为空。 ");
            }

            int interval = Math.Clamp(command.RepeatIntervalMs, 10, 60_000);
            _repeatCommands[command.Id] = source;
            command.IsRepeating = true;
            command.RegisterUse();
            ScheduleProfileSave();
            Log.Information(
                "启动快捷指令高性能循环发送 | 名称={Name} | 端点={Endpoint} | 间隔={Interval}ms | 字节={ByteCount}",
                command.Name,
                session.Transport.DisplayName,
                interval,
                data.Length);
            await HighResolutionPeriodicTask.RunAsync(
                TimeSpan.FromMilliseconds(interval),
                token => SendPreparedRepeatIterationAsync(session, data, isHex, token),
                cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or IOException or ArgumentException)
        {
            StatusText = $"快捷指令循环发送失败：{exception.Message}";
            Log.Error(exception, "快捷指令高性能循环发送失败 | 名称={Name}", command.Name);
        }
        finally
        {
            if (_repeatCommands.TryGetValue(command.Id, out CancellationTokenSource? current)
                && ReferenceEquals(current, source))
            {
                _repeatCommands.Remove(command.Id);
                command.IsRepeating = false;
            }
            source.Dispose();
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleQuickParameterRepeat), AllowConcurrentExecutions = true)]
    private async Task ToggleQuickParameterRepeatAsync(QuickCommandItemViewModel? command)
    {
        if (command is null)
        {
            return;
        }

        if (_parameterRepeatCommands.Remove(command.Id, out CancellationTokenSource? existing))
        {
            CancelWithoutThrow(existing);
            command.IsParameterRepeating = false;
            return;
        }

        CancellationTokenSource source = new();
        CancellationToken cancellationToken = source.Token;
        try
        {
            CommunicationSession session = _session ?? throw new InvalidOperationException("请先建立连接。 ");
            (bool isHex, Encoding encoding, _) = ResolveQuickCommandDataFormat(command);
            byte[] data = BuildSendPayload(
                command.ResolvedPayload,
                isHex,
                command.LineEnding,
                command.Checksum,
                command.ChecksumLittleEndian,
                encoding);
            if (data.Length == 0)
            {
                throw new InvalidOperationException("发送内容为空。 ");
            }

            int interval = Math.Clamp(command.ParameterRepeatIntervalMs, 10, 60_000);
            _parameterRepeatCommands[command.Id] = source;
            command.IsParameterRepeating = true;
            command.RegisterUse();
            ScheduleProfileSave();
            Log.Information(
                "启动快捷参数高性能循环发送 | 名称={Name} | 端点={Endpoint} | 间隔={Interval}ms | 字节={ByteCount}",
                command.Name,
                session.Transport.DisplayName,
                interval,
                data.Length);
            await HighResolutionPeriodicTask.RunAsync(
                TimeSpan.FromMilliseconds(interval),
                token => SendPreparedRepeatIterationAsync(session, data, isHex, token),
                cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or IOException or ArgumentException)
        {
            StatusText = $"快捷参数循环发送失败：{exception.Message}";
            Log.Error(exception, "快捷参数高性能循环发送失败 | 名称={Name}", command.Name);
        }
        finally
        {
            if (_parameterRepeatCommands.TryGetValue(command.Id, out CancellationTokenSource? current)
                && ReferenceEquals(current, source))
            {
                _parameterRepeatCommands.Remove(command.Id);
                command.IsParameterRepeating = false;
            }
            source.Dispose();
        }
    }

    private bool CanAddQuickCommandVariableSet(QuickCommandItemViewModel? command) =>
        command is not null && QuickCommands.Contains(command);

    [RelayCommand(CanExecute = nameof(CanAddQuickCommandVariableSet))]
    private void AddQuickCommandVariableSet(QuickCommandItemViewModel? command)
    {
        command ??= SelectedQuickCommand;
        if (command is null || !QuickCommands.Contains(command))
        {
            return;
        }

        int sequence = command.VariableSets.Count + 1;
        QuickCommandVariableSetItemViewModel variableSet = new(new QuickCommandVariableSet
        {
            Name = $"方案 {sequence}",
            Variables = GetCommandVariableNames(command.Template)
                .Select(name => new QuickCommandVariable { Name = name })
                .ToList()
        });
        if (variableSet.Variables.Count == 0)
        {
            variableSet.Variables.Add(new QuickCommandVariableItemViewModel(
                new QuickCommandVariable { Name = string.Empty }));
        }
        command.VariableSets.Add(variableSet);
        command.SelectedVariableSet = variableSet;
        ScheduleProfileSave();
    }

    [RelayCommand]
    private void DeleteQuickCommandVariableSet(QuickCommandVariableSetItemViewModel? variableSet)
    {
        QuickCommandItemViewModel? command = variableSet is null ? null : FindQuickCommand(variableSet);
        if (command is null || variableSet is null || command.VariableSets.Count <= 1)
        {
            return;
        }

        int index = command.VariableSets.IndexOf(variableSet);
        command.VariableSets.Remove(variableSet);
        if (ReferenceEquals(command.SelectedVariableSet, variableSet))
        {
            command.SelectedVariableSet = command.VariableSets.Count == 0
                ? null
                : command.VariableSets[Math.Clamp(index, 0, command.VariableSets.Count - 1)];
        }
        ScheduleProfileSave();
    }

    [RelayCommand]
    private void DuplicateQuickCommandVariableSet(QuickCommandVariableSetItemViewModel? variableSet)
    {
        QuickCommandItemViewModel? command = variableSet is null ? null : FindQuickCommand(variableSet);
        if (command is null || variableSet is null)
        {
            return;
        }

        QuickCommandVariableSetItemViewModel copy = new(new QuickCommandVariableSet
        {
            Name = $"{variableSet.Name} 副本",
            Variables = variableSet.Variables.Select(variable => new QuickCommandVariable
            {
                Name = variable.Name,
                Value = variable.Value,
                Type = variable.Type
            }).ToList()
        });
        int index = command.VariableSets.IndexOf(variableSet);
        command.VariableSets.Insert(index + 1, copy);
        command.SelectedVariableSet = copy;
        ScheduleProfileSave();
    }

    [RelayCommand]
    private void AddQuickCommandVariable(QuickCommandVariableSetItemViewModel? variableSet)
    {
        if (variableSet is null || FindQuickCommand(variableSet) is null)
        {
            return;
        }

        HashSet<string> existing = variableSet.Variables
            .Select(variable => variable.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int sequence = 1;
        while (existing.Contains($"var{sequence}"))
        {
            sequence++;
        }

        variableSet.Variables.Add(new QuickCommandVariableItemViewModel(new QuickCommandVariable
        {
            Name = $"var{sequence}"
        }));
        ScheduleProfileSave();
    }

    [RelayCommand]
    private void DeleteQuickCommandVariable(QuickCommandVariableItemViewModel? variable)
    {
        if (variable is null)
        {
            return;
        }

        QuickCommandVariableSetItemViewModel? variableSet = QuickCommands
            .SelectMany(command => command.VariableSets)
            .FirstOrDefault(candidate => candidate.Variables.Contains(variable));
        if (variableSet is null)
        {
            return;
        }
        if (variableSet.Variables.Remove(variable))
        {
            ScheduleProfileSave();
        }
    }

    [RelayCommand]
    private void AddQuickCommand()
    {
        QuickCommandSearchText = string.Empty;
        QuickCommandItemViewModel command = new(new QuickCommand
        {
            Name = "新命令",
            Payload = string.Empty,
            LineEnding = "CRLF"
        });
        QuickCommands.Add(command);
        SelectedQuickCommand = command;
        ScheduleProfileSave();
    }

    [RelayCommand]
    private void DeleteQuickCommand(QuickCommandItemViewModel? command)
    {
        if (command is not null)
        {
            StopQuickCommandRepeat(command);
            int index = QuickCommands.IndexOf(command);
            QuickCommands.Remove(command);
            if (ReferenceEquals(SelectedQuickCommand, command))
            {
                SelectedQuickCommand = QuickCommands.Count == 0
                    ? null
                    : QuickCommands[Math.Clamp(index, 0, QuickCommands.Count - 1)];
            }
            ScheduleProfileSave();
        }
    }

    [RelayCommand]
    private void ToggleQuickCommandPin(QuickCommandItemViewModel? command)
    {
        if (command is null || !QuickCommands.Contains(command))
        {
            return;
        }

        if (command.IsPinned)
        {
            command.IsPinned = false;
            command.PinnedOrder = 0;
        }
        else
        {
            int firstPinnedOrder = QuickCommands
                .Where(item => item.IsPinned)
                .Select(item => item.PinnedOrder)
                .DefaultIfEmpty(0)
                .Min();
            command.IsPinned = true;
            command.PinnedOrder = firstPinnedOrder - 1;
        }

        ApplyQuickCommandSort();
        QuickCommandsView.Refresh();
        ScheduleProfileSave();
    }

    public void ReorderPinnedQuickCommands(IReadOnlyList<QuickCommandItemViewModel> orderedCommands)
    {
        int order = 0;
        foreach (QuickCommandItemViewModel command in orderedCommands.Where(item => item.IsPinned))
        {
            command.PinnedOrder = order++;
        }

        ApplyQuickCommandSort();
        QuickCommandsView.Refresh();
        ScheduleProfileSave();
    }

    private bool CanDeleteSelectedQuickCommands() =>
        QuickCommands.Any(command => command.IsSelectedForBulkDelete)
        || SelectedQuickCommand is not null && QuickCommands.Contains(SelectedQuickCommand);

    public void NotifyQuickCommandBulkDeleteSelectionChanged() =>
        DeleteSelectedQuickCommandsCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedQuickCommands))]
    private void DeleteSelectedQuickCommands()
    {
        QuickCommandItemViewModel[] selected = QuickCommands
            .Where(command => command.IsSelectedForBulkDelete)
            .ToArray();
        if (selected.Length == 0 && SelectedQuickCommand is { } selectedCommand && QuickCommands.Contains(selectedCommand))
        {
            selected = [selectedCommand];
        }

        foreach (QuickCommandItemViewModel command in selected)
        {
            StopQuickCommandRepeat(command);
            QuickCommands.Remove(command);
        }

        if (selected.Length > 0)
        {
            if (SelectedQuickCommand is not null && selected.Contains(SelectedQuickCommand))
            {
                SelectedQuickCommand = QuickCommands.FirstOrDefault();
            }
            StatusText = $"已删除 {selected.Length} 条快捷指令";
            ScheduleProfileSave();
        }
    }

    [RelayCommand]
    private void MoveQuickCommandUp(QuickCommandItemViewModel? command)
    {
        if (command is null)
        {
            return;
        }
        int index = QuickCommands.IndexOf(command);
        if (index > 0)
        {
            QuickCommands.Move(index, index - 1);
            ScheduleProfileSave();
        }
    }

    private void StopQuickCommandRepeat(QuickCommandItemViewModel command)
    {
        StopQuickCommandMainRepeat(command);
        StopQuickParameterRepeat(command);
    }

    private void StopQuickCommandMainRepeat(QuickCommandItemViewModel command)
    {
        if (_repeatCommands.Remove(command.Id, out CancellationTokenSource? source))
        {
            CancelWithoutThrow(source);
        }
        command.IsRepeating = false;
    }

    private void StopQuickParameterRepeat(QuickCommandItemViewModel command)
    {
        if (_parameterRepeatCommands.Remove(command.Id, out CancellationTokenSource? source))
        {
            CancelWithoutThrow(source);
        }
        command.IsParameterRepeating = false;
    }

    private static void CancelWithoutThrow(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void StopSendRepeat()
    {
        if (_sendRepeatCancellation is { } source)
        {
            CancelWithoutThrow(source);
        }
    }

    private void RestartSendRepeatIfActive()
    {
        if (SendRepeatEnabled && _sendRepeatCancellation is { } source)
        {
            CancelWithoutThrow(source);
        }
    }

    private static async ValueTask<bool> SendPreparedRepeatIterationAsync(
        CommunicationSession session,
        byte[] data,
        bool sentAsHex,
        CancellationToken cancellationToken)
    {
        await session.SendAsync(data, cancellationToken: cancellationToken, sentAsHex: sentAsHex).ConfigureAwait(false);
        return true;
    }

    private QuickCommandItemViewModel? FindQuickCommand(QuickCommandVariableSetItemViewModel variableSet) =>
        QuickCommands.FirstOrDefault(command => command.VariableSets.Contains(variableSet));

    private static IReadOnlyList<string> GetCommandVariableNames(string payload)
        => ByteText.GetVariableNames(payload);

    private void ResetAnsiTerminal()
    {
        _ansiTerminal.Reset();
        AnsiTerminalReset?.Invoke();
    }

    [RelayCommand]
    private void ClearTerminal()
    {
        _pendingTerminal.Clear();
        _serialTerminalBuffer.Clear();
        _terminalWaveSeparatorTracker.Reset();
        ResetAnsiTerminal();
        TerminalRecords.Clear();
        FrameRecords.Clear();
        StatusText = "已清空显示，捕获文件未删除";
    }

    [RelayCommand]
    private void ApplyFrameTemplate()
    {
        try
        {
            List<FrameTemplate> templates = DeserializeFrameTemplates(FrameTemplateJson);
            ValidateFrameTemplates(templates);
            LoadFrameTemplates(templates);
            StatusText = templates.Count == 1
                ? "帧模板已应用"
                : $"已应用 {templates.Count} 套帧模板";
            ScheduleProfileSave();
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or InvalidDataException)
        {
            StatusText = $"帧模板无效：{exception.Message}";
        }
    }

    [RelayCommand]
    private void ApplyFramingOptions()
    {
        FrameTemplate framing = _frameTemplate with
        {
            Mode = SelectedFramingMode,
            DelimiterHex = DelimiterHex,
            FixedLength = Math.Max(1, FixedFrameLength),
            LengthOffset = Math.Max(0, LengthFieldOffset),
            LengthSize = Math.Clamp(LengthFieldSize, 1, 4),
            LengthAdjustment = LengthAdjustment,
            IdleGapMs = Math.Max(1, IdleGapMs)
        };
        _frameTemplates = _frameTemplates.Count == 0
            ? [framing]
            : _frameTemplates.Select(template => template with
            {
                Mode = framing.Mode,
                DelimiterHex = framing.DelimiterHex,
                FixedLength = framing.FixedLength,
                LengthOffset = framing.LengthOffset,
                LengthSize = framing.LengthSize,
                LengthAdjustment = framing.LengthAdjustment,
                IdleGapMs = framing.IdleGapMs
            }).ToList();
        _frameTemplate = _frameTemplates[0];
        lock (_frameCodecSync)
        {
            _frameCodec = CreateCodec(_frameTemplate);
            _idleGapFlushed = true;
        }
        FrameTemplateJson = SerializeFrameTemplates(_frameTemplates);
        string modeName = FramingModes.FirstOrDefault(option => option.Mode == SelectedFramingMode)?.Name
            ?? SelectedFramingMode.ToString();
        StatusText = $"已切换分帧：{modeName}";
        ScheduleProfileSave();
    }

    [RelayCommand]
    private async Task SendModbusAsync()
    {
        try
        {
            byte function = byte.Parse(SelectedModbusFunction.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            ushort[] values = ParseRegisterValues(ModbusValues);
            byte[] request = SelectedTransportKind is TransportKind.TcpClient or TransportKind.TcpServer
                ? ModbusRequestBuilder.BuildTcp((ushort)ModbusTransactionId, (byte)ModbusUnitId, function, (ushort)ModbusAddress, (ushort)ModbusQuantityOrValue, values)
                : ModbusRequestBuilder.BuildRtu((byte)ModbusUnitId, function, (ushort)ModbusAddress, (ushort)ModbusQuantityOrValue, values);
            if (!CanUseConnectedSession)
            {
                throw new InvalidOperationException("请先建立连接。 ");
            }
            CommunicationSession session = _session ?? throw new InvalidOperationException("请先建立连接。 ");
            await session.SendAsync(request).ConfigureAwait(true);
            ModbusTransactionId = ModbusTransactionId >= ushort.MaxValue ? 1 : ModbusTransactionId + 1;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException)
        {
            StatusText = $"Modbus 请求失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private async Task ReadBleAsync()
    {
        try
        {
            if (_session?.Transport is not BleGattTransport ble || !IsConnected)
            {
                throw new InvalidOperationException("请先建立 BLE GATT 连接。 ");
            }
            await ble.ReadConfiguredCharacteristicAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            StatusText = $"BLE 读取失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveSelectedProfileAsync()
    {
        await SaveActiveProfileSnapshotAsync(showStatus: true).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task AddProfileAsync()
    {
        DeviceProfile profile = new()
        {
            Name = $"设备 {Profiles.Count + 1}",
            WorkspaceMode = SelectedWorkspaceMode.Mode,
            Transport = BuildDefaultTransportForMode(SelectedWorkspaceMode.Mode)
        };
        await _profileStore.SaveAsync(profile).ConfigureAwait(true);
        await ReloadProfilesAsync(profile.Id).ConfigureAwait(true);
    }

    public async Task DeleteSelectedProfileAsync()
    {
        DeviceProfile? selectedProfile = SelectedProfile;
        if (selectedProfile is null)
        {
            return;
        }

        DeviceProfile[] profilesToDelete = GetSelectedProfileDeleteTargets(selectedProfile);
        HashSet<Guid> profileIds = profilesToDelete.Select(profile => profile.Id).ToHashSet();
        _profileSaveTimer.Stop();
        _appSettingsSaveTimer.Stop();
        foreach (Guid profileId in profileIds)
        {
            _deletedProfileIds.TryAdd(profileId, 0);
        }

        Interlocked.Increment(ref _profileSaveRevision);
        Task pendingSave = _lastProfileSaveTask;
        try
        {
            await pendingSave.ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "删除设备配置前等待自动保存失败，继续执行删除");
        }

        try
        {
            foreach (Guid profileId in profileIds)
            {
                await _profileStore.DeleteAsync(profileId).ConfigureAwait(true);
                _importedProfileSourcePaths.TryRemove(profileId, out _);
            }

            if (_activeProfile is not null && profileIds.Contains(_activeProfile.Id))
            {
                _activeProfile = null;
            }

            await ReloadProfilesAsync().ConfigureAwait(true);
            await SaveAppSettingsAsync().ConfigureAwait(true);
            StatusText = profilesToDelete.Length > 1
                ? $"已删除 {profilesToDelete.Length} 个同名设备配置：{selectedProfile.Name}"
                : $"已删除设备配置：{selectedProfile.Name}";
        }
        catch
        {
            foreach (Guid profileId in profileIds)
            {
                _deletedProfileIds.TryRemove(profileId, out _);
            }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Task captureConfigurationTask;
        lock (_captureConfigurationGate)
        {
            _captureConfigurationStopping = true;
            captureConfigurationTask = _captureConfigurationTask;
        }
        try
        {
            await captureConfigurationTask.ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "关闭窗口时等待通信记录配置失败");
        }

        _uiTimer.Stop();
        _profileSaveTimer.Stop();
        _appSettingsSaveTimer.Stop();
        StopSendRepeat();
        CancellationTokenSource[] repeatSources = _repeatCommands.Values
            .Concat(_parameterRepeatCommands.Values)
            .ToArray();
        _repeatCommands.Clear();
        _parameterRepeatCommands.Clear();
        foreach (CancellationTokenSource source in repeatSources)
        {
            CancelWithoutThrow(source);
        }
        await TftpClient.DisposeAsync().ConfigureAwait(true);
        await JLink.DisposeAsync().ConfigureAwait(true);
        await PowerShell.DisposeAsync().ConfigureAwait(true);
        await SaveActiveProfileSnapshotAsync(showStatus: false).ConfigureAwait(true);
        try
        {
            await _lastProfileSaveTask.ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "关闭窗口时保存设备配置失败");
        }
        await _lastAppSettingsSaveTask.ConfigureAwait(true);
        await SaveAppSettingsAsync().ConfigureAwait(true);
        _connectionDesired = false;
        CancelPendingBaudRateHotSwitch();
        _connectionAttemptCancellation?.Cancel();
        await DisconnectInternalAsync().ConfigureAwait(true);
        _appSettingsSaveLock.Dispose();
        _captureConfigurationLock.Dispose();
    }

    partial void OnSelectedProfileChanged(DeviceProfile? value)
    {
        if (_suppressProfileSelection || value is null)
        {
            return;
        }

        if (_activeProfile is not null && _activeProfile.Id != value.Id)
        {
            RequireManualReconnectAfterConfigurationChange();
            _pendingTerminal.Clear();
            _serialTerminalBuffer.Clear();
            _terminalWaveSeparatorTracker.Reset();
            DeviceProfile? snapshot = CreateActiveProfileSnapshot();
            if (snapshot is not null)
            {
                int index = Profiles.ToList().FindIndex(profile => profile.Id == snapshot.Id);
                if (index >= 0)
                {
                    Profiles[index] = snapshot;
                }
                QueueProfileSave(snapshot);
            }
        }

        LoadProfile(value);
        _appSettingsSaveTimer.Stop();
        _appSettingsSaveTimer.Start();
    }

    private Task QueueProfileSave(DeviceProfile snapshot)
    {
        if (_deletedProfileIds.ContainsKey(snapshot.Id))
        {
            return Task.CompletedTask;
        }

        long revision = Interlocked.Increment(ref _profileSaveRevision);
        Task previousSave = _lastProfileSaveTask;
        Task saveTask = Task.Run(() => QueueProfileSaveAsync(previousSave, snapshot, revision));
        _lastProfileSaveTask = saveTask;
        return saveTask;
    }

    private async Task QueueProfileSaveAsync(Task previousSave, DeviceProfile snapshot, long revision)
    {
        try
        {
            await previousSave.ConfigureAwait(false);
        }
        catch
        {
            // 上一次保存失败不应阻塞后续配置写入。
        }

        if (revision != Volatile.Read(ref _profileSaveRevision)
            || _deletedProfileIds.ContainsKey(snapshot.Id))
        {
            return;
        }

        await _profileStore.SaveAsync(snapshot).ConfigureAwait(false);
        if (_importedProfileSourcePaths.TryGetValue(snapshot.Id, out string? sourcePath))
        {
            try
            {
                await _profileFileService.ExportAsync(snapshot, sourcePath).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"默认配置已保存，但导入文件覆盖失败：{sourcePath}。{exception.Message}",
                    exception);
            }
        }
    }

    private async Task ObserveProfileSaveAsync(Task saveTask)
    {
        try
        {
            await saveTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Dispatcher dispatcher = Application.Current.Dispatcher;
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                return;
            }

            try
            {
                await dispatcher.InvokeAsync(() =>
                {
                    if (Volatile.Read(ref _disposed) == 0)
                    {
                        StatusText = $"设备配置自动保存失败：{exception.Message}";
                    }
                }, DispatcherPriority.Background);
            }
            catch (InvalidOperationException)
            {
                // 关闭窗口期间 Dispatcher 可能已经停止，此时无需再更新状态栏。
            }
        }
    }

    partial void OnTerminalFontSizeChanged(double value)
    {
        double normalized = Math.Clamp(Math.Round(value), 9, 28);
        if (!value.Equals(normalized))
        {
            TerminalFontSize = normalized;
            return;
        }

        _appSettingsSaveTimer.Stop();
        _appSettingsSaveTimer.Start();
    }

    partial void OnTerminalSeparatorEnabledChanged(bool value)
    {
        if (_loadingTerminalDisplaySettings)
        {
            return;
        }

        _terminalWaveSeparatorTracker.Reset();
        RefreshTerminalRecordsView();
        _appSettingsSaveTimer.Stop();
        _appSettingsSaveTimer.Start();
    }

    partial void OnTerminalSeparatorIntervalMsChanged(double value)
    {
        double normalized = Math.Clamp(Math.Round(value), 1, 600_000);
        if (!value.Equals(normalized))
        {
            TerminalSeparatorIntervalMs = normalized;
            return;
        }

        OnPropertyChanged(nameof(TerminalSeparatorPreviewText));
        if (_loadingTerminalDisplaySettings)
        {
            return;
        }

        _terminalWaveSeparatorTracker.Reset();
        _appSettingsSaveTimer.Stop();
        _appSettingsSaveTimer.Start();
    }

    partial void OnSelectedTerminalSeparatorStyleChanged(TerminalSeparatorStyleOption value)
    {
        OnPropertyChanged(nameof(TerminalSeparatorFillText));
        OnPropertyChanged(nameof(TerminalSeparatorPreviewText));
        if (_loadingTerminalDisplaySettings)
        {
            return;
        }

        _appSettingsSaveTimer.Stop();
        _appSettingsSaveTimer.Start();
    }

    public void SaveTerminalColumnWidths(
        double timeWidth,
        double directionWidth,
        double endpointWidth,
        double sizeWidth,
        double contentWidth)
    {
        TerminalTimeColumnWidth = NormalizeTerminalColumnWidth(
            timeWidth,
            AppSettings.DefaultTerminalTimeColumnWidth);
        TerminalDirectionColumnWidth = NormalizeTerminalColumnWidth(
            directionWidth,
            AppSettings.DefaultTerminalDirectionColumnWidth);
        TerminalEndpointColumnWidth = NormalizeTerminalColumnWidth(
            endpointWidth,
            AppSettings.DefaultTerminalEndpointColumnWidth);
        TerminalSizeColumnWidth = NormalizeTerminalColumnWidth(
            sizeWidth,
            AppSettings.DefaultTerminalSizeColumnWidth);
        TerminalContentColumnWidth = NormalizeTerminalColumnWidth(
            contentWidth,
            AppSettings.DefaultTerminalContentColumnWidth);
        _appSettingsSaveTimer.Stop();
        _appSettingsSaveTimer.Start();
    }

    public void SaveFrameColumnWidths(
        double timeWidth,
        double lengthWidth,
        double hexWidth,
        double summaryWidth)
    {
        FrameTimeColumnWidth = NormalizeTerminalColumnWidth(
            timeWidth,
            AppSettings.DefaultFrameTimeColumnWidth);
        FrameLengthColumnWidth = NormalizeTerminalColumnWidth(
            lengthWidth,
            AppSettings.DefaultFrameLengthColumnWidth);
        FrameHexColumnWidth = NormalizeTerminalColumnWidth(
            hexWidth,
            AppSettings.DefaultFrameHexColumnWidth);
        FrameSummaryColumnWidth = NormalizeTerminalColumnWidth(
            summaryWidth,
            AppSettings.DefaultFrameSummaryColumnWidth);
        _appSettingsSaveTimer.Stop();
        _appSettingsSaveTimer.Start();
    }

    private static double NormalizeTerminalColumnWidth(double value, double fallback) =>
        double.IsFinite(value) && value > 0 ? Math.Clamp(value, 24, 5000) : fallback;

    private void ApplyTerminalDisplaySettings(AppSettings settings)
    {
        _loadingTerminalDisplaySettings = true;
        try
        {
            TerminalFontSize = Math.Clamp(Math.Round(settings.TerminalFontSize), 9, 28);
            TerminalTimeColumnWidth = NormalizeTerminalColumnWidth(
                settings.TerminalTimeColumnWidth,
                AppSettings.DefaultTerminalTimeColumnWidth);
            TerminalDirectionColumnWidth = NormalizeTerminalColumnWidth(
                settings.TerminalDirectionColumnWidth,
                AppSettings.DefaultTerminalDirectionColumnWidth);
            TerminalEndpointColumnWidth = NormalizeTerminalColumnWidth(
                settings.TerminalEndpointColumnWidth,
                AppSettings.DefaultTerminalEndpointColumnWidth);
            TerminalSizeColumnWidth = NormalizeTerminalColumnWidth(
                settings.TerminalSizeColumnWidth,
                AppSettings.DefaultTerminalSizeColumnWidth);
            TerminalContentColumnWidth = NormalizeTerminalColumnWidth(
                settings.TerminalContentColumnWidth,
                AppSettings.DefaultTerminalContentColumnWidth);
            FrameTimeColumnWidth = NormalizeTerminalColumnWidth(
                settings.FrameTimeColumnWidth,
                AppSettings.DefaultFrameTimeColumnWidth);
            FrameLengthColumnWidth = NormalizeTerminalColumnWidth(
                settings.FrameLengthColumnWidth,
                AppSettings.DefaultFrameLengthColumnWidth);
            FrameHexColumnWidth = NormalizeTerminalColumnWidth(
                settings.FrameHexColumnWidth,
                AppSettings.DefaultFrameHexColumnWidth);
            FrameSummaryColumnWidth = NormalizeTerminalColumnWidth(
                settings.FrameSummaryColumnWidth,
                AppSettings.DefaultFrameSummaryColumnWidth);
            TerminalTextColor = App.NormalizeTerminalColor(settings.TerminalTextColor, App.DefaultTerminalTextColor);
            TerminalBackgroundColor = App.NormalizeTerminalColor(settings.TerminalBackgroundColor, App.DefaultTerminalBackgroundColor);
            TerminalSeparatorEnabled = settings.TerminalSeparatorEnabled;
            TerminalSeparatorIntervalMs = Math.Clamp(settings.TerminalSeparatorIntervalMs, 1, 600_000);
            SelectedTerminalSeparatorStyle = TerminalSeparatorStyles.FirstOrDefault(option =>
                string.Equals(option.Character, settings.TerminalSeparatorStyle, StringComparison.Ordinal))
                ?? TerminalSeparatorStyles[0];
            TerminalSeparatorColor = App.NormalizeTerminalColor(settings.TerminalSeparatorColor, App.DefaultTerminalSeparatorColor);
            LoadTerminalPalette(TerminalTextPalette, settings.TerminalTextPalette, App.DefaultTerminalTextPalette);
            LoadTerminalPalette(TerminalBackgroundPalette, settings.TerminalBackgroundPalette, App.DefaultTerminalBackgroundPalette);
        }
        finally
        {
            _loadingTerminalDisplaySettings = false;
        }

        ApplyTerminalThemeColors(ApplicationThemeManager.GetAppTheme());
    }

    public void ApplyTerminalThemeColors(ApplicationTheme theme) =>
        App.ApplyTerminalColorsForTheme(theme, TerminalTextColor, TerminalBackgroundColor, TerminalSeparatorColor);

    partial void OnTerminalTextColorChanged(string value) => ApplyTerminalColorChange(value, true);

    partial void OnTerminalBackgroundColorChanged(string value) => ApplyTerminalColorChange(value, false);

    partial void OnTerminalSeparatorColorChanged(string value)
    {
        if (_loadingTerminalDisplaySettings || !App.TryNormalizeTerminalColor(value, out string normalized))
        {
            return;
        }

        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            TerminalSeparatorColor = normalized;
            return;
        }

        App.ApplyTerminalSeparatorColor(normalized);
        _appSettingsSaveTimer.Stop();
        _appSettingsSaveTimer.Start();
    }

    private void ApplyTerminalColorChange(string value, bool isTextColor)
    {
        if (_loadingTerminalDisplaySettings)
        {
            return;
        }

        if (!App.TryNormalizeTerminalColor(value, out string normalized))
        {
            return;
        }

        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            if (isTextColor)
            {
                TerminalTextColor = normalized;
            }
            else
            {
                TerminalBackgroundColor = normalized;
            }
            return;
        }

        App.ApplyTerminalColors(TerminalTextColor, TerminalBackgroundColor, TerminalSeparatorColor);
        _appSettingsSaveTimer.Stop();
        _appSettingsSaveTimer.Start();
    }

    public void UpdateTerminalPaletteColor(ColorPaletteItem item, string color, bool isTextColor)
    {
        ObservableCollection<ColorPaletteItem> palette = isTextColor ? TerminalTextPalette : TerminalBackgroundPalette;
        if (!palette.Contains(item) || !App.TryNormalizeTerminalColor(color, out string normalized))
        {
            return;
        }

        item.Color = normalized;
        if (isTextColor)
        {
            TerminalTextColor = normalized;
        }
        else
        {
            TerminalBackgroundColor = normalized;
        }
        _appSettingsSaveTimer.Stop();
        _appSettingsSaveTimer.Start();
    }

    public void ResetTerminalDisplaySettings()
    {
        TerminalFontSize = 12;
        TerminalTextColor = App.DefaultTerminalTextColor;
        TerminalBackgroundColor = App.DefaultTerminalBackgroundColor;
        TerminalSeparatorEnabled = false;
        TerminalSeparatorIntervalMs = AppSettings.DefaultTerminalSeparatorIntervalMs;
        SelectedTerminalSeparatorStyle = TerminalSeparatorStyles[0];
        TerminalSeparatorColor = App.DefaultTerminalSeparatorColor;
        LoadTerminalPalette(TerminalTextPalette, App.DefaultTerminalTextPalette, App.DefaultTerminalTextPalette);
        LoadTerminalPalette(TerminalBackgroundPalette, App.DefaultTerminalBackgroundPalette, App.DefaultTerminalBackgroundPalette);
        _appSettingsSaveTimer.Stop();
        _appSettingsSaveTimer.Start();
    }

    public void ResetOnlineUpdateSettings()
    {
        GitHubRepository = AppSettings.DefaultGitHubRepository;
        AutoUpdateEnabled = true;
        UpdateStatusText = "未检查";
        _appSettingsSaveTimer.Stop();
        _appSettingsSaveTimer.Start();
    }

    public void ResetDiagnosticSettings()
    {
        DebugLoggingEnabled = false;
        _appSettingsSaveTimer.Stop();
        _appSettingsSaveTimer.Start();
    }

    private static void LoadTerminalPalette(
        ObservableCollection<ColorPaletteItem> target,
        IReadOnlyList<string>? savedColors,
        IReadOnlyList<string> defaultColors)
    {
        target.Clear();
        for (int index = 0; index < defaultColors.Count; index++)
        {
            string candidate = savedColors is not null && index < savedColors.Count
                ? savedColors[index]
                : defaultColors[index];
            target.Add(new ColorPaletteItem(App.NormalizeTerminalColor(candidate, defaultColors[index])));
        }
    }

    partial void OnSelectedWorkspaceModeChanged(WorkspaceModeOption value)
    {
        SendRepeatEnabled = false;
        StopSendRepeat();
        _pendingTerminal.Clear();
        _serialTerminalBuffer.Clear();
        _terminalWaveSeparatorTracker.Reset();
        if (_connectedWorkspaceMode != value.Mode
            && (_connectionDesired
                || IsConnected
                || _session is not null
                || _connectionAttemptCancellation is not null))
        {
            RequireManualReconnectAfterConfigurationChange();
        }
        UpdateAvailableTransportOptions();
        SelectedWorkspaceTabIndex = value.Mode switch
        {
            WorkspaceMode.Modbus => 3,
            WorkspaceMode.Bluetooth => 4,
            WorkspaceMode.Tftp => 5,
            WorkspaceMode.JLink => 6,
            WorkspaceMode.PowerShell => 7,
            _ => 0
        };
        if (value.Mode == WorkspaceMode.PowerShell)
        {
            _ = PowerShell.EnsureStartedAsync();
        }
        SelectQuickCommandCategoryForWorkspace(value.Mode);
        OnPropertyChanged(nameof(IsSerialWorkspace));
        OnPropertyChanged(nameof(IsNetworkWorkspace));
        OnPropertyChanged(nameof(IsBluetoothWorkspace));
        OnPropertyChanged(nameof(IsModbusWorkspace));
        OnPropertyChanged(nameof(IsTftpWorkspace));
        OnPropertyChanged(nameof(IsJLinkWorkspace));
        OnPropertyChanged(nameof(IsPowerShellWorkspace));
        OnPropertyChanged(nameof(IsFrameWorkspaceVisible));
        OnPropertyChanged(nameof(IsChartWorkspaceVisible));
        OnPropertyChanged(nameof(TerminalTabHeader));
        OnPropertyChanged(nameof(CanSend));
        RefreshSendCommandAvailability();
        ScheduleProfileSave();
    }

    partial void OnSelectedTransportOptionChanged(TransportOption value)
    {
        if (value is null)
        {
            return;
        }
        _pendingTerminal.Clear();
        _serialTerminalBuffer.Clear();
        _terminalWaveSeparatorTracker.Reset();
        OnPropertyChanged(nameof(ConnectionButtonText));
        OnPropertyChanged(nameof(ConnectionSummary));
        OnPropertyChanged(nameof(TerminalTabHeader));
        ScheduleProfileSave();
        RequireManualReconnectAfterConfigurationChange();
    }

    private void OnTftpPreferencesChanged() => ScheduleProfileSave();

    private void OnJLinkPreferencesChanged() => ScheduleProfileSave();

    partial void OnSelectedFramingModeChanged(FramingMode value) =>
        OnPropertyChanged(nameof(SelectedFramingModeDescription));

    partial void OnProfileNameChanged(string value) => ScheduleProfileSave();

    partial void OnGitHubRepositoryChanged(string value)
    {
        _appSettingsSaveTimer.Stop();
        _appSettingsSaveTimer.Start();
    }

    partial void OnAutoUpdateEnabledChanged(bool value)
    {
        _appSettingsSaveTimer.Stop();
        _appSettingsSaveTimer.Start();
    }

    partial void OnDebugLoggingEnabledChanged(bool value)
    {
        if (App.DiagnosticLoggingEnabled != value)
        {
            App.ConfigureDiagnosticLogging(value);
        }
        if (value)
        {
            Log.Information("调试日志已开启");
        }
        _appSettingsSaveTimer.Stop();
        _appSettingsSaveTimer.Start();
    }

    partial void OnCaptureCommunicationChanged(bool value)
    {
        QueueCaptureConfiguration(value);
    }

    private void QueueCaptureConfiguration(bool enabled)
    {
        Task configurationTask;
        lock (_captureConfigurationGate)
        {
            if (_captureConfigurationStopping || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            configurationTask = ConfigureCaptureAsync(_captureConfigurationTask, enabled);
            _captureConfigurationTask = configurationTask;
        }

        _ = ObserveCaptureConfigurationAsync(configurationTask);
    }

    private async Task ObserveCaptureConfigurationAsync(Task configurationTask)
    {
        try
        {
            await configurationTask.ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "通信记录配置任务未处理异常");
        }
    }

    private async Task ConfigureCaptureAsync(Task previousTask, bool enabled)
    {
        try
        {
            try
            {
                await previousTask.ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "前一通信记录配置任务失败，继续处理最新设置");
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            await _captureConfigurationLock.WaitAsync().ConfigureAwait(true);
            try
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                CommunicationSession? session = _session;
                if (session is null || !IsConnected)
                {
                    StatusText = enabled
                        ? "通信记录已开启，将从下一次连接开始保存"
                        : "通信记录已关闭，后续连接不再自动保存";
                    return;
                }

                if (!enabled)
                {
                    if (session.IsCapturing)
                    {
                        await session.StopCaptureAsync().ConfigureAwait(true);
                    }

                    StatusText = "通信记录已关闭，后续数据不再写入捕获文件";
                    return;
                }

                if (session.IsCapturing)
                {
                    StatusText = "通信记录正在保存";
                    return;
                }

                SqliteCaptureStore captureStore = new();
                bool captureStoreAccepted = false;
                try
                {
                    await session.StartCaptureAsync(captureStore, BuildCaptureHistory()).ConfigureAwait(true);
                    captureStoreAccepted = session.IsCapturing;
                    if (!CaptureCommunication)
                    {
                        await session.StopCaptureAsync().ConfigureAwait(true);
                        return;
                    }

                    StatusText = string.IsNullOrWhiteSpace(captureStore.FilePath)
                        ? "通信记录已开启"
                        : $"通信记录已开启：{captureStore.FilePath}";
                }
                catch (Exception exception)
                {
                    if (!captureStoreAccepted)
                    {
                        try
                        {
                            await captureStore.DisposeAsync().ConfigureAwait(true);
                        }
                        catch (Exception disposeException)
                        {
                            Log.Warning(disposeException, "通信记录开启失败后的存储清理失败");
                        }
                    }
                    StatusText = enabled
                        ? $"通信记录开启失败：{exception.Message}"
                        : $"通信记录关闭失败：{exception.Message}";
                }
            }
            finally
            {
                _captureConfigurationLock.Release();
            }
        }
        catch (Exception exception)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                StatusText = enabled
                    ? $"通信记录开启失败：{exception.Message}"
                    : $"通信记录关闭失败：{exception.Message}";
            }
            Log.Warning(exception, "通信记录配置失败");
        }
    }

    private IReadOnlyList<TransportPacket> BuildCaptureHistory() =>
        TerminalRecords
            .Select(item => item.IsSeparator
                ? new TransportPacket(
                    item.Timestamp,
                    PacketDirection.Information,
                    [],
                    "终端",
                    BuildTerminalSeparatorLine(item.SeparatorGapMilliseconds))
                : new TransportPacket(
                    item.Timestamp,
                    item.Direction,
                    item.Data.ToArray(),
                    item.Endpoint,
                    item.IsMessage ? item.Content : null,
                    item.SentAsHex))
            .ToList();

    partial void OnSendTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanSend));
        RefreshSendCommandAvailability();
        RestartSendRepeatIfActive();
        UpdateSendRepeatState();
    }

    partial void OnSendAsHexChanged(bool value)
    {
        if (_suppressSendModeConversion || string.IsNullOrEmpty(SendText))
        {
            ScheduleProfileSave();
            RestartSendRepeatIfActive();
            return;
        }

        _suppressSendModeConversion = true;
        try
        {
            if (value)
            {
                SendText = NormalizeHexInput(SendText);
                StatusText = "发送框使用 HEX 输入";
            }
            else if (TryConvertHexToReadableText(SendText, out string text))
            {
                SendText = text;
                StatusText = "发送框使用文本输入";
            }
            else
            {
                StatusText = "发送框使用文本输入，内容保持不变";
            }
        }
        finally
        {
            _suppressSendModeConversion = false;
            ScheduleProfileSave();
            RestartSendRepeatIfActive();
        }
    }

    partial void OnQuickCommandSearchTextChanged(string value) => QuickCommandsView.Refresh();

    partial void OnSelectedQuickCommandCategoryChanging(QuickCommandCategoryOption value)
    {
        if (!_switchingQuickCommandCategory)
        {
            _quickCommandCategoryBeforeSelectionChange = selectedQuickCommandCategory.Category;
        }
    }

    partial void OnSelectedQuickCommandCategoryChanged(QuickCommandCategoryOption value)
    {
        if (!_switchingQuickCommandCategory)
        {
            SwitchQuickCommandCategory(_quickCommandCategoryBeforeSelectionChange, value.Category);
        }
    }

    partial void OnSelectedQuickCommandSortChanged(string value)
    {
        ApplyQuickCommandSort();
        QuickCommandsView.Refresh();
    }

    partial void OnSelectedQuickCommandDataFormatChanged(string value)
    {
        if (!QuickCommandDataFormats.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            SelectedQuickCommandDataFormat = QuickCommandDataFormats[0];
            return;
        }

        ScheduleProfileSave();
    }

    partial void OnSelectedQuickCommandChanged(QuickCommandItemViewModel? value)
    {
        AddQuickCommandVariableSetCommand.NotifyCanExecuteChanged();
        DeleteSelectedQuickCommandsCommand.NotifyCanExecuteChanged();
        RefreshSendCommandAvailability();
    }

    partial void OnPortNameChanged(string value) => OnConnectionConfigurationChanged();
    partial void OnBaudRateChanged(int value)
    {
        OnPropertyChanged(nameof(ConnectionSummary));
        ScheduleProfileSave();
        ScheduleBaudRateHotSwitch();
    }
    partial void OnDataBitsChanged(int value) => OnConnectionConfigurationChanged();
    partial void OnSerialParityChanged(SerialParity value) => OnConnectionConfigurationChanged();
    partial void OnSerialStopBitsChanged(SerialStopBits value) => OnConnectionConfigurationChanged();
    partial void OnSerialHandshakeChanged(SerialHandshake value) => OnConnectionConfigurationChanged();
    partial void OnDtrEnableChanged(bool value) => OnConnectionConfigurationChanged();
    partial void OnRtsEnableChanged(bool value) => OnConnectionConfigurationChanged();
    partial void OnReceiveTimeoutMsChanged(int value) => ScheduleProfileSave();
    partial void OnSendRepeatEnabledChanged(bool value) => UpdateSendRepeatState();
    partial void OnSelectedLineEndingChanged(string value)
    {
        ScheduleProfileSave();
        RestartSendRepeatIfActive();
    }
    partial void OnSelectedSendChecksumChanged(ChecksumKind value)
    {
        ScheduleProfileSave();
        RestartSendRepeatIfActive();
    }
    partial void OnChecksumLittleEndianChanged(bool value)
    {
        ScheduleProfileSave();
        RestartSendRepeatIfActive();
    }
    partial void OnSendRepeatIntervalMsChanged(int value)
    {
        int normalized = Math.Clamp(value, 1, 60_000);
        if (value != normalized)
        {
            SendRepeatIntervalMs = normalized;
            return;
        }

        ScheduleProfileSave();
        RestartSendRepeatIfActive();
    }
    partial void OnHostChanged(string value) => OnConnectionConfigurationChanged();
    partial void OnRemotePortChanged(int value) => OnConnectionConfigurationChanged();
    partial void OnLocalAddressChanged(string value) => OnConnectionConfigurationChanged();
    partial void OnLocalPortChanged(int value) => OnConnectionConfigurationChanged();
    partial void OnUdpBroadcastChanged(bool value) => OnConnectionConfigurationChanged();
    partial void OnMulticastAddressChanged(string value) => OnConnectionConfigurationChanged();
    partial void OnBleServiceUuidChanged(string value) => OnConnectionConfigurationChanged();
    partial void OnBleReadUuidChanged(string value) => OnConnectionConfigurationChanged();
    partial void OnBleWriteUuidChanged(string value) => OnConnectionConfigurationChanged();
    partial void OnBleNotifyUuidChanged(string value) => OnConnectionConfigurationChanged();
    partial void OnBleWriteWithoutResponseChanged(bool value) => OnConnectionConfigurationChanged();

    partial void OnSelectedBleDeviceChanged(BleDeviceInfo? value)
    {
        OnConnectionConfigurationChanged();
    }

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectionButtonText));
        OnPropertyChanged(nameof(CanSend));
        RefreshSendCommandAvailability();
        UpdateSendRepeatState();
    }

    private void RefreshSendCommandAvailability()
    {
        Application? application = Application.Current;
        if (application is null)
        {
            return;
        }

        Dispatcher dispatcher = application.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            {
                dispatcher.BeginInvoke(RefreshSendCommandAvailability, DispatcherPriority.Normal);
            }
            return;
        }

        if (Interlocked.Exchange(ref _sendCommandAvailabilityRefreshPending, 1) != 0)
        {
            return;
        }

        _ = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _sendCommandAvailabilityRefreshPending, 0);
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            SendCommand.NotifyCanExecuteChanged();
            SendQuickCommandCommand.NotifyCanExecuteChanged();
            SendQuickParameterCommandCommand.NotifyCanExecuteChanged();
            ToggleQuickRepeatCommand.NotifyCanExecuteChanged();
            ToggleQuickParameterRepeatCommand.NotifyCanExecuteChanged();
        }));
    }

    partial void OnModbusUnitIdChanged(int value)
    {
        _modbusSlave.UnitId = (byte)Math.Clamp(value, 0, byte.MaxValue);
    }

    public bool IsTerminalRecordVisible(TerminalRecordItem item) =>
        (!item.IsSeparator || TerminalSeparatorEnabled)
        && item.MatchesSearch(SearchText, ReceiveAsHex);

    partial void OnSearchTextChanged(string value) => RefreshTerminalRecordsView();

    partial void OnReceiveAsHexChanged(bool value) => RefreshTerminalRecordsView();

    private void RefreshTerminalRecordsView()
    {
        ICollectionView view = CollectionViewSource.GetDefaultView(TerminalRecords);
        view.Filter = item => item is not TerminalRecordItem record || IsTerminalRecordVisible(record);
        view.Refresh();
    }

    private void OnConnectionConfigurationChanged()
    {
        OnPropertyChanged(nameof(ConnectionSummary));
        ScheduleProfileSave();
        RequireManualReconnectAfterConfigurationChange();
    }

    private void ScheduleBaudRateHotSwitch()
    {
        if (Volatile.Read(ref _disposed) != 0
            || SelectedTransportKind != TransportKind.Serial
            || BaudRate <= 0
            || !_connectionDesired
            || (!IsConnected && _session is null && _connectionAttemptCancellation is null))
        {
            return;
        }

        CancellationTokenSource source = new();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _baudRateHotSwitchCancellation, source);
        previous?.Cancel();
        _ = ApplyBaudRateHotSwitchAfterDelayAsync(source);
    }

    private async Task ApplyBaudRateHotSwitchAfterDelayAsync(CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(250, source.Token).ConfigureAwait(true);
            if (!ReferenceEquals(_baudRateHotSwitchCancellation, source))
            {
                return;
            }

            Interlocked.CompareExchange(ref _baudRateHotSwitchCancellation, null, source);
            if (Volatile.Read(ref _disposed) != 0
                || SelectedTransportKind != TransportKind.Serial
                || BaudRate <= 0
                || !_connectionDesired)
            {
                return;
            }

            bool connectionActive = IsConnected
                || _session is not null
                || _connectionAttemptCancellation is not null;
            if (!connectionActive)
            {
                return;
            }

            _manualDisconnect = false;
            SendRepeatEnabled = false;
            StopSendRepeat();
            _connectionAttemptCancellation?.Cancel();
            IsConnected = false;
            StatusText = "波特率已改变，正在热切换…";
            OnPropertyChanged(nameof(ConnectionButtonText));
            EnsureConnectionStateWorker();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            source.Dispose();
        }
    }

    private void CancelPendingBaudRateHotSwitch()
    {
        CancellationTokenSource? source = Interlocked.Exchange(ref _baudRateHotSwitchCancellation, null);
        source?.Cancel();
    }

    private void RequireManualReconnectAfterConfigurationChange()
    {
        bool connectionActive = _connectionDesired
            || IsConnected
            || _session is not null
            || _connectionAttemptCancellation is not null;
        if (!connectionActive || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        CancelPendingBaudRateHotSwitch();
        _connectionDesired = false;
        _manualDisconnect = true;
        SendRepeatEnabled = false;
        StopSendRepeat();
        _connectionAttemptCancellation?.Cancel();
        IsConnected = false;
        OnPropertyChanged(nameof(ConnectionButtonText));
        EnsureConnectionStateWorker();
    }

    private void EnsureConnectionStateWorker()
    {
        if (Interlocked.CompareExchange(ref _connectionWorkerRunning, 1, 0) == 0)
        {
            _ = ProcessConnectionStateAsync();
        }
    }

    private bool NeedsConnectionStateProcessing() =>
        (!_systemSuspended && _connectionDesired != IsConnected)
        || (!_connectionDesired && _session is not null);

    /// <summary>
    /// 处理 Windows 电源模式切换，睡眠时暂停连接健康判定，唤醒后恢复用户期望的连接。
    /// </summary>
    /// <param name="suspending">是否即将进入睡眠；传入 false 表示系统已唤醒。</param>
    public void HandleSystemPowerModeChanged(bool suspending)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _systemSuspended = suspending;
        if (_session?.Transport is SerialPortTransport serialTransport)
        {
            serialTransport.SetSystemSuspended(suspending);
        }

        if (suspending)
        {
            _connectionAttemptCancellation?.Cancel();
            if (_connectionDesired || IsConnected || _session is not null)
            {
                StatusText = "系统睡眠，连接将在唤醒后恢复…";
            }
            return;
        }

        if (!_connectionDesired || _manualDisconnect)
        {
            return;
        }

        // USB 串口设备可能在唤醒时被 Windows 重新枚举，主动重建会话比复用旧句柄可靠。
        IsConnected = false;
        StatusText = "系统已唤醒，正在恢复连接…";
        OnPropertyChanged(nameof(ConnectionButtonText));
        EnsureConnectionStateWorker();
    }

    private async Task ProcessConnectionStateAsync()
    {
        try
        {
            while (NeedsConnectionStateProcessing())
            {
                if (_connectionDesired && !IsConnected && _session is not null)
                {
                    await DisconnectInternalAsync().ConfigureAwait(true);
                    continue;
                }
                if (_connectionDesired)
                {
                    using CancellationTokenSource source = new();
                    _connectionAttemptCancellation = source;
                    await ConnectInternalAsync(source.Token).ConfigureAwait(true);
                    if (ReferenceEquals(_connectionAttemptCancellation, source))
                    {
                        _connectionAttemptCancellation = null;
                    }
                }
                else
                {
                    await DisconnectInternalAsync().ConfigureAwait(true);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _connectionWorkerRunning, 0);
            if (NeedsConnectionStateProcessing())
            {
                EnsureConnectionStateWorker();
            }
        }
    }

    private async Task ConnectInternalAsync(CancellationToken cancellationToken = default)
    {
        TransportKind connectingKind = SelectedTransportKind;
        IsBusy = true;
        _manualDisconnect = false;
        StatusText = connectingKind == TransportKind.TcpServer ? "正在启动监听…" : "正在连接…";
        CommunicationSession? connectingSession = null;
        try
        {
            WorkspaceMode connectingWorkspace = SelectedWorkspaceMode.Mode;
            TransportSettings settings = BuildTransportSettings();
            ITransport transport = TransportFactory.Create(settings);
            ICaptureStore capture = CaptureCommunication
                ? new SqliteCaptureStore()
                : new NullCaptureStore();
            CommunicationSession newSession = new(SelectedProfile?.Name ?? "快速调试", transport, capture);
            newSession.ReceivePacketHandler = (packet, token) =>
                HandleAnsiTerminalPacketAsync(newSession, packet, token);
            connectingSession = newSession;
            connectingSession.Faulted += OnSessionFaulted;
            ResetAnsiTerminal();
            await newSession.ConnectAsync(
                    cancellationToken,
                    CaptureCommunication ? BuildCaptureHistory() : null)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_connectionDesired || SelectedWorkspaceMode.Mode != connectingWorkspace)
            {
                connectingSession.Faulted -= OnSessionFaulted;
                await connectingSession.DisposeAsync().ConfigureAwait(true);
                connectingSession = null;
                StatusText = _connectionDesired
                    ? connectingKind == TransportKind.TcpServer ? "监听配置已变化，请重新监听" : "连接模式已变化，请重新连接"
                    : GetDisconnectedStatusText(connectingKind);
                return;
            }

            _session = connectingSession;
            connectingSession = null;
            _consumeCancellation = new CancellationTokenSource();
            CommunicationSession activeSession = _session;
            _consumeTask = Task.Run(() => ConsumeSessionAsync(activeSession, _consumeCancellation.Token), CancellationToken.None);
            _connectedWorkspaceMode = connectingWorkspace;
            Interlocked.Exchange(ref _terminalPacketDropCount, 0);
            Interlocked.Exchange(ref _chartValueDropCount, 0);
            Interlocked.Exchange(ref _frameDropCount, 0);
            _lastDisplayDropCount = 0;
            _lastTerminalDropLogCount = 0;
            _lastChartDropLogCount = 0;
            _lastFrameDropLogCount = 0;
            _lastQueueSampleTimestamp = DateTimeOffset.MinValue;
            IsConnected = true;
            StatusText = settings switch
            {
                TcpServerTransportSettings tcpServer => $"已监听：{tcpServer.LocalAddress}:{tcpServer.Port}",
                TcpClientTransportSettings tcpClient => $"已连接：{tcpClient.Host}:{tcpClient.Port}",
                _ => $"已连接：{transport.DisplayName}"
            };
            if (CaptureCommunication)
            {
                StatusText += " · 正在记录通信";
            }
            QueueCaptureConfiguration(CaptureCommunication);
        }
        catch (OperationCanceledException)
        {
            StatusText = _connectionDesired
                ? connectingKind == TransportKind.TcpServer ? "正在重新监听…" : "正在重新连接…"
                : GetDisconnectedStatusText(connectingKind);
        }
        catch (Exception exception)
        {
            StatusText = connectingKind == TransportKind.TcpServer
                ? $"监听失败：{exception.Message}"
                : $"连接失败：{exception.Message}";
            _connectionDesired = false;
            OnPropertyChanged(nameof(ConnectionButtonText));
        }
        finally
        {
            if (connectingSession is not null)
            {
                connectingSession.Faulted -= OnSessionFaulted;
                await connectingSession.DisposeAsync().ConfigureAwait(true);
            }
            IsBusy = false;
        }
    }

    private async Task DisconnectInternalAsync()
    {
        TransportKind disconnectingKind = _session?.Transport.Kind ?? SelectedTransportKind;
        await DisconnectSessionAsync(GetDisconnectedStatusText(disconnectingKind)).ConfigureAwait(true);
    }

    private static string GetDisconnectedStatusText(TransportKind kind) => kind == TransportKind.TcpServer
        ? "已停止监听"
        : "已断开连接";

    private async Task DisconnectSessionAsync(string statusText)
    {
        CancellationTokenSource? consume = Interlocked.Exchange(ref _consumeCancellation, null);
        consume?.Cancel();
        CommunicationSession? session = Interlocked.Exchange(ref _session, null);
        Task? consumeTask = Interlocked.Exchange(ref _consumeTask, null);
        if (session is not null)
        {
            session.Faulted -= OnSessionFaulted;
            await session.DisposeAsync().ConfigureAwait(true);
        }
        if (consumeTask is not null)
        {
            try
            {
                await consumeTask.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
        }
        consume?.Dispose();
        _pendingTerminal.Clear();
        _pendingFrames.Clear();
        _pendingChartValues.Clear();
        _serialTerminalBuffer.Clear();
        _terminalWaveSeparatorTracker.Reset();
        _connectedWorkspaceMode = null;
        IsConnected = false;
        StatusText = statusText;
    }

    private async Task ConsumeSessionAsync(CommunicationSession session, CancellationToken cancellationToken)
    {
        await foreach (TransportPacket packet in session.ReadDisplayAsync(cancellationToken).ConfigureAwait(false))
        {
            LogReceivedTransportPacket(packet);
            if (packet.Direction == PacketDirection.Receive)
            {
                Interlocked.Add(ref _rxTotal, packet.Data.Length);
            }
            else if (packet.Direction == PacketDirection.Send)
            {
                Interlocked.Add(ref _txTotal, packet.Data.Length);
            }

            if (!IsPaused)
            {
                EnqueueTerminalPacket(packet);
            }

            if (packet.Direction == PacketDirection.Receive && packet.Data.Length > 0)
            {
                ProcessFrames(packet);
                if (ModbusSlaveEnabled && SelectedTransportKind is TransportKind.Serial or TransportKind.TcpServer)
                {
                    await RespondAsModbusSlaveAsync(packet).ConfigureAwait(false);
                }
            }
        }
    }

    private async ValueTask HandleAnsiTerminalPacketAsync(
        CommunicationSession session,
        TransportPacket packet,
        CancellationToken cancellationToken)
    {
        if (packet.Data.Length == 0)
        {
            return;
        }

        AnsiTerminalFeedResult ansiResult = _ansiTerminal.Feed(packet.Data);
        await SendAnsiTerminalResponsesAsync(
                session,
                packet.Endpoint,
                packet.ArrivalTimestamp,
                ansiResult.Responses,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SendAnsiTerminalResponsesAsync(
        CommunicationSession session,
        string endpoint,
        long receiveArrivalTimestamp,
        IReadOnlyList<byte[]> responses,
        CancellationToken cancellationToken)
    {
        foreach (byte[] response in responses)
        {
            try
            {
                string? target = session.Transport.Kind == TransportKind.TcpServer
                    ? endpoint
                    : null;
                await session.SendControlAsync(
                        response,
                        target,
                        cancellationToken,
                        sentAsHex: false)
                    .ConfigureAwait(false);
                Interlocked.Add(ref _txTotal, response.Length);
                LogTransportDiagnostic(
                    PacketDirection.Send,
                    "ANSI/VT终端响应",
                    endpoint,
                    response,
                    receiveArrivalTimestamp > 0
                        ? $"探测响应耗时={Stopwatch.GetElapsedTime(receiveArrivalTimestamp).TotalMilliseconds:F1} ms"
                        : null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException)
            {
                Log.Warning(exception, "ANSI/VT 终端状态响应失败 | 端点={Endpoint}", endpoint);
            }
        }
    }

    private async Task RespondAsModbusSlaveAsync(TransportPacket packet)
    {
        CommunicationSession? session = _session;
        if (session is null)
        {
            return;
        }

        byte[]? response = SelectedTransportKind == TransportKind.TcpServer
            ? _modbusSlave.ProcessTcp(packet.Data)
            : _modbusSlave.ProcessRtu(packet.Data);
        if (response is null)
        {
            return;
        }

        await session.SendAsync(response, SelectedTransportKind == TransportKind.TcpServer ? packet.Endpoint : null).ConfigureAwait(false);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            foreach (ModbusRegisterItem item in ModbusRegisters)
            {
                item.Value = _modbusSlave.GetHoldingRegister(item.Address);
            }
            StatusText = $"已响应 Modbus 请求：{packet.Endpoint}";
        });
    }

    private void ProcessFrames(TransportPacket packet)
    {
        try
        {
            IReadOnlyList<byte[]> frames;
            lock (_frameCodecSync)
            {
                frames = _frameCodec.Push(packet.Data, packet.Timestamp);
                _lastFrameInput = packet.Timestamp;
                _idleGapFlushed = false;
            }
            EnqueueDecodedFrames(frames, packet.Timestamp);

            if (ChartAutoExtract)
            {
                Match match = Regex.Match(GetSelectedEncoding().GetString(packet.Data), ChartValuePattern);
                if (match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                {
                    EnqueueChartValue(value);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or ArgumentException)
        {
            EnqueueFrame(new FrameRecordItem(packet.Timestamp, packet.Data.Length, ByteText.ToHex(packet.Data), exception.Message, false));
        }
    }

    private void EnqueueDecodedFrames(IReadOnlyList<byte[]> frames, DateTimeOffset timestamp)
    {
        foreach (byte[] frame in frames)
        {
            FrameTemplate? template = SelectFrameTemplate(frame);
            DecodedFrame decoded = template is null
                ? new DecodedFrame(frame, [], false, "未匹配帧模板")
                : GenericFrameDecoder.Decode(frame, template);
            EnqueueFrame(FrameRecordItem.Create(timestamp, decoded, template?.Name));
            foreach (DecodedField field in decoded.Fields)
            {
                if (field.Value is double numeric)
                {
                    EnqueueChartValue(numeric);
                }
            }
        }
    }

    private void EnqueueChartValue(double value)
    {
        if (_pendingChartValues.Count >= MaximumPendingChartValues)
        {
            Interlocked.Increment(ref _chartValueDropCount);
            return;
        }

        _pendingChartValues.Enqueue(value);
    }

    private void EnqueueTerminalPacket(TransportPacket packet)
    {
        _pendingTerminal.Enqueue(packet);
        while (_pendingTerminal.Count > MaximumPendingTerminalPackets
            && _pendingTerminal.TryDequeue(out _))
        {
            Interlocked.Increment(ref _terminalPacketDropCount);
        }
    }

    private void EnqueueFrame(FrameRecordItem frame)
    {
        if (_pendingFrames.Count >= MaximumPendingFrames)
        {
            Interlocked.Increment(ref _frameDropCount);
            return;
        }

        _pendingFrames.Enqueue(frame);
    }

    private FrameTemplate? SelectFrameTemplate(ReadOnlySpan<byte> frame)
    {
        foreach (FrameTemplate template in _frameTemplates.Where(template => !string.IsNullOrWhiteSpace(template.MatchHex)))
        {
            if (GenericFrameDecoder.Matches(frame, template))
            {
                return template;
            }
        }

        return _frameTemplates.FirstOrDefault(template => string.IsNullOrWhiteSpace(template.MatchHex));
    }

    private async Task<bool> SendPayloadAsync(
        string payload,
        bool isHex,
        string lineEnding,
        ChecksumKind checksum,
        bool littleEndian,
        Encoding? textEncoding = null,
        string? formatLabel = null,
        IReadOnlyDictionary<string, string>? variables = null,
        bool updateStatus = true,
        string source = "发送")
    {
        byte[]? final = null;
        try
        {
            if (!CanUseConnectedSession)
            {
                throw new InvalidOperationException("请先建立连接。 ");
            }

            final = BuildSendPayload(
                payload,
                isHex,
                lineEnding,
                checksum,
                littleEndian,
                textEncoding,
                variables);
            if (final.Length == 0)
            {
                StatusText = "发送内容为空";
                Log.Warning("通信发送内容为空 | 来源={Source}", source);
                return false;
            }
            CommunicationSession session = _session ?? throw new InvalidOperationException("请先建立连接。 ");
            await session.SendAsync(final, sentAsHex: isHex).ConfigureAwait(true);
            LogTransportDiagnostic(
                PacketDirection.Send,
                source,
                session.Transport.DisplayName,
                final,
                $"格式={formatLabel ?? (isHex ? "HEX" : textEncoding?.WebName ?? GetSelectedEncoding().WebName)}，行尾={lineEnding}，校验={checksum}");
            if (updateStatus)
            {
                StatusText = $"已发送 {final.Length} 字节（{formatLabel ?? (isHex ? "HEX 输入" : "文本输入")}）";
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusText = "发送已取消，连接状态已变化";
            Log.Warning("通信发送已取消 | 来源={Source}", source);
            return false;
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or IOException or ArgumentException)
        {
            StatusText = $"发送失败：{exception.Message}";
            Log.Error(
                exception,
                "通信发送失败 | 来源={Source} | HEX={Hex}",
                source,
                FormatTransportDiagnosticData(final ?? []));
            return false;
        }
    }

    private byte[] BuildSendPayload(
        string payload,
        bool isHex,
        string lineEnding,
        ChecksumKind checksum,
        bool littleEndian,
        Encoding? textEncoding = null,
        IReadOnlyDictionary<string, string>? variables = null)
    {
        string expanded = ByteText.ExpandVariables(payload, variables ?? EmptyVariables);
        byte[] data = ByteText.ParseInput(expanded, isHex, textEncoding ?? GetSelectedEncoding());
        byte[] ending = GetLineEnding(lineEnding);
        byte[] withEnding = new byte[data.Length + ending.Length];
        data.CopyTo(withEnding, 0);
        ending.CopyTo(withEnding, data.Length);
        return ChecksumCalculator.Append(withEnding, checksum, littleEndian);
    }

    private async void OnSessionFaulted(object? sender, Exception exception)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            await Application.Current.Dispatcher.InvokeAsync(() => OnSessionFaulted(sender, exception));
            return;
        }

        if (sender is not CommunicationSession faultedSession
            || !ReferenceEquals(faultedSession, _session))
        {
            return;
        }

        if (_systemSuspended)
        {
            StatusText = "系统睡眠期间连接暂不可用，唤醒后正在恢复…";
            return;
        }

        TransportKind faultedKind = faultedSession.Transport.Kind;
        StatusText = faultedKind == TransportKind.TcpServer
            ? $"监听异常：{exception.Message}"
            : $"连接异常：{exception.Message}";
        IsConnected = false;
        _connectionDesired = false;
        OnPropertyChanged(nameof(ConnectionButtonText));

        await Task.Yield();
        await DisconnectInternalAsync().ConfigureAwait(true);
    }

    private void OnUiTimerTick(object? sender, EventArgs args)
    {
        List<TransportPacket> pendingPackets = new(500);
        while (pendingPackets.Count < 500 && _pendingTerminal.TryDequeue(out TransportPacket? packet))
        {
            pendingPackets.Add(packet);
        }

        double receiveGapMilliseconds = Math.Clamp(ReceiveTimeoutMs, 1, 5000);
        if (TerminalSeparatorEnabled)
        {
            receiveGapMilliseconds = Math.Min(receiveGapMilliseconds, TerminalSeparatorIntervalMs);
        }
        TimeSpan receiveGap = TimeSpan.FromMilliseconds(receiveGapMilliseconds);
        IReadOnlyList<TransportPacket> displayPackets;
        if (SelectedTransportKind == TransportKind.Serial)
        {
            _serialTerminalBuffer.AddRange(pendingPackets);
            int readyCount = TransportPacketCoalescer.GetReadyPrefixCount(
                _serialTerminalBuffer,
                DateTimeOffset.Now,
                Stopwatch.GetTimestamp(),
                receiveGap);
            List<TransportPacket> readyPackets = _serialTerminalBuffer.GetRange(0, readyCount);
            _serialTerminalBuffer.RemoveRange(0, readyCount);
            displayPackets = TransportPacketCoalescer.CoalesceAdjacentReceives(readyPackets, receiveGap);
        }
        else
        {
            if (_serialTerminalBuffer.Count == 0)
            {
                displayPackets = pendingPackets;
            }
            else
            {
                _serialTerminalBuffer.AddRange(pendingPackets);
                displayPackets = TransportPacketCoalescer.CoalesceAdjacentReceives(
                    _serialTerminalBuffer,
                    receiveGap);
                _serialTerminalBuffer.Clear();
            }
        }
        List<TerminalRecordItem> displayRecords = new(displayPackets.Count * 2);
        foreach (TransportPacket packet in displayPackets)
        {
            AppendFormattedPacket(displayRecords, packet);
        }
        if (displayRecords.Count > 0)
        {
            TerminalRecords.AddRange(displayRecords);
        }
        int added = displayRecords.Count;
        int limit = SelectedProfile?.Terminal.UiRecordLimit ?? 100_000;
        if (TerminalRecords.Count > limit + TerminalEvictionBatchSize)
        {
            TerminalRecords.RemoveFirst(TerminalRecords.Count - limit);
        }

        int frameAdded = 0;
        List<FrameRecordItem> frameBatch = [];
        while (frameAdded < 200 && _pendingFrames.TryDequeue(out FrameRecordItem? frame))
        {
            frameBatch.Add(frame);
            frameAdded++;
        }
        if (frameBatch.Count > 0)
        {
            FrameRecords.AddRange(frameBatch);
        }
        if (FrameRecords.Count > FrameRecordLimit + FrameEvictionBatchSize)
        {
            FrameRecords.RemoveFirst(FrameRecords.Count - FrameRecordLimit);
        }

        int chartAdded = 0;
        while (chartAdded < ChartValuesPerUiTick && _pendingChartValues.TryDequeue(out double value))
        {
            ChartValueAdded?.Invoke(value);
            chartAdded++;
        }

        ReceivedBytes = Interlocked.Read(ref _rxTotal);
        SentBytes = Interlocked.Read(ref _txTotal);
        long displayDropCount = _session?.DisplayDropCount ?? 0;
        if (displayDropCount > _lastDisplayDropCount)
        {
            Log.Warning(
                "通信显示队列发生丢包 | 本次新增={Dropped} | 累计={Total}",
                displayDropCount - _lastDisplayDropCount,
                displayDropCount);
            _lastDisplayDropCount = displayDropCount;
        }
        long terminalDropCount = Interlocked.Read(ref _terminalPacketDropCount);
        if (terminalDropCount > _lastTerminalDropLogCount)
        {
            Log.Warning(
                "终端待显示队列达到上限丢包 | 本次新增={Dropped} | 累计={Total}",
                terminalDropCount - _lastTerminalDropLogCount,
                terminalDropCount);
            _lastTerminalDropLogCount = terminalDropCount;
        }
        long chartDropCount = Interlocked.Read(ref _chartValueDropCount);
        if (chartDropCount > _lastChartDropLogCount)
        {
            Log.Warning(
                "曲线值队列达到上限丢弃 | 本次新增={Dropped} | 累计={Total}",
                chartDropCount - _lastChartDropLogCount,
                chartDropCount);
            _lastChartDropLogCount = chartDropCount;
        }
        long frameDropCount = Interlocked.Read(ref _frameDropCount);
        if (frameDropCount > _lastFrameDropLogCount)
        {
            Log.Warning(
                "帧记录队列达到上限丢弃 | 本次新增={Dropped} | 累计={Total}",
                frameDropCount - _lastFrameDropLogCount,
                frameDropCount);
            _lastFrameDropLogCount = frameDropCount;
        }
        DateTimeOffset now = DateTimeOffset.Now;
        if (now - _lastQueueSampleTimestamp >= TimeSpan.FromSeconds(1))
        {
            _lastQueueSampleTimestamp = now;
            if (File.Exists(AppPaths.DebugLoggingMarkerPath))
            {
                long workerBytes = 0;
                long workerRetries = 0;
                long workerTimeouts = 0;
                long captureDropCount = _session?.CaptureDropCount ?? 0;
                if (_session?.Transport is SerialPortTransport serialTransport)
                {
                    workerBytes = serialTransport.WorkerReceivedBytes;
                    workerRetries = serialTransport.WorkerRetryCount;
                    workerTimeouts = serialTransport.WorkerTimeoutStreak;
                }

                Log.Information(
                    "通信采样 | 底层接收={WorkerBytes} | 传输接收={ReceivedBytes} | 发送={SentBytes} | "
                        + "终端待显示={PendingTerminal} | 帧待解析={PendingFrames} | 曲线待入队={PendingChartValues} | "
                        + "显示丢弃累计={DisplayDropCount} | 终端丢弃累计={TerminalDropCount} | 曲线丢弃累计={ChartDropCount} | "
                        + "帧丢弃累计={FrameDropCount} | 捕获丢弃累计={CaptureDropCount} | "
                        + "工作进程累计重试={WorkerRetries} | 工作进程连续超时={WorkerTimeouts}",
                    workerBytes,
                    ReceivedBytes,
                    SentBytes,
                    _pendingTerminal.Count,
                    _pendingFrames.Count,
                    _pendingChartValues.Count,
                    displayDropCount,
                    terminalDropCount,
                    chartDropCount,
                    frameDropCount,
                    captureDropCount,
                    workerRetries,
                    workerTimeouts);
            }
        }
        if (SelectedFramingMode == FramingMode.IdleGap && !_idleGapFlushed && now - _lastFrameInput >= TimeSpan.FromMilliseconds(Math.Max(1, IdleGapMs)))
        {
            IReadOnlyList<byte[]> frames;
            lock (_frameCodecSync)
            {
                frames = _frameCodec.Flush();
                _idleGapFlushed = true;
            }
            EnqueueDecodedFrames(frames, now);
        }
        if (now >= _nextPortRefresh && Interlocked.Exchange(ref _portRefreshRunning, 1) == 0)
        {
            _nextPortRefresh = now.AddSeconds(2);
            _ = RefreshPortsInBackgroundAsync();
        }
        if (now - _lastRateTimestamp >= TimeSpan.FromSeconds(1))
        {
            long total = ReceivedBytes + SentBytes;
            double rate = (total - _lastRateBytes) / (now - _lastRateTimestamp).TotalSeconds;
            RateText = FormatRate(rate);
            _lastRateBytes = total;
            _lastRateTimestamp = now;
        }

        if (added > 0)
        {
            RecordsAppended?.Invoke(added);
        }
    }

    private async Task RefreshPortsInBackgroundAsync()
    {
        try
        {
            IReadOnlyList<SerialPortInfo> ports = await Task.Run(SerialPortDiscovery.GetPorts).ConfigureAwait(true);
            // 枚举偶发会返回空（WMI/注册表抖动），此时不能拿空列表覆盖已有端口，
            // 否则下拉项被清光、串口名称直接消失。真要清空请用“刷新串口”手动触发。
            if (ports.Count == 0 && SerialPorts.Count > 0)
            {
                return;
            }

            if (!ports.Select(item => item.PortName).SequenceEqual(SerialPorts.Select(item => item.PortName), StringComparer.OrdinalIgnoreCase))
            {
                ApplyPortList(ports);
            }
        }
        catch
        {
        }
        finally
        {
            Interlocked.Exchange(ref _portRefreshRunning, 0);
        }
    }

    private void StartPortRefreshInBackground()
    {
        if (Interlocked.Exchange(ref _portRefreshRunning, 1) == 0)
        {
            _ = RefreshPortsInBackgroundAsync();
        }
    }

    private void ApplyPortList(IReadOnlyList<SerialPortInfo> ports)
    {
        string previous = PortName ?? string.Empty;
        string selected = ports.Any(port => string.Equals(port.PortName, previous, StringComparison.OrdinalIgnoreCase))
            ? previous
            : !IsConnected && ports.Count > 0
                ? ports[0].PortName
                : previous;
        SerialPorts.Clear();
        foreach (SerialPortInfo port in ports)
        {
            SerialPorts.Add(port);
        }
        PortName = selected;
        // Clear/Add 会把 ComboBox 的 SelectedValue 清掉并回写 PortName；若 selected 与回写后的值相同，
        // SetProperty 视为无变化不再发通知，绑定就不会把选中项同步回下拉框，串口框会一直空着（标题栏却还显示端口名）。
        // 这里显式补一次通知，只影响绑定同步，不会再触发 OnPortNameChanged 的副作用。
        OnPropertyChanged(nameof(PortName));
        OnPropertyChanged(nameof(ConnectionSummary));
    }

    private void UpdateAvailableTransportOptions()
    {
        TransportKind[] kinds = SelectedWorkspaceMode.Mode switch
        {
            WorkspaceMode.Serial => [TransportKind.Serial],
            WorkspaceMode.Network => [TransportKind.TcpClient, TransportKind.TcpServer, TransportKind.Udp],
            WorkspaceMode.Bluetooth => [TransportKind.BleGatt],
            WorkspaceMode.Modbus => [TransportKind.Serial],
            WorkspaceMode.Tftp => [],
            WorkspaceMode.JLink => [],
            WorkspaceMode.PowerShell => [],
            _ => [TransportKind.Serial]
        };
        if (kinds.Length == 0)
        {
            AvailableTransportOptions.Clear();
            OnPropertyChanged(nameof(ConnectionSummary));
            return;
        }
        TransportKind previousKind = SelectedTransportOption?.Kind ?? kinds[0];
        AvailableTransportOptions.Clear();
        foreach (TransportKind kind in kinds)
        {
            AvailableTransportOptions.Add(TransportOptions.First(option => option.Kind == kind));
        }
        SelectedTransportOption = AvailableTransportOptions.FirstOrDefault(option => option.Kind == previousKind)
            ?? AvailableTransportOptions[0];
    }

    private static TransportSettings BuildDefaultTransportForMode(WorkspaceMode mode) => mode switch
    {
        WorkspaceMode.Network => new TcpClientTransportSettings(),
        WorkspaceMode.Bluetooth => new BleGattTransportSettings(),
        _ => new SerialTransportSettings()
    };

    private void OnQuickCommandsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_replacingQuickCommands)
        {
            return;
        }

        if (args.OldItems is not null)
        {
            foreach (QuickCommandItemViewModel item in args.OldItems)
            {
                item.PropertyChanged -= OnQuickCommandPropertyChanged;
            }
        }
        if (args.NewItems is not null)
        {
            foreach (QuickCommandItemViewModel item in args.NewItems)
            {
                item.PropertyChanged += OnQuickCommandPropertyChanged;
            }
        }
        QuickCommandsView.Refresh();
        DeleteSelectedQuickCommandsCommand.NotifyCanExecuteChanged();
        StoreCurrentQuickCommands();
        ScheduleProfileSave();
    }

    private void OnQuickCommandPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(QuickCommandItemViewModel.IsExpanded))
        {
            if (sender is QuickCommandItemViewModel { IsExpanded: true } expanded)
            {
                SelectedQuickCommand = expanded;
                foreach (QuickCommandItemViewModel command in QuickCommands)
                {
                    if (!ReferenceEquals(command, expanded) && command.IsExpanded)
                    {
                        command.IsExpanded = false;
                    }
                }
            }
            return;
        }
        if (args.PropertyName is nameof(QuickCommandItemViewModel.IsPinned)
            or nameof(QuickCommandItemViewModel.PinnedOrder))
        {
            ApplyQuickCommandSort();
            QuickCommandsView.Refresh();
            ScheduleProfileSave();
            return;
        }
        if (args.PropertyName == nameof(QuickCommandItemViewModel.IsSelectedForBulkDelete))
        {
            DeleteSelectedQuickCommandsCommand.NotifyCanExecuteChanged();
        }
        if (args.PropertyName is nameof(QuickCommandItemViewModel.Payload)
            or nameof(QuickCommandItemViewModel.Template)
            or nameof(QuickCommandItemViewModel.ResolvedPayload)
            or nameof(QuickCommandItemViewModel.VariableSets))
        {
            RefreshSendCommandAvailability();
        }
        if (sender is QuickCommandItemViewModel changedCommand)
        {
            bool sharedPayloadSettingsChanged = args.PropertyName is nameof(QuickCommandItemViewModel.LineEnding)
                or nameof(QuickCommandItemViewModel.Checksum)
                or nameof(QuickCommandItemViewModel.ChecksumLittleEndian);
            bool directPayloadChanged = args.PropertyName == nameof(QuickCommandItemViewModel.Payload)
                || sharedPayloadSettingsChanged;
            bool parameterPayloadChanged = (args.PropertyName is nameof(QuickCommandItemViewModel.Template)
                or nameof(QuickCommandItemViewModel.ResolvedPayload)
                or nameof(QuickCommandItemViewModel.SelectedVariableSet)
                or nameof(QuickCommandItemViewModel.VariableSets))
                || sharedPayloadSettingsChanged;
            bool mainRepeatStopped = changedCommand.IsRepeating
                && (directPayloadChanged || args.PropertyName == nameof(QuickCommandItemViewModel.RepeatIntervalMs));
            bool parameterRepeatStopped = changedCommand.IsParameterRepeating
                && (parameterPayloadChanged || args.PropertyName == nameof(QuickCommandItemViewModel.ParameterRepeatIntervalMs));
            if (mainRepeatStopped)
            {
                StopQuickCommandMainRepeat(changedCommand);
            }
            if (parameterRepeatStopped)
            {
                StopQuickParameterRepeat(changedCommand);
            }
            if (mainRepeatStopped || parameterRepeatStopped)
            {
                StatusText = mainRepeatStopped && parameterRepeatStopped
                    ? "快捷指令和快捷参数循环发送已停止"
                    : mainRepeatStopped
                        ? "快捷指令循环发送已停止"
                        : "快捷参数循环发送已停止";
            }
        }
        ScheduleProfileSave();
    }

    private void ReplaceQuickCommands(IReadOnlyList<QuickCommandItemViewModel> commands)
    {
        foreach (QuickCommandItemViewModel command in QuickCommands)
        {
            StopQuickCommandRepeat(command);
            command.PropertyChanged -= OnQuickCommandPropertyChanged;
        }

        _replacingQuickCommands = true;
        try
        {
            QuickCommands.ReplaceAll(commands);
        }
        finally
        {
            _replacingQuickCommands = false;
        }

        foreach (QuickCommandItemViewModel command in QuickCommands)
        {
            command.PropertyChanged += OnQuickCommandPropertyChanged;
        }

        SelectedQuickCommand = QuickCommands.FirstOrDefault();
        QuickCommandsView.Refresh();
        DeleteSelectedQuickCommandsCommand.NotifyCanExecuteChanged();
        RefreshSendCommandAvailability();
    }

    private void SelectQuickCommandCategoryForWorkspace(WorkspaceMode workspaceMode)
    {
        QuickCommandCategory category = GetQuickCommandCategoryForWorkspace(workspaceMode);
        QuickCommandCategoryOption option = QuickCommandCategories.First(item => item.Category == category);
        if (SelectedQuickCommandCategory.Category != category)
        {
            SelectedQuickCommandCategory = option;
        }
    }

    private static QuickCommandCategory GetQuickCommandCategoryForWorkspace(WorkspaceMode workspaceMode) => workspaceMode switch
    {
        WorkspaceMode.Network => QuickCommandCategory.Tcp,
        WorkspaceMode.Bluetooth => QuickCommandCategory.Bluetooth,
        WorkspaceMode.Modbus => QuickCommandCategory.Modbus,
        _ => QuickCommandCategory.Serial
    };

    private void SwitchQuickCommandCategory(QuickCommandCategory previousCategory, QuickCommandCategory category)
    {
        if (_switchingQuickCommandCategory)
        {
            return;
        }

        _switchingQuickCommandCategory = true;
        try
        {
            StoreQuickCommands(previousCategory);
            ReplaceQuickCommands(GetQuickCommandCategoryItems(category));
        }
        finally
        {
            _switchingQuickCommandCategory = false;
        }
    }

    private List<QuickCommandItemViewModel> GetQuickCommandCategoryItems(QuickCommandCategory category)
    {
        if (!_quickCommandsByCategory.TryGetValue(category, out List<QuickCommandItemViewModel>? commands))
        {
            commands = [];
            _quickCommandsByCategory[category] = commands;
        }
        return commands;
    }

    private void StoreCurrentQuickCommands()
    {
        StoreQuickCommands(SelectedQuickCommandCategory.Category);
    }

    private void StoreQuickCommands(QuickCommandCategory category) =>
        _quickCommandsByCategory[category] = QuickCommands.ToList();

    private List<QuickCommandGroup> CreateQuickCommandGroups()
    {
        StoreCurrentQuickCommands();
        return QuickCommandCategories
            .Select(option => new QuickCommandGroup
            {
                Name = $"{option.Name}指令",
                Category = option.Category,
                Commands = GetQuickCommandCategoryItems(option.Category)
                    .Select(item => item.ToModel())
                    .ToList()
            })
            .Where(group => group.Commands.Count > 0)
            .ToList();
    }

    private static QuickCommandCategory ResolveQuickCommandCategory(
        DeviceProfile profile,
        QuickCommandGroup group)
    {
        if (group.Category != QuickCommandCategory.Serial)
        {
            return group.Category;
        }

        return IsSscomImportedProfile(profile)
            && group.Name.Contains("SSCOM", StringComparison.OrdinalIgnoreCase)
            ? QuickCommandCategory.Sscom
            : QuickCommandCategory.Serial;
    }

    private bool RemoveDuplicateSscomImportedCommandCopies(DeviceProfile profile)
    {
        if (!IsSscomImportedProfile(profile))
        {
            return false;
        }

        List<QuickCommandItemViewModel> sscomCommands = GetQuickCommandCategoryItems(QuickCommandCategory.Sscom);
        if (sscomCommands.Count == 0)
        {
            return false;
        }

        bool removed = false;
        foreach (QuickCommandCategoryOption option in QuickCommandCategories.Where(item => item.Category != QuickCommandCategory.Sscom))
        {
            List<QuickCommandItemViewModel> commands = GetQuickCommandCategoryItems(option.Category);
            if (commands.Count == sscomCommands.Count
                && commands.Select(item => item.Id).SequenceEqual(sscomCommands.Select(item => item.Id)))
            {
                commands.Clear();
                removed = true;
            }
        }
        return removed;
    }

    private static bool IsSscomImportedProfile(DeviceProfile profile) =>
        profile.Description.StartsWith("从 SSCOM 导入：", StringComparison.Ordinal);

    private bool FilterQuickCommand(object item)
    {
        if (item is not QuickCommandItemViewModel command || string.IsNullOrWhiteSpace(QuickCommandSearchText))
        {
            return true;
        }
        return command.Name.Contains(QuickCommandSearchText, StringComparison.CurrentCultureIgnoreCase)
            || command.Payload.Contains(QuickCommandSearchText, StringComparison.CurrentCultureIgnoreCase)
            || command.Template.Contains(QuickCommandSearchText, StringComparison.CurrentCultureIgnoreCase);
    }

    private void ApplyQuickCommandSort()
    {
        QuickCommandsView.SortDescriptions.Clear();
        QuickCommandsView.SortDescriptions.Add(new SortDescription(nameof(QuickCommandItemViewModel.IsPinned), ListSortDirection.Descending));
        QuickCommandsView.SortDescriptions.Add(new SortDescription(nameof(QuickCommandItemViewModel.PinnedOrder), ListSortDirection.Ascending));
        switch (SelectedQuickCommandSort)
        {
            case "使用频率":
                QuickCommandsView.SortDescriptions.Add(new SortDescription(nameof(QuickCommandItemViewModel.UsageCount), ListSortDirection.Descending));
                QuickCommandsView.SortDescriptions.Add(new SortDescription(nameof(QuickCommandItemViewModel.LastUsedAt), ListSortDirection.Descending));
                break;
            case "最近使用":
                QuickCommandsView.SortDescriptions.Add(new SortDescription(nameof(QuickCommandItemViewModel.LastUsedAt), ListSortDirection.Descending));
                break;
        }
    }

    private static void NormalizePinnedQuickCommandOrders(IList<QuickCommandItemViewModel> commands)
    {
        int order = 0;
        foreach (QuickCommandItemViewModel command in commands
                     .Select((item, index) => (item, index))
                     .Where(item => item.item.IsPinned)
                     .OrderBy(item => item.item.PinnedOrder)
                     .ThenBy(item => item.index)
                     .Select(item => item.item))
        {
            command.PinnedOrder = order++;
        }
    }

    private void ScheduleProfileSave()
    {
        if (_activeProfile is null || _suppressProfileSelection)
        {
            return;
        }
        _profileSaveTimer.Stop();
        _profileSaveTimer.Start();
    }

    private DeviceProfile? CreateActiveProfileSnapshot()
    {
        if (_activeProfile is null)
        {
            return null;
        }
        string name = string.IsNullOrWhiteSpace(ProfileName) ? "未命名设备" : ProfileName.Trim();
        return BuildCurrentProfile(_activeProfile.Id, name);
    }

    private async Task SaveActiveProfileSnapshotAsync(bool showStatus)
    {
        DeviceProfile? snapshot = CreateActiveProfileSnapshot();
        if (snapshot is null)
        {
            return;
        }

        string? importedSourcePath = _importedProfileSourcePaths.TryGetValue(snapshot.Id, out string? sourcePath)
            ? sourcePath
            : null;
        try
        {
            await QueueProfileSave(snapshot).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            if (showStatus)
            {
                StatusText = $"设备配置保存失败：{exception.Message}";
            }
            return;
        }

        // 自动保存只写文件，不替换整个 Profiles 集合；手动保存时再同步名称等轻量元数据。
        if (showStatus)
        {
            int index = Profiles.ToList().FindIndex(profile => profile.Id == snapshot.Id);
            if (index >= 0 && !string.Equals(Profiles[index].Name, snapshot.Name, StringComparison.Ordinal))
            {
                _suppressProfileSelection = true;
                try
                {
                    Profiles[index] = snapshot;
                    SelectedProfile = snapshot;
                }
                finally
                {
                    _suppressProfileSelection = false;
                }
            }
        }

        if (showStatus)
        {
            StatusText = importedSourcePath is null
                ? $"设备配置已保存：{_profileStore.DirectoryPath}"
                : $"设备配置已保存，并覆盖导入文件：{importedSourcePath}";
        }
    }

    private async Task SaveImportedLegacyProfilesAsync(LegacyImportResult result)
    {
        Dictionary<string, DeviceProfile> existing = Profiles.ToDictionary(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase);
        foreach (DeviceProfile imported in result.Profiles)
        {
            DeviceProfile profile = existing.TryGetValue(imported.Name, out DeviceProfile? old)
                ? imported with { Id = old.Id }
                : imported;
            await _profileStore.SaveAsync(profile).ConfigureAwait(true);
        }

        await ReloadProfilesAsync().ConfigureAwait(true);
        StatusText = result.Warnings.Count == 0
            ? $"已导入 {result.Profiles.Count} 个设备配置到：{_profileStore.DirectoryPath}"
            : $"已导入 {result.Profiles.Count} 个配置，{result.Warnings.Count} 个警告；位置：{_profileStore.DirectoryPath}";
    }

    private async Task ReloadProfilesAsync(Guid? preferredId = null)
    {
        Guid? selectedId = preferredId ?? SelectedProfile?.Id;
        IReadOnlyList<DeviceProfile> profiles = await _profileStore.LoadAllAsync().ConfigureAwait(true);
        _suppressProfileSelection = true;
        try
        {
            Profiles.Clear();
            foreach (DeviceProfile profile in profiles)
            {
                Profiles.Add(profile);
            }
            SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id == selectedId) ?? Profiles.FirstOrDefault();
        }
        finally
        {
            _suppressProfileSelection = false;
        }
        if (SelectedProfile is not null)
        {
            LoadProfile(SelectedProfile);
        }
        else
        {
            _activeProfile = null;
        }
    }

    private DeviceProfile[] GetSelectedProfileDeleteTargets(DeviceProfile selectedProfile)
    {
        string selectedName = NormalizeProfileName(selectedProfile.Name);
        DeviceProfile[] matches = Profiles
            .Where(profile => profile.Id == selectedProfile.Id
                || string.Equals(
                    NormalizeProfileName(profile.Name),
                    selectedName,
                    StringComparison.OrdinalIgnoreCase))
            .DistinctBy(profile => profile.Id)
            .ToArray();
        return matches.Length == 0 ? [selectedProfile] : matches;
    }

    private static string NormalizeProfileName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "未命名设备" : name.Trim();

    private void LoadProfile(DeviceProfile profile)
    {
        SendRepeatEnabled = false;
        StopSendRepeat();
        bool previousSuppression = _suppressProfileSelection;
        bool repairedSscomGroups = false;
        _suppressProfileSelection = true;
        try
        {
            WorkspaceMode mode = profile.WorkspaceMode;
            if (mode == WorkspaceMode.Serial && profile.Transport.Kind != TransportKind.Serial)
            {
                mode = profile.Transport.Kind == TransportKind.BleGatt ? WorkspaceMode.Bluetooth : WorkspaceMode.Network;
            }
            SelectedWorkspaceMode = WorkspaceModes.First(option => option.Mode == mode);
            ProfileName = profile.Name;
            SelectedTransportOption = TransportOptions.First(option => option.Kind == profile.Transport.Kind);
            switch (profile.Transport)
            {
                case SerialTransportSettings serial:
                    PortName = string.IsNullOrWhiteSpace(serial.PortName) && SerialPorts.Count > 0
                        ? SerialPorts[0].PortName
                        : serial.PortName;
                    BaudRate = serial.BaudRate;
                    DataBits = serial.DataBits;
                    SerialParity = serial.Parity;
                    SerialStopBits = serial.StopBits;
                    SerialHandshake = serial.Handshake;
                    DtrEnable = serial.DtrEnable;
                    RtsEnable = serial.RtsEnable;
                    break;
                case TcpClientTransportSettings tcpClient:
                    Host = tcpClient.Host;
                    RemotePort = tcpClient.Port;
                    break;
                case TcpServerTransportSettings tcpServer:
                    LocalAddress = tcpServer.LocalAddress;
                    LocalPort = tcpServer.Port;
                    break;
                case UdpTransportSettings udp:
                    LocalAddress = udp.LocalAddress;
                    LocalPort = udp.LocalPort;
                    Host = udp.RemoteAddress;
                    RemotePort = udp.RemotePort;
                    UdpBroadcast = udp.EnableBroadcast;
                    MulticastAddress = udp.MulticastAddress ?? string.Empty;
                    break;
                case BleGattTransportSettings ble:
                    BleServiceUuid = ble.ServiceUuid;
                    BleReadUuid = ble.ReadCharacteristicUuid;
                    BleWriteUuid = ble.WriteCharacteristicUuid;
                    BleNotifyUuid = ble.NotifyCharacteristicUuid;
                    BleWriteWithoutResponse = ble.WriteWithoutResponse;
                    break;
            }

            TftpClient.ApplyPreferences(profile.Tftp ?? new TftpPreferences());
            JLink.ApplyPreferences(profile.JLink ?? new JLinkPreferences());

            SelectedEncodingName = profile.Terminal.EncodingName;
            SelectedQuickCommandDataFormat = NormalizeQuickCommandDataFormat(profile.Terminal.QuickCommandDataFormat);
            SendAsHex = profile.Terminal.SendAsHex;
            ReceiveAsHex = profile.Terminal.ReceiveAsHex;
            SendRepeatIntervalMs = Math.Clamp(profile.Terminal.SendRepeatIntervalMs, 1, 60_000);
            ReceiveTimeoutMs = Math.Clamp(profile.Terminal.ReceiveTimeoutMs, 1, 5000);
            SelectedLineEnding = profile.Terminal.LineEnding;
            _quickCommandsByCategory.Clear();
            foreach (QuickCommandCategoryOption option in QuickCommandCategories)
            {
                _quickCommandsByCategory[option.Category] = [];
            }
            foreach (QuickCommandGroup group in profile.CommandGroups)
            {
                QuickCommandCategory category = ResolveQuickCommandCategory(profile, group);
                List<QuickCommandItemViewModel> commands = GetQuickCommandCategoryItems(category);
                foreach (QuickCommand command in group.Commands)
                {
                    QuickCommand normalizedCommand = command;
                    if (category == QuickCommandCategory.Sscom
                        && string.Equals(command.LineEnding, "None", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(profile.Terminal.LineEnding, "None", StringComparison.OrdinalIgnoreCase))
                    {
                        normalizedCommand = command with { LineEnding = profile.Terminal.LineEnding };
                    }

                    commands.Add(new QuickCommandItemViewModel(normalizedCommand));
                }
            }
            foreach (List<QuickCommandItemViewModel> commands in _quickCommandsByCategory.Values)
            {
                NormalizePinnedQuickCommandOrders(commands);
            }
            repairedSscomGroups = RemoveDuplicateSscomImportedCommandCopies(profile);
            QuickCommandCategory workspaceCategory = IsSscomImportedProfile(profile)
                ? QuickCommandCategory.Sscom
                : GetQuickCommandCategoryForWorkspace(mode);
            _switchingQuickCommandCategory = true;
            try
            {
                SelectedQuickCommandCategory = QuickCommandCategories.First(item => item.Category == workspaceCategory);
            }
            finally
            {
                _switchingQuickCommandCategory = false;
            }
            ReplaceQuickCommands(GetQuickCommandCategoryItems(SelectedQuickCommandCategory.Category));
            LoadFrameTemplates(profile.FrameTemplates.Count > 0
                ? profile.FrameTemplates
                : [profile.FrameTemplate]);
            _activeProfile = profile;
        }
        finally
        {
            _suppressProfileSelection = previousSuppression;
        }

        if (repairedSscomGroups)
        {
            ScheduleProfileSave();
        }
    }

    private void LoadFrameTemplates(IEnumerable<FrameTemplate> templates)
    {
        _frameTemplates = templates.ToList();
        if (_frameTemplates.Count == 0)
        {
            _frameTemplates.Add(new FrameTemplate());
        }

        _frameTemplate = _frameTemplates[0];
        SelectedFramingMode = _frameTemplate.Mode;
        DelimiterHex = _frameTemplate.DelimiterHex;
        FixedFrameLength = _frameTemplate.FixedLength;
        LengthFieldOffset = _frameTemplate.LengthOffset;
        LengthFieldSize = _frameTemplate.LengthSize;
        LengthAdjustment = _frameTemplate.LengthAdjustment;
        IdleGapMs = _frameTemplate.IdleGapMs;
        FrameTemplateJson = SerializeFrameTemplates(_frameTemplates);
        lock (_frameCodecSync)
        {
            _frameCodec = CreateCodec(_frameTemplate);
            _idleGapFlushed = true;
        }
    }

    private DeviceProfile BuildCurrentProfile(Guid id, string name) => new()
    {
        Id = id,
        Name = name,
        Description = _activeProfile?.Description ?? string.Empty,
        WorkspaceMode = SelectedWorkspaceMode.Mode,
        Transport = BuildTransportSettings(),
        Terminal = new TerminalPreferences
        {
            EncodingName = SelectedEncodingName,
            QuickCommandDataFormat = NormalizeQuickCommandDataFormat(SelectedQuickCommandDataFormat),
            SendAsHex = SendAsHex,
            ReceiveAsHex = ReceiveAsHex,
            ShowTimestamp = true,
            LineEnding = SelectedLineEnding,
            SendRepeatIntervalMs = Math.Clamp(SendRepeatIntervalMs, 1, 60_000),
            ReceiveTimeoutMs = Math.Clamp(ReceiveTimeoutMs, 1, 5000),
            UiRecordLimit = _activeProfile?.Terminal.UiRecordLimit ?? 100_000
        },
        Tftp = TftpClient.CreatePreferences(),
        JLink = JLink.CreatePreferences(),
        CommandGroups = CreateQuickCommandGroups(),
        FrameTemplate = _frameTemplate,
        FrameTemplates = _frameTemplates.ToList(),
        ChartBindings = _activeProfile?.ChartBindings ?? []
    };

    private TransportKind SelectedTransportKind => SelectedTransportOption?.Kind
        ?? AvailableTransportOptions.FirstOrDefault()?.Kind
        ?? TransportKind.Serial;

    private TransportSettings BuildTransportSettings() => SelectedTransportKind switch
    {
        TransportKind.Serial => new SerialTransportSettings
        {
            PortName = PortName,
            BaudRate = BaudRate,
            DataBits = DataBits,
            Parity = SerialParity,
            StopBits = SerialStopBits,
            Handshake = SerialHandshake,
            DtrEnable = DtrEnable,
            RtsEnable = RtsEnable
        },
        TransportKind.TcpClient => new TcpClientTransportSettings { Host = Host, Port = RemotePort },
        TransportKind.TcpServer => new TcpServerTransportSettings { LocalAddress = LocalAddress, Port = LocalPort },
        TransportKind.Udp => new UdpTransportSettings
        {
            LocalAddress = LocalAddress,
            LocalPort = LocalPort,
            RemoteAddress = Host,
            RemotePort = RemotePort,
            EnableBroadcast = UdpBroadcast,
            MulticastAddress = string.IsNullOrWhiteSpace(MulticastAddress) ? null : MulticastAddress
        },
        TransportKind.BleGatt => new BleGattTransportSettings
        {
            BluetoothAddress = SelectedBleDevice?.Address ?? (_activeProfile?.Transport as BleGattTransportSettings)?.BluetoothAddress ?? 0,
            ServiceUuid = BleServiceUuid,
            ReadCharacteristicUuid = BleReadUuid,
            WriteCharacteristicUuid = BleWriteUuid,
            NotifyCharacteristicUuid = BleNotifyUuid,
            WriteWithoutResponse = BleWriteWithoutResponse
        },
        _ => throw new NotSupportedException()
    };

    private IFrameCodec CreateCodec(FrameTemplate template) => template.Mode switch
    {
        FramingMode.Line => new LineFrameCodec(ByteText.ParseHex(template.DelimiterHex)),
        FramingMode.Delimiter => new DelimiterFrameCodec(ByteText.ParseHex(template.DelimiterHex)),
        FramingMode.FixedLength => new FixedLengthFrameCodec(Math.Max(1, template.FixedLength)),
        FramingMode.LengthField => new LengthFieldFrameCodec(template.LengthOffset, template.LengthSize, template.LittleEndian, template.LengthAdjustment),
        FramingMode.IdleGap => new IdleGapFrameCodec(TimeSpan.FromMilliseconds(Math.Max(1, template.IdleGapMs))),
        _ => new RawFrameCodec()
    };

    private Encoding GetSelectedEncoding() => SelectedEncodingName.ToUpperInvariant() switch
    {
        "ASCII" => Encoding.ASCII,
        "GBK" => Encoding.GetEncoding(936),
        _ => new UTF8Encoding(false)
    };

    private (bool IsHex, Encoding Encoding, string FormatLabel) ResolveQuickCommandDataFormat(
        QuickCommandItemViewModel command) => NormalizeQuickCommandDataFormat(SelectedQuickCommandDataFormat) switch
    {
        "ASCII" => (false, Encoding.ASCII, "ASCII"),
        "UTF-8" => (false, new UTF8Encoding(false), "UTF-8"),
        "GBK" => (false, Encoding.GetEncoding(936), "GBK"),
        "HEX" => (true, Encoding.ASCII, "HEX"),
        _ => command.IsHex
            ? (true, Encoding.ASCII, "HEX")
            : (false, GetSelectedEncoding(), SelectedEncodingName)
    };

    private string NormalizeQuickCommandDataFormat(string? value) =>
        QuickCommandDataFormats.FirstOrDefault(
            item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
        ?? QuickCommandDataFormats[0];

    private void LogReceivedTransportPacket(TransportPacket packet)
    {
        if (packet.Direction is PacketDirection.Send or PacketDirection.Receive)
        {
            LogTransportDiagnostic(
                packet.Direction,
                packet.Direction == PacketDirection.Send ? "会话发送确认" : "传输接收",
                packet.Endpoint,
                packet.Data,
                packet.SentAsHex is null ? null : $"HEX输入={packet.SentAsHex.Value}");
            return;
        }

        if (!string.IsNullOrWhiteSpace(packet.Message))
        {
            Log.Warning(
                "通信状态消息 | 类型={Direction} | 端点={Endpoint} | 内容={Message}",
                packet.Direction,
                packet.Endpoint,
                packet.Message);
        }
    }

    private void LogTransportDiagnostic(
        PacketDirection direction,
        string source,
        string endpoint,
        ReadOnlySpan<byte> data,
        string? detail = null)
    {
        if (!App.DiagnosticLoggingEnabled)
        {
            return;
        }

        bool shouldLog;
        bool suppressionStarted = false;
        lock (_transportDiagnosticLogLock)
        {
            DateTimeOffset now = DateTimeOffset.Now;
            if (now - _transportDiagnosticWindowStart >= TimeSpan.FromSeconds(1))
            {
                _transportDiagnosticWindowStart = now;
                _transportDiagnosticEventCount = 0;
                _transportDiagnosticSuppressionLogged = false;
            }

            shouldLog = _transportDiagnosticEventCount < MaximumTransportDiagnosticEventsPerSecond;
            if (shouldLog)
            {
                _transportDiagnosticEventCount++;
            }
            else if (!_transportDiagnosticSuppressionLogged)
            {
                _transportDiagnosticSuppressionLogged = true;
                suppressionStarted = true;
            }
        }

        if (suppressionStarted)
        {
            Log.Warning(
                "通信诊断日志已限流 | 每秒最多记录 {Maximum} 条收发事件",
                MaximumTransportDiagnosticEventsPerSecond);
        }
        if (!shouldLog)
        {
            return;
        }

        Log.Information(
            "通信{Direction} | 来源={Source} | 端点={Endpoint} | 字节={ByteCount} | HEX={Hex} | {Detail}",
            direction == PacketDirection.Send ? "发送" : "接收",
            source,
            endpoint,
            data.Length,
            FormatTransportDiagnosticData(data),
            detail ?? "无附加参数");
    }

    private static string FormatTransportDiagnosticData(ReadOnlySpan<byte> data)
    {
        const int maximumBytes = 160;
        return data.Length <= maximumBytes
            ? ByteText.ToHex(data)
            : $"{ByteText.ToHex(data[..maximumBytes])} …（总计 {data.Length} 字节）";
    }

    private string NormalizeHexInput(string value)
    {
        try
        {
            return ByteText.ToHex(ByteText.ParseHex(value));
        }
        catch (FormatException)
        {
            return ByteText.ToHex(GetSelectedEncoding().GetBytes(value));
        }
    }

    private bool TryConvertHexToReadableText(string value, out string text)
    {
        try
        {
            text = GetSelectedEncoding().GetString(ByteText.ParseHex(value));
            return text.All(character => !char.IsControl(character) || character is '\r' or '\n' or '\t')
                && !text.Contains('\uFFFD');
        }
        catch (FormatException)
        {
            text = string.Empty;
            return false;
        }
    }

    private TerminalRecordItem FormatPacket(TransportPacket packet)
    {
        bool isMessage = packet.Message is not null;
        string content = packet.Message ?? DecodeIncrementally(packet.Data);
        return new TerminalRecordItem(
            packet.Timestamp,
            packet.Direction,
            packet.Endpoint,
            packet.Data.Length,
            packet.Data.ToArray(),
            content,
            isMessage,
            packet.SentAsHex);
    }

    private void AppendFormattedPacket(List<TerminalRecordItem> target, TransportPacket packet)
    {
        TimeSpan? separatorGap = _terminalWaveSeparatorTracker.Observe(
            packet,
            TimeSpan.FromMilliseconds(TerminalSeparatorIntervalMs));
        if (TerminalSeparatorEnabled && separatorGap is TimeSpan gap)
        {
            target.Add(TerminalRecordItem.CreateSeparator(packet.Timestamp, gap));
        }
        target.Add(FormatPacket(packet));
    }

    private async Task SaveAppSettingsAsync()
    {
        await _appSettingsSaveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _appSettingsStore.SaveAsync(new AppSettings
            {
                ProfileDirectory = _profileStore.DirectoryPath,
                SelectedProfileId = SelectedProfile?.Id,
                ImportedProfileSourcePaths = _importedProfileSourcePaths.ToDictionary(
                    item => item.Key,
                    item => item.Value),
                TerminalFontSize = TerminalFontSize,
                TerminalTimeColumnWidth = TerminalTimeColumnWidth,
                TerminalDirectionColumnWidth = TerminalDirectionColumnWidth,
                TerminalEndpointColumnWidth = TerminalEndpointColumnWidth,
                TerminalSizeColumnWidth = TerminalSizeColumnWidth,
                TerminalContentColumnWidth = TerminalContentColumnWidth,
                FrameTimeColumnWidth = FrameTimeColumnWidth,
                FrameLengthColumnWidth = FrameLengthColumnWidth,
                FrameHexColumnWidth = FrameHexColumnWidth,
                FrameSummaryColumnWidth = FrameSummaryColumnWidth,
                TerminalTextColor = App.NormalizeTerminalColor(TerminalTextColor, App.DefaultTerminalTextColor),
                TerminalBackgroundColor = App.NormalizeTerminalColor(TerminalBackgroundColor, App.DefaultTerminalBackgroundColor),
                TerminalSeparatorEnabled = TerminalSeparatorEnabled,
                TerminalSeparatorIntervalMs = (int)TerminalSeparatorIntervalMs,
                TerminalSeparatorStyle = SelectedTerminalSeparatorStyle.Character,
                TerminalSeparatorColor = App.NormalizeTerminalColor(TerminalSeparatorColor, App.DefaultTerminalSeparatorColor),
                GitHubRepository = GitHubRepository.Trim(),
                AutoUpdateEnabled = AutoUpdateEnabled,
                DebugLoggingEnabled = DebugLoggingEnabled,
                TerminalTextPalette = TerminalTextPalette.Select(item => item.Color).ToList(),
                TerminalBackgroundPalette = TerminalBackgroundPalette.Select(item => item.Color).ToList()
            }).ConfigureAwait(false);
        }
        finally
        {
            _appSettingsSaveLock.Release();
        }
    }

    private string DecodeIncrementally(ReadOnlySpan<byte> data)
    {
        lock (_decoderSync)
        {
            if (_receiveDecoder is null || !string.Equals(_decoderEncodingName, SelectedEncodingName, StringComparison.OrdinalIgnoreCase))
            {
                Encoding encoding = GetSelectedEncoding();
                _receiveDecoder = encoding.GetDecoder();
                _decoderEncodingName = SelectedEncodingName;
            }

            char[] characters = new char[Math.Max(1, GetSelectedEncoding().GetMaxCharCount(data.Length))];
            _receiveDecoder.Convert(data, characters, false, out _, out int charsUsed, out _);
            return new string(characters, 0, charsUsed);
        }
    }

    private byte[] GetLineEnding(string value) => value.ToUpperInvariant() switch
    {
        "CR" => [0x0D],
        "LF" => [0x0A],
        "CRLF" => [0x0D, 0x0A],
        _ => []
    };

    private string SerializeTemplate(FrameTemplate template) => JsonSerializer.Serialize(template, new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    });

    private List<FrameTemplate> DeserializeFrameTemplates(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => JsonSerializer.Deserialize<List<FrameTemplate>>(document.RootElement.GetRawText(), _jsonOptions) ?? [],
            JsonValueKind.Object =>
            [JsonSerializer.Deserialize<FrameTemplate>(document.RootElement.GetRawText(), _jsonOptions)
                ?? throw new JsonException("模板为空。 ")],
            _ => throw new JsonException("模板必须是 JSON 对象或数组。 ")
        };
    }

    private static void ValidateFrameTemplates(IEnumerable<FrameTemplate> templates)
    {
        List<FrameTemplate> items = templates.ToList();
        if (items.Count == 0)
        {
            throw new InvalidDataException("至少需要一套帧模板。 ");
        }

        foreach (FrameTemplate template in items)
        {
            if (string.IsNullOrWhiteSpace(template.MatchHex))
            {
                continue;
            }

            if (template.MatchOffset < 0)
            {
                throw new InvalidDataException($"模板“{template.Name}”的 MatchOffset 必须大于等于 0。 ");
            }

            string[] tokens = template.MatchHex
                .Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || tokens.Any(token => token is not ("?" or "??")
                && !byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)))
            {
                throw new FormatException($"模板“{template.Name}”的 MatchHex 不是有效十六进制。 ");
            }
        }
    }

    private string SerializeFrameTemplates(IReadOnlyList<FrameTemplate> templates) => templates.Count == 1
        ? SerializeTemplate(templates[0])
        : JsonSerializer.Serialize(templates, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        });

    private static ushort[] ParseRegisterValues(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }
        return text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? ushort.Parse(item.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : ushort.Parse(item, CultureInfo.InvariantCulture))
            .ToArray();
    }

    private static string FormatRate(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1024 * 1024 => $"{bytesPerSecond / 1024 / 1024:F1} MB/s",
        >= 1024 => $"{bytesPerSecond / 1024:F1} KB/s",
        _ => $"{bytesPerSecond:F0} B/s"
    };
}
