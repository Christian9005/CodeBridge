using System.Net.Sockets;
using CodeBridge.Core;
using CodeBridge.Core.Abstractions;
using CodeBridge.Core.Enums;
using CodeBridge.ESP32;
using CodeBridge.Transport;
using CodeBridge.Transport.Serial;
using CodeBridge.Transport.Simulation;
using CodeBridge.Transport.Wifi;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeBridge.Hosting;

/// <summary>Where the shared board connection stands.</summary>
public enum BoardConnectionState
{
    Disconnected,
    Connecting,
    Connected,

    /// <summary>The last attempt or command failed; a reconnection is scheduled when <see cref="CodeBridgeOptions.AutoReconnect"/> is on.</summary>
    Faulted
}

/// <summary>
/// One shared, thread-safe connection to a board for the whole application (ASP.NET Core, Blazor, MAUI, worker services).
/// Commands from many requests or components are serialized, a dropped connection is detected and re-established with
/// backoff, and the virtual board can stand in when no hardware is attached.
/// </summary>
public sealed class BoardService : IAsyncDisposable
{
    private readonly CodeBridgeOptions _options;
    private readonly ILogger<BoardService> _logger;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private IBoard? _board;
    private SimulatedTransport? _simulator;
    private Task? _reconnectTask;
    private bool _disposed;

    public BoardService(IOptions<CodeBridgeOptions> options, ILogger<BoardService>? logger = null)
    {
        _options = options.Value;
        _logger = logger ?? NullLogger<BoardService>.Instance;
    }

    /// <summary>Raised (from any thread) whenever the state, the target or the board information changes.</summary>
    public event Action? Changed;

    public BoardConnectionState State { get; private set; } = BoardConnectionState.Disconnected;

    /// <summary>The last connection or command error, cleared on success.</summary>
    public string? LastError { get; private set; }

    /// <summary>What the service connected to: <c>COM3</c>, <c>192.168.1.50</c> or <c>simulator</c>.</summary>
    public string? Target { get; private set; }

    public string? FirmwareVersion { get; private set; }

    public BoardInfo? Info { get; private set; }

    /// <summary>True while talking to the virtual board.</summary>
    public bool IsSimulated => _simulator is not null;

    /// <summary>The virtual board, when simulating: script sensors and read outputs from demos and tests.</summary>
    public SimulatedTransport? Simulator => _simulator;

    public bool IsConnected => State == BoardConnectionState.Connected;

    // ---------------------------------------------------------------- connection

