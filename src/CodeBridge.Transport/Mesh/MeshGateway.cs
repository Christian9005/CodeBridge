using CodeBridge.Core.Abstractions;

namespace CodeBridge.Transport.Mesh;

/// <summary>How a command reaches the gateway board: any open connection to it (USB, Wi-Fi, a service that already owns it).</summary>
public interface IMeshLink
{
    /// <summary>Sends one protocol command to the gateway and returns its one-line answer.</summary>
    Task<string> SendAsync(string command, CancellationToken ct = default);
}

/// <summary><see cref="IMeshLink"/> over a transport that is already connected.</summary>
public sealed class TransportMeshLink : IMeshLink
{
    private readonly ITransport _transport;

    public TransportMeshLink(ITransport transport) => _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    // the transports expect the protocol's line terminator (BridgeProtocol.BuildCommand adds it)
    public Task<string> SendAsync(string command, CancellationToken ct = default) =>
        _transport.SendCommandAsync(
            command.EndsWith(CodeBridge.Core.Protocol.BridgeProtocol.TERMINATOR, StringComparison.Ordinal) ? command : command + CodeBridge.Core.Protocol.BridgeProtocol.TERMINATOR,
            ct);
}

/// <summary><see cref="IMeshLink"/> over a delegate (for example a hosted board's raw command).</summary>
public sealed class DelegateMeshLink : IMeshLink
{
    private readonly Func<string, CancellationToken, Task<string>> _send;

    public DelegateMeshLink(Func<string, CancellationToken, Task<string>> send) => _send = send ?? throw new ArgumentNullException(nameof(send));

    public Task<string> SendAsync(string command, CancellationToken ct = default) => _send(command, ct);
}

/// <summary>
/// The board the PC talks to, which forwards commands to the other boards of the mesh over ESP-NOW while keeping its own Wi-Fi.
/// One gateway serves any number of nodes: <see cref="CreateNode"/> returns a transport that makes a node look like a board of its own,
/// so flows, sensors, the Blazor panels and the Home Assistant bridge work on it unchanged.
/// </summary>
public sealed class MeshGateway
{
    private readonly IMeshLink _link;

    public MeshGateway(IMeshLink link) => _link = link ?? throw new ArgumentNullException(nameof(link));

    public MeshGateway(ITransport connectedTransport) : this(new TransportMeshLink(connectedTransport))
    {
    }

    public async Task<MeshStatus> GetStatusAsync(CancellationToken ct = default) =>
        MeshProtocol.ParseStatus(await _link.SendAsync(MeshProtocol.Status, ct));

    /// <summary>Asks everything in range to introduce itself (about one second) and lists what answered.</summary>
    public async Task<IReadOnlyList<MeshNodeInfo>> ScanAsync(CancellationToken ct = default) =>
        MeshProtocol.ParseScan(await _link.SendAsync(MeshProtocol.Scan, ct));

    /// <summary>Runs one command on a node and returns the node's own answer line (<c>OK:...</c> or <c>ERR:...</c>).</summary>
    public async Task<string> RelayAsync(string mac, string command, CancellationToken ct = default) =>
        MeshProtocol.UnwrapRelay(await _link.SendAsync(MeshProtocol.BuildRelay(mac, command), ct));

    /// <summary>
    /// A transport that sends everything to the node <paramref name="mac"/> through this gateway.
    /// Pass <paramref name="ownedLink"/> (for example the gateway's own transport) to have it closed when the node transport is disposed.
    /// </summary>
    public MeshNodeTransport CreateNode(string mac, IDisposable? ownedLink = null) => new(this, mac, ownedLink);
}

/// <summary>The link to one node of the mesh, through its gateway. It looks like any other transport to the rest of the SDK.</summary>
public sealed class MeshNodeTransport : ITransport
{
    private readonly MeshGateway _gateway;
    private readonly IDisposable? _ownedLink;
    private bool _connected;
    private bool _disposed;

    public MeshNodeTransport(MeshGateway gateway, string mac, IDisposable? ownedLink = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        Mac = MeshProtocol.NormalizeMac(mac);
        _ownedLink = ownedLink;
    }

    public string Mac { get; }

    public bool IsConnected => _connected && !_disposed;

    public event EventHandler<DataReceivedEventArgs>? DataReceived
    {
        add { }
        remove { }
    }

    /// <summary>Checks that the node answers. A node that is off, out of range or on another key fails here with a timeout.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var answer = await _gateway.RelayAsync(Mac, "PING", ct);
        if (!answer.StartsWith("OK", StringComparison.Ordinal))
            throw new IOException($"The node {Mac} answered {answer}.");

        _connected = true;
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _connected = false;
        return Task.CompletedTask;
    }

    public async Task<string> SendCommandAsync(string command, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_connected)
            throw new InvalidOperationException("Not connected to the node.");

        try
        {
            return await _gateway.RelayAsync(Mac, command, ct);
        }
        catch (TimeoutException)
        {
            _connected = false; // the board service reconnects: the node may be back in a moment
            throw;
        }
    }

    public Task SendRawAsync(byte[] data, CancellationToken ct = default) =>
        throw new NotSupportedException("Raw bytes are not relayed over the mesh; use commands.");

    public Task<byte[]> ReceiveRawAsync(int length, CancellationToken ct = default) =>
        throw new NotSupportedException("Raw bytes are not relayed over the mesh; use commands.");

    public void Dispose()
    {
        _disposed = true;
        _connected = false;
        _ownedLink?.Dispose();
    }
}
