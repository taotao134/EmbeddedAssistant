using System.Text;
using DeviceDebugStudio.App.ViewModels;
using DeviceDebugStudio.Core.Transports;

namespace DeviceDebugStudio.Tests;

public sealed class TerminalRecordItemTests
{
    private static readonly TerminalRecordItem ReceiveRecord = new(
        new DateTimeOffset(2026, 7, 22, 14, 35, 48, 123, TimeSpan.FromHours(8)),
        PacketDirection.Receive,
        "COM8",
        98,
        Encoding.UTF8.GetBytes("温度=26.5"),
        "温度=26.5",
        false);

    [Theory]
    [InlineData("14:35:48.123")]
    [InlineData("2026-07-22")]
    [InlineData("98")]
    [InlineData("温度")]
    [InlineData("COM8")]
    [InlineData("收")]
    [InlineData("接收")]
    [InlineData("RX")]
    public void MatchesSearch_FindsEveryVisibleReceiveField(string query)
    {
        Assert.True(ReceiveRecord.MatchesSearch(query, displayHex: false));
    }

    [Theory]
    [InlineData("发")]
    [InlineData("发送")]
    [InlineData("TX")]
    public void MatchesSearch_FindsSendDirectionAliases(string query)
    {
        TerminalRecordItem record = ReceiveRecord with { Direction = PacketDirection.Send };

        Assert.True(record.MatchesSearch(query, displayHex: false));
    }

    [Fact]
    public void MatchesSearch_UsesCurrentHexDisplayContent()
    {
        Assert.True(ReceiveRecord.MatchesSearch("E6 B8", displayHex: true));
        Assert.False(ReceiveRecord.MatchesSearch("E6 B8", displayHex: false));
    }

    [Fact]
    public void MatchesSearch_RejectsUnrelatedText()
    {
        Assert.False(ReceiveRecord.MatchesSearch("不存在的内容", displayHex: false));
    }

    [Fact]
    public void SeparatorRecord_FormatsGapAndParticipatesInSearch()
    {
        TerminalRecordItem separator = TerminalRecordItem.CreateSeparator(
            ReceiveRecord.Timestamp,
            TimeSpan.FromMilliseconds(1234.4));

        Assert.True(separator.IsSeparator);
        Assert.Equal("间隔 1,234 ms", separator.SeparatorGapText);
        Assert.Equal(string.Empty, separator.TimeText);
        Assert.True(separator.MatchesSearch("1,234", displayHex: false));
    }

    [Fact]
    public void WaveSeparatorTracker_UsesEndOfPreviousReceiveAndStrictThreshold()
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        TerminalWaveSeparatorTracker tracker = new();
        TransportPacket first = new(startedAt, PacketDirection.Receive, [0x41], "COM8")
        {
            EndTimestamp = startedAt.AddMilliseconds(20)
        };

        Assert.Null(tracker.Observe(first, TimeSpan.FromMilliseconds(100)));
        Assert.Null(tracker.Observe(
            new(startedAt.AddMilliseconds(120), PacketDirection.Receive, [0x42], "COM8"),
            TimeSpan.FromMilliseconds(100)));
        TimeSpan? gap = tracker.Observe(
            new(startedAt.AddMilliseconds(221), PacketDirection.Receive, [0x43], "COM8"),
            TimeSpan.FromMilliseconds(100));

        Assert.Equal(TimeSpan.FromMilliseconds(101), gap);
    }
}
