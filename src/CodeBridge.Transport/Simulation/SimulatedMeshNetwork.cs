using CodeBridge.Core.Abstractions;
using CodeBridge.Transport.Mesh;

namespace CodeBridge.Transport.Simulation;

/// <summary>
/// A virtual ESP-NOW mesh: one gateway and any number of nodes, each a <see cref="SimulatedTransport"/>. The gateway answers
/// <c>MESH</c>, <c>MSCAN</c> and <c>MRLY</c> exactly as the firmware does, so fleets, flows and the editor can be built and tested without a
/// second board, a radio or a key. Take a node offline to see what a lost node looks like.
/// </summary>
public sealed class SimulatedMeshNetwork
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SimNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly SimulatedTransport _gatewayBoard;
    private bool _enabled = true;
    private bool _hasKey = true;
    private string _name = "gateway";

    public SimulatedMeshNetwork(string gatewayMac = "AABBCC000001", SimulatedBoardOptions? gatewayOptions = null, bool provisioned = true)
    {
        GatewayMac = MeshProtocol.NormalizeMac(gatewayMac);
        _gatewayBoard = new SimulatedTransport(gatewayOptions);
        _enabled = _hasKey = provisioned;
        Gateway = new GatewayTransport(this);
    }

    public string GatewayMac { get; }

    /// <summary>The gateway as a transport: connect an <c>ESP32Board</c> or a <see cref="MeshGateway"/> to it.</summary>
    public ITransport Gateway { get; }

    /// <summary>The gateway's own virtual pins.</summary>
    public SimulatedTransport GatewayBoard => _gatewayBoard;

    /// <summary>Adds a node and returns its virtual board, for scripting inputs and checking outputs.</summary>
    public SimulatedTransport AddNode(string mac, string name = "", string chip = "ESP32-S3-SIM", SimulatedBoardOptions? options = null)
    {
        var normalized = MeshProtocol.NormalizeMac(mac);
        options ??= new SimulatedBoardOptions();
        options.ChipName = chip;

        var board = new SimulatedTransport(options);
        board.ConnectAsync().GetAwaiter().GetResult();
        lock (_gate)
            _nodes[normalized] = new SimNode(normalized, name, chip, board);

        return board;
    }

    public void SetOnline(string mac, bool online)
    {
        lock (_gate)
        {
            if (_nodes.TryGetValue(MeshProtocol.NormalizeMac(mac), out var node))
                node.Online = online;
        }
    }

    public SimulatedTransport? Node(string mac)
    {
        lock (_gate)
            return _nodes.TryGetValue(MeshProtocol.NormalizeMac(mac), out var node) ? node.Board : null;
    }

    private string? Handle(string command)
    {
        var sep = command.IndexOf(':');
        var name = (sep < 0 ? command : command[..sep]).ToUpperInvariant();
        var args = sep < 0 ? string.Empty : command[(sep + 1)..];

        switch (name)
        {
            case "MESH":
                return $"OK:{(_enabled ? "on" : "off")},{GatewayMac},6,1,{(_hasKey ? 1 : 0)},{_name},0,0,0,0";
            case "MESHKEY":
                _hasKey = args.Length == 32;
                if (!_hasKey)
                    _enabled = false;
                return args.Length is 0 or 32 ? "OK" : "ERR:Mesh key must be 32 hex characters";
            case "MESHNAME":
                _name = args;
                return "OK";
            case "MESHCH":
                return "OK";
            case "MESHON":
                if (!_hasKey)
                    return "ERR:MESH_NOKEY";
                _enabled = true;
                return "OK";
            case "MESHOFF":
                _enabled = false;
                return "OK";
            case "MSCAN":
                if (!_hasKey) return "ERR:MESH_NOKEY";
                if (!_enabled) return "ERR:MESH_OFF";
                lock (_gate)
                    return "OK:" + string.Join(';', _nodes.Values.Where(n => n.Online).Select(n => $"{n.Mac},{n.Chip},0.9.1,{n.Name},1"));
            case "MRLY":
                return Relay(args);
            default:
                return null;
        }
    }

    private string Relay(string args)
    {
        if (!_hasKey) return "ERR:MESH_NOKEY";
        if (!_enabled) return "ERR:MESH_OFF";
        if (args.Length < 14 || args[12] != ':' || !MeshProtocol.TryNormalizeMac(args[..12], out var mac))
            return "ERR:MESH_BAD_REQUEST";

        var inner = args[13..];
        var innerName = inner.Split(':')[0];
        if (MeshProtocol.NeverRelayed.Contains(innerName))
            return "ERR:MESH_DENIED";

        SimNode? node;
        lock (_gate)
            _nodes.TryGetValue(mac, out node);

        if (node is null || !node.Online)
            return "ERR:MESH_TIMEOUT";

        return "RLY:" + node.Board.SendCommandAsync(inner).GetAwaiter().GetResult();
    }

    private sealed class SimNode
    {
        public SimNode(string mac, string name, string chip, SimulatedTransport board)
        {
            Mac = mac;
            Name = name;
            Chip = chip;
            Board = board;
        }

        public string Mac { get; }
        public string Name { get; }
        public string Chip { get; }
        public SimulatedTransport Board { get; }
        public bool Online { get; set; } = true;
    }

    private sealed class GatewayTransport : ITransport
    {
        private readonly SimulatedMeshNetwork _network;

        public GatewayTransport(SimulatedMeshNetwork network) => _network = network;

        public bool IsConnected => _network._gatewayBoard.IsConnected;

        public event EventHandler<DataReceivedEventArgs>? DataReceived
        {
            add => _network._gatewayBoard.DataReceived += value;
            remove => _network._gatewayBoard.DataReceived -= value;
        }

        public Task ConnectAsync(CancellationToken ct = default) => _network._gatewayBoard.ConnectAsync(ct);

        public Task DisconnectAsync(CancellationToken ct = default) => _network._gatewayBoard.DisconnectAsync(ct);

        public Task<string> SendCommandAsync(string command, CancellationToken ct = default)
        {
            command = command.Trim(); // like the firmware, which reads one line without its terminator
            var mesh = _network.Handle(command);
            return mesh is not null ? Task.FromResult(mesh) : _network._gatewayBoard.SendCommandAsync(command, ct);
        }

        public Task SendRawAsync(byte[] data, CancellationToken ct = default) => _network._gatewayBoard.SendRawAsync(data, ct);

        public Task<byte[]> ReceiveRawAsync(int length, CancellationToken ct = default) => _network._gatewayBoard.ReceiveRawAsync(length, ct);

        public void Dispose()
        {
        }
    }
}
