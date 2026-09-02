using System.IO;
using System.Windows;
using DeviceDebugStudio.App.ViewModels;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using DeviceDebugStudio.Infrastructure.Persistence;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace DeviceDebugStudio.App;

public partial class DeviceWorkspaceWindow : FluentWindow
{
    private readonly MainWindowViewModel _viewModel;

    public DeviceWorkspaceWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        App.ApplyWindowTitleBarTheme(this, ApplicationThemeManager.GetAppTheme());
    }

    private void OnOpenProfileActionsClick(object sender, RoutedEventArgs e)
    {
        ProfileActionsMenu.PlacementTarget = ProfileActionsButton;
        ProfileActionsMenu.IsOpen = true;
    }

    private async void OnDeleteProfileClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedProfile is null)
        {
            return;
        }

        int deleteCount = _viewModel.SelectedProfileDeleteCount;
        string message = deleteCount > 1
            ? $"检测到 {deleteCount} 个同名设备配置“{_viewModel.SelectedProfile.Name}”，将全部删除。原 SSCOM 文件不会受影响。"
            : $"删除设备配置“{_viewModel.SelectedProfile.Name}”？原 SSCOM 文件不会受影响。";
        MessageBoxResult result = MessageBox.Show(
            this,
            message,
            "删除设备配置",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            await _viewModel.DeleteSelectedProfileAsync();
        }
    }

    private async void OnImportLegacyClick(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "选择 SSCOM 或 NetAssist 配置文件",
            Filter = "配置文件 (*.ini;*.cfg)|*.ini;*.cfg|所有文件 (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.ImportLegacyFilesAsync(dialog.FileNames);
        }
    }

    private async void OnImportDeviceProfileClick(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "导入设备配置",
            Filter = "设备配置 JSON (*.json)|*.json|所有文件 (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.ImportDeviceProfilesAsync(dialog.FileNames);
        }
    }

    private async void OnExportDeviceProfileClick(object sender, RoutedEventArgs e)
    {
        string profileName = string.IsNullOrWhiteSpace(_viewModel.ProfileName) ? "设备配置" : _viewModel.ProfileName;
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            profileName = profileName.Replace(invalid, '_');
        }

        SaveFileDialog dialog = new()
        {
            Title = "导出当前设备配置",
            Filter = "设备配置 JSON (*.json)|*.json",
            FileName = $"{profileName}.json"
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.ExportSelectedProfileAsync(dialog.FileName);
        }
    }

    private async void OnOpenCaptureClick(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "打开捕获数据库",
            Filter = "捕获数据库 (*.db)|*.db|所有文件 (*.*)|*.*",
            InitialDirectory = AppPaths.CaptureDirectory
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.OpenCaptureAsync(dialog.FileName);
        }
    }

    private async void OnConfigureProfileDirectoryClick(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "选择设备配置保存目录",
            InitialDirectory = _viewModel.ProfileDirectory,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.ConfigureProfileDirectoryAsync(dialog.FolderName);
        }
    }
}
