using DeviceDebugStudio.Core.Protocol;
using DeviceDebugStudio.Core.Transports;

namespace DeviceDebugStudio.Core.Profiles;

public enum WorkspaceMode
{
    Serial,
    Network,
    Bluetooth,
    Modbus
}

public sealed record DeviceProfile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "新设备";
    public string Description { get; init; } = string.Empty;
    public WorkspaceMode WorkspaceMode { get; init; } = WorkspaceMode.Serial;
    public TransportSettings Transport { get; init; } = new SerialTransportSettings();
    public TerminalPreferences Terminal { get; init; } = new();
    public List<QuickCommandGroup> CommandGroups { get; init; } = [];
    public FrameTemplate FrameTemplate { get; init; } = new();
    public List<ChartBinding> ChartBindings { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed record TerminalPreferences
{
    public string EncodingName { get; init; } = "UTF-8";
    public bool SendAsHex { get; init; }
    public bool ReceiveAsHex { get; init; }
    public bool ShowTimestamp { get; init; } = true;
    public string LineEnding { get; init; } = "None";
    public int UiRecordLimit { get; init; } = 100_000;
}

public sealed record QuickCommandGroup
{
    public string Name { get; init; } = "常用命令";
    public List<QuickCommand> Commands { get; init; } = [];
}

public sealed record QuickCommand
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "命令";
    public string Payload { get; init; } = string.Empty;
    public bool IsHex { get; init; }
    public string LineEnding { get; init; } = "None";
    public ChecksumKind Checksum { get; init; }
    public bool ChecksumLittleEndian { get; init; } = true;
    public int RepeatIntervalMs { get; init; } = 1000;
    public bool RepeatEnabled { get; init; }
    public string Shortcut { get; init; } = string.Empty;
    public long UsageCount { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
}

public enum FramingMode
{
    Raw,
    Line,
    Delimiter,
    FixedLength,
    LengthField,
    IdleGap
}

public enum EscapeMode
{
    None,
    Slip,
    Cobs
}

public sealed record FrameTemplate
{
    public string Name { get; init; } = "原始数据";
    public FramingMode Mode { get; init; }
    public string DelimiterHex { get; init; } = "0D 0A";
    public int FixedLength { get; init; } = 8;
    public int LengthOffset { get; init; } = 1;
    public int LengthSize { get; init; } = 1;
    public int LengthAdjustment { get; init; }
    public bool LittleEndian { get; init; } = true;
    public int IdleGapMs { get; init; } = 20;
    public EscapeMode Escape { get; init; }
    public ChecksumKind Checksum { get; init; }
    public List<FrameField> Fields { get; init; } = [];
}

public enum FrameFieldType
{
    UInt8,
    Int8,
    UInt16,
    Int16,
    UInt32,
    Int32,
    Float32,
    Ascii,
    Bytes,
    BitField
}

public sealed record FrameField
{
    public string Name { get; init; } = "字段";
    public FrameFieldType Type { get; init; }
    public int Offset { get; init; }
    public int Length { get; init; } = 1;
    public bool LittleEndian { get; init; } = true;
    public int BitOffset { get; init; }
    public int BitLength { get; init; } = 1;
    public double Scale { get; init; } = 1;
    public double Bias { get; init; }
    public string Unit { get; init; } = string.Empty;
}

public sealed record ChartBinding
{
    public string Name { get; init; } = "通道";
    public string FieldName { get; init; } = string.Empty;
    public string? TextPattern { get; init; }
    public string Color { get; init; } = "#00796B";
    public bool Enabled { get; init; } = true;
}

public interface IDeviceProfileStore
{
    Task<IReadOnlyList<DeviceProfile>> LoadAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(DeviceProfile profile, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default);
}

public interface IConfigurableDeviceProfileStore : IDeviceProfileStore
{
    string DirectoryPath { get; }
    void SetDirectory(string directory);
}
