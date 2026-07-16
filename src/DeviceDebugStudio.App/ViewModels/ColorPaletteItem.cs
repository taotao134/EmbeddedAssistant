using CommunityToolkit.Mvvm.ComponentModel;

namespace DeviceDebugStudio.App.ViewModels;

public partial class ColorPaletteItem : ObservableObject
{
    public ColorPaletteItem(string color)
    {
        this.color = color;
    }

    [ObservableProperty]
    private string color;
}
