using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
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
using DeviceDebugStudio.Core.Transports;
using DeviceDebugStudio.Infrastructure.Import;
using DeviceDebugStudio.Infrastructure.Persistence;
using DeviceDebugStudio.Infrastructure.Transports;

namespace DeviceDebugStudio.App.ViewModels;

public sealed record TransportOption(TransportKind Kind, string Name);
public sealed record WorkspaceModeOption(WorkspaceMode Mode, string Name);

public partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IConfigurableDeviceProfileStore _profileStore;
    private readonly LegacyConfigImporter _legacyImporter;
    private readonly AppSettingsStore _appSettingsStore;
    private readonly DeviceProfileFileService _profileFileService;
    private readonly CaptureFileReader _captureFileReader;
    private readonly BleDiscoveryService _bleDiscovery;
    private readonly BleGattBrowserService _bleGattBrowser;
    private readonly ConcurrentQueue<TransportPacket> _pendingTerminal = new();
    private readonly List<TransportPacket> _serialTerminalBuffer = [];
    private readonly ConcurrentQueue<FrameRecordItem> _pendingFrames = new();
    private readonly ConcurrentQueue<double> _pendingChartValues = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _repeatCommands = [];
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
    private long _rxTotal;
    private long _txTotal;
    private long _lastRateBytes;
    private DateTimeOffset _lastRateTimestamp = DateTimeOffset.Now;
    private DateTimeOffset _nextPortRefresh = DateTimeOffset.Now.AddSeconds(2);
    private int _portRefreshRunning;
    private int _connectionWorkerRunning;
    private bool _manualDisconnect;
    private bool _connectionDesired;
    private CancellationTokenSource? _connectionAttemptCancellation;
    private Decoder? _receiveDecoder;
    private string _decoderEncodingName = string.Empty;
    private DateTimeOffset _lastFrameInput;
    private bool _idleGapFlushed = true;
    private bool _suppressProfileSelection;
    private bool _suppressSendModeConversion;
    private DeviceProfile? _activeProfile;
    private Task _lastProfileSaveTask = Task.CompletedTask;
    private Task _lastAppSettingsSaveTask = Task.CompletedTask;
    private readonly SemaphoreSlim _appSettingsSaveLock = new(1, 1);
    private WorkspaceMode? _connectedWorkspaceMode;

    public MainWindowViewModel(
        IConfigurableDeviceProfileStore profileStore,
        LegacyConfigImporter legacyImporter,
        AppSettingsStore appSettingsStore,
        DeviceProfileFileService profileFileService,
        CaptureFileReader captureFileReader,
        BleDiscoveryService bleDiscovery,
        BleGattBrowserService bleGattBrowser)
    {
        _profileStore = profileStore;
        _legacyImporter = legacyImporter;
        _appSettingsStore = appSettingsStore;
        _profileFileService = profileFileService;
        _captureFileReader = captureFileReader;
        _bleDiscovery = bleDiscovery;
        _bleGattBrowser = bleGattBrowser;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        selectedTransportOption = TransportOptions[0];
        selectedWorkspaceMode = WorkspaceModes[0];
        selectedQuickCommandSort = QuickCommandSortOptions[0];
        selectedEncodingName = "UTF-8";
        selectedLineEnding = "None";
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
            await SaveActiveProfileSnapshotAsync(showStatus: false).ConfigureAwait(true);
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

        RefreshPorts();
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
        new(WorkspaceMode.Modbus, "Modbus")
    ];

    public ObservableCollection<TransportOption> AvailableTransportOptions { get; } = [];
    public IReadOnlyList<string> QuickCommandSortOptions { get; } = ["使用频率", "最近使用", "手动顺序"];

    public IReadOnlyList<int> BaudRates { get; } = [9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600, 1000000, 2000000];
    public IReadOnlyList<string> EncodingNames { get; } = ["ASCII", "UTF-8", "GBK"];
    public IReadOnlyList<string> LineEndings { get; } = ["None", "CR", "LF", "CRLF"];
    public IReadOnlyList<ChecksumKind> ChecksumKinds { get; } = Enum.GetValues<ChecksumKind>();
    public IReadOnlyList<FramingMode> FramingModes { get; } = Enum.GetValues<FramingMode>();
    public IReadOnlyList<string> ModbusFunctions { get; } =
    [
        "01 读线圈", "02 读离散输入", "03 读保持寄存器", "04 读输入寄存器",
        "05 写单线圈", "06 写单寄存器", "0F 写多线圈", "10 写多寄存器"
    ];

    public ObservableCollection<DeviceProfile> Profiles { get; } = [];
    public ObservableCollection<SerialPortInfo> SerialPorts { get; } = [];
    public ObservableCollection<BleDeviceInfo> BleDevices { get; } = [];
    public ObservableCollection<BleGattServiceInfo> GattServices { get; } = [];
    public ObservableCollection<TerminalRecordItem> TerminalRecords { get; } = [];
    public ObservableCollection<FrameRecordItem> FrameRecords { get; } = [];
    public ObservableCollection<QuickCommandItemViewModel> QuickCommands { get; } = [];
    public ObservableCollection<ColorPaletteItem> TerminalTextPalette { get; } = [];
    public ObservableCollection<ColorPaletteItem> TerminalBackgroundPalette { get; } = [];
    public ICollectionView QuickCommandsView { get; }
    public ObservableCollection<ModbusRegisterItem> ModbusRegisters { get; } = [];

    public event Action<int>? RecordsAppended;
    public event Action<double>? ChartValueAdded;

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
    private bool autoReconnect;

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
    private string variablesText = string.Empty;

    [ObservableProperty]
    private bool isPaused;

    [ObservableProperty]
    private bool autoScroll = true;

    [ObservableProperty]
    private double terminalFontSize = 12;

    [ObservableProperty]
    private string terminalTextColor = App.DefaultTerminalTextColor;

    [ObservableProperty]
    private string terminalBackgroundColor = App.DefaultTerminalBackgroundColor;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private string quickCommandSearchText = string.Empty;

    [ObservableProperty]
    private string selectedQuickCommandSort;

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

    public string ConnectionButtonText => _connectionDesired ? "断开" : "连接";
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
    public bool IsFrameWorkspaceVisible => SelectedWorkspaceMode.Mode is WorkspaceMode.Serial or WorkspaceMode.Network;
    public bool IsChartWorkspaceVisible => SelectedWorkspaceMode.Mode is WorkspaceMode.Serial or WorkspaceMode.Network or WorkspaceMode.Bluetooth;
    public string TerminalTabHeader => IsModbusWorkspace ? "通信记录" : "终端";

    public async Task InitializeAsync()
    {
        AppSettings settings = await _appSettingsStore.LoadAsync().ConfigureAwait(true);
        try
        {
            _profileStore.SetDirectory(settings.ProfileDirectory);
        }
        catch (Exception)
        {
            _profileStore.SetDirectory(AppPaths.ProfilesDirectory);
        }
        ProfileDirectory = _profileStore.DirectoryPath;
        TerminalFontSize = Math.Clamp(Math.Round(settings.TerminalFontSize), 9, 28);
        TerminalTextColor = App.NormalizeTerminalColor(settings.TerminalTextColor, App.DefaultTerminalTextColor);
        TerminalBackgroundColor = App.NormalizeTerminalColor(settings.TerminalBackgroundColor, App.DefaultTerminalBackgroundColor);
        LoadTerminalPalette(TerminalTextPalette, settings.TerminalTextPalette, App.DefaultTerminalTextPalette);
        LoadTerminalPalette(TerminalBackgroundPalette, settings.TerminalBackgroundPalette, App.DefaultTerminalBackgroundPalette);
        App.ApplyTerminalColors(TerminalTextColor, TerminalBackgroundColor);
        UpdateAvailableTransportOptions();
        await ReloadProfilesAsync().ConfigureAwait(true);
        if (Profiles.Count == 0)
        {
            DeviceProfile profile = new()
            {
                Name = "快速调试",
                WorkspaceMode = WorkspaceMode.Serial,
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
        else
        {
            SelectedProfile = Profiles[0];
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
                DeviceProfile profile = await _profileFileService.ImportAsync(path).ConfigureAwait(true);
                await _profileStore.SaveAsync(profile).ConfigureAwait(true);
                count++;
            }
            await ReloadProfilesAsync().ConfigureAwait(true);
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
            int index = 0;
            foreach (TransportPacket packet in result.Packets)
            {
                TerminalRecords.Add(FormatPacket(packet));
                if (++index % 1000 == 0)
                {
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }
            }
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

    public string ExportTerminalText() => string.Join(Environment.NewLine, TerminalRecords.Select(item =>
        $"{item.Timestamp:O}\t{item.DirectionText}\t{item.Endpoint}\t{item.GetDisplayContent(ReceiveAsHex)}"));

    [RelayCommand]
    private void RefreshPorts()
    {
        ApplyPortList(SerialPortDiscovery.GetPorts());
    }

    [RelayCommand]
    private async Task ScanBleAsync()
    {
        IsBusy = true;
        StatusText = "正在扫描 BLE 设备…";
        try
        {
            IReadOnlyList<BleDeviceInfo> devices = await _bleDiscovery.ScanAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(true);
            BleDevices.Clear();
            foreach (BleDeviceInfo device in devices)
            {
                BleDevices.Add(device);
            }
            StatusText = $"发现 {devices.Count} 个 BLE 设备";
        }
        catch (Exception exception)
        {
            StatusText = $"BLE 扫描失败：{exception.Message}";
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
            _connectionAttemptCancellation?.Cancel();
            IsConnected = false;
        }
        OnPropertyChanged(nameof(ConnectionButtonText));
        EnsureConnectionStateWorker();
    }

    private bool CanUseConnectedSession => IsConnected
        && _session is not null
        && _connectedWorkspaceMode == SelectedWorkspaceMode.Mode;

    public bool CanSend => CanUseConnectedSession && !string.IsNullOrEmpty(SendText);

    private bool CanSendQuickCommand(QuickCommandItemViewModel? command) =>
        CanUseConnectedSession && command is not null && !string.IsNullOrEmpty(command.Payload);

    private bool CanToggleQuickRepeat(QuickCommandItemViewModel? command) =>
        CanUseConnectedSession && command is not null && !string.IsNullOrEmpty(command.Payload);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        try
        {
            _ = await SendPayloadAsync(SendText, SendAsHex, SelectedLineEnding, SelectedSendChecksum, ChecksumLittleEndian).ConfigureAwait(true);
        }
        finally
        {
            RefreshSendCommandAvailability();
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
        if (await SendPayloadAsync(command.Payload, command.IsHex, command.LineEnding, command.Checksum, command.ChecksumLittleEndian).ConfigureAwait(true))
        {
            command.RegisterUse();
            ScheduleProfileSave();
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleQuickRepeat))]
    private async Task ToggleQuickRepeatAsync(QuickCommandItemViewModel? command)
    {
        if (command is null)
        {
            return;
        }

        if (_repeatCommands.Remove(command.Id, out CancellationTokenSource? existing))
        {
            existing.Cancel();
            existing.Dispose();
            command.IsRepeating = false;
            return;
        }

        CancellationTokenSource source = new();
        _repeatCommands[command.Id] = source;
        command.IsRepeating = true;
        command.RegisterUse();
        ScheduleProfileSave();
        try
        {
            while (!source.IsCancellationRequested)
            {
                _ = await SendPayloadAsync(command.Payload, command.IsHex, command.LineEnding, command.Checksum, command.ChecksumLittleEndian).ConfigureAwait(true);
                await Task.Delay(Math.Max(10, command.RepeatIntervalMs), source.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            command.IsRepeating = false;
        }
    }

    [RelayCommand]
    private void AddQuickCommand()
    {
        QuickCommands.Add(new QuickCommandItemViewModel(new QuickCommand { Name = "新命令", Payload = string.Empty }));
        ScheduleProfileSave();
    }

    [RelayCommand]
    private void ApplyQuickCommandHexMode(QuickCommandItemViewModel? command)
    {
        if (command is null || string.IsNullOrEmpty(command.Payload))
        {
            return;
        }

        if (command.IsHex)
        {
            command.Payload = NormalizeHexInput(command.Payload);
            StatusText = "快捷指令使用 HEX 输入";
            return;
        }

        if (!TryConvertHexToReadableText(command.Payload, out string text))
        {
            StatusText = "快捷指令使用文本输入，内容保持不变";
            return;
        }

        command.Payload = text;
        StatusText = "快捷指令使用文本输入";
    }

    [RelayCommand]
    private void DeleteQuickCommand(QuickCommandItemViewModel? command)
    {
        if (command is not null)
        {
            StopQuickCommandRepeat(command);
            QuickCommands.Remove(command);
            ScheduleProfileSave();
        }
    }

    private bool CanDeleteSelectedQuickCommands() => QuickCommands.Any(command => command.IsSelectedForBulkDelete);

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedQuickCommands))]
    private void DeleteSelectedQuickCommands()
    {
        QuickCommandItemViewModel[] selected = QuickCommands
            .Where(command => command.IsSelectedForBulkDelete)
            .ToArray();
        foreach (QuickCommandItemViewModel command in selected)
        {
            StopQuickCommandRepeat(command);
            QuickCommands.Remove(command);
        }

        if (selected.Length > 0)
        {
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
        if (_repeatCommands.Remove(command.Id, out CancellationTokenSource? source))
        {
            source.Cancel();
            source.Dispose();
        }
        command.IsRepeating = false;
    }

    [RelayCommand]
    private void ClearTerminal()
    {
        _pendingTerminal.Clear();
        _serialTerminalBuffer.Clear();
        TerminalRecords.Clear();
        FrameRecords.Clear();
        StatusText = "已清空显示，捕获文件未删除";
    }

    [RelayCommand]
    private void ApplyFrameTemplate()
    {
        try
        {
            _frameTemplate = JsonSerializer.Deserialize<FrameTemplate>(FrameTemplateJson, _jsonOptions)
                ?? throw new JsonException("模板为空。 ");
            LoadFrameTemplate(_frameTemplate);
            StatusText = "帧模板已应用";
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            StatusText = $"帧模板无效：{exception.Message}";
        }
    }

    [RelayCommand]
    private void ApplyFramingOptions()
    {
        _frameTemplate = _frameTemplate with
        {
            Mode = SelectedFramingMode,
            DelimiterHex = DelimiterHex,
            FixedLength = Math.Max(1, FixedFrameLength),
            LengthOffset = Math.Max(0, LengthFieldOffset),
            LengthSize = Math.Clamp(LengthFieldSize, 1, 4),
            LengthAdjustment = LengthAdjustment,
            IdleGapMs = Math.Max(1, IdleGapMs)
        };
        lock (_frameCodecSync)
        {
            _frameCodec = CreateCodec(_frameTemplate);
            _idleGapFlushed = true;
        }
        FrameTemplateJson = SerializeTemplate(_frameTemplate);
        StatusText = $"已切换分帧：{SelectedFramingMode}";
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
        if (SelectedProfile is null)
        {
            return;
        }
        await _profileStore.DeleteAsync(SelectedProfile.Id).ConfigureAwait(true);
        await ReloadProfilesAsync().ConfigureAwait(true);
    }

    public async ValueTask DisposeAsync()
    {
        _uiTimer.Stop();
        _profileSaveTimer.Stop();
        _appSettingsSaveTimer.Stop();
        foreach (CancellationTokenSource source in _repeatCommands.Values)
        {
            source.Cancel();
            source.Dispose();
        }
        _repeatCommands.Clear();
        await SaveActiveProfileSnapshotAsync(showStatus: false).ConfigureAwait(true);
        await _lastProfileSaveTask.ConfigureAwait(true);
        await _lastAppSettingsSaveTask.ConfigureAwait(true);
        await SaveAppSettingsAsync().ConfigureAwait(true);
        _connectionDesired = false;
        _connectionAttemptCancellation?.Cancel();
        await DisconnectInternalAsync().ConfigureAwait(true);
        _appSettingsSaveLock.Dispose();
    }

    partial void OnSelectedProfileChanged(DeviceProfile? value)
    {
        if (_suppressProfileSelection || value is null)
        {
            return;
        }

        if (_activeProfile is not null && _activeProfile.Id != value.Id)
        {
            DeviceProfile? snapshot = CreateActiveProfileSnapshot();
            if (snapshot is not null)
            {
                int index = Profiles.ToList().FindIndex(profile => profile.Id == snapshot.Id);
                if (index >= 0)
                {
                    Profiles[index] = snapshot;
                }
                _lastProfileSaveTask = _profileStore.SaveAsync(snapshot);
            }
        }

        LoadProfile(value);
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

    partial void OnTerminalTextColorChanged(string value) => ApplyTerminalColorChange(value, true);

    partial void OnTerminalBackgroundColorChanged(string value) => ApplyTerminalColorChange(value, false);

    private void ApplyTerminalColorChange(string value, bool isTextColor)
    {
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

        App.ApplyTerminalColors(TerminalTextColor, TerminalBackgroundColor);
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
        LoadTerminalPalette(TerminalTextPalette, App.DefaultTerminalTextPalette, App.DefaultTerminalTextPalette);
        LoadTerminalPalette(TerminalBackgroundPalette, App.DefaultTerminalBackgroundPalette, App.DefaultTerminalBackgroundPalette);
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
        if (_connectedWorkspaceMode is not null && _connectedWorkspaceMode != value.Mode)
        {
            _connectionDesired = false;
            _manualDisconnect = true;
            _connectionAttemptCancellation?.Cancel();
            OnPropertyChanged(nameof(ConnectionButtonText));
            EnsureConnectionStateWorker();
        }
        UpdateAvailableTransportOptions();
        SelectedWorkspaceTabIndex = value.Mode switch
        {
            WorkspaceMode.Modbus => 3,
            WorkspaceMode.Bluetooth => 4,
            _ => 0
        };
        OnPropertyChanged(nameof(IsSerialWorkspace));
        OnPropertyChanged(nameof(IsNetworkWorkspace));
        OnPropertyChanged(nameof(IsBluetoothWorkspace));
        OnPropertyChanged(nameof(IsModbusWorkspace));
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
        OnPropertyChanged(nameof(ConnectionSummary));
        ScheduleProfileSave();
    }

    partial void OnProfileNameChanged(string value) => ScheduleProfileSave();

    partial void OnSendTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanSend));
        SendCommand.NotifyCanExecuteChanged();
    }

    partial void OnSendAsHexChanged(bool value)
    {
        if (_suppressSendModeConversion || string.IsNullOrEmpty(SendText))
        {
            ScheduleProfileSave();
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
        }
    }

    partial void OnQuickCommandSearchTextChanged(string value) => QuickCommandsView.Refresh();

    partial void OnSelectedQuickCommandSortChanged(string value)
    {
        ApplyQuickCommandSort();
        QuickCommandsView.Refresh();
    }

    partial void OnPortNameChanged(string value) => OnPropertyChanged(nameof(ConnectionSummary));
    partial void OnBaudRateChanged(int value) => OnPropertyChanged(nameof(ConnectionSummary));
    partial void OnHostChanged(string value) => OnPropertyChanged(nameof(ConnectionSummary));
    partial void OnRemotePortChanged(int value) => OnPropertyChanged(nameof(ConnectionSummary));
    partial void OnLocalAddressChanged(string value) => OnPropertyChanged(nameof(ConnectionSummary));
    partial void OnLocalPortChanged(int value) => OnPropertyChanged(nameof(ConnectionSummary));

    partial void OnSelectedBleDeviceChanged(BleDeviceInfo? value)
    {
        OnPropertyChanged(nameof(ConnectionSummary));
    }

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectionButtonText));
        OnPropertyChanged(nameof(CanSend));
        RefreshSendCommandAvailability();
    }

    private void RefreshSendCommandAvailability()
    {
        Dispatcher dispatcher = Application.Current.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            {
                dispatcher.BeginInvoke(RefreshSendCommandAvailability, DispatcherPriority.Normal);
            }
            return;
        }

        SendCommand.NotifyCanExecuteChanged();
        SendQuickCommandCommand.NotifyCanExecuteChanged();
        ToggleQuickRepeatCommand.NotifyCanExecuteChanged();
    }

    partial void OnModbusUnitIdChanged(int value)
    {
        _modbusSlave.UnitId = (byte)Math.Clamp(value, 0, byte.MaxValue);
    }

    partial void OnSearchTextChanged(string value)
    {
        ICollectionView view = CollectionViewSource.GetDefaultView(TerminalRecords);
        view.Filter = item =>
        {
            if (string.IsNullOrWhiteSpace(value) || item is not TerminalRecordItem record)
            {
                return true;
            }
            return record.GetDisplayContent(ReceiveAsHex).Contains(value, StringComparison.CurrentCultureIgnoreCase)
                || record.Endpoint.Contains(value, StringComparison.CurrentCultureIgnoreCase)
                || record.DirectionText.Contains(value, StringComparison.OrdinalIgnoreCase);
        };
        view.Refresh();
    }

    private void EnsureConnectionStateWorker()
    {
        if (Interlocked.CompareExchange(ref _connectionWorkerRunning, 1, 0) == 0)
        {
            _ = ProcessConnectionStateAsync();
        }
    }

    private async Task ProcessConnectionStateAsync()
    {
        try
        {
            while (_connectionDesired != IsConnected || (!_connectionDesired && _session is not null))
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
            if (_connectionDesired != IsConnected || (!_connectionDesired && _session is not null))
            {
                EnsureConnectionStateWorker();
            }
        }
    }

    private async Task ConnectInternalAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        _manualDisconnect = false;
        StatusText = "正在连接…";
        CommunicationSession? connectingSession = null;
        try
        {
            WorkspaceMode connectingWorkspace = SelectedWorkspaceMode.Mode;
            TransportSettings settings = BuildTransportSettings();
            ITransport transport = TransportFactory.Create(settings);
            SqliteCaptureStore capture = new();
            connectingSession = new CommunicationSession(SelectedProfile?.Name ?? "快速调试", transport, capture);
            connectingSession.Faulted += OnSessionFaulted;
            await connectingSession.ConnectAsync(cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_connectionDesired || SelectedWorkspaceMode.Mode != connectingWorkspace)
            {
                connectingSession.Faulted -= OnSessionFaulted;
                await connectingSession.DisposeAsync().ConfigureAwait(true);
                connectingSession = null;
                StatusText = _connectionDesired ? "连接模式已变化，请重新连接" : "已断开";
                return;
            }

            _session = connectingSession;
            connectingSession = null;
            _consumeCancellation = new CancellationTokenSource();
            CommunicationSession activeSession = _session;
            _consumeTask = Task.Run(() => ConsumeSessionAsync(activeSession, _consumeCancellation.Token), CancellationToken.None);
            _connectedWorkspaceMode = connectingWorkspace;
            IsConnected = true;
            StatusText = $"已连接：{transport.DisplayName}";
        }
        catch (OperationCanceledException)
        {
            StatusText = _connectionDesired ? "正在重新连接…" : "已断开";
        }
        catch (Exception exception)
        {
            StatusText = $"连接失败：{exception.Message}";
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

    private async Task DisconnectInternalAsync() => await DisconnectSessionAsync("已断开").ConfigureAwait(true);

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
        _connectedWorkspaceMode = null;
        IsConnected = false;
        StatusText = statusText;
    }

    private async Task ConsumeSessionAsync(CommunicationSession session, CancellationToken cancellationToken)
    {
        await foreach (TransportPacket packet in session.ReadDisplayAsync(cancellationToken).ConfigureAwait(false))
        {
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
                _pendingTerminal.Enqueue(packet);
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
                    _pendingChartValues.Enqueue(value);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or ArgumentException)
        {
            _pendingFrames.Enqueue(new FrameRecordItem(packet.Timestamp, packet.Data.Length, ByteText.ToHex(packet.Data), exception.Message, false));
        }
    }

    private void EnqueueDecodedFrames(IReadOnlyList<byte[]> frames, DateTimeOffset timestamp)
    {
        foreach (byte[] frame in frames)
        {
            DecodedFrame decoded = GenericFrameDecoder.Decode(frame, _frameTemplate);
            _pendingFrames.Enqueue(FrameRecordItem.Create(timestamp, decoded));
            foreach (DecodedField field in decoded.Fields)
            {
                if (field.Value is double numeric)
                {
                    _pendingChartValues.Enqueue(numeric);
                }
            }
        }
    }

    private async Task<bool> SendPayloadAsync(string payload, bool isHex, string lineEnding, ChecksumKind checksum, bool littleEndian)
    {
        try
        {
            if (!CanUseConnectedSession)
            {
                throw new InvalidOperationException("请先建立连接。 ");
            }

            string expanded = ByteText.ExpandVariables(payload, ParseVariables());
            byte[] data = ByteText.ParseInput(expanded, isHex, GetSelectedEncoding());
            byte[] ending = GetLineEnding(lineEnding);
            byte[] withEnding = new byte[data.Length + ending.Length];
            data.CopyTo(withEnding, 0);
            ending.CopyTo(withEnding, data.Length);
            byte[] final = ChecksumCalculator.Append(withEnding, checksum, littleEndian);
            if (final.Length == 0)
            {
                StatusText = "发送内容为空";
                return false;
            }
            CommunicationSession session = _session ?? throw new InvalidOperationException("请先建立连接。 ");
            await session.SendAsync(final, sentAsHex: isHex).ConfigureAwait(true);
            StatusText = isHex
                ? $"已发送 {final.Length} 字节（HEX 输入）"
                : $"已发送 {final.Length} 字节（文本输入）";
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusText = "发送已取消，连接状态已变化";
            return false;
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or IOException or ArgumentException)
        {
            StatusText = $"发送失败：{exception.Message}";
            return false;
        }
    }

    private async void OnSessionFaulted(object? sender, Exception exception)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            await Application.Current.Dispatcher.InvokeAsync(() => OnSessionFaulted(sender, exception));
            return;
        }

        StatusText = $"连接异常：{exception.Message}";
        IsConnected = false;
        bool shouldReconnect = !_manualDisconnect && _connectionDesired && AutoReconnect;
        if (!shouldReconnect)
        {
            _connectionDesired = false;
            OnPropertyChanged(nameof(ConnectionButtonText));
        }
        else
        {
            StatusText = "连接中断，2 秒后重连…";
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
        }

        await Task.Yield();
        await DisconnectInternalAsync().ConfigureAwait(true);
        if (shouldReconnect && _connectionDesired && !_manualDisconnect)
        {
            EnsureConnectionStateWorker();
        }
    }

    private void OnUiTimerTick(object? sender, EventArgs args)
    {
        List<TransportPacket> pendingPackets = new(500);
        while (pendingPackets.Count < 500 && _pendingTerminal.TryDequeue(out TransportPacket? packet))
        {
            pendingPackets.Add(packet);
        }

        IReadOnlyList<TransportPacket> displayPackets;
        if (SelectedTransportKind == TransportKind.Serial)
        {
            TimeSpan receiveGap = TimeSpan.FromMilliseconds(10);
            _serialTerminalBuffer.AddRange(pendingPackets);
            int readyCount = TransportPacketCoalescer.GetReadyPrefixCount(
                _serialTerminalBuffer,
                DateTimeOffset.Now,
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
                    TimeSpan.FromMilliseconds(10));
                _serialTerminalBuffer.Clear();
            }
        }
        foreach (TransportPacket packet in displayPackets)
        {
            TerminalRecords.Add(FormatPacket(packet));
        }
        int added = displayPackets.Count;
        int limit = SelectedProfile?.Terminal.UiRecordLimit ?? 100_000;
        while (TerminalRecords.Count > limit)
        {
            TerminalRecords.RemoveAt(0);
        }

        int frameAdded = 0;
        while (frameAdded < 200 && _pendingFrames.TryDequeue(out FrameRecordItem? frame))
        {
            FrameRecords.Add(frame);
            frameAdded++;
        }
        while (FrameRecords.Count > 20_000)
        {
            FrameRecords.RemoveAt(0);
        }

        while (_pendingChartValues.TryDequeue(out double value))
        {
            ChartValueAdded?.Invoke(value);
        }

        ReceivedBytes = Interlocked.Read(ref _rxTotal);
        SentBytes = Interlocked.Read(ref _txTotal);
        DateTimeOffset now = DateTimeOffset.Now;
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
            if (!ports.Select(item => item.PortName).SequenceEqual(SerialPorts.Select(item => item.PortName), StringComparer.OrdinalIgnoreCase))
            {
                ApplyPortList(ports);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _portRefreshRunning, 0);
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
        OnPropertyChanged(nameof(ConnectionSummary));
    }

    private void UpdateAvailableTransportOptions()
    {
        TransportKind[] kinds = SelectedWorkspaceMode.Mode switch
        {
            WorkspaceMode.Serial => [TransportKind.Serial],
            WorkspaceMode.Network => [TransportKind.TcpClient, TransportKind.TcpServer, TransportKind.Udp],
            WorkspaceMode.Bluetooth => [TransportKind.BleGatt],
            WorkspaceMode.Modbus => [TransportKind.Serial, TransportKind.TcpClient, TransportKind.TcpServer],
            _ => [TransportKind.Serial]
        };
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
        ScheduleProfileSave();
    }

    private void OnQuickCommandPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(QuickCommandItemViewModel.IsSelectedForBulkDelete))
        {
            DeleteSelectedQuickCommandsCommand.NotifyCanExecuteChanged();
        }
        ScheduleProfileSave();
    }

    private bool FilterQuickCommand(object item)
    {
        if (item is not QuickCommandItemViewModel command || string.IsNullOrWhiteSpace(QuickCommandSearchText))
        {
            return true;
        }
        return command.Name.Contains(QuickCommandSearchText, StringComparison.CurrentCultureIgnoreCase)
            || command.Payload.Contains(QuickCommandSearchText, StringComparison.CurrentCultureIgnoreCase);
    }

    private void ApplyQuickCommandSort()
    {
        QuickCommandsView.SortDescriptions.Clear();
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
        await _lastProfileSaveTask.ConfigureAwait(true);
        await _profileStore.SaveAsync(snapshot).ConfigureAwait(true);
        _activeProfile = snapshot;

        int index = Profiles.ToList().FindIndex(profile => profile.Id == snapshot.Id);
        if (index >= 0)
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

        if (showStatus)
        {
            StatusText = $"设备配置已保存：{_profileStore.DirectoryPath}";
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
    }

    private void LoadProfile(DeviceProfile profile)
    {
        bool previousSuppression = _suppressProfileSelection;
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
            AutoReconnect = profile.Transport.AutoReconnect;
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

            SelectedEncodingName = profile.Terminal.EncodingName;
            SendAsHex = profile.Terminal.SendAsHex;
            ReceiveAsHex = profile.Terminal.ReceiveAsHex;
            SelectedLineEnding = profile.Terminal.LineEnding;
            QuickCommands.Clear();
            foreach (QuickCommand command in profile.CommandGroups.SelectMany(group => group.Commands))
            {
                QuickCommands.Add(new QuickCommandItemViewModel(command));
            }
            LoadFrameTemplate(profile.FrameTemplate);
            _activeProfile = profile;
        }
        finally
        {
            _suppressProfileSelection = previousSuppression;
        }
        QuickCommandsView.Refresh();
    }

    private void LoadFrameTemplate(FrameTemplate template)
    {
        _frameTemplate = template;
        SelectedFramingMode = template.Mode;
        DelimiterHex = template.DelimiterHex;
        FixedFrameLength = template.FixedLength;
        LengthFieldOffset = template.LengthOffset;
        LengthFieldSize = template.LengthSize;
        LengthAdjustment = template.LengthAdjustment;
        IdleGapMs = template.IdleGapMs;
        FrameTemplateJson = SerializeTemplate(template);
        lock (_frameCodecSync)
        {
            _frameCodec = CreateCodec(template);
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
            SendAsHex = SendAsHex,
            ReceiveAsHex = ReceiveAsHex,
            ShowTimestamp = true,
            LineEnding = SelectedLineEnding,
            UiRecordLimit = _activeProfile?.Terminal.UiRecordLimit ?? 100_000
        },
        CommandGroups = [new QuickCommandGroup { Name = "常用命令", Commands = QuickCommands.Select(item => item.ToModel()).ToList() }],
        FrameTemplate = _frameTemplate,
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
            RtsEnable = RtsEnable,
            AutoReconnect = AutoReconnect
        },
        TransportKind.TcpClient => new TcpClientTransportSettings { Host = Host, Port = RemotePort, AutoReconnect = AutoReconnect },
        TransportKind.TcpServer => new TcpServerTransportSettings { LocalAddress = LocalAddress, Port = LocalPort, AutoReconnect = AutoReconnect },
        TransportKind.Udp => new UdpTransportSettings
        {
            LocalAddress = LocalAddress,
            LocalPort = LocalPort,
            RemoteAddress = Host,
            RemotePort = RemotePort,
            EnableBroadcast = UdpBroadcast,
            MulticastAddress = string.IsNullOrWhiteSpace(MulticastAddress) ? null : MulticastAddress,
            AutoReconnect = AutoReconnect
        },
        TransportKind.BleGatt => new BleGattTransportSettings
        {
            BluetoothAddress = SelectedBleDevice?.Address ?? (_activeProfile?.Transport as BleGattTransportSettings)?.BluetoothAddress ?? 0,
            ServiceUuid = BleServiceUuid,
            ReadCharacteristicUuid = BleReadUuid,
            WriteCharacteristicUuid = BleWriteUuid,
            NotifyCharacteristicUuid = BleNotifyUuid,
            WriteWithoutResponse = BleWriteWithoutResponse,
            AutoReconnect = AutoReconnect
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

    private Dictionary<string, string> ParseVariables()
    {
        Dictionary<string, string> variables = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in VariablesText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf('=');
            if (separator > 0)
            {
                variables[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }
        return variables;
    }

    private Encoding GetSelectedEncoding() => SelectedEncodingName.ToUpperInvariant() switch
    {
        "ASCII" => Encoding.ASCII,
        "GBK" => Encoding.GetEncoding(936),
        _ => new UTF8Encoding(false)
    };

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

    private async Task SaveAppSettingsAsync()
    {
        await _appSettingsSaveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _appSettingsStore.SaveAsync(new AppSettings
            {
                ProfileDirectory = _profileStore.DirectoryPath,
                TerminalFontSize = TerminalFontSize,
                TerminalTextColor = App.NormalizeTerminalColor(TerminalTextColor, App.DefaultTerminalTextColor),
                TerminalBackgroundColor = App.NormalizeTerminalColor(TerminalBackgroundColor, App.DefaultTerminalBackgroundColor),
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
