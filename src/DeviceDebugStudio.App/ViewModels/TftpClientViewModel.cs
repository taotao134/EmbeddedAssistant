using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceDebugStudio.Core.Profiles;
using DeviceDebugStudio.Infrastructure.Transports;

namespace DeviceDebugStudio.App.ViewModels;

public sealed record TftpBlockSizeOption(int Value, string Name);

public partial class TftpClientViewModel : ObservableObject, IAsyncDisposable
{
    private readonly TftpClient _client = new();
    private CancellationTokenSource? _transferCancellation;
    private bool _suppressPreferenceChanged;
    private int _disposed;

    public TftpClientViewModel()
    {
        selectedBlockSize = BlockSizes[0];
    }

    public IReadOnlyList<TftpBlockSizeOption> BlockSizes { get; } =
    [
        new(512, "默认（512 字节）"),
        new(1024, "1024 字节"),
        new(1428, "1428 字节")
    ];

    public ObservableCollection<string> LogEntries { get; } = [];

    [ObservableProperty]
    private string logText = string.Empty;

    public event Action? PreferencesChanged;

    [ObservableProperty]
    private string host = "192.168.1.26";

    [ObservableProperty]
    private int port = 69;

    [ObservableProperty]
    private string localDirectory = string.Empty;

    [ObservableProperty]
    private string localFile = string.Empty;

    [ObservableProperty]
    private string remoteFile = string.Empty;

    [ObservableProperty]
    private TftpBlockSizeOption selectedBlockSize;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isProgressIndeterminate;

    [ObservableProperty]
    private int progressPercent;

    [ObservableProperty]
    private string progressText = "等待传输";

    [ObservableProperty]
    private string statusText = "就绪";

    public TftpPreferences CreatePreferences() => new()
    {
        Host = Host.Trim(),
        Port = Math.Clamp(Port, 1, 65535),
        LocalDirectory = GetLocalDirectory(),
        LocalFile = LocalFile.Trim(),
        RemoteFile = RemoteFile.Trim(),
        BlockSize = SelectedBlockSize.Value
    };

    public void ApplyPreferences(TftpPreferences preferences)
    {
        _suppressPreferenceChanged = true;
        try
        {
            Host = string.IsNullOrWhiteSpace(preferences.Host) ? "192.168.1.26" : preferences.Host;
            Port = Math.Clamp(preferences.Port, 1, 65535);
            LocalFile = preferences.LocalFile ?? string.Empty;
            LocalDirectory = string.IsNullOrWhiteSpace(preferences.LocalDirectory)
                ? GetDirectoryFromPath(LocalFile)
                : preferences.LocalDirectory;
            RemoteFile = preferences.RemoteFile ?? string.Empty;
            SelectedBlockSize = BlockSizes.FirstOrDefault(item => item.Value == preferences.BlockSize) ?? BlockSizes[0];
        }
        finally
        {
            _suppressPreferenceChanged = false;
        }
    }

