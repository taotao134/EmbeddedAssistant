using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DeviceDebugStudio.Core.Transports;

namespace DeviceDebugStudio.Core.Sessions;

public interface ICaptureStore : IAsyncDisposable
{
    long DroppedPacketCount { get; }
    Task StartAsync(string sessionName, TransportKind transportKind, CancellationToken cancellationToken = default);
    ValueTask AppendAsync(TransportPacket packet, CancellationToken cancellationToken = default);
    Task CompleteAsync(CancellationToken cancellationToken = default);
}

public sealed class NullCaptureStore : ICaptureStore
{
    public long DroppedPacketCount => 0;
    public Task StartAsync(string sessionName, TransportKind transportKind, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask AppendAsync(TransportPacket packet, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public Task CompleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class CommunicationSession : IAsyncDisposable
{
    private readonly ITransport _transport;
    private ICaptureStore _captureStore;
    private readonly Channel<TransportPacket> _displayChannel;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _captureLock = new(1, 1);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _disposeGate = new();
    private CancellationTokenSource? _sessionCancellation;
    private Task? _receiveTask;
    private Task? _disposeTask;
    private long _displayDropCount;
    private long _captureDropCount;
    private int _faultNotified;
    private bool _captureStarted;
    private int _disposed;

    public CommunicationSession(string name, ITransport transport, ICaptureStore? captureStore = null, int displayCapacity = 4096)
    {
        Name = name;
        _transport = transport;
        _captureStore = captureStore ?? new NullCaptureStore();
        _displayChannel = Channel.CreateBounded<TransportPacket>(new BoundedChannelOptions(displayCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
        _transport.StateChanged += OnTransportStateChanged;
    }

    public string Name { get; }
    public ITransport Transport => _transport;
    public long DisplayDropCount => Interlocked.Read(ref _displayDropCount);
    public long CaptureDropCount => Interlocked.Read(ref _captureDropCount) + _captureStore.DroppedPacketCount;
    public bool IsCapturing => Volatile.Read(ref _captureStarted);
    public event EventHandler<Exception>? Faulted;

    public async Task ConnectAsync(
        CancellationToken cancellationToken = default,
        IEnumerable<TransportPacket>? captureHistory = null)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_sessionCancellation is not null)
            {
                return;
            }

            await ConnectCoreAsync(cancellationToken, captureHistory).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task ConnectCoreAsync(
        CancellationToken cancellationToken,
        IEnumerable<TransportPacket>? captureHistory)
    {
        CancellationTokenSource sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken sessionToken = sessionCancellation.Token;
        _sessionCancellation = sessionCancellation;
        Interlocked.Exchange(ref _faultNotified, 0);
        try
        {
            await StartInitialCaptureAsync(captureHistory, sessionToken).ConfigureAwait(false);
            await _transport.ConnectAsync(sessionToken).ConfigureAwait(false);
            sessionToken.ThrowIfCancellationRequested();
            _receiveTask = Task.Run(() => ReceiveLoopAsync(sessionToken), CancellationToken.None);
        }
        catch
        {
            try
            {
                await _transport.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // 保留连接失败的原始异常；销毁会话时仍会尝试释放传输资源。
            }
            Interlocked.CompareExchange(ref _sessionCancellation, null, sessionCancellation);
            try
            {
                await StopCaptureAsync().ConfigureAwait(false);
            }
            catch
            {
                // 保留连接失败的原始异常；捕获存储会在会话销毁时再次尝试释放。
            }
            sessionCancellation.Dispose();
            throw;
        }
    }

    public async Task StartCaptureAsync(
        ICaptureStore captureStore,
        IEnumerable<TransportPacket>? history = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(captureStore);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_sessionCancellation is null)
        {
            throw new InvalidOperationException("连接会话未启动。 ");
        }

        await _captureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_sessionCancellation is null)
            {
                throw new InvalidOperationException("连接会话未启动。 ");
            }

            if (_captureStarted)
            {
                if (!ReferenceEquals(captureStore, _captureStore))
                {
                    await captureStore.DisposeAsync().ConfigureAwait(false);
                }
                return;
            }

            try
            {
                await captureStore.StartAsync(Name, _transport.Kind, cancellationToken).ConfigureAwait(false);
                if (history is not null)
                {
                    foreach (TransportPacket packet in history)
                    {
                        await captureStore.AppendAsync(packet, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                try
                {
                    await captureStore.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 保留启动或历史写入失败的原始异常。
                }
                throw;
            }

            _captureStore = captureStore;
            Volatile.Write(ref _captureStarted, true);
        }
        finally
        {
            _captureLock.Release();
        }
    }

    public async Task StopCaptureAsync(CancellationToken cancellationToken = default)
    {
        await _captureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_captureStarted)
            {
                return;
            }

            ICaptureStore captureStore = _captureStore;
            _captureStore = new NullCaptureStore();
            Volatile.Write(ref _captureStarted, false);
            Interlocked.Add(ref _captureDropCount, captureStore.DroppedPacketCount);
            await CompleteAndDisposeCaptureStoreAsync(captureStore, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _captureLock.Release();
        }
    }

    public async Task AppendCaptureHistoryAsync(
        IEnumerable<TransportPacket> history,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        await _captureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_captureStarted)
            {
                return;
            }

            foreach (TransportPacket packet in history)
            {
                await _captureStore.AppendAsync(packet, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _captureLock.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        // 先取消正在进行的连接/接收，再等待生命周期锁，避免断开调用被长时间连接阻塞。
        CancelSessionWithoutThrow();
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? source = Interlocked.Exchange(ref _sessionCancellation, null);
        if (source is null)
        {
            await StopCaptureAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        Exception? disconnectFailure = null;
        try
        {
            try
            {
                source.Cancel();
            }
            catch (Exception exception)
            {
                disconnectFailure ??= exception;
            }
            try
            {
                await _transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                disconnectFailure ??= exception;
            }

            if (_receiveTask is not null)
            {
                try
                {
                    await _receiveTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    disconnectFailure ??= exception;
                }
            }

            try
            {
                // 传输断开即使被调用方取消，捕获文件也必须完成并释放句柄。
                await StopCaptureAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                disconnectFailure ??= exception;
            }
            _receiveTask = null;
        }

        finally
        {
            source.Dispose();
        }

        if (disconnectFailure is not null)
        {
            ExceptionDispatchInfo.Capture(disconnectFailure).Throw();
        }
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
        Task disposeTask;
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            disposeTask = _disposeTask;
        }
        await disposeTask.ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        CancelSessionWithoutThrow();
        Exception? disposeFailure = null;
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                await DisconnectCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                disposeFailure = exception;
            }

            _transport.StateChanged -= OnTransportStateChanged;
            try
            {
                await _transport.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                disposeFailure ??= exception;
            }

            await _captureLock.WaitAsync().ConfigureAwait(false);
            try
            {
                ICaptureStore captureStore = _captureStore;
                _captureStore = new NullCaptureStore();
                Volatile.Write(ref _captureStarted, false);
                try
                {
                    await CompleteAndDisposeCaptureStoreAsync(captureStore, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    disposeFailure ??= exception;
                }
            }
            finally
            {
                _captureLock.Release();
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }

        if (disposeFailure is not null)
        {
            ExceptionDispatchInfo.Capture(disposeFailure).Throw();
        }
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
        await _captureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_captureStarted)
            {
                await _captureStore.AppendAsync(packet, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _captureLock.Release();
        }
        Publish(packet);
    }

    private async Task StartInitialCaptureAsync(
        IEnumerable<TransportPacket>? history,
        CancellationToken cancellationToken)
    {
        await _captureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_captureStarted || _captureStore is NullCaptureStore)
            {
                return;
            }

            try
            {
                await _captureStore.StartAsync(Name, _transport.Kind, cancellationToken).ConfigureAwait(false);
                if (history is not null)
                {
                    foreach (TransportPacket packet in history)
                    {
                        await _captureStore.AppendAsync(packet, cancellationToken).ConfigureAwait(false);
                    }
                }
                Volatile.Write(ref _captureStarted, true);
            }
            catch
            {
                try
                {
                    await _captureStore.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 保留初始化失败的原始异常，避免清理异常掩盖根因。
                }
                _captureStore = new NullCaptureStore();
                Volatile.Write(ref _captureStarted, false);
                throw;
            }
        }
        finally
        {
            _captureLock.Release();
        }
    }

    private static async Task CompleteAndDisposeCaptureStoreAsync(
        ICaptureStore captureStore,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await captureStore.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            await captureStore.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private void CancelSessionWithoutThrow()
    {
        try
        {
            Volatile.Read(ref _sessionCancellation)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 另一个断开调用已经完成取消并正在释放令牌源。
        }
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
