namespace CodeBridge.Transport.Discovery;

/// <summary>A CodeBridge board found on the local network.</summary>
/// <param name="Id">Stable identifier of the board (its MAC address); it survives IP changes, so pairing tokens are filed under it.</param>
/// <param name="Host">mDNS name such as <c>codebridge-a1b2.local</c>.</param>
/// <param name="Address">Current IPv4 address, when the board announced it.</param>
public sealed record WifiBoard(string Id, string Name, string Host, string? Address, int Port, string? Firmware, string? Chip, bool RequiresToken)
{
    /// <summary>What to connect to: the address when known (always resolvable), otherwise the mDNS name.</summary>
    public string Target => Address ?? Host;
}

/// <summary>
/// Looks for boards that run the CodeBridge firmware (0.9+) on the local network. The firmware announces itself as
/// <c>_codebridge._tcp</c> with its MAC address, firmware version and chip, so no one has to read an IP address off a serial monitor.
/// </summary>
public static class WifiBoardFinder
{
    public const string ServiceName = "_codebridge._tcp.local";

    public static async Task<IReadOnlyList<WifiBoard>> FindAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var records = await MdnsClient.QueryAsync(ServiceName, timeout ?? TimeSpan.FromSeconds(1.5), ct);
        return FromRecords(records);
    }

    public static IReadOnlyList<WifiBoard> FromRecords(IEnumerable<MdnsRecord> records) =>
        MdnsParser.Services(records, "_codebridge._tcp")
            .Select(service =>
            {
                service.Text.TryGetValue("id", out var id);
                service.Text.TryGetValue("fw", out var firmware);
                service.Text.TryGetValue("board", out var chip);
                service.Text.TryGetValue("auth", out var auth);
                return new WifiBoard(
                    string.IsNullOrWhiteSpace(id) ? service.Host : id,
                    service.Name,
                    service.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? service.Host : service.Host + ".local",
                    service.Address,
                    service.Port > 0 ? service.Port : 8080,
                    firmware,
                    chip,
                    auth == "1");
            })
            .OrderBy(board => board.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
