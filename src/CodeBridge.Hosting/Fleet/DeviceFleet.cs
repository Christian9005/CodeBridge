using System.Text.Json;
using System.Text.Json.Serialization;
using CodeBridge.Transport.Discovery;
using CodeBridge.Transport.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeBridge.Hosting;

/// <summary>How a device of the fleet is reached.</summary>
public enum DeviceKind
{
    /// <summary>First board found on USB, or the simulator when there is none.</summary>
    Auto,

    /// <summary>A USB serial port such as <c>COM3</c>.</summary>
    Usb,

    /// <summary>A Wi-Fi board: its IP address or <c>codebridge-xxxx.local</c>. Needs the pairing token.</summary>
    Wifi,

    /// <summary>A board without a cable or Wi-Fi link of its own, reached through a gateway over ESP-NOW.</summary>
    MeshNode,

    /// <summary>The built-in virtual board.</summary>
    Simulator
}

/// <summary>One device of the fleet: a name the user chose and where to find it. Secrets (tokens, the mesh key) are never part of it.</summary>
public sealed class DeviceDefinition
{
    /// <summary>Short stable identifier (<c>kitchen</c>, <c>garage-door</c>); the key used in code and in saved files.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>What people see. Defaults to the id.</summary>
    public string? Name { get; set; }

    public DeviceKind Kind { get; set; } = DeviceKind.Auto;

    public BoardKind Board { get; set; } = BoardKind.Esp32;

    /// <summary>COM port, IP address or host name. Not used for mesh nodes.</summary>
    public string? Target { get; set; }

    /// <summary>Mesh nodes: the id of the fleet device that is the gateway.</summary>
    public string? GatewayId { get; set; }

    /// <summary>Mesh nodes: the node's MAC address.</summary>
    public string? Mac { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name!;

    internal static string Slug(string text)
    {
        var chars = text.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);

        return slug.Trim('-');
    }
}

/// <summary>Settings of the fleet. Bind them from the <c>"CodeBridgeFleet"</c> configuration section or set them in code.</summary>
public sealed class FleetOptions
{
    /// <summary>The devices known at start. More can be added at run time with <see cref="DeviceFleet.Add(DeviceDefinition)"/>.</summary>
    public List<DeviceDefinition> Devices { get; set; } = new();

    /// <summary>Connect every device in the background when the host starts.</summary>
    public bool ConnectOnStart { get; set; } = true;

    /// <summary>
    /// The 32-hex-digit key of the ESP-NOW mesh, for tools that provision boards. Falls back to <c>CODEBRIDGE_MESH_KEY</c>.
    /// Nodes themselves are reached through their gateway and need no key on the PC.
    /// </summary>
    public string? MeshKey { get; set; }

    /// <summary>A file that remembers the fleet between runs (definitions only, no secrets). Null keeps it in memory.</summary>
    public string? StorePath { get; set; }

    /// <summary>Gives the pairing token of a Wi-Fi device. Falls back to the <c>CODEBRIDGE_TOKEN_&lt;ID&gt;</c> or <c>CODEBRIDGE_TOKEN</c> environment variables.</summary>
    public Func<DeviceDefinition, string?>? TokenProvider { get; set; }

    /// <summary>Applied to every device's options just before its board service is created (observers, timeouts, reconnect delays).</summary>
    public Action<DeviceDefinition, CodeBridgeOptions>? ConfigureDevice { get; set; }
}

/// <summary>One live device: its definition and the shared connection to it.</summary>
public sealed class FleetDevice
{
    internal FleetDevice(DeviceDefinition definition, BoardService board)
    {
        Definition = definition;
        Board = board;
    }

    public DeviceDefinition Definition { get; }

    public string Id => Definition.Id;

    public string Name => Definition.DisplayName;

    /// <summary>The same <see cref="BoardService"/> the rest of the SDK uses: typed reads and writes, <c>UseAsync</c>, reconnection.</summary>
    public BoardService Board { get; }

    public BoardConnectionState State => Board.State;

    public bool IsConnected => Board.IsConnected;
}

