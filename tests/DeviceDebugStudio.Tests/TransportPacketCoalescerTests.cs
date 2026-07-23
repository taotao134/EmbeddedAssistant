using System.Diagnostics;
using DeviceDebugStudio.Core.Transports;

namespace DeviceDebugStudio.Tests;

public sealed class TransportPacketCoalescerTests
{
    [Fact]
    public void GetReadyPrefixCount_HoldsTrailingReceiveUntilIdleGapExpires()
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        TransportPacket[] packets =
        [
            TransportPacket.Info("已连接"),
            new(startedAt, PacketDirection.Receive, [0x41], "COM4"),
            new(startedAt.AddMilliseconds(1), PacketDirection.Receive, [0x42], "COM4")
        ];

        int result = TransportPacketCoalescer.GetReadyPrefixCount(
            packets,
            startedAt.AddMilliseconds(5),
            TimeSpan.FromMilliseconds(10));

        Assert.Equal(1, result);
    }

    [Fact]
    public void GetReadyPrefixCount_ReleasesReceiveAfterIdleGapExpires()
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        TransportPacket[] packets =
        [
            new(startedAt, PacketDirection.Receive, [0x41], "COM4"),
            new(startedAt.AddMilliseconds(1), PacketDirection.Receive, [0x42], "COM4")
        ];

        int result = TransportPacketCoalescer.GetReadyPrefixCount(
            packets,
            startedAt.AddMilliseconds(11),
            TimeSpan.FromMilliseconds(10));

        Assert.Equal(2, result);
    }

    [Fact]
    public void CoalesceAdjacentReceives_MergesSerialReadFragmentsInOrder()
    {
        DateTimeOffset startedAt = DateTimeOffset.Parse("2026-07-16T04:11:56.563-07:00");
        TransportPacket[] packets =
        [
            new(startedAt, PacketDirection.Receive, [0x70], "COM4"),
            new(startedAt.AddMilliseconds(1), PacketDirection.Receive, [0x6F, 0x77, 0x65, 0x72], "COM4"),
            new(startedAt.AddMilliseconds(2), PacketDirection.Receive, [0x0D, 0x0A], "COM4")
        ];

        IReadOnlyList<TransportPacket> result = TransportPacketCoalescer.CoalesceAdjacentReceives(
            packets,
            TimeSpan.FromMilliseconds(10));

        TransportPacket merged = Assert.Single(result);
        Assert.Equal([0x70, 0x6F, 0x77, 0x65, 0x72, 0x0D, 0x0A], merged.Data);
    }

    [Fact]
    public void CoalesceAdjacentReceives_KeepsMessagesSeparatedAfterIdleGap()
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        TransportPacket[] packets =
        [
            new(startedAt, PacketDirection.Receive, [0x41], "COM4"),
            new(startedAt.AddMilliseconds(11), PacketDirection.Receive, [0x42], "COM4")
        ];

        IReadOnlyList<TransportPacket> result = TransportPacketCoalescer.CoalesceAdjacentReceives(
            packets,
            TimeSpan.FromMilliseconds(10));

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void CoalesceAdjacentReceives_UsesMonotonicArrivalTimeWhenWallClockMatches()
    {
        DateTimeOffset displayedAt = DateTimeOffset.UtcNow;
        long arrivedAt = Stopwatch.GetTimestamp();
        long arrivedLater = arrivedAt + Math.Max(1, Stopwatch.Frequency / 500);
        TransportPacket[] packets =
        [
            new(displayedAt, PacketDirection.Receive, [0x41], "COM4", ArrivalTimestamp: arrivedAt),
            new(displayedAt, PacketDirection.Receive, [0x42], "COM4", ArrivalTimestamp: arrivedLater)
        ];

        IReadOnlyList<TransportPacket> result = TransportPacketCoalescer.CoalesceAdjacentReceives(
            packets,
            TimeSpan.FromMilliseconds(1));

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void GetReadyPrefixCount_UsesMonotonicArrivalTimeForIdleGap()
    {
        DateTimeOffset displayedAt = DateTimeOffset.UtcNow;
        long arrivedAt = Stopwatch.GetTimestamp();
        long checkedAt = arrivedAt + Math.Max(1, Stopwatch.Frequency / 500);
        TransportPacket[] packets =
        [
            new(displayedAt, PacketDirection.Receive, [0x41], "COM4", ArrivalTimestamp: arrivedAt)
        ];

        int result = TransportPacketCoalescer.GetReadyPrefixCount(
            packets,
            displayedAt,
            checkedAt,
            TimeSpan.FromMilliseconds(1));

        Assert.Equal(1, result);
    }

    [Fact]
    public void CoalesceAdjacentReceives_DoesNotMergeAcrossSendRecord()
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        TransportPacket[] packets =
        [
            new(startedAt, PacketDirection.Receive, [0x41], "COM4"),
            new(startedAt.AddMilliseconds(1), PacketDirection.Send, [0x42], "COM4"),
            new(startedAt.AddMilliseconds(2), PacketDirection.Receive, [0x43], "COM4")
        ];

        IReadOnlyList<TransportPacket> result = TransportPacketCoalescer.CoalesceAdjacentReceives(
            packets,
            TimeSpan.FromMilliseconds(10));

        Assert.Equal(3, result.Count);
    }
}
