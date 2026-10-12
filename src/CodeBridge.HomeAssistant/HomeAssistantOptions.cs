namespace CodeBridge.HomeAssistant;

/// <summary>Where the MQTT broker of Home Assistant is and how the board shows up there.</summary>
public sealed class HomeAssistantOptions
{
    /// <summary>MQTT broker host (usually the Home Assistant machine running the Mosquitto add-on). Use <see cref="HomeAssistantFinder"/> to look for it.</summary>
    public string Broker { get; set; } = "homeassistant.local";

    public int Port { get; set; } = 1883;

    /// <summary>Create a dedicated Home Assistant user for CodeBridge (Settings → People → Users) and use it here.</summary>
    public string? Username { get; set; }

    public string? Password { get; set; }

    public bool UseTls { get; set; }

    /// <summary>Stable id of this board in Home Assistant. Entities keep their history and automations while it stays the same.</summary>
    public string DeviceId { get; set; } = "codebridge-" + SanitizeId(Environment.MachineName);

    /// <summary>Name shown for the device in Home Assistant.</summary>
    public string DeviceName { get; set; } = "CodeBridge board";

    /// <summary>MQTT discovery prefix configured in Home Assistant (default <c>homeassistant</c>).</summary>
    public string DiscoveryPrefix { get; set; } = "homeassistant";

    /// <summary>Prefix of the state and command topics.</summary>
    public string BaseTopic { get; set; } = "codebridge";

    /// <summary>Re-publish unchanged sensor values at least this often, so Home Assistant never shows a stale reading as current.</summary>
    public TimeSpan Heartbeat { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>First wait before reconnecting to the broker; it doubles up to <see cref="MaxReconnectDelay"/>.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(60);

    public static string SanitizeId(string value)
    {
        var chars = value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var id = new string(chars);
        while (id.Contains("--", StringComparison.Ordinal))
            id = id.Replace("--", "-", StringComparison.Ordinal);

        id = id.Trim('-');
        return id.Length == 0 ? "board" : id;
    }
}
