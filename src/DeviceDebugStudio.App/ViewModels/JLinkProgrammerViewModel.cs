using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceDebugStudio.Core.Profiles;
using DeviceDebugStudio.Infrastructure.Programming;

namespace DeviceDebugStudio.App.ViewModels;

public sealed partial class JLinkFlashSectorItemViewModel : ObservableObject
{
    private readonly JLinkFlashSector _sector;

    public JLinkFlashSectorItemViewModel(JLinkFlashSector sector, bool isSelected)
    {
        _sector = sector;
        IsSelected = isSelected;
    }

    public int Index => _sector.Index;
    public string Label => _sector.Label;
    public uint StartAddress => _sector.StartAddress;
    public uint EndAddressExclusive => _sector.EndAddressExclusive;
    public string AddressText => $"0x{StartAddress:X8} - 0x{EndAddressExclusive - 1:X8}";
    public string SizeText => _sector.SizeBytes >= 1024
        ? $"{_sector.SizeBytes / 1024} KB"
        : $"{_sector.SizeBytes} B";
    public JLinkFlashSector Sector => _sector;

    [ObservableProperty]
    private bool isSelected;

    public event Action? SelectionChanged;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();
}

public partial class JLinkProgrammerViewModel : ObservableObject, IAsyncDisposable
{
    private readonly JLinkProgrammer _programmer = new();
    private CancellationTokenSource? _operationCancellation;
    private TaskCompletionSource<bool>? _operationCompletion;
    private bool _suppressPreferenceChanged;
    private List<JLinkEraseRangePreference> _savedEraseRanges = [];
    private int _disposed;

    public IReadOnlyList<string> Interfaces { get; } = ["SWD", "JTAG"];
    public ObservableCollection<string> LogEntries { get; } = [];
    public ObservableCollection<JLinkFlashSectorItemViewModel> FlashSectors { get; } = [];

    public event Action? PreferencesChanged;

    [ObservableProperty]
    private string executablePath = string.Empty;

    [ObservableProperty]
    private string device = "STM32F103C8";

    [ObservableProperty]
    private bool autoDetectTarget = true;

    [ObservableProperty]
    private string interfaceName = "SWD";

    [ObservableProperty]
    private int speedKHz = 4000;

    [ObservableProperty]
    private string firmwareFile = string.Empty;

    [ObservableProperty]
    private string flashAddressText = "0x08000000";

    [ObservableProperty]
    private bool eraseBeforeProgramming = true;

    [ObservableProperty]
    private bool verify = true;

    [ObservableProperty]
    private bool resetAfterProgramming = true;

    [ObservableProperty]
    private bool runAfterProgramming = true;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isConnecting;

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private bool isProgressIndeterminate;

    [ObservableProperty]
    private int progressPercent;

    [ObservableProperty]
    private string progressText = "等待烧录";

    [ObservableProperty]
    private string statusText = "未连接";

    [ObservableProperty]
    private string logText = string.Empty;

    [ObservableProperty]
    private string detectedDeviceText = "未读取目标信息";

    [ObservableProperty]
    private string targetCoreText = "-";

    [ObservableProperty]
    private string flashSizeText = "-";

    public int SelectedSectorCount => FlashSectors.Count(item => item.IsSelected);

    public string SectorSelectionText => FlashSectors.Count == 0
        ? "连接后读取 Flash 扇区"
        : $"已选择 {SelectedSectorCount}/{FlashSectors.Count} 个扇区";

    public JLinkPreferences CreatePreferences() => new()
    {
        ExecutablePath = ExecutablePath.Trim(),
        Device = string.IsNullOrWhiteSpace(Device) ? "STM32F103C8" : Device.Trim(),
        AutoDetectTarget = AutoDetectTarget,
        InterfaceName = string.IsNullOrWhiteSpace(InterfaceName) ? "SWD" : InterfaceName.Trim(),
        SpeedKHz = Math.Clamp(SpeedKHz, 1, 50_000),
        FirmwareFile = FirmwareFile.Trim(),
        FlashAddress = ParseFlashAddressOrDefault(FlashAddressText),
        EraseBeforeProgramming = EraseBeforeProgramming,
        EraseRanges = GetSelectedEraseRanges(),
        Verify = Verify,
        ResetAfterProgramming = ResetAfterProgramming,
        RunAfterProgramming = RunAfterProgramming
    };

