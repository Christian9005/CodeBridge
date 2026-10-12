using System.ComponentModel;
using CodeBridge.Core.Protocol;
using CodeBridge.Flow;
using CodeBridge.Transport.Discovery;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CodeBridge.Mcp;

/// <summary>Tools to learn a board and to look at or poke its pins. Everything defaults to the simulator.</summary>
[McpServerToolType]
public sealed class BoardTools
{
    private readonly McpSession _session;

    public BoardTools(McpSession session) => _session = session;

    [McpServerTool(Name = "list_boards", ReadOnly = true, Title = "List supported boards")]
    [Description("Lists the boards CodeBridge supports (ESP32 variants, Arduino Uno/Nano/Mega) with their ids. Use an id with get_board_manifest.")]
    public string ListBoards() =>
        McpSession.ToJson(BuiltInBoardProfiles.All.Select(b => new
        {
            id = b.Id,
            name = b.DisplayName,
            family = b.Family.ToString(),
            wifi = b.SupportsWifi,
            maxPin = b.MaxPin
        }));

    [McpServerTool(Name = "get_board_manifest", ReadOnly = true, Title = "Get a board's pins, capabilities and rules")]
    [Description(
        "READ THIS BEFORE choosing any pin. Returns every pin of the board with what it can do (digital, analog, pwm, interrupt, i2c/spi/uart roles), " +
        "its safety ('ok', 'boot-strap', 'input-only', 'reserved'), the bus pins, the electrical limits (logic level, safe current per pin) and the rules that keep a design from " +
        "damaging the board or silently failing. Never invent pin numbers: use this.")]
    public string GetBoardManifest([Description("A board id from list_boards, for example 'esp32-devkit'.")] string boardId) =>
        McpSession.ToJson(BoardManifest.For(Find(boardId)));

    [McpServerTool(Name = "list_ports", ReadOnly = true, OpenWorld = true, Title = "Find boards on USB and on the network")]
    [Description("Lists serial ports and CodeBridge boards announcing themselves on the local network. Only informational: connecting to real hardware is a separate, gated step.")]
    public async Task<string> ListPorts(CancellationToken ct)
    {
        var serial = System.IO.Ports.SerialPort.GetPortNames().OrderBy(n => n.Length).ThenBy(n => n).ToList();
        var network = await WifiBoardFinder.FindAsync(TimeSpan.FromSeconds(1.5), ct);
        return McpSession.ToJson(new
        {
            hardwareAllowed = McpSession.HardwareAllowed,
            serial,
            wifi = network.Select(b => new { b.Name, target = b.Target, b.Firmware, b.Chip, b.RequiresToken }),
            simulator = "simulator"
        });
    }

    [McpServerTool(Name = "connect_board", Destructive = false, Idempotent = true, OpenWorld = true, Title = "Choose the board and where it is")]
    [Description(
        "Selects the board model and connects. The default port 'simulator' is a virtual board: safe, instant, and it reports exactly what the program did to each pin. " +
        "A COM port or IP address means REAL hardware, which only works if the person who started this server enabled it; if you get an error about that, stay on the simulator and tell the user.")]
    public async Task<string> ConnectBoard(
        [Description("Board id from list_boards.")] string boardId,
        [Description("'simulator' (default), a serial port such as COM3, or the board's IP address.")] string port = "simulator",
        CancellationToken ct = default)
    {
        var board = await _session.ConnectAsync(boardId, port, ct);
        return McpSession.ToJson(Status(board));
    }

    [McpServerTool(Name = "board_status", ReadOnly = true, Title = "Connection state and pin activity")]
    [Description("Shows which board is connected, whether it is the simulator, its firmware, and what happened on each pin so far.")]
    public async Task<string> BoardStatus(CancellationToken ct)
    {
        var board = await _session.EnsureBoardAsync(ct);
        return McpSession.ToJson(Status(board));
    }

