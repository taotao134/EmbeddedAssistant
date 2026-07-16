using DeviceDebugStudio.Core.Protocol;

namespace DeviceDebugStudio.Tests;

public sealed class FrameCodecTests
{
    [Fact]
    public void DelimiterCodecHandlesSplitAndCoalescedPackets()
    {
        DelimiterFrameCodec codec = new([0x0D, 0x0A]);
        Assert.Empty(codec.Push([0x01, 0x02, 0x0D], DateTimeOffset.Now));

        IReadOnlyList<byte[]> frames = codec.Push([0x0A, 0x03, 0x0D, 0x0A], DateTimeOffset.Now);

        Assert.Equal(2, frames.Count);
        Assert.Equal([0x01, 0x02], frames[0]);
        Assert.Equal([0x03], frames[1]);
    }

    [Fact]
    public void FixedLengthCodecSplitsMultipleFramesAndKeepsRemainder()
    {
        FixedLengthFrameCodec codec = new(3);
        IReadOnlyList<byte[]> frames = codec.Push([1, 2, 3, 4, 5, 6, 7], DateTimeOffset.Now);

        Assert.Equal(2, frames.Count);
        Assert.Equal([1, 2, 3], frames[0]);
        Assert.Equal([4, 5, 6], frames[1]);
        Assert.Equal([7], Assert.Single(codec.Flush()));
    }

    [Fact]
    public void LengthFieldCodecWaitsForCompleteFrame()
    {
        LengthFieldFrameCodec codec = new(lengthOffset: 1, lengthSize: 1, littleEndian: true);
        Assert.Empty(codec.Push([0xAA, 0x03, 0x10], DateTimeOffset.Now));

        byte[] frame = Assert.Single(codec.Push([0x11, 0x12], DateTimeOffset.Now));

        Assert.Equal([0xAA, 0x03, 0x10, 0x11, 0x12], frame);
    }

    [Fact]
    public void IdleGapCodecClosesPreviousFrameWhenGapIsReached()
    {
        IdleGapFrameCodec codec = new(TimeSpan.FromMilliseconds(20));
        DateTimeOffset start = DateTimeOffset.Now;
        Assert.Empty(codec.Push([1, 2], start));

        byte[] frame = Assert.Single(codec.Push([3], start.AddMilliseconds(25)));

        Assert.Equal([1, 2], frame);
        Assert.Equal([3], Assert.Single(codec.Flush()));
    }
}
