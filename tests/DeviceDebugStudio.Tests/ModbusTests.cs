using DeviceDebugStudio.Core.Protocol;

namespace DeviceDebugStudio.Tests;

public sealed class ModbusTests
{
    [Fact]
    public void BuildsKnownReadHoldingRegistersRtuRequest()
    {
        byte[] request = ModbusRequestBuilder.BuildRtu(0x01, 0x03, 0x0000, 0x000A);

        Assert.Equal([0x01, 0x03, 0x00, 0x00, 0x00, 0x0A, 0xC5, 0xCD], request);
    }

    [Fact]
    public void BuildsModbusTcpHeaderAndPdu()
    {
        byte[] request = ModbusRequestBuilder.BuildTcp(0x1234, 0x01, 0x06, 0x0010, 0x002A);

        Assert.Equal([0x12, 0x34, 0x00, 0x00, 0x00, 0x06, 0x01, 0x06, 0x00, 0x10, 0x00, 0x2A], request);
    }

    [Fact]
    public void RtuSlaveReadsAndWritesHoldingRegisters()
    {
        ModbusSlaveSimulator simulator = new() { UnitId = 1 };
        simulator.SetHoldingRegister(0, 0x1234);

        byte[] readResponse = Assert.IsType<byte[]>(simulator.ProcessRtu(ModbusRequestBuilder.BuildRtu(1, 3, 0, 1)));
        Assert.Equal([0x01, 0x03, 0x02, 0x12, 0x34], readResponse[..5]);
        Assert.Equal(ChecksumCalculator.ComputeCrc16Modbus(readResponse[..^2]), System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(readResponse[^2..]));

        byte[] writeRequest = ModbusRequestBuilder.BuildRtu(1, 6, 0, 0x5678);
        Assert.NotNull(simulator.ProcessRtu(writeRequest));
        Assert.Equal(0x5678, simulator.GetHoldingRegister(0));
    }

    [Fact]
    public void TcpSlavePreservesTransactionId()
    {
        ModbusSlaveSimulator simulator = new() { UnitId = 1 };
        byte[] request = ModbusRequestBuilder.BuildTcp(0x4321, 1, 6, 4, 9);

        byte[] response = Assert.IsType<byte[]>(simulator.ProcessTcp(request));

        Assert.Equal(0x43, response[0]);
        Assert.Equal(0x21, response[1]);
        Assert.Equal((ushort)9, simulator.GetHoldingRegister(4));
    }
}