/// <summary>Something found on the network or in the air that is not in the fleet yet.</summary>
public sealed record DiscoveredDevice(DeviceKind Kind, string Name, string Target, string? Mac, string? GatewayId, string? Chip, string? Firmware, bool RequiresToken)
{
    /// <summary>A definition ready to add: <c>fleet.Add(found.ToDefinition())</c>.</summary>
    public DeviceDefinition ToDefinition(string? id = null)
    {
        var chip = Chip ?? string.Empty;
        return new DeviceDefinition
        {
            Id = string.IsNullOrWhiteSpace(id) ? DeviceDefinition.Slug(string.IsNullOrWhiteSpace(Name) ? Mac ?? Target : Name) : id!,
            Name = Name,
            Kind = Kind,
            Board = chip.Contains("S3", StringComparison.OrdinalIgnoreCase) ? BoardKind.Esp32S3
                : chip.Contains("S2", StringComparison.OrdinalIgnoreCase) ? BoardKind.Esp32S2
                : chip.Contains("C3", StringComparison.OrdinalIgnoreCase) ? BoardKind.Esp32C3
                : BoardKind.Esp32,
            Target = Kind == DeviceKind.MeshNode ? null : Target,
            GatewayId = GatewayId,
            Mac = Mac
        };
    }
}

