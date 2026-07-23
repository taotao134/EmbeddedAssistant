using DeviceDebugStudio.Core.Profiles;
using DeviceDebugStudio.Core.Protocol;

namespace DeviceDebugStudio.Tests;

public sealed class GenericFrameDecoderTests
{
    [Fact]
    public void MatchesFrameTypeAndSupportsWildcardBytes()
    {
        FrameTemplate template = new()
        {
            MatchOffset = 1,
            MatchHex = "00 ?? 05"
        };

        Assert.True(GenericFrameDecoder.Matches([0xFE, 0x00, 0x1E, 0x05], template));
        Assert.False(GenericFrameDecoder.Matches([0xFE, 0x00, 0x1E, 0x06], template));
    }

    [Fact]
    public void DecodesLengthPrefixedFieldAndFollowingFields()
    {
        byte[] frame =
        [
            0xFE, 0x00, 0x1E, 0x05,
            0x32, 0x34, 0x34, 0x38, 0x32, 0x39, 0x34, 0x38,
            0x05, 0x31, 0x32, 0x33, 0x34, 0x35, 0x2C,
            0x00, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x02,
            0x00, 0x00, 0xEF
        ];
        FrameTemplate template = new()
        {
            MatchOffset = 3,
            MatchHex = "05",
            Fields =
            [
                new FrameField { Name = "设备ID", Type = FrameFieldType.Ascii, Offset = 4, Length = 8 },
                new FrameField { Name = "电话", Type = FrameFieldType.Ascii, Offset = 13, LengthFromOffset = 12 },
                new FrameField { Name = "分隔符", Type = FrameFieldType.Bytes, Offset = 13, OffsetFromLengthOffset = 12 },
                new FrameField { Name = "经度", Type = FrameFieldType.UInt32, Offset = 14, OffsetFromLengthOffset = 12, LittleEndian = false },
                new FrameField { Name = "纬度", Type = FrameFieldType.UInt32, Offset = 18, OffsetFromLengthOffset = 12, LittleEndian = false }
            ]
        };

        DecodedFrame decoded = GenericFrameDecoder.Decode(frame, template);

        Assert.Null(decoded.Error);
        Assert.Equal("24482948", decoded.Fields[0].Value);
        Assert.Equal("12345", decoded.Fields[1].Value);
        Assert.Equal(1d, decoded.Fields[3].Value);
        Assert.Equal(2d, decoded.Fields[4].Value);
        Assert.Equal(19, decoded.Fields[3].Offset);
    }
}
