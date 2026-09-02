using DeviceDebugStudio.App.ViewModels;
using DeviceDebugStudio.Core.Profiles;
using DeviceDebugStudio.Infrastructure.Import;
using DeviceDebugStudio.Infrastructure.Persistence;
using DeviceDebugStudio.Infrastructure.Transports;
using DeviceDebugStudio.Infrastructure.Updates;

namespace DeviceDebugStudio.Tests;

public sealed class ProfileDeletionTests
{
    [Fact]
    public async Task DeletesAllSameNamedProfilesInOneAction()
    {
        string directory = CreateTemporaryDirectory();
        DeviceProfile selected = new() { Name = "北京站S变频器" };
        DeviceProfile duplicate = new() { Name = "北京站s变频器" };
        DeviceProfile other = new() { Name = "其他设备" };
        ControllableProfileStore store = new([selected, duplicate, other]);
        using OnlineUpdateService updateService = new(currentVersion: new Version(1, 0, 0));
        MainWindowViewModel viewModel = CreateViewModel(store, directory, updateService);

        try
        {
            foreach (DeviceProfile profile in new[] { selected, duplicate, other })
            {
                viewModel.Profiles.Add(profile);
            }
            viewModel.SelectedProfile = selected;

            Assert.Equal(2, viewModel.SelectedProfileDeleteCount);

            await viewModel.DeleteSelectedProfileAsync();

            DeviceProfile remaining = Assert.Single(await store.LoadAllAsync());
            Assert.Equal(other.Id, remaining.Id);
            Assert.Equal(other.Id, Assert.Single(viewModel.Profiles).Id);
            Assert.Equal(other.Id, viewModel.SelectedProfile?.Id);
            Assert.Contains("已删除 2 个同名设备配置", viewModel.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task PendingSaveFinishesBeforeProfileIsDeleted()
    {
        string directory = CreateTemporaryDirectory();
        DeviceProfile profile = new() { Name = "北京站S变频器" };
        ControllableProfileStore store = new([profile]);
        using OnlineUpdateService updateService = new(currentVersion: new Version(1, 0, 0));
        MainWindowViewModel viewModel = CreateViewModel(store, directory, updateService);

        try
        {
            viewModel.Profiles.Add(profile);
            viewModel.SelectedProfile = profile;
            viewModel.ProfileName = "北京站S变频器（已编辑）";
            store.DelayNextSave();

            Task saveTask = viewModel.SaveSelectedProfileCommand.ExecuteAsync(null);
            await store.WaitForSaveStartAsync();
            Task deleteTask = viewModel.DeleteSelectedProfileAsync();

            Assert.False(deleteTask.IsCompleted);
            store.ReleaseSave();
            await Task.WhenAll(saveTask, deleteTask);

            Assert.Empty(await store.LoadAllAsync());
            Assert.Empty(viewModel.Profiles);
            Assert.Null(viewModel.SelectedProfile);
        }
        finally
        {
            store.ReleaseSave();
            await viewModel.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    private static MainWindowViewModel CreateViewModel(
        IConfigurableDeviceProfileStore profileStore,
        string directory,
        OnlineUpdateService updateService) => new(
            profileStore,
            new LegacyConfigImporter(),
            new AppSettingsStore(Path.Combine(directory, "settings.json")),
            new DeviceProfileFileService(),
            new CaptureFileReader(),
            updateService,
            new BleDiscoveryService(),
            new BleGattBrowserService());

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"DeviceDebugStudio.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class ControllableProfileStore(IEnumerable<DeviceProfile> profiles) : IConfigurableDeviceProfileStore
    {
        private readonly object _sync = new();
        private readonly Dictionary<Guid, DeviceProfile> _profiles = profiles.ToDictionary(profile => profile.Id);
        private TaskCompletionSource<bool>? _saveStarted;
        private TaskCompletionSource<bool>? _saveRelease;

        public string DirectoryPath { get; private set; } = Path.GetTempPath();

        public void SetDirectory(string directory)
        {
            DirectoryPath = Path.GetFullPath(directory);
        }

        public Task<IReadOnlyList<DeviceProfile>> LoadAllAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                IReadOnlyList<DeviceProfile> result = _profiles.Values
                    .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
                return Task.FromResult(result);
            }
        }

        public async Task SaveAsync(DeviceProfile profile, CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<bool>? saveStarted;
            TaskCompletionSource<bool>? saveRelease;
            lock (_sync)
            {
                saveStarted = _saveStarted;
                saveRelease = _saveRelease;
            }

            if (saveStarted is not null && saveRelease is not null)
            {
                saveStarted.TrySetResult(true);
                await saveRelease.Task.WaitAsync(cancellationToken);
                lock (_sync)
                {
                    if (ReferenceEquals(_saveStarted, saveStarted))
                    {
                        _saveStarted = null;
                        _saveRelease = null;
                    }
                }
            }

            lock (_sync)
            {
                _profiles[profile.Id] = profile;
            }
        }

        public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                _profiles.Remove(profileId);
            }
            return Task.CompletedTask;
        }

        public void DelayNextSave()
        {
            lock (_sync)
            {
                _saveStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _saveRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public async Task WaitForSaveStartAsync()
        {
            Task task;
            lock (_sync)
            {
                task = _saveStarted?.Task ?? throw new InvalidOperationException("未配置待阻塞的保存任务。");
            }
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public void ReleaseSave()
        {
            lock (_sync)
            {
                _saveRelease?.TrySetResult(true);
            }
        }
    }
}
