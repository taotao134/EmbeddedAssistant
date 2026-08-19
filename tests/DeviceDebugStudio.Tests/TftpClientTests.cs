using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DeviceDebugStudio.App.ViewModels;
using DeviceDebugStudio.Core.Profiles;
using DeviceDebugStudio.Infrastructure.Transports;

namespace DeviceDebugStudio.Tests;

public sealed class TftpClientTests
{
    [Fact]
    public async Task DownloadUsesDynamicTransferPortAndNegotiatesBlockSize()
    {
        int port = GetFreeUdpPort();
        byte[] expected = CreatePattern(1500, 17);
        string localFile = Path.Combine(Path.GetTempPath(), $"tftp-download-{Guid.NewGuid():N}.bin");
        using UdpClient requestSocket = new(new IPEndPoint(IPAddress.Loopback, port));
        Task serverTask = Task.Run(async () =>
        {
            UdpReceiveResult request = await requestSocket.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, ReadUInt16(request.Buffer, 0));
            Assert.Contains("blksize\0" + "1024\0", Encoding.ASCII.GetString(request.Buffer));

            using UdpClient transferSocket = new(new IPEndPoint(IPAddress.Loopback, 0));
            await transferSocket.SendAsync(BuildOptionAcknowledgement(1024), request.RemoteEndPoint);
            UdpReceiveResult ack0 = await transferSocket.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, ReadUInt16(ack0.Buffer, 2));

            await transferSocket.SendAsync(BuildData(1, expected.AsSpan(0, 1024)), request.RemoteEndPoint);
            UdpReceiveResult ack1 = await transferSocket.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, ReadUInt16(ack1.Buffer, 2));
            await transferSocket.SendAsync(BuildData(2, expected.AsSpan(1024)), request.RemoteEndPoint);
            UdpReceiveResult ack2 = await transferSocket.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, ReadUInt16(ack2.Buffer, 2));
            Assert.Equal(IPAddress.Loopback, ack0.RemoteEndPoint.Address);
        });

        try
        {
            TftpClient client = new();
            await client.DownloadAsync(new TftpTransferOptions
            {
                Host = "127.0.0.1",
                Port = port,
                LocalFile = localFile,
                RemoteFile = "image.bin",
                BlockSize = 1024,
                TimeoutMs = 500,
                MaxRetries = 2
            });
            await serverTask;
            Assert.Equal(expected, await File.ReadAllBytesAsync(localFile));
        }
        finally
        {
            requestSocket.Dispose();
            if (File.Exists(localFile))
            {
                File.Delete(localFile);
            }
        }
    }

    [Fact]
    public async Task UploadSendsFinalShortBlockAfterOptionAcknowledgement()
    {
        int port = GetFreeUdpPort();
        byte[] source = CreatePattern(1500, 23);
        string localFile = Path.Combine(Path.GetTempPath(), $"tftp-upload-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(localFile, source);
        using UdpClient requestSocket = new(new IPEndPoint(IPAddress.Loopback, port));
        Task<byte[]> serverTask = Task.Run(async () =>
        {
            UdpReceiveResult request = await requestSocket.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, ReadUInt16(request.Buffer, 0));
            Assert.Contains("blksize\0" + "1024\0", Encoding.ASCII.GetString(request.Buffer));

            using UdpClient transferSocket = new(new IPEndPoint(IPAddress.Loopback, 0));
            await transferSocket.SendAsync(BuildOptionAcknowledgement(1024), request.RemoteEndPoint);
            UdpReceiveResult ack0 = await transferSocket.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(4, ReadUInt16(ack0.Buffer, 0));
            Assert.Equal(0, ReadUInt16(ack0.Buffer, 2));

            using MemoryStream received = new();
            for (ushort block = 1; ; block++)
            {
                UdpReceiveResult data = await transferSocket.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(3, ReadUInt16(data.Buffer, 0));
                Assert.Equal(block, ReadUInt16(data.Buffer, 2));
                received.Write(data.Buffer, 4, data.Buffer.Length - 4);
                await transferSocket.SendAsync(BuildAcknowledgement(block), request.RemoteEndPoint);
                if (data.Buffer.Length - 4 < 1024)
                {
                    return received.ToArray();
                }
            }
        });

        try
        {
            TftpClient client = new();
            await client.UploadAsync(new TftpTransferOptions
            {
                Host = "127.0.0.1",
                Port = port,
                LocalFile = localFile,
                RemoteFile = "image.bin",
                BlockSize = 1024,
                TimeoutMs = 500,
                MaxRetries = 2
            });
            Assert.Equal(source, await serverTask);
        }
        finally
        {
            requestSocket.Dispose();
            if (File.Exists(localFile))
            {
                File.Delete(localFile);
            }
        }
    }

    [Fact]
    public async Task UploadWaitsForDelayedInitialAcknowledgementWithoutResendingWriteRequest()
    {
        int port = GetFreeUdpPort();
        byte[] source = CreatePattern(17, 41);
        string localFile = Path.Combine(Path.GetTempPath(), $"tftp-delayed-upload-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(localFile, source);
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, port));
        Task<byte[]> serverTask = Task.Run(async () =>
        {
            UdpReceiveResult request = await server.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, ReadUInt16(request.Buffer, 0));

            await Task.Delay(300);
            await server.SendAsync(BuildAcknowledgement(0), request.RemoteEndPoint);

            UdpReceiveResult data = await server.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, ReadUInt16(data.Buffer, 0));
            Assert.Equal(1, ReadUInt16(data.Buffer, 2));
            byte[] received = data.Buffer[4..];
            await server.SendAsync(BuildAcknowledgement(1), request.RemoteEndPoint);
            return received;
        });

        try
        {
            TftpClient client = new();
            await client.UploadAsync(new TftpTransferOptions
            {
                Host = "127.0.0.1",
                Port = port,
                LocalFile = localFile,
                RemoteFile = "image.bin",
                BlockSize = 512,
                TimeoutMs = 50,
                MaxRetries = 1
            });
            Assert.Equal(source, await serverTask);
        }
        finally
        {
            server.Dispose();
            if (File.Exists(localFile))
            {
                File.Delete(localFile);
            }
        }
    }

    [Fact]
    public void TftpPreferencesRoundTripPreservesLocalDirectory()
    {
        TftpClientViewModel client = new();
        client.Host = "192.168.0.100";
        client.Port = 69;
        client.SetLocalDirectory(@"D:\firmware");
        client.SetLocalFile(@"D:\firmware\KA_APP.bin");

        TftpPreferences preferences = client.CreatePreferences();
        Assert.Equal(@"D:\firmware", preferences.LocalDirectory);
        Assert.Equal(@"D:\firmware\KA_APP.bin", preferences.LocalFile);

        TftpClientViewModel restored = new();
        restored.ApplyPreferences(preferences);
        Assert.Equal(@"D:\firmware", restored.LocalDirectory);
        Assert.Equal(@"D:\firmware\KA_APP.bin", restored.LocalFile);
    }

    private static byte[] BuildOptionAcknowledgement(int blockSize) =>
        [0, 6, .. Encoding.ASCII.GetBytes($"blksize\0{blockSize}\0")];

    private static byte[] BuildAcknowledgement(ushort block) =>
        [0, 4, (byte)(block >> 8), (byte)block];

    private static byte[] BuildData(ushort block, ReadOnlySpan<byte> data) =>
        [0, 3, (byte)(block >> 8), (byte)block, .. data.ToArray()];

    private static ushort ReadUInt16(byte[] packet, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, 2));

    private static byte[] CreatePattern(int length, int seed)
    {
        byte[] result = new byte[length];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = (byte)((index * 31 + seed) & 0xFF);
        }
        return result;
    }

    private static int GetFreeUdpPort()
    {
        using UdpClient client = new(0);
        return ((IPEndPoint)client.Client.LocalEndPoint!).Port;
    }
}
