using System.Text.Json.Serialization;

namespace DeviceDebugStudio.Core.Transports;

public enum TransportKind
{
    Serial,
    TcpClient,
    TcpServer,
    Udp,
    BleGatt
}

public enum TransportState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Faulted
}

public enum PacketDirection
{
    Receive,
    Send,
    Information,
    Error
}

public enum SerialParity
{
    None,
    Odd,
    Even,
    Mark,
    Space
}

public enum SerialStopBits
{
    One,
    OnePointFive,
    Two
}

public enum SerialHandshake
{
    None,
    XOnXOff,
    RtsCts,
    RtsCtsXOnXOff
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(SerialTransportSettings), "serial")]
[JsonDerivedType(typeof(TcpClientTransportSettings), "tcp-client")]
[JsonDerivedType(typeof(TcpServerTransportSettings), "tcp-server")]
[JsonDerivedType(typeof(UdpTransportSettings), "udp")]
[JsonDerivedType(typeof(BleGattTransportSettings), "ble-gatt")]
public abstract record TransportSettings(TransportKind Kind)
{
    public bool AutoReconnect { get; init; }
}

public sealed record SerialTransportSettings() : TransportSettings(TransportKind.Serial)
{
    public string PortName { get; init; } = string.Empty;
    public int BaudRate { get; init; } = 115200;
    public int DataBits { get; init; } = 8;
    public SerialParity Parity { get; init; }
    public SerialStopBits StopBits { get; init; } = SerialStopBits.One;
    public SerialHandshake Handshake { get; init; }
    public bool DtrEnable { get; init; }
    public bool RtsEnable { get; init; }
}

public sealed record TcpClientTransportSettings() : TransportSettings(TransportKind.TcpClient)
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 777;
    public int ConnectTimeoutMs { get; init; } = 5000;
}

public sealed record TcpServerTransportSettings() : TransportSettings(TransportKind.TcpServer)
{
    public string LocalAddress { get; init; } = "0.0.0.0";
    public int Port { get; init; } = 777;
}

public sealed record UdpTransportSettings() : TransportSettings(TransportKind.Udp)
{
    public string LocalAddress { get; init; } = "0.0.0.0";
    public int LocalPort { get; init; } = 777;
    public string RemoteAddress { get; init; } = "127.0.0.1";
    public int RemotePort { get; init; } = 777;
    public bool EnableBroadcast { get; init; }
    public string? MulticastAddress { get; init; }
}

public sealed record BleGattTransportSettings() : TransportSettings(TransportKind.BleGatt)
{
    public ulong BluetoothAddress { get; init; }
    public string ServiceUuid { get; init; } = string.Empty;
    public string ReadCharacteristicUuid { get; init; } = string.Empty;
    public string WriteCharacteristicUuid { get; init; } = string.Empty;
    public string NotifyCharacteristicUuid { get; init; } = string.Empty;
    public bool WriteWithoutResponse { get; init; }
    public bool SubscribeOnConnect { get; init; } = true;
}

public sealed record TransportPacket(
    DateTimeOffset Timestamp,
    PacketDirection Direction,
    byte[] Data,
    string Endpoint,
    string? Message = null,
    bool? SentAsHex = null)
{
    public static TransportPacket Info(string message, string endpoint = "系统") =>
        new(DateTimeOffset.Now, PacketDirection.Information, [], endpoint, message);

    public static TransportPacket Error(string message, string endpoint = "系统") =>
        new(DateTimeOffset.Now, PacketDirection.Error, [], endpoint, message);
}

public sealed record TransportStateChangedEventArgs(
    TransportState Previous,
    TransportState Current,
    string? ErrorMessage = null);

public interface ITransport : IAsyncDisposable
{
    string DisplayName { get; }
    TransportKind Kind { get; }
    TransportState State { get; }
    event EventHandler<TransportStateChangedEventArgs>? StateChanged;
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    ValueTask SendAsync(ReadOnlyMemory<byte> data, string? target = null, CancellationToken cancellationToken = default);
    IAsyncEnumerable<TransportPacket> ReadAllAsync(CancellationToken cancellationToken = default);
}

public sealed record SerialPortInfo(string PortName, string FriendlyName, string? Vid, string? Pid)
{
    public string DisplayName => string.Equals(PortName, FriendlyName, StringComparison.OrdinalIgnoreCase)
        ? PortName
        : $"{FriendlyName} ({PortName})";
    public string CompactDisplayName => string.Equals(PortName, FriendlyName, StringComparison.OrdinalIgnoreCase)
        ? PortName
        : $"{PortName} · {FriendlyName}";
}
