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
