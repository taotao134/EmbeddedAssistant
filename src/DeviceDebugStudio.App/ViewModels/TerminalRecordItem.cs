using System.Globalization;
using System.Text;
using DeviceDebugStudio.Core.Protocol;
using DeviceDebugStudio.Core.Transports;

namespace DeviceDebugStudio.App.ViewModels;

public sealed record TerminalRecordItem(
    DateTimeOffset Timestamp,
    PacketDirection Direction,
    string Endpoint,
    int Size,
    byte[] Data,
    string Content,
    bool IsMessage,
    bool? SentAsHex = null)
{
    public string TimeText => Timestamp.ToString("HH:mm:ss.fff");

    public string DirectionText => Direction switch
    {
        PacketDirection.Receive => "RX",
        PacketDirection.Send => "TX",
        PacketDirection.Information => "INFO",
        PacketDirection.Error => "ERR",
        _ => "-"
    };

    public string GetDisplayContent(bool displayHex)
    {
        if (IsMessage)
        {
            return ByteText.EscapeControlCharacters(Content);
        }
        if (displayHex)
        {
            return ByteText.ToHex(Data);
        }
        return Direction == PacketDirection.Send && SentAsHex == true
            ? ByteText.ToEscapedHex(Data)
            : ByteText.EscapeControlCharacters(Content);
    }

    public string GetContinuousTextContent(bool displayHex)
    {
        if (displayHex || IsMessage || Direction == PacketDirection.Send && SentAsHex == true)
        {
            return GetDisplayContent(displayHex);
        }
        return Content;
    }

    public bool MatchesSearch(string? query, bool displayHex)
    {
        string searchText = query?.Trim() ?? string.Empty;
        if (searchText.Length == 0)
        {
            return true;
        }

        return TimeText.Contains(searchText, StringComparison.OrdinalIgnoreCase)
            || Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                .Contains(searchText, StringComparison.OrdinalIgnoreCase)
            || Size.ToString(CultureInfo.InvariantCulture).Contains(searchText, StringComparison.OrdinalIgnoreCase)
            || GetDisplayContent(displayHex).Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
            || Endpoint.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
            || GetDirectionSearchText().Contains(searchText, StringComparison.CurrentCultureIgnoreCase);
    }

    private string GetDirectionSearchText() => Direction switch
    {
        PacketDirection.Receive => "RX 收 接收",
        PacketDirection.Send => "TX 发 发送",
        PacketDirection.Information => "INFO 信息",
        PacketDirection.Error => "ERR 错误",
        _ => DirectionText
    };

    public static TerminalRecordItem FromPacket(TransportPacket packet, Encoding encoding)
    {
        bool isMessage = packet.Message is not null;
        string content = packet.Message ?? ByteText.Decode(packet.Data, encoding);
        return new TerminalRecordItem(
            packet.Timestamp,
            packet.Direction,
            packet.Endpoint,
            packet.Data.Length,
            packet.Data.ToArray(),
            content,
            isMessage,
            packet.SentAsHex);
    }
}
