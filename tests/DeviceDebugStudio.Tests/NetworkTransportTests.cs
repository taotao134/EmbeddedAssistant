using System.Buffers.Binary;
using System.Diagnostics;
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
    public async Task TcpServerSupportsConcurrentClientsAndBroadcastsToEachClient()
    {
        int port = GetFreeTcpPort();
        await using TcpServerTransport server = new(new TcpServerTransportSettings
        {
            LocalAddress = "127.0.0.1",
            Port = port
        });
        List<TcpClientTransport> clients = [];
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));

        try
        {
            await server.ConnectAsync(timeout.Token);
            for (int index = 0; index < 4; index++)
            {
                clients.Add(new TcpClientTransport(new TcpClientTransportSettings
                {
                    Host = "127.0.0.1",
                    Port = port,
                    ConnectTimeoutMs = 3000
                }));
            }

            await Task.WhenAll(clients.Select(client => client.ConnectAsync(timeout.Token)));
            await WaitUntilAsync(() => server.Clients.Count == clients.Count, TimeSpan.FromSeconds(3));

            byte[][] payloads = clients
                .Select((_, index) => CreatePattern(4096, index + 1))
                .ToArray();
            Task<Dictionary<string, byte[]>> receiveTask = ReadTcpPayloadsByEndpointAsync(
                server,
                server.Clients,
                payloads[0].Length,
                timeout.Token);

            await Task.WhenAll(clients.Select((client, index) => client.SendAsync(payloads[index], cancellationToken: timeout.Token).AsTask()));
            Dictionary<string, byte[]> receivedByEndpoint = await receiveTask;

            Assert.Equal(clients.Count, receivedByEndpoint.Count);
            foreach (byte[] received in receivedByEndpoint.Values)
            {
                Assert.Contains(payloads, payload => payload.SequenceEqual(received));
            }

            byte[] broadcast = CreatePattern(2048, 99);
            Task<byte[]>[] broadcastReads = clients
                .Select(client => ReadBytesAsync(client, broadcast.Length, TimeSpan.FromSeconds(5)))
                .ToArray();
            await server.SendAsync(broadcast, cancellationToken: timeout.Token);
            byte[][] broadcastResults = await Task.WhenAll(broadcastReads);

            foreach (byte[] result in broadcastResults)
            {
                Assert.Equal(broadcast, result);
            }

            string target = server.Clients.First();
            byte[] targeted = CreatePattern(37, 123);
            Task<byte[]?>[] targetedReads = clients
                .Select(client => TryReadBytesAsync(client, targeted.Length, TimeSpan.FromSeconds(2)))
                .ToArray();
            await server.SendAsync(targeted, target, timeout.Token);
            byte[]?[] targetedResults = await Task.WhenAll(targetedReads);

            Assert.Equal(1, targetedResults.Count(result => result is not null));
            Assert.Contains(targetedResults, result => result is not null && result.SequenceEqual(targeted));
        }
        finally
        {
            foreach (TcpClientTransport client in clients)
            {
                await client.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task TcpPreservesLargePayloadAcrossStreamFragments()
    {
        int port = GetFreeTcpPort();
        await using TcpServerTransport server = new(new TcpServerTransportSettings { LocalAddress = "127.0.0.1", Port = port });
        await using TcpClientTransport client = new(new TcpClientTransportSettings { Host = "127.0.0.1", Port = port });
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));

        await server.ConnectAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await WaitUntilAsync(() => server.Clients.Count == 1, TimeSpan.FromSeconds(3));

        byte[] clientPayload = CreatePattern(384 * 1024, 17);
        Task<byte[]> serverRead = ReadBytesAsync(server, clientPayload.Length, TimeSpan.FromSeconds(10));
        await client.SendAsync(clientPayload, cancellationToken: timeout.Token);
        Assert.Equal(clientPayload, await serverRead);

        byte[] serverPayload = CreatePattern(384 * 1024, 29);
        Task<byte[]> clientRead = ReadBytesAsync(client, serverPayload.Length, TimeSpan.FromSeconds(10));
        await server.SendAsync(serverPayload, cancellationToken: timeout.Token);
        Assert.Equal(serverPayload, await clientRead);
    }

    [Fact]
    public async Task TcpClientCanDisconnectAndReconnect()
    {
        int port = GetFreeTcpPort();
        await using TcpServerTransport server = new(new TcpServerTransportSettings { LocalAddress = "127.0.0.1", Port = port });
        await using TcpClientTransport client = new(new TcpClientTransportSettings { Host = "127.0.0.1", Port = port });
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        await server.ConnectAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await WaitUntilAsync(() => server.Clients.Count == 1, TimeSpan.FromSeconds(3));

        await client.DisconnectAsync(timeout.Token);
        await WaitUntilAsync(() => server.Clients.Count == 0, TimeSpan.FromSeconds(3));
        Assert.Equal(TransportState.Disconnected, client.State);

        await client.ConnectAsync(timeout.Token);
        await WaitUntilAsync(() => server.Clients.Count == 1, TimeSpan.FromSeconds(3));
        byte[] payload = [0xA5, 0x5A, 0x00, 0xFF];
        Task<byte[]> readTask = ReadBytesAsync(server, payload.Length, TimeSpan.FromSeconds(5));
        await client.SendAsync(payload, cancellationToken: timeout.Token);
        Assert.Equal(payload, await readTask);
    }

    [Fact]
    public async Task TcpServerReportsPortConflictAndCanBindAfterRelease()
    {
        using TcpListener reservation = new(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        await using TcpServerTransport server = new(new TcpServerTransportSettings
        {
            LocalAddress = "127.0.0.1",
            Port = port
        });

        await Assert.ThrowsAnyAsync<SocketException>(() => server.ConnectAsync());
        Assert.Equal(TransportState.Faulted, server.State);

        reservation.Stop();
        await server.ConnectAsync();
        Assert.Equal(TransportState.Connected, server.State);
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

    [Fact]
    public async Task UdpPreservesLargeDatagramBoundary()
    {
        int firstPort = GetFreeUdpPort();
        int secondPort = GetFreeUdpPort();
        await using UdpTransport first = new(new UdpTransportSettings
        {
            LocalAddress = "127.0.0.1",
            LocalPort = firstPort,
            RemoteAddress = "127.0.0.1",
            RemotePort = secondPort
        });
        await using UdpTransport second = new(new UdpTransportSettings
        {
            LocalAddress = "127.0.0.1",
            LocalPort = secondPort,
            RemoteAddress = "127.0.0.1",
            RemotePort = firstPort
        });
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        await first.ConnectAsync(timeout.Token);
        await second.ConnectAsync(timeout.Token);
        byte[] payload = CreatePattern(16 * 1024, 41);
        Task<TransportPacket> readTask = ReadDataPacketAsync(second, timeout.Token);
        await first.SendAsync(payload, cancellationToken: timeout.Token);

        TransportPacket packet = await readTask;
        Assert.Equal(payload, packet.Data);
    }

    [Fact]
    public async Task UdpHandlesHighRateSequencedDatagramsWithoutCorruption()
    {
        const int packetCount = 256;
        const int packetSize = 256;
        int firstPort = GetFreeUdpPort();
        int secondPort = GetFreeUdpPort();
        await using UdpTransport first = new(new UdpTransportSettings
        {
            LocalAddress = "127.0.0.1",
            LocalPort = firstPort,
            RemoteAddress = "127.0.0.1",
            RemotePort = secondPort
        });
        await using UdpTransport second = new(new UdpTransportSettings
        {
            LocalAddress = "127.0.0.1",
            LocalPort = secondPort,
            RemoteAddress = "127.0.0.1",
            RemotePort = firstPort
        });
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));

        await first.ConnectAsync(timeout.Token);
        await second.ConnectAsync(timeout.Token);
        Task<List<byte[]>> receiveTask = ReadDatagramsAsync(second, packetCount, TimeSpan.FromSeconds(10));

        for (int batchStart = 0; batchStart < packetCount; batchStart += 16)
        {
            int batchEnd = Math.Min(batchStart + 16, packetCount);
            Task[] sends = Enumerable.Range(batchStart, batchEnd - batchStart)
                .Select(index => first.SendAsync(CreateSequencedPayload(index, packetSize), cancellationToken: timeout.Token).AsTask())
                .ToArray();
            await Task.WhenAll(sends);
        }

        List<byte[]> packets = await receiveTask;
        Assert.Equal(packetCount, packets.Count);
        HashSet<int> sequenceNumbers = [];
        foreach (byte[] packet in packets)
        {
            Assert.Equal(packetSize, packet.Length);
            int sequence = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(0, sizeof(int)));
            Assert.InRange(sequence, 0, packetCount - 1);
            Assert.True(sequenceNumbers.Add(sequence), $"收到重复 UDP 序号 {sequence}。");
            Assert.Equal(CreateSequencedPayload(sequence, packetSize), packet);
        }
    }

    [Fact]
    public async Task UdpReportsPortConflictAndCanBindAfterRelease()
    {
        using UdpClient reservation = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        await using UdpTransport transport = new(new UdpTransportSettings
        {
            LocalAddress = "127.0.0.1",
            LocalPort = port
        });

        await Assert.ThrowsAnyAsync<SocketException>(() => transport.ConnectAsync());
        Assert.Equal(TransportState.Faulted, transport.State);

        reservation.Dispose();
        await transport.ConnectAsync();
        Assert.Equal(TransportState.Connected, transport.State);
    }

    [Fact]
    public async Task NetworkTransportsRejectInvalidStateAndEndpointInput()
    {
        await using TcpClientTransport tcp = new(new TcpClientTransportSettings
        {
            Host = "127.0.0.1",
            Port = 0,
            ConnectTimeoutMs = 200
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => tcp.SendAsync(new byte[] { 0x01 }).AsTask());

        int port = GetFreeUdpPort();
        await using UdpTransport udp = new(new UdpTransportSettings
        {
            LocalAddress = "127.0.0.1",
            LocalPort = port
        });
        await udp.ConnectAsync();
        await Assert.ThrowsAsync<FormatException>(() => udp.SendAsync(new byte[] { 0x01 }, "not-an-endpoint").AsTask());
        await Assert.ThrowsAsync<FormatException>(() => udp.SendAsync(new byte[] { 0x01 }, "127.0.0.1:not-a-port").AsTask());

        await using UdpTransport invalidAddress = new(new UdpTransportSettings
        {
            LocalAddress = "not-an-ip-address",
            LocalPort = GetFreeUdpPort()
        });
        await Assert.ThrowsAsync<FormatException>(() => invalidAddress.ConnectAsync());
        Assert.Equal(TransportState.Faulted, invalidAddress.State);
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

    private static async Task<byte[]> ReadBytesAsync(ITransport transport, int expectedLength, TimeSpan timeout)
    {
        using CancellationTokenSource source = new(timeout);
        try
        {
            return await ReadBytesCoreAsync(transport, expectedLength, source.Token);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            throw new TimeoutException($"等待 {transport.DisplayName} 的 {expectedLength} 字节数据超时。");
        }
    }

    private static async Task<byte[]?> TryReadBytesAsync(ITransport transport, int expectedLength, TimeSpan timeout)
    {
        try
        {
            return await ReadBytesAsync(transport, expectedLength, timeout);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadBytesCoreAsync(ITransport transport, int expectedLength, CancellationToken cancellationToken)
    {
        List<byte> bytes = new(expectedLength);
        await foreach (TransportPacket packet in transport.ReadAllAsync(cancellationToken))
        {
            if (packet.Direction == PacketDirection.Error)
            {
                throw new InvalidOperationException(packet.Message ?? "网络传输报告错误。");
            }

            if (packet.Direction != PacketDirection.Receive || packet.Data.Length == 0)
            {
                continue;
            }

            bytes.AddRange(packet.Data);
            if (bytes.Count > expectedLength)
            {
                throw new InvalidOperationException($"收到数据超过预期长度 {expectedLength} 字节。");
            }

            if (bytes.Count == expectedLength)
            {
                return bytes.ToArray();
            }
        }

        throw new EndOfStreamException($"{transport.DisplayName} 在收到完整数据前结束。");
    }

    private static async Task<Dictionary<string, byte[]>> ReadTcpPayloadsByEndpointAsync(
        ITransport transport,
        IReadOnlyCollection<string> endpoints,
        int payloadLength,
        CancellationToken cancellationToken)
    {
        Dictionary<string, List<byte>> received = endpoints.ToDictionary(endpoint => endpoint, _ => new List<byte>(payloadLength));
        await foreach (TransportPacket packet in transport.ReadAllAsync(cancellationToken))
        {
            if (packet.Direction == PacketDirection.Error)
            {
                throw new InvalidOperationException(packet.Message ?? "TCP 服务端报告错误。");
            }

            if (packet.Direction != PacketDirection.Receive || packet.Data.Length == 0 || !received.TryGetValue(packet.Endpoint, out List<byte>? bytes))
            {
                continue;
            }

            bytes.AddRange(packet.Data);
            if (bytes.Count > payloadLength)
            {
                throw new InvalidOperationException($"TCP 端点 {packet.Endpoint} 收到数据超过预期长度。");
            }

            if (received.Values.All(value => value.Count == payloadLength))
            {
                return received.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
            }
        }

        throw new EndOfStreamException("TCP 服务端在收到全部客户端数据前结束。");
    }

    private static async Task<List<byte[]>> ReadDatagramsAsync(ITransport transport, int expectedCount, TimeSpan timeout)
    {
        using CancellationTokenSource source = new(timeout);
        List<byte[]> packets = new(expectedCount);
        try
        {
            await foreach (TransportPacket packet in transport.ReadAllAsync(source.Token))
            {
                if (packet.Direction == PacketDirection.Error)
                {
                    throw new InvalidOperationException(packet.Message ?? "UDP 接收报告错误。");
                }

                if (packet.Direction != PacketDirection.Receive || packet.Data.Length == 0)
                {
                    continue;
                }

                packets.Add(packet.Data);
                if (packets.Count == expectedCount)
                {
                    return packets;
                }
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            throw new TimeoutException($"等待 {expectedCount} 个 UDP 数据报超时，实际收到 {packets.Count} 个。");
        }

        throw new EndOfStreamException("UDP 接收通道提前结束。");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            TimeSpan remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException("等待网络状态变化超时。");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(10, remaining.TotalMilliseconds)));
        }
    }

    private static byte[] CreatePattern(int length, int seed)
    {
        byte[] data = new byte[length];
        for (int index = 0; index < data.Length; index++)
        {
            data[index] = (byte)((index * 31 + seed * 17) & 0xFF);
        }

        return data;
    }

    private static byte[] CreateSequencedPayload(int sequence, int length)
    {
        byte[] data = CreatePattern(length, sequence + 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0, sizeof(int)), sequence);
        return data;
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
