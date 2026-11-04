using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CodeBridge.Core.Abstractions;
using CodeBridge.Core.Protocol;
using CodeBridge.ESP32;
using CodeBridge.Flow;
using CodeBridge.Transport.Serial;
using CodeBridge.Transport.Simulation;
using CodeBridge.Transport.Wifi;

namespace CodeBridge.LiveLink;

/// <summary>
/// One engine connected to one board: it reads the engine's messages, moves the servos 50 times a second through the <see cref="ArmController"/>, and
/// sends the state back about 20 times a second. When the engine goes away the arm stops where it is.
/// </summary>
internal sealed class LinkSession
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly TimeSpan ControlPeriod = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan StatePeriod = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan FeedbackPeriod = TimeSpan.FromMilliseconds(100);

    private readonly WebSocket _socket;
    private readonly IBoard _board;
    private readonly BoardProfile _profile;
    private readonly bool _simulated;
    private readonly ArmController _arm = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Dictionary<int, int> _feedback = new();
    private readonly HashSet<int> _attached = new();

    public LinkSession(WebSocket socket, IBoard board, BoardProfile profile, bool simulated)
    {
        _socket = socket;
        _board = board;
        _profile = profile;
        _simulated = simulated;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await SendAsync(new ServerMessage { Type = "hello", Board = _profile.DisplayName, Firmware = _board.FirmwareVersion, Simulated = _simulated }, stop.Token);

        var control = Task.Run(() => ControlLoopAsync(stop.Token), stop.Token);
        try
        {
            await ReceiveLoopAsync(stop.Token);
        }
        finally
        {
            // the engine is gone (closed, crashed, network lost): nothing moves any more
            lock (_gate)
                _arm.Stop();

            stop.Cancel();
            try { await control; } catch (OperationCanceledException) { }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    return;
                }

                message.Write(buffer, 0, result.Count);
                if (message.Length > 64 * 1024)
                    throw new InvalidOperationException("Message too large.");
            }
            while (!result.EndOfMessage);

            await HandleAsync(Encoding.UTF8.GetString(message.ToArray()), ct);
        }
    }

    private async Task HandleAsync(string text, CancellationToken ct)
    {
        ClientMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<ClientMessage>(text, Json);
        }
        catch (JsonException ex)
        {
            await SendErrorAsync("Not a message: " + ex.Message, ct);
            return;
        }

        switch (message?.Type?.ToLowerInvariant())
        {
            case "configure":
            {
                IReadOnlyList<string> problems;
                lock (_gate)
                    problems = _arm.Configure(message.Joints ?? [], message.FeedbackPins, _profile);

                if (problems.Count > 0)
                    await SendErrorAsync(string.Join(" ", problems), ct);
                break;
            }

            case "target":
            {
                IReadOnlyList<string> unknown;
                lock (_gate)
                    unknown = _arm.SetTargets(message.Joints ?? []);

                if (unknown.Count > 0)
                    await SendErrorAsync($"Unknown joint(s): {string.Join(", ", unknown)}. Send a configure message first.", ct);
                break;
            }

            case "stop":
                lock (_gate)
                    _arm.Stop();
                break;

            case "resume":
                lock (_gate)
                    _arm.Resume();
                break;

            default:
                await SendErrorAsync($"Unknown message type '{message?.Type}'.", ct);
                break;
        }
    }

    private async Task ControlLoopAsync(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed;
        var lastState = TimeSpan.Zero;
        var lastFeedback = TimeSpan.Zero;

        while (!ct.IsCancellationRequested)
        {
            var now = clock.Elapsed;
            IReadOnlyList<(int Pin, int Angle)> writes;
            int[] feedbackPins;
            lock (_gate)
            {
                writes = _arm.Tick(now - last);
                feedbackPins = _arm.FeedbackPins.ToArray();
            }

            last = now;
            try
            {
                foreach (var (pin, angle) in writes)
                    await WriteServoAsync(pin, angle, ct);

                if (feedbackPins.Length > 0 && now - lastFeedback >= FeedbackPeriod)
                {
                    lastFeedback = now;
                    foreach (var pin in feedbackPins)
                        _feedback[pin] = await _board.Gpio.AnalogReadAsync(pin, ct);
                }

                if (now - lastState >= StatePeriod)
                {
                    lastState = now;
                    ServerMessage state;
                    lock (_gate)
                    {
                        state = new ServerMessage
                        {
                            Type = "state",
                            Stopped = _arm.Stopped,
                            Joints = _arm.Snapshot(),
                            Feedback = feedbackPins.Where(_feedback.ContainsKey).Select(p => new PinReading { Pin = p, Value = _feedback[p] }).ToArray()
                        };
                    }

                    await SendAsync(state, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
            {
                // a board that stops answering stops the arm; the engine is told why
                lock (_gate)
                    _arm.Stop();

                await SendErrorAsync("The board did not answer: " + ex.Message, ct);
            }

            var wait = ControlPeriod - (clock.Elapsed - now);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);
        }
    }

    private async Task WriteServoAsync(int pin, int angle, CancellationToken ct)
    {
        if (_attached.Add(pin))
            Check(await _board.Transport.SendCommandAsync(BridgeProtocol.BuildCommand(BridgeProtocol.CMD_SERVO_ATTACH, pin, 500, 2500), ct), "attach");

        Check(await _board.Transport.SendCommandAsync(BridgeProtocol.BuildCommand(BridgeProtocol.CMD_SERVO_WRITE, pin, Math.Clamp(angle, 0, 180)), ct), "write");
    }

    private static void Check(string response, string what)
    {
        var (success, data) = BridgeProtocol.ParseResponse(response);
        if (!success)
            throw new InvalidOperationException($"Servo {what} failed: {data}");
    }

    private Task SendErrorAsync(string message, CancellationToken ct) => SendAsync(new ServerMessage { Type = "error", Message = message }, ct);

    private async Task SendAsync(ServerMessage message, CancellationToken ct)
    {
        if (_socket.State != WebSocketState.Open)
            return;

        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        await _sendLock.WaitAsync(ct);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch (WebSocketException)
        {
            // the engine went away; the receive loop ends the session
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary><c>simulator</c>, an IP address (a paired Wi-Fi board; token in CODEBRIDGE_TOKEN) or a serial port.</summary>
    public static async Task<(IBoard Board, bool Simulated, SimulatedTransport? Simulator)> ConnectAsync(string target, BoardProfile profile, CancellationToken ct)
    {
        ITransport transport;
        SimulatedTransport? simulator = null;
        if (string.Equals(target, "simulator", StringComparison.OrdinalIgnoreCase))
            transport = simulator = new SimulatedTransport(new SimulatedBoardOptions { AnalogMax = profile.AnalogMaxValue, MaxPin = profile.MaxPin });
        else if (IPAddress.TryParse(target, out _))
            transport = new WifiTransport(target, 8080, Environment.GetEnvironmentVariable("CODEBRIDGE_TOKEN"));
        else
            transport = new SerialTransport(target, 115200);

        IBoard board = profile.Family == CodeBridge.Core.Enums.BoardFamily.ESP32
            ? new ESP32Board(transport, profile.MaxPin)
            : new CodeBridgeProtocolBoard(transport, profile.DisplayName, profile.Family, profile.MaxPin);
        await board.ConnectAsync(ct);
        return (board, simulator is not null, simulator);
    }
}
