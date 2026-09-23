using System.Text;

namespace DeviceDebugStudio.Core.Terminal;

public enum AnsiTerminalColor : byte
{
    Default,
    Black,
    Red,
    Green,
    Yellow,
    Blue,
    Magenta,
    Cyan,
    White,
    BrightBlack,
    BrightRed,
    BrightGreen,
    BrightYellow,
    BrightBlue,
    BrightMagenta,
    BrightCyan,
    BrightWhite
}

public readonly record struct AnsiTerminalCell(
    char Character,
    AnsiTerminalColor Foreground = AnsiTerminalColor.Default,
    AnsiTerminalColor Background = AnsiTerminalColor.Default,
    bool Bold = false,
    bool Inverse = false)
{
    public static AnsiTerminalCell Blank => new(' ');
}

public sealed record AnsiTerminalLine(IReadOnlyList<AnsiTerminalCell> Cells);

public sealed record AnsiTerminalSnapshot(
    IReadOnlyList<AnsiTerminalLine> Lines,
    int CursorRow,
    int CursorColumn,
    bool CursorVisible,
    long Version);

public sealed record AnsiTerminalFeedResult(
    IReadOnlyList<byte[]> Responses,
    bool Changed);

/// <summary>
/// ESP-IDF REPL 所需的轻量 ANSI/VT 终端状态机。
/// 保留光标和屏幕缓冲区，避免把控制序列直接显示为乱码。
/// </summary>
public sealed class AnsiTerminalSession
{
    private static readonly byte[] DeviceStatusOkResponse = [0x1B, 0x5B, 0x30, 0x6E];
    private static readonly Encoding Ascii = Encoding.ASCII;

    private readonly object _gate = new();
    private readonly List<AnsiTerminalCell[]> _rows = [];
    private readonly int _width;
    private readonly int _maximumRows;
    private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();
    private readonly char[] _decodeBuffer = new char[2048];
    private readonly StringBuilder _csiParameters = new();
    private readonly StringBuilder _oscBuffer = new();

    private ParserState _state;
    private char _csiPrivateMarker;
    private int _cursorRow;
    private int _cursorColumn;
    private int _savedCursorRow;
    private int _savedCursorColumn;
    private bool _cursorSaved;
    private bool _cursorVisible = true;
    private AnsiTerminalColor _foreground;
    private AnsiTerminalColor _background;
    private bool _bold;
    private bool _inverse;
    private long _version;

    public AnsiTerminalSession(int width = 160, int maximumRows = 2000)
    {
        _width = Math.Clamp(width, 20, 400);
        _maximumRows = Math.Clamp(maximumRows, 100, 10_000);
        ResetCore();
    }

