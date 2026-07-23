using System.ComponentModel;
using System.Windows;
using DeviceDebugStudio.Infrastructure.Updates;

namespace DeviceDebugStudio.App;

public partial class UpdateProgressWindow : Window
{
    private bool _allowClose;
    private bool _operationCompleted;

    public UpdateProgressWindow(UpdateCheckResult result)
    {
        InitializeComponent();
        CurrentVersionText.Text = result.CurrentVersion.ToString(3);
        TargetVersionText.Text = result.LatestVersion.ToString(3);
        PackageText.Text = result.Manifest.PackageSize is long size && size > 0
            ? $"更新包大小：{FormatByteSize(size)}"
            : "更新包大小：读取中…";
    }

    public bool IsCancellationRequested { get; private set; }

    public event EventHandler? CancellationRequested;

    public void ReportProgress(UpdateProgressInfo info)
    {
        if (!IsVisible || _allowClose || _operationCompleted || IsCancellationRequested)
        {
            return;
        }

        DownloadProgressBar.IsIndeterminate = info.Phase == UpdateProgressPhase.Downloading
            && info.TotalBytes is not > 0;
        DownloadProgressBar.Value = Math.Clamp(info.Progress, 0, 1);
        PercentText.Text = info.Phase == UpdateProgressPhase.PreparingToRestart
            ? "完成"
            : $"{info.Progress:P0}";
        StatusText.Text = info.Phase switch
        {
            UpdateProgressPhase.Downloading => "正在下载更新包…",
            UpdateProgressPhase.Verifying => "正在校验更新包…",
            UpdateProgressPhase.PreparingToRestart => "正在准备重启…",
            _ => "正在更新…"
        };

        if (info.TotalBytes is long total && total > 0)
        {
            PackageText.Text = $"已下载 {FormatByteSize(info.BytesDownloaded)} / {FormatByteSize(total)}";
        }
        else if (info.BytesDownloaded > 0)
        {
            PackageText.Text = $"已下载 {FormatByteSize(info.BytesDownloaded)}";
        }
    }

    public void MarkCompleted()
    {
        _operationCompleted = true;
        DownloadProgressBar.IsIndeterminate = false;
        DownloadProgressBar.Value = 1;
        PercentText.Text = "完成";
        StatusText.Text = "更新已准备，程序即将重启…";
        PackageText.Text = "更新包校验完成";
        CancelButton.IsEnabled = false;
    }

    public void AllowClose()
    {
        _allowClose = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        RequestCancellation();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        RequestCancellation();
        e.Cancel = true;
    }

    private void RequestCancellation()
    {
        if (IsCancellationRequested)
        {
            return;
        }

        IsCancellationRequested = true;
        CancelButton.IsEnabled = false;
        StatusText.Text = "正在取消更新…";
        PackageText.Text = "正在清理临时文件";
        CancellationRequested?.Invoke(this, EventArgs.Empty);
    }

    private static string FormatByteSize(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024d:0.0} MB";
        }

        if (bytes >= 1024)
        {
            return $"{bytes / 1024d:0.0} KB";
        }

        return $"{bytes:N0} B";
    }
}