/// <summary>The outcome of running something on one device of the fleet.</summary>
public sealed record FleetResult<T>(FleetDevice Device, T? Value, Exception? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Any number of named boards behind one object: USB, Wi-Fi and boards reached over an ESP-NOW mesh through a gateway that keeps its own Wi-Fi.
/// Each device is a <see cref="BoardService"/>, so everything built for one board (flows, Blazor panels, Home Assistant, the MCP server) works on all of them.
/// <code>
/// fleet.Add(new DeviceDefinition { Id = "hub", Kind = DeviceKind.Wifi, Target = "192.168.1.50" });
/// fleet.Add(new DeviceDefinition { Id = "garage", Kind = DeviceKind.MeshNode, GatewayId = "hub", Mac = "E072A1F97B84" });
/// await fleet.ConnectAllAsync();
/// await fleet["garage"].Board.WriteDigitalAsync(2, true);
/// </code>
/// </summary>
public sealed class DeviceFleet : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FleetDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly FleetOptions _options;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private bool _disposed;

    public DeviceFleet(IOptions<FleetOptions>? options = null, ILoggerFactory? loggers = null)
    {
        _options = options?.Value ?? new FleetOptions();
        _loggers = loggers ?? NullLoggerFactory.Instance;
        _logger = _loggers.CreateLogger<DeviceFleet>();

        var saved = _options.StorePath is { } path ? FleetStore.Load(path) : new List<DeviceDefinition>();
        foreach (var definition in _options.Devices.Concat(saved.Where(s => _options.Devices.All(o => !string.Equals(o.Id, s.Id, StringComparison.OrdinalIgnoreCase)))))
            Add(definition, save: false);
    }

    /// <summary>Raised when a device is added or removed, or any device changes state.</summary>
    public event Action? Changed;

    public IReadOnlyList<FleetDevice> Devices
    {
        get
        {
            lock (_gate)
                return _devices.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public FleetDevice this[string id] => TryGet(id) ?? throw new KeyNotFoundException($"There is no device '{id}' in the fleet. Known: {string.Join(", ", Devices.Select(d => d.Id))}.");

    public FleetDevice? TryGet(string id)
    {
        lock (_gate)
            return _devices.TryGetValue(id, out var device) ? device : null;
    }

    public FleetDevice Add(DeviceDefinition definition) => Add(definition, save: true);

    private FleetDevice Add(DeviceDefinition definition, bool save)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(definition);

        definition.Id = DeviceDefinition.Slug(definition.Id);
        if (definition.Id.Length == 0)
            throw new ArgumentException("A device needs an id (letters, digits and dashes).", nameof(definition));

        if (definition.Kind == DeviceKind.MeshNode)
        {
            if (!MeshProtocol.TryNormalizeMac(definition.Mac, out var mac))
                throw new ArgumentException($"Device '{definition.Id}': a mesh node needs its MAC address.", nameof(definition));

            definition.Mac = mac;
            if (string.IsNullOrWhiteSpace(definition.GatewayId))
                throw new ArgumentException($"Device '{definition.Id}': a mesh node needs the id of its gateway.", nameof(definition));
        }

        var device = new FleetDevice(definition, new BoardService(Options.Create(BuildOptions(definition)), _loggers.CreateLogger<BoardService>()));
        lock (_gate)
        {
            if (_devices.ContainsKey(definition.Id))
                throw new InvalidOperationException($"The fleet already has a device '{definition.Id}'.");

            _devices[definition.Id] = device;
        }

        device.Board.Changed += RaiseChanged;
        Persist(save);
        RaiseChanged();
        return device;
    }

    public async Task<bool> RemoveAsync(string id)
    {
        FleetDevice? device;
        lock (_gate)
        {
            if (!_devices.Remove(id, out device))
                return false;
        }

        device.Board.Changed -= RaiseChanged;
        await device.Board.DisposeAsync();
        Persist(true);
        RaiseChanged();
        return true;
    }

    /// <summary>Connects every device: gateways first (nodes need them), then the rest in parallel. A device that fails does not stop the others.</summary>
    public async Task ConnectAllAsync(CancellationToken ct = default)
    {
        var devices = Devices;
        var gatewayIds = devices.Where(d => d.Definition.Kind == DeviceKind.MeshNode).Select(d => d.Definition.GatewayId!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        await Task.WhenAll(devices.Where(d => gatewayIds.Contains(d.Id)).Select(d => TryConnectAsync(d, ct)));
        await Task.WhenAll(devices.Where(d => !gatewayIds.Contains(d.Id)).Select(d => TryConnectAsync(d, ct)));
    }

    private async Task TryConnectAsync(FleetDevice device, CancellationToken ct)
    {
        try
        {
            await device.Board.ConnectAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Device '{Id}' is not available: {Message}", device.Id, ex.Message);
        }
    }

    public async Task DisconnectAllAsync()
    {
        foreach (var device in Devices)
            await device.Board.DisconnectAsync();
    }

    /// <summary>
    /// Runs <paramref name="action"/> on every device (or only the connected ones) at the same time and gathers what each returned or why it failed.
    /// One broken device never hides the results of the others.
    /// </summary>
    public async Task<IReadOnlyList<FleetResult<T>>> ForEachAsync<T>(Func<FleetDevice, CancellationToken, Task<T>> action, bool connectedOnly = true, CancellationToken ct = default)
    {
        var targets = Devices.Where(d => !connectedOnly || d.IsConnected).ToList();
        return await Task.WhenAll(targets.Select(async device =>
        {
            try
            {
                return new FleetResult<T>(device, await action(device, ct), null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return new FleetResult<T>(device, default, ex);
            }
        }));
    }

    /// <summary>
    /// Finds boards that are not in the fleet yet: Wi-Fi boards announced on the network (mDNS) and, through every connected device whose mesh is on,
    /// the boards in range over ESP-NOW. Adding them is one call: <c>fleet.Add(found.ToDefinition())</c>.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var known = Devices;
        var found = new List<DiscoveredDevice>();

        var wifi = WifiBoardFinder.FindAsync(timeout ?? TimeSpan.FromSeconds(1.5), ct);
        var mesh = Task.WhenAll(known.Where(d => d.IsConnected && d.Definition.Kind is not DeviceKind.MeshNode).Select(async gateway =>
        {
            try
            {
                var link = new DelegateMeshLink((command, token) => gateway.Board.SendRawAsync(command, token));
                var meshGateway = new MeshGateway(link);
                if (!(await meshGateway.GetStatusAsync(ct)).Enabled)
                    return Array.Empty<DiscoveredDevice>();

                return (await meshGateway.ScanAsync(ct))
                    .Select(n => new DiscoveredDevice(DeviceKind.MeshNode, n.DisplayName, MeshProtocol.BuildAddress(gateway.Id, n.Mac), n.Mac, gateway.Id, n.Board, n.Firmware, false))
                    .ToArray();
            }
            catch (Exception ex) when (ex is MeshException or TimeoutException or NotSupportedException or IOException or InvalidOperationException)
            {
                return Array.Empty<DiscoveredDevice>(); // that device has no mesh (older firmware) or it is off
            }
        }));

        foreach (var board in await wifi)
        {
            var already = known.Any(d => d.Definition.Kind == DeviceKind.Wifi &&
                (string.Equals(d.Definition.Target, board.Address, StringComparison.OrdinalIgnoreCase) || string.Equals(d.Definition.Target, board.Host, StringComparison.OrdinalIgnoreCase)));
            if (!already)
                found.Add(new DiscoveredDevice(DeviceKind.Wifi, board.Name, board.Target, board.Id, null, board.Chip, board.Firmware, board.RequiresToken));
        }

        foreach (var batch in await mesh)
        {
            foreach (var node in batch)
            {
                // a node can be heard by several gateways: list it once; a node (or gateway) already in the fleet is not new
                var isKnown = known.Any(d => string.Equals(d.Definition.Mac, node.Mac, StringComparison.OrdinalIgnoreCase));
                if (!isKnown && found.All(f => !string.Equals(f.Mac, node.Mac, StringComparison.OrdinalIgnoreCase)))
                    found.Add(node);
            }
        }

        return found;
    }

    private CodeBridgeOptions BuildOptions(DeviceDefinition definition)
    {
        var options = new CodeBridgeOptions
        {
            Board = definition.Board,
            AutoConnect = false,
            AutoReconnect = true,
            FallbackToSimulator = false
        };

        switch (definition.Kind)
        {
            case DeviceKind.Simulator:
                options.Port = "simulator";
                break;
            case DeviceKind.Usb:
                options.Port = definition.Target;
                break;
            case DeviceKind.Wifi:
                options.Port = definition.Target;
                options.AccessToken = _options.TokenProvider?.Invoke(definition)
                    ?? Environment.GetEnvironmentVariable("CODEBRIDGE_TOKEN_" + definition.Id.ToUpperInvariant().Replace('-', '_'))
                    ?? Environment.GetEnvironmentVariable("CODEBRIDGE_TOKEN");
                break;
            case DeviceKind.MeshNode:
                var gatewayId = definition.GatewayId!;
                var mac = definition.Mac!;
                options.TargetLabel = MeshProtocol.BuildAddress(gatewayId, mac);
                options.TransportFactory = _ =>
                {
                    var gateway = TryGet(gatewayId) ?? throw new InvalidOperationException($"The gateway '{gatewayId}' of '{definition.Id}' is not in the fleet.");
                    var link = new DelegateMeshLink((command, token) => gateway.Board.SendRawAsync(command, token));
                    return Task.FromResult<CodeBridge.Core.Abstractions.ITransport>(new MeshGateway(link).CreateNode(mac));
                };
                break;
            default:
                options.Port = "auto";
                options.FallbackToSimulator = true;
                break;
        }

        _options.ConfigureDevice?.Invoke(definition, options);
        return options;
    }

    private void Persist(bool save)
    {
        if (!save || _options.StorePath is not { } path)
            return;

        try
        {
            FleetStore.Save(path, Devices.Select(d => d.Definition));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The fleet could not be saved to {Path}: {Message}", path, ex.Message);
        }
    }

    private void RaiseChanged() => Changed?.Invoke();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        List<FleetDevice> devices;
        lock (_gate)
        {
            devices = _devices.Values.ToList();
            _devices.Clear();
        }

        foreach (var device in devices)
        {
            device.Board.Changed -= RaiseChanged;
            await device.Board.DisposeAsync();
        }
    }
}

/// <summary>Reads and writes the fleet's definitions as JSON (no secrets in it).</summary>
public static class FleetStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static List<DeviceDefinition> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<DeviceDefinition>>(File.ReadAllText(path), Json) ?? new List<DeviceDefinition>()
                : new List<DeviceDefinition>();
        }
        catch (JsonException)
        {
            return new List<DeviceDefinition>(); // a damaged file must not stop the application
        }
    }

    public static void Save(string path, IEnumerable<DeviceDefinition> devices)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(devices.ToList(), Json));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>Registration of the device fleet.</summary>
