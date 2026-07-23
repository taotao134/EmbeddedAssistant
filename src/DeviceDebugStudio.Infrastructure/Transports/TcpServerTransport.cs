using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DeviceDebugStudio.Core.Transports;

namespace DeviceDebugStudio.Infrastructure.Transports;

public sealed class TcpServerTransport(TcpServerTransportSettings settings) : TransportBase
{
    private readonly ConcurrentDictionary<string, TcpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _clientTasks = new(StringComparer.OrdinalIgnoreCase);
    private TcpListener? _listener;
    private CancellationTokenSource? _serverCancellation;
    private Task? _acceptTask;

    public override string DisplayName => $"TCP Server {settings.LocalAddress}:{settings.Port}";
    public override TransportKind Kind => TransportKind.TcpServer;
    public IReadOnlyCollection<string> Clients => _clients.Keys.ToArray();

    protected override Task OnConnectAsync(CancellationToken cancellationToken)
    {
        IPAddress address = ResolveLocalAddress(settings.LocalAddress);
        _listener = new TcpListener(address, settings.Port);
        _listener.Start();
        _serverCancellation = new CancellationTokenSource();
        _acceptTask = Task.Run(() => AcceptLoopAsync(_serverCancellation.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    protected override async Task OnDisconnectAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? source = Interlocked.Exchange(ref _serverCancellation, null);
        source?.Cancel();
        _listener?.Stop();
        foreach (TcpClient client in _clients.Values)
        {
            client.Close();
        }

        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await Task.WhenAll(_clientTasks.Values).ConfigureAwait(false);
        _clients.Clear();
        _clientTasks.Clear();
        _listener = null;
        source?.Dispose();
    }

    public override async ValueTask SendAsync(ReadOnlyMemory<byte> data, string? target = null, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        TcpClient[] clients = string.IsNullOrWhiteSpace(target)
            ? _clients.Values.ToArray()
            : _clients.TryGetValue(target, out TcpClient? selectedClient) ? [selectedClient] : [];
        if (clients.Length == 0)
        {
            throw new InvalidOperationException("TCP Server 当前没有可发送的客户端。 ");
        }

        foreach (TcpClient targetClient in clients)
        {
            await targetClient.GetStream().WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await (_listener ?? throw new InvalidOperationException()).AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                client.NoDelay = true;
                string endpoint = client.Client.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString("N");
                _clients[endpoint] = client;
                Publish(TransportPacket.Info($"客户端已连接：{endpoint}", DisplayName));
                _clientTasks[endpoint] = Task.Run(() => ClientReadLoopAsync(endpoint, client, cancellationToken), CancellationToken.None);
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReportFault(exception);
        }
    }

    private async Task ClientReadLoopAsync(string endpoint, TcpClient client, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[32 * 1024];
        try
        {
            NetworkStream stream = client.GetStream();
            while (!cancellationToken.IsCancellationRequested)
            {
                int count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                PublishReceived(buffer.AsSpan(0, count), endpoint);
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Publish(TransportPacket.Error($"客户端 {endpoint}：{exception.Message}", DisplayName));
        }
        finally
        {
            _clients.TryRemove(endpoint, out _);
            _clientTasks.TryRemove(endpoint, out _);
            client.Dispose();
            Publish(TransportPacket.Info($"客户端已断开：{endpoint}", DisplayName));
        }
    }

    private static IPAddress ResolveLocalAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "0.0.0.0")
        {
            return IPAddress.Any;
        }

        if (value == "::")
        {
            return IPAddress.IPv6Any;
        }

        return IPAddress.Parse(value);
    }
}
