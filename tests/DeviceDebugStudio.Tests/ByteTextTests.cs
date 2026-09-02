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
    public void ExpandVariablesSupportsMultipleNamedValues()
    {
        string result = ByteText.ExpandVariables(
            "SET ${id} ${speed}",
            new Dictionary<string, string>
            {
                ["id"] = "01",
                ["speed"] = "1500"
            });

        Assert.Equal("SET 01 1500", result);
    }

    [Fact]
    public void ExpandVariablesOnlyMatchesDollarBracePlaceholders()
    {
        string result = ByteText.ExpandVariables(
            "hmt+sound_enable=&{语音编号},${开关}",
            new Dictionary<string, string>
            {
                ["语音编号"] = "1",
                ["开关"] = "0"
            });

        Assert.Equal("hmt+sound_enable=&{语音编号},0", result);
        Assert.Equal(["开关"], ByteText.GetVariableNames("&{语音编号},${开关}"));
    }

    [Fact]
    public void ExpandVariablesKeepsFixedCommandUnchangedWhenDefaultSetIsEmpty()
    {
        const string command = "AT+BAUD=115200";

        string result = ByteText.ExpandVariables(command, new Dictionary<string, string>());

        Assert.Equal(command, result);
    }

    [Fact]
    public void TryCreateParameterTemplateRecognizesDelimitedValues()
    {
        bool created = ByteText.TryCreateParameterTemplate(
            "$TEST2595,9.690000,30",
            out string template,
            out IReadOnlyList<string> values);

        Assert.True(created);
        Assert.Equal("$TEST2595,${param1},${param2}", template);
        Assert.Equal(["9.690000", "30"], values);
    }

    [Fact]
    public void TryCreateParameterTemplateRecognizesEmptyAssignmentParameters()
    {
        bool created = ByteText.TryCreateParameterTemplate(
            "kas+pid=,,,",
            out string template,
            out IReadOnlyList<string> values);

        Assert.True(created);
        Assert.Equal("kas+pid=${param1},${param2},${param3}", template);
        Assert.Equal([string.Empty, string.Empty, string.Empty], values);
    }

    [Fact]
    public void TryCreateParameterTemplatePreservesEmptyAndTrailingFields()
    {
        bool created = ByteText.TryCreateParameterTemplate(
            "cmd,first,,third,",
            out string template,
            out IReadOnlyList<string> values);

        Assert.True(created);
        Assert.Equal("cmd,${param1},${param2},${param3},${param4}", template);
        Assert.Equal(["first", string.Empty, "third", string.Empty], values);
    }

    [Fact]
    public void TryCreateParameterTemplateRejectsCommandWithoutParameters()
    {
        bool created = ByteText.TryCreateParameterTemplate(
            "AT+BAUD=115200",
            out string template,
            out IReadOnlyList<string> values);

        Assert.False(created);
        Assert.Equal("AT+BAUD=115200", template);
        Assert.Empty(values);
    }

    [Fact]
    public void ToEscapedHex_PrefixesEveryByteWithoutAmbiguity()
    {
        Assert.Equal(@"\x12\x33", ByteText.ToEscapedHex([0x12, 0x33]));
    }
}
