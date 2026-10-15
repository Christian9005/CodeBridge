using System.Globalization;
using CodeBridge.Core.Abstractions;
using CodeBridge.ESP32;
using CodeBridge.Flow.Execution;

namespace CodeBridge.Flow;

/// <summary>How a module block uses one of its pins: the validator checks the pin can do it, and flags the same GPIO used two incompatible ways.</summary>
/// <param name="Property">The property that holds the pin number (<c>pin</c>, <c>trigPin</c>, <c>redPin</c>...).</param>
/// <param name="Needs">What the pin must be able to do.</param>
/// <param name="What">Shown in the error: "digital output", "analog read"...</param>
public sealed record ModulePin(string Property, PinCapability Needs, string What, ModulePinRole Role);

public enum ModulePinRole
{
    DigitalInput,
    DigitalOutput,
    AnalogInput,
    Pwm
}

/// <summary>
/// A sensor or module (DHT, ultrasonic, relay, LCD...) described once: its ports and properties, what it does when a flow runs on a board, the C# it
/// becomes when the flow is exported, and which pins it needs. The catalog, the runtime, the exporter and the validator all read this one description.
/// </summary>
public sealed class ModuleBlock
{
    public required string Type { get; init; }
    public required string DisplayName { get; init; }

    /// <summary><c>Sensors</c>, <c>Modules</c> or <c>Math</c> / <c>Logic</c> for the helpers.</summary>
    public required string Category { get; init; }

    public required string Description { get; init; }
    public required IReadOnlyList<FlowPortDefinition> Ports { get; init; }
    public required Func<BoardProfile, IReadOnlyList<FlowPropertyDefinition>> Properties { get; init; }
    public IReadOnlyList<ModulePin> Pins { get; init; } = [];

    /// <summary>The block talks to peripherals through the ESP32 SDK classes (I2C, OneWire, DHT, tone...), so it is offered on the ESP32 family only.</summary>
    public bool Esp32Only { get; init; }

    public required Func<ModuleRun, ValueTask<IReadOnlyDictionary<string, object?>>> Run { get; init; }
    public required Action<ModuleCode> Emit { get; init; }

    internal FlowBlockDefinition ToDefinition(BoardProfile board) => new()
    {
        Type = Type,
        DisplayName = DisplayName,
        Category = Category,
        Description = Description,
        Ports = Ports,
        Properties = Properties(board)
    };
}

/// <summary>What a module block can see and do while a flow runs.</summary>
public sealed class ModuleRun
{
    private readonly FlowNodeExecutionContext _context;

    internal ModuleRun(FlowNodeExecutionContext context) => _context = context;

    public CancellationToken Ct => _context.CancellationToken;

    public IBoard Board => _context.Board ?? throw new InvalidOperationException($"Block '{_context.Node.Type}' requires a connected board.");

    /// <summary>The board as an ESP32 (the SDK peripherals are created from it).</summary>
    public ESP32Board Esp32 => Board as ESP32Board
        ?? throw new InvalidOperationException($"Block '{_context.Node.Type}' needs an ESP32 board (this is {Board.Name}).");

    public int Int(string property) => (int)Math.Round(_context.GetParameter<double>(property));

    public double Double(string property) => _context.GetParameter<double>(property);

    public bool Bool(string property) => _context.GetParameter<bool>(property);

    public string String(string property) => _context.GetParameter<string>(property);

    public bool HasInput(string port) => _context.Inputs.ContainsKey(port);

    /// <summary>The id of the board profile of the flow (<c>esp32-devkit</c>...).</summary>
    public string BoardId => _context.Document.BoardId;

    /// <summary>The wired value as it came, whatever its type.</summary>
    public object? RawInput(string port) => _context.Inputs.TryGetValue(port, out var value) ? value : null;

    public T Input<T>(string port) => _context.GetInput<T>(port);

    /// <summary>The wired input when there is one, the property otherwise.</summary>
    public T InputOr<T>(string port, string property) => _context.GetInputOrParameter<T>(port, property);

    public static IReadOnlyDictionary<string, object?> Output(params (string Name, object? Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);
}

/// <summary>What a module block writes when a flow is exported to C#.</summary>
public sealed class ModuleCode
{
    private readonly Action<string> _line;
    private readonly Action<string, string> _assign;
    private readonly Func<string, string, string> _input;
    private readonly Func<string, bool> _hasInput;
    private readonly Func<string, int> _int;
    private readonly Func<string, double> _double;
    private readonly Func<string, bool> _bool;
    private readonly Func<string, string> _string;

    internal ModuleCode(
        string name,
        string boardId,
        Action<string> line,
        Action<string, string> assign,
        Func<string, string, string> input,
        Func<string, bool> hasInput,
        Func<string, int> integer,
        Func<string, double> number,
        Func<string, bool> boolean,
        Func<string, string> text)
    {
        Name = name;
        BoardId = boardId;
        _line = line;
        _assign = assign;
        _input = input;
        _hasInput = hasInput;
        _int = integer;
        _double = number;
        _bool = boolean;
        _string = text;
    }

    /// <summary>A unique C# identifier prefix for this block (<c>dht1</c>); use it to name local variables.</summary>
    public string Name { get; }

    public string BoardId { get; }

    public void Line(string text) => _line(text);

    /// <summary>Sets an output port. Nothing is written when no block reads it.</summary>
    public void Assign(string port, string expression) => _assign(port, expression);

    public bool HasInput(string port) => _hasInput(port);

    /// <summary>The C# variable of the wired input, or <paramref name="fallback"/> when nothing is wired.</summary>
    public string Input(string port, string fallback) => _input(port, fallback);

    public int Int(string property) => _int(property);

    public double Double(string property) => _double(property);

    public bool Bool(string property) => _bool(property);

    public string String(string property) => _string(property);

    public static string Literal(double value) => value.ToString("0.0###############", CultureInfo.InvariantCulture);

    public static string Literal(bool value) => value ? "true" : "false";

    public static string Literal(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
}

