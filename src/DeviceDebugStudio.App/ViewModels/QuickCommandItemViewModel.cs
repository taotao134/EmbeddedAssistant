using CommunityToolkit.Mvvm.ComponentModel;
using DeviceDebugStudio.Core.Profiles;
using DeviceDebugStudio.Core.Protocol;

namespace DeviceDebugStudio.App.ViewModels;

public partial class QuickCommandItemViewModel : ObservableObject
{
    public QuickCommandItemViewModel(QuickCommand command)
    {
        Id = command.Id;
        name = command.Name;
        payload = command.Payload;
        isHex = command.IsHex;
        lineEnding = command.LineEnding;
        checksum = command.Checksum;
        checksumLittleEndian = command.ChecksumLittleEndian;
        repeatIntervalMs = command.RepeatIntervalMs;
        shortcut = command.Shortcut;
        usageCount = command.UsageCount;
        lastUsedAt = command.LastUsedAt;
    }

    public Guid Id { get; }

    [ObservableProperty]
    private string name;

    [ObservableProperty]
    private string payload;

    [ObservableProperty]
    private bool isHex;

    [ObservableProperty]
    private bool isSelectedForBulkDelete;

    [ObservableProperty]
    private string lineEnding;

    [ObservableProperty]
    private ChecksumKind checksum;

    [ObservableProperty]
    private bool checksumLittleEndian;

    [ObservableProperty]
    private int repeatIntervalMs;

    [ObservableProperty]
    private string shortcut;

    [ObservableProperty]
    private long usageCount;

    [ObservableProperty]
    private DateTimeOffset? lastUsedAt;

    [ObservableProperty]
    private bool isRepeating;

    [ObservableProperty]
    private bool isDropTarget;

    [ObservableProperty]
    private bool isDropTargetAfter;

    public string UsageText => UsageCount == 0 ? "未使用" : $"使用 {UsageCount} 次";
    public string UsageShortText => UsageCount > 999 ? "999+" : UsageCount.ToString();

    public void RegisterUse()
    {
        UsageCount++;
        LastUsedAt = DateTimeOffset.Now;
        OnPropertyChanged(nameof(UsageText));
        OnPropertyChanged(nameof(UsageShortText));
    }

    public QuickCommand ToModel() => new()
    {
        Id = Id,
        Name = Name,
        Payload = Payload,
        IsHex = IsHex,
        LineEnding = LineEnding,
        Checksum = Checksum,
        ChecksumLittleEndian = ChecksumLittleEndian,
        RepeatIntervalMs = Math.Max(10, RepeatIntervalMs),
        RepeatEnabled = false,
        Shortcut = Shortcut,
        UsageCount = UsageCount,
        LastUsedAt = LastUsedAt
    };
}
