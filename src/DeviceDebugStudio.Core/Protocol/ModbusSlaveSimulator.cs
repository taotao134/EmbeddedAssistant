using System.Buffers.Binary;

namespace DeviceDebugStudio.Core.Protocol;

public sealed class ModbusSlaveSimulator
{
    private readonly object _sync = new();
    private readonly bool[] _coils = new bool[ushort.MaxValue + 1];
    private readonly bool[] _discreteInputs = new bool[ushort.MaxValue + 1];
    private readonly ushort[] _holdingRegisters = new ushort[ushort.MaxValue + 1];
    private readonly ushort[] _inputRegisters = new ushort[ushort.MaxValue + 1];

    public byte UnitId { get; set; } = 1;

    public void SetHoldingRegister(ushort address, ushort value)
    {
        lock (_sync)
        {
            _holdingRegisters[address] = value;
        }
    }

    public ushort GetHoldingRegister(ushort address)
    {
        lock (_sync)
        {
            return _holdingRegisters[address];
        }
    }

    public byte[]? ProcessRtu(ReadOnlySpan<byte> request)
    {
        if (request.Length < 4)
        {
            return null;
        }

        ushort actualCrc = BinaryPrimitives.ReadUInt16LittleEndian(request[^2..]);
        if (ChecksumCalculator.ComputeCrc16Modbus(request[..^2]) != actualCrc)
        {
            return null;
        }

        byte requestUnit = request[0];
        if (requestUnit != UnitId && requestUnit != 0)
        {
            return null;
        }

        byte[] pduResponse = ProcessPdu(request.Slice(1, request.Length - 3));
        if (requestUnit == 0)
        {
            return null;
        }

        byte[] response = new byte[pduResponse.Length + 3];
        response[0] = UnitId;
        pduResponse.CopyTo(response.AsSpan(1));
        ushort crc = ChecksumCalculator.ComputeCrc16Modbus(response.AsSpan(0, response.Length - 2));
        BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(response.Length - 2), crc);
        return response;
    }

    public byte[]? ProcessTcp(ReadOnlySpan<byte> request)
    {
        if (request.Length < 8 || BinaryPrimitives.ReadUInt16BigEndian(request.Slice(2, 2)) != 0)
        {
            return null;
        }

        int declaredLength = BinaryPrimitives.ReadUInt16BigEndian(request.Slice(4, 2));
        if (declaredLength < 2 || request.Length < 6 + declaredLength)
        {
            return null;
        }

        byte requestUnit = request[6];
        if (requestUnit != UnitId && requestUnit != 0)
        {
            return null;
        }

        byte[] pduResponse = ProcessPdu(request.Slice(7, declaredLength - 1));
        if (requestUnit == 0)
        {
            return null;
        }

        byte[] response = new byte[7 + pduResponse.Length];
        request[..4].CopyTo(response);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4), (ushort)(pduResponse.Length + 1));
        response[6] = UnitId;
        pduResponse.CopyTo(response.AsSpan(7));
        return response;
    }

    private byte[] ProcessPdu(ReadOnlySpan<byte> pdu)
    {
        if (pdu.IsEmpty)
        {
            return ExceptionResponse(0, 0x03);
        }

        byte function = pdu[0];
        try
        {
            return function switch
            {
                0x01 => ReadBits(function, pdu, _coils),
                0x02 => ReadBits(function, pdu, _discreteInputs),
                0x03 => ReadRegisters(function, pdu, _holdingRegisters),
                0x04 => ReadRegisters(function, pdu, _inputRegisters),
                0x05 => WriteSingleCoil(pdu),
                0x06 => WriteSingleRegister(pdu),
                0x0F => WriteMultipleCoils(pdu),
                0x10 => WriteMultipleRegisters(pdu),
                _ => ExceptionResponse(function, 0x01)
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return ExceptionResponse(function, 0x02);
        }
        catch (InvalidDataException)
        {
            return ExceptionResponse(function, 0x03);
        }
    }

    private byte[] ReadBits(byte function, ReadOnlySpan<byte> pdu, bool[] source)
    {
        (ushort address, ushort quantity) = ParseAddressQuantity(pdu);
        if (quantity is < 1 or > 2000 || address + quantity > source.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }

        int byteCount = (quantity + 7) / 8;
        byte[] response = new byte[2 + byteCount];
        response[0] = function;
        response[1] = (byte)byteCount;
        lock (_sync)
        {
            for (int index = 0; index < quantity; index++)
            {
                if (source[address + index])
                {
                    response[2 + index / 8] |= (byte)(1 << (index % 8));
                }
            }
        }
        return response;
    }

    private byte[] ReadRegisters(byte function, ReadOnlySpan<byte> pdu, ushort[] source)
    {
        (ushort address, ushort quantity) = ParseAddressQuantity(pdu);
        if (quantity is < 1 or > 125 || address + quantity > source.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }

        byte[] response = new byte[2 + quantity * 2];
        response[0] = function;
        response[1] = (byte)(quantity * 2);
        lock (_sync)
        {
            for (int index = 0; index < quantity; index++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2 + index * 2), source[address + index]);
            }
        }
        return response;
    }

    private byte[] WriteSingleCoil(ReadOnlySpan<byte> pdu)
    {
        EnsureLength(pdu, 5);
        ushort address = BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(1, 2));
        ushort raw = BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(3, 2));
        if (raw is not (0x0000 or 0xFF00))
        {
            throw new InvalidDataException();
        }
        lock (_sync)
        {
            _coils[address] = raw == 0xFF00;
        }
        return pdu[..5].ToArray();
    }

    private byte[] WriteSingleRegister(ReadOnlySpan<byte> pdu)
    {
        EnsureLength(pdu, 5);
        ushort address = BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(1, 2));
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(3, 2));
        lock (_sync)
        {
            _holdingRegisters[address] = value;
        }
        return pdu[..5].ToArray();
    }

    private byte[] WriteMultipleCoils(ReadOnlySpan<byte> pdu)
    {
        EnsureLength(pdu, 6);
        ushort address = BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(1, 2));
        ushort quantity = BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(3, 2));
        int byteCount = pdu[5];
        if (quantity is < 1 or > 1968 || byteCount != (quantity + 7) / 8 || pdu.Length < 6 + byteCount || address + quantity > _coils.Length)
        {
            throw new InvalidDataException();
        }
        lock (_sync)
        {
            for (int index = 0; index < quantity; index++)
            {
                _coils[address + index] = (pdu[6 + index / 8] & (1 << (index % 8))) != 0;
            }
        }
        return pdu[..5].ToArray();
    }

    private byte[] WriteMultipleRegisters(ReadOnlySpan<byte> pdu)
    {
        EnsureLength(pdu, 6);
        ushort address = BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(1, 2));
        ushort quantity = BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(3, 2));
        int byteCount = pdu[5];
        if (quantity is < 1 or > 123 || byteCount != quantity * 2 || pdu.Length < 6 + byteCount || address + quantity > _holdingRegisters.Length)
        {
            throw new InvalidDataException();
        }
        lock (_sync)
        {
            for (int index = 0; index < quantity; index++)
            {
                _holdingRegisters[address + index] = BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(6 + index * 2, 2));
            }
        }
        return pdu[..5].ToArray();
    }

    private static (ushort Address, ushort Quantity) ParseAddressQuantity(ReadOnlySpan<byte> pdu)
    {
        EnsureLength(pdu, 5);
        return (
            BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(1, 2)),
            BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(3, 2)));
    }

    private static void EnsureLength(ReadOnlySpan<byte> data, int minimum)
    {
        if (data.Length < minimum)
        {
            throw new InvalidDataException();
        }
    }

    private static byte[] ExceptionResponse(byte function, byte code) => [(byte)(function | 0x80), code];
}
