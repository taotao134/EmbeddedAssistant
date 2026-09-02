using DeviceDebugStudio.App.ViewModels;
using DeviceDebugStudio.Core.Profiles;
using DeviceDebugStudio.Infrastructure.Import;
using DeviceDebugStudio.Infrastructure.Persistence;
using DeviceDebugStudio.Infrastructure.Transports;
using DeviceDebugStudio.Infrastructure.Updates;

namespace DeviceDebugStudio.Tests;

public sealed class QuickCommandInteractionTests
{
    [Fact]
    public async Task AddQuickCommandClearsSearchAndSelectsNewCommand()
    {
        string directory = CreateTemporaryDirectory();
        using OnlineUpdateService updateService = new(currentVersion: new Version(1, 0, 0));
        MainWindowViewModel viewModel = CreateViewModel(directory, updateService);

        try
        {
            viewModel.QuickCommandSearchText = "不会匹配";

            viewModel.AddQuickCommandCommand.Execute(null);

            QuickCommandItemViewModel command = Assert.Single(viewModel.QuickCommands);
            Assert.Same(command, viewModel.SelectedQuickCommand);
            Assert.Empty(viewModel.QuickCommandSearchText);
            Assert.Contains(command, viewModel.QuickCommandsView.Cast<QuickCommandItemViewModel>());
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task BulkDeleteCommandTracksCheckboxSelection()
    {
        string directory = CreateTemporaryDirectory();
        using OnlineUpdateService updateService = new(currentVersion: new Version(1, 0, 0));
        MainWindowViewModel viewModel = CreateViewModel(directory, updateService);

        try
        {
            QuickCommandItemViewModel command = new(new QuickCommand { Name = "待删除" });
            viewModel.QuickCommands.Add(command);

            Assert.False(viewModel.DeleteSelectedQuickCommandsCommand.CanExecute(null));

            command.IsSelectedForBulkDelete = true;

            Assert.True(viewModel.DeleteSelectedQuickCommandsCommand.CanExecute(null));
            viewModel.DeleteSelectedQuickCommandsCommand.Execute(null);
            Assert.Empty(viewModel.QuickCommands);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task BulkDeleteCommandDeletesTheSelectedCommandWhenNothingIsChecked()
    {
        string directory = CreateTemporaryDirectory();
        using OnlineUpdateService updateService = new(currentVersion: new Version(1, 0, 0));
        MainWindowViewModel viewModel = CreateViewModel(directory, updateService);

        try
        {
            QuickCommandItemViewModel command = new(new QuickCommand { Name = "当前指令" });
            viewModel.QuickCommands.Add(command);
            viewModel.SelectedQuickCommand = command;

            Assert.True(viewModel.DeleteSelectedQuickCommandsCommand.CanExecute(null));
            viewModel.DeleteSelectedQuickCommandsCommand.Execute(null);

            Assert.Empty(viewModel.QuickCommands);
            Assert.Null(viewModel.SelectedQuickCommand);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    private static MainWindowViewModel CreateViewModel(string directory, OnlineUpdateService updateService) => new(
        new JsonDeviceProfileStore(directory),
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
}