    public void ApplyPreferences(JLinkPreferences preferences)
    {
        _suppressPreferenceChanged = true;
        try
        {
            ExecutablePath = preferences.ExecutablePath ?? string.Empty;
            Device = string.IsNullOrWhiteSpace(preferences.Device) ? "STM32F103C8" : preferences.Device.Trim();
            AutoDetectTarget = preferences.AutoDetectTarget;
            InterfaceName = Interfaces.FirstOrDefault(item => string.Equals(
                item,
                preferences.InterfaceName,
                StringComparison.OrdinalIgnoreCase)) ?? "SWD";
            SpeedKHz = Math.Clamp(preferences.SpeedKHz, 1, 50_000);
            FirmwareFile = preferences.FirmwareFile ?? string.Empty;
            FlashAddressText = $"0x{preferences.FlashAddress:X8}";
            EraseBeforeProgramming = preferences.EraseBeforeProgramming;
            Verify = preferences.Verify;
            ResetAfterProgramming = preferences.ResetAfterProgramming;
            RunAfterProgramming = preferences.RunAfterProgramming;
            _savedEraseRanges = preferences.EraseRanges?
                .Where(IsValidEraseRange)
                .Select(item => item with { })
                .ToList() ?? [];
            IsConnected = false;
            DetectedDeviceText = "未读取目标信息";
            TargetCoreText = "-";
            FlashSizeText = "-";
            ReplaceFlashSectors([]);
        }
        finally
        {
            _suppressPreferenceChanged = false;
        }
    }

