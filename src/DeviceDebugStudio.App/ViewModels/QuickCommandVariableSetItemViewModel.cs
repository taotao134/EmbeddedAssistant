using CommunityToolkit.Mvvm.ComponentModel;
using DeviceDebugStudio.Core.Profiles;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DeviceDebugStudio.App.ViewModels;

public partial class QuickCommandVariableSetItemViewModel : ObservableObject
{
    public QuickCommandVariableSetItemViewModel(QuickCommandVariableSet variableSet)
    {
        Id = variableSet.Id;
        name = variableSet.Name;
        Variables.CollectionChanged += OnVariablesCollectionChanged;
        foreach (QuickCommandVariable variable in variableSet.Variables)
        {
            Variables.Add(new QuickCommandVariableItemViewModel(variable));
        }
    }

    public Guid Id { get; }

    public ObservableCollection<QuickCommandVariableItemViewModel> Variables { get; } = [];

    [ObservableProperty]
    private string name;

    [ObservableProperty]
    private bool isRenaming;

    public IReadOnlyDictionary<string, string> GetValues()
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (QuickCommandVariableItemViewModel variable in Variables)
        {
            string variableName = variable.Name.Trim();
            if (string.IsNullOrEmpty(variableName))
            {
                continue;
            }
            if (!values.ContainsKey(variableName))
            {
                values.Add(variableName, variable.Value);
            }
        }
        return values;
    }

    public QuickCommandVariableSet ToModel() => new()
    {
        Id = Id,
        Name = string.IsNullOrWhiteSpace(Name) ? "未命名套装" : Name.Trim(),
        Variables = Variables.Select(variable => variable.ToModel()).ToList()
    };

    private void OnVariablesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.OldItems is not null)
        {
            foreach (QuickCommandVariableItemViewModel variable in args.OldItems)
            {
                variable.PropertyChanged -= OnVariablePropertyChanged;
            }
        }
        if (args.NewItems is not null)
        {
            foreach (QuickCommandVariableItemViewModel variable in args.NewItems)
            {
                variable.PropertyChanged += OnVariablePropertyChanged;
            }
        }
        OnPropertyChanged(nameof(Variables));
    }

    private void OnVariablePropertyChanged(object? sender, PropertyChangedEventArgs args) =>
        OnPropertyChanged(nameof(Variables));
}
