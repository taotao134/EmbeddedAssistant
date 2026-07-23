using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DeviceDebugStudio.Core.Transports;

namespace DeviceDebugStudio.Core.Sessions;

public interface ICaptureStore : IAsyncDisposable
{
    Task StartAsync(string sessionName, TransportKind transportKind, CancellationToken cancellationToken = default);
    ValueTask AppendAsync(TransportPacket packet, CancellationToken cancellationToken = default);
    Task CompleteAsync(CancellationToken cancellationToken = default);
}

public sealed class NullCaptureStore : ICaptureStore
{
    public Task StartAsync(string sessionName, TransportKind transportKind, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask AppendAsync(TransportPacket packet, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public Task CompleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class CommunicationSession : IAsyncDisposable
{
    private readonly ITransport _transport;
    private readonly ICaptureStore _captureStore;
    private readonly Channel<TransportPacket> _displayChannel;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _sessionCancellation;
    private Task? _receiveTask;
    private long _displayDropCount;
    private int _faultNotified;

    public CommunicationSession(string name, ITransport transport, ICaptureStore? captureStore = null, int displayCapacity = 4096)
    {
        Name = name;
        _transport = transport;
        _captureStore = captureStore ?? new NullCaptureStore();
        _displayChannel = Channel.CreateBounded<TransportPacket>(new BoundedChannelOptions(displayCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = false,
            SingleWriter = false
        });
        _transport.StateChanged += OnTransportStateChanged;
    }

    public string Name { get; }
    public ITransport Transport => _transport;
    public long DisplayDropCount => Interlocked.Read(ref _displayDropCount);
    public event EventHandler<Exception>? Faulted;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_sessionCancellation is not null)
        {
            return;
        }

        CancellationTokenSource sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken sessionToken = sessionCancellation.Token;
        _sessionCancellation = sessionCancellation;
        Interlocked.Exchange(ref _faultNotified, 0);
        await _captureStore.StartAsync(Name, _transport.Kind, cancellationToken).ConfigureAwait(false);
        await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(sessionToken), CancellationToken.None);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? source = Interlocked.Exchange(ref _sessionCancellation, null);
        if (source is null)
        {
            return;
        }

        source.Cancel();
        await _transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _captureStore.CompleteAsync(cancellationToken).ConfigureAwait(false);
        source.Dispose();
    }

    public async ValueTask SendAsync(
        ReadOnlyMemory<byte> data,
        string? target = null,
        CancellationToken cancellationToken = default,
        bool? sentAsHex = null)
    {
        if (data.IsEmpty)
        {
            throw new ArgumentException("发送数据不能为空。", nameof(data));
        }

        CancellationToken sessionToken = _sessionCancellation?.Token
            ?? throw new InvalidOperationException("连接会话未启动。 ");
        using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, cancellationToken);
        await _sendLock.WaitAsync(linkedSource.Token).ConfigureAwait(false);
        try
        {
            await _transport.SendAsync(data, target, linkedSource.Token).ConfigureAwait(false);
            TransportPacket packet = new(
                DateTimeOffset.Now,
                PacketDirection.Send,
                data.ToArray(),
                target ?? _transport.DisplayName,
                SentAsHex: sentAsHex,
                ArrivalTimestamp: Stopwatch.GetTimestamp());
            await RecordAndPublishAsync(packet, linkedSource.Token).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async IAsyncEnumerable<TransportPacket> ReadDisplayAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (TransportPacket packet in _displayChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return packet;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _transport.StateChanged -= OnTransportStateChanged;
        await _transport.DisposeAsync().ConfigureAwait(false);
        await _captureStore.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (TransportPacket packet in _transport.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await RecordAndPublishAsync(packet, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Faulted?.Invoke(this, exception);
            Publish(TransportPacket.Error(exception.Message, _transport.DisplayName));
        }
    }

    private async ValueTask RecordAndPublishAsync(TransportPacket packet, CancellationToken cancellationToken)
    {
        await _captureStore.AppendAsync(packet, cancellationToken).ConfigureAwait(false);
        Publish(packet);
    }

    private void Publish(TransportPacket packet)
    {
        if (!_displayChannel.Writer.TryWrite(packet))
        {
            Interlocked.Increment(ref _displayDropCount);
        }
    }

    private void OnTransportStateChanged(object? sender, TransportStateChangedEventArgs args)
    {
        string message = args.ErrorMessage is null
            ? $"连接状态：{args.Current}"
            : $"连接状态：{args.Current}，{args.ErrorMessage}";
        Publish(args.Current == TransportState.Faulted
            ? TransportPacket.Error(message, _transport.DisplayName)
            : TransportPacket.Info(message, _transport.DisplayName));

        if (args.Current == TransportState.Faulted
            && _sessionCancellation is { IsCancellationRequested: false }
            && Interlocked.Exchange(ref _faultNotified, 1) == 0)
        {
            Faulted?.Invoke(this, new IOException(args.ErrorMessage ?? "传输连接已故障。 "));
        }
    }
}
