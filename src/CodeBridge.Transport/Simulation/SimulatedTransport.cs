using System.Globalization;
using System.Text.Json;
using CodeBridge.Core.Abstractions;
using CodeBridge.Core.Protocol;

namespace CodeBridge.Transport.Simulation;

/// <summary>What changed on the virtual board (an output pin, a PWM duty, a servo angle...).</summary>
public sealed record SimulatedBoardEvent(string Kind, int Pin, double Value);

/// <summary>Tunable behaviour of the virtual board.</summary>
public sealed class SimulatedBoardOptions
{
    /// <summary>Largest value an analog read returns (4095 for an ESP32, 1023 for an Arduino Uno).</summary>
    public int AnalogMax { get; set; } = 4095;

    /// <summary>Chip name reported by INFO.</summary>
    public string ChipName { get; set; } = "ESP32-SIM";

    /// <summary>Highest valid pin number (39 on the ESP32 DevKit, 19 on the Uno).</summary>
    public int MaxPin { get; set; } = 39;

    /// <summary>Delay added to every command, to mimic a real link (0 = instant).</summary>
    public TimeSpan Latency { get; set; } = TimeSpan.Zero;

    /// <summary>Random jitter added to analog readings, in counts (0 = a clean wave).</summary>
    public int AnalogNoise { get; set; } = 12;

    /// <summary>Seed of the noise generator: the same seed gives the same readings.</summary>
    public int Seed { get; set; } = 42;
}

/// <summary>
/// A virtual CodeBridge board. It speaks the same text protocol as the firmware, so <c>ESP32Board</c>, the sensors, the flow runtime
/// and the exported C# run on it unchanged — no hardware, no cable, no risk. Analog inputs follow a slow sine wave unless you
/// pin them to a value; digital inputs, temperature, distance and the other sensors can be scripted for tests and demos.
/// </summary>
public sealed class SimulatedTransport : ITransport
{
    private const int MaxLoggedCommands = 1000;

    private readonly SimulatedBoardOptions _options;
    private readonly object _gate = new();
    private readonly Random _random;
    private readonly DateTime _started = DateTime.UtcNow;
    private readonly Dictionary<int, int> _pinModes = new();
    private readonly Dictionary<int, bool> _outputs = new();
    private readonly Dictionary<int, bool> _digitalInputs = new();
    private readonly Dictionary<int, int> _analogInputs = new();
    private readonly Dictionary<int, int> _pwmDuty = new();
    private readonly Dictionary<int, double> _servoAngles = new();
    private readonly Dictionary<string, SampleChannel> _channels = new();
    private readonly List<string> _log = new();
    private readonly Dictionary<int, ISimulatedI2cDevice> _i2cDevices = new();
    private bool _connected;
    private int _nextChannel;

    public SimulatedTransport(SimulatedBoardOptions? options = null)
    {
        _options = options ?? new SimulatedBoardOptions();
        _random = new Random(_options.Seed);
    }

    public bool IsConnected => _connected;

    public event EventHandler<DataReceivedEventArgs>? DataReceived { add { } remove { } }

    /// <summary>Raised when the program under test changes an output, a PWM duty or a servo angle.</summary>
    public event Action<SimulatedBoardEvent>? BoardChanged;

    // ---------------------------------------------------------------- scripting

    /// <summary>Temperature returned by DHT, BME280 and DS18B20 reads (°C).</summary>
    public double Temperature { get; set; } = 23.5;

    /// <summary>Relative humidity returned by DHT and BME280 reads (%).</summary>
    public double Humidity { get; set; } = 48.0;

    /// <summary>Pressure returned by BME280 reads (hPa).</summary>
    public double Pressure { get; set; } = 1013.2;

    /// <summary>Ambient light returned by BH1750 reads (lux).</summary>
    public double Lux { get; set; } = 320.0;

    /// <summary>Distance returned by ultrasonic reads (cm).</summary>
    public double Distance { get; set; } = 42.0;

