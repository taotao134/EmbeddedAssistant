namespace DeviceDebugStudio.Core.Transports;

public static class TransportPacketCoalescer
{
    public static int GetReadyPrefixCount(
        IReadOnlyList<TransportPacket> packets,
        DateTimeOffset now,
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
            || now - last.Timestamp >= maximumGap)
        {
            return packets.Count;
        }

        int pendingStart = packets.Count - 1;
        while (pendingStart > 0)
        {
            TransportPacket previous = packets[pendingStart - 1];
            TransportPacket current = packets[pendingStart];
            TimeSpan gap = current.Timestamp - previous.Timestamp;
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
        DateTimeOffset lastReceivedAt = default;

        foreach (TransportPacket packet in packets)
        {
            if (packet.Direction == PacketDirection.Receive && packet.Message is null)
            {
                TimeSpan gap = packet.Timestamp - lastReceivedAt;
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

                lastReceivedAt = packet.Timestamp;
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
}
