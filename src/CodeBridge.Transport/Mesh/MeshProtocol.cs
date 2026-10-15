using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CodeBridge.Transport.Mesh;

/// <summary>A board that answered a mesh scan.</summary>
/// <param name="Mac">Twelve hexadecimal digits, upper case (the board's station MAC address): its identity in the mesh.</param>
/// <param name="Age">How long ago the gateway last heard from it.</param>
public sealed record MeshNodeInfo(string Mac, string Board, string Firmware, string Name, TimeSpan Age)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Mac : Name;
}

/// <summary>What a board says about its own mesh (the <c>MESH</c> command).</summary>
/// <param name="Locked">The board is on a working channel: it joined the router's channel, or a gateway answered it.</param>
public sealed record MeshStatus(bool Enabled, string Mac, int Channel, bool Locked, bool HasKey, string Name, int Received, int BadSignatures, int Delivered, int Lost);

/// <summary>The mesh failed in a way the firmware can name.</summary>
public sealed class MeshException : Exception
{
    public MeshException(string code, string message) : base(message) => Code = code;

    /// <summary>The firmware's code, for example <c>MESH_NOKEY</c>, <c>MESH_OFF</c> or <c>MESH_DENIED</c>.</summary>
    public string Code { get; }
}

/// <summary>The text protocol of the ESP-NOW mesh (firmware 0.9.1 and later). All of it rides on the board's normal command channel.</summary>
public static class MeshProtocol
{
    public const string Status = "MESH";
    public const string Scan = "MSCAN";
    public const string RelayPrefix = "MRLY";