    [McpServerTool(Name = "get_pin_states", ReadOnly = true, Title = "What the program did to each pin")]
    [Description("The last thing done to every pin that was touched: output level, PWM duty, servo angle, analog or digital reading. This is how you check that a flow really drove the pins you intended.")]
    public async Task<string> GetPinStates(CancellationToken ct)
    {
        await _session.EnsureBoardAsync(ct);
        return McpSession.ToJson(_session.PinStates.Select(p => new { p.Pin, p.Kind, p.Value, ageMs = (long)(DateTime.UtcNow - p.UpdatedUtc).TotalMilliseconds }));
    }

    [McpServerTool(Name = "read_pin", ReadOnly = true, Title = "Read a pin")]
    [Description("Reads one pin. Analog needs a pin with analog-read in the manifest and returns 0 to analogMax; digital returns 0 or 1.")]
    public async Task<string> ReadPin(
        [Description("Pin number from the manifest.")] int pin,
        [Description("'digital' or 'analog'.")] string mode = "digital",
        CancellationToken ct = default)
    {
        var board = await _session.EnsureBoardAsync(ct);
        var manifest = ManifestOf(_session.Profile!);
        var info = PinOf(manifest, pin);

        if (mode.Equals("analog", StringComparison.OrdinalIgnoreCase))
        {
            Require(info, "analog-read", $"Pin {pin} cannot read analog values. Analog-capable pins: {string.Join(", ", manifest.Pins.Where(p => p.Capabilities.Contains("analog-read") && p.Safety != "reserved").Select(p => p.Number))}.");
            return McpSession.ToJson(new { pin, mode = "analog", value = await board.ReadAnalogAsync(pin, ct), max = manifest.AnalogMax });
        }

        Require(info, "digital-read", $"Pin {pin} cannot be read as a digital input.");
        return McpSession.ToJson(new { pin, mode = "digital", value = await board.ReadDigitalAsync(pin, ct) ? 1 : 0 });
    }

    [McpServerTool(Name = "set_pin", Destructive = true, Title = "Drive a pin")]
    [Description(
        "Drives one pin: kind 'digital' (value 0 or 1), 'pwm' (value 0 to 255) or 'servo' (angle 0 to 180). Pins that are reserved or input-only are refused. " +
        "On real hardware this moves things in the physical world: explain what will happen to the user, get their approval, then pass confirmed=true.")]
    public async Task<string> SetPin(
        [Description("Pin number from the manifest.")] int pin,
        [Description("'digital', 'pwm' or 'servo'.")] string kind,
        [Description("0/1 for digital, 0-255 for pwm, 0-180 degrees for servo.")] double value,
        [Description("Only needed on real hardware: true once the user approved this exact action.")] bool confirmed = false,
        CancellationToken ct = default)
    {
        var board = await _session.EnsureBoardAsync(ct);
        _session.RequireConsentForHardware(confirmed, $"Setting pin {pin} ({kind} {value})");

        var manifest = ManifestOf(_session.Profile!);
        var info = PinOf(manifest, pin);
        if (info.Safety == "reserved")
            throw new McpException($"Pin {pin} is reserved ({info.Description}). Choose a pin whose safety is 'ok'.");
        if (info.Safety == "input-only")
            throw new McpException($"Pin {pin} is input only and cannot be driven.");

        var warnings = new List<string>();
        if (info.Safety == "boot-strap")
            warnings.Add($"Pin {pin} is a boot-strap pin: whatever is wired to it can change how the board starts.");

        switch (kind.ToLowerInvariant())
        {
            case "digital":
                Require(info, "digital-write", $"Pin {pin} cannot be a digital output.");
                if (value is not (0 or 1))
                    throw new McpException("A digital value is 0 or 1.");
                await board.WriteDigitalAsync(pin, value == 1, ct);
                break;

            case "pwm":
                Require(info, "pwm", $"Pin {pin} has no PWM. PWM pins: {string.Join(", ", manifest.Pins.Where(p => p.Capabilities.Contains("pwm") && p.Safety == "ok").Select(p => p.Number))}.");
                if (value is < 0 or > 255)
                    throw new McpException("A PWM duty is 0 to 255.");
                await board.WritePwmAsync(pin, (int)Math.Round(value), ct: ct);
                break;

            case "servo":
                Require(info, "pwm", $"Pin {pin} has no PWM, so it cannot drive a servo.");
                if (value is < 0 or > 180)
                    throw new McpException("A servo angle is 0 to 180 degrees.");
                await board.UseAsync(async (b, token) =>
                {
                    foreach (var command in new[] { BridgeProtocol.BuildCommand(BridgeProtocol.CMD_SERVO_ATTACH, pin), BridgeProtocol.BuildCommand(BridgeProtocol.CMD_SERVO_WRITE, pin, (int)Math.Round(value)) })
                    {
                        var (ok, data) = BridgeProtocol.ParseResponse(await b.Transport.SendCommandAsync(command, token));
                        if (!ok)
                            throw new McpException($"The board refused the servo command: {data}");
                    }
                }, ct);
                break;

            default:
                throw new McpException("kind must be 'digital', 'pwm' or 'servo'.");
        }

        return McpSession.ToJson(new { ok = true, pin, kind, value, simulated = _session.IsSimulated, warnings });
    }

