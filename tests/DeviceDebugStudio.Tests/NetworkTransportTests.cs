using System.Net;
using System.Net.Sockets;
using DeviceDebugStudio.Core.Transports;
using DeviceDebugStudio.Infrastructure.Transports;

namespace DeviceDebugStudio.Tests;

public sealed class NetworkTransportTests
{
    [Fact]
    public async Task TcpClientAndServerExchangeBytes()
    {
        int port = GetFreeTcpPort();
        await using TcpServerTransport server = new(new TcpServerTransportSettings { Port = port });
        await using TcpClientTransport client = new(new TcpClientTransportSettings { Port = port });
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        await server.ConnectAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await client.SendAsync(new byte[] { 0x11, 0x22 }, cancellationToken: timeout.Token);
        TransportPacket serverPacket = await ReadDataPacketAsync(server, timeout.Token);
        Assert.Equal([0x11, 0x22], serverPacket.Data);

        await server.SendAsync(new byte[] { 0x33 }, cancellationToken: timeout.Token);
        TransportPacket clientPacket = await ReadDataPacketAsync(client, timeout.Token);
        Assert.Equal([0x33], clientPacket.Data);
    }

    [Fact]
    public async Task UdpPreservesDatagramBoundary()
    {
        int firstPort = GetFreeUdpPort();
        int secondPort = GetFreeUdpPort();
        await using UdpTransport first = new(new UdpTransportSettings { LocalPort = firstPort, RemotePort = secondPort });
        await using UdpTransport second = new(new UdpTransportSettings { LocalPort = secondPort, RemotePort = firstPort });
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        await first.ConnectAsync(timeout.Token);
        await second.ConnectAsync(timeout.Token);
        await first.SendAsync(new byte[] { 1, 2, 3, 4 }, cancellationToken: timeout.Token);
        TransportPacket packet = await ReadDataPacketAsync(second, timeout.Token);

        Assert.Equal([1, 2, 3, 4], packet.Data);
    }

    private static async Task<TransportPacket> ReadDataPacketAsync(ITransport transport, CancellationToken cancellationToken)
    {
        await foreach (TransportPacket packet in transport.ReadAllAsync(cancellationToken))
        {
            if (packet.Direction == PacketDirection.Receive && packet.Data.Length > 0)
            {
                return packet;
            }
        }
        throw new EndOfStreamException();
    }

    private static int GetFreeTcpPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static int GetFreeUdpPort()
    {
        using UdpClient client = new(0);
        return ((IPEndPoint)client.Client.LocalEndPoint!).Port;
    }
}
