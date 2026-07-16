using DeviceDebugStudio.Core.Transports;
using DeviceDebugStudio.Infrastructure.Persistence;
using DeviceDebugStudio.Infrastructure.Transports;
using Microsoft.Data.Sqlite;

namespace DeviceDebugStudio.Tests;

public sealed class HardwareAndPerformanceTests
{
    [Fact]
    [Trait("Category", "LongRunning")]
    public async Task CapturePipelineStoresThirtyMinutesAtOneMegabitEquivalentVolume()
    {
        if (Environment.GetEnvironmentVariable("DEVICEDEBUGSTUDIO_LONG_TESTS") != "1")
        {
            return;
        }

        const int payloadSize = 16 * 1024;
        const long expectedBytes = 125_000L * 60 * 30;
        int packetCount = checked((int)Math.Ceiling(expectedBytes / (double)payloadSize));
        byte[] payload = Enumerable.Range(0, payloadSize).Select(index => (byte)index).ToArray();
        string directory = Path.Combine(Path.GetTempPath(), "DeviceDebugStudio.Performance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            SqliteCaptureStore store = new(directory);
            await store.StartAsync("1Mbps_30min", TransportKind.Serial);
            for (int index = 0; index < packetCount; index++)
            {
                await store.AppendAsync(new TransportPacket(DateTimeOffset.Now, PacketDirection.Receive, payload, "SIM"));
            }
            await store.CompleteAsync();

            await using SqliteConnection connection = new($"Data Source={store.FilePath};Pooling=False");
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*), SUM(LENGTH(payload)) FROM packets";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(packetCount, reader.GetInt32(0));
            Assert.Equal((long)packetCount * payloadSize, reader.GetInt64(1));
            await store.DisposeAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task BleScannerUsesInstalledWindowsAdapter()
    {
        if (Environment.GetEnvironmentVariable("DEVICEDEBUGSTUDIO_HARDWARE_TESTS") != "1")
        {
            return;
        }

        IReadOnlyList<BleDeviceInfo> devices = await new BleDiscoveryService().ScanAsync(TimeSpan.FromSeconds(4));
        Assert.NotNull(devices);
    }
}
