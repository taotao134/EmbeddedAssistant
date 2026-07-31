using System.Collections.Concurrent;
using DeviceDebugStudio.Core.Transports;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace DeviceDebugStudio.Infrastructure.Transports;

[Flags]
public enum BleDeviceDiscoverySource
{
    None = 0,
    Advertisement = 1,
    System = 2,
}

public sealed record BleDeviceInfo(
    ulong Address,
    string Name,
    short Rssi,
    BleDeviceDiscoverySource Source = BleDeviceDiscoverySource.Advertisement)
{
    public string AddressText => Address.ToString("X12");

    public string DisplayName
    {
        get
        {
            string baseName = string.IsNullOrWhiteSpace(Name) ? "未知设备" : Name;
            return $"{baseName} ({AddressText})";
        }
    }

    public string SourceText => Source switch
    {
        BleDeviceDiscoverySource.Advertisement => "广播",
        BleDeviceDiscoverySource.System => "系统",
        BleDeviceDiscoverySource.Advertisement | BleDeviceDiscoverySource.System => "广播+系统",
        _ => "未知"
    };

    public BleDeviceInfo MergeAdvertisement(string? name, short rssi) => this with
    {
        Name = string.IsNullOrWhiteSpace(name) ? Name : name.Trim(),
        Rssi = rssi,
        Source = Source | BleDeviceDiscoverySource.Advertisement
    };

    public BleDeviceInfo MergeSystem(string? name) => this with
    {
        Name = string.IsNullOrWhiteSpace(name) ? Name : name.Trim(),
        Source = Source | BleDeviceDiscoverySource.System
    };

    public BleDeviceInfo WithSystemDisplayName(string? systemDisplayName) => string.IsNullOrWhiteSpace(systemDisplayName)
        ? this
        : this with { Name = systemDisplayName.Trim(), Source = Source | BleDeviceDiscoverySource.System };
}

public sealed class BleDiscoveryService
{
    public IReadOnlyList<string> LastClassicDeviceNames { get; private set; } = [];
    public int LastAdvertisementCount { get; private set; }
    public int LastSystemCount { get; private set; }
    public string? LastWatcherError { get; private set; }

