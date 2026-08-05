using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceDebugStudio.Core.Profiles;
using DeviceDebugStudio.Core.Protocol;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;

namespace DeviceDebugStudio.App.ViewModels;

public partial class QuickCommandItemViewModel : ObservableObject
{
    public QuickCommandItemViewModel(QuickCommand command)
    {
        Id = command.Id;
        name = command.Name;
        string initialPayload = ByteText.NormalizeSscomCommandText(command.Payload);
        string initialTemplate = ByteText.NormalizeSscomCommandText(
            string.IsNullOrEmpty(command.Template) ? initialPayload : command.Template);
        if (ByteText.GetVariableNames(initialTemplate).Count == 0)
        {
            if (string.IsNullOrEmpty(initialPayload))
            {
                initialPayload = initialTemplate;
            }
            initialTemplate = initialPayload;
        }
        payload = initialPayload;
        template = initialTemplate;
        isHex = command.IsHex;
        lineEnding = command.LineEnding;
        checksum = command.Checksum;
        checksumLittleEndian = command.ChecksumLittleEndian;
        repeatIntervalMs = NormalizeInterval(command.RepeatIntervalMs);
        repeatIntervalText = FormatInterval(repeatIntervalMs);
        parameterRepeatIntervalMs = NormalizeInterval(command.ParameterRepeatIntervalMs);
        parameterRepeatIntervalText = FormatInterval(parameterRepeatIntervalMs);
        shortcut = command.Shortcut;
        usageCount = command.UsageCount;
        lastUsedAt = command.LastUsedAt;
        isPinned = command.IsPinned;
        pinnedOrder = command.PinnedOrder;
        nameColumnWidth = CreateStarWidth(command.NameColumnWeight, 132);
        payloadColumnWidth = CreateStarWidth(command.PayloadColumnWeight, 300);
        VariableSets.CollectionChanged += OnVariableSetsCollectionChanged;
        IEnumerable<QuickCommandVariableSet> variableSets = command.VariableSets;
        if (command.VariableSets.Count == 0)
        {
            variableSets =
            [
                new QuickCommandVariableSet
                {
                    Name = "默认",
                    Variables = command.Variables
                }
            ];
        }
        foreach (QuickCommandVariableSet variableSet in variableSets)
        {
            VariableSets.Add(new QuickCommandVariableSetItemViewModel(variableSet));
        }
        selectedVariableSet = VariableSets.FirstOrDefault(item => item.Id == command.SelectedVariableSetId)
            ?? VariableSets.FirstOrDefault();
        SynchronizeTemplateVariables();
    }

    public Guid Id { get; }

    public ObservableCollection<QuickCommandVariableSetItemViewModel> VariableSets { get; } = [];

    [ObservableProperty]
    private QuickCommandVariableSetItemViewModel? selectedVariableSet;

    [ObservableProperty]
    private string name;

    [ObservableProperty]
    private string payload;

    [ObservableProperty]
    private string template;

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
    private string repeatIntervalText = string.Empty;

    [ObservableProperty]
    private int parameterRepeatIntervalMs;

    [ObservableProperty]
    private string parameterRepeatIntervalText = string.Empty;

    [ObservableProperty]
    private string shortcut;

    [ObservableProperty]
    private long usageCount;

    [ObservableProperty]
    private DateTimeOffset? lastUsedAt;

    [ObservableProperty]
    private bool isPinned;

    [ObservableProperty]
    private int pinnedOrder;

    [ObservableProperty]
    private GridLength nameColumnWidth;

    [ObservableProperty]
    private GridLength payloadColumnWidth;

    [ObservableProperty]
    private bool isRepeating;

    [ObservableProperty]
    private bool isParameterRepeating;

    [ObservableProperty]
    private bool isDropTarget;

    [ObservableProperty]
    private bool isDropTargetAfter;

    [ObservableProperty]
    private bool isExpanded;

