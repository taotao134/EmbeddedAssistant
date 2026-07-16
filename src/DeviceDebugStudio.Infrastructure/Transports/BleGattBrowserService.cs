using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace DeviceDebugStudio.Infrastructure.Transports;

public sealed record BleGattCharacteristicInfo(Guid Uuid, string Properties)
{
    public string DisplayName => $"{Uuid}  [{Properties}]";
}

public sealed record BleGattServiceInfo(Guid Uuid, IReadOnlyList<BleGattCharacteristicInfo> Characteristics)
{
    public string DisplayName => Uuid.ToString();
}

public sealed class BleGattBrowserService
{
    public async Task<IReadOnlyList<BleGattServiceInfo>> BrowseAsync(ulong address, CancellationToken cancellationToken = default)
    {
        using BluetoothLEDevice? device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (device is null)
        {
            throw new IOException("无法打开 BLE 设备。 ");
        }

        GattDeviceServicesResult servicesResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        if (servicesResult.Status != GattCommunicationStatus.Success)
        {
            throw new IOException($"读取 GATT 服务失败：{servicesResult.Status}。 ");
        }

        List<BleGattServiceInfo> services = [];
        foreach (GattDeviceService service in servicesResult.Services)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                GattCharacteristicsResult characteristicsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                IReadOnlyList<BleGattCharacteristicInfo> characteristics = characteristicsResult.Status == GattCommunicationStatus.Success
                    ? characteristicsResult.Characteristics.Select(characteristic => new BleGattCharacteristicInfo(
                        characteristic.Uuid,
                        FormatProperties(characteristic.CharacteristicProperties))).ToArray()
                    : [];
                services.Add(new BleGattServiceInfo(service.Uuid, characteristics));
            }
            finally
            {
                service.Dispose();
            }
        }

        return services;
    }

    private static string FormatProperties(GattCharacteristicProperties properties)
    {
        List<string> names = [];
        if (properties.HasFlag(GattCharacteristicProperties.Read)) names.Add("读");
        if (properties.HasFlag(GattCharacteristicProperties.Write)) names.Add("写");
        if (properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)) names.Add("无响应写");
        if (properties.HasFlag(GattCharacteristicProperties.Notify)) names.Add("通知");
        if (properties.HasFlag(GattCharacteristicProperties.Indicate)) names.Add("指示");
        return names.Count == 0 ? properties.ToString() : string.Join('/', names);
    }
}