    /// <summary>
    /// Puts a virtual chip on the I2C bus at <paramref name="address"/>: scans list it, and reads and writes to that address reach it (so the drivers of the
    /// .NET IoT bindings can be tried without hardware). Addresses with no chip keep the simulator's default answers.
    /// </summary>
    public void AddI2cDevice(int address, ISimulatedI2cDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_gate) _i2cDevices[address] = device;
    }

    /// <summary>Makes a digital input read HIGH or LOW (like pressing a button).</summary>
    public void SetDigitalInput(int pin, bool high)
    {
        lock (_gate) _digitalInputs[pin] = high;
    }

    /// <summary>Pins an analog input to a fixed value instead of the default wave.</summary>
    public void SetAnalogInput(int pin, int value)
    {
        lock (_gate) _analogInputs[pin] = Math.Clamp(value, 0, _options.AnalogMax);
    }

    /// <summary>Goes back to the default wave for an analog input.</summary>
    public void ReleaseAnalogInput(int pin)
    {
        lock (_gate) _analogInputs.Remove(pin);
    }

    /// <summary>The level the program last wrote to an output pin (false when never written).</summary>
    public bool GetOutput(int pin)
    {
        lock (_gate) return _outputs.TryGetValue(pin, out var level) && level;
    }

    /// <summary>The last PWM duty (0-255) written to a pin.</summary>
    public int GetPwmDuty(int pin)
    {
        lock (_gate) return _pwmDuty.TryGetValue(pin, out var duty) ? duty : 0;
    }

    /// <summary>The last angle written to the servo on a pin.</summary>
    public double GetServoAngle(int pin)
    {
        lock (_gate) return _servoAngles.TryGetValue(pin, out var angle) ? angle : 0;
    }

    /// <summary>The most recent commands the board received (at most 1000).</summary>
    public IReadOnlyList<string> CommandLog
    {
        get
        {
            lock (_gate) return _log.ToArray();
        }
    }

    // ---------------------------------------------------------------- ITransport

    public Task ConnectAsync(CancellationToken ct = default)
    {
        _connected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _connected = false;
        return Task.CompletedTask;
    }

    public Task SendRawAsync(byte[] data, CancellationToken ct = default) => Task.CompletedTask;

    public Task<byte[]> ReceiveRawAsync(int length, CancellationToken ct = default) => Task.FromResult(new byte[length]);

    public void Dispose() => _connected = false;

    public async Task<string> SendCommandAsync(string command, CancellationToken ct = default)
    {
        if (!_connected)
            throw new InvalidOperationException("Not connected. Call ConnectAsync() first.");

        if (_options.Latency > TimeSpan.Zero)
            await Task.Delay(_options.Latency, ct);

        ct.ThrowIfCancellationRequested();
        var line = command.Trim();

        lock (_gate)
        {
            if (_log.Count >= MaxLoggedCommands)
                _log.RemoveAt(0);
            _log.Add(line);

            return Execute(line);
        }
    }

    // ---------------------------------------------------------------- the virtual firmware

    private string Execute(string line)
    {
        var colon = line.IndexOf(':');
        var name = colon < 0 ? line : line[..colon];
        var args = colon < 0 ? Array.Empty<string>() : line[(colon + 1)..].Split(':');

        switch (name)
        {
            case "PING": return "OK:PONG";
            case "VER": return "OK:" + BridgeProtocol.EXPECTED_FIRMWARE_VERSION;
            case "INFO": return "OK:" + JsonSerializer.Serialize(new { chip = _options.ChipName, freq = 240, heap = 280000, flash = 4194304, sdk = "simulator", auth = false });
            case "RST": return "OK";
            case "AUTH": case "WTOK": return "OK";

            case "PM":
                if (!TryPin(args, 0, out var pm)) return "ERR:Invalid pin";
                _pinModes[pm] = Arg(args, 1);
                return "OK";

            case "DW":
            {
                if (!TryPin(args, 0, out var pin)) return "ERR:Invalid pin";
                var high = Arg(args, 1) != 0;
                _outputs[pin] = high;
                BoardChanged?.Invoke(new SimulatedBoardEvent("digital", pin, high ? 1 : 0));
                return "OK";
            }

            case "DR":
            {
                if (!TryPin(args, 0, out var pin)) return "ERR:Invalid pin";
                var isOutput = _pinModes.TryGetValue(pin, out var mode) && mode == 1;
                var high = isOutput ? _outputs.GetValueOrDefault(pin) : _digitalInputs.GetValueOrDefault(pin);
                return high ? "OK:1" : "OK:0";
            }

            case "AR":
                if (!TryPin(args, 0, out var ar)) return "ERR:Invalid pin";
                return "OK:" + Invariant(AnalogValue(ar));

            case "PW":
            {
                if (!TryPin(args, 0, out var pin)) return "ERR:Invalid pin";
                var duty = Arg(args, 1);
                if (duty < 0 || duty > 255) return "ERR:Invalid duty";
                _pwmDuty[pin] = duty;
                BoardChanged?.Invoke(new SimulatedBoardEvent("pwm", pin, duty));
                return "OK";
            }

            case "SA": case "SD":
                return TryPin(args, 0, out _) ? "OK" : "ERR:Invalid pin";

            case "SV":
            {
                if (!TryPin(args, 0, out var pin)) return "ERR:Invalid pin";
                var angle = Math.Clamp(Arg(args, 1), 0, 180);
                _servoAngles[pin] = angle;
                BoardChanged?.Invoke(new SimulatedBoardEvent("servo", pin, angle));
                return "OK";
            }

            case "SVR":
                return TryPin(args, 0, out var svr) ? "OK:" + Invariant((int)GetServoAngleUnsafe(svr)) : "ERR:Invalid pin";

            case "SU":
                return TryPin(args, 0, out _) ? "OK" : "ERR:Invalid pin";

            case "IS": return "OK:" + (_i2cDevices.Count > 0 ? string.Join(",", _i2cDevices.Keys.Order()) : "35,60,118");   // default: BH1750, SSD1306, BME280
            case "IW" or "IR" or "IWR" or "IRR" when _i2cDevices.Count > 0 && !_i2cDevices.ContainsKey(Arg(args, 0)):
                return "ERR:I2C_NACK at address " + Arg(args, 0);   // a bus with virtual chips behaves like a real one: nobody answers at an empty address
            case "IW" when _i2cDevices.TryGetValue(Arg(args, 0), out var device):
                device.Write(HexBytes(args.ElementAtOrDefault(1)));
                return "OK";
            case "IWR" when _i2cDevices.TryGetValue(Arg(args, 0), out var device):
                device.Write([(byte)Arg(args, 1), .. HexBytes(args.ElementAtOrDefault(2))]);
                return "OK";
            case "IR" when _i2cDevices.TryGetValue(Arg(args, 0), out var device):
                return "OK:" + Convert.ToHexString(device.Read(Math.Clamp(Arg(args, 1), 0, 128)));
            case "IRR" when _i2cDevices.TryGetValue(Arg(args, 0), out var device):
                device.Write([(byte)Arg(args, 1)]);
                return "OK:" + Convert.ToHexString(device.Read(Math.Clamp(Arg(args, 2), 0, 128)));
            case "IW": case "IWR": return "OK";
            case "IR": return "OK:" + new string('0', Math.Clamp(Arg(args, 1), 0, 128) * 2);
            case "IRR": return "OK:" + new string('0', Math.Clamp(Arg(args, 2), 0, 128) * 2);

            case "DHTI": return "OK";
            case "DHTR": return "OK:" + Format(Temperature + Noise(0.2)) + "," + Format(Humidity + Noise(0.5));
            case "USR": return "OK:" + Format(Math.Max(2, Distance + Noise(0.4)));
            case "BMI": return "OK";
            case "BMR": return "OK:" + Format(Temperature + Noise(0.1)) + "," + Format(Humidity + Noise(0.3)) + "," + Format(Pressure + Noise(0.2));
            case "BLI": return "OK";
            case "BLR": return "OK:" + Format(Math.Max(0, Lux + Noise(3)));
            case "MPI": case "INI": case "TCI": return "OK";
            case "MPR": return "OK:0.01,0.02,9.81,0.1,0.0,-0.1," + Format(Temperature);
            case "INR": return "OK:120.5,5.02,605.0,12.0";
            case "TCR": return "OK:120,90,60,300,4500,250";
            case "OWS": return "OK:28FF000000000001";
            case "OWT": return "OK:" + Format(Temperature + Noise(0.1));
            case "OWR": case "OWW": return "OK";

            case "GINT": case "GINTD": return "OK";
            case "GINTP": return "OK:NONE";

            case "SCFG": return StartChannel(args);
            case "SRD": return ReadChannel(args);
            case "SSTOP": return _channels.Remove(args.ElementAtOrDefault(0) ?? string.Empty) ? "OK" : "ERR:Unknown channel";

            case "TN": case "NT": case "NI": case "NS": case "NA": case "NH": case "NC": case "NB": case "NR":
            case "MI": case "MS": case "MX": case "STI": case "STS":
            case "OI": case "OC": case "OT": case "OP": case "OL": case "OR": case "OE": case "OF": case "OB":
            case "LI": case "LC": case "LT": case "LB": case "LK":
            case "WDI": case "WDF": case "WDD":
            case "ST": case "SW": case "SR": case "SC":
                return "OK";

            case "DSL": case "DSLP": return "OK:SLEEPING";
            case "WSTAT": return "OK:" + JsonSerializer.Serialize(new { enabled = false, connected = false });
            case "WSCAN": return "OK:" + JsonSerializer.Serialize(new { networks = Array.Empty<object>() });
            case "WCFG": case "WCFGX": return "ERR:The simulator has no Wi-Fi";
            case "OTAB": case "OTAS": return "ERR:Not available in the simulator";

            case "MQC": case "MQP": case "MQS": case "MQU": case "MQD": return "OK";
            case "MQR": return "OK:NONE";

            default: return "ERR:Unknown command";
        }
    }

    private double GetServoAngleUnsafe(int pin) => _servoAngles.TryGetValue(pin, out var angle) ? angle : 0;

    private bool TryPin(string[] args, int index, out int pin)
    {
        pin = index < args.Length && int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1;
        return pin >= 0 && pin <= _options.MaxPin;
    }

    private static byte[] HexBytes(string? hex) => string.IsNullOrEmpty(hex) ? [] : Convert.FromHexString(hex);

    private static int Arg(string[] args, int index) =>
        index < args.Length && int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private int AnalogValue(int pin)
    {
        if (_analogInputs.TryGetValue(pin, out var fixedValue))
            return fixedValue;

        // A slow wave (10 s period, a different phase per pin) around mid-scale, plus a little noise.
        var seconds = (DateTime.UtcNow - _started).TotalSeconds;
        var wave = Math.Sin(seconds / 10.0 * 2 * Math.PI + pin * 0.7);
        var value = _options.AnalogMax / 2.0 * (1 + 0.85 * wave) + (_options.AnalogNoise > 0 ? _random.Next(-_options.AnalogNoise, _options.AnalogNoise + 1) : 0);
        return Math.Clamp((int)Math.Round(value), 0, _options.AnalogMax);
    }

    private double Noise(double amplitude) => _options.AnalogNoise > 0 ? (_random.NextDouble() * 2 - 1) * amplitude : 0;

    private static string Format(double value) => value.ToString("0.0#", CultureInfo.InvariantCulture);

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- buffered sampling (SCFG / SRD / SSTOP)

    private sealed class SampleChannel
    {
        public int Pin;
        public bool Analog;
        public int Rate;
        public int Capacity;
        public int Batch;
        public DateTime Started;
        public long Delivered;
    }

    private string StartChannel(string[] args)
    {
        // SCFG:pin:analog:mode:rate:capacity:backpressure:batch
        if (!TryPin(args, 0, out var pin)) return "ERR:Invalid pin";
        var rate = Arg(args, 3);
        var capacity = Arg(args, 4);
        if (rate <= 0 || rate > 10000) return "ERR:Sample rate 1-10000 Hz";
        if (capacity <= 0 || capacity > 4096) return "ERR:Buffer capacity 1-4096";
        if (_channels.Count >= 4) return "ERR:No sample channels available";

        var id = "ch" + _nextChannel++;
        _channels[id] = new SampleChannel { Pin = pin, Analog = Arg(args, 1) != 0, Rate = rate, Capacity = capacity, Batch = Math.Max(1, Arg(args, 6)), Started = DateTime.UtcNow };
        return "OK:" + id;
    }

    private string ReadChannel(string[] args)
    {
        if (!_channels.TryGetValue(args.ElementAtOrDefault(0) ?? string.Empty, out var channel))
            return "ERR:Unknown channel";

        var elapsed = DateTime.UtcNow - channel.Started;
        var produced = (long)(elapsed.TotalSeconds * channel.Rate);
        var pending = produced - channel.Delivered;
        if (pending > channel.Capacity)
        {
            channel.Delivered = produced - channel.Capacity; // the ring buffer overwrote the oldest samples
            pending = channel.Capacity;
        }

        var count = (int)Math.Min(pending, Math.Max(1, Arg(args, 1)));
        if (count <= 0)
            return "OK:NONE";

        var frames = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var sequence = channel.Delivered + i;
            var micros = (long)(sequence * 1_000_000.0 / channel.Rate);
            var value = channel.Analog ? AnalogValue(channel.Pin) : (_digitalInputs.GetValueOrDefault(channel.Pin) ? 1 : 0);
            frames.Add($"{sequence},{micros},{value}");
        }

        channel.Delivered += count;
        return "OK:" + string.Join(';', frames);
    }
}

/// <summary>
/// A virtual I2C chip for <see cref="SimulatedTransport.AddI2cDevice"/>. It sees the bus the way a real chip does: bytes written to it, and reads that
/// return the next bytes it wants to send (a register read is a write of the register number, then a read).
/// </summary>
public interface ISimulatedI2cDevice
{
    /// <summary>The master wrote these bytes (a register number, a command, a register number and its value...).</summary>
    void Write(byte[] data);

    /// <summary>The master reads <paramref name="length"/> bytes.</summary>
    byte[] Read(int length);
}
