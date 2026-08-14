using System.Diagnostics;
using DeviceDebugStudio.Core.Transports;

namespace DeviceDebugStudio.App.ViewModels;

public sealed class TerminalWaveSeparatorTracker
{
    private DateTimeOffset? _lastPacketEndTimestamp;
    private long _lastPacketEndArrivalTimestamp;

    public TimeSpan? Observe(TransportPacket packet, TimeSpan threshold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, TimeSpan.Zero);
        if (packet.Direction is not (PacketDirection.Receive or PacketDirection.Send)
            || packet.Data.Length == 0
            || packet.Message is not null)
        {
            return null;
        }

        TimeSpan? gap = null;
        if (_lastPacketEndTimestamp is DateTimeOffset previousEnd)
        {
            gap = _lastPacketEndArrivalTimestamp > 0 && packet.ArrivalTimestamp > 0
                ? Stopwatch.GetElapsedTime(_lastPacketEndArrivalTimestamp, packet.ArrivalTimestamp)
                : packet.Timestamp - previousEnd;
        }

        _lastPacketEndTimestamp = packet.EndTimestamp == default ? packet.Timestamp : packet.EndTimestamp;
        _lastPacketEndArrivalTimestamp = packet.EndArrivalTimestamp > 0
            ? packet.EndArrivalTimestamp
            : packet.ArrivalTimestamp;
        return gap > threshold ? gap : null;
    }

    public void Reset()
    {
        _lastPacketEndTimestamp = null;
        _lastPacketEndArrivalTimestamp = 0;
    }
}
