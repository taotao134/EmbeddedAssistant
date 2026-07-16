using System.IO.Ports;
using DeviceDebugStudio.Core.Transports;

namespace DeviceDebugStudio.Infrastructure.Transports;

public sealed class SerialPortTransport(SerialTransportSettings settings) : TransportBase
{
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
            WriteBufferSize = 64 * 1024
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

        if (_readTask is not null)
        {
            try
            {
                await _readTask.ConfigureAwait(false);
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
            await port.BaseStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
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
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                SerialPort? port = _serialPort;
                if (port is null)
                {
                    break;
                }
                int count = await port.BaseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count > 0)
                {
                    PublishReceived(buffer.AsSpan(0, count), settings.PortName);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportFault(new IOException($"串口 {settings.PortName} 读取失败：{exception.Message}", exception));
        }
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
