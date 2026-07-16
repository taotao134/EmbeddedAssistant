using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DeviceDebugStudio.App.ViewModels;
using DrawingColor = System.Drawing.Color;
using WinFormsColorDialog = System.Windows.Forms.ColorDialog;
using WinFormsDialogResult = System.Windows.Forms.DialogResult;
using WinFormsIWin32Window = System.Windows.Forms.IWin32Window;

namespace DeviceDebugStudio.App;

public partial class SettingsWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public SettingsWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OnNavigationSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (TerminalDisplayPage is not null && e.NewValue is TreeViewItem { Tag: "TerminalDisplay" })
        {
            TerminalDisplayPage.Visibility = Visibility.Visible;
        }
    }

    private void OnAppearanceCategoryPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        AppearanceCategory.IsExpanded = !AppearanceCategory.IsExpanded;
        e.Handled = true;
    }

    private void OnTextColorSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ColorPaletteItem item })
        {
            _viewModel.TerminalTextColor = item.Color;
        }
    }

    private void OnBackgroundColorSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ColorPaletteItem item })
        {
            _viewModel.TerminalBackgroundColor = item.Color;
        }
    }

    private void OnTextColorSwatchDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button { DataContext: ColorPaletteItem item })
        {
            OpenColorPalette(
                item.Color,
                App.DefaultTerminalTextColor,
                color => _viewModel.UpdateTerminalPaletteColor(item, color, true));
            e.Handled = true;
        }
    }

    private void OnBackgroundColorSwatchDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button { DataContext: ColorPaletteItem item })
        {
            OpenColorPalette(
                item.Color,
                App.DefaultTerminalBackgroundColor,
                color => _viewModel.UpdateTerminalPaletteColor(item, color, false));
            e.Handled = true;
        }
    }

    private void OnOpenTextColorPaletteClick(object sender, RoutedEventArgs e) =>
        OpenColorPalette(
            _viewModel.TerminalTextColor,
            App.DefaultTerminalTextColor,
            color => _viewModel.TerminalTextColor = color);

    private void OnOpenBackgroundColorPaletteClick(object sender, RoutedEventArgs e) =>
        OpenColorPalette(
            _viewModel.TerminalBackgroundColor,
            App.DefaultTerminalBackgroundColor,
            color => _viewModel.TerminalBackgroundColor = color);

    private void OpenColorPalette(string currentColor, string fallbackColor, Action<string> applyColor)
    {
        string normalized = App.NormalizeTerminalColor(currentColor, fallbackColor);
        System.Windows.Media.Color wpfColor = (System.Windows.Media.Color)ColorConverter.ConvertFromString(normalized);
        using WinFormsColorDialog dialog = new()
        {
            AllowFullOpen = true,
            AnyColor = true,
            FullOpen = true,
            SolidColorOnly = true,
            Color = DrawingColor.FromArgb(wpfColor.R, wpfColor.G, wpfColor.B)
        };

        nint ownerHandle = new WindowInteropHelper(this).Handle;
        if (dialog.ShowDialog(new DialogOwner(ownerHandle)) == WinFormsDialogResult.OK)
        {
            applyColor($"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
        }
    }

    private void OnTerminalTextColorLostFocus(object sender, RoutedEventArgs e) =>
        NormalizeColorTextBox(TerminalTextColorTextBox, true);

    private void OnTerminalBackgroundColorLostFocus(object sender, RoutedEventArgs e) =>
        NormalizeColorTextBox(TerminalBackgroundColorTextBox, false);

    private void NormalizeColorTextBox(TextBox textBox, bool isTextColor)
    {
        string fallback = isTextColor ? App.DefaultTerminalTextColor : App.DefaultTerminalBackgroundColor;
        string normalized;
        if (!App.TryNormalizeTerminalColor(textBox.Text, out normalized))
        {
            string resourceKey = isTextColor ? "TerminalTextBrush" : "TerminalBackgroundBrush";
            normalized = Application.Current.Resources[resourceKey] is SolidColorBrush brush
                ? App.NormalizeTerminalColor(brush.Color.ToString(), fallback)
                : fallback;
        }

        if (isTextColor)
        {
            _viewModel.TerminalTextColor = normalized;
        }
        else
        {
            _viewModel.TerminalBackgroundColor = normalized;
        }
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        _viewModel.ResetTerminalDisplaySettings();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private sealed class DialogOwner(nint handle) : WinFormsIWin32Window
    {
        public nint Handle { get; } = handle;
    }
}
