using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DeviceDebugStudio.Core.Transports;

namespace DeviceDebugStudio.Infrastructure.Transports;

public abstract class TransportBase : ITransport
{
    private readonly Channel<TransportPacket> _incoming = Channel.CreateUnbounded<TransportPacket>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });
    private TransportState _state = TransportState.Disconnected;
    private int _disposed;

    public abstract string DisplayName { get; }
    public abstract TransportKind Kind { get; }
    public TransportState State => _state;
    public event EventHandler<TransportStateChangedEventArgs>? StateChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_state is TransportState.Connected or TransportState.Connecting)
        {
            return;
        }

        SetState(TransportState.Connecting);
        try
        {
            await OnConnectAsync(cancellationToken).ConfigureAwait(false);
            SetState(TransportState.Connected);
        }
        catch (Exception exception)
        {
            SetState(TransportState.Faulted, exception.Message);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_state is TransportState.Disconnected or TransportState.Disconnecting)
        {
            return;
        }

        SetState(TransportState.Disconnecting);
        try
        {
            await OnDisconnectAsync(cancellationToken).ConfigureAwait(false);
            SetState(TransportState.Disconnected);
        }
        catch (Exception exception)
        {
            SetState(TransportState.Faulted, exception.Message);
            throw;
        }
    }

    public abstract ValueTask SendAsync(ReadOnlyMemory<byte> data, string? target = null, CancellationToken cancellationToken = default);

    public async IAsyncEnumerable<TransportPacket> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (TransportPacket packet in _incoming.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return packet;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        _incoming.Writer.TryComplete();
        await OnDisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    protected abstract Task OnConnectAsync(CancellationToken cancellationToken);
    protected abstract Task OnDisconnectAsync(CancellationToken cancellationToken);
    protected virtual ValueTask OnDisposeAsync() => ValueTask.CompletedTask;

    protected void PublishReceived(ReadOnlySpan<byte> data, string endpoint) =>
        Publish(new TransportPacket(DateTimeOffset.Now, PacketDirection.Receive, data.ToArray(), endpoint));

    protected void Publish(TransportPacket packet)
    {
        _incoming.Writer.TryWrite(packet);
    }

    protected void ReportFault(Exception exception)
    {
        SetState(TransportState.Faulted, exception.Message);
        Publish(TransportPacket.Error(exception.Message, DisplayName));
    }

    protected void EnsureConnected()
    {
        if (_state != TransportState.Connected)
        {
            throw new InvalidOperationException($"{DisplayName} 尚未连接。 ");
        }
    }

    private void SetState(TransportState state, string? error = null)
    {
        TransportState previous = _state;
        _state = state;
        StateChanged?.Invoke(this, new TransportStateChangedEventArgs(previous, state, error));
    }
}