    /// <summary>Connects (or reconnects). Does nothing when already connected.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _connectGate.WaitAsync(ct);
        try
        {
            if (State == BoardConnectionState.Connected && _board?.IsConnected == true)
                return;

            SetState(BoardConnectionState.Connecting, null);
            await DropBoardAsync();

            try
            {
                var (board, target, simulator) = await CreateBoardAsync(ct);
                _board = board;
                _simulator = simulator;
                Target = target;
                FirmwareVersion = board.FirmwareVersion;
                Info = await TryGetInfoAsync(board, ct);

                _logger.LogInformation("CodeBridge connected to {Target} (firmware {Firmware}).", target, board.FirmwareVersion);
                SetState(BoardConnectionState.Connected, null);
            }
            catch (OperationCanceledException)
            {
                SetState(BoardConnectionState.Disconnected, null);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CodeBridge could not connect: {Message}", ex.Message);
                SetState(BoardConnectionState.Faulted, ex.Message);
                StartReconnect();
                throw;
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _connectGate.WaitAsync();
        try
        {
            await DropBoardAsync();
            SetState(BoardConnectionState.Disconnected, null);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    // ---------------------------------------------------------------- using the board

    /// <summary>
    /// Runs <paramref name="action"/> on the board. Calls are serialized; the first call connects when needed.
    /// A transport failure marks the service as faulted and starts the background reconnection.
    /// </summary>
    public async Task<T> UseAsync<T>(Func<IBoard, CancellationToken, Task<T>> action, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(action);

        await _commandGate.WaitAsync(ct);
        try
        {
            if (State != BoardConnectionState.Connected || _board is null)
            {
                if (State == BoardConnectionState.Faulted && _reconnectTask is { IsCompleted: false })
                    throw new InvalidOperationException($"The board is not connected ({LastError ?? "reconnecting"}).");

                await ConnectAsync(ct);
            }

            try
            {
                return await action(_board!, ct);
            }
            catch (Exception ex) when (IsLinkFailure(ex))
            {
                SetState(BoardConnectionState.Faulted, ex.Message);
                StartReconnect();
                throw;
            }
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public Task UseAsync(Func<IBoard, CancellationToken, Task> action, CancellationToken ct = default) =>
        UseAsync<object?>(async (board, token) => { await action(board, token); return null; }, ct);

    /// <summary>
    /// Sends one protocol command (<c>PING</c>, <c>MESH</c>, <c>DW:2:1</c>...) to the board and returns its one-line answer. For tools and for the mesh;
    /// everyday code uses the typed methods or <see cref="UseAsync{T}"/>.
    /// </summary>
    public Task<string> SendRawAsync(string command, CancellationToken ct = default)
    {
        var terminator = CodeBridge.Core.Protocol.BridgeProtocol.TERMINATOR;
        var line = command.EndsWith(terminator, StringComparison.Ordinal) ? command : command + terminator;
        return UseAsync((board, token) => board switch
        {
            CodeBridge.ESP32.ESP32Board esp => esp.Transport.SendCommandAsync(line, token),
            CodeBridge.ESP32.CodeBridgeProtocolBoard protocol => protocol.Transport.SendCommandAsync(line, token),
            _ => throw new NotSupportedException("This board does not expose raw commands.")
        }, ct);
    }

    public Task<int> ReadAnalogAsync(int pin, CancellationToken ct = default) =>
        UseAsync((board, token) => board.Gpio.AnalogReadAsync(pin, token), ct);

    public Task<bool> ReadDigitalAsync(int pin, CancellationToken ct = default) =>
        UseAsync(async (board, token) => await board.Gpio.DigitalReadAsync(pin, token) == PinValue.High, ct);

    /// <summary>Makes the pin an output and writes HIGH or LOW.</summary>
    public Task WriteDigitalAsync(int pin, bool high, CancellationToken ct = default) =>
        UseAsync(async (board, token) =>
        {
            await board.Gpio.SetPinModeAsync(pin, PinMode.Output, token);
            await board.Gpio.DigitalWriteAsync(pin, high ? PinValue.High : PinValue.Low, token);
        }, ct);

    /// <summary>Writes a PWM duty from 0 to 255.</summary>
    public Task WritePwmAsync(int pin, int duty, int frequencyHz = 5000, CancellationToken ct = default) =>
        UseAsync((board, token) => board.Gpio.PwmWriteAsync(pin, Math.Clamp(duty, 0, 255), frequencyHz, token), ct);

    /// <summary>The largest value an analog read returns on this board (4095 on the ESP32, 1023 on the Uno).</summary>
    public int AnalogMax => _options.Board.AnalogMax();

    // ---------------------------------------------------------------- internals

    private async Task<(IBoard Board, string Target, SimulatedTransport? Simulator)> CreateBoardAsync(CancellationToken ct)
    {
        if (_options.TransportFactory is { } factory)
            return (await ConnectBoardAsync(await factory(ct), ct), _options.TargetLabel ?? "custom", null);

        var port = string.IsNullOrWhiteSpace(_options.Port) ? "auto" : _options.Port.Trim();

        if (port.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var found = FindUsbBoard();
            if (found is null)
            {
                if (!_options.FallbackToSimulator)
                    throw new InvalidOperationException("No CodeBridge board was found on USB. Connect it, set CodeBridge:Port, or enable FallbackToSimulator.");

                port = "simulator";
            }
            else
            {
                port = found;
            }
        }

        if (port.Equals("simulator", StringComparison.OrdinalIgnoreCase))
        {
            var transport = new SimulatedTransport(new SimulatedBoardOptions
            {
                AnalogMax = AnalogMax,
                MaxPin = _options.Board.MaxPin(),
                ChipName = _options.Board.ChipName()
            });
            return (await ConnectBoardAsync(transport, ct), "simulator", transport);
        }

        var isSerial = port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || port.StartsWith("/dev/", StringComparison.Ordinal);
        if (isSerial)
            return (await ConnectBoardAsync(new SerialTransport(port, _options.BaudRate), ct), port, null);

        if (_options.Board.IsArduino())
            throw new NotSupportedException("The Arduino boards work over USB only. Set CodeBridge:Port to the COM port.");

        var token = string.IsNullOrWhiteSpace(_options.AccessToken) ? Environment.GetEnvironmentVariable("CODEBRIDGE_TOKEN") : _options.AccessToken;
        return (await ConnectBoardAsync(new WifiTransport(port, _options.TcpPort, token), ct), port, null);
    }

    private async Task<IBoard> ConnectBoardAsync(ITransport transport, CancellationToken ct)
    {
        if (_options.CommandObserver is { } observer)
            transport = new CommandTapTransport(transport, observer);

        IBoard board = _options.Board.IsArduino()
            ? new CodeBridgeProtocolBoard(transport, _options.Board.ToString(), BoardFamily.Arduino, _options.Board.MaxPin())
            : new ESP32Board(transport, _options.Board.MaxPin());

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

    private static string? FindUsbBoard()
    {
        try
        {
            // a recognised board first, then any other USB adapter; the built-in COM1 of a PC is not a board
            return BoardDiscovery.FindBoardCandidates().FirstOrDefault()?.Name;
        }
        catch (Exception)
        {
            return null; // port enumeration is unavailable on this platform
        }
    }

    private static async Task<BoardInfo?> TryGetInfoAsync(IBoard board, CancellationToken ct)
    {
        try
        {
            return await board.GetInfoAsync(ct);
        }
        catch (Exception)
        {
            return null; // optional: the Uno sketch does not implement INFO
        }
    }

    private static bool IsLinkFailure(Exception ex) =>
        ex is TimeoutException or IOException or SocketException or ObjectDisposedException
        || (ex is InvalidOperationException && ex.Message.Contains("Not connected", StringComparison.OrdinalIgnoreCase));

    private void StartReconnect()
    {
        if (!_options.AutoReconnect || _disposed || _reconnectTask is { IsCompleted: false })
            return;

        _reconnectTask = Task.Run(async () =>
        {
            var delay = _options.ReconnectDelay;
            while (!_lifetime.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(delay, _lifetime.Token);
                    await ConnectAsync(_lifetime.Token);
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception)
                {
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxReconnectDelay.Ticks));
                }
            }
        });
    }

    private async Task DropBoardAsync()
    {
        var board = _board;
        _board = null;
        _simulator = null;
        if (board is null)
            return;

        try
        {
            if (board is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else
                board.Dispose();
        }
        catch (Exception)
        {
            // the link is gone already
        }
    }

    private void SetState(BoardConnectionState state, string? error)
    {
        State = state;
        LastError = error;
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A CodeBridge Changed handler failed.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _lifetime.Cancel();
        if (_reconnectTask is not null)
        {
            try { await _reconnectTask; } catch (Exception) { }
        }

        await DropBoardAsync();
        State = BoardConnectionState.Disconnected;
        _lifetime.Dispose();
        _commandGate.Dispose();
        _connectGate.Dispose();
    }
}
