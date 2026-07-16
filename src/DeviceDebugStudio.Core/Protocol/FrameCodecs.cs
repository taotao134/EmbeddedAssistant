namespace DeviceDebugStudio.Core.Protocol;

public interface IFrameCodec
{
    string Name { get; }
    IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> data, DateTimeOffset timestamp);
    IReadOnlyList<byte[]> Flush();
    void Reset();
}

public sealed class RawFrameCodec : IFrameCodec
{
    public string Name => "原始数据块";

    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> data, DateTimeOffset timestamp) =>
        data.IsEmpty ? [] : [data.ToArray()];

    public IReadOnlyList<byte[]> Flush() => [];

    public void Reset()
    {
    }
}

public sealed class DelimiterFrameCodec(byte[] delimiter, bool includeDelimiter = false, int maximumFrameLength = 1024 * 1024) : IFrameCodec
{
    private readonly List<byte> _buffer = [];
    private readonly byte[] _delimiter = delimiter.Length > 0 ? delimiter : throw new ArgumentException("分隔符不能为空。", nameof(delimiter));

    public string Name => "分隔符";

    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> data, DateTimeOffset timestamp)
    {
        List<byte[]> frames = [];
        foreach (byte value in data)
        {
            _buffer.Add(value);
            if (_buffer.Count > maximumFrameLength)
            {
                _buffer.Clear();
                throw new InvalidDataException($"帧长度超过限制 {maximumFrameLength} 字节。 ");
            }

            if (!EndsWithDelimiter())
            {
                continue;
            }

            int count = includeDelimiter ? _buffer.Count : _buffer.Count - _delimiter.Length;
            frames.Add(_buffer.GetRange(0, count).ToArray());
            _buffer.Clear();
        }

        return frames;
    }

    public IReadOnlyList<byte[]> Flush()
    {
        if (_buffer.Count == 0)
        {
            return [];
        }

        byte[] pending = [.. _buffer];
        _buffer.Clear();
        return [pending];
    }

    public void Reset() => _buffer.Clear();

    private bool EndsWithDelimiter()
    {
        if (_buffer.Count < _delimiter.Length)
        {
            return false;
        }

        int offset = _buffer.Count - _delimiter.Length;
        for (int index = 0; index < _delimiter.Length; index++)
        {
            if (_buffer[offset + index] != _delimiter[index])
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class LineFrameCodec(byte[] lineEnding, bool includeLineEnding = false) : IFrameCodec
{
    private readonly DelimiterFrameCodec _inner = new(lineEnding, includeLineEnding);

    public string Name => "文本行";
    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> data, DateTimeOffset timestamp) => _inner.Push(data, timestamp);
    public IReadOnlyList<byte[]> Flush() => _inner.Flush();
    public void Reset() => _inner.Reset();
}

public sealed class FixedLengthFrameCodec(int frameLength) : IFrameCodec
{
    private readonly List<byte> _buffer = [];
    private readonly int _frameLength = frameLength > 0 ? frameLength : throw new ArgumentOutOfRangeException(nameof(frameLength));

    public string Name => "固定长度";

    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> data, DateTimeOffset timestamp)
    {
        _buffer.AddRange(data.ToArray());
        List<byte[]> frames = [];
        while (_buffer.Count >= _frameLength)
        {
            frames.Add(_buffer.GetRange(0, _frameLength).ToArray());
            _buffer.RemoveRange(0, _frameLength);
        }

        return frames;
    }

    public IReadOnlyList<byte[]> Flush()
    {
        if (_buffer.Count == 0)
        {
            return [];
        }

        byte[] pending = [.. _buffer];
        _buffer.Clear();
        return [pending];
    }

    public void Reset() => _buffer.Clear();
}

public sealed class LengthFieldFrameCodec(
    int lengthOffset,
    int lengthSize,
    bool littleEndian,
    int lengthAdjustment = 0,
    int maximumFrameLength = 1024 * 1024) : IFrameCodec
{
    private readonly List<byte> _buffer = [];

    public string Name => "长度字段";

    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> data, DateTimeOffset timestamp)
    {
        if (lengthOffset < 0 || lengthSize is < 1 or > 4)
        {
            throw new InvalidOperationException("长度字段参数无效。 ");
        }

        _buffer.AddRange(data.ToArray());
        List<byte[]> frames = [];
        int headerLength = lengthOffset + lengthSize;
        while (_buffer.Count >= headerLength)
        {
            int payloadLength = ReadLength(_buffer, lengthOffset, lengthSize, littleEndian);
            int frameLength = headerLength + payloadLength + lengthAdjustment;
            if (frameLength < headerLength || frameLength > maximumFrameLength)
            {
                _buffer.Clear();
                throw new InvalidDataException($"长度字段产生了无效帧长度 {frameLength}。 ");
            }

            if (_buffer.Count < frameLength)
            {
                break;
            }

            frames.Add(_buffer.GetRange(0, frameLength).ToArray());
            _buffer.RemoveRange(0, frameLength);
        }

        return frames;
    }

    public IReadOnlyList<byte[]> Flush()
    {
        if (_buffer.Count == 0)
        {
            return [];
        }

        byte[] pending = [.. _buffer];
        _buffer.Clear();
        return [pending];
    }

    public void Reset() => _buffer.Clear();

    private static int ReadLength(IReadOnlyList<byte> data, int offset, int size, bool littleEndian)
    {
        int result = 0;
        for (int index = 0; index < size; index++)
        {
            int sourceIndex = littleEndian ? offset + index : offset + size - index - 1;
            result |= data[sourceIndex] << (8 * index);
        }

        return result;
    }
}

public sealed class IdleGapFrameCodec(TimeSpan gap, int maximumFrameLength = 1024 * 1024) : IFrameCodec
{
    private readonly List<byte> _buffer = [];
    private DateTimeOffset? _lastTimestamp;

    public string Name => "空闲间隔";

    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> data, DateTimeOffset timestamp)
    {
        List<byte[]> frames = [];
        if (_lastTimestamp is not null && timestamp - _lastTimestamp >= gap && _buffer.Count > 0)
        {
            frames.Add([.. _buffer]);
            _buffer.Clear();
        }

        _buffer.AddRange(data.ToArray());
        if (_buffer.Count > maximumFrameLength)
        {
            _buffer.Clear();
            throw new InvalidDataException($"帧长度超过限制 {maximumFrameLength} 字节。 ");
        }

        _lastTimestamp = timestamp;
        return frames;
    }

    public IReadOnlyList<byte[]> Flush()
    {
        if (_buffer.Count == 0)
        {
            return [];
        }

        byte[] pending = [.. _buffer];
        Reset();
        return [pending];
    }

    public void Reset()
    {
        _buffer.Clear();
        _lastTimestamp = null;
    }
}
