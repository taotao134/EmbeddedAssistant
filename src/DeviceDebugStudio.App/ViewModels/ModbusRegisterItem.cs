using CommunityToolkit.Mvvm.ComponentModel;

namespace DeviceDebugStudio.App.ViewModels;

public partial class ModbusRegisterItem(ushort address, ushort value = 0) : ObservableObject
{
    public ushort Address { get; } = address;

    [ObservableProperty]
    private ushort value = value;

    public string AddressText => $"{Address} / 0x{Address:X4}";
}