    public long Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _decoder.Reset();
            ResetCore();
            _version++;
        }
    }

    public AnsiTerminalFeedResult Feed(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return new([], false);
        }

        lock (_gate)
        {
            List<byte[]>? responses = null;
            bool changed = false;
            int offset = 0;
            while (offset < data.Length)
            {
                _decoder.Convert(
                    data[offset..],
                    _decodeBuffer,
                    flush: false,
                    out int bytesUsed,
                    out int charsUsed,
                    out _);
                offset += bytesUsed;
                for (int index = 0; index < charsUsed; index++)
                {
                    changed |= ProcessCharacter(_decodeBuffer[index], ref responses);
                }

                if (bytesUsed == 0 && charsUsed == 0)
                {
                    break;
                }
            }

            if (changed)
            {
                _version++;
            }

            return new(responses ?? [], changed);
        }
    }

    public AnsiTerminalSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            List<AnsiTerminalLine> lines = new(_rows.Count);
            for (int rowIndex = 0; rowIndex < _rows.Count; rowIndex++)
            {
                AnsiTerminalCell[] row = _rows[rowIndex];
                int length = row.Length;
                while (length > 0 && IsBlank(row[length - 1]))
                {
                    length--;
                }

                if (_cursorVisible && rowIndex == _cursorRow)
                {
                    length = Math.Max(length, Math.Min(_width, _cursorColumn + 1));
                }

                lines.Add(new AnsiTerminalLine(row[..length].ToArray()));
            }

            if (lines.Count == 0)
            {
                lines.Add(new AnsiTerminalLine([]));
            }

            return new(lines, _cursorRow, _cursorColumn, _cursorVisible, _version);
        }
    }

    private void ResetCore()
    {
        _rows.Clear();
        _rows.Add(CreateRow());
        _state = ParserState.Ground;
        _csiParameters.Clear();
        _oscBuffer.Clear();
        _csiPrivateMarker = '\0';
        _cursorRow = 0;
        _cursorColumn = 0;
        _savedCursorRow = 0;
        _savedCursorColumn = 0;
        _cursorSaved = false;
        _cursorVisible = true;
        _foreground = AnsiTerminalColor.Default;
        _background = AnsiTerminalColor.Default;
        _bold = false;
        _inverse = false;
    }

    private bool ProcessCharacter(char value, ref List<byte[]>? responses)
    {
        return _state switch
        {
            ParserState.Ground => ProcessGroundCharacter(value),
            ParserState.Escape => ProcessEscapeCharacter(value),
            ParserState.EscapeCharset => ProcessEscapeCharsetCharacter(),
            ParserState.Csi => ProcessCsiCharacter(value, ref responses),
            ParserState.Osc => ProcessOscCharacter(value),
            ParserState.OscEscape => ProcessOscEscapeCharacter(value),
            _ => false
        };
    }

    private bool ProcessGroundCharacter(char value)
    {
        if (value == '\x1B')
        {
            _state = ParserState.Escape;
            return false;
        }

        return value switch
        {
            '\r' => MoveCursor(_cursorRow, 0),
            '\n' => AdvanceLine(resetColumn: true),
            '\b' => MoveCursor(_cursorRow, Math.Max(0, _cursorColumn - 1)),
            '\t' => MoveCursor(_cursorRow, Math.Min(_width - 1, ((_cursorColumn / 8) + 1) * 8)),
            '\a' or '\0' or '\x7F' => false,
            _ when value >= ' ' => PutCharacter(value),
            _ => false
        };
    }

    private bool ProcessEscapeCharacter(char value)
    {
        switch (value)
        {
            case '[':
                _state = ParserState.Csi;
                _csiParameters.Clear();
                _csiPrivateMarker = '\0';
                return false;
            case ']':
                _state = ParserState.Osc;
                _oscBuffer.Clear();
                return false;
            case '7':
                SaveCursor();
                _state = ParserState.Ground;
                return false;
            case '8':
                _state = ParserState.Ground;
                return _cursorSaved && MoveCursor(_savedCursorRow, _savedCursorColumn);
            case 'c':
                ResetCore();
                return true;
            case 'D':
                _state = ParserState.Ground;
                return AdvanceLine(resetColumn: false);
            case 'E':
                _state = ParserState.Ground;
                bool moved = MoveCursor(_cursorRow, 0);
                return AdvanceLine(resetColumn: true) || moved;
            case 'M':
                _state = ParserState.Ground;
                return ReverseIndex();
            case '(':
            case ')':
                _state = ParserState.EscapeCharset;
                return false;
            case '\x1B':
                _state = ParserState.Escape;
                return false;
            default:
                _state = ParserState.Ground;
                return false;
        }
    }

    private bool ProcessEscapeCharsetCharacter()
    {
        _state = ParserState.Ground;
        return false;
    }

    private bool ProcessCsiCharacter(char value, ref List<byte[]>? responses)
    {
        if (value == '\x1B')
        {
            _state = ParserState.Escape;
            _csiParameters.Clear();
            _csiPrivateMarker = '\0';
            return false;
        }

        if (_csiParameters.Length == 0 && _csiPrivateMarker == '\0' && value is '?' or '>' or '!')
        {
            _csiPrivateMarker = value;
            return false;
        }

        if (value is >= '0' and <= '9' or ';' or ':')
        {
            _csiParameters.Append(value);
            return false;
        }

        if (value is >= '\x20' and <= '\x2F')
        {
            return false;
        }

        if (value is >= '\x40' and <= '\x7E')
        {
            bool changed = ExecuteCsi(value, ref responses);
            _state = ParserState.Ground;
            _csiParameters.Clear();
            _csiPrivateMarker = '\0';
            return changed;
        }

        _state = ParserState.Ground;
        _csiParameters.Clear();
        _csiPrivateMarker = '\0';
        return false;
    }

    private bool ProcessOscCharacter(char value)
    {
        if (value == '\a')
        {
            _state = ParserState.Ground;
        }
        else if (value == '\x1B')
        {
            _state = ParserState.OscEscape;
        }
        else if (_oscBuffer.Length < 4096)
        {
            _oscBuffer.Append(value);
        }

        return false;
    }

    private bool ProcessOscEscapeCharacter(char value)
    {
        _state = value == '\\' ? ParserState.Ground : ParserState.Osc;
        return false;
    }

    private bool ExecuteCsi(char final, ref List<byte[]>? responses)
    {
        int[] parameters = ParseParameters();
        int first = GetParameter(parameters, 0, 1);
        int second = GetParameter(parameters, 1, 1);
        switch (final)
        {
            case 'A':
                return MoveCursor(_cursorRow - first, _cursorColumn);
            case 'B':
            case 'e':
                return MoveCursor(_cursorRow + first, _cursorColumn);
            case 'C':
            case 'a':
                return MoveCursor(_cursorRow, _cursorColumn + first);
            case 'D':
                return MoveCursor(_cursorRow, _cursorColumn - first);
            case 'E':
                return MoveCursor(_cursorRow + first, 0);
            case 'F':
                return MoveCursor(_cursorRow - first, 0);
            case 'G':
            case '`':
                return MoveCursor(_cursorRow, Math.Clamp(first - 1, 0, _width - 1));
            case 'd':
                return MoveCursor(Math.Clamp(first - 1, 0, _maximumRows - 1), _cursorColumn);
            case 'H':
            case 'f':
                return MoveCursor(
                    Math.Clamp(first - 1, 0, _maximumRows - 1),
                    Math.Clamp(second - 1, 0, _width - 1));
            case 'J':
                return EraseDisplay(GetParameter(parameters, 0, 0));
            case 'K':
                return EraseLine(GetParameter(parameters, 0, 0));
            case 'm':
                ApplySgr(parameters);
                return false;
            case 's':
                SaveCursor();
                return false;
            case 'u':
                return _cursorSaved && MoveCursor(_savedCursorRow, _savedCursorColumn);
            case 'n':
                HandleDeviceStatusQuery(parameters, ref responses);
                return false;
            case 'c':
                if (_csiPrivateMarker == '\0' && (parameters.Length == 0 || first == 0))
                {
                    AddResponse(ref responses, Ascii.GetBytes("\x1B[?1;2c"));
                }
                return false;
            case 'h':
            case 'l':
                return SetMode(final == 'h', parameters);
            case '@':
                return InsertCharacters(first);
            case 'P':
                return DeleteCharacters(first);
            case 'X':
                return EraseCharacters(first);
            case 'L':
                return InsertLines(first);
            case 'M':
                return DeleteLines(first);
            case 'S':
                return ScrollRows(first);
            case 'T':
                return ScrollRows(-first);
            default:
                return false;
        }
    }

    private void HandleDeviceStatusQuery(int[] parameters, ref List<byte[]>? responses)
    {
        if (_csiPrivateMarker != '\0' || parameters.Length == 0)
        {
            return;
        }

        switch (parameters[0])
        {
            case 5:
                AddResponse(ref responses, DeviceStatusOkResponse.ToArray());
                break;
            case 6:
                AddResponse(
                    ref responses,
                    Ascii.GetBytes($"\x1B[{_cursorRow + 1};{Math.Min(_width, _cursorColumn + 1)}R"));
                break;
        }
    }

    private bool SetMode(bool enabled, int[] parameters)
    {
        if (_csiPrivateMarker != '?' || parameters.Length == 0)
        {
            return false;
        }

        bool changed = false;
        foreach (int parameter in parameters)
        {
            if (parameter == 25 && _cursorVisible != enabled)
            {
                _cursorVisible = enabled;
                changed = true;
            }
        }

        return changed;
    }

    private void ApplySgr(int[] parameters)
    {
        if (parameters.Length == 0)
        {
            parameters = [0];
        }

        for (int index = 0; index < parameters.Length; index++)
        {
            int parameter = parameters[index];
            switch (parameter)
            {
                case 0:
                    _foreground = AnsiTerminalColor.Default;
                    _background = AnsiTerminalColor.Default;
                    _bold = false;
                    _inverse = false;
                    break;
                case 1:
                    _bold = true;
                    break;
                case 22:
                    _bold = false;
                    break;
                case 7:
                    _inverse = true;
                    break;
                case 27:
                    _inverse = false;
                    break;
                case 39:
                    _foreground = AnsiTerminalColor.Default;
                    break;
                case 49:
                    _background = AnsiTerminalColor.Default;
                    break;
                default:
                    if (parameter is >= 30 and <= 37)
                    {
                        _foreground = (AnsiTerminalColor)(parameter - 29);
                    }
                    else if (parameter is >= 40 and <= 47)
                    {
                        _background = (AnsiTerminalColor)(parameter - 39);
                    }
                    else if (parameter is >= 90 and <= 97)
                    {
                        _foreground = (AnsiTerminalColor)(parameter - 81);
                    }
                    else if (parameter is >= 100 and <= 107)
                    {
                        _background = (AnsiTerminalColor)(parameter - 91);
                    }
                    else if (parameter is 38 or 48 && index + 2 < parameters.Length && parameters[index + 1] == 5)
                    {
                        // 256 色不是 REPL 必需能力，跳过其颜色索引，避免影响后续参数。
                        index += 2;
                    }
                    break;
            }
        }
    }

    private bool PutCharacter(char value)
    {
        if (_cursorColumn >= _width)
        {
            AdvanceLine(resetColumn: true);
        }

        EnsureRow(_cursorRow);
        _rows[_cursorRow][_cursorColumn] = new(value, _foreground, _background, _bold, _inverse);
        _cursorColumn++;
        if (_cursorColumn >= _width)
        {
            AdvanceLine(resetColumn: true);
        }

        return true;
    }

    private bool AdvanceLine(bool resetColumn)
    {
        int previousRow = _cursorRow;
        int previousColumn = _cursorColumn;
        if (resetColumn)
        {
            _cursorColumn = 0;
        }

        if (_cursorRow + 1 >= _maximumRows)
        {
            _rows.RemoveAt(0);
            _rows.Add(CreateRow());
            _cursorRow = _maximumRows - 1;
        }
        else
        {
            _cursorRow++;
            EnsureRow(_cursorRow);
        }

        return previousRow != _cursorRow || previousColumn != _cursorColumn;
    }

    private bool ReverseIndex()
    {
        if (_cursorRow > 0)
        {
            _cursorRow--;
            return true;
        }

        _rows.Insert(0, CreateRow());
        if (_rows.Count > _maximumRows)
        {
            _rows.RemoveAt(_rows.Count - 1);
        }

        return true;
    }

    private bool MoveCursor(int row, int column)
    {
        int normalizedRow = Math.Clamp(row, 0, _maximumRows - 1);
        int normalizedColumn = Math.Clamp(column, 0, _width - 1);
        EnsureRow(normalizedRow);
        bool changed = normalizedRow != _cursorRow || normalizedColumn != _cursorColumn;
        _cursorRow = normalizedRow;
        _cursorColumn = normalizedColumn;
        return changed;
    }

    private void SaveCursor()
    {
        _savedCursorRow = _cursorRow;
        _savedCursorColumn = _cursorColumn;
        _cursorSaved = true;
    }

    private bool EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseCells(_rows[_cursorRow], _cursorColumn, _width - 1);
                for (int row = _cursorRow + 1; row < _rows.Count; row++)
                {
                    ClearRow(_rows[row]);
                }
                return true;
            case 1:
                for (int row = 0; row < _cursorRow; row++)
                {
                    ClearRow(_rows[row]);
                }
                EraseCells(_rows[_cursorRow], 0, _cursorColumn);
                return true;
            case 2:
                foreach (AnsiTerminalCell[] row in _rows)
                {
                    ClearRow(row);
                }
                return true;
            case 3:
                _rows.Clear();
                _rows.Add(CreateRow());
                _cursorRow = 0;
                _cursorColumn = 0;
                return true;
            default:
                return false;
        }
    }

    private bool EraseLine(int mode)
    {
        AnsiTerminalCell[] row = _rows[_cursorRow];
        switch (mode)
        {
            case 0:
                EraseCells(row, _cursorColumn, _width - 1);
                return true;
            case 1:
                EraseCells(row, 0, _cursorColumn);
                return true;
            case 2:
                ClearRow(row);
                return true;
            default:
                return false;
        }
    }

    private bool InsertCharacters(int count)
    {
        int amount = Math.Clamp(count, 1, _width);
        AnsiTerminalCell[] row = _rows[_cursorRow];
        for (int index = _width - 1; index >= _cursorColumn + amount; index--)
        {
            row[index] = row[index - amount];
        }
        EraseCells(row, _cursorColumn, Math.Min(_width - 1, _cursorColumn + amount - 1));
        return true;
    }

    private bool DeleteCharacters(int count)
    {
        int amount = Math.Clamp(count, 1, _width);
        AnsiTerminalCell[] row = _rows[_cursorRow];
        for (int index = _cursorColumn; index < _width - amount; index++)
        {
            row[index] = row[index + amount];
        }
        EraseCells(row, _width - amount, _width - 1);
        return true;
    }

    private bool EraseCharacters(int count)
    {
        int amount = Math.Clamp(count, 1, _width - _cursorColumn);
        EraseCells(_rows[_cursorRow], _cursorColumn, _cursorColumn + amount - 1);
        return true;
    }

    private bool InsertLines(int count)
    {
        int amount = Math.Clamp(count, 1, _maximumRows);
        for (int index = 0; index < amount; index++)
        {
            _rows.Insert(_cursorRow, CreateRow());
        }
        while (_rows.Count > _maximumRows)
        {
            _rows.RemoveAt(_rows.Count - 1);
        }
        return true;
    }

    private bool DeleteLines(int count)
    {
        int amount = Math.Clamp(count, 1, _maximumRows);
        for (int index = 0; index < amount && _cursorRow < _rows.Count; index++)
        {
            _rows.RemoveAt(_cursorRow);
        }
        if (_rows.Count == 0)
        {
            _rows.Add(CreateRow());
        }
        _cursorRow = Math.Clamp(_cursorRow, 0, _rows.Count - 1);
        return true;
    }

    private bool ScrollRows(int signedCount)
    {
        if (signedCount == 0 || _rows.Count == 0)
        {
            return false;
        }

        int amount = Math.Min(Math.Abs(signedCount), _rows.Count);
        if (signedCount > 0)
        {
            _rows.RemoveRange(0, amount);
            for (int index = 0; index < amount; index++)
            {
                _rows.Add(CreateRow());
            }
        }
        else
        {
            _rows.InsertRange(0, Enumerable.Range(0, amount).Select(_ => CreateRow()));
            while (_rows.Count > _maximumRows)
            {
                _rows.RemoveAt(_rows.Count - 1);
            }
        }

        _cursorRow = Math.Clamp(_cursorRow, 0, _rows.Count - 1);
        return true;
    }

    private void EnsureRow(int row)
    {
        while (_rows.Count <= row && _rows.Count < _maximumRows)
        {
            _rows.Add(CreateRow());
        }
    }

    private AnsiTerminalCell[] CreateRow() => Enumerable.Repeat(AnsiTerminalCell.Blank, _width).ToArray();

    private static bool IsBlank(AnsiTerminalCell cell) =>
        cell.Character == ' '
        && cell.Foreground == AnsiTerminalColor.Default
        && cell.Background == AnsiTerminalColor.Default
        && !cell.Bold
        && !cell.Inverse;

    private static void ClearRow(AnsiTerminalCell[] row) => Array.Fill(row, AnsiTerminalCell.Blank);

    private static void EraseCells(AnsiTerminalCell[] row, int start, int end)
    {
        if (start > end || start >= row.Length || end < 0)
        {
            return;
        }

        Array.Fill(row, AnsiTerminalCell.Blank, Math.Max(0, start), Math.Min(row.Length - 1, end) - Math.Max(0, start) + 1);
    }

    private int[] ParseParameters()
    {
        if (_csiParameters.Length == 0)
        {
            return [];
        }

        return _csiParameters
            .ToString()
            .Split(';')
            .Select(value =>
            {
                int separator = value.IndexOf(':');
                string first = separator >= 0 ? value[..separator] : value;
                return int.TryParse(first, out int parsed) ? parsed : 0;
            })
            .ToArray();
    }

    private static int GetParameter(int[] parameters, int index, int fallback)
    {
        if (index >= parameters.Length || parameters[index] == 0)
        {
            return fallback;
        }

        return Math.Max(1, parameters[index]);
    }

    private static void AddResponse(ref List<byte[]>? responses, byte[] response)
    {
        responses ??= [];
        responses.Add(response);
    }

    private enum ParserState
    {
        Ground,
        Escape,
        EscapeCharset,
        Csi,
        Osc,
        OscEscape
    }
}
