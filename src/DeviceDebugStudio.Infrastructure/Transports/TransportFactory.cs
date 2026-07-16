using DeviceDebugStudio.Core.Transports;

namespace DeviceDebugStudio.Infrastructure.Transports;

public static class TransportFactory
{
    public static ITransport Create(TransportSettings settings) => settings switch
    {
        SerialTransportSettings serial => new SerialPortTransport(serial),
        TcpClientTransportSettings client => new TcpClientTransport(client),
        TcpServerTransportSettings server => new TcpServerTransport(server),
        UdpTransportSettings udp => new UdpTransport(udp),
        BleGattTransportSettings ble => new BleGattTransport(ble),
        _ => throw new NotSupportedException($"不支持传输类型 {settings.Kind}。 ")
    };
}
