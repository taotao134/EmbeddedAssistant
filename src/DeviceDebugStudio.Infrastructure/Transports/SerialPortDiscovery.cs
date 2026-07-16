using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;
using DeviceDebugStudio.Core.Transports;

namespace DeviceDebugStudio.Infrastructure.Transports;

public static partial class SerialPortDiscovery
{
    public static IReadOnlyList<SerialPortInfo> GetPorts()
    {
        Dictionary<string, SerialPortInfo> ports = SerialPort.GetPortNames()
            .ToDictionary(name => name, name => new SerialPortInfo(name, name, null, null), StringComparer.OrdinalIgnoreCase);

        try
        {
            using ManagementObjectSearcher searcher = new("SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%)'");
            foreach (ManagementObject item in searcher.Get())
            {
                string name = item["Name"]?.ToString() ?? string.Empty;
                Match portMatch = PortRegex().Match(name);
                if (!portMatch.Success)
                {
                    continue;
                }

                string portName = portMatch.Groups[1].Value;
                string pnpId = item["PNPDeviceID"]?.ToString() ?? string.Empty;
                Match usbMatch = VidPidRegex().Match(pnpId);
                ports[portName] = new SerialPortInfo(
                    portName,
                    PortSuffixRegex().Replace(name, string.Empty).Trim(),
                    usbMatch.Success ? usbMatch.Groups[1].Value : null,
                    usbMatch.Success ? usbMatch.Groups[2].Value : null);
            }
        }
        catch (ManagementException)
        {
        }

        return ports.Values.OrderBy(item => PortNumber(item.PortName)).ThenBy(item => item.PortName).ToArray();
    }

    private static int PortNumber(string name) => int.TryParse(NonDigitRegex().Replace(name, string.Empty), out int value) ? value : int.MaxValue;

    [GeneratedRegex(@"\((COM\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex PortRegex();

    [GeneratedRegex(@"\s*\(COM\d+\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PortSuffixRegex();

    [GeneratedRegex(@"VID_([0-9A-F]{4}).*PID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidPidRegex();

    [GeneratedRegex(@"\D")]
    private static partial Regex NonDigitRegex();
}
