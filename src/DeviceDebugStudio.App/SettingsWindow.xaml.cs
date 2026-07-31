using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DeviceDebugStudio.App.ViewModels;
using DeviceDebugStudio.Infrastructure.Persistence;
using Microsoft.Win32;
using DrawingColor = System.Drawing.Color;
using WinFormsColorDialog = System.Windows.Forms.ColorDialog;
using WinFormsDialogResult = System.Windows.Forms.DialogResult;
using WinFormsIWin32Window = System.Windows.Forms.IWin32Window;
using Wpf.Ui.Appearance;

namespace DeviceDebugStudio.App;

public partial class SettingsWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private string _selectedPage = "TerminalDisplay";

    public SettingsWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Title = $"设置 · {App.MainWindowTitle}";
        DebugLoggingToggle.IsChecked = viewModel.DebugLoggingEnabled;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        App.ApplyWindowTitleBarTheme(this, ApplicationThemeManager.GetAppTheme());
    }

    private void OnNavigationPageChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string page }
            || TerminalDisplayPage is null
            || OnlineUpdatePage is null
            || DiagnosticsPage is null)
        {
            return;
        }

        _selectedPage = page;
        TerminalDisplayPage.Visibility = page == "TerminalDisplay" ? Visibility.Visible : Visibility.Collapsed;
        OnlineUpdatePage.Visibility = page == "OnlineUpdate" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPage.Visibility = page == "Diagnostics" ? Visibility.Visible : Visibility.Collapsed;
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

    private void OnOpenSeparatorColorPaletteClick(object sender, RoutedEventArgs e) =>
        OpenColorPalette(
            _viewModel.TerminalSeparatorColor,
            App.DefaultTerminalSeparatorColor,
            color => _viewModel.TerminalSeparatorColor = color);

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

    private void OnTerminalSeparatorColorLostFocus(object sender, RoutedEventArgs e)
    {
        string normalized;
        if (!App.TryNormalizeTerminalColor(TerminalSeparatorColorTextBox.Text, out normalized))
        {
            normalized = Application.Current.Resources["TerminalSeparatorBrush"] is SolidColorBrush brush
                ? App.NormalizeTerminalColor(brush.Color.ToString(), App.DefaultTerminalSeparatorColor)
                : App.DefaultTerminalSeparatorColor;
        }
        _viewModel.TerminalSeparatorColor = normalized;
    }

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
        if (_selectedPage == "OnlineUpdate")
        {
            _viewModel.ResetOnlineUpdateSettings();
            return;
        }

        if (_selectedPage == "Diagnostics")
        {
            _viewModel.ResetDiagnosticSettings();
            DebugLoggingToggle.IsChecked = false;
            DiagnosticsStatusText.Text = "诊断设置已恢复默认值";
            return;
        }

        _viewModel.ResetTerminalDisplaySettings();
    }

    private void OnDebugLoggingClick(object sender, RoutedEventArgs e)
    {
        bool enabled = DebugLoggingToggle.IsChecked == true;
        _viewModel.DebugLoggingEnabled = enabled;
        App.ConfigureDiagnosticLogging(enabled);
        bool markerMatches = File.Exists(AppPaths.DebugLoggingMarkerPath) == enabled;
        DiagnosticsStatusText.Text = markerMatches
            ? enabled ? "调试日志已开启" : "调试日志已关闭"
            : "调试日志状态写入失败，请检查日志目录权限";
    }

    private void OnOpenDiagnosticFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DiagnosticsDirectory);
            Process.Start(new ProcessStartInfo(AppPaths.DiagnosticsDirectory) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            DiagnosticsStatusText.Text = $"日志目录打开失败：{exception.Message}";
        }
    }

    private void OnClearDiagnosticLogsClick(object sender, RoutedEventArgs e)
    {
        MessageBoxResult result = MessageBox.Show(
            this,
            "确定清空调试日志和更新失败日志吗？",
            "清空日志",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            int removed = App.ClearDiagnosticLogs();
            DiagnosticsStatusText.Text = $"已清空 {removed} 个日志文件";
        }
        catch (Exception exception)
        {
            DiagnosticsStatusText.Text = $"日志清理失败：{exception.Message}";
        }
    }

    private void OnExportDiagnosticBundleClick(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new()
        {
            Title = "导出诊断包",
            Filter = "ZIP 压缩包 (*.zip)|*.zip",
            FileName = $"DeviceDebugStudio-Diagnostics-{DateTime.Now:yyyyMMdd_HHmmss}.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            using FileStream output = new(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None);
            using ZipArchive archive = new(output, ZipArchiveMode.Create);
            AddLogDirectoryToArchive(archive, AppPaths.DiagnosticsDirectory, "Logs");
            AddLogDirectoryToArchive(archive, AppPaths.UpdateDiagnosticsDirectory, "Updates");
            ZipArchiveEntry information = archive.CreateEntry("diagnostic-info.txt", CompressionLevel.Optimal);
            using (StreamWriter writer = new(information.Open(), new UTF8Encoding(false)))
            {
                writer.WriteLine($"导出时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
                writer.WriteLine($"程序版本：{typeof(App).Assembly.GetName().Version}");
                writer.WriteLine($"操作系统：{RuntimeInformation.OSDescription}");
                writer.WriteLine($"运行时：{RuntimeInformation.FrameworkDescription}");
                writer.WriteLine($"调试日志：{(App.DiagnosticLoggingEnabled ? "开启" : "关闭")}");
            }
            DiagnosticsStatusText.Text = $"诊断包已导出：{dialog.FileName}";
        }
        catch (Exception exception)
        {
            DiagnosticsStatusText.Text = $"诊断包导出失败：{exception.Message}";
        }
    }

    private void OnOpenGitHubIssueClick(object sender, RoutedEventArgs e)
    {
        string repository = NormalizeGitHubRepository(_viewModel.GitHubRepository);
        if (string.IsNullOrEmpty(repository))
        {
            DiagnosticsStatusText.Text = "请先在“GitHub 更新”中填写有效仓库";
            return;
        }

        string version = typeof(App).Assembly.GetName().Version?.ToString() ?? "未知";
        string title = $"[诊断] 嵌入式调试台 {version}";
        string body = $"程序版本：{version}\n操作系统：{RuntimeInformation.OSDescription}\n\n问题描述：\n\n复现步骤：\n\n诊断包：请将“导出诊断包”生成的 ZIP 拖入此问题。";
        string url = $"https://github.com/{repository}/issues/new?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            DiagnosticsStatusText.Text = "已打开 GitHub 问题页面，请将诊断 ZIP 拖入问题描述";
        }
        catch (Exception exception)
        {
            DiagnosticsStatusText.Text = $"GitHub 问题页面打开失败：{exception.Message}";
        }
    }

    private static void AddLogDirectoryToArchive(ZipArchive archive, string directory, string entryDirectory)
    {
        foreach (string path in Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly))
        {
            ZipArchiveEntry entry = archive.CreateEntry($"{entryDirectory}/{Path.GetFileName(path)}", CompressionLevel.Optimal);
            using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using Stream destination = entry.Open();
            input.CopyTo(destination);
        }
    }

    private static string NormalizeGitHubRepository(string value)
    {
        string candidate = value.Trim();
        if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
        {
            if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }
            candidate = uri.AbsolutePath.Trim('/');
        }

        if (candidate.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[..^4];
        }
        string[] parts = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 ? $"{parts[0]}/{parts[1]}" : string.Empty;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private sealed class DialogOwner(nint handle) : WinFormsIWin32Window
    {
        public nint Handle { get; } = handle;
    }
}
