using System.Diagnostics;
using DeviceDebugStudio.Core.Transports;
using DeviceDebugStudio.Infrastructure.Transports;

namespace DeviceDebugStudio.Tests;

public sealed class SerialWorkerIsolationTests
{
    [Fact]
    public async Task FailedHighBaudOpenDoesNotPoisonFollowingAttempt()
    {
        TimeSpan firstDuration = await MeasureFailedOpenAsync(2_000_000);
        TimeSpan secondDuration = await MeasureFailedOpenAsync(460_800);

        Assert.True(firstDuration < TimeSpan.FromSeconds(3), $"首次失败耗时 {firstDuration.TotalMilliseconds:N0} ms。");
        Assert.True(secondDuration < TimeSpan.FromSeconds(3), $"再次失败耗时 {secondDuration.TotalMilliseconds:N0} ms。");
    }

    private static async Task<TimeSpan> MeasureFailedOpenAsync(int baudRate)
    {
        SerialTransportSettings settings = new()
        {
            PortName = "COM9999",
            BaudRate = baudRate,
            Parity = SerialParity.None,
            DataBits = 8,
            StopBits = SerialStopBits.One,
            Handshake = SerialHandshake.None,
            DtrEnable = false,
            RtsEnable = false
        };
        await using SerialPortTransport transport = new(settings);
        Stopwatch stopwatch = Stopwatch.StartNew();

        Exception exception = await Assert.ThrowsAnyAsync<Exception>(
            () => transport.ConnectAsync(CancellationToken.None));

        stopwatch.Stop();
        Assert.Contains("不限制波特率", exception.Message, StringComparison.Ordinal);
        return stopwatch.Elapsed;
    }
}
