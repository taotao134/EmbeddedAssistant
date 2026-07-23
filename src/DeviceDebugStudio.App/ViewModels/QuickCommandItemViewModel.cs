using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceDebugStudio.Core.Profiles;
using DeviceDebugStudio.Core.Protocol;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;

namespace DeviceDebugStudio.App.ViewModels;

public partial class QuickCommandItemViewModel : ObservableObject
{
    public QuickCommandItemViewModel(QuickCommand command)
    {
        Id = command.Id;
        name = command.Name;
        payload = command.Payload;
        template = string.IsNullOrEmpty(command.Template) ? command.Payload : command.Template;
        isHex = command.IsHex;
        lineEnding = command.LineEnding;
        checksum = command.Checksum;
        checksumLittleEndian = command.ChecksumLittleEndian;
        repeatIntervalMs = command.RepeatIntervalMs;
        shortcut = command.Shortcut;
        usageCount = command.UsageCount;
        lastUsedAt = command.LastUsedAt;
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
        if (selectedVariableSet is not null && selectedVariableSet.Variables.Count == 0)
        {
            selectedVariableSet.Variables.Add(CreateEmptyVariable());
            selectedVariableSet.Variables.Add(CreateEmptyVariable());
        }
        else if (selectedVariableSet is not null && selectedVariableSet.Variables.Count % 2 != 0)
        {
            selectedVariableSet.Variables.Add(CreateEmptyVariable());
        }
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
    private string shortcut;

    [ObservableProperty]
    private long usageCount;

    [ObservableProperty]
    private DateTimeOffset? lastUsedAt;

    [ObservableProperty]
    private GridLength nameColumnWidth;

    [ObservableProperty]
    private GridLength payloadColumnWidth;

    [ObservableProperty]
    private bool isRepeating;

    [ObservableProperty]
    private bool isDropTarget;

    [ObservableProperty]
    private bool isDropTargetAfter;

    [ObservableProperty]
    private bool isExpanded;

    public string UsageText => UsageCount == 0 ? "未使用" : $"使用 {UsageCount} 次";
    public string UsageShortText => UsageCount > 999 ? "999+" : UsageCount.ToString();
    public string VariableSetCountText => $"{VariableSets.Count} 套方案";
    public string TemplateOrPayload => string.IsNullOrEmpty(Template) ? Payload : Template;
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
        Payload = Payload,
        Template = Template,
        IsHex = IsHex,
        LineEnding = LineEnding,
        Checksum = Checksum,
        ChecksumLittleEndian = ChecksumLittleEndian,
        RepeatIntervalMs = Math.Max(10, RepeatIntervalMs),
        RepeatEnabled = false,
        Shortcut = Shortcut,
        UsageCount = UsageCount,
        LastUsedAt = LastUsedAt,
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
        OnPropertyChanged(nameof(TemplateOrPayload));
        NotifyVariablePresentationChanged();
    }

    partial void OnTemplateChanged(string value)
    {
        OnPropertyChanged(nameof(TemplateOrPayload));
        SynchronizeTemplateVariables();
    }

    partial void OnSelectedVariableSetChanged(QuickCommandVariableSetItemViewModel? value) =>
        NotifyVariablePresentationChanged();

    private IReadOnlyList<string> GetTemplateVariableNames()
    {
        List<string> names = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in VariableRegex.Matches(Template))
        {
            string name = match.Groups[1].Value;
            if (seen.Add(name))
            {
                names.Add(name);
            }
        }
        return names;
    }

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

    private static readonly Regex VariableRegex = new(
        @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static QuickCommandVariableItemViewModel CreateEmptyVariable() =>
        new(new QuickCommandVariable { Name = string.Empty });

    private static GridLength CreateStarWidth(double value, double fallback) =>
        new(double.IsFinite(value) && value > 0 ? value : fallback, GridUnitType.Star);

    private static double GetColumnWeight(GridLength width, double fallback) =>
        double.IsFinite(width.Value) && width.Value > 0 ? width.Value : fallback;
}
