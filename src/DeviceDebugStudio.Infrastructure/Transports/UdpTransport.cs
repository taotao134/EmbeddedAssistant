using System.Net;
using System.Net.Sockets;
using DeviceDebugStudio.Core.Transports;

namespace DeviceDebugStudio.Infrastructure.Transports;

public sealed class UdpTransport(UdpTransportSettings settings) : TransportBase
{
    private UdpClient? _client;
    private CancellationTokenSource? _receiveCancellation;
    private Task? _receiveTask;

    public override string DisplayName => $"UDP {settings.LocalAddress}:{settings.LocalPort}";
    public override TransportKind Kind => TransportKind.Udp;

    protected override Task OnConnectAsync(CancellationToken cancellationToken)
    {
        IPAddress localAddress = string.IsNullOrWhiteSpace(settings.LocalAddress) || settings.LocalAddress == "0.0.0.0"
            ? IPAddress.Any
            : IPAddress.Parse(settings.LocalAddress);
        _client = new UdpClient(new IPEndPoint(localAddress, settings.LocalPort));
        _client.EnableBroadcast = settings.EnableBroadcast;
        if (!string.IsNullOrWhiteSpace(settings.MulticastAddress))
        {
            _client.JoinMulticastGroup(IPAddress.Parse(settings.MulticastAddress));
        }

        _receiveCancellation = new CancellationTokenSource();
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCancellation.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    protected override async Task OnDisconnectAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? source = Interlocked.Exchange(ref _receiveCancellation, null);
        source?.Cancel();
        _client?.Close();
        Task? receiveTask = Interlocked.Exchange(ref _receiveTask, null);
        if (receiveTask is not null)
        {
            try
            {
                await receiveTask.ConfigureAwait(false);
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
        IPEndPoint endpoint = target is null ? await ResolveEndpointAsync(settings.RemoteAddress, settings.RemotePort, cancellationToken).ConfigureAwait(false) : ParseEndpoint(target);
        await (_client ?? throw new InvalidOperationException("UDP 未初始化。 ")).SendAsync(data, endpoint, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult result = await (_client ?? throw new InvalidOperationException()).ReceiveAsync(cancellationToken).ConfigureAwait(false);
                PublishReceived(result.Buffer, result.RemoteEndPoint.ToString());
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

    private static async Task<IPEndPoint> ResolveEndpointAsync(string host, int port, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out IPAddress? address))
        {
            return new IPEndPoint(address, port);
        }

        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        return new IPEndPoint(addresses.First(), port);
    }

    private static IPEndPoint ParseEndpoint(string value)
    {
        int separator = value.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(value[(separator + 1)..], out int port))
        {
            throw new FormatException("UDP 目标格式应为 地址:端口。 ");
        }

        return new IPEndPoint(IPAddress.Parse(value[..separator]), port);
    }
}
