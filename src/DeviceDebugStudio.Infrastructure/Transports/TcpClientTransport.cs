using System.Net.Sockets;
using DeviceDebugStudio.Core.Transports;

namespace DeviceDebugStudio.Infrastructure.Transports;

public sealed class TcpClientTransport(TcpClientTransportSettings settings) : TransportBase
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _client;
    private CancellationTokenSource? _readCancellation;
    private Task? _readTask;

    public override string DisplayName => $"TCP {settings.Host}:{settings.Port}";
    public override TransportKind Kind => TransportKind.TcpClient;

    protected override async Task OnConnectAsync(CancellationToken cancellationToken)
    {
        _client = new TcpClient { NoDelay = true };
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.ConnectTimeoutMs);
        await _client.ConnectAsync(settings.Host, settings.Port, timeout.Token).ConfigureAwait(false);
        _readCancellation = new CancellationTokenSource();
        _readTask = Task.Run(() => ReadLoopAsync(_readCancellation.Token), CancellationToken.None);
    }

    protected override async Task OnDisconnectAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? source = Interlocked.Exchange(ref _readCancellation, null);
        source?.Cancel();
        _client?.Close();
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

        _client?.Dispose();
        _client = null;
        source?.Dispose();
    }

    public override async ValueTask SendAsync(ReadOnlyMemory<byte> data, string? target = null, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        NetworkStream stream = _client?.GetStream() ?? throw new InvalidOperationException("TCP 客户端未初始化。 ");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
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
        byte[] buffer = new byte[32 * 1024];
        try
        {
            NetworkStream stream = _client?.GetStream() ?? throw new InvalidOperationException("TCP 客户端未初始化。 ");
            while (!cancellationToken.IsCancellationRequested)
            {
                int count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    throw new IOException("远端已关闭连接。 ");
                }

                PublishReceived(buffer.AsSpan(0, count), $"{settings.Host}:{settings.Port}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportFault(exception);
        }
    }
}