    /// <summary>The commands a relayed board refuses, whatever the sender: they would let someone with the mesh key reach beyond the mesh.</summary>
    public static readonly IReadOnlySet<string> NeverRelayed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "AUTH", "WTOK", "WCFG", "WCFGX", "WSCAN", "OTAB", "OTAS", "MQC", "MQP", "MQS", "MQU", "MQR", "MQD",
        "MESH", "MESHON", "MESHOFF", "MESHKEY", "MESHNAME", "MESHCH", "MSCAN", "MRLY"
    };

    /// <summary>A new random 128-bit key as 32 hex digits. Every board of one mesh gets the same key, over USB.</summary>
    public static string GenerateKey()
    {
        var bytes = new byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static bool IsValidKey(string? key) =>
        key is { Length: 32 } && key.All(Uri.IsHexDigit);

    /// <summary>Upper-case MAC without separators; accepts <c>e0:72:a1:f9:7b:84</c>, <c>E0-72-A1-F9-7B-84</c> or plain hex.</summary>
    public static string NormalizeMac(string mac)
    {
        var clean = new string((mac ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return clean.Length == 12 ? clean : throw new FormatException($"'{mac}' is not a MAC address (12 hexadecimal digits).");
    }

    public static bool TryNormalizeMac(string? mac, out string normalized)
    {
        try
        {
            normalized = NormalizeMac(mac ?? string.Empty);
            return true;
        }
        catch (FormatException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    /// <summary>The command a gateway turns into a frame for <paramref name="mac"/>.</summary>
    public static string BuildRelay(string mac, string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("The command is empty.", nameof(command));

        return $"{RelayPrefix}:{NormalizeMac(mac)}:{command.Trim()}";
    }

    /// <summary>Takes the node's own answer out of the gateway's reply. Gateway-level failures become a <see cref="MeshException"/>.</summary>
    public static string UnwrapRelay(string reply)
    {
        if (reply.StartsWith("RLY:", StringComparison.Ordinal))
            return reply[4..];

        if (reply.StartsWith("ERR:", StringComparison.Ordinal))
        {
            var code = reply[4..].Trim();
            throw code switch
            {
                "MESH_TIMEOUT" => new TimeoutException("The node did not answer. Check that it is powered, in range and has the same mesh key."),
                "MESH_NOKEY" => new MeshException(code, "The gateway has no mesh key. Provision it over USB first."),
                "MESH_OFF" => new MeshException(code, "The mesh is switched off on the gateway."),
                "MESH_DENIED" => new MeshException(code, "That command is never relayed over the mesh."),
                "MESH_RESPONSE_TOO_LONG" => new MeshException(code, "The node's answer did not fit in one radio frame."),
                "MESH_CHALLENGE" => new MeshException(code, "The node refused the command twice. Try again."),
                _ => new MeshException(code, $"The gateway refused the relay: {code}")
            };
        }

        throw new MeshException("MESH_PROTOCOL", $"Unexpected answer from the gateway: {reply}");
    }

    /// <summary>Parses the answer of <c>MSCAN</c>: <c>OK:MAC,board,fw,name,ageSeconds;MAC,...</c>.</summary>
    public static IReadOnlyList<MeshNodeInfo> ParseScan(string reply)
    {
        var body = Body(reply);
        if (body.Length == 0)
            return Array.Empty<MeshNodeInfo>();

        var nodes = new List<MeshNodeInfo>();
        foreach (var entry in body.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split(',');
            if (parts.Length < 5 || !TryNormalizeMac(parts[0], out var mac))
                continue;

            var age = int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
            nodes.Add(new MeshNodeInfo(mac, parts[1], parts[2], parts[3], TimeSpan.FromSeconds(age)));
        }

        return nodes;
    }

    /// <summary>Parses the answer of <c>MESH</c>: <c>OK:on,MAC,channel,locked,hasKey,name[,rx,bad,delivered,lost]</c>.</summary>
    public static MeshStatus ParseStatus(string reply)
    {
        var parts = Body(reply).Split(',');
        if (parts.Length < 6)
            throw new MeshException("MESH_PROTOCOL", $"Unexpected mesh status: {reply}");

        int Number(int index) => index < parts.Length && int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return new MeshStatus(
            parts[0] == "on",
            parts[1].ToUpperInvariant(),
            Number(2),
            Number(3) == 1,
            Number(4) == 1,
            parts[5],
            Number(6),
            Number(7),
            Number(8),
            Number(9));
    }

    private static string Body(string reply)
    {
        if (reply.StartsWith("ERR:", StringComparison.Ordinal))
            UnwrapRelay(reply); // throws with the firmware's reason

        return reply.StartsWith("OK:", StringComparison.Ordinal) ? reply[3..].Trim() : reply == "OK" ? string.Empty : throw new MeshException("MESH_PROTOCOL", $"Unexpected answer: {reply}");
    }

    /// <summary>The commands that set a board up for the mesh over USB: name, key and, when it has no Wi-Fi, an optional fixed channel.</summary>
    public static IReadOnlyList<string> ProvisioningCommands(string key, string? name = null, int? channel = null)
    {
        if (!IsValidKey(key))
            throw new ArgumentException("The mesh key must be 32 hexadecimal digits.", nameof(key));

        var commands = new List<string>();
        if (!string.IsNullOrWhiteSpace(name))
        {
            if (name.Length > 24 || name.Any(c => c < 33 || c > 126 || c is '|' or ',' or ';'))
                throw new ArgumentException("The name must be 1-24 visible characters without '|', ',' or ';'.", nameof(name));
            commands.Add($"MESHNAME:{name}");
        }

        commands.Add($"MESHKEY:{key}");
        if (channel is { } fixedChannel)
            commands.Add($"MESHCH:{(fixedChannel is < 0 or > 13 ? throw new ArgumentOutOfRangeException(nameof(channel), "Channel 0-13.") : fixedChannel)}");

        commands.Add("MESHON");
        return commands;
    }

    /// <summary>The name used to address a node over a given gateway, for example <c>mesh://COM3/E072A1F97B84</c>.</summary>
    public static string BuildAddress(string gatewayTarget, string mac) => $"mesh://{gatewayTarget}/{NormalizeMac(mac)}";

    public static bool TryParseAddress(string? address, out string gatewayTarget, out string mac)
    {
        gatewayTarget = mac = string.Empty;
        if (address is null || !address.StartsWith("mesh://", StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = address["mesh://".Length..];
        var slash = rest.LastIndexOf('/');
        if (slash <= 0 || !TryNormalizeMac(rest[(slash + 1)..], out mac))
            return false;

        gatewayTarget = rest[..slash];
        return true;
    }

    internal static string Describe(IEnumerable<MeshNodeInfo> nodes) =>
        string.Join(", ", nodes.Select(n => $"{n.DisplayName} ({n.Mac})"));

    internal static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
