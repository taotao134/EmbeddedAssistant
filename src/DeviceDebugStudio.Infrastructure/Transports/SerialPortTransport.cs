using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.IO.Ports;
using System.Text;
using DeviceDebugStudio.Core.Transports;
using DeviceDebugStudio.Infrastructure.Persistence;

namespace DeviceDebugStudio.Infrastructure.Transports;

public sealed class SerialPortTransport(SerialTransportSettings settings) : TransportBase
{
    private const string WorkerSwitch = "--device-debug-studio-serial-worker";
    private const string AccessDeniedWorkerErrorPrefix = "ACCESS_DENIED:";
    private const int OpenTimeoutMilliseconds = 3000;
    private const int AccessDeniedOpenRetryCount = 3;
    private const int AccessDeniedOpenRetryDelayMilliseconds = 300;
    private const int WorkerStartupTimeoutMilliseconds = 5000;
    private const int WorkerShutdownTimeoutMilliseconds = 1000;
    private const int MaximumFrameLength = 16 * 1024 * 1024;
    private const string DebugLogMutexName = @"Local\DeviceDebugStudio.SerialPortDebugLog";
    private const byte WorkerReadyMessage = 1;
    private const byte PortOpenedMessage = 2;
    private const byte ReceivedDataMessage = 3;
    private const byte WorkerErrorMessage = 4;
    private const byte WorkerHeartbeatMessage = 5;
    private const byte SendDataCommand = 1;
    private const byte ClosePortCommand = 2;
    private const int EventWriteTimeoutMilliseconds = 2000;
    private const int WorkerHeartbeatIntervalMilliseconds = 1000;
    private const int WorkerHeartbeatTimeoutMilliseconds = 3000;
    private const int WorkerReadActivityTimeoutMilliseconds = 3000;
    private const int WorkerHeartbeatSendFailureLimit = 3;
    private const int MaxTransientReadRetryCount = 5;
    private const int ReadLoopYieldDelayMilliseconds = 1;
    private const int DebugLogMutexTimeoutMilliseconds = 100;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _workerCleanupLock = new(1, 1);
    private readonly object _workerSync = new();
    private Process? _workerProcess;
    private NamedPipeServerStream? _commandPipe;
    private NamedPipeServerStream? _eventPipe;
    private CancellationTokenSource? _readCancellation;
    private Task? _readTask;
    private string? _diagnosticAttemptId;
    private int _disconnectInProgress;
    private long _lastWorkerHeartbeatUnixMs;
    private long _lastWorkerReadActivityUnixMs;
    private long _workerReceivedBytes;
    private long _workerRetryCount;
    private long _workerTimeoutStreak;

    public override string DisplayName => string.IsNullOrWhiteSpace(settings.PortName) ? "串口" : settings.PortName;
    public override TransportKind Kind => TransportKind.Serial;

    public long WorkerReceivedBytes => Interlocked.Read(ref _workerReceivedBytes);
    public long WorkerRetryCount => Interlocked.Read(ref _workerRetryCount);
    public long WorkerTimeoutStreak => Interlocked.Read(ref _workerTimeoutStreak);