    public async Task<IReadOnlyList<BleDeviceInfo>> ScanAsync(TimeSpan duration, string? nameFilter = null, CancellationToken cancellationToken = default)
    {
        duration = TimeSpan.FromMilliseconds(Math.Clamp(duration.TotalMilliseconds, 500, 10_000));
        ConcurrentDictionary<ulong, BleDeviceInfo> devices = new();
        TaskCompletionSource<string?> watcherFault = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BluetoothLEAdvertisementWatcher watcher = new()
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += (_, args) =>
        {
            string name = args.Advertisement.LocalName ?? string.Empty;
            devices.AddOrUpdate(
                args.BluetoothAddress,
                new BleDeviceInfo(
                    args.BluetoothAddress,
                    name.Trim(),
                    args.RawSignalStrengthInDBm,
                    BleDeviceDiscoverySource.Advertisement),
                (_, existing) => existing.MergeAdvertisement(name, args.RawSignalStrengthInDBm));
        };
        watcher.Stopped += (_, args) =>
        {
            if (args.Error != BluetoothError.Success)
            {
                watcherFault.TrySetResult(args.Error.ToString());
            }
            else
            {
                watcherFault.TrySetResult(null);
            }
        };

        LastWatcherError = null;
        try
        {
            watcher.Start();
        }
        catch (Exception exception)
        {
            LastWatcherError = exception.Message;
            throw new IOException($"无法启动 BLE 广播扫描：{exception.Message}", exception);
        }

        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (watcher.Status is BluetoothLEAdvertisementWatcherStatus.Started
                    or BluetoothLEAdvertisementWatcherStatus.Stopping)
                {
                    watcher.Stop();
                }
            }
            catch
            {
            }
        }

        try
        {
            string? watcherError = await watcherFault.Task
                .WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(watcherError))
            {
                LastWatcherError = watcherError;
            }
        }
        catch (TimeoutException)
        {
        }

        LastAdvertisementCount = devices.Count;

        // 广播已经发现设备时直接返回，避免再逐个打开 Windows 设备对象产生数秒延迟。
        // 只有没有收到广播时才回退到系统关联列表，兼容已配对但暂时停止广播的设备。
        if (devices.IsEmpty)
        {
            await MergeSystemBleDevicesAsync(devices, cancellationToken).ConfigureAwait(false);
            LastClassicDeviceNames = await EnumerateClassicBluetoothNamesAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            LastClassicDeviceNames = [];
        }

        LastSystemCount = devices.Values.Count(device => device.Source.HasFlag(BleDeviceDiscoverySource.System));

        return devices.Values
            .Where(device => string.IsNullOrWhiteSpace(nameFilter)
                || device.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)
                || device.AddressText.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(device => device.Source.HasFlag(BleDeviceDiscoverySource.Advertisement))
            .ThenByDescending(device => device.Rssi)
            .ThenBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static async Task MergeSystemBleDevicesAsync(
        ConcurrentDictionary<ulong, BleDeviceInfo> devices,
        CancellationToken cancellationToken)
    {
        Task<DeviceInformationCollection?>[] discoveryTasks = [
            FindSystemBleDevicesAsync(true),
            FindSystemBleDevicesAsync(false)
        ];
        DeviceInformationCollection?[] collections = await Task.WhenAll(discoveryTasks).ConfigureAwait(false);
        using SemaphoreSlim limiter = new(4, 4);
        List<Task> resolveTasks = [];
        foreach (DeviceInformationCollection? systemDevices in collections)
        {
            if (systemDevices is null)
            {
                continue;
            }

            foreach (DeviceInformation deviceInfo in systemDevices)
            {
                resolveTasks.Add(ResolveSystemBleDeviceAsync(deviceInfo, devices, limiter, cancellationToken));
            }
        }

        await Task.WhenAll(resolveTasks).ConfigureAwait(false);
    }

    private static async Task<DeviceInformationCollection?> FindSystemBleDevicesAsync(bool paired)
    {
        try
        {
            string selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(paired);
            return await DeviceInformation.FindAllAsync(selector);
        }
        catch
        {
            return null;
        }
    }

    private static async Task ResolveSystemBleDeviceAsync(
        DeviceInformation deviceInfo,
        ConcurrentDictionary<ulong, BleDeviceInfo> devices,
        SemaphoreSlim limiter,
        CancellationToken cancellationToken)
    {
        await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using BluetoothLEDevice? bluetoothDevice = await BluetoothLEDevice.FromIdAsync(deviceInfo.Id);
            if (bluetoothDevice is null || bluetoothDevice.BluetoothAddress == 0)
            {
                return;
            }

            string? name = string.IsNullOrWhiteSpace(deviceInfo.Name)
                ? bluetoothDevice.Name
                : deviceInfo.Name;
            devices.AddOrUpdate(
                bluetoothDevice.BluetoothAddress,
                new BleDeviceInfo(
                    bluetoothDevice.BluetoothAddress,
                    name?.Trim() ?? string.Empty,
                    short.MinValue,
                    BleDeviceDiscoverySource.System),
                (_, existing) => existing.MergeSystem(name));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
        finally
        {
            limiter.Release();
        }
    }

    private static async Task<IReadOnlyList<string>> EnumerateClassicBluetoothNamesAsync(CancellationToken cancellationToken)
    {
        ConcurrentDictionary<string, byte> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (bool paired in new[] { true, false })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string selector = BluetoothDevice.GetDeviceSelectorFromPairingState(paired);
                DeviceInformationCollection classicDevices = await DeviceInformation.FindAllAsync(selector);
                foreach (DeviceInformation deviceInfo in classicDevices)
                {
                    if (!string.IsNullOrWhiteSpace(deviceInfo.Name))
                    {
                        names[deviceInfo.Name.Trim()] = 0;
                    }
                }
            }
            catch
            {
            }
        }

        return names.Keys
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static async Task<BleDeviceInfo> ResolveSystemDisplayNameAsync(BleDeviceInfo device, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using BluetoothLEDevice? bluetoothDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(device.Address);
            string? systemDisplayName = bluetoothDevice?.DeviceInformation.Name;
            return device.WithSystemDisplayName(systemDisplayName);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return device;
        }
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

        // Some Android peripherals reject the Windows UUID-filtered discovery call
        // even though a full GATT discovery succeeds. Discover all services first,
        // then select the configured service locally.
        GattDeviceServicesResult serviceResult = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        if (serviceResult.Status != GattCommunicationStatus.Success)
        {
            throw new IOException($"读取 BLE 服务失败：{serviceResult.Status}。 ");
        }

        _service = serviceResult.Services.FirstOrDefault(service => service.Uuid == serviceUuid);
        if (_service is null)
        {
            foreach (GattDeviceService service in serviceResult.Services)
            {
                service.Dispose();
            }

            throw new IOException($"未找到 BLE 服务：{serviceUuid}。 ");
        }

        foreach (GattDeviceService service in serviceResult.Services)
        {
            if (!ReferenceEquals(service, _service))
            {
                service.Dispose();
            }
        }

        GattCharacteristicsResult characteristicsResult = await _service
            .GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        if (characteristicsResult.Status != GattCommunicationStatus.Success)
        {
            throw new IOException($"读取 BLE 特征值失败：{characteristicsResult.Status}。 ");
        }

        GattCharacteristic[] characteristics = characteristicsResult.Characteristics.ToArray();
        _readCharacteristic = ResolveCharacteristic(
            settings.ReadCharacteristicUuid,
            characteristics,
            characteristic => characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read));
        _writeCharacteristic = ResolveCharacteristic(
            settings.WriteCharacteristicUuid,
            characteristics,
            characteristic => characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
                || characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse));
        _notifyCharacteristic = ResolveCharacteristic(
            settings.NotifyCharacteristicUuid,
            characteristics,
            characteristic => characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
                || characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate));

        if (settings.SubscribeOnConnect && _notifyCharacteristic is not null)
        {
            _notifyCharacteristic.ValueChanged += OnValueChanged;
            GattClientCharacteristicConfigurationDescriptorValue descriptor =
                _notifyCharacteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
                    ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                    : GattClientCharacteristicConfigurationDescriptorValue.Indicate;
            GattCommunicationStatus status = await _notifyCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(descriptor);
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
        bool supportsWriteWithResponse = characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write);
        GattWriteOption option = settings.WriteWithoutResponse || !supportsWriteWithResponse
            ? GattWriteOption.WriteWithoutResponse
            : GattWriteOption.WriteWithResponse;
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

    private static GattCharacteristic? ResolveCharacteristic(
        string uuidText,
        IReadOnlyList<GattCharacteristic> characteristics,
        Func<GattCharacteristic, bool> fallbackPredicate)
    {
        if (string.IsNullOrWhiteSpace(uuidText))
        {
            return characteristics.FirstOrDefault(fallbackPredicate);
        }

        if (!Guid.TryParse(uuidText, out Guid uuid))
        {
            throw new FormatException($"BLE 特征值 UUID 无效：{uuidText}");
        }

        return characteristics.FirstOrDefault(characteristic => characteristic.Uuid == uuid);
    }
}
