using System.Text.Json;
using CodeBridge.Core.Abstractions;
using CodeBridge.Core.Enums;
using CodeBridge.Designer.WinForms;
using CodeBridge.Designer.WinForms.Hardware;
using CodeBridge.Flow;
using CodeBridge.Transport.Mesh;
using CodeBridge.Transport.Serial;
using CodeBridge.Transport.Simulation;
using CodeBridge.Transport.Wifi;

namespace CodeBridge.FlowHost;

// The ESP-NOW mesh from the editor: look at a gateway, list the boards it hears, set boards up over USB, and reach a node like any board.
internal static partial class Program
{
    /// <summary>Opens the connection to a gateway: a COM port or the IP address of a paired Wi-Fi board (token in CODEBRIDGE_TOKEN).</summary>
    private static async Task<ITransport> OpenGatewayAsync(string target, Options options, CancellationToken ct)
    {
        // "simulator" is a gateway with three virtual nodes, to try the mesh (and build flows for it) with no boards and no radio.
        if (string.Equals(target, "simulator", StringComparison.OrdinalIgnoreCase))
        {
            var simulated = SimulatedMesh.Value;
            await simulated.Gateway.ConnectAsync(ct);
            return new SharedTransport(simulated.Gateway);
        }

        var isSerial = target.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || target.StartsWith("/dev/", StringComparison.Ordinal);
        ITransport transport = isSerial
            ? new SerialTransport(target, options.GetInt("baud", 115200))
            : new WifiTransport(target, options.GetInt("tcp-port", 8080), options.Get("token") ?? Environment.GetEnvironmentVariable("CODEBRIDGE_TOKEN"));

        try
        {
            await transport.ConnectAsync(ct);
            return transport;
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    private static readonly Lazy<SimulatedMeshNetwork> SimulatedMesh = new(() =>
    {
        var network = new SimulatedMeshNetwork("AABBCC000001");
        network.AddNode("AABBCC000002", "kitchen", "ESP32-D0WD-V3");
        network.AddNode("AABBCC000003", "garage", "ESP32-C3");
        network.AddNode("AABBCC000004", "garden", "ESP32-S3");
        return network;
    });

    /// <summary>Lets several users share the simulated gateway: closing one does not close it for the rest.</summary>
    private sealed class SharedTransport : ITransport
    {
        private readonly ITransport _inner;

        public SharedTransport(ITransport inner) => _inner = inner;

        public bool IsConnected => _inner.IsConnected;

        public event EventHandler<DataReceivedEventArgs>? DataReceived
        {
            add => _inner.DataReceived += value;
            remove => _inner.DataReceived -= value;
        }

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> SendCommandAsync(string command, CancellationToken ct = default) => _inner.SendCommandAsync(command, ct);

        public Task SendRawAsync(byte[] data, CancellationToken ct = default) => _inner.SendRawAsync(data, ct);

        public Task<byte[]> ReceiveRawAsync(int length, CancellationToken ct = default) => _inner.ReceiveRawAsync(length, ct);

        public void Dispose()
        {
        }
    }

    private static string GatewayTarget(Options options) =>
        options.Get("port") ?? options.Get("host") ?? throw new InvalidOperationException("Select the gateway: a COM port or the board's IP address.");

    private static async Task<int> MeshStatusAsync(Options options, CancellationToken ct)
    {
        using var transport = await OpenGatewayAsync(GatewayTarget(options), options, ct);
        try
        {
            var status = await new MeshGateway(transport).GetStatusAsync(ct);
            Emit(new { type = "mesh-status", status.Enabled, status.Mac, status.Channel, status.Locked, status.HasKey, status.Name, status.Received, status.BadSignatures, status.Delivered, status.Lost });
        }
        catch (MeshException ex) when (ex.Code == "MESH_PROTOCOL")
        {
            // an older firmware answers "ERR:Unknown command"
            Emit(new { type = "mesh-unsupported", message = "This board's firmware has no mesh support. Upload the firmware again (0.9.1 or later)." });
        }

        return 0;
    }

    private static async Task<int> MeshScanAsync(Options options, CancellationToken ct)
    {
        var target = GatewayTarget(options);
        using var transport = await OpenGatewayAsync(target, options, ct);
        var gateway = new MeshGateway(transport);
        var nodes = await gateway.ScanAsync(ct);
        Emit(new
        {
            type = "mesh-nodes",
            gateway = target,
            nodes = nodes.Select(n => new
            {
                mac = n.Mac,
                name = n.Name,
                board = n.Board,
                firmware = n.Firmware,
                ageSeconds = (int)n.Age.TotalSeconds,
                address = MeshProtocol.BuildAddress(target, n.Mac),
                suggestedBoard = SuggestBoardForChip(n.Board)
            })
        });
        return 0;
    }

    /// <summary>
    /// Sets a board up for the mesh over USB. The request arrives on standard input as one JSON line so the key never shows in a command line:
    /// <c>{"key":"…32 hex…","name":"garage","channel":6}</c>. Without a key a new one is made and returned, to give to the other boards.
    /// </summary>
    private static async Task<int> MeshProvisionAsync(Options options, CancellationToken ct)
    {
        var line = await Console.In.ReadLineAsync(ct);
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(line) ? "{}" : line);
        var root = document.RootElement;
        string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        var key = Text("key");
        var generated = string.IsNullOrWhiteSpace(key);
        key = generated ? MeshProtocol.GenerateKey() : key!.Trim().ToLowerInvariant();
        int? channel = root.TryGetProperty("channel", out var ch) && ch.TryGetInt32(out var number) && number > 0 ? number : null;

        var port = GatewayTarget(options);
        if (!(port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || port.StartsWith("/dev/", StringComparison.Ordinal) || string.Equals(port, "simulator", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The mesh key is only written over USB. Connect the board with its cable.");

        using var transport = await OpenGatewayAsync(port, options, ct);
        foreach (var command in MeshProtocol.ProvisioningCommands(key, Text("name"), channel))
        {
            var answer = await transport.SendCommandAsync(command, ct);
            if (!answer.StartsWith("OK", StringComparison.Ordinal))
                throw new InvalidOperationException($"The board refused '{command.Split(':')[0]}': {answer}");
        }

        var status = await new MeshGateway(transport).GetStatusAsync(ct);
        Emit(new { type = "mesh-provisioned", mac = status.Mac, name = status.Name, key = generated ? key : null, generated, channel = status.Channel });
        return 0;
    }

    /// <summary>
    /// Builds the connection request for the selected target. A <c>mesh://gateway/MAC</c> target opens the gateway first and reaches the node through it.
    /// </summary>
    private static async Task<BoardConnectionRequest> CreateRequestAsync(BoardProfile profile, Options options, CancellationToken ct)
    {
        var target = options.Get("port") ?? options.Get("host");
        if (!MeshProtocol.TryParseAddress(target, out var gatewayTarget, out var mac))
            return CreateRequest(profile, options);

        var gatewayTransport = await OpenGatewayAsync(gatewayTarget, options, ct);
        var node = new MeshGateway(gatewayTransport).CreateNode(mac, ownedLink: gatewayTransport);
        return new BoardConnectionRequest(profile, CodeBridgeTransportMode.Serial, string.Empty, options.GetInt("baud", 115200), string.Empty, options.GetInt("tcp-port", 8080))
        {
            Transport = node
        };
    }
}
