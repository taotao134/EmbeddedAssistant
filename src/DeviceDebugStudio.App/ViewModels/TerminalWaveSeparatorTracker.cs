using System.Diagnostics;
using DeviceDebugStudio.Core.Transports;

namespace DeviceDebugStudio.App.ViewModels;

public sealed class TerminalWaveSeparatorTracker
{
    private DateTimeOffset? _lastReceiveEndTimestamp;
    private long _lastReceiveEndArrivalTimestamp;

    public TimeSpan? Observe(TransportPacket packet, TimeSpan threshold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, TimeSpan.Zero);
        if (packet.Direction != PacketDirection.Receive || packet.Data.Length == 0 || packet.Message is not null)
        {
            return null;
        }

        TimeSpan? gap = null;
        if (_lastReceiveEndTimestamp is DateTimeOffset previousEnd)
        {
            gap = _lastReceiveEndArrivalTimestamp > 0 && packet.ArrivalTimestamp > 0
                ? Stopwatch.GetElapsedTime(_lastReceiveEndArrivalTimestamp, packet.ArrivalTimestamp)
                : packet.Timestamp - previousEnd;
        }

        _lastReceiveEndTimestamp = packet.EndTimestamp == default ? packet.Timestamp : packet.EndTimestamp;
        _lastReceiveEndArrivalTimestamp = packet.EndArrivalTimestamp > 0
            ? packet.EndArrivalTimestamp
            : packet.ArrivalTimestamp;
        return gap > threshold ? gap : null;
    }

    public void Reset()
    {
        _lastReceiveEndTimestamp = null;
        _lastReceiveEndArrivalTimestamp = 0;
    }
}
