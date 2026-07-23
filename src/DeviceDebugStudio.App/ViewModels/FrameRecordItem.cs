using DeviceDebugStudio.Core.Protocol;

namespace DeviceDebugStudio.App.ViewModels;

public sealed record FrameRecordItem(DateTimeOffset Timestamp, int Length, string Hex, string Summary, bool ChecksumValid)
{
    public string TimeText => Timestamp.ToString("HH:mm:ss.fff");

    public static FrameRecordItem Create(DateTimeOffset timestamp, DecodedFrame decoded, string? templateName = null)
    {
        string summary = decoded.Error ?? (decoded.Fields.Count == 0
            ? "未配置字段"
            : string.Join("  ", decoded.Fields.Select(field => $"{field.Name}={field.Value}{field.Unit}")));
        if (!string.IsNullOrWhiteSpace(templateName))
        {
            summary = $"[{templateName}] {summary}";
        }
        return new FrameRecordItem(timestamp, decoded.Raw.Length, ByteText.ToHex(decoded.Raw), summary, decoded.ChecksumValid);
    }
}
