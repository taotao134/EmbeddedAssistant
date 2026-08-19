using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DeviceDebugStudio.Infrastructure.Transports;

public sealed record TftpTransferOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = 69;
    public required string LocalFile { get; init; }
    public required string RemoteFile { get; init; }
    public int BlockSize { get; init; } = 512;
    public int TimeoutMs { get; init; } = 3000;
    public int MaxRetries { get; init; } = 5;
}

public sealed record TftpTransferProgress(
    long BytesTransferred,
    long? TotalBytes,
    ushort BlockNumber,
    int BlockSize);

public sealed class TftpErrorException(int code, string message) : IOException(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// 提供 RFC 1350 TFTP 客户端上传、下载能力，并支持 RFC 2347 块大小协商。
/// </summary>
public sealed class TftpClient
{
    private const ushort ReadRequest = 1;
    private const ushort WriteRequest = 2;
    private const ushort Data = 3;
    private const ushort Acknowledgement = 4;
    private const ushort Error = 5;
    private const ushort OptionAcknowledgement = 6;
    private const int DefaultBlockSize = 512;
    private const int MinimumBlockSize = 8;
    private const int MaximumBlockSize = 65464;
    private const int InitialResponseTimeoutMs = 60_000;
    private static readonly Encoding PacketEncoding = Encoding.ASCII;

    public async Task DownloadAsync(
        TftpTransferOptions options,
        IProgress<TftpTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options, requireLocalFile: false);
        IPEndPoint serverEndpoint = await ResolveEndpointAsync(options.Host, options.Port, cancellationToken).ConfigureAwait(false);
        string outputPath = Directory.Exists(options.LocalFile)
            ? Path.Combine(options.LocalFile, options.RemoteFile)
            : options.LocalFile;
        string? directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using UdpClient client = CreateClient();
        await using FileStream stream = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        IPEndPoint transferEndpoint = serverEndpoint;
        byte[] lastPacket = BuildRequest(ReadRequest, options.RemoteFile, options.BlockSize);
        ushort expectedBlock = 1;
        int blockSize = DefaultBlockSize;
        int retryCount = 0;
        bool firstResponse = true;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await client.SendAsync(lastPacket, transferEndpoint, cancellationToken).ConfigureAwait(false);
            int receiveTimeoutMs = firstResponse
                ? Math.Max(options.TimeoutMs, InitialResponseTimeoutMs)
                : options.TimeoutMs;
            UdpReceiveResult? received = await ReceiveAsync(client, receiveTimeoutMs, cancellationToken).ConfigureAwait(false);
            if (received is null)
            {
                if (++retryCount > options.MaxRetries)
                {
                    throw new TimeoutException($"等待 TFTP 数据块 {expectedBlock} 超时。");
                }
                continue;
            }

            if (!IsSameAddress(received.Value.RemoteEndPoint, serverEndpoint))
            {
                continue;
            }

            transferEndpoint = received.Value.RemoteEndPoint;
            ReadOnlySpan<byte> packet = received.Value.Buffer;
            ushort opcode = ReadOpcode(packet);
            if (opcode == Error)
            {
                throw CreateErrorException(packet);
            }

            if (firstResponse && opcode == OptionAcknowledgement)
            {
                blockSize = ReadNegotiatedBlockSize(packet, options.BlockSize);
                lastPacket = BuildAcknowledgement(0);
                firstResponse = false;
                retryCount = 0;
                continue;
            }

            if (opcode != Data || packet.Length < 4)
            {
                continue;
            }

            firstResponse = false;
            ushort block = ReadUInt16(packet, 2);
            ushort previousBlock = unchecked((ushort)(expectedBlock - 1));
            if (block == previousBlock)
            {
                lastPacket = BuildAcknowledgement(block);
                retryCount = 0;
                continue;
            }
            if (block != expectedBlock)
            {
                continue;
            }

            int dataLength = packet.Length - 4;
            await stream.WriteAsync(received.Value.Buffer.AsMemory(4, dataLength), cancellationToken).ConfigureAwait(false);
            long transferred = stream.Length;
            progress?.Report(new TftpTransferProgress(transferred, null, block, blockSize));
            lastPacket = BuildAcknowledgement(block);
            retryCount = 0;
            if (dataLength < blockSize)
            {
                await client.SendAsync(lastPacket, transferEndpoint, cancellationToken).ConfigureAwait(false);
                progress?.Report(new TftpTransferProgress(transferred, transferred, block, blockSize));
                return;
            }

            expectedBlock = unchecked((ushort)(expectedBlock + 1));
        }
    }

    public async Task UploadAsync(
        TftpTransferOptions options,
        IProgress<TftpTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options, requireLocalFile: true);
        if (!File.Exists(options.LocalFile))
        {
            throw new FileNotFoundException("本地文件不存在。", options.LocalFile);
        }

        IPEndPoint serverEndpoint = await ResolveEndpointAsync(options.Host, options.Port, cancellationToken).ConfigureAwait(false);
        using UdpClient client = CreateClient();
        await using FileStream stream = new(options.LocalFile, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        IPEndPoint transferEndpoint = serverEndpoint;
        byte[] lastPacket = BuildRequest(WriteRequest, options.RemoteFile, options.BlockSize);
        int blockSize = DefaultBlockSize;
        int retryCount = 0;
        bool firstResponse = true;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await client.SendAsync(lastPacket, transferEndpoint, cancellationToken).ConfigureAwait(false);
            int receiveTimeoutMs = firstResponse
                ? Math.Max(options.TimeoutMs, InitialResponseTimeoutMs)
                : options.TimeoutMs;
            UdpReceiveResult? received = await ReceiveAsync(client, receiveTimeoutMs, cancellationToken).ConfigureAwait(false);
            if (received is null)
            {
                if (++retryCount > options.MaxRetries)
                {
                    throw new TimeoutException("等待 TFTP 初始响应超时。");
                }
                continue;
            }

            if (!IsSameAddress(received.Value.RemoteEndPoint, serverEndpoint))
            {
                continue;
            }

            transferEndpoint = received.Value.RemoteEndPoint;
            ReadOnlySpan<byte> packet = received.Value.Buffer;
            ushort opcode = ReadOpcode(packet);
            if (opcode == Error)
            {
                throw CreateErrorException(packet);
            }

            if (firstResponse && opcode == OptionAcknowledgement)
            {
                blockSize = ReadNegotiatedBlockSize(packet, options.BlockSize);
                lastPacket = BuildAcknowledgement(0);
                await client.SendAsync(lastPacket, transferEndpoint, cancellationToken).ConfigureAwait(false);
                firstResponse = false;
                retryCount = 0;
                break;
            }
            if (opcode == Acknowledgement && packet.Length >= 4 && ReadUInt16(packet, 2) == 0)
            {
                firstResponse = false;
                retryCount = 0;
                break;
            }
        }

        ushort blockNumber = 1;
        long totalBytes = stream.Length;
        long transferredBytes = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] data = new byte[blockSize];
            int count = await stream.ReadAsync(data.AsMemory(), cancellationToken).ConfigureAwait(false);
            lastPacket = BuildData(blockNumber, data, count);
            await SendAndWaitForAcknowledgementAsync(
                client,
                lastPacket,
                transferEndpoint,
                serverEndpoint,
                blockNumber,
                options,
                cancellationToken).ConfigureAwait(false);

            transferredBytes += count;
            progress?.Report(new TftpTransferProgress(transferredBytes, totalBytes, blockNumber, blockSize));
            if (count < blockSize)
            {
                return;
            }

            blockNumber = unchecked((ushort)(blockNumber + 1));
        }
    }

    private static async Task SendAndWaitForAcknowledgementAsync(
        UdpClient client,
        byte[] packet,
        IPEndPoint transferEndpoint,
        IPEndPoint serverEndpoint,
        ushort expectedBlock,
        TftpTransferOptions options,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await client.SendAsync(packet, transferEndpoint, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                UdpReceiveResult? received = await ReceiveAsync(client, options.TimeoutMs, cancellationToken).ConfigureAwait(false);
                if (received is null)
                {
                    break;
                }
                if (!IsSameAddress(received.Value.RemoteEndPoint, serverEndpoint))
                {
                    continue;
                }

                ReadOnlySpan<byte> response = received.Value.Buffer;
                ushort opcode = ReadOpcode(response);
                if (opcode == Error)
                {
                    throw CreateErrorException(response);
                }
                if (opcode == Acknowledgement
                    && response.Length >= 4
                    && ReadUInt16(response, 2) == expectedBlock)
                {
                    return;
                }
            }
        }

        throw new TimeoutException($"等待 TFTP ACK {expectedBlock} 超时。");
    }

    private static UdpClient CreateClient() => new(new IPEndPoint(IPAddress.Any, 0));

    private static async Task<UdpReceiveResult?> ReceiveAsync(
        UdpClient client,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeoutMs);
        try
        {
            return await client.ReceiveAsync(timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<IPEndPoint> ResolveEndpointAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out IPAddress? parsed))
        {
            if (parsed.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new NotSupportedException("TFTP 客户端当前只支持 IPv4 地址。 ");
            }
            return new IPEndPoint(parsed, port);
        }

        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        IPAddress? address = addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork);
        return address is null
            ? throw new SocketException((int)SocketError.HostNotFound)
            : new IPEndPoint(address, port);
    }

    private static void ValidateOptions(TftpTransferOptions options, bool requireLocalFile)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            throw new ArgumentException("请输入 TFTP 服务器地址。", nameof(options));
        }
        if (options.Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Port), "TFTP 端口必须在 1 到 65535 之间。 ");
        }
        if (requireLocalFile && string.IsNullOrWhiteSpace(options.LocalFile))
        {
            throw new ArgumentException("请选择本地文件。", nameof(options));
        }
        if (string.IsNullOrWhiteSpace(options.RemoteFile))
        {
            throw new ArgumentException("请输入远程文件名。", nameof(options));
        }
        if (options.BlockSize != DefaultBlockSize
            && (options.BlockSize is < MinimumBlockSize or > MaximumBlockSize))
        {
            throw new ArgumentOutOfRangeException(nameof(options.BlockSize), $"块大小必须在 {MinimumBlockSize} 到 {MaximumBlockSize} 字节之间。 ");
        }
        if (options.TimeoutMs < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options.TimeoutMs));
        }
        if (options.MaxRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxRetries));
        }
    }

    private static bool IsSameAddress(IPEndPoint candidate, IPEndPoint expected) =>
        candidate.Address.Equals(expected.Address);

    private static byte[] BuildRequest(ushort opcode, string remoteFile, int blockSize)
    {
        using MemoryStream stream = new();
        WriteUInt16(stream, opcode);
        WriteString(stream, remoteFile);
        WriteString(stream, "octet");
        if (blockSize != DefaultBlockSize)
        {
            WriteString(stream, "blksize");
            WriteString(stream, blockSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return stream.ToArray();
    }

    private static byte[] BuildAcknowledgement(ushort block) =>
        [(byte)(Acknowledgement >> 8), (byte)Acknowledgement, (byte)(block >> 8), (byte)block];

    private static byte[] BuildData(ushort block, byte[] buffer, int count)
    {
        byte[] packet = new byte[count + 4];
        packet[0] = (byte)(Data >> 8);
        packet[1] = (byte)Data;
        packet[2] = (byte)(block >> 8);
        packet[3] = (byte)block;
        Buffer.BlockCopy(buffer, 0, packet, 4, count);
        return packet;
    }

    private static ushort ReadOpcode(ReadOnlySpan<byte> packet) => packet.Length < 2
        ? (ushort)0
        : (ushort)((packet[0] << 8) | packet[1]);

    private static ushort ReadUInt16(ReadOnlySpan<byte> packet, int offset) =>
        packet.Length < offset + 2
            ? (ushort)0
            : (ushort)((packet[offset] << 8) | packet[offset + 1]);

    private static void WriteUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteString(Stream stream, string value)
    {
        byte[] bytes = PacketEncoding.GetBytes(value);
        stream.Write(bytes);
        stream.WriteByte(0);
    }

    private static int ReadNegotiatedBlockSize(ReadOnlySpan<byte> packet, int requestedBlockSize)
    {
        Dictionary<string, string> options = ParseOptions(packet);
        if (!options.TryGetValue("blksize", out string? value)
            || !int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int negotiated)
            || negotiated is < MinimumBlockSize or > MaximumBlockSize)
        {
            return DefaultBlockSize;
        }

        return Math.Min(negotiated, requestedBlockSize);
    }

    private static Dictionary<string, string> ParseOptions(ReadOnlySpan<byte> packet)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        int offset = 2;
        while (offset < packet.Length)
        {
            string? key = ReadNullTerminatedString(packet, ref offset);
            string? value = ReadNullTerminatedString(packet, ref offset);
            if (string.IsNullOrWhiteSpace(key) || value is null)
            {
                break;
            }
            result[key] = value;
        }
        return result;
    }

    private static string? ReadNullTerminatedString(ReadOnlySpan<byte> packet, ref int offset)
    {
        if (offset >= packet.Length)
        {
            return null;
        }
        int end = packet[offset..].IndexOf((byte)0);
        if (end < 0)
        {
            offset = packet.Length;
            return null;
        }
        string value = PacketEncoding.GetString(packet.Slice(offset, end));
        offset += end + 1;
        return value;
    }

    private static TftpErrorException CreateErrorException(ReadOnlySpan<byte> packet)
    {
        int code = ReadUInt16(packet, 2);
        string message = packet.Length > 4
            ? PacketEncoding.GetString(packet[4..]).TrimEnd('\0')
            : "未知错误";
        if (code == 2 && message.Contains("Only one connection at a time is supported", StringComparison.OrdinalIgnoreCase))
        {
            message = "设备当前已有未结束的 TFTP 连接，请等待上一次传输释放后再重试。";
        }

        return new TftpErrorException(code, $"TFTP 服务器错误（{code}）：{message}");
    }
}