    public string UsageText => UsageCount == 0 ? "未使用" : $"使用 {UsageCount} 次";
    public string UsageShortText => UsageCount > 999 ? "999+" : UsageCount.ToString();
    public string VariableSetCountText => $"{VariableSets.Count} 套方案";
    public string TemplateOrPayload => HasTemplateVariables ? Template : Payload;
    public string ResolvedPayload => ByteText.ExpandVariables(TemplateOrPayload, SelectedVariableSet?.GetValues() ?? EmptyVariables);
    public bool HasTemplateVariables => GetTemplateVariableNames().Count > 0;
    public bool IsDirectPayloadMode => !HasTemplateVariables;
    public bool HasSelectedVariables => SelectedVariableSet?.Variables.Count > 0;

    public void RegisterUse()
    {
        UsageCount++;
        LastUsedAt = DateTimeOffset.Now;
        OnPropertyChanged(nameof(UsageText));
        OnPropertyChanged(nameof(UsageShortText));
    }

    public void CommitRepeatIntervalText()
    {
        RepeatIntervalMs = ParseInterval(RepeatIntervalText, RepeatIntervalMs);
        RepeatIntervalText = FormatInterval(RepeatIntervalMs);
    }

    public void CommitParameterRepeatIntervalText()
    {
        ParameterRepeatIntervalMs = ParseInterval(ParameterRepeatIntervalText, ParameterRepeatIntervalMs);
        ParameterRepeatIntervalText = FormatInterval(ParameterRepeatIntervalMs);
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    public void SynchronizeTemplateVariables()
    {
        IReadOnlyList<string> variableNames = GetTemplateVariableNames();
        foreach (QuickCommandVariableSetItemViewModel variableSet in VariableSets)
        {
            HashSet<string> existing = variableSet.Variables
                .Select(variable => variable.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string variableName in variableNames)
            {
                if (existing.Add(variableName))
                {
                    variableSet.Variables.Add(new QuickCommandVariableItemViewModel(new QuickCommandVariable
                    {
                        Name = variableName
                    }));
                }
            }
        }

        NotifyVariablePresentationChanged();
    }

    public QuickCommand ToModel() => new()
    {
        Id = Id,
        Name = Name,
        Payload = ByteText.NormalizeSscomCommandText(Payload),
        Template = ByteText.NormalizeSscomCommandText(Template),
        IsHex = IsHex,
        LineEnding = LineEnding,
        Checksum = Checksum,
        ChecksumLittleEndian = ChecksumLittleEndian,
        RepeatIntervalMs = Math.Max(10, RepeatIntervalMs),
        ParameterRepeatIntervalMs = Math.Clamp(ParameterRepeatIntervalMs, 10, 60_000),
        RepeatEnabled = false,
        Shortcut = Shortcut,
        UsageCount = UsageCount,
        LastUsedAt = LastUsedAt,
        IsPinned = IsPinned,
        PinnedOrder = PinnedOrder,
        NameColumnWeight = GetColumnWeight(NameColumnWidth, 132),
        PayloadColumnWeight = GetColumnWeight(PayloadColumnWidth, 300),
        Variables = [],
        VariableSets = VariableSets.Select(variableSet => variableSet.ToModel()).ToList(),
        SelectedVariableSetId = SelectedVariableSet?.Id
    };

    private void OnVariableSetsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.OldItems is not null)
        {
            foreach (QuickCommandVariableSetItemViewModel variableSet in args.OldItems)
            {
                variableSet.PropertyChanged -= OnVariableSetPropertyChanged;
            }
        }
        if (args.NewItems is not null)
        {
            foreach (QuickCommandVariableSetItemViewModel variableSet in args.NewItems)
            {
                variableSet.PropertyChanged += OnVariableSetPropertyChanged;
            }
        }

