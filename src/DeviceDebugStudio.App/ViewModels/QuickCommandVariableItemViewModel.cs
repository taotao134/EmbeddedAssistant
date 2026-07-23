using CommunityToolkit.Mvvm.ComponentModel;
using DeviceDebugStudio.Core.Profiles;

namespace DeviceDebugStudio.App.ViewModels;

public partial class QuickCommandVariableItemViewModel : ObservableObject
{
    public QuickCommandVariableItemViewModel(QuickCommandVariable variable)
    {
        Id = variable.Id;
        name = variable.Name;
        value = variable.Value;
        type = string.IsNullOrWhiteSpace(variable.Type) ? "文本" : variable.Type;
    }

    public Guid Id { get; }

    [ObservableProperty]
    private string name;

    [ObservableProperty]
    private string value;

    [ObservableProperty]
    private string type;

    public QuickCommandVariable ToModel() => new()
    {
        Id = Id,
        Name = Name.Trim(),
        Value = Value,
        Type = string.IsNullOrWhiteSpace(Type) ? "文本" : Type
    };
}
