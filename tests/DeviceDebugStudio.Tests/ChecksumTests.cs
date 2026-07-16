using System.Text;
using DeviceDebugStudio.Core.Protocol;

namespace DeviceDebugStudio.Tests;

public sealed class ChecksumTests
{
    private static readonly byte[] StandardVector = Encoding.ASCII.GetBytes("123456789");

    [Fact]
    public void StandardCrcVectorsMatchPublishedValues()
    {
        Assert.Equal(0xF4, ChecksumCalculator.ComputeCrc8(StandardVector));
        Assert.Equal(0x4B37, ChecksumCalculator.ComputeCrc16Modbus(StandardVector));
        Assert.Equal(0x29B1, ChecksumCalculator.ComputeCrc16Ccitt(StandardVector));
        Assert.Equal(0xCBF43926u, ChecksumCalculator.ComputeCrc32(StandardVector));
    }

    [Fact]
    public void ModbusChecksumAppendsLowByteFirst()
    {
        byte[] frame = ChecksumCalculator.Append(StandardVector, ChecksumKind.Crc16Modbus);
        Assert.Equal(0x37, frame[^2]);
        Assert.Equal(0x4B, frame[^1]);
    }

    [Theory]
    [InlineData("01 03 00 00", new byte[] { 0x01, 0x03, 0x00, 0x00 })]
    [InlineData("01-03-0A-FF", new byte[] { 0x01, 0x03, 0x0A, 0xFF })]
    [InlineData("01030aff", new byte[] { 0x01, 0x03, 0x0A, 0xFF })]
    [InlineData("0x01, 0x03, 0x0A, 0xFF", new byte[] { 0x01, 0x03, 0x0A, 0xFF })]
    public void HexParserAcceptsCommonSeparators(string text, byte[] expected)
    {
        Assert.Equal(expected, ByteText.ParseHex(text));
        Assert.Equal(string.Join(' ', expected.Select(value => value.ToString("X2"))), ByteText.ToHex(expected));
    }
}
