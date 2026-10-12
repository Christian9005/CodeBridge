using System.Text.Json;
using CodeBridge.Flow;
using CodeBridge.HomeAssistant;
using CodeBridge.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeBridge.FlowHost;

// Home Assistant from the editor: find the installation, test the MQTT broker, and keep the board published there.
internal static partial class Program
{
    private static async Task<int> HomeAssistantFindAsync(CancellationToken ct)
    {
        var found = await HomeAssistantFinder.FindAsync(TimeSpan.FromSeconds(4), ct);
        Emit(new
        {
            type = "ha-instances",
            instances = found.Select(i => new { name = i.Name, host = i.Host, address = i.Address, port = i.Port, version = i.Version, broker = i.BrokerHost })
        });
        return 0;
    }

    /// <summary>The broker settings travel on standard input (never on the command line) as one JSON line.</summary>
    private static async Task<HomeAssistantOptions> ReadHomeAssistantOptionsAsync(CancellationToken ct)
    {
        var line = await Console.In.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(line))
            throw new InvalidOperationException("The Home Assistant settings were not provided.");

        return ParseHomeAssistantOptions(line);
    }

    internal static HomeAssistantOptions ParseHomeAssistantOptions(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        var broker = Text("broker")?.Trim();
        if (string.IsNullOrEmpty(broker))
            throw new InvalidOperationException("Enter the address of the MQTT broker.");

        var options = new HomeAssistantOptions
        {
            Broker = broker,
            Username = string.IsNullOrWhiteSpace(Text("username")) ? null : Text("username"),
            Password = Text("password"),
            UseTls = root.TryGetProperty("tls", out var tls) && tls.ValueKind == JsonValueKind.True
        };
        if (root.TryGetProperty("port", out var port) && port.TryGetInt32(out var number) && number is > 0 and <= 65535)
            options.Port = number;
        if (!string.IsNullOrWhiteSpace(Text("deviceId")))
            options.DeviceId = HomeAssistantOptions.SanitizeId(Text("deviceId")!);
        if (!string.IsNullOrWhiteSpace(Text("deviceName")))
            options.DeviceName = Text("deviceName")!.Trim();

        return options;
    }

    private static async Task<int> HomeAssistantTestAsync(CancellationToken ct)
    {
        var options = await ReadHomeAssistantOptionsAsync(ct);
        var result = await HomeAssistantBrokerCheck.RunAsync(options, ct: ct);
        Emit(new { type = "ha-test", ok = result.Ok, reason = result.Reason, message = result.Message });
        return 0;
    }

    /// <summary>
    /// Keeps the board visible in Home Assistant until "stop" arrives on standard input. The entities come from the input and output
    /// blocks of the flow (--flow), or are the built-in LED when no flow is given.
    /// </summary>
    private static async Task<int> HomeAssistantBridgeAsync(Options options, CancellationTokenSource cancellation)
    {
        var ct = cancellation.Token;
        var broker = await ReadHomeAssistantOptionsAsync(ct);

        FlowDocument? document = options.Has("flow") ? LoadDocument(options) : null;
        var profile = BuiltInBoardProfiles.FindById(options.Get("board") ?? document?.BoardId) ?? BuiltInBoardProfiles.Default;
        var entities = HomeAssistantFlow.EntitiesFor(document, profile);
        if (entities.Count == 0)
        {
            Emit(new { type = "result", success = false, message = "The flow has no input or output blocks to show in Home Assistant. Add a Digital Output, PWM Output, Analog Read or Digital Read block." });
            return 1;
        }

        var target = options.Get("port") ?? options.Get("host")
            ?? throw new InvalidOperationException("Select a serial port (or enter the board's IP address) before connecting.");

        _ = Task.Run(async () =>
        {
            while (await Console.In.ReadLineAsync() is { } line)
            {
                if (line.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase))
                    break;
            }

            cancellation.Cancel();
        });

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new EmitLoggerProvider());
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Services.AddCodeBridge(o =>
        {
            o.Port = target;
            o.Board = BoardKindFor(profile);
            o.AccessToken = options.Get("token") ?? Environment.GetEnvironmentVariable("CODEBRIDGE_TOKEN");
            o.TcpPort = options.GetInt("tcp-port", 8080);
            o.FallbackToSimulator = string.Equals(target, "simulator", StringComparison.OrdinalIgnoreCase);
        });
        builder.Services.AddCodeBridgeHomeAssistant(
            o =>
            {
                o.Broker = broker.Broker;
                o.Port = broker.Port;
                o.Username = broker.Username;
                o.Password = broker.Password;
                o.UseTls = broker.UseTls;
                o.DeviceId = broker.DeviceId;
                o.DeviceName = broker.DeviceName;
            },
            ha =>
            {
                foreach (var entity in entities)
                    ha.Add(entity);
            });

        using var host = builder.Build();
        Emit(new { type = "status", state = "connecting", message = $"Connecting to the board and to the broker at {broker.Broker}..." });
        Emit(new { type = "ha-entities", device = broker.DeviceId, entities = entities.Select(e => new { id = e.Id, name = e.Name, kind = e.Component }) });

        try
        {
            await host.StartAsync(ct);
            Emit(new { type = "status", state = "running", message = $"Publishing {entities.Count} entities to Home Assistant." });
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            // asked to stop
        }

        await host.StopAsync(CancellationToken.None);
        Emit(new { type = "result", success = true, cancelled = true, message = "Stopped publishing to Home Assistant." });
        return 0;
    }

    internal static BoardKind BoardKindFor(BoardProfile profile) => profile.Id switch
    {
        "arduino-uno" => BoardKind.ArduinoUno,
        "arduino-nano" or "arduino-nano-old" => BoardKind.ArduinoNano,
        "arduino-mega" => BoardKind.ArduinoMega,
        "esp32-s3-devkit" => BoardKind.Esp32S3,
        "esp32-c3-devkit" => BoardKind.Esp32C3,
        _ => BoardKind.Esp32
    };

    /// <summary>Sends the bridge's log lines to the editor as JSON messages (standard output carries the protocol).</summary>
    private sealed class EmitLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new EmitLogger();

        public void Dispose()
        {
        }

        private sealed class EmitLogger : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                    return;

                Emit(new { type = "log", level = logLevel.ToString().ToLowerInvariant(), text = formatter(state, exception) });
            }
        }
    }
}
