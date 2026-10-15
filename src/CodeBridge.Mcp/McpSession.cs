using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeBridge.Flow;
using CodeBridge.Hosting;
using CodeBridge.Transport;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace CodeBridge.Mcp;

/// <summary>What was last done to a pin.</summary>
public sealed record PinState(int Pin, string Kind, double Value, DateTime UpdatedUtc);

/// <summary>
/// The one board connection an assistant works with. It defaults to the built-in simulator, so an AI can design and test a
/// flow without any risk; reaching real hardware needs two deliberate steps by the human: starting the server with
/// <c>CODEBRIDGE_MCP_ALLOW_HARDWARE=1</c>, and the assistant passing <c>confirmed=true</c> on each action that moves something.
/// </summary>
public sealed class McpSession : IAsyncDisposable
{
    public const string HardwareSwitch = "CODEBRIDGE_MCP_ALLOW_HARDWARE";
    private const int MaxEvents = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = false
    };

    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly ConcurrentDictionary<int, PinState> _pins = new();
    private readonly List<string> _events = new();
    private readonly object _eventsLock = new();

    public static bool HardwareAllowed => Environment.GetEnvironmentVariable(HardwareSwitch) == "1";

    public BoardService? Board { get; private set; }

    public BoardProfile? Profile { get; private set; }

    public string? Port { get; private set; }

    public bool IsSimulated => Board?.IsSimulated ?? true;

    public static string ToJson(object value) => JsonSerializer.Serialize(value, JsonOptions);

    // ------------------------------------------------------------------ connection

    /// <summary>Connects to <paramref name="port"/> (<c>simulator</c> by default). Anything else is real hardware and needs the human's switch.</summary>
    public async Task<BoardService> ConnectAsync(string boardId, string port, CancellationToken ct)
    {
        var profile = BuiltInBoardProfiles.FindById(boardId)
            ?? throw new McpException($"Unknown board '{boardId}'. Call list_boards to see the supported ones.");

        port = string.IsNullOrWhiteSpace(port) ? "simulator" : port.Trim();
        var simulated = port.Equals("simulator", StringComparison.OrdinalIgnoreCase);
        if (!simulated && !HardwareAllowed)
        {
            throw new McpException(
                $"Real hardware is switched off. The person who runs this server must start it with {HardwareSwitch}=1 to allow '{port}'. " +
                "Until then, work on the simulator (port 'simulator'): it behaves like the board and cannot damage anything.");
        }

        await _connectGate.WaitAsync(ct);
        try
        {
            if (Board is not null)
                await Board.DisposeAsync();

            _pins.Clear();
            lock (_eventsLock) _events.Clear();

            var options = new CodeBridgeOptions
            {
                Port = simulated ? "simulator" : port,
                Board = ToKind(profile),
                AutoConnect = false,
                AutoReconnect = false,
                FallbackToSimulator = false,
                CommandObserver = Observe
            };

            var service = new BoardService(Options.Create(options));
            try
            {
                await service.ConnectAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await service.DisposeAsync();
                throw new McpException($"Could not connect to {profile.DisplayName} on '{port}': {ex.Message}");
            }

            Board = service;
            Profile = profile;
            Port = simulated ? "simulator" : port;
            return service;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>The current board, connecting to the simulator ESP32 on first use so an assistant can simply start experimenting.</summary>
    public async Task<BoardService> EnsureBoardAsync(CancellationToken ct)
    {
        if (Board is { IsConnected: true })
            return Board;

        return await ConnectAsync(Profile?.Id ?? BuiltInBoardProfiles.Esp32DevKit.Id, Port ?? "simulator", ct);
    }

    /// <summary>Real hardware only moves when the assistant says the human agreed.</summary>
    public void RequireConsentForHardware(bool confirmed, string action)
    {
        if (IsSimulated || confirmed)
            return;

        throw new McpException(
            $"{action} would act on real hardware ({Port}). Tell the user exactly what will happen, wait for their approval, then call again with confirmed=true.");
    }

    // ------------------------------------------------------------------ what the board did

    private void Observe(string command, string response)
    {
        var activity = PinActivity.Parse(command, response);
        if (activity is null)
            return;

        _pins[activity.Pin] = new PinState(activity.Pin, activity.Kind, activity.Value, DateTime.UtcNow);
        lock (_eventsLock)
        {
            _events.Add($"pin {activity.Pin}: {activity.Kind} {activity.Value:0.##}");
            if (_events.Count > MaxEvents)
                _events.RemoveAt(0);
        }
    }

    public IReadOnlyList<PinState> PinStates => _pins.Values.OrderBy(p => p.Pin).ToList();

    public int EventMark
    {
        get { lock (_eventsLock) return _events.Count; }
    }

    public IReadOnlyList<string> EventsSince(int mark)
    {
        lock (_eventsLock)
            return mark >= _events.Count ? Array.Empty<string>() : _events.Skip(mark).ToList();
    }

    private static BoardKind ToKind(BoardProfile profile) => profile.Id switch
    {
        "esp32-s3-devkit" => BoardKind.Esp32S3,
        "xiao-esp32-s3" => BoardKind.XiaoEsp32S3,
        "esp32-s2-mini" => BoardKind.Esp32S2,
        "esp32-c3-devkit" => BoardKind.Esp32C3,
        "arduino-uno" => BoardKind.ArduinoUno,
        "arduino-nano" or "arduino-nano-old" => BoardKind.ArduinoNano,
        "arduino-mega" => BoardKind.ArduinoMega,
        _ => BoardKind.Esp32
    };

    public async ValueTask DisposeAsync()
    {
        if (Board is not null)
            await Board.DisposeAsync();

        _connectGate.Dispose();
    }
}
