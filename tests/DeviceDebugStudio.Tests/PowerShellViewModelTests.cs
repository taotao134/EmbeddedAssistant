using DeviceDebugStudio.App.ViewModels;

namespace DeviceDebugStudio.Tests;

public sealed class PowerShellViewModelTests
{
    [Fact]
    public async Task ExecutesCommandAndReturnsOutput()
    {
        await using PowerShellViewModel viewModel = new();

        await viewModel.EnsureStartedAsync();
        Assert.True(viewModel.IsRunning, viewModel.StatusText);

        viewModel.CommandText = "Write-Output 'DeviceDebugStudioPowerShellTest'";
        await viewModel.ExecuteCommandCommand.ExecuteAsync(null);

        Assert.Contains("DeviceDebugStudioPowerShellTest", viewModel.OutputText, StringComparison.Ordinal);
        Assert.Contains("TX Write-Output 'DeviceDebugStudioPowerShellTest'", viewModel.OutputText, StringComparison.Ordinal);
        Assert.Contains("RX DeviceDebugStudioPowerShellTest", viewModel.OutputText, StringComparison.Ordinal);
        Assert.Matches(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] TX", viewModel.OutputText);
        Assert.Matches(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] RX", viewModel.OutputText);
        Assert.True(viewModel.StatusText == "就绪", viewModel.OutputText);

        viewModel.CommandText = "Write-Output 'DeviceDebugStudioPowerShellSecondTest'";
        await viewModel.ExecuteCommandCommand.ExecuteAsync(null);

        Assert.Contains("DeviceDebugStudioPowerShellSecondTest", viewModel.OutputText, StringComparison.Ordinal);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.ExecuteCommandCommand.CanExecute(null));
    }

    [Fact]
    public async Task StopsAndRestartsSession()
    {
        await using PowerShellViewModel viewModel = new();

        await viewModel.EnsureStartedAsync();
        await viewModel.StopCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsRunning);
        Assert.Equal("已停止", viewModel.StatusText);

        await viewModel.StartCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsRunning, viewModel.StatusText);

        viewModel.CommandText = "Write-Output 'DeviceDebugStudioPowerShellRestartTest'";
        await viewModel.ExecuteCommandCommand.ExecuteAsync(null);

        Assert.Contains("DeviceDebugStudioPowerShellRestartTest", viewModel.OutputText, StringComparison.Ordinal);
        Assert.True(viewModel.StatusText == "就绪", viewModel.OutputText);
    }

    [Fact]
    public async Task CtrlCInterruptsRunningCommandAndKeepsSessionUsable()
    {
        await using PowerShellViewModel viewModel = new();

        await viewModel.EnsureStartedAsync();
        viewModel.CommandText = "Start-Sleep -Seconds 10";
        Task executeTask = viewModel.ExecuteCommandCommand.ExecuteAsync(null);

        Assert.True(SpinWait.SpinUntil(() => viewModel.IsBusy, TimeSpan.FromSeconds(5)), viewModel.StatusText);
        await Task.Delay(250);
        await viewModel.SendControlAsync(PowerShellControlKey.Cancel);
        await executeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(viewModel.IsRunning, viewModel.StatusText);
        Assert.False(viewModel.IsBusy);
        Assert.Contains("^C", viewModel.OutputText, StringComparison.Ordinal);

        viewModel.CommandText = "Write-Output 'AfterCtrlCTest'";
        await viewModel.ExecuteCommandCommand.ExecuteAsync(null);
        Assert.Contains("AfterCtrlCTest", viewModel.OutputText, StringComparison.Ordinal);
        Assert.Equal("就绪", viewModel.StatusText);
    }

    [Fact]
    public async Task StreamsOutputBeforeLongRunningCommandCompletes()
    {
        await using PowerShellViewModel viewModel = new();

        await viewModel.EnsureStartedAsync();
        viewModel.CommandText = "Write-Output 'StreamingBeforeSleep'; Start-Sleep -Milliseconds 800; Write-Output 'StreamingAfterSleep'";
        Task executeTask = viewModel.ExecuteCommandCommand.ExecuteAsync(null);

        Assert.True(
            SpinWait.SpinUntil(
                () => viewModel.OutputText.Contains("StreamingBeforeSleep", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5)),
            viewModel.OutputText);
        Assert.True(viewModel.IsBusy);

        await executeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("StreamingAfterSleep", viewModel.OutputText, StringComparison.Ordinal);
        Assert.Equal("就绪", viewModel.StatusText);
    }

    [Fact]
    public async Task StreamsNativePingOutputAndInterruptsIt()
    {
        await using PowerShellViewModel viewModel = new();

        await viewModel.EnsureStartedAsync();
        viewModel.CommandText = "ping.exe -t 127.0.0.1";
        Task executeTask = viewModel.ExecuteCommandCommand.ExecuteAsync(null);

        bool receivedReply = SpinWait.SpinUntil(
            () => viewModel.OutputText.Contains("TTL=", StringComparison.OrdinalIgnoreCase)
                || viewModel.OutputText.Contains("生存时间", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
        Assert.True(receivedReply, viewModel.OutputText);

        await viewModel.SendControlAsync(PowerShellControlKey.Cancel);
        await executeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.IsRunning, viewModel.StatusText);
        Assert.Contains("^C", viewModel.OutputText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanInterruptMoreThanOneCommand()
    {
        await using PowerShellViewModel viewModel = new();

        await viewModel.EnsureStartedAsync();
        for (int index = 0; index < 2; index++)
        {
            viewModel.CommandText = $"Start-Sleep -Seconds 10";
            Task executeTask = viewModel.ExecuteCommandCommand.ExecuteAsync(null);

            Assert.True(SpinWait.SpinUntil(() => viewModel.IsBusy, TimeSpan.FromSeconds(5)), viewModel.StatusText);
            await viewModel.SendControlAsync(PowerShellControlKey.Cancel);
            await executeTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(viewModel.IsRunning, viewModel.StatusText);
            Assert.False(viewModel.IsBusy);
        }
    }

    [Fact]
    public async Task RepeatedCtrlCWhileIdleKeepsPromptAndTimestamps()
    {
        await using PowerShellViewModel viewModel = new();

        await viewModel.EnsureStartedAsync();
        await viewModel.SendControlAsync(PowerShellControlKey.Cancel);
        await viewModel.SendControlAsync(PowerShellControlKey.Cancel);

        Assert.True(
            SpinWait.SpinUntil(
                () => CountOccurrences(viewModel.OutputText, "RX ^C") >= 2,
                TimeSpan.FromSeconds(5)),
            viewModel.OutputText);
        Assert.Contains("TX Ctrl+C", viewModel.OutputText, StringComparison.Ordinal);
        Assert.Matches(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] TX", viewModel.OutputText);
        Assert.Matches(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] RX", viewModel.OutputText);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