    protected override async Task OnConnectAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.PortName))
        {
            throw new InvalidOperationException("请选择串口。");
        }

        if (settings.BaudRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings.BaudRate), "波特率必须是正整数。");
        }

        string attemptId = Guid.NewGuid().ToString("N")[..12];
        _diagnosticAttemptId = attemptId;
        Interlocked.Exchange(ref _lastWorkerHeartbeatUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Interlocked.Exchange(ref _lastWorkerReadActivityUnixMs, 0);
        Interlocked.Exchange(ref _workerReceivedBytes, 0);
        Interlocked.Exchange(ref _workerRetryCount, 0);
        Interlocked.Exchange(ref _workerTimeoutStreak, 0);
        WriteDebugLog(
            attemptId,
            $"父进程开始连接：端口={settings.PortName}，波特率={settings.BaudRate}，数据位={settings.DataBits}，"
                + $"校验={settings.Parity}，停止位={settings.StopBits}，流控={settings.Handshake}，"
                + $"DTR={settings.DtrEnable}，RTS={settings.RtsEnable}，父进程={Environment.ProcessId}。");

        string commandPipeName = $"DeviceDebugStudio.Serial.Command.{attemptId}";
        string eventPipeName = $"DeviceDebugStudio.Serial.Event.{Guid.NewGuid():N}";
        NamedPipeServerStream commandPipe = new(
            commandPipeName,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        NamedPipeServerStream eventPipe = new(
            eventPipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        Process workerProcess = new()
        {
            StartInfo = CreateWorkerStartInfo(commandPipeName, eventPipeName),
            EnableRaisingEvents = true
        };

        lock (_workerSync)
        {
            if (_workerProcess is not null)
            {
                WriteDebugLog(attemptId, "父进程拒绝连接：当前传输实例仍保留工作进程引用。");
                commandPipe.Dispose();
                eventPipe.Dispose();
                workerProcess.Dispose();
                throw new InvalidOperationException("串口正在打开或已经初始化。");
            }

            _workerProcess = workerProcess;
            _commandPipe = commandPipe;
            _eventPipe = eventPipe;
        }

        try
        {
            if (!workerProcess.Start())
            {
                throw new IOException("无法启动串口工作进程。");
            }
            WriteDebugLog(attemptId, $"父进程已启动工作进程：PID={workerProcess.Id}，路径={workerProcess.StartInfo.FileName}。");
            workerProcess.Exited += (_, _) => OnWorkerProcessExited(attemptId, workerProcess);

            WriteDebugLog(attemptId, "父进程等待命令管道和事件管道连接。");
            Task connectPipesTask = Task.WhenAll(
                commandPipe.WaitForConnectionAsync(cancellationToken),
                eventPipe.WaitForConnectionAsync(cancellationToken));
            await connectPipesTask
                .WaitAsync(TimeSpan.FromMilliseconds(WorkerStartupTimeoutMilliseconds), cancellationToken)
                .ConfigureAwait(false);
            WriteDebugLog(attemptId, "父进程管道连接完成，等待工作进程就绪消息。");

            WorkerFrame readyFrame = await ReadFrameAsync(eventPipe, cancellationToken)
                .WaitAsync(TimeSpan.FromMilliseconds(WorkerStartupTimeoutMilliseconds), cancellationToken)
                .ConfigureAwait(false);
            WriteDebugLog(attemptId, $"父进程收到工作进程首帧：类型={readyFrame.Kind}，长度={readyFrame.Payload.Length}。");
            if (readyFrame.Kind != WorkerReadyMessage)
            {
                throw CreateWorkerException(readyFrame, "串口工作进程启动协议异常。");
            }

            WorkerFrame openedFrame;
            try
            {
                WriteDebugLog(attemptId, $"父进程等待串口打开结果，超时={OpenTimeoutMilliseconds} ms。");
                openedFrame = await ReadFrameAsync(eventPipe, cancellationToken)
                    .WaitAsync(TimeSpan.FromMilliseconds(OpenTimeoutMilliseconds), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                WriteDebugLog(attemptId, "父进程等待串口打开结果超时。", exception);
                throw new TimeoutException(
                    BuildOpenFailureMessage($"超过 {OpenTimeoutMilliseconds} ms 未完成，已强制终止本次打开"),
                    exception);
            }
            WriteDebugLog(
                attemptId,
                $"父进程收到串口打开结果：类型={openedFrame.Kind}，长度={openedFrame.Payload.Length}，"
                    + $"消息={DecodeMessageForLog(openedFrame.Payload)}。");

            if (openedFrame.Kind != PortOpenedMessage)
            {
                throw CreateWorkerException(openedFrame, "驱动未能将串口置为打开状态。");
            }

            CancellationTokenSource readCancellation = new();
            _readCancellation = readCancellation;
            _readTask = Task.Run(
                () => ReadWorkerEventsAsync(eventPipe, readCancellation.Token),
                CancellationToken.None);
            _ = Task.Run(
                () => WatchWorkerActivityAsync(attemptId, readCancellation.Token),
                CancellationToken.None);
            WriteDebugLog(attemptId, $"父进程确认连接成功：工作进程 PID={workerProcess.Id}。");
        }
        catch (OperationCanceledException exception)
        {
            WriteDebugLog(attemptId, "父进程连接操作被取消，开始终止工作进程。", exception);
            await TerminateWorkerAsync(attemptId, workerProcess, commandPipe, eventPipe).ConfigureAwait(false);
            ClearWorker(workerProcess);
            _diagnosticAttemptId = null;
            throw;
        }
        catch (TimeoutException exception)
        {
            WriteDebugLog(attemptId, "父进程连接超时，开始终止工作进程。", exception);
            await TerminateWorkerAsync(attemptId, workerProcess, commandPipe, eventPipe).ConfigureAwait(false);
            ClearWorker(workerProcess);
            _diagnosticAttemptId = null;
            throw;
        }
        catch (Exception exception)
        {
            WriteDebugLog(attemptId, "父进程连接失败，开始终止工作进程。", exception);
            await TerminateWorkerAsync(attemptId, workerProcess, commandPipe, eventPipe).ConfigureAwait(false);
            ClearWorker(workerProcess);
            _diagnosticAttemptId = null;
            throw new IOException(BuildOpenFailureMessage(exception.Message), exception);
        }
    }

    protected override async Task OnDisconnectAsync(CancellationToken cancellationToken)
    {
        string attemptId = _diagnosticAttemptId ?? "无连接编号";
        WriteDebugLog(attemptId, "父进程开始断开串口连接。");
        Interlocked.Exchange(ref _disconnectInProgress, 1);
        try
        {
            Process? workerProcess;
            NamedPipeServerStream? commandPipe;
            NamedPipeServerStream? eventPipe;
            Task? readTask;
            CancellationTokenSource? readCancellation;
            await _workerCleanupLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                readCancellation = Interlocked.Exchange(ref _readCancellation, null);
                readCancellation?.Cancel();
                readTask = Interlocked.Exchange(ref _readTask, null);
                lock (_workerSync)
                {
                    workerProcess = _workerProcess;
                    commandPipe = _commandPipe;
                    eventPipe = _eventPipe;
                    _workerProcess = null;
                    _commandPipe = null;
                    _eventPipe = null;
                }
            }
            finally
            {
                _workerCleanupLock.Release();
            }

            if (workerProcess is not null && commandPipe is not null && eventPipe is not null)
            {
                try
                {
                    if (commandPipe.IsConnected)
                    {
                        WriteDebugLog(attemptId, "父进程发送关闭串口命令。");
                        await WriteFrameAsync(commandPipe, ClosePortCommand, ReadOnlyMemory<byte>.Empty, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception exception)
                {
                    WriteDebugLog(attemptId, "父进程发送关闭串口命令失败，继续清理工作进程。", exception);
                }

                await TerminateWorkerAsync(attemptId, workerProcess, commandPipe, eventPipe, allowGracefulExit: true)
                    .ConfigureAwait(false);
            }
            else
            {
                commandPipe?.Dispose();
                eventPipe?.Dispose();
                workerProcess?.Dispose();
            }

            if (readTask is not null)
            {
                try
                {
                    await readTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            readCancellation?.Dispose();
            _diagnosticAttemptId = null;
            Interlocked.Exchange(ref _lastWorkerHeartbeatUnixMs, 0);
            Interlocked.Exchange(ref _lastWorkerReadActivityUnixMs, 0);
            WriteDebugLog(attemptId, "父进程断开流程结束。");
        }
        finally
        {
            Interlocked.Exchange(ref _disconnectInProgress, 0);
        }
    }

    public override async ValueTask SendAsync(
        ReadOnlyMemory<byte> data,
        string? target = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        NamedPipeServerStream commandPipe = _commandPipe ?? throw new InvalidOperationException("串口工作进程未初始化。");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteFrameAsync(commandPipe, SendDataCommand, data, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            ReportFault(new IOException($"串口 {settings.PortName} 写入失败：{exception.Message}", exception));
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    protected override ValueTask OnDisposeAsync()
    {
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }

    public static bool IsWorkerInvocation(IReadOnlyList<string> arguments) =>
        arguments.Count > 1 && string.Equals(arguments[1], WorkerSwitch, StringComparison.Ordinal);

    public static int RunWorkerProcess(IReadOnlyList<string> arguments)
    {
        if (!IsWorkerInvocation(arguments))
        {
            return 2;
        }

        string attemptId = arguments.Count > 2 ? ExtractAttemptId(arguments[2]) : "参数不完整";
        try
        {
            WriteDebugLog(attemptId, $"工作进程入口：PID={Environment.ProcessId}，参数数量={arguments.Count}。");
            RunWorkerProcessAsync(arguments).GetAwaiter().GetResult();
            WriteDebugLog(attemptId, "工作进程正常返回，退出码=0。");
            return 0;
        }
        catch (Exception exception)
        {
            WriteDebugLog(attemptId, "工作进程发生未处理异常，退出码=1。", exception);
            return 1;
        }
    }

    private ProcessStartInfo CreateWorkerStartInfo(string commandPipeName, string eventPipeName)
    {
        string executablePath = ResolveWorkerExecutablePath();
        ProcessStartInfo startInfo = new(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add(WorkerSwitch);
        startInfo.ArgumentList.Add(commandPipeName);
        startInfo.ArgumentList.Add(eventPipeName);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(settings.PortName);
        startInfo.ArgumentList.Add(settings.BaudRate.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(((int)settings.Parity).ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(settings.DataBits.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(((int)settings.StopBits).ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(((int)settings.Handshake).ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(settings.DtrEnable.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(settings.RtsEnable.ToString(CultureInfo.InvariantCulture));
        return startInfo;
    }

    private async Task ReadWorkerEventsAsync(Stream eventPipe, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                WorkerFrame frame = await ReadFrameAsync(eventPipe, cancellationToken).ConfigureAwait(false);
                switch (frame.Kind)
                {
                    case ReceivedDataMessage when frame.Payload.Length > 0:
                        PublishReceived(frame.Payload, settings.PortName);
                        MarkWorkerHeartbeatActivity();
                        break;
                    case WorkerHeartbeatMessage:
                        if (ApplyWorkerHeartbeat(frame.Payload))
                        {
                            MarkWorkerHeartbeatActivity();
                        }
                        break;
                    case WorkerErrorMessage:
                        ReportFault(new IOException($"串口 {settings.PortName} 读取失败：{DecodeMessage(frame.Payload)}"));
                        return;
                    default:
                        ReportFault(new IOException($"串口 {settings.PortName} 工作进程返回了未知消息。"));
                        return;
                }
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (EndOfStreamException exception)
        {
            WriteDebugLog(_diagnosticAttemptId ?? "无连接编号", "父进程读取工作进程事件时发现管道已关闭。", exception);
            ReportFault(new IOException($"串口 {settings.PortName} 工作进程已意外退出。", exception));
        }
        catch (Exception exception)
        {
            WriteDebugLog(_diagnosticAttemptId ?? "无连接编号", "父进程读取工作进程事件失败。", exception);
            ReportFault(new IOException($"串口 {settings.PortName} 通信失败：{exception.Message}", exception));
        }
    }

    private bool ApplyWorkerHeartbeat(byte[] payload)
    {
        if (!TryParseWorkerHeartbeat(payload, out WorkerHeartbeatSnapshot? snapshot) || snapshot is null)
        {
            WriteDebugLog(_diagnosticAttemptId ?? "无连接编号", $"工作进程心跳格式无效：{DecodeMessage(payload)}");
            return false;
        }

        Interlocked.Exchange(ref _workerReceivedBytes, snapshot.ReceivedBytes);
        Interlocked.Exchange(ref _workerRetryCount, snapshot.TransientRetryCount);
        Interlocked.Exchange(ref _workerTimeoutStreak, snapshot.TimeoutStreak);
        Interlocked.Exchange(ref _lastWorkerReadActivityUnixMs, snapshot.ReadLoopActivityUnixMs);
        WriteDebugLog(
            _diagnosticAttemptId ?? "无连接编号",
            $"工作进程心跳：PID={snapshot.ProcessId}，串口打开={snapshot.PortOpen}，读取循环活动={snapshot.ReadLoopActivityUnixMs} ms，"
                + $"最后成功读取={snapshot.LastSuccessfulReadUnixMs} ms，"
                + $"累计接收={snapshot.ReceivedBytes} B，连续超时={snapshot.TimeoutStreak}，累计重试={snapshot.TransientRetryCount}。");
        return true;
    }

    private async Task WatchWorkerActivityAsync(string attemptId, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(WorkerHeartbeatIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long lastHeartbeat = Interlocked.Read(ref _lastWorkerHeartbeatUnixMs);
                long lastReadActivity = Interlocked.Read(ref _lastWorkerReadActivityUnixMs);
                string? failureReason = GetWorkerFailureReason(now, lastHeartbeat, lastReadActivity);
                long heartbeatIdleMilliseconds = lastHeartbeat <= 0 ? long.MaxValue : now - lastHeartbeat;
                long readIdleMilliseconds = lastReadActivity <= 0 ? 0 : now - lastReadActivity;

                if (failureReason is not null && State == TransportState.Connected)
                {
                    WriteDebugLog(
                        attemptId,
                        $"父进程检测到工作进程故障：心跳空闲={heartbeatIdleMilliseconds} ms，读取循环空闲={readIdleMilliseconds} ms，"
                            + failureReason);
                    await FailWorkerAsync(attemptId, failureReason).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            WriteDebugLog(attemptId, "父进程心跳监视任务异常。", exception);
        }
    }

    private void MarkWorkerHeartbeatActivity() =>
        Interlocked.Exchange(ref _lastWorkerHeartbeatUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    internal static string? GetWorkerFailureReason(
        long nowUnixMilliseconds,
        long lastHeartbeatUnixMilliseconds,
        long lastReadActivityUnixMilliseconds)
    {
        long heartbeatIdleMilliseconds = lastHeartbeatUnixMilliseconds <= 0
            ? long.MaxValue
            : nowUnixMilliseconds - lastHeartbeatUnixMilliseconds;
        if (heartbeatIdleMilliseconds > WorkerHeartbeatTimeoutMilliseconds)
        {
            return $"串口工作进程心跳超时（{heartbeatIdleMilliseconds / 1000.0:N1} 秒无响应），已停止接收。";
        }

        long readIdleMilliseconds = lastReadActivityUnixMilliseconds <= 0
            ? 0
            : nowUnixMilliseconds - lastReadActivityUnixMilliseconds;
        return readIdleMilliseconds > WorkerReadActivityTimeoutMilliseconds
            ? $"串口读取循环疑似卡死（{readIdleMilliseconds / 1000.0:N1} 秒无活动），已停止接收。"
            : null;
    }

    private void OnWorkerProcessExited(string attemptId, Process workerProcess)
    {
        lock (_workerSync)
        {
            if (!ReferenceEquals(_workerProcess, workerProcess))
            {
                WriteDebugLog(attemptId, $"父进程忽略旧工作进程退出事件：PID={TryGetProcessId(workerProcess)}。");
                return;
            }
        }

        int exitCode = TryGetExitCode(workerProcess);
        WriteDebugLog(
            attemptId,
            $"父进程收到工作进程退出事件：PID={TryGetProcessId(workerProcess)}，退出码={exitCode}，"
                + $"连接状态={State}。");
        if (_disconnectInProgress != 0 || _readCancellation is null)
        {
            return;
        }

        _ = FailWorkerAsync(attemptId, $"串口工作进程已退出（退出码={exitCode}）。");
    }

    private async Task FailWorkerAsync(string attemptId, string reason)
    {
        WriteDebugLog(attemptId, $"父进程判定串口工作进程故障：{reason}");
        ReportFault(new IOException(reason));
        await _workerCleanupLock.WaitAsync().ConfigureAwait(false);
        try
        {
            CancellationTokenSource? readCancellation = Interlocked.Exchange(ref _readCancellation, null);
            readCancellation?.Cancel();
            Task? readTask = Interlocked.Exchange(ref _readTask, null);
            Process? workerProcess;
            NamedPipeServerStream? commandPipe;
            NamedPipeServerStream? eventPipe;
            lock (_workerSync)
            {
                workerProcess = _workerProcess;
                commandPipe = _commandPipe;
                eventPipe = _eventPipe;
                _workerProcess = null;
                _commandPipe = null;
                _eventPipe = null;
            }

            if (workerProcess is not null && commandPipe is not null && eventPipe is not null)
            {
                await TerminateWorkerAsync(attemptId, workerProcess, commandPipe, eventPipe).ConfigureAwait(false);
            }
            else
            {
                commandPipe?.Dispose();
                eventPipe?.Dispose();
                workerProcess?.Dispose();
            }

            if (readTask is not null)
            {
                try
                {
                    await readTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            readCancellation?.Dispose();
            _diagnosticAttemptId = null;
            Interlocked.Exchange(ref _lastWorkerHeartbeatUnixMs, 0);
            Interlocked.Exchange(ref _lastWorkerReadActivityUnixMs, 0);
        }
        finally
        {
            _workerCleanupLock.Release();
        }
    }

    private void ClearWorker(Process workerProcess)
    {
        lock (_workerSync)
        {
            if (ReferenceEquals(_workerProcess, workerProcess))
            {
                _workerProcess = null;
                _commandPipe = null;
                _eventPipe = null;
            }
        }
    }

    private static async Task TerminateWorkerAsync(
        string attemptId,
        Process workerProcess,
        NamedPipeServerStream commandPipe,
        NamedPipeServerStream eventPipe,
        bool allowGracefulExit = false)
    {
        int workerProcessId = TryGetProcessId(workerProcess);
        WriteDebugLog(
            attemptId,
            $"父进程开始清理工作进程：PID={workerProcessId}，允许正常退出={allowGracefulExit}，"
                + $"初始状态={DescribeProcessState(workerProcess)}。");
        commandPipe.Dispose();
        WriteDebugLog(attemptId, "父进程已释放命令管道。");
        if (allowGracefulExit)
        {
            try
            {
                await workerProcess.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromMilliseconds(WorkerShutdownTimeoutMilliseconds))
                    .ConfigureAwait(false);
                WriteDebugLog(
                    attemptId,
                    $"工作进程在正常退出等待期内结束：PID={workerProcessId}，{DescribeProcessState(workerProcess)}。");
            }
            catch (Exception exception)
            {
                WriteDebugLog(
                    attemptId,
                    $"等待工作进程正常退出未完成：PID={workerProcessId}，{DescribeProcessState(workerProcess)}。",
                    exception);
            }
        }

        try
        {
            if (!workerProcess.HasExited)
            {
                WriteDebugLog(attemptId, $"父进程强制终止工作进程：PID={workerProcessId}。");
                workerProcess.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception)
        {
            WriteDebugLog(attemptId, $"父进程强制终止工作进程失败：PID={workerProcessId}。", exception);
        }

        try
        {
            await workerProcess.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromMilliseconds(WorkerShutdownTimeoutMilliseconds))
                .ConfigureAwait(false);
            WriteDebugLog(
                attemptId,
                $"父进程确认工作进程已退出：PID={workerProcessId}，{DescribeProcessState(workerProcess)}。");
        }
        catch (Exception exception)
        {
            WriteDebugLog(
                attemptId,
                $"父进程未能在期限内确认工作进程退出：PID={workerProcessId}，{DescribeProcessState(workerProcess)}。",
                exception);
        }

        eventPipe.Dispose();
        workerProcess.Dispose();
        WriteDebugLog(attemptId, $"父进程已释放事件管道和工作进程对象：PID={workerProcessId}。");
    }

    private static async Task RunWorkerProcessAsync(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 13)
        {
            throw new ArgumentException("串口工作进程参数不完整。", nameof(arguments));
        }

        string commandPipeName = arguments[2];
        string attemptId = ExtractAttemptId(commandPipeName);
        string eventPipeName = arguments[3];
        int parentProcessId = ParseInt32(arguments[4], "父进程 ID");
        string portName = arguments[5];
        int baudRate = ParseInt32(arguments[6], "波特率");
        SerialParity parity = (SerialParity)ParseInt32(arguments[7], "校验位");
        int dataBits = ParseInt32(arguments[8], "数据位");
        SerialStopBits stopBits = (SerialStopBits)ParseInt32(arguments[9], "停止位");
        SerialHandshake handshake = (SerialHandshake)ParseInt32(arguments[10], "流控");
        bool dtrEnable = bool.Parse(arguments[11]);
        bool rtsEnable = bool.Parse(arguments[12]);
        WriteDebugLog(
            attemptId,
            $"工作进程解析参数完成：父进程={parentProcessId}，端口={portName}，波特率={baudRate}，"
                + $"数据位={dataBits}，校验={parity}，停止位={stopBits}，流控={handshake}，"
                + $"DTR={dtrEnable}，RTS={rtsEnable}。");

        using NamedPipeClientStream commandPipe = new(
            ".",
            commandPipeName,
            PipeDirection.In,
            PipeOptions.Asynchronous);
        using NamedPipeClientStream eventPipe = new(
            ".",
            eventPipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);
        using CancellationTokenSource workerCancellation = new();
        _ = WatchParentProcessAsync(parentProcessId, workerCancellation.Token);

        await Task.WhenAll(
            commandPipe.ConnectAsync(WorkerStartupTimeoutMilliseconds, workerCancellation.Token),
            eventPipe.ConnectAsync(WorkerStartupTimeoutMilliseconds, workerCancellation.Token))
            .ConfigureAwait(false);
        WriteDebugLog(attemptId, "工作进程管道连接完成，发送就绪消息。");
        await WriteFrameAsync(eventPipe, WorkerReadyMessage, ReadOnlyMemory<byte>.Empty, workerCancellation.Token)
            .ConfigureAwait(false);

        SerialPort? port = null;
        Exception? openFailure = null;
        for (int attempt = 1; attempt <= AccessDeniedOpenRetryCount; attempt++)
        {
            if (port is not null)
            {
                WriteDebugLog(attemptId, $"工作进程释放第 {attempt - 1} 次失败打开所用的 SerialPort 对象。");
            }
            port?.Dispose();
            port = CreateSerialPort(
                portName,
                baudRate,
                parity,
                dataBits,
                stopBits,
                handshake,
                dtrEnable,
                rtsEnable);
            Stopwatch openStopwatch = Stopwatch.StartNew();
            WriteDebugLog(
                attemptId,
                $"工作进程第 {attempt}/{AccessDeniedOpenRetryCount} 次调用 SerialPort.Open()："
                    + $"端口={portName}，波特率={baudRate}。");
            try
            {
                port.Open();
                openStopwatch.Stop();
                WriteDebugLog(
                    attemptId,
                    $"工作进程 SerialPort.Open() 成功：第 {attempt} 次，耗时={openStopwatch.ElapsedMilliseconds} ms，"
                        + $"IsOpen={port.IsOpen}。");
                openFailure = null;
                break;
            }
            catch (UnauthorizedAccessException exception)
            {
                openStopwatch.Stop();
                WriteDebugLog(
                    attemptId,
                    $"工作进程 SerialPort.Open() 返回 UnauthorizedAccessException：第 {attempt} 次，"
                        + $"耗时={openStopwatch.ElapsedMilliseconds} ms。",
                    exception);
                openFailure = exception;
                if (attempt < AccessDeniedOpenRetryCount)
                {
                    WriteDebugLog(attemptId, $"工作进程等待 {AccessDeniedOpenRetryDelayMilliseconds} ms 后重试打开。");
                    await Task.Delay(AccessDeniedOpenRetryDelayMilliseconds, workerCancellation.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                openStopwatch.Stop();
                WriteDebugLog(
                    attemptId,
                    $"工作进程 SerialPort.Open() 返回异常：第 {attempt} 次，"
                        + $"耗时={openStopwatch.ElapsedMilliseconds} ms。",
                    exception);
                openFailure = exception;
                break;
            }
        }

        if (openFailure is not null || port is null || !port.IsOpen)
        {
            string message = openFailure is UnauthorizedAccessException
                ? AccessDeniedWorkerErrorPrefix + openFailure.Message
                : openFailure?.Message ?? "驱动未能将串口置为打开状态。";
            port?.Dispose();
            WriteDebugLog(attemptId, $"工作进程打开失败，已释放 SerialPort 对象，向父进程报告：{message}");
            await TryWriteWorkerErrorAsync(eventPipe, message, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        using (port)
        {
            WriteDebugLog(attemptId, "工作进程向父进程发送串口打开成功消息。");
            await WriteFrameAsync(eventPipe, PortOpenedMessage, ReadOnlyMemory<byte>.Empty, workerCancellation.Token)
                .ConfigureAwait(false);

            using SemaphoreSlim eventWriteLock = new(1, 1);
            SerialReadStats readStats = new();
            Task commandTask = RunCommandLoopAsync(commandPipe, port, attemptId, workerCancellation.Token);
            Task readTask = Task.Run(
                () => ReadSerialInWorkerAsync(new SerialPortReadSource(port), eventPipe, eventWriteLock, readStats, attemptId, workerCancellation.Token),
                CancellationToken.None);
            Task heartbeatTask = Task.Run(
                () => RunHeartbeatLoopAsync(port, eventPipe, eventWriteLock, readStats, attemptId, workerCancellation.Token),
                CancellationToken.None);
            try
            {
                Task completed = await Task.WhenAny(commandTask, readTask, heartbeatTask).ConfigureAwait(false);
                if (workerCancellation.IsCancellationRequested)
                {
                    WriteDebugLog(attemptId, "工作进程因取消而结束主循环（正常关闭路径）。");
                }
                else if (completed == commandTask && commandTask.Status == TaskStatus.RanToCompletion)
                {
                    WriteDebugLog(attemptId, "工作进程命令循环正常结束（收到关闭命令）。");
                }
                else
                {
                    string failureMessage;
                    Exception? failureException;
                    if (completed == commandTask && ContainsException(commandTask.Exception, typeof(EndOfStreamException)))
                    {
                        WriteDebugLog(attemptId, "工作进程命令管道已关闭（父进程侧断开），按正常结束处理。");
                    }
                    else if (completed == readTask)
                    {
                        failureException = readTask.Exception?.GetBaseException();
                        WriteDebugLog(attemptId, "工作进程串口读取任务异常或意外结束。", failureException);
                        failureMessage = failureException is null
                            ? "串口读取循环意外提前结束。"
                            : $"串口读取循环异常终止：{failureException.Message}";
                        await FailWorkerSelfAsync(
                                eventPipe,
                                eventWriteLock,
                                failureMessage,
                                attemptId,
                                workerCancellation)
                            .ConfigureAwait(false);
                    }
                    else if (completed == heartbeatTask)
                    {
                        failureException = heartbeatTask.Exception?.GetBaseException();
                        WriteDebugLog(attemptId, "工作进程心跳循环异常结束。", failureException);
                        failureMessage = $"串口工作进程心跳发送失败：{failureException?.Message ?? "未知异常"}";
                        await FailWorkerSelfAsync(
                                eventPipe,
                                eventWriteLock,
                                failureMessage,
                                attemptId,
                                workerCancellation)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        failureException = commandTask.Exception?.GetBaseException();
                        WriteDebugLog(attemptId, "工作进程命令循环异常结束。", failureException);
                        failureMessage = $"串口工作进程命令循环异常：{failureException?.Message ?? "未知异常"}";
                        await FailWorkerSelfAsync(
                                eventPipe,
                                eventWriteLock,
                                failureMessage,
                                attemptId,
                                workerCancellation)
                            .ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                WriteDebugLog(attemptId, $"工作进程开始关闭串口：IsOpen={port.IsOpen}。");
                workerCancellation.Cancel();
                Stopwatch closeStopwatch = Stopwatch.StartNew();
                try
                {
                    port.Close();
                    closeStopwatch.Stop();
                    WriteDebugLog(
                        attemptId,
                        $"工作进程 SerialPort.Close() 返回：耗时={closeStopwatch.ElapsedMilliseconds} ms，IsOpen={port.IsOpen}。");
                }
                catch (Exception exception)
                {
                    closeStopwatch.Stop();
                    WriteDebugLog(
                        attemptId,
                        $"工作进程 SerialPort.Close() 异常：耗时={closeStopwatch.ElapsedMilliseconds} ms。",
                        exception);
                }

                await AwaitTaskQuietlyAsync(attemptId, "命令循环", commandTask).ConfigureAwait(false);
                await AwaitTaskQuietlyAsync(attemptId, "串口读取循环", readTask).ConfigureAwait(false);
                await AwaitTaskQuietlyAsync(attemptId, "心跳循环", heartbeatTask).ConfigureAwait(false);
                WriteDebugLog(attemptId, "工作进程三个循环均已结束。");
            }
        }
    }

    private static SerialPort CreateSerialPort(
        string portName,
        int baudRate,
        SerialParity parity,
        int dataBits,
        SerialStopBits stopBits,
        SerialHandshake handshake,
        bool dtrEnable,
        bool rtsEnable) => new(
            portName,
            baudRate,
            MapParity(parity),
            dataBits,
            MapStopBits(stopBits))
        {
            Handshake = MapHandshake(handshake),
            DtrEnable = dtrEnable,
            RtsEnable = rtsEnable,
            ReadBufferSize = 64 * 1024,
            WriteBufferSize = 64 * 1024,
            ReadTimeout = 250,
            WriteTimeout = 1000
        };

    private static async Task RunCommandLoopAsync(
        NamedPipeClientStream commandPipe,
        SerialPort port,
        string attemptId,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            WorkerFrame command = await ReadFrameAsync(commandPipe, cancellationToken).ConfigureAwait(false);
            switch (command.Kind)
            {
                case SendDataCommand:
                    if (command.Payload.Length > 0)
                    {
                        port.Write(command.Payload, 0, command.Payload.Length);
                    }
                    break;
                case ClosePortCommand:
                    WriteDebugLog(attemptId, "工作进程收到关闭串口命令。");
                    return;
                default:
                    throw new IOException("收到未知的串口工作进程命令。");
            }
        }
    }

    internal interface ISerialPortReadSource
    {
        int Read(byte[] buffer, int offset, int count);
        bool IsOpen { get; }
        int ReadTimeout { get; }
    }

    private sealed class SerialPortReadSource(SerialPort port) : ISerialPortReadSource
    {
        public int Read(byte[] buffer, int offset, int count) => port.Read(buffer, offset, count);
        public bool IsOpen => port.IsOpen;
        public int ReadTimeout => port.ReadTimeout;
    }

    internal static async Task ReadSerialInWorkerAsync(
        ISerialPortReadSource port,
        Stream eventPipe,
        SemaphoreSlim eventWriteLock,
        SerialReadStats stats,
        string attemptId,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        int transientFailureCount = 0;
        int timeoutStreak = 0;
        long lastSuccessfulReadLogTimestamp = 0;
        WriteDebugLog(attemptId, $"工作进程串口读取循环启动：ReadTimeout={port.ReadTimeout} ms，缓冲区={buffer.Length} B。");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int count;
                Interlocked.Exchange(
                    ref stats.ReadLoopActivityUnixMs,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                try
                {
                    count = port.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException)
                {
                    timeoutStreak++;
                    Interlocked.Exchange(ref stats.TimeoutStreak, timeoutStreak);
                    await Task.Delay(ReadLoopYieldDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                catch (Exception exception) when (TransientReadRetryPolicy.IsTransient(exception))
                {
                    transientFailureCount++;
                    Interlocked.Increment(ref stats.TransientRetryCount);
                    int delay = TransientReadRetryPolicy.GetRetryDelay(transientFailureCount);
                    WriteDebugLog(
                        attemptId,
                        $"工作进程串口读取返回瞬时异常：第 {transientFailureCount}/{MaxTransientReadRetryCount} 次，"
                            + $"退避={delay} ms，串口仍打开={port.IsOpen}。",
                        exception);
                    if (transientFailureCount >= MaxTransientReadRetryCount || !port.IsOpen)
                    {
                        throw new IOException(
                            $"串口读取连续失败 {transientFailureCount} 次（驱动返回瞬时中止）：{exception.Message}",
                            exception);
                    }

                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                transientFailureCount = 0;
                timeoutStreak = 0;
                Interlocked.Exchange(ref stats.TimeoutStreak, 0);
                if (count <= 0)
                {
                    await Task.Delay(ReadLoopYieldDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                long totalBytes = Interlocked.Add(ref stats.TotalBytes, count);
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Interlocked.Exchange(ref stats.LastSuccessfulReadUnixMs, now);
                if (now - lastSuccessfulReadLogTimestamp >= WorkerHeartbeatIntervalMilliseconds)
                {
                    lastSuccessfulReadLogTimestamp = now;
                    WriteDebugLog(attemptId, $"工作进程串口成功读取 {count} B，累计={totalBytes} B。");
                }

                await WriteEventFrameWithTimeoutAsync(
                    eventPipe,
                    eventWriteLock,
                    ReceivedDataMessage,
                    buffer.AsMemory(0, count),
                    attemptId,
                    cancellationToken,
                    EventWriteTimeoutMilliseconds).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested)
        {
            WriteDebugLog(attemptId, "工作进程串口读取循环在取消中退出。", exception);
        }
    }

    private static async Task RunHeartbeatLoopAsync(
        SerialPort port,
        Stream eventPipe,
        SemaphoreSlim eventWriteLock,
        SerialReadStats stats,
        string attemptId,
        CancellationToken cancellationToken)
    {
        int consecutiveSendFailures = 0;
        WriteDebugLog(attemptId, "工作进程心跳循环启动。");
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(WorkerHeartbeatIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            byte[] payload = BuildHeartbeatPayload(port, stats);
            try
            {
                await WriteEventFrameWithTimeoutAsync(
                    eventPipe,
                    eventWriteLock,
                    WorkerHeartbeatMessage,
                    payload,
                    attemptId,
                    cancellationToken,
                    EventWriteTimeoutMilliseconds).ConfigureAwait(false);
                consecutiveSendFailures = 0;
                WriteDebugLog(attemptId, $"工作进程心跳已发送：{DecodeMessage(payload)}");
            }
            catch (IOException exception) when (!cancellationToken.IsCancellationRequested)
            {
                consecutiveSendFailures++;
                WriteDebugLog(
                    attemptId,
                    $"工作进程心跳发送失败：连续 {consecutiveSendFailures}/{WorkerHeartbeatSendFailureLimit} 次。",
                    exception);
                if (consecutiveSendFailures >= WorkerHeartbeatSendFailureLimit)
                {
                    throw new IOException("工作进程心跳连续发送失败，判定父进程或事件管道异常。", exception);
                }
            }
        }
    }

    private static byte[] BuildHeartbeatPayload(SerialPort port, SerialReadStats stats) => BuildHeartbeatPayload(
        port.IsOpen,
        Interlocked.Read(ref stats.LastSuccessfulReadUnixMs),
        Interlocked.Read(ref stats.ReadLoopActivityUnixMs),
        Interlocked.Read(ref stats.TotalBytes),
        Interlocked.Read(ref stats.TimeoutStreak),
        Interlocked.Read(ref stats.TransientRetryCount));

    internal static byte[] BuildHeartbeatPayload(
        bool portOpen,
        long lastSuccessfulReadUnixMs,
        long readLoopActivityUnixMs,
        long receivedBytes,
        long timeoutStreak,
        long transientRetryCount)
    {
        StringBuilder builder = new();
        builder.Append("pid=").Append(Environment.ProcessId)
            .Append(";open=").Append(portOpen ? 1 : 0)
            .Append(";lastReadMs=").Append(lastSuccessfulReadUnixMs)
            .Append(";readLoopMs=").Append(readLoopActivityUnixMs)
            .Append(";bytes=").Append(receivedBytes)
            .Append(";timeouts=").Append(timeoutStreak)
            .Append(";retries=").Append(transientRetryCount);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    internal sealed record WorkerHeartbeatSnapshot(
        long ProcessId,
        bool PortOpen,
        long LastSuccessfulReadUnixMs,
        long ReadLoopActivityUnixMs,
        long ReceivedBytes,
        long TimeoutStreak,
        long TransientRetryCount);

    internal static bool TryParseWorkerHeartbeat(byte[] payload, out WorkerHeartbeatSnapshot? snapshot)
    {
        snapshot = null;
        string text = DecodeMessage(payload);
        long? pid = null;
        bool? portOpen = null;
        long? lastReadMs = null;
        long? readLoopMs = null;
        long? bytes = null;
        long? timeouts = null;
        long? retries = null;
        foreach (string segment in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = segment.IndexOf('=');
            if (separator <= 0 || separator >= segment.Length - 1)
            {
                return false;
            }

            string key = segment[..separator];
            string valueText = segment[(separator + 1)..];
            switch (key)
            {
                case "pid" when long.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value):
                    pid = value;
                    break;
                case "open" when valueText is "0" or "1":
                    portOpen = valueText == "1";
                    break;
                case "lastReadMs" when long.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value):
                    lastReadMs = value;
                    break;
                case "readLoopMs" when long.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value):
                    readLoopMs = value;
                    break;
                case "bytes" when long.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value):
                    bytes = value;
                    break;
                case "timeouts" when long.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value):
                    timeouts = value;
                    break;
                case "retries" when long.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value):
                    retries = value;
                    break;
            }
        }

        if (pid is null || portOpen is null || lastReadMs is null || readLoopMs is null || bytes is null || timeouts is null || retries is null)
        {
            return false;
        }

        snapshot = new WorkerHeartbeatSnapshot(
            pid.Value,
            portOpen.Value,
            lastReadMs.Value,
            readLoopMs.Value,
            bytes.Value,
            timeouts.Value,
            retries.Value);
        return true;
    }

    private static async Task FailWorkerSelfAsync(
        Stream eventPipe,
        SemaphoreSlim eventWriteLock,
        string message,
        string attemptId,
        CancellationTokenSource workerCancellation)
    {
        WriteDebugLog(attemptId, $"工作进程判定自身故障，取消全部循环并报告：{message}");
        workerCancellation.Cancel();
        await SendWorkerErrorFrameAsync(eventPipe, eventWriteLock, message, attemptId, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task SendWorkerErrorFrameAsync(
        Stream eventPipe,
        SemaphoreSlim eventWriteLock,
        string message,
        string attemptId,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteEventFrameWithTimeoutAsync(
                eventPipe,
                eventWriteLock,
                WorkerErrorMessage,
                Encoding.UTF8.GetBytes(message),
                attemptId,
                cancellationToken,
                EventWriteTimeoutMilliseconds).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            WriteDebugLog(attemptId, "工作进程发送错误帧失败。", exception);
        }
    }

    internal static async Task WriteEventFrameWithTimeoutAsync(
        Stream eventPipe,
        SemaphoreSlim eventWriteLock,
        byte kind,
        ReadOnlyMemory<byte> payload,
        string attemptId,
        CancellationToken cancellationToken,
        int timeoutMilliseconds)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        bool lockAcquired = false;
        long lockWaitMilliseconds = 0;
        long writeMilliseconds = 0;
        string? timeoutReason = null;
        Exception? timeoutException = null;
        try
        {
            lockAcquired = await eventWriteLock
                .WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds), cancellationToken)
                .ConfigureAwait(false);
            lockWaitMilliseconds = stopwatch.ElapsedMilliseconds;
            if (!lockAcquired)
            {
                timeoutReason = $"事件管道写入超时（写锁等待超过 {timeoutMilliseconds} ms），父进程或管道异常。";
            }
            else
            {
                using CancellationTokenSource writeCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                writeCancellation.CancelAfter(timeoutMilliseconds);
                Stopwatch writeStopwatch = Stopwatch.StartNew();
                try
                {
                    await WriteFrameAsync(eventPipe, kind, payload, writeCancellation.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    writeCancellation.IsCancellationRequested
                    && !cancellationToken.IsCancellationRequested)
                {
                    timeoutReason = $"事件管道写入超时（>{timeoutMilliseconds} ms），父进程或管道异常。";
                    timeoutException = exception;
                }

                writeMilliseconds = writeStopwatch.ElapsedMilliseconds;
            }
        }
        finally
        {
            if (lockAcquired)
            {
                eventWriteLock.Release();
            }
        }

        if (timeoutReason is not null)
        {
            WriteDebugLog(
                attemptId,
                $"{timeoutReason} 锁等待={lockWaitMilliseconds} ms，写入={writeMilliseconds} ms，帧类型={kind}。",
                timeoutException);
            throw new IOException(timeoutReason, timeoutException);
        }

        if (writeMilliseconds > 500 || lockWaitMilliseconds > 500)
        {
            WriteDebugLog(
                attemptId,
                $"事件管道写入偏慢：锁等待={lockWaitMilliseconds} ms，写入={writeMilliseconds} ms，帧类型={kind}。");
        }
    }

    private static bool ContainsException(Exception? exception, Type type)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (type.IsInstanceOfType(current))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task AwaitTaskQuietlyAsync(string attemptId, string taskName, Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
            WriteDebugLog(attemptId, $"工作进程{taskName}已结束。");
        }
        catch (Exception exception)
        {
            WriteDebugLog(attemptId, $"工作进程等待{taskName}结束时发生异常。", exception);
        }
    }

    private static int TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    internal sealed class SerialReadStats
    {
        public long TotalBytes;
        public long LastSuccessfulReadUnixMs;
        public long ReadLoopActivityUnixMs;
        public long TransientRetryCount;
        public long TimeoutStreak;
    }

    internal sealed class TransientReadRetryPolicy
    {
        private static readonly int[] DefaultRetryDelaysMilliseconds = [50, 100, 200, 400, 800];
        private const int DefaultMaxRetryCount = 5;
        private static readonly int OperationAbortedHResult = unchecked((int)0x800703E3);
        private const int OperationAbortedNativeErrorCode = 995;

        public static bool IsTransient(Exception exception)
        {
            if (exception is TimeoutException or OperationCanceledException)
            {
                return true;
            }

            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                if (current.HResult == OperationAbortedHResult)
                {
                    return true;
                }

                if (current is Win32Exception { NativeErrorCode: OperationAbortedNativeErrorCode })
                {
                    return true;
                }
            }

            return false;
        }

        public static int GetRetryDelay(int retryCount) => DefaultRetryDelaysMilliseconds[
            Math.Clamp(retryCount - 1, 0, DefaultRetryDelaysMilliseconds.Length - 1)];

        public static int MaxRetryCount => DefaultMaxRetryCount;
    }

    private static async Task WatchParentProcessAsync(int parentProcessId, CancellationToken cancellationToken)
    {
        try
        {
            using Process parentProcess = Process.GetProcessById(parentProcessId);
            await parentProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            Environment.Exit(3);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            Environment.Exit(3);
        }
    }

    private static async Task WriteFrameAsync(
        Stream stream,
        byte kind,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[5];
        header[0] = kind;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (!payload.IsEmpty)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WorkerFrame> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[5];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (length < 0 || length > MaximumFrameLength)
        {
            throw new IOException($"串口工作进程消息长度无效：{length}。");
        }

        byte[] payload = length == 0 ? [] : new byte[length];
        if (length > 0)
        {
            await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        }
        return new WorkerFrame(header[0], payload);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int count = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException();
            }
            offset += count;
        }
    }

    private static async Task TryWriteWorkerErrorAsync(
        Stream eventPipe,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteFrameAsync(
                eventPipe,
                WorkerErrorMessage,
                Encoding.UTF8.GetBytes(message),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private IOException CreateWorkerException(WorkerFrame frame, string fallbackMessage)
    {
        string reason = frame.Kind == WorkerErrorMessage && frame.Payload.Length > 0
            ? DecodeMessage(frame.Payload)
            : fallbackMessage;
        return new IOException(reason);
    }

    private string BuildOpenFailureMessage(string reason)
    {
        if (reason.StartsWith(AccessDeniedWorkerErrorPrefix, StringComparison.Ordinal))
        {
            return $"打开串口 {settings.PortName}（{settings.BaudRate}）失败：端口被其他程序占用，或上一轮关闭尚未完成。"
                + "已自动重试 3 次；请关闭 SSCOM 等占用该端口的程序后重试。";
        }

        return $"打开串口 {settings.PortName}（{settings.BaudRate}）失败：{reason}。"
            + "本次打开已终止并释放，不限制波特率，可直接切换任意波特率重试。";
    }

    private static string ExtractAttemptId(string commandPipeName)
    {
        int separatorIndex = commandPipeName.LastIndexOf('.');
        return separatorIndex >= 0 && separatorIndex < commandPipeName.Length - 1
            ? commandPipeName[(separatorIndex + 1)..]
            : commandPipeName;
    }

    private static int TryGetProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return -1;
        }
    }

    private static string DescribeProcessState(Process process)
    {
        try
        {
            return process.HasExited
                ? $"已退出=True，退出码={process.ExitCode}"
                : "已退出=False";
        }
        catch (Exception exception)
        {
            return $"状态读取失败={exception.GetType().Name}: {exception.Message}";
        }
    }

    private static string DecodeMessageForLog(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return "<空>";
        }

        string message = DecodeMessage(payload).Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return message.Length <= 500 ? message : message[..500] + "...";
    }

    private static void WriteDebugLog(string attemptId, string message, Exception? exception = null)
    {
        if (!File.Exists(AppPaths.DebugLoggingMarkerPath))
        {
            return;
        }

        Mutex? mutex = null;
        bool mutexAcquired = false;
        try
        {
            mutex = new Mutex(false, DebugLogMutexName);
            try
            {
                mutexAcquired = mutex.WaitOne(TimeSpan.FromMilliseconds(DebugLogMutexTimeoutMilliseconds));
            }
            catch (AbandonedMutexException)
            {
                mutexAcquired = true;
            }

            if (!mutexAcquired)
            {
                return;
            }

            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeviceDebugStudio",
                "Logs");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"SerialPortDebug-{DateTime.Now:yyyyMMdd}.log");
            string role = IsWorkerInvocation(Environment.GetCommandLineArgs()) ? "工作进程" : "父进程";
            string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} "
                + $"[PID={Environment.ProcessId}] [TID={Environment.CurrentManagedThreadId}] "
                + $"[{role}] [连接={attemptId}] {message}";
            if (exception is not null)
            {
                line += $" | 异常链={DescribeException(exception)}";
            }

            File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch
        {
        }
        finally
        {
            if (mutexAcquired)
            {
                try
                {
                    mutex!.ReleaseMutex();
                }
                catch
                {
                }
            }

            mutex?.Dispose();
        }
    }

    private static string DescribeException(Exception exception)
    {
        StringBuilder builder = new();
        int depth = 0;
        for (Exception? current = exception; current is not null && depth < 8; current = current.InnerException)
        {
            if (depth > 0)
            {
                builder.Append(" -> ");
            }

            builder.Append(current.GetType().FullName)
                .Append("(HResult=0x")
                .Append(current.HResult.ToString("X8", CultureInfo.InvariantCulture));
            if (current is Win32Exception win32Exception)
            {
                builder.Append(", NativeErrorCode=")
                    .Append(win32Exception.NativeErrorCode.ToString(CultureInfo.InvariantCulture));
            }

            builder.Append("): ")
                .Append(current.Message.Replace("\r", "\\r", StringComparison.Ordinal)
                    .Replace("\n", "\\n", StringComparison.Ordinal));
            depth++;
        }

        return builder.ToString();
    }

    private static string DecodeMessage(byte[] payload) => Encoding.UTF8.GetString(payload);

    private static string ResolveWorkerExecutablePath()
    {
        string currentPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前程序路径，不能启动串口工作进程。");
        if (string.Equals(
                Path.GetFileNameWithoutExtension(currentPath),
                "DeviceDebugStudio",
                StringComparison.OrdinalIgnoreCase))
        {
            return currentPath;
        }

        string appHostPath = Path.Combine(AppContext.BaseDirectory, "DeviceDebugStudio.exe");
        return File.Exists(appHostPath)
            ? appHostPath
            : throw new InvalidOperationException("未找到 DeviceDebugStudio.exe，不能启动串口工作进程。");
    }

    private static int ParseInt32(string text, string name) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new ArgumentException($"{name}格式无效。");

    private static Parity MapParity(SerialParity value) => value switch
    {
        SerialParity.Odd => Parity.Odd,
        SerialParity.Even => Parity.Even,
        SerialParity.Mark => Parity.Mark,
        SerialParity.Space => Parity.Space,
        _ => Parity.None
    };

    private static StopBits MapStopBits(SerialStopBits value) => value switch
    {
        SerialStopBits.OnePointFive => StopBits.OnePointFive,
        SerialStopBits.Two => StopBits.Two,
        _ => StopBits.One
    };

    private static Handshake MapHandshake(SerialHandshake value) => value switch
    {
        SerialHandshake.XOnXOff => Handshake.XOnXOff,
        SerialHandshake.RtsCts => Handshake.RequestToSend,
        SerialHandshake.RtsCtsXOnXOff => Handshake.RequestToSendXOnXOff,
        _ => Handshake.None
    };

    private sealed record WorkerFrame(byte Kind, byte[] Payload);
}
