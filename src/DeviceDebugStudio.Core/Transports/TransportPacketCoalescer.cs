using System.Diagnostics;

namespace DeviceDebugStudio.Core.Transports;

public static class TransportPacketCoalescer
{
    public static int GetReadyPrefixCount(
        IReadOnlyList<TransportPacket> packets,
        DateTimeOffset now,
        TimeSpan maximumGap)
    {
        return GetReadyPrefixCount(packets, now, 0, maximumGap);
    }

    public static int GetReadyPrefixCount(
        IReadOnlyList<TransportPacket> packets,
        DateTimeOffset now,
        long nowArrivalTimestamp,
        TimeSpan maximumGap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumGap, TimeSpan.Zero);
        if (packets.Count == 0)
        {
            return 0;
        }

        TransportPacket last = packets[^1];
        if (last.Direction != PacketDirection.Receive
            || last.Message is not null
            || GetElapsedToNow(last, now, nowArrivalTimestamp) >= maximumGap)
        {
            return packets.Count;
        }

        int pendingStart = packets.Count - 1;
        while (pendingStart > 0)
        {
            TransportPacket previous = packets[pendingStart - 1];
            TransportPacket current = packets[pendingStart];
            TimeSpan gap = GetElapsed(previous, current);
            if (previous.Direction != PacketDirection.Receive
                || previous.Message is not null
                || gap < TimeSpan.Zero
                || gap > maximumGap
                || !string.Equals(previous.Endpoint, current.Endpoint, StringComparison.Ordinal))
            {
                break;
            }
            pendingStart--;
        }

        return pendingStart;
    }

    public static IReadOnlyList<TransportPacket> CoalesceAdjacentReceives(
        IEnumerable<TransportPacket> packets,
        TimeSpan maximumGap,
        int maximumBatchBytes = 256 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumGap, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBatchBytes, 1);

        List<TransportPacket> result = [];
        TransportPacket? pending = null;
        TransportPacket? lastReceived = null;

        foreach (TransportPacket packet in packets)
        {
            if (packet.Direction == PacketDirection.Receive && packet.Message is null)
            {
                TimeSpan gap = lastReceived is null ? TimeSpan.Zero : GetElapsed(lastReceived, packet);
                bool canMerge = pending is not null
                    && gap >= TimeSpan.Zero
                    && gap <= maximumGap
                    && string.Equals(pending.Endpoint, packet.Endpoint, StringComparison.Ordinal)
                    && pending.Data.Length + packet.Data.Length <= maximumBatchBytes;

                if (canMerge)
                {
                    byte[] merged = new byte[pending!.Data.Length + packet.Data.Length];
                    pending.Data.CopyTo(merged, 0);
                    packet.Data.CopyTo(merged, pending.Data.Length);
                    pending = pending with { Data = merged };
                }
                else
                {
                    FlushPending();
                    pending = packet;
                }

                lastReceived = packet;
                continue;
            }

            FlushPending();
            result.Add(packet);
        }

        FlushPending();
        return result;

        void FlushPending()
        {
            if (pending is not null)
            {
                result.Add(pending);
                pending = null;
            }
        }
    }

    private static TimeSpan GetElapsed(TransportPacket earlier, TransportPacket later)
    {
        if (earlier.ArrivalTimestamp > 0 && later.ArrivalTimestamp > 0)
        {
            return Stopwatch.GetElapsedTime(earlier.ArrivalTimestamp, later.ArrivalTimestamp);
        }

        return later.Timestamp - earlier.Timestamp;
    }

    private static TimeSpan GetElapsedToNow(
        TransportPacket packet,
        DateTimeOffset now,
        long nowArrivalTimestamp)
    {
        if (packet.ArrivalTimestamp > 0 && nowArrivalTimestamp > 0)
        {
            return Stopwatch.GetElapsedTime(packet.ArrivalTimestamp, nowArrivalTimestamp);
        }

        return now - packet.Timestamp;
    }
}