    public void SetLocalFile(string path, bool updateRemoteFile = true)
    {
        LocalFile = path;
        string directory = GetDirectoryFromPath(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            LocalDirectory = directory;
        }

        if (updateRemoteFile && !string.IsNullOrWhiteSpace(path))
        {
            string fileName = Path.GetFileName(path);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                RemoteFile = fileName;
            }
        }
    }

    public void SetLocalDirectory(string path) => LocalDirectory = path;

    [RelayCommand(CanExecute = nameof(CanStartTransfer))]
    private Task GetAsync() => RunTransferAsync(upload: false);

    [RelayCommand(CanExecute = nameof(CanStartTransfer))]
    private Task PutAsync() => RunTransferAsync(upload: true);

    [RelayCommand(CanExecute = nameof(CanCancelTransfer))]
    private void Break()
    {
        if (_transferCancellation is null)
        {
            return;
        }

        StatusText = "正在中止传输…";
        _transferCancellation.Cancel();
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogEntries.Clear();
        LogText = string.Empty;
    }

    private bool CanStartTransfer() => !IsBusy;

    private bool CanCancelTransfer() => IsBusy;

    private async Task RunTransferAsync(bool upload)
    {
        if (IsBusy)
        {
            return;
        }

        TftpTransferOptions options = new()
        {
            Host = Host.Trim(),
            Port = Port,
            LocalFile = LocalFile.Trim(),
            RemoteFile = RemoteFile.Trim(),
            BlockSize = SelectedBlockSize.Value
        };

        using CancellationTokenSource cancellation = new();
        _transferCancellation = cancellation;
        IsBusy = true;
        IsProgressIndeterminate = !upload;
        ProgressPercent = 0;
        ProgressText = upload ? "准备上传" : "准备下载";
        StatusText = upload ? "正在上传…" : "正在下载…";
        AddLog(upload
            ? $"开始上传：{options.LocalFile} → {options.Host}:{options.Port}/{options.RemoteFile}"
            : $"开始下载：{options.Host}:{options.Port}/{options.RemoteFile} → {options.LocalFile}");
        if (upload)
        {
            AddLog("等待设备确认上传请求（设备可能正在准备升级区）…");
        }

        Progress<TftpTransferProgress> progress = new(item => OnProgress(item, upload));
        try
        {
            if (upload)
            {
                await _client.UploadAsync(options, progress, cancellation.Token).ConfigureAwait(true);
            }
            else
            {
                await _client.DownloadAsync(options, progress, cancellation.Token).ConfigureAwait(true);
            }

            IsProgressIndeterminate = false;
            ProgressPercent = 100;
            ProgressText = "100%";
            StatusText = upload ? "上传完成" : "下载完成";
            AddLog(upload ? "上传完成。" : "下载完成。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            IsProgressIndeterminate = false;
            StatusText = "传输已中止";
            AddLog("传输已中止。");
        }
        catch (Exception exception)
        {
            IsProgressIndeterminate = false;
            StatusText = "传输失败";
            AddLog($"传输失败：{exception.Message}");
        }
        finally
        {
            _transferCancellation = null;
            IsBusy = false;
        }
    }

    private void OnProgress(TftpTransferProgress progress, bool upload)
    {
        if (progress.TotalBytes is > 0)
        {
            long total = progress.TotalBytes.Value;
            IsProgressIndeterminate = false;
            ProgressPercent = (int)Math.Clamp(progress.BytesTransferred * 100L / total, 0, 100);
            ProgressText = $"{ProgressPercent}%（{progress.BytesTransferred:N0}/{total:N0} 字节）";
        }
        else
        {
            IsProgressIndeterminate = !upload;
            ProgressText = upload
                ? $"{progress.BytesTransferred:N0} 字节"
                : $"已接收 {progress.BytesTransferred:N0} 字节";
        }
    }

    private void AddLog(string message)
    {
        LogEntries.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (LogEntries.Count > 2000)
        {
            LogEntries.RemoveAt(0);
        }

        LogText = string.Join(Environment.NewLine, LogEntries);
    }

    partial void OnHostChanged(string value) => NotifyPreferencesChanged();
    partial void OnPortChanged(int value) => NotifyPreferencesChanged();
    partial void OnLocalDirectoryChanged(string value) => NotifyPreferencesChanged();
    partial void OnLocalFileChanged(string value) => NotifyPreferencesChanged();
    partial void OnRemoteFileChanged(string value) => NotifyPreferencesChanged();
    partial void OnSelectedBlockSizeChanged(TftpBlockSizeOption value) => NotifyPreferencesChanged();

    partial void OnIsBusyChanged(bool value)
    {
        GetCommand.NotifyCanExecuteChanged();
        PutCommand.NotifyCanExecuteChanged();
        BreakCommand.NotifyCanExecuteChanged();
    }

    private void NotifyPreferencesChanged()
    {
        if (!_suppressPreferenceChanged)
        {
            PreferencesChanged?.Invoke();
        }
    }

    private string GetLocalDirectory()
    {
        string directory = LocalDirectory?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(directory) ? GetDirectoryFromPath(LocalFile) : directory;
    }

    private static string GetDirectoryFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetDirectoryName(path.Trim()) ?? string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _transferCancellation?.Cancel();
        _transferCancellation = null;
        await Task.CompletedTask;
    }
}
