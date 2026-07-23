using System.ComponentModel;
using System.IO.Ports;
using DeviceDebugStudio.Core.Transports;

namespace DeviceDebugStudio.Infrastructure.Transports;

public sealed class SerialPortTransport(SerialTransportSettings settings) : TransportBase
{
    private const int ErrorOperationAborted = 995;
    private const int OperationAbortedHResult = unchecked((int)0x800703E3);
    private const int MaximumTransientReadAbortRetries = 5;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private SerialPort? _serialPort;
    private CancellationTokenSource? _readCancellation;
    private Task? _readTask;

    public override string DisplayName => string.IsNullOrWhiteSpace(settings.PortName) ? "串口" : settings.PortName;
    public override TransportKind Kind => TransportKind.Serial;

    protected override Task OnConnectAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.PortName))
        {
            throw new InvalidOperationException("请选择串口。 ");
        }

        _serialPort = new SerialPort(settings.PortName, settings.BaudRate, MapParity(settings.Parity), settings.DataBits, MapStopBits(settings.StopBits))
        {
            Handshake = MapHandshake(settings.Handshake),
            DtrEnable = settings.DtrEnable,
            RtsEnable = settings.RtsEnable,
            ReadBufferSize = 64 * 1024,
            WriteBufferSize = 64 * 1024,
            ReadTimeout = 250,
            WriteTimeout = 1000
        };
        _serialPort.Open();
        _readCancellation = new CancellationTokenSource();
        _readTask = Task.Run(() => ReadLoopAsync(_readCancellation.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    protected override async Task OnDisconnectAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? source = Interlocked.Exchange(ref _readCancellation, null);
        source?.Cancel();
        try
        {
            _serialPort?.Close();
        }
        catch
        {
        }

        Task? readTask = Interlocked.Exchange(ref _readTask, null);
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

        _serialPort?.Dispose();
        _serialPort = null;
        source?.Dispose();
    }

    public override async ValueTask SendAsync(ReadOnlyMemory<byte> data, string? target = null, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        SerialPort port = _serialPort ?? throw new InvalidOperationException("串口未初始化。 ");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await port.BaseStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                ReportFault(new IOException($"串口 {settings.PortName} 写入失败：{exception.Message}", exception));
                throw;
            }
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

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        int transientAbortCount = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            SerialPort? port = _serialPort;
            if (port is null)
            {
                return;
            }

            try
            {
                int count = port.Read(buffer, 0, buffer.Length);
                transientAbortCount = 0;
                if (count > 0)
                {
                    PublishReceived(buffer.AsSpan(0, count), settings.PortName);
                }
            }
            // 关闭串口会中止 Windows 重叠读取，驱动可能以 IOException 而不是 OCE 返回；此时属于正常断开。
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (TimeoutException)
            {
                transientAbortCount = 0;
            }
            // CH340 等驱动偶发中止单次重叠读取，但端口仍可继续使用；有限重试后再判定为真实断线。
            catch (Exception exception) when (CanRetryReadAfterOperationAborted(port, exception, transientAbortCount))
            {
                transientAbortCount++;
                await Task.Delay(TimeSpan.FromMilliseconds(50 * transientAbortCount), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                ReportFault(new IOException($"串口 {settings.PortName} 读取失败：{exception.Message}", exception));
                return;
            }
        }
    }

    private static bool CanRetryReadAfterOperationAborted(SerialPort port, Exception exception, int transientAbortCount) =>
        transientAbortCount < MaximumTransientReadAbortRetries
        && port.IsOpen
        && IsOperationAborted(exception);

    private static bool IsOperationAborted(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException
                || current is Win32Exception { NativeErrorCode: ErrorOperationAborted }
                || current is IOException { HResult: OperationAbortedHResult })
            {
                return true;
            }
        }

        return false;
    }

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
}
