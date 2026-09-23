using System.Text;
using DeviceDebugStudio.Core.Terminal;

namespace DeviceDebugStudio.Tests;

public sealed class AnsiTerminalSessionTests
{
    [Fact]
    public void DeviceStatusProbe_ReturnsTerminalReadyResponse()
    {
        AnsiTerminalSession session = new();

        AnsiTerminalFeedResult result = session.Feed([0x1B, 0x5B, 0x35, 0x6E]);

        byte[] response = Assert.Single(result.Responses);
        Assert.Equal([0x1B, 0x5B, 0x30, 0x6E], response);
        Assert.False(result.Changed);
    }

    [Fact]
    public void DeviceStatusProbe_CanBeSplitAcrossPackets()
    {
        AnsiTerminalSession session = new();

        Assert.Empty(session.Feed([0x1B, 0x5B]).Responses);
        AnsiTerminalFeedResult result = session.Feed([0x35, 0x6E]);

        Assert.Equal([0x1B, 0x5B, 0x30, 0x6E], Assert.Single(result.Responses));
    }

    [Fact]
    public void CursorMovement_OverwritesExistingText()
    {
        AnsiTerminalSession session = new();
        session.Feed(Encoding.ASCII.GetBytes("abc\x1B[2DX"));

        AnsiTerminalCell[] cells = Assert.Single(session.GetSnapshot().Lines).Cells.ToArray();

        Assert.Equal("aXc", new string(cells.Select(cell => cell.Character).ToArray()));
    }

    [Fact]
    public void EraseLine_RemovesVisibleCharacters()
    {
        AnsiTerminalSession session = new();
        session.Feed(Encoding.ASCII.GetBytes("abc\x1B[2K"));

        AnsiTerminalLine line = Assert.Single(session.GetSnapshot().Lines);

        Assert.All(line.Cells, cell => Assert.Equal(' ', cell.Character));
    }

    [Fact]
    public void SgrColor_IsStoredOnFollowingCharacters()
    {
        AnsiTerminalSession session = new();
        session.Feed(Encoding.ASCII.GetBytes("\x1B[31mR\x1B[0mN"));

        AnsiTerminalCell[] cells = Assert.Single(session.GetSnapshot().Lines).Cells.ToArray();

        Assert.Equal('R', cells[0].Character);
        Assert.Equal(AnsiTerminalColor.Red, cells[0].Foreground);
        Assert.Equal('N', cells[1].Character);
        Assert.Equal(AnsiTerminalColor.Default, cells[1].Foreground);
    }

    [Fact]
    public void CursorPositionProbe_ReturnsOneBasedPosition()
    {
        AnsiTerminalSession session = new();
        session.Feed(Encoding.ASCII.GetBytes("ab"));

        AnsiTerminalFeedResult result = session.Feed([0x1B, 0x5B, 0x36, 0x6E]);

        Assert.Equal(Encoding.ASCII.GetBytes("\x1B[1;3R"), Assert.Single(result.Responses));
    }

    [Fact]
    public void Utf8Character_CanBeSplitAcrossPackets()
    {
        AnsiTerminalSession session = new();
        byte[] encoded = Encoding.UTF8.GetBytes("中");

        session.Feed(encoded.AsSpan(0, 1));
        session.Feed(encoded.AsSpan(1));

        AnsiTerminalCell cell = Assert.Single(session.GetSnapshot().Lines).Cells[0];
        Assert.Equal('中', cell.Character);
    }
}