    [McpServerTool(Name = "simulate_input", Destructive = false, Idempotent = true, Title = "Set what a sensor or button reads (simulator)")]
    [Description("Simulator only: makes an input pin read a chosen value, so you can test how a flow reacts (a dark room, a pressed button, a hot day). kind 'analog' (0 to analogMax), 'digital' (0/1) or 'release' (back to the default wave).")]
    public async Task<string> SimulateInput(
        [Description("Pin number from the manifest.")] int pin,
        [Description("'analog', 'digital' or 'release'.")] string kind,
        [Description("The value to read (ignored for 'release').")] double value = 0,
        CancellationToken ct = default)
    {
        var board = await _session.EnsureBoardAsync(ct);
        var simulator = board.Simulator ?? throw new McpException("simulate_input only works on the simulator. Real sensors report what they really measure.");
        var manifest = ManifestOf(_session.Profile!);
        PinOf(manifest, pin);

        switch (kind.ToLowerInvariant())
        {
            case "analog":
                simulator.SetAnalogInput(pin, (int)Math.Clamp(value, 0, manifest.AnalogMax));
                break;
            case "digital":
                simulator.SetDigitalInput(pin, value != 0);
                break;
            case "release":
                simulator.ReleaseAnalogInput(pin);
                break;
            default:
                throw new McpException("kind must be 'analog', 'digital' or 'release'.");
        }

        return McpSession.ToJson(new { ok = true, pin, kind, value });
    }

    // ------------------------------------------------------------------ helpers

    private object Status(Hosting.BoardService board) => new
    {
        connected = board.IsConnected,
        simulated = board.IsSimulated,
        board = _session.Profile?.Id,
        target = board.Target,
        firmware = board.FirmwareVersion,
        chip = board.Info?.ChipModel,
        hardwareAllowed = McpSession.HardwareAllowed,
        pins = _session.PinStates.Select(p => new { p.Pin, p.Kind, p.Value })
    };

    private static BoardProfile Find(string boardId) =>
        BuiltInBoardProfiles.FindById(boardId) ?? throw new McpException($"Unknown board '{boardId}'. Call list_boards.");

    private static BoardManifest ManifestOf(BoardProfile profile) => BoardManifest.For(profile);

    private static PinManifest PinOf(BoardManifest manifest, int pin) =>
        manifest.Pins.FirstOrDefault(p => p.Number == pin)
        ?? throw new McpException($"{manifest.DisplayName} has no usable pin {pin}. Valid pins: {string.Join(", ", manifest.Pins.Where(p => p.Safety != "reserved").Select(p => p.Number))}.");

    private static void Require(PinManifest pin, string capability, string message)
    {
        if (!pin.Capabilities.Contains(capability))
            throw new McpException(message);
    }
}
