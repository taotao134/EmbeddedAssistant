using DeviceDebugStudio.Core.Protocol;

namespace DeviceDebugStudio.Tests;

public sealed class ByteTextTests
{
    [Fact]
    public void ParseInput_TextMode_EncodesLiteralCharacters()
    {
        byte[] result = ByteText.ParseInput("41 54", false, System.Text.Encoding.ASCII);

        Assert.Equal([0x34, 0x31, 0x20, 0x35, 0x34], result);
    }

    [Fact]
    public void ParseInput_HexMode_ParsesByteValues()
    {
        byte[] result = ByteText.ParseInput("41 54", true, System.Text.Encoding.ASCII);

        Assert.Equal([0x41, 0x54], result);
    }

    [Fact]
    public void EscapeControlCharacters_KeepsTerminalRecordOnOneLine()
    {
        const string input = "AT\r\nvalue\t\0\u0001\u2028";

        string result = ByteText.EscapeControlCharacters(input);

        Assert.Equal(@"AT\r\nvalue\t\0\x01\u2028", result);
        Assert.DoesNotContain('\r', result);
        Assert.DoesNotContain('\n', result);
    }

    [Fact]
    public void EscapeControlCharacters_LeavesReadableTextUnchanged()
    {
        Assert.Equal("状态查询：正常", ByteText.EscapeControlCharacters("状态查询：正常"));
    }

    [Fact]
    public void ToHex_SeparatesEveryByteWithSpace()
    {
        Assert.Equal("41 54 0D 0A", ByteText.ToHex([0x41, 0x54, 0x0D, 0x0A]));
    }

    [Fact]
    public void ToEscapedHex_PrefixesEveryByteWithoutAmbiguity()
    {
        Assert.Equal(@"\x12\x33", ByteText.ToEscapedHex([0x12, 0x33]));
    }
}
