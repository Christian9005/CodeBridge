using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeBridge.Core.Protocol;
using CodeBridge.Transport.Serial;

namespace CodeBridge.Transport.Provisioning;

/// <summary>A Wi-Fi network seen by the board.</summary>
public sealed record WifiNetwork(string Ssid, int Rssi, bool Secured);

/// <summary>The board's current Wi-Fi state.</summary>
public sealed record WifiStatus(bool Connected, string? Ssid, string? IpAddress, int Port, int Rssi, string? Mac = null);

/// <summary>What the board reported after joining the network, plus the token the PC must use from now on.</summary>
public sealed record WifiProvisioningResult(string IpAddress, int Port, string AccessToken, string? DeviceId = null);

/// <summary>
/// Configures an ESP32's Wi-Fi over its USB connection: scans networks, stores the SSID and password in the board's flash
/// and pairs the board with a random access token. Provisioning is USB-only on purpose — the token is never sent over the
/// network and firmware 0.9+ ignores every Wi-Fi command until a client authenticates with it.
/// </summary>
public sealed class Esp32WifiProvisioner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly SerialTransport _serial;

    /// <param name="serial">A connected serial transport (the board over USB).</param>
    public Esp32WifiProvisioner(SerialTransport serial)
    {
        _serial = serial ?? throw new ArgumentNullException(nameof(serial));
    }

    /// <summary>Lists the networks the board can see (strongest first, at most ten).</summary>
    public async Task<IReadOnlyList<WifiNetwork>> ScanAsync(CancellationToken ct = default)
    {
        var data = await SendAsync(BridgeProtocol.BuildCommand(BridgeProtocol.CMD_WIFI_SCAN), timeoutSeconds: 15, ct);

        using var document = JsonDocument.Parse(data);
        var networks = new List<WifiNetwork>();
        if (document.RootElement.TryGetProperty("networks", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var ssid = item.TryGetProperty("ssid", out var s) ? s.GetString() : null;
                if (string.IsNullOrWhiteSpace(ssid))
                    continue;

                networks.Add(new WifiNetwork(
                    ssid!,
                    item.TryGetProperty("rssi", out var r) && r.TryGetInt32(out var rssi) ? rssi : 0,
                    item.TryGetProperty("enc", out var e) && e.ValueKind == JsonValueKind.True));
            }
        }

        return networks.GroupBy(n => n.Ssid).Select(g => g.OrderByDescending(n => n.Rssi).First()).OrderByDescending(n => n.Rssi).ToList();
    }

    /// <summary>Reads the board's current Wi-Fi state.</summary>
    public async Task<WifiStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var data = await SendAsync(BridgeProtocol.BuildCommand(BridgeProtocol.CMD_WIFI_STATUS), timeoutSeconds: 5, ct);
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;

        var connected = root.TryGetProperty("connected", out var c) && c.ValueKind == JsonValueKind.True;
        return new WifiStatus(
            connected,
            root.TryGetProperty("ssid", out var ssid) ? ssid.GetString() : null,
            root.TryGetProperty("ip", out var ip) ? ip.GetString() : null,
            root.TryGetProperty("port", out var port) && port.TryGetInt32(out var p) ? p : 8080,
            root.TryGetProperty("rssi", out var rssi) && rssi.TryGetInt32(out var rs) ? rs : 0,
            root.TryGetProperty("mac", out var mac) ? mac.GetString() : null);
    }

    /// <summary>
    /// Pairs the board (stores <paramref name="accessToken"/>, or a fresh random one) and joins the given network.
    /// The returned token must be passed to <c>WiFi(ip, port, token)</c>; the board is unreachable over the network without it.
    /// </summary>
    public async Task<WifiProvisioningResult> ProvisionAsync(string ssid, string password, string? accessToken = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ssid) || Encoding.UTF8.GetByteCount(ssid) > 32)
            throw new ArgumentException("The network name must be 1 to 32 bytes.", nameof(ssid));
        password ??= string.Empty;
        if (Encoding.UTF8.GetByteCount(password) > 63)
            throw new ArgumentException("The Wi-Fi password must be at most 63 bytes.", nameof(password));
        if (password.Length is > 0 and < 8)
            throw new ArgumentException("A WPA password must be at least 8 characters (leave it empty for an open network).", nameof(password));

        var token = accessToken ?? GenerateToken();
        ValidateToken(token);

        await SendAsync(BridgeProtocol.BuildCommand(BridgeProtocol.CMD_SET_TOKEN, token), timeoutSeconds: 5, ct);

        var data = await SendAsync(
            BridgeProtocol.BuildCommand(BridgeProtocol.CMD_WIFI_CONFIG_HEX, ToHex(ssid) + BridgeProtocol.SEPARATOR + ToHex(password)),
            timeoutSeconds: 30,
            ct);

        // OK:<ip>:<port>
        var parts = data.Split(BridgeProtocol.SEPARATOR);
        var port = parts.Length > 1 && int.TryParse(parts[^1], out var p) ? p : 8080;

        // The MAC address is the board's stable identity (the same one it announces on the network), so the token can be
        // found again after the router hands the board a different IP address.
        string? deviceId = null;
        try
        {
            var status = await GetStatusAsync(ct);
            deviceId = status.Mac?.Replace(":", string.Empty).ToLowerInvariant();
        }
        catch (Exception)
        {
            // pairing worked; the id is a convenience
        }

        return new WifiProvisioningResult(parts[0], port, token, deviceId);
    }

    /// <summary>Removes the pairing token: the board stops accepting network clients until it is paired again.</summary>
    public Task ClearTokenAsync(CancellationToken ct = default) =>
        SendAsync(BridgeProtocol.BuildCommand(BridgeProtocol.CMD_SET_TOKEN, string.Empty), timeoutSeconds: 5, ct);

    /// <summary>A random 128-bit token as 32 hex characters.</summary>
    public static string GenerateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static void ValidateToken(string token)
    {
        if (token.Length is < 8 or > 64 || token.Any(ch => ch < 33 || ch > 126))
            throw new ArgumentException("The token must be 8 to 64 printable characters without spaces.", nameof(token));
    }

    private static string ToHex(string value) => Convert.ToHexString(Encoding.UTF8.GetBytes(value));

    private async Task<string> SendAsync(string command, int timeoutSeconds, CancellationToken ct)
    {
        var previous = _serial.CommandTimeoutSeconds;
        _serial.CommandTimeoutSeconds = timeoutSeconds;
        try
        {
            var response = await _serial.SendCommandAsync(command, ct);
            var (success, data) = BridgeProtocol.ParseResponse(response);
            if (!success)
                throw new InvalidOperationException(FriendlyError(data));
            return data;
        }
        finally
        {
            _serial.CommandTimeoutSeconds = previous;
        }
    }

    private static string FriendlyError(string firmwareMessage) =>
        firmwareMessage.Contains("Unknown command", StringComparison.OrdinalIgnoreCase)
            ? "This board runs an old CodeBridge firmware without Wi-Fi pairing. Upload the current firmware (Upload FW) and try again."
            : firmwareMessage.Contains("FAILED", StringComparison.OrdinalIgnoreCase) || firmwareMessage.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)
                ? "The board could not join the network. Check the name and password, and that the network is 2.4 GHz (ESP32 does not support 5 GHz)."
                : firmwareMessage;
}
