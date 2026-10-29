using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using CodeBridge.Core.Abstractions;
using CodeBridge.ESP32;
using CodeBridge.Flow;
using CodeBridge.Flow.Execution;
using CodeBridge.Transport.Serial;
using CodeBridge.Transport.Simulation;
using CodeBridge.Transport.Wifi;

namespace CodeBridge.WebEditor;

/// <summary>Runs the flow on the board with a <see cref="FlowSupervisor"/> (so it survives a lost board) and tells every open page what is happening, live.</summary>
internal sealed class RunHub
{
    private readonly EditorSession _session;
    private readonly string _target;
    private readonly ConcurrentDictionary<Guid, Channel<string>> _listeners = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _run;
    private Task? _task;

    public RunHub(EditorSession session, string target)
    {
        _session = session;
        _target = target;
    }

    public string Target => _target;

    public bool Running
    {
        get
        {
            lock (_gate)
                return _task is { IsCompleted: false };
        }
    }

    public (Guid Id, ChannelReader<string> Reader) Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });
        _listeners[id] = channel;
        channel.Writer.TryWrite(EditorSession.Serialize(new { type = "state", running = Running, target = _target }));
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_listeners.TryRemove(id, out var channel))
            channel.Writer.TryComplete();
    }

    /// <summary>Starts the current flow. Returns false when it is already running.</summary>
    public bool Start(out string message)
    {
        lock (_gate)
        {
            if (_task is { IsCompleted: false })
            {
                message = "The flow is already running.";
                return false;
            }

            var document = _session.Document;
            var profile = _session.Board;
            var catalog = BuiltInBlockCatalog.Create(profile);
            var validation = new CodeBridge.Flow.Validation.FlowValidator().Validate(document, catalog);
            if (!validation.IsValid)
            {
                message = "The flow has errors: " + string.Join("; ", validation.Errors.Select(e => e.Message));
                return false;
            }

            _run = new CancellationTokenSource();
            var supervisor = new FlowSupervisor(
                document,
                catalog,
                ct => ConnectAsync(profile, ct),
                board => board is IAsyncDisposable disposable ? disposable.DisposeAsync().AsTask() : Task.Run(board.Dispose),
                new FlowSupervisorOptions
                {
                    Interval = TimeSpan.FromMilliseconds(1000),
                    TraceDelay = TimeSpan.FromMilliseconds(120),
                    NodeEvents = (e, _) =>
                    {
                        Publish(new { type = "node", kind = e.Kind.ToString().ToLowerInvariant(), nodeId = e.NodeId, blockType = e.BlockType, message = e.Message, outputs = e.Outputs?.ToDictionary(p => p.Key, p => p.Value?.ToString()) });
                        return ValueTask.CompletedTask;
                    }
                },
                e => Publish(new { type = "supervisor", kind = e.Kind.ToString().ToLowerInvariant(), message = e.Message, attempt = e.Attempt, pass = e.Iteration, waitSeconds = e.Delay.TotalSeconds }));

            var token = _run.Token;
            _task = Task.Run(async () =>
            {
                Publish(new { type = "state", running = true, target = _target });
                try
                {
                    await supervisor.RunAsync(token);
                }
                finally
                {
                    Publish(new { type = "state", running = false, target = _target });
                }
            });

            message = "Started.";
            return true;
        }
    }

    public async Task StopAsync()
    {
        Task? task;
        lock (_gate)
        {
            _run?.Cancel();
            task = _task;
        }

        if (task is not null)
            await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
    }

    private void Publish(object value)
    {
        var line = EditorSession.Serialize(value);
        foreach (var listener in _listeners.Values)
            listener.Writer.TryWrite(line);
    }

    /// <summary><c>simulator</c>, an IP address (a paired Wi-Fi board; the token is read from CODEBRIDGE_TOKEN) or a serial port name (COM3, /dev/ttyUSB0).</summary>
    private async Task<IBoard> ConnectAsync(BoardProfile profile, CancellationToken ct)
    {
        ITransport transport;
        if (string.Equals(_target, "simulator", StringComparison.OrdinalIgnoreCase))
            transport = new SimulatedTransport(new SimulatedBoardOptions { AnalogMax = profile.AnalogMaxValue, MaxPin = profile.MaxPin });
        else if (IPAddress.TryParse(_target, out _))
            transport = new WifiTransport(_target, 8080, Environment.GetEnvironmentVariable("CODEBRIDGE_TOKEN"));
        else
            transport = new SerialTransport(_target, 115200);

        IBoard board = profile.Family == CodeBridge.Core.Enums.BoardFamily.ESP32
            ? new ESP32Board(transport, profile.MaxPin)
            : new CodeBridgeProtocolBoard(transport, profile.DisplayName, profile.Family, profile.MaxPin);

        try
        {
            await board.ConnectAsync(ct);
            return board;
        }
        catch
        {
            board.Dispose();
            throw;
        }
    }
}
