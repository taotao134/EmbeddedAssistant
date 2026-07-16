using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DeviceDebugStudio.Core.Protocol;

public static partial class ByteText
{
    public static byte[] ParseInput(string text, bool isHex, Encoding textEncoding) =>
        isHex ? ParseHex(text) : textEncoding.GetBytes(text);

    public static byte[] ParseHex(string text)
    {
        string normalized = text.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase);
        string compact = NonHexSeparatorRegex().Replace(normalized, string.Empty);
        if (compact.Length == 0)
        {
            return [];
        }

        if ((compact.Length & 1) != 0)
        {
            throw new FormatException("HEX 数据必须包含完整字节。 ");
        }

        byte[] result = new byte[compact.Length / 2];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = byte.Parse(compact.AsSpan(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return result;
    }

    public static string ToHex(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return string.Empty;
        }

        char[] result = new char[data.Length * 3 - 1];
        const string digits = "0123456789ABCDEF";
        for (int index = 0; index < data.Length; index++)
        {
            int output = index * 3;
            result[output] = digits[data[index] >> 4];
            result[output + 1] = digits[data[index] & 0x0F];
            if (index < data.Length - 1)
            {
                result[output + 2] = ' ';
            }
        }

        return new string(result);
    }

    public static string ToHexCompact(ReadOnlySpan<byte> data) => Convert.ToHexString(data);

    public static string ToEscapedHex(ReadOnlySpan<byte> data)
    {
        StringBuilder result = new(data.Length * 4);
        foreach (byte value in data)
        {
            result.Append("\\x").Append(value.ToString("X2", CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }

    public static string Decode(ReadOnlySpan<byte> data, Encoding encoding)
    {
        try
        {
            return encoding.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.UTF8.GetString(data);
        }
    }

    public static string EscapeControlCharacters(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        StringBuilder result = new(text.Length);
        foreach (char value in text)
        {
            switch (value)
            {
                case '\r':
                    result.Append("\\r");
                    break;
                case '\n':
                    result.Append("\\n");
                    break;
                case '\t':
                    result.Append("\\t");
                    break;
                case '\0':
                    result.Append("\\0");
                    break;
                case '\b':
                    result.Append("\\b");
                    break;
                case '\f':
                    result.Append("\\f");
                    break;
                case '\v':
                    result.Append("\\v");
                    break;
                case '\u2028':
                case '\u2029':
                    result.Append("\\u").Append(((int)value).ToString("X4", CultureInfo.InvariantCulture));
                    break;
                default:
                    if (char.IsControl(value))
                    {
                        result.Append("\\x").Append(((int)value).ToString("X2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        result.Append(value);
                    }
                    break;
            }
        }

        return result.ToString();
    }

    public static string ExpandVariables(string input, IReadOnlyDictionary<string, string> variables) =>
        VariableRegex().Replace(input, match => variables.TryGetValue(match.Groups[1].Value, out string? value) ? value : match.Value);

    [GeneratedRegex(@"[^0-9A-Fa-f]")]
    private static partial Regex NonHexSeparatorRegex();

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex VariableRegex();
}
