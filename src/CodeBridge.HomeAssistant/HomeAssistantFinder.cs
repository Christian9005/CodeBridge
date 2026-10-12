using CodeBridge.Transport.Discovery;

namespace CodeBridge.HomeAssistant;

/// <summary>A Home Assistant installation announced on the local network.</summary>
public sealed record HomeAssistantInstance(string Name, string Host, string? Address, int Port, string? BaseUrl, string? Version)
{
    /// <summary>The address to give to <see cref="HomeAssistantOptions.Broker"/>: the MQTT broker normally runs next to Home Assistant.</summary>
    public string BrokerHost => Address ?? Host;
}

/// <summary>
/// Looks for Home Assistant on the local network with mDNS (it announces <c>_home-assistant._tcp</c>), so a user does not have to
/// know its address. The MQTT broker normally lives on the same machine.
/// </summary>
public static class HomeAssistantFinder
{
    private const string ServiceName = "_home-assistant._tcp.local";

    /// <summary>Asks the network and collects answers for <paramref name="timeout"/> (default 3 seconds).</summary>
    public static async Task<IReadOnlyList<HomeAssistantInstance>> FindAsync(TimeSpan? timeout = null, CancellationToken ct = default) =>
        ToInstances(await MdnsClient.QueryAsync(ServiceName, timeout ?? TimeSpan.FromSeconds(3), ct));

    internal static IReadOnlyList<HomeAssistantInstance> ToInstances(IEnumerable<MdnsRecord> records) =>
        MdnsParser.Services(records, "_home-assistant._tcp")
            .Select(service =>
            {
                var label = service.Name;
                if (service.Text.TryGetValue("location_name", out var location) && !string.IsNullOrWhiteSpace(location))
                    label = location;

                return new HomeAssistantInstance(
                    label,
                    service.Host,
                    service.Address,
                    service.Port > 0 ? service.Port : 8123,
                    service.Text.TryGetValue("base_url", out var baseUrl) ? baseUrl : null,
                    service.Text.TryGetValue("version", out var version) ? version : null);
            })
            .ToList();
}
