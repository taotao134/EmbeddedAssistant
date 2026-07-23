using DeviceDebugStudio.Core.Transports;
using DeviceDebugStudio.Infrastructure.Persistence;
using DeviceDebugStudio.Infrastructure.Transports;
using Microsoft.Data.Sqlite;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Xunit.Abstractions;

namespace DeviceDebugStudio.Tests;

public sealed class HardwareAndPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void BleAdvertisementWithoutNameDoesNotEraseKnownDeviceName()
    {
        BleDeviceInfo device = new(0x41348241E559, "atao", -62);

        BleDeviceInfo updated = device.MergeAdvertisement(string.Empty, -55);

        Assert.Equal("atao", updated.Name);
        Assert.Equal("atao", updated.DisplayName);
        Assert.Equal(-55, updated.Rssi);
    }

    [Fact]
    public void BleAdvertisementRefreshesDeviceNameWhenOneBecomesAvailable()
    {
        BleDeviceInfo device = new(0x41348241E559, string.Empty, -62);

        BleDeviceInfo updated = device.MergeAdvertisement(" atao ", -55);

        Assert.Equal("atao", updated.Name);
        Assert.Equal("atao", updated.DisplayName);
    }

    [Fact]
    public void BleSystemDisplayNameOverridesAdvertisementName()
    {
        BleDeviceInfo device = new(0x41348241E559, "advertisement-name", -55);

        BleDeviceInfo updated = device.WithSystemDisplayName(" atao ");

        Assert.Equal("atao", updated.Name);
        Assert.Equal("atao", updated.DisplayName);
    }

    [Fact]
    public void BleDeviceWithoutSystemNameUsesWindowsUnknownDeviceLabel()
    {
        BleDeviceInfo device = new(0x41348241E559, string.Empty, -55);

        Assert.Equal("未知设备", device.DisplayName);
    }

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

        BleDiscoveryService discovery = new();
        IReadOnlyList<BleDeviceInfo> devices = await discovery.ScanAsync(TimeSpan.FromSeconds(4));
        foreach (BleDeviceInfo device in devices)
        {
            output.WriteLine($"BLE GATT: {device.DisplayName}; address={device.AddressText}");
        }
        output.WriteLine($"经典蓝牙: {string.Join("、", discovery.LastClassicDeviceNames)}");
        Assert.NotNull(devices);
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task WindowsBleAssociationEndpointsExposeSystemDisplayNames()
    {
        if (Environment.GetEnvironmentVariable("DEVICEDEBUGSTUDIO_HARDWARE_TESTS") != "1")
        {
            return;
        }

        foreach (bool paired in new[] { false, true })
        {
            string selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(paired);
            DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(selector);
            foreach (DeviceInformation deviceInfo in devices)
            {
                using BluetoothLEDevice? device = await BluetoothLEDevice.FromIdAsync(deviceInfo.Id);
                output.WriteLine($"paired={paired}; name={deviceInfo.Name}; address={device?.BluetoothAddress:X12}; id={deviceInfo.Id}");
            }
        }

        string classicSelector = BluetoothDevice.GetDeviceSelectorFromPairingState(false);
        DeviceInformationCollection classicDevices = await DeviceInformation.FindAllAsync(classicSelector);
        foreach (DeviceInformation deviceInfo in classicDevices)
        {
            using BluetoothDevice? device = await BluetoothDevice.FromIdAsync(deviceInfo.Id);
            output.WriteLine($"classic; name={deviceInfo.Name}; address={device?.BluetoothAddress:X12}; id={deviceInfo.Id}");
        }
    }
}
