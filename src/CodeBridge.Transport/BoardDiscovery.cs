using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using CodeBridge.Transport.Serial;

namespace CodeBridge.Transport;

public static class BoardDiscovery
{
    public static PortInfo[] DiscoverPorts()
    {
        var ports = new Dictionary<string, PortInfo>(StringComparer.OrdinalIgnoreCase);

        // Get basic list from SerialPort
        foreach (var portName in SerialTransport.GetAvailablePorts())
        {
            ports[portName] = new PortInfo(portName);
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
                foreach (ManagementBaseObject queryObj in searcher.Get())
                {
                    var nameObj = queryObj["Name"];
                    if (nameObj == null) continue;

                    string name = nameObj.ToString()!;
                    var match = Regex.Match(name, @"\((COM\d+)\)");
                    if (match.Success)
                    {
                        string portName = match.Groups[1].Value;
                        string? deviceId = queryObj["PNPDeviceID"]?.ToString();
                        string? description = queryObj["Description"]?.ToString();
                        string? manufacturer = queryObj["Manufacturer"]?.ToString();

                        string? vid = null;
                        string? pid = null;

                        if (deviceId != null)
                        {
                            var vidMatch = Regex.Match(deviceId, @"VID_([0-9A-Fa-f]{4})");
                            var pidMatch = Regex.Match(deviceId, @"PID_([0-9A-Fa-f]{4})");
                            
                            if (vidMatch.Success) vid = vidMatch.Groups[1].Value;
                            if (pidMatch.Success) pid = pidMatch.Groups[1].Value;
                        }

                        var (boardHint, _) = UsbDeviceIdentifier.Identify(vid, pid);

                        ports[portName] = new PortInfo(portName)
                        {
                            PnpDeviceId = deviceId,
                            Description = description,
                            VendorId = vid,
                            ProductId = pid,
                            Manufacturer = manufacturer,
                            BoardHint = boardHint
                        };
                    }
                }
            }
            catch
            {
                // Ignore WMI errors
            }
        }

        return ports.Values.OrderBy(p => p.Name).ToArray();
    }

    /// <summary>The ports that are most likely a CodeBridge board, best first. See <see cref="RankCandidates"/>.</summary>
    public static PortInfo[] FindBoardCandidates() => RankCandidates(DiscoverPorts());

    /// <summary>
    /// Keeps the ports worth trying when nobody picked one: a recognised board first, then any other USB serial adapter.
    /// The legacy COM1 of a desktop PC, Bluetooth virtual ports and the like are not boards and are left out.
    /// When the system could not describe the ports at all, every port except COM1 is a candidate.
    /// </summary>
    public static PortInfo[] RankCandidates(IEnumerable<PortInfo> ports)
    {
        return ports
            .Where(IsCandidate)
            .OrderBy(p => !string.IsNullOrWhiteSpace(p.BoardHint) ? 0 : p.IsUsb ? 1 : 2)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsCandidate(PortInfo port)
    {
        if (!string.IsNullOrWhiteSpace(port.BoardHint) || port.IsUsb)
            return true;

        // No description at all: the system could not tell what the port is, so only the usual built-in port is ruled out.
        return port.PnpDeviceId is null && !port.Name.Equals("COM1", StringComparison.OrdinalIgnoreCase);
    }
}

public record PortInfo(string Name)
{
    public string? PnpDeviceId { get; init; }
    public string? Description { get; init; }
    public string? VendorId { get; init; }
    public string? ProductId { get; init; }
    public string? Manufacturer { get; init; }
    public string? BoardHint { get; init; }

    /// <summary>True when the port belongs to a USB serial adapter (a board, an FTDI cable...), not to a built-in or Bluetooth port.</summary>
    public bool IsUsb =>
        !string.IsNullOrWhiteSpace(VendorId)
        || (PnpDeviceId is { } id && (id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"FTDIBUS\", StringComparison.OrdinalIgnoreCase)));

    public override string ToString()
    {
        if (!string.IsNullOrWhiteSpace(BoardHint))
        {
            return $"{Name} - {BoardHint}";
        }
        else if (!string.IsNullOrWhiteSpace(Description))
        {
            return $"{Name} - {Description}";
        }
        
        return Name;
    }
}