    public void SetFirmwareFile(string path) => FirmwareFile = path ?? string.Empty;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (IsConnecting || IsBusy || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        JLinkConnectionRequest request;
        try
        {
            request = CreateConnectionRequest();
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            StatusText = "参数无效";
            AddLog($"参数无效：{exception.Message}");
            return;
        }

        using CancellationTokenSource cancellation = BeginOperation("正在连接…");
        AddLog($"连接 J-Link：{request.Device} ({request.InterfaceName}, {request.SpeedKHz} kHz)");
        try
        {
            JLinkConnectionResult result = await _programmer
                .ConnectAsync(request, new Progress<string>(OnToolOutput), cancellation.Token)
                .ConfigureAwait(true);
            if (!result.Connected || result.TargetInfo is null)
            {
                IsConnected = false;
                StatusText = "连接失败";
                AddLog("J-Link 未建立目标连接。 ");
                return;
            }

            IsConnected = true;
            ApplyTargetInfo(result.TargetInfo);
            StatusText = "已连接";
            AddLog($"连接成功：{DetectedDeviceText}");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            IsConnected = false;
            StatusText = "连接已中止";
            AddLog("连接已中止。 ");
        }
        catch (Exception exception)
        {
            IsConnected = false;
            StatusText = "连接失败";
            AddLog($"连接失败：{exception.Message}");
        }
        finally
        {
            EndOperation(cancellation);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        if (IsBusy || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            await _programmer.DisconnectAsync().ConfigureAwait(true);
            IsConnected = false;
            StatusText = "未连接";
            AddLog("J-Link 已断开。 ");
        }
        catch (Exception exception)
        {
            StatusText = "断开失败";
            AddLog($"断开失败：{exception.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanReadTarget))]
    private async Task ReadTargetAsync()
    {
        if (!IsConnected || IsBusy || IsConnecting || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        using CancellationTokenSource cancellation = BeginOperation("正在读取目标信息…");
        AddLog("读取目标 ID、内核和 Flash 容量…");
        try
        {
            JLinkTargetInfo targetInfo = await _programmer
                .ReadTargetInfoAsync(new Progress<string>(OnToolOutput), cancellation.Token)
                .ConfigureAwait(true);
            ApplyTargetInfo(targetInfo);
            StatusText = "已连接";
            AddLog($"目标信息：{DetectedDeviceText}");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            StatusText = "读取已中止";
            AddLog("读取目标信息已中止。 ");
        }
        catch (Exception exception)
        {
            StatusText = "读取失败";
            AddLog($"读取目标信息失败：{exception.Message}");
        }
        finally
        {
            EndOperation(cancellation);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSelectSectors))]
    private void SelectAllSectors()
    {
        foreach (JLinkFlashSectorItemViewModel sector in FlashSectors)
        {
            sector.IsSelected = true;
        }
        NotifySectorSelectionChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSelectSectors))]
    private void ClearSectors()
    {
        foreach (JLinkFlashSectorItemViewModel sector in FlashSectors)
        {
            sector.IsSelected = false;
        }
        NotifySectorSelectionChanged();
    }

    [RelayCommand(CanExecute = nameof(CanProgram))]
    private async Task ProgramAsync()
    {
        if (IsBusy || IsConnecting || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        JLinkPreferences preferences;
        try
        {
            preferences = CreatePreferences();
            _ = ParseFlashAddress(FlashAddressText);
            if (string.IsNullOrWhiteSpace(preferences.FirmwareFile))
            {
                throw new ArgumentException("请选择固件文件。 ");
            }
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or ArgumentOutOfRangeException)
        {
            StatusText = "参数无效";
            AddLog($"参数无效：{exception.Message}");
            return;
        }

        if (EraseBeforeProgramming && FlashSectors.Count > 0 && SelectedSectorCount == 0)
        {
            StatusText = "未选择擦除扇区";
            AddLog("已启用烧录前擦除，请至少选择一个 Flash 扇区。 ");
            return;
        }

        using CancellationTokenSource cancellation = BeginOperation("正在烧录…");
        IsProgressIndeterminate = true;
        ProgressPercent = 0;
        ProgressText = "准备烧录";
        AddLog($"开始烧录：{preferences.FirmwareFile} → {preferences.Device} ({preferences.InterfaceName}, {preferences.SpeedKHz} kHz)");
        AddLog(preferences.EraseBeforeProgramming
            ? GetEraseDescription(preferences)
            : "操作：保留现有内容并写入。 ");

        Progress<string> progress = new(OnToolOutput);
        try
        {
            if (!IsConnected)
            {
            AddLog("尚未连接，先自动连接 J-Link…");
                JLinkConnectionResult connection = await _programmer
                    .ConnectAsync(CreateConnectionRequest(), progress, cancellation.Token)
                    .ConfigureAwait(true);
                if (!connection.Connected || connection.TargetInfo is null)
                {
                    throw new InvalidOperationException("自动连接 J-Link 失败。 ");
                }
                IsConnected = true;
                ApplyTargetInfo(connection.TargetInfo);
                AddLog($"自动连接成功：{DetectedDeviceText}");
                preferences = CreatePreferences();
            }

            JLinkProgrammingRequest request = new()
            {
                ExecutablePath = preferences.ExecutablePath,
                Device = preferences.Device,
                InterfaceName = preferences.InterfaceName,
                SpeedKHz = preferences.SpeedKHz,
                FirmwareFile = preferences.FirmwareFile,
                FlashAddress = preferences.FlashAddress,
                EraseBeforeProgramming = preferences.EraseBeforeProgramming,
                EraseRangeSelectionEnabled = FlashSectors.Count > 0,
                EraseRanges = (preferences.EraseRanges ?? [])
                    .Select(item => new JLinkEraseRange(item.StartAddress, item.EndAddressExclusive))
                    .ToList(),
                Verify = preferences.Verify,
                ResetAfterProgramming = preferences.ResetAfterProgramming,
                RunAfterProgramming = preferences.RunAfterProgramming
            };

            JLinkProgrammingResult result = await _programmer
                .ProgramAsync(request, progress, cancellation.Token)
                .ConfigureAwait(true);
            IsProgressIndeterminate = false;
            ProgressPercent = 100;
            ProgressText = "100%";
            if (result.ExitCode == 0)
            {
                StatusText = "烧录完成";
                AddLog($"烧录完成，用时 {result.Duration.TotalSeconds:F1} 秒。 ");
            }
            else
            {
                StatusText = $"烧录失败（退出码 {result.ExitCode}）";
                AddLog($"J-Link 返回退出码 {result.ExitCode}。 ");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            IsProgressIndeterminate = false;
            StatusText = "已中止";
            ProgressText = "已中止";
            AddLog("烧录已中止。 ");
        }
        catch (Exception exception)
        {
            IsProgressIndeterminate = false;
            StatusText = "烧录失败";
            ProgressText = "失败";
            AddLog($"烧录失败：{exception.Message}");
        }
        finally
        {
            EndOperation(cancellation);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (_operationCancellation is null)
        {
            return;
        }

        StatusText = "正在中止…";
        _operationCancellation.Cancel();
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogEntries.Clear();
        LogText = string.Empty;
    }

    public static uint ParseFlashAddress(string text)
    {
        string value = text?.Trim() ?? string.Empty;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        if (string.IsNullOrWhiteSpace(value)
            || !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint address))
        {
            throw new FormatException("烧录地址必须是十六进制，例如 0x08000000。 ");
        }

        return address;
    }

    private static uint ParseFlashAddressOrDefault(string text)
    {
        try
        {
            return ParseFlashAddress(text);
        }
        catch (FormatException)
        {
            return 0x0800_0000;
        }
    }

    private JLinkConnectionRequest CreateConnectionRequest() => new()
    {
        ExecutablePath = ExecutablePath.Trim(),
        Device = string.IsNullOrWhiteSpace(Device) ? "STM32F103C8" : Device.Trim(),
        AutoDetectTarget = AutoDetectTarget,
        InterfaceName = string.IsNullOrWhiteSpace(InterfaceName) ? "SWD" : InterfaceName.Trim(),
        SpeedKHz = Math.Clamp(SpeedKHz, 1, 50_000)
    };

    private List<JLinkEraseRangePreference> GetSelectedEraseRanges()
    {
        if (FlashSectors.Count == 0)
        {
            return _savedEraseRanges.Where(IsValidEraseRange).ToList();
        }

        return FlashSectors
            .Where(item => item.IsSelected)
            .Select(item => new JLinkEraseRangePreference
            {
                StartAddress = item.StartAddress,
                EndAddressExclusive = item.EndAddressExclusive,
                Selected = true
            })
            .ToList();
    }

    private void ApplyTargetInfo(JLinkTargetInfo targetInfo)
    {
        DetectedDeviceText = targetInfo.DetectedDevice;
        TargetCoreText = string.IsNullOrWhiteSpace(targetInfo.CoreName) ? "未读取" : targetInfo.CoreName;
        FlashSizeText = targetInfo.FlashSizeBytes is uint bytes
            ? $"{bytes / 1024} KB"
            : "未读取";
        ReplaceFlashSectors(targetInfo.FlashSectors);
    }

    private void ReplaceFlashSectors(IReadOnlyList<JLinkFlashSector> sectors)
    {
        foreach (JLinkFlashSectorItemViewModel item in FlashSectors)
        {
            item.SelectionChanged -= NotifySectorSelectionChanged;
        }
        FlashSectors.Clear();
        Dictionary<(uint Start, uint End), bool> savedSelections = _savedEraseRanges
            .Where(IsValidEraseRange)
            .ToDictionary(item => (item.StartAddress, item.EndAddressExclusive), item => item.Selected);
        foreach (JLinkFlashSector sector in sectors)
        {
            bool selected = savedSelections.Count == 0
                || !savedSelections.TryGetValue((sector.StartAddress, sector.EndAddressExclusive), out bool value)
                || value;
            JLinkFlashSectorItemViewModel item = new(sector, selected);
            item.SelectionChanged += NotifySectorSelectionChanged;
            FlashSectors.Add(item);
        }
        NotifySectorSelectionChanged();
    }

    private CancellationTokenSource BeginOperation(string status)
    {
        CancellationTokenSource cancellation = new();
        _operationCancellation = cancellation;
        _operationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IsBusy = true;
        IsConnecting = status.Contains("连接", StringComparison.Ordinal);
        StatusText = status;
        return cancellation;
    }

    private void EndOperation(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_operationCancellation, cancellation))
        {
            _operationCancellation = null;
        }
        TaskCompletionSource<bool>? completion = _operationCompletion;
        _operationCompletion = null;
        completion?.TrySetResult(true);
        IsBusy = false;
        IsConnecting = false;
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        ReadTargetCommand.NotifyCanExecuteChanged();
    }

    private bool CanConnect() => !IsBusy && !IsConnecting && !IsConnected;
    private bool CanDisconnect() => !IsBusy && !IsConnecting && IsConnected;
    private bool CanReadTarget() => !IsBusy && !IsConnecting && IsConnected;
    private bool CanSelectSectors() => !IsBusy && FlashSectors.Count > 0;
    private bool CanProgram() => !IsBusy && !IsConnecting && !string.IsNullOrWhiteSpace(FirmwareFile);
    private bool CanCancel() => IsBusy;

    private string GetEraseDescription(JLinkPreferences preferences)
    {
        int count = preferences.EraseRanges?.Count ?? 0;
        if (FlashSectors.Count > 0 && count == 0)
        {
            return "操作：未选择擦除扇区，无法开始烧录。 ";
        }
        return count == 0
            ? "操作：整片擦除后写入。"
            : $"操作：仅擦除已选择的 {count} 个 Flash 扇区后写入。 ";
    }

    private void OnToolOutput(string line)
    {
        AddLog(line);
        int percentIndex = line.IndexOf('%');
        if (percentIndex <= 0)
        {
            return;
        }

        int start = percentIndex - 1;
        while (start >= 0 && char.IsDigit(line[start]))
        {
            start--;
        }

        if (int.TryParse(line[(start + 1)..percentIndex], CultureInfo.InvariantCulture, out int percent))
        {
            ProgressPercent = Math.Clamp(percent, 0, 100);
            ProgressText = $"{ProgressPercent}%";
            IsProgressIndeterminate = false;
        }
    }

    private void AddLog(string message)
    {
        LogEntries.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (LogEntries.Count > 5000)
        {
            LogEntries.RemoveAt(0);
        }

        LogText = string.Join(Environment.NewLine, LogEntries);
    }

    private void NotifySectorSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedSectorCount));
        OnPropertyChanged(nameof(SectorSelectionText));
        NotifyPreferencesChanged();
    }

    private static bool IsValidEraseRange(JLinkEraseRangePreference range) =>
        range.EndAddressExclusive > range.StartAddress;

    partial void OnExecutablePathChanged(string value) => NotifyPreferencesChanged();
    partial void OnDeviceChanged(string value) => NotifyPreferencesChanged();
    partial void OnAutoDetectTargetChanged(bool value) => NotifyPreferencesChanged();
    partial void OnInterfaceNameChanged(string value) => NotifyPreferencesChanged();
    partial void OnSpeedKHzChanged(int value) => NotifyPreferencesChanged();
    partial void OnFirmwareFileChanged(string value)
    {
        NotifyPreferencesChanged();
        ProgramCommand.NotifyCanExecuteChanged();
    }
    partial void OnFlashAddressTextChanged(string value) => NotifyPreferencesChanged();
    partial void OnEraseBeforeProgrammingChanged(bool value) => NotifyPreferencesChanged();
    partial void OnVerifyChanged(bool value) => NotifyPreferencesChanged();
    partial void OnResetAfterProgrammingChanged(bool value) => NotifyPreferencesChanged();
    partial void OnRunAfterProgrammingChanged(bool value) => NotifyPreferencesChanged();

    partial void OnIsBusyChanged(bool value)
    {
        ProgramCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        ReadTargetCommand.NotifyCanExecuteChanged();
        SelectAllSectorsCommand.NotifyCanExecuteChanged();
        ClearSectorsCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsConnectingChanged(bool value)
    {
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        ReadTargetCommand.NotifyCanExecuteChanged();
        ProgramCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsConnectedChanged(bool value)
    {
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        ReadTargetCommand.NotifyCanExecuteChanged();
    }

    private void NotifyPreferencesChanged()
    {
        if (!_suppressPreferenceChanged)
        {
            PreferencesChanged?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _operationCancellation?.Cancel();
        }

        TaskCompletionSource<bool>? completion = _operationCompletion;
        if (completion is not null)
        {
            try
            {
                await completion.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
            }
        }

        await _programmer.DisposeAsync().ConfigureAwait(true);
    }
}