public static class FleetServiceCollectionExtensions
{
    /// <summary>
    /// Adds a <see cref="DeviceFleet"/> and, unless <see cref="FleetOptions.ConnectOnStart"/> is off, connects it in the background at startup.
    /// <code>services.AddCodeBridgeFleet(o =&gt; o.Devices.Add(new DeviceDefinition { Id = "bench", Kind = DeviceKind.Simulator }));</code>
    /// </summary>
    public static IServiceCollection AddCodeBridgeFleet(this IServiceCollection services, Action<FleetOptions>? configure = null)
    {
        var options = services.AddOptions<FleetOptions>();
        if (configure is not null)
            options.Configure(configure);

        services.AddSingleton<DeviceFleet>();
        services.AddHostedService<FleetHostedService>();
        return services;
    }

    /// <summary>Same as <see cref="AddCodeBridgeFleet(IServiceCollection, Action{FleetOptions}?)"/>, reading the <c>"CodeBridgeFleet"</c> section first.</summary>
    public static IServiceCollection AddCodeBridgeFleet(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration, Action<FleetOptions>? configure = null)
    {
        services.AddOptions<FleetOptions>().Bind(configuration.GetSection("CodeBridgeFleet"));
        return services.AddCodeBridgeFleet(configure);
    }
}

internal sealed class FleetHostedService : IHostedService
{
    private readonly DeviceFleet _fleet;
    private readonly FleetOptions _options;
    private readonly ILogger<FleetHostedService> _logger;

    public FleetHostedService(DeviceFleet fleet, IOptions<FleetOptions> options, ILogger<FleetHostedService> logger)
    {
        _fleet = fleet;
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.ConnectOnStart)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _fleet.ConnectAllAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("The fleet could not be connected at startup: {Message}", ex.Message);
                }
            }, CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => _fleet.DisconnectAllAsync();
}
