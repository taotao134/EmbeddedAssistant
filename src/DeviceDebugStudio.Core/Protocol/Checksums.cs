namespace DeviceDebugStudio.Core.Protocol;

public enum ChecksumKind
{
    None,
    Xor8,
    Add8,
    Crc8,
    Crc16Modbus,
    Crc16Ccitt,
    Crc32
}

public static class ChecksumCalculator
{
    public static byte[] Compute(ReadOnlySpan<byte> data, ChecksumKind kind, bool littleEndian = true) => kind switch
    {
        ChecksumKind.None => [],
        ChecksumKind.Xor8 => [ComputeXor8(data)],
        ChecksumKind.Add8 => [ComputeAdd8(data)],
        ChecksumKind.Crc8 => [ComputeCrc8(data)],
        ChecksumKind.Crc16Modbus => GetBytes(ComputeCrc16Modbus(data), littleEndian),
        ChecksumKind.Crc16Ccitt => GetBytes(ComputeCrc16Ccitt(data), littleEndian),
        ChecksumKind.Crc32 => GetBytes(ComputeCrc32(data), littleEndian),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static byte[] Append(ReadOnlySpan<byte> data, ChecksumKind kind, bool littleEndian = true)
    {
        byte[] checksum = Compute(data, kind, littleEndian);
        byte[] result = new byte[data.Length + checksum.Length];
        data.CopyTo(result);
        checksum.CopyTo(result.AsSpan(data.Length));
        return result;
    }

    public static byte ComputeXor8(ReadOnlySpan<byte> data)
    {
        byte value = 0;
        foreach (byte item in data)
        {
            value ^= item;
        }

        return value;
    }

    public static byte ComputeAdd8(ReadOnlySpan<byte> data)
    {
        int value = 0;
        foreach (byte item in data)
        {
            value += item;
        }

        return unchecked((byte)value);
    }

    public static byte ComputeCrc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (byte item in data)
        {
            crc ^= item;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
            }
        }

        return crc;
    }

    public static ushort ComputeCrc16Modbus(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte item in data)
        {
            crc ^= item;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
            }
        }

        return crc;
    }

    public static ushort ComputeCrc16Ccitt(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte item in data)
        {
            crc ^= (ushort)(item << 8);
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
            }
        }

        return crc;
    }

    public static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte item in data)
        {
            crc ^= item;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return ~crc;
    }

    private static byte[] GetBytes(ushort value, bool littleEndian) => littleEndian
        ? [(byte)value, (byte)(value >> 8)]
        : [(byte)(value >> 8), (byte)value];

    private static byte[] GetBytes(uint value, bool littleEndian) => littleEndian
        ? [(byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)]
        : [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
}