        OnPropertyChanged(nameof(VariableSets));
        NotifyVariablePresentationChanged();
    }

    private void OnVariableSetPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        OnPropertyChanged(nameof(VariableSets));
        NotifyVariablePresentationChanged();
    }

    partial void OnPayloadChanged(string value)
    {
        string normalized = ByteText.NormalizeSscomCommandText(value);
        if (!string.Equals(normalized, value, StringComparison.Ordinal))
        {
            payload = normalized;
            value = normalized;
            OnPropertyChanged(nameof(Payload));
        }

        if (!HasTemplateVariables && !string.Equals(template, value, StringComparison.Ordinal))
        {
            template = value;
            OnPropertyChanged(nameof(Template));
        }
        OnPropertyChanged(nameof(TemplateOrPayload));
        NotifyVariablePresentationChanged();
    }

    partial void OnTemplateChanged(string value)
    {
        string normalized = ByteText.NormalizeSscomCommandText(value);
        if (!string.Equals(normalized, value, StringComparison.Ordinal))
        {
            template = normalized;
            value = normalized;
            OnPropertyChanged(nameof(Template));
        }

        if (ByteText.GetVariableNames(value).Count == 0 && !string.Equals(payload, value, StringComparison.Ordinal))
        {
            payload = value;
            OnPropertyChanged(nameof(Payload));
        }
        OnPropertyChanged(nameof(TemplateOrPayload));
        SynchronizeTemplateVariables();
    }

    partial void OnSelectedVariableSetChanged(QuickCommandVariableSetItemViewModel? value) =>
        NotifyVariablePresentationChanged();

    partial void OnRepeatIntervalMsChanged(int value)
    {
        int normalized = NormalizeInterval(value);
        if (value != normalized)
        {
            RepeatIntervalMs = normalized;
            return;
        }

        string text = FormatInterval(normalized);
        if (!string.Equals(repeatIntervalText, text, StringComparison.Ordinal))
        {
            repeatIntervalText = text;
            OnPropertyChanged(nameof(RepeatIntervalText));
        }
    }

    partial void OnRepeatIntervalTextChanged(string value)
    {
        if (TryParseInterval(value, out int parsed))
        {
            RepeatIntervalMs = parsed;
        }
    }

    partial void OnParameterRepeatIntervalMsChanged(int value)
    {
        int normalized = NormalizeInterval(value);
        if (value != normalized)
        {
            ParameterRepeatIntervalMs = normalized;
            return;
        }

        string text = FormatInterval(normalized);
        if (!string.Equals(parameterRepeatIntervalText, text, StringComparison.Ordinal))
        {
            parameterRepeatIntervalText = text;
            OnPropertyChanged(nameof(ParameterRepeatIntervalText));
        }
    }

    partial void OnParameterRepeatIntervalTextChanged(string value)
    {
        if (TryParseInterval(value, out int parsed))
        {
            ParameterRepeatIntervalMs = parsed;
        }
    }

    private IReadOnlyList<string> GetTemplateVariableNames() => ByteText.GetVariableNames(Template);

    private void NotifyVariablePresentationChanged()
    {
        OnPropertyChanged(nameof(VariableSetCountText));
        OnPropertyChanged(nameof(ResolvedPayload));
        OnPropertyChanged(nameof(HasTemplateVariables));
        OnPropertyChanged(nameof(IsDirectPayloadMode));
        OnPropertyChanged(nameof(HasSelectedVariables));
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyVariables =
        new Dictionary<string, string>();

    private static GridLength CreateStarWidth(double value, double fallback) =>
        new(double.IsFinite(value) && value > 0 ? value : fallback, GridUnitType.Star);

    private static double GetColumnWeight(GridLength width, double fallback) =>
        double.IsFinite(width.Value) && width.Value > 0 ? width.Value : fallback;

    private static int ParseInterval(string value, int fallback) =>
        TryParseInterval(value, out int parsed) ? parsed : NormalizeInterval(fallback);

    private static bool TryParseInterval(string value, out int interval) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out interval)
        && interval is >= 10 and <= 60_000;

    private static int NormalizeInterval(int value) => Math.Clamp(value, 10, 60_000);

    private static string FormatInterval(int value) =>
        NormalizeInterval(value).ToString(CultureInfo.InvariantCulture);
}
