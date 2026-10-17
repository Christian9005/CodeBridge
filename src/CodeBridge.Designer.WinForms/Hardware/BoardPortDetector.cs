using Microsoft.Win32;
using CodeBridge.Flow;
using CodeBridge.Transport;

namespace CodeBridge.Designer.WinForms.Hardware;

internal static class BoardPortDetector
{
    /// <summary>The board a port most likely has behind it, from its USB identity (see <see cref="BoardIdentification.FromUsb"/>). Null when it cannot tell.</summary>
    public static BoardProfile? SuggestBoardForPort(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
            return null;

        // The registry answers at once; it does not know every adapter, so ask Windows what it knows about the port when it says nothing.
        var suggestion = BoardIdentification.FromUsb(null, null, TryGetUsbPortDescription(portName));
        if (suggestion is not null)
            return suggestion;

        var port = FindPort(portName);
        return port is null
            ? null
            : BoardIdentification.FromUsb(port.VendorId, port.ProductId, port.BoardHint + " " + port.Description);
    }

    /// <summary>Human readable USB description of a COM port (for example "Silicon Labs CP210x"), or null.</summary>
    public static string? DescribePort(string portName)
    {
        var description = TryGetUsbPortDescription(portName);
        if (string.IsNullOrWhiteSpace(description))
            return FindPort(portName)?.Description;

        // "<friendly name> <VID_xxxx&PID_xxxx> <serial>": keep the friendly part.
        var vid = description.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        return (vid > 0 ? description[..vid] : description).Trim();
    }

    private static PortInfo? FindPort(string portName)
    {
        try
        {
            return BoardDiscovery.DiscoverPorts().FirstOrDefault(port => string.Equals(port.Name, portName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return null; // ports cannot be described on this system
        }
    }

    private static string? TryGetUsbPortDescription(string portName)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            foreach (var rootPath in new[] { @"SYSTEM\CurrentControlSet\Enum\USB", @"SYSTEM\CurrentControlSet\Enum\FTDIBUS" })
            {
                var description = TryGetUsbPortDescription(rootPath, portName);
                if (!string.IsNullOrWhiteSpace(description))
                    return description;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string? TryGetUsbPortDescription(string rootPath, string portName)
    {
        using var root = Registry.LocalMachine.OpenSubKey(rootPath);
        if (root is null)
            return null;

        foreach (var deviceName in root.GetSubKeyNames())
        {
            using var device = root.OpenSubKey(deviceName);
            if (device is null)
                continue;

            foreach (var instanceName in device.GetSubKeyNames())
            {
                using var instance = device.OpenSubKey(instanceName);
                using var parameters = instance?.OpenSubKey("Device Parameters");
                var registeredPort = parameters?.GetValue("PortName")?.ToString();
                if (!string.Equals(registeredPort, portName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var friendlyName = instance?.GetValue("FriendlyName")?.ToString();
                return $"{friendlyName} {deviceName} {instanceName}";
            }
        }

        return null;
    }
}
