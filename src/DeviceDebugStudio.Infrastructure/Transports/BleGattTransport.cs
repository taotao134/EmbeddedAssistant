using System.Collections.Concurrent;
using DeviceDebugStudio.Core.Transports;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace DeviceDebugStudio.Infrastructure.Transports;

public sealed record BleDeviceInfo(ulong Address, string Name, short Rssi)
{
    public string AddressText => Address.ToString("X12");
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"BLE {AddressText}" : $"{Name} ({AddressText})";
}

public sealed class BleDiscoveryService
{
    public async Task<IReadOnlyList<BleDeviceInfo>> ScanAsync(TimeSpan duration, string? nameFilter = null, CancellationToken cancellationToken = default)
    {
        ConcurrentDictionary<ulong, BleDeviceInfo> devices = new();
        BluetoothLEAdvertisementWatcher watcher = new() { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, args) =>
        {
            string name = args.Advertisement.LocalName ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(nameFilter) && !name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            devices[args.BluetoothAddress] = new BleDeviceInfo(args.BluetoothAddress, name, args.RawSignalStrengthInDBm);
        };

        watcher.Start();
        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            watcher.Stop();
        }

        return devices.Values.OrderByDescending(device => device.Rssi).ToArray();
    }
}

public sealed class BleGattTransport(BleGattTransportSettings settings) : TransportBase
{
    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _readCharacteristic;
    private GattCharacteristic? _writeCharacteristic;
    private GattCharacteristic? _notifyCharacteristic;

    public override string DisplayName => $"BLE {settings.BluetoothAddress:X12}";
    public override TransportKind Kind => TransportKind.BleGatt;

    protected override async Task OnConnectAsync(CancellationToken cancellationToken)
    {
        if (settings.BluetoothAddress == 0 || !Guid.TryParse(settings.ServiceUuid, out Guid serviceUuid))
        {
            throw new InvalidOperationException("BLE 地址或服务 UUID 无效。 ");
        }

        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(settings.BluetoothAddress);
        if (_device is null)
        {
            throw new IOException("无法连接 BLE 设备。 ");
        }

        GattDeviceServicesResult serviceResult = await _device.GetGattServicesForUuidAsync(serviceUuid, BluetoothCacheMode.Uncached);
        if (serviceResult.Status != GattCommunicationStatus.Success || serviceResult.Services.Count == 0)
        {
            throw new IOException($"读取 BLE 服务失败：{serviceResult.Status}。 ");
        }

        _service = serviceResult.Services[0];
        if (Guid.TryParse(settings.ReadCharacteristicUuid, out Guid readUuid))
        {
            _readCharacteristic = await FindCharacteristicAsync(_service, readUuid).ConfigureAwait(false);
        }
        if (Guid.TryParse(settings.WriteCharacteristicUuid, out Guid writeUuid))
        {
            _writeCharacteristic = await FindCharacteristicAsync(_service, writeUuid).ConfigureAwait(false);
        }

        if (Guid.TryParse(settings.NotifyCharacteristicUuid, out Guid notifyUuid))
        {
            _notifyCharacteristic = await FindCharacteristicAsync(_service, notifyUuid).ConfigureAwait(false);
        }

        if (settings.SubscribeOnConnect && _notifyCharacteristic is not null)
        {
            _notifyCharacteristic.ValueChanged += OnValueChanged;
            GattCommunicationStatus status = await _notifyCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (status != GattCommunicationStatus.Success)
            {
                throw new IOException($"订阅 BLE 通知失败：{status}。 ");
            }
        }
    }

    protected override async Task OnDisconnectAsync(CancellationToken cancellationToken)
    {
        if (_notifyCharacteristic is not null)
        {
            _notifyCharacteristic.ValueChanged -= OnValueChanged;
            try
            {
                await _notifyCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch
            {
            }
        }

        _readCharacteristic = null;
        _writeCharacteristic = null;
        _notifyCharacteristic = null;
        _service?.Dispose();
        _service = null;
        _device?.Dispose();
        _device = null;
    }

    public override async ValueTask SendAsync(ReadOnlyMemory<byte> data, string? target = null, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        GattCharacteristic characteristic = _writeCharacteristic ?? throw new InvalidOperationException("未配置 BLE 写特征值。 ");
        using DataWriter writer = new();
        writer.WriteBytes(data.ToArray());
        IBuffer buffer = writer.DetachBuffer();
        GattWriteOption option = settings.WriteWithoutResponse ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse;
        GattWriteResult result = await characteristic.WriteValueWithResultAsync(buffer, option);
        if (result.Status != GattCommunicationStatus.Success)
        {
            throw new IOException($"BLE 写入失败：{result.Status}。 ");
        }
    }

    public async Task ReadConfiguredCharacteristicAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        GattCharacteristic characteristic = _readCharacteristic ?? throw new InvalidOperationException("未配置 BLE 读特征值。 ");
        GattReadResult result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success)
        {
            throw new IOException($"BLE 读取失败：{result.Status}。 ");
        }

        using DataReader reader = DataReader.FromBuffer(result.Value);
        byte[] data = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(data);
        PublishReceived(data, $"{DisplayName} 读取");
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        using DataReader reader = DataReader.FromBuffer(args.CharacteristicValue);
        byte[] data = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(data);
        PublishReceived(data, DisplayName);
    }

    private static async Task<GattCharacteristic?> FindCharacteristicAsync(GattDeviceService service, Guid uuid)
    {
        GattCharacteristicsResult result = await service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success)
        {
            throw new IOException($"读取 BLE 特征值失败：{result.Status}。 ");
        }

        return result.Characteristics.FirstOrDefault();
    }
}
