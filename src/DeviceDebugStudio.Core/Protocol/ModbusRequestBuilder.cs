using System.Buffers.Binary;

namespace DeviceDebugStudio.Core.Protocol;

public static class ModbusRequestBuilder
{
    public static byte[] BuildRtu(byte unitId, byte functionCode, ushort address, ushort quantityOrValue, ReadOnlySpan<ushort> values = default)
    {
        byte[] pdu = BuildPdu(functionCode, address, quantityOrValue, values);
        byte[] frame = new byte[pdu.Length + 3];
        frame[0] = unitId;
        pdu.CopyTo(frame.AsSpan(1));
        ushort crc = ChecksumCalculator.ComputeCrc16Modbus(frame.AsSpan(0, frame.Length - 2));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(frame.Length - 2), crc);
        return frame;
    }

    public static byte[] BuildTcp(ushort transactionId, byte unitId, byte functionCode, ushort address, ushort quantityOrValue, ReadOnlySpan<ushort> values = default)
    {
        byte[] pdu = BuildPdu(functionCode, address, quantityOrValue, values);
        byte[] frame = new byte[7 + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(pdu.Length + 1));
        frame[6] = unitId;
        pdu.CopyTo(frame.AsSpan(7));
        return frame;
    }

    private static byte[] BuildPdu(byte functionCode, ushort address, ushort quantityOrValue, ReadOnlySpan<ushort> values)
    {
        if (functionCode is 0x0F or 0x10)
        {
            if (values.IsEmpty)
            {
                throw new ArgumentException("写多个值时必须提供数据。", nameof(values));
            }

            int byteCount = functionCode == 0x0F ? (values.Length + 7) / 8 : values.Length * 2;
            byte[] multiple = new byte[6 + byteCount];
            multiple[0] = functionCode;
            BinaryPrimitives.WriteUInt16BigEndian(multiple.AsSpan(1), address);
            BinaryPrimitives.WriteUInt16BigEndian(multiple.AsSpan(3), (ushort)values.Length);
            multiple[5] = (byte)byteCount;
            if (functionCode == 0x0F)
            {
                for (int index = 0; index < values.Length; index++)
                {
                    if (values[index] != 0)
                    {
                        multiple[6 + index / 8] |= (byte)(1 << (index % 8));
                    }
                }
            }
            else
            {
                for (int index = 0; index < values.Length; index++)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(multiple.AsSpan(6 + index * 2), values[index]);
                }
            }

            return multiple;
        }

        if (functionCode is not (0x01 or 0x02 or 0x03 or 0x04 or 0x05 or 0x06))
        {
            throw new ArgumentOutOfRangeException(nameof(functionCode), "首版仅支持常用 Modbus 功能码。 ");
        }

        byte[] pdu = new byte[5];
        pdu[0] = functionCode;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), quantityOrValue);
        return pdu;
    }
}
