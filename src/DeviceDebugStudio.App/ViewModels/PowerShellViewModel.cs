using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace DeviceDebugStudio.App.ViewModels;

public enum PowerShellControlKey
{
    Cancel,
    Break,
    EndOfFile,
    Suspend
}

/// <summary>
/// 管理应用内 PowerShell 会话，保持一个进程供多条命令连续执行。
/// </summary>
public partial class PowerShellViewModel : ObservableObject, IAsyncDisposable
{
    private const string CommandDoneMarker = "__DEVICE_DEBUG_STUDIO_COMMAND_DONE__";
    private const string ExitMarker = "__DEVICE_DEBUG_STUDIO_EXIT__";
    private const string ControlInputPrefix = "__DEVICE_DEBUG_STUDIO_CONTROL__:";
    private const int MaximumOutputCharacters = 500_000;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly Regex AnsiEscapeRegex = new(
        "\\u001B(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\u0007]*(?:\\u0007|\\u001B\\\\)|[()][0-2A-Z])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string RunnerScript =
        """
        $OutputEncoding = New-Object System.Text.UTF8Encoding($false)
        [Console]::OutputEncoding = $OutputEncoding
        $ErrorActionPreference = 'Continue'
        $env:TERM = 'xterm-256color'
        $commandDoneMarker = '__DEVICE_DEBUG_STUDIO_COMMAND_DONE__'
        $exitMarker = '__DEVICE_DEBUG_STUDIO_EXIT__'
        $controlInputPrefix = '__DEVICE_DEBUG_STUDIO_CONTROL__:'
        $script:pendingLines = [System.Collections.Generic.Queue[string]]::new()
        $script:reader = $null
        $script:readResult = $null
        $runspace = [runspacefactory]::CreateRunspace()
        $runspace.Open()
        $pipeline = $null
        $inputClosed = $false

        function Start-InputRead {
            if ($null -ne $script:reader) {
                $script:reader.Dispose()
            }
            $script:reader = [powershell]::Create()
            $null = $script:reader.AddScript({ [Console]::In.ReadLine() })
            $script:readResult = $script:reader.BeginInvoke()
        }

        Start-InputRead

        function New-CommandPipeline {
            $newPipeline = [powershell]::Create()
            $newPipeline.Runspace = $runspace
            return $newPipeline
        }

        function Get-NextInputLine {
            if ($script:pendingLines.Count -gt 0) {
                return $script:pendingLines.Dequeue()
            }

            while (-not $script:readResult.IsCompleted) {
                Start-Sleep -Milliseconds 15
            }

            $line = $script:reader.EndInvoke($script:readResult)
            if ($null -ne $line) {
                Start-InputRead
            }
            return $line
        }

        function Write-FormattedValue([object] $value) {
            foreach ($formattedLine in @($value | Out-String -Stream)) {
                [Console]::Out.WriteLine([string]$formattedLine)
            }
        }

        function Write-ControlEcho([int] $controlCode) {
            $echo = switch ($controlCode) {
                3 { '^C'; break }
                24 { '^X'; break }
                26 { '^Z'; break }
                28 { '^Break'; break }
                default { $null }
            }
            if ($null -ne $echo) {
                [Console]::Out.WriteLine($echo)
            }
        }

        try {
            $pipeline = New-CommandPipeline
            while (-not $inputClosed) {
                $line = Get-NextInputLine
                if ($null -eq $line -or $line -eq $exitMarker) {
                    break
                }

                if ($line.StartsWith($controlInputPrefix, [StringComparison]::Ordinal)) {
                    $controlCode = 0
                    $controlParsed = [int]::TryParse($line.Substring($controlInputPrefix.Length), [ref]$controlCode)
                    if ($controlParsed -and $controlCode -eq 4) {
                        break
                    }

                    if ($controlParsed) {
                        Write-ControlEcho $controlCode
                    }
                    [Console]::Out.WriteLine($commandDoneMarker)
                    continue
                }

                $interrupted = $false
                $pipeline.Commands.Clear()
                $pipeline.Streams.Error.Clear()
                $inputCollection = [System.Management.Automation.PSDataCollection[System.Management.Automation.PSObject]]::new()
                $outputCollection = [System.Management.Automation.PSDataCollection[System.Management.Automation.PSObject]]::new()
                $null = $pipeline.AddScript($line)
                $asyncResult = $pipeline.BeginInvoke($inputCollection, $outputCollection)
                $inputCollection.Complete()
                $outputCursor = 0

                while (-not $asyncResult.IsCompleted -or $outputCursor -lt $outputCollection.Count) {
                    while ($outputCursor -lt $outputCollection.Count) {
                        Write-FormattedValue $outputCollection[$outputCursor]
                        $outputCursor++
                    }

                    if ($script:readResult.IsCompleted) {
                        $inputLine = Get-NextInputLine
                        if ($null -eq $inputLine -or $inputLine -eq $exitMarker) {
                            $inputClosed = $true
                            $pipeline.Stop()
                            break
                        }

                        if ($inputLine.StartsWith($controlInputPrefix, [StringComparison]::Ordinal)) {
                            $controlCode = 0
                            $controlParsed = [int]::TryParse($inputLine.Substring($controlInputPrefix.Length), [ref]$controlCode)
                            if ($controlParsed -and $controlCode -in @(3, 24, 26, 28)) {
                                $interrupted = $true
                                Write-ControlEcho $controlCode
                                $pipeline.Stop()
                            }
                        }
                        else {
                            $script:pendingLines.Enqueue($inputLine)
                        }
                    }

                    if (-not $asyncResult.IsCompleted -or $outputCursor -lt $outputCollection.Count) {
                        Start-Sleep -Milliseconds 15
                    }
                }

                try {
                    $null = $pipeline.EndInvoke($asyncResult)
                }
                catch [System.Management.Automation.PipelineStoppedException] {
                    $interrupted = $true
                }
                catch {
                    Write-FormattedValue $_
                }

                foreach ($errorRecord in @($pipeline.Streams.Error)) {
                    Write-FormattedValue $errorRecord
                }

                if ($interrupted) {
                    $pipeline.Dispose()
                    $pipeline = New-CommandPipeline
                }

                $pipeline.Commands.Clear()
                $pipeline.Streams.Error.Clear()
                if (-not $inputClosed) {
                    [Console]::Out.WriteLine($commandDoneMarker)
                }
            }
        }
        finally {
            if ($null -ne $pipeline) {
                $pipeline.Dispose()
            }
            if ($null -ne $script:reader) {
                $script:reader.Dispose()
            }
            $runspace.Close()
            $runspace.Dispose()
        }
        """;

    private readonly object _processSync = new();
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly SemaphoreSlim _inputWriteLock = new(1, 1);
    private readonly StringBuilder _outputBuilder = new();
    private CancellationTokenSource? _readCancellation;
    private Task? _outputTask;
    private TaskCompletionSource<bool>? _commandCompletion;
    private Process? _process;
    private ConPtySession? _conPtySession;
    private int _starting;
    private int _disposed;
    private int _historyIndex;

    public PowerShellViewModel()
    {
        workingDirectory = Environment.CurrentDirectory;
        _historyIndex = 0;
    }

    public ObservableCollection<string> History { get; } = [];

    [ObservableProperty]
    private string outputText = string.Empty;

    [ObservableProperty]
    private string commandText = string.Empty;

    [ObservableProperty]
    private string workingDirectory;

    [ObservableProperty]
    private string statusText = "未启动";

    [ObservableProperty]
    private bool isRunning;

    [ObservableProperty]
    private bool isBusy;

    public Task EnsureStartedAsync() => StartProcessAsync(restart: false);

    public string NavigateHistory(int direction)
    {
        if (History.Count == 0)
        {
            return CommandText;
        }

        _historyIndex = Math.Clamp(_historyIndex + direction, 0, History.Count);
        CommandText = _historyIndex < History.Count ? History[_historyIndex] : string.Empty;
        return CommandText;
    }

    [RelayCommand]
    private Task StartAsync() => StartProcessAsync(restart: false);

    [RelayCommand]
    private Task RestartAsync() => StartProcessAsync(restart: true);

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => StopProcessAsync();

    [RelayCommand]
    private void Clear()
    {
        _outputBuilder.Clear();
        OutputText = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanExecuteCommand))]
    private async Task ExecuteCommandAsync()
    {
        string command = CommandText.Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        await EnsureStartedAsync().ConfigureAwait(true);
        Process? process = GetProcess();
        if (process is null || process.HasExited)
        {
            StatusText = "PowerShell 未启动";
            return;
        }

        await _commandLock.WaitAsync().ConfigureAwait(true);
        try
        {
            process = GetProcess();
            if (process is null || process.HasExited)
            {
                StatusText = "PowerShell 未启动";
                return;
            }

            AddHistory(command);
            CommandText = string.Empty;
            AppendOutputLine("TX", command);
            StatusText = "执行中…";
            IsBusy = true;

            TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_processSync)
            {
                _commandCompletion = completion;
            }

            try
            {
                // 普通命令和控制信号共用标准输入，必须串行写入，避免并发写破坏协议行。
                await WriteInputLineAsync(process, command).ConfigureAwait(true);
                await completion.Task.WaitAsync(TimeSpan.FromMinutes(30)).ConfigureAwait(true);
                StatusText = "就绪";
            }
            catch (TimeoutException)
            {
                AppendOutputLine("ERR", "命令执行超时，仍保留 PowerShell 会话。");
                StatusText = "命令超时";
            }
            catch (OperationCanceledException)
            {
                StatusText = "正在停止";
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "PowerShell 命令执行失败");
                AppendOutputLine("ERR", $"执行失败：{exception.Message}");
                StatusText = "执行失败";
            }
            finally
            {
                lock (_processSync)
                {
                    if (ReferenceEquals(_commandCompletion, completion))
                    {
                        _commandCompletion = null;
                    }
                }
            }
        }
        finally
        {
            IsBusy = false;
            _commandLock.Release();
            ExecuteCommandCommand.NotifyCanExecuteChanged();
        }
    }

    public Task SendControlAsync(PowerShellControlKey controlKey)
    {
        int controlCode = controlKey switch
        {
            PowerShellControlKey.Cancel => 3,
            PowerShellControlKey.Break => 28,
            PowerShellControlKey.EndOfFile => 4,
            PowerShellControlKey.Suspend => 26,
            _ => throw new ArgumentOutOfRangeException(nameof(controlKey), controlKey, null)
        };

        return SendControlCodeAsync(controlCode);
    }

    private async Task SendControlCodeAsync(int controlCode)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Process? process = GetProcess();
        if (process is null || process.HasExited)
        {
            return;
        }

        try
        {
            await RunOnUiAsync(() => AppendOutputLine("TX", GetControlDisplayText(controlCode))).ConfigureAwait(false);
            if (controlCode == 3 && IsBusy && GetConPtySession() is { } conPtySession)
            {
                // 忙碌时发送真实控制字符，让 ConPTY 把 Ctrl+C 传给当前前台程序。
                await WriteInputBytesAsync(conPtySession, new byte[] { 0x03 }).ConfigureAwait(true);
            }
            else
            {
                await WriteInputLineAsync(process, $"{ControlInputPrefix}{controlCode}").ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            Log.Debug(exception, "PowerShell 控制信号发送失败");
        }
    }

    private async Task WriteInputLineAsync(Process process, string line, ConPtySession? sessionOverride = null)
    {
        await _inputWriteLock.WaitAsync().ConfigureAwait(true);
        try
        {
            ConPtySession? session = sessionOverride ?? GetConPtySession();
            if (session is not null)
            {
                await WriteInputBytesCoreAsync(session, Utf8NoBom.GetBytes(line + "\r")).ConfigureAwait(true);
                return;
            }

            await process.StandardInput.WriteLineAsync(line).ConfigureAwait(true);
            await process.StandardInput.FlushAsync().ConfigureAwait(true);
        }
        finally
        {
            _inputWriteLock.Release();
        }
    }

    private async Task WriteInputBytesAsync(ConPtySession session, ReadOnlyMemory<byte> bytes)
    {
        // 所有输入都经过同一把锁，避免命令文本和 Ctrl+C 字节交叉写入伪终端。
        await _inputWriteLock.WaitAsync().ConfigureAwait(true);
        try
        {
            await WriteInputBytesCoreAsync(session, bytes).ConfigureAwait(true);
        }
        finally
        {
            _inputWriteLock.Release();
        }
    }

    private static Task WriteInputBytesCoreAsync(ConPtySession session, ReadOnlyMemory<byte> bytes)
    {
        // ConPTY 使用同步匿名管道，直接同步写入可以避免非 overlapped 句柄上的异步写入失效。
        session.Input.Write(bytes.Span);
        session.Input.Flush();
        return Task.CompletedTask;
    }

    private bool CanStop() => IsRunning;

    private bool CanExecuteCommand() => !IsBusy && Volatile.Read(ref _disposed) == 0;

    private async Task StartProcessAsync(bool restart)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (restart)
        {
            await StopProcessAsync().ConfigureAwait(true);
        }

        if (GetProcess() is { HasExited: false } || Interlocked.Exchange(ref _starting, 1) != 0)
        {
            return;
        }

        try
        {
            string executable = ResolvePowerShellExecutable();
            string directory = ResolveWorkingDirectory();
            List<string> arguments =
            [
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                RunnerScript
            ];
            ConPtySession conPtySession = ConPtySession.Start(executable, arguments, directory);
            Process process = Process.GetProcessById(conPtySession.ProcessId);

            CancellationTokenSource readCancellation = new();
            Task outputTask = ReadOutputAsync(conPtySession, readCancellation.Token);
            lock (_processSync)
            {
                _process = process;
                _conPtySession = conPtySession;
                _readCancellation = readCancellation;
                _outputTask = outputTask;
            }

            IsRunning = true;
            StatusText = "就绪";
            Clear();
            AppendOutputLine("INFO", "Windows PowerShell 已启动");
            AppendOutputLine("INFO", $"工作目录：{directory}");
            AppendPrompt();
            _ = ObserveProcessAsync(process, conPtySession, readCancellation, outputTask);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "启动 PowerShell 失败");
            StatusText = $"启动失败：{exception.Message}";
            IsRunning = false;
        }
        finally
        {
            Volatile.Write(ref _starting, 0);
            StopCommand.NotifyCanExecuteChanged();
            ExecuteCommandCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task StopProcessAsync()
    {
        Process? process;
        ConPtySession? conPtySession;
        CancellationTokenSource? cancellation;
        TaskCompletionSource<bool>? completion;
        lock (_processSync)
        {
            process = _process;
            conPtySession = _conPtySession;
            cancellation = _readCancellation;
            completion = _commandCompletion;
            _process = null;
            _conPtySession = null;
            _readCancellation = null;
            _outputTask = null;
            _commandCompletion = null;
        }

        completion?.TrySetCanceled();
        cancellation?.Cancel();
        if (process is null)
        {
            IsRunning = false;
            StatusText = "已停止";
            StopCommand.NotifyCanExecuteChanged();
            return;
        }

        try
        {
            if (conPtySession is not null)
            {
                if (!conPtySession.HasExited)
                {
                    // 停止按钮同样由 UI 线程触发，保持停止后的属性和命令状态更新在 UI 线程。
                    await WriteInputLineAsync(process, ExitMarker, conPtySession).ConfigureAwait(true);
                    await conPtySession.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(800)).ConfigureAwait(true);
                }

                if (!conPtySession.HasExited)
                {
                    conPtySession.Kill();
                }
            }
            else if (!process.HasExited)
            {
                await WriteInputLineAsync(process, ExitMarker).ConfigureAwait(true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(800)).ConfigureAwait(true);
            }
        }
        catch
        {
            try
            {
                if (conPtySession is not null)
                {
                    conPtySession.Kill();
                }
                else if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // 进程可能已经在退出流程中，不再重复报告停止错误。
            }
        }
        finally
        {
            process.Dispose();
            if (conPtySession is not null)
            {
                await conPtySession.DisposeAsync().ConfigureAwait(true);
            }
            cancellation?.Dispose();
            IsRunning = false;
            IsBusy = false;
            StatusText = "已停止";
            StopCommand.NotifyCanExecuteChanged();
            ExecuteCommandCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task ObserveProcessAsync(
        Process process,
        ConPtySession conPtySession,
        CancellationTokenSource cancellation,
        Task outputTask)
    {
        bool isCurrent = false;
        try
        {
            await outputTask.ConfigureAwait(false);
            await conPtySession.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException
                or ObjectDisposedException
                or IOException
                or InvalidOperationException)
        {
        }
        finally
        {
            lock (_processSync)
            {
                isCurrent = ReferenceEquals(_process, process);
                if (isCurrent)
                {
                    _process = null;
                    _conPtySession = null;
                    _readCancellation = null;
                    _outputTask = null;
                    _commandCompletion?.TrySetCanceled();
                    _commandCompletion = null;
                }
            }

            if (isCurrent)
            {
                await RunOnUiAsync(() =>
                {
                    IsRunning = false;
                    IsBusy = false;
                    StatusText = "已退出";
                    AppendOutputLine("INFO", "PowerShell 会话已退出。");
                    StopCommand.NotifyCanExecuteChanged();
                    ExecuteCommandCommand.NotifyCanExecuteChanged();
                }).ConfigureAwait(false);

                process.Dispose();
                await conPtySession.DisposeAsync().ConfigureAwait(false);
                cancellation.Dispose();
            }
        }
    }

    private async Task ReadOutputAsync(ConPtySession session, CancellationToken cancellationToken)
    {
        try
        {
            using StreamReader reader = new(session.Output, Utf8NoBom, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            char[] readBuffer = new char[4096];
            StringBuilder protocolBuffer = new();
            while (true)
            {
                int readCount = await reader.ReadAsync(readBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (readCount == 0)
                {
                    break;
                }

                protocolBuffer.Append(readBuffer, 0, readCount);
                await FlushTerminalBufferAsync(protocolBuffer, flushAll: false).ConfigureAwait(false);
            }

            await FlushTerminalBufferAsync(protocolBuffer, flushAll: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException
                or ObjectDisposedException
                or IOException
                or InvalidOperationException)
        {
        }
    }

    private async Task FlushTerminalBufferAsync(StringBuilder protocolBuffer, bool flushAll)
    {
        while (protocolBuffer.Length > 0)
        {
            string snapshot = protocolBuffer.ToString();
            int markerIndex = snapshot.IndexOf(CommandDoneMarker, StringComparison.Ordinal);
            if (markerIndex >= 0)
            {
                await EmitTerminalTextAsync(snapshot[..markerIndex]).ConfigureAwait(false);
                protocolBuffer.Remove(0, markerIndex + CommandDoneMarker.Length);
                lock (_processSync)
                {
                    _commandCompletion?.TrySetResult(true);
                }

                await RunOnUiAsync(AppendPrompt).ConfigureAwait(false);
                continue;
            }

            int keepLength = flushAll ? 0 : GetMarkerPrefixSuffixLength(snapshot);
            int emitLength = snapshot.Length - keepLength;
            if (emitLength <= 0)
            {
                return;
            }

            await EmitTerminalTextAsync(snapshot[..emitLength]).ConfigureAwait(false);
            protocolBuffer.Remove(0, emitLength);
            if (!flushAll)
            {
                return;
            }
        }
    }

    private static int GetMarkerPrefixSuffixLength(string text)
    {
        int maximumLength = Math.Min(CommandDoneMarker.Length - 1, text.Length);
        for (int length = maximumLength; length > 0; length--)
        {
            if (text.AsSpan(text.Length - length, length)
                .SequenceEqual(CommandDoneMarker.AsSpan(0, length)))
            {
                return length;
            }
        }

        return 0;
    }

    private async Task EmitTerminalTextAsync(string text)
    {
        string cleanedText = CleanTerminalText(text);
        if (cleanedText.Length == 0)
        {
            return;
        }

        cleanedText = cleanedText.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        foreach (string line in cleanedText.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            await RunOnUiAsync(() => AppendOutputLine("RX", line)).ConfigureAwait(false);
        }
    }

    private static string CleanTerminalText(string text)
    {
        string withoutAnsi = AnsiEscapeRegex.Replace(text, string.Empty);
        StringBuilder cleaned = new(withoutAnsi.Length);
        foreach (char character in withoutAnsi)
        {
            if (character == '\b' || (char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            {
                continue;
            }

            cleaned.Append(character);
        }

        return cleaned.ToString();
    }

    private async Task RunOnUiAsync(Action action)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        await dispatcher.InvokeAsync(action);
    }

    private void AppendOutput(string text)
    {
        _outputBuilder.Append(text);
        if (_outputBuilder.Length > MaximumOutputCharacters)
        {
            int removeCount = _outputBuilder.Length - MaximumOutputCharacters;
            _outputBuilder.Remove(0, removeCount);
        }

        OutputText = _outputBuilder.ToString();
    }

    private void AppendOutputLine(string direction, string text)
    {
        AppendOutput($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {direction} {text}{Environment.NewLine}");
    }

    private void AppendPrompt() => AppendOutputLine("INFO", $"PS {WorkingDirectory}>");

    private static string GetControlDisplayText(int controlCode) => controlCode switch
    {
        3 => "Ctrl+C",
        24 => "Ctrl+X",
        26 => "Ctrl+Z",
        28 => "Ctrl+Break",
        4 => "Ctrl+D",
        _ => $"控制码 {controlCode}"
    };

    private void AddHistory(string command)
    {
        if (History.Count == 0 || !string.Equals(History[^1], command, StringComparison.Ordinal))
        {
            History.Add(command);
        }

        _historyIndex = History.Count;
    }

    private Process? GetProcess()
    {
        lock (_processSync)
        {
            return _process;
        }
    }

    private ConPtySession? GetConPtySession()
    {
        lock (_processSync)
        {
            return _conPtySession;
        }
    }

    private string ResolveWorkingDirectory()
    {
        try
        {
            if (Directory.Exists(WorkingDirectory))
            {
                return Path.GetFullPath(WorkingDirectory);
            }
        }
        catch (ArgumentException)
        {
        }

        return Environment.CurrentDirectory;
    }

    private static string ResolvePowerShellExecutable()
    {
        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string windowsPowerShell = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(windowsPowerShell) ? windowsPowerShell : "pwsh.exe";
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopProcessAsync().ConfigureAwait(true);
        _commandLock.Dispose();
        _inputWriteLock.Dispose();
    }

    partial void OnIsRunningChanged(bool value)
    {
        StopCommand.NotifyCanExecuteChanged();
        ExecuteCommandCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value) => ExecuteCommandCommand.NotifyCanExecuteChanged();
}
