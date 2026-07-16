using System.Buffers.Binary;
using System.Text;
using DeviceDebugStudio.Core.Profiles;

namespace DeviceDebugStudio.Core.Protocol;

public sealed record DecodedField(string Name, object Value, string Unit, int Offset, int Length);

public sealed record DecodedFrame(byte[] Raw, IReadOnlyList<DecodedField> Fields, bool ChecksumValid, string? Error = null);

public static class GenericFrameDecoder
{
    public static DecodedFrame Decode(ReadOnlySpan<byte> frame, FrameTemplate template)
    {
        try
        {
            ReadOnlySpan<byte> decoded = template.Escape switch
            {
                EscapeMode.None => frame,
                EscapeMode.Slip => DecodeSlip(frame),
                EscapeMode.Cobs => DecodeCobs(frame),
                _ => frame
            };

            List<DecodedField> values = [];
            foreach (FrameField field in template.Fields)
            {
                values.Add(DecodeField(decoded, field));
            }

            bool checksumValid = ValidateChecksum(decoded, template.Checksum);
            return new DecodedFrame(decoded.ToArray(), values, checksumValid);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IndexOutOfRangeException)
        {
            return new DecodedFrame(frame.ToArray(), [], false, exception.Message);
        }
    }

    private static DecodedField DecodeField(ReadOnlySpan<byte> frame, FrameField field)
    {
        int length = field.Type switch
        {
            FrameFieldType.UInt8 or FrameFieldType.Int8 => 1,
            FrameFieldType.UInt16 or FrameFieldType.Int16 => 2,
            FrameFieldType.UInt32 or FrameFieldType.Int32 or FrameFieldType.Float32 => 4,
            _ => field.Length
        };

        if (field.Offset < 0 || length < 1 || field.Offset + length > frame.Length)
        {
            throw new InvalidDataException($"字段 {field.Name} 超出帧范围。 ");
        }

        ReadOnlySpan<byte> data = frame.Slice(field.Offset, length);
        object rawValue = field.Type switch
        {
            FrameFieldType.UInt8 => data[0],
            FrameFieldType.Int8 => unchecked((sbyte)data[0]),
            FrameFieldType.UInt16 => field.LittleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(data) : BinaryPrimitives.ReadUInt16BigEndian(data),
            FrameFieldType.Int16 => field.LittleEndian ? BinaryPrimitives.ReadInt16LittleEndian(data) : BinaryPrimitives.ReadInt16BigEndian(data),
            FrameFieldType.UInt32 => field.LittleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(data) : BinaryPrimitives.ReadUInt32BigEndian(data),
            FrameFieldType.Int32 => field.LittleEndian ? BinaryPrimitives.ReadInt32LittleEndian(data) : BinaryPrimitives.ReadInt32BigEndian(data),
            FrameFieldType.Float32 => ReadFloat(data, field.LittleEndian),
            FrameFieldType.Ascii => Encoding.ASCII.GetString(data).TrimEnd('\0'),
            FrameFieldType.Bytes => ByteText.ToHex(data),
            FrameFieldType.BitField => ReadBits(data, field.BitOffset, field.BitLength),
            _ => ByteText.ToHex(data)
        };

        object value = rawValue is IConvertible && field.Type is not FrameFieldType.Ascii and not FrameFieldType.Bytes
            ? Convert.ToDouble(rawValue) * field.Scale + field.Bias
            : rawValue;
        return new DecodedField(field.Name, value, field.Unit, field.Offset, length);
    }

    private static float ReadFloat(ReadOnlySpan<byte> data, bool littleEndian)
    {
        int bits = littleEndian ? BinaryPrimitives.ReadInt32LittleEndian(data) : BinaryPrimitives.ReadInt32BigEndian(data);
        return BitConverter.Int32BitsToSingle(bits);
    }

    private static ulong ReadBits(ReadOnlySpan<byte> data, int bitOffset, int bitLength)
    {
        if (bitOffset < 0 || bitLength is < 1 or > 64 || bitOffset + bitLength > data.Length * 8)
        {
            throw new InvalidDataException("位域范围无效。 ");
        }

        ulong value = 0;
        for (int bit = 0; bit < bitLength; bit++)
        {
            int sourceBit = bitOffset + bit;
            if ((data[sourceBit / 8] & (1 << (sourceBit % 8))) != 0)
            {
                value |= 1UL << bit;
            }
        }

        return value;
    }

    private static bool ValidateChecksum(ReadOnlySpan<byte> frame, ChecksumKind kind)
    {
        int checksumSize = kind switch
        {
            ChecksumKind.None => 0,
            ChecksumKind.Xor8 or ChecksumKind.Add8 or ChecksumKind.Crc8 => 1,
            ChecksumKind.Crc16Modbus or ChecksumKind.Crc16Ccitt => 2,
            ChecksumKind.Crc32 => 4,
            _ => 0
        };
        if (checksumSize == 0)
        {
            return true;
        }

        if (frame.Length <= checksumSize)
        {
            return false;
        }

        ReadOnlySpan<byte> payload = frame[..^checksumSize];
        ReadOnlySpan<byte> expected = frame[^checksumSize..];
        return ChecksumCalculator.Compute(payload, kind).AsSpan().SequenceEqual(expected);
    }

    private static byte[] DecodeSlip(ReadOnlySpan<byte> data)
    {
        List<byte> result = [];
        for (int index = 0; index < data.Length; index++)
        {
            byte value = data[index];
            if (value != 0xDB)
            {
                if (value != 0xC0)
                {
                    result.Add(value);
                }
                continue;
            }

            if (++index >= data.Length)
            {
                throw new InvalidDataException("SLIP 转义序列不完整。 ");
            }

            result.Add(data[index] switch
            {
                0xDC => (byte)0xC0,
                0xDD => (byte)0xDB,
                _ => throw new InvalidDataException("SLIP 转义序列无效。 ")
            });
        }

        return [.. result];
    }

    private static byte[] DecodeCobs(ReadOnlySpan<byte> data)
    {
        List<byte> result = [];
        int index = 0;
        while (index < data.Length)
        {
            int code = data[index++];
            if (code == 0 || index + code - 1 > data.Length)
            {
                throw new InvalidDataException("COBS 数据无效。 ");
            }

            for (int count = 1; count < code; count++)
            {
                result.Add(data[index++]);
            }

            if (code < 0xFF && index < data.Length)
            {
                result.Add(0);
            }
        }

        return [.. result];
    }
}
