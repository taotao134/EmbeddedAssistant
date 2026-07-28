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
    private const byte SendDataCommand = 1;
    private const byte ClosePortCommand = 2;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _workerSync = new();
    private Process? _workerProcess;
    private NamedPipeServerStream? _commandPipe;
    private NamedPipeServerStream? _eventPipe;
    private CancellationTokenSource? _readCancellation;
    private Task? _readTask;
    private string? _diagnosticAttemptId;

    public override string DisplayName => string.IsNullOrWhiteSpace(settings.PortName) ? "串口" : settings.PortName;
    public override TransportKind Kind => TransportKind.Serial;

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
        CancellationTokenSource? readCancellation = Interlocked.Exchange(ref _readCancellation, null);
        readCancellation?.Cancel();

        Process? workerProcess;
        NamedPipeServerStream? commandPipe;
        NamedPipeServerStream? eventPipe;
        Task? readTask = Interlocked.Exchange(ref _readTask, null);
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
        WriteDebugLog(attemptId, "父进程断开流程结束。");
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
            Task readTask = Task.Run(
                () => ReadSerialInWorkerAsync(port, eventPipe, eventWriteLock, workerCancellation.Token),
                CancellationToken.None);
            try
            {
                while (!workerCancellation.IsCancellationRequested)
                {
                    WorkerFrame command = await ReadFrameAsync(commandPipe, workerCancellation.Token).ConfigureAwait(false);
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
            catch (EndOfStreamException)
            {
                WriteDebugLog(attemptId, "工作进程命令管道已关闭。");
            }
            catch (Exception exception) when (!workerCancellation.IsCancellationRequested)
            {
                WriteDebugLog(attemptId, "工作进程命令循环异常。", exception);
                await eventWriteLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await TryWriteWorkerErrorAsync(eventPipe, exception.Message, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    eventWriteLock.Release();
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

                try
                {
                    await readTask.ConfigureAwait(false);
                    WriteDebugLog(attemptId, "工作进程串口读取任务已结束。");
                }
                catch (Exception exception)
                {
                    WriteDebugLog(attemptId, "工作进程等待串口读取任务结束时发生异常。", exception);
                }
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

    private static async Task ReadSerialInWorkerAsync(
        SerialPort port,
        Stream eventPipe,
        SemaphoreSlim eventWriteLock,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int count;
                try
                {
                    count = port.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException)
                {
                    continue;
                }

                if (count <= 0)
                {
                    continue;
                }

                await eventWriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await WriteFrameAsync(
                        eventPipe,
                        ReceivedDataMessage,
                        buffer.AsMemory(0, count),
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    eventWriteLock.Release();
                }
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await eventWriteLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await TryWriteWorkerErrorAsync(eventPipe, exception.Message, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                eventWriteLock.Release();
            }
        }
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
                mutexAcquired = mutex.WaitOne(TimeSpan.FromSeconds(2));
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
