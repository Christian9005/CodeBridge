using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using CodeBridge.Core.Abstractions;
using CodeBridge.Flow;
using Iot.Device.Ads1115;
using Iot.Device.Mcp9808;
using Iot.Device.Mlx90614;
using Iot.Device.Pwm;
using Iot.Device.Rtc;
using Iot.Device.Sht3x;
using Iot.Device.Vl53L0X;

namespace CodeBridge.IotBindings;

/// <summary>
/// Flow blocks for devices of the .NET IoT bindings (<c>Iot.Device.Bindings</c>), run on a board through <see cref="BridgeI2cDevice"/>. Call
/// <see cref="Register"/> once at start-up; the blocks then appear in the toolbox, run, validate and export like the built-in ones.
/// </summary>
/// <remarks>
/// Adding a device is one <see cref="ModuleBlock"/> of about twenty lines: its ports and properties, how it is read, and the C# it exports to. This proof of
/// concept ships seven; more than a hundred drivers in the package take an <c>I2cDevice</c> and follow the same pattern.
/// </remarks>
public static class IotBindingBlocks
{
    public const string Sht3x = "iot.sht3x";
    public const string Mcp9808 = "iot.mcp9808";
    public const string Mlx90614 = "iot.mlx90614";
    public const string Vl53L0X = "iot.vl53l0x";
    public const string Ads1115 = "iot.ads1115";
    public const string Ds3231 = "iot.ds3231";
    public const string Pca9685 = "iot.pca9685";

    private const string Bridge = "CodeBridge.IotBindings.BridgeI2cDevice";

    private static readonly object RegisterLock = new();
    private static bool _registered;

    /// <summary>The blocks of this package, in toolbox order.</summary>
    public static IReadOnlyList<ModuleBlock> All { get; } = Build();

    /// <summary>Adds the blocks to <see cref="ModuleBlocks"/>. Safe to call more than once.</summary>
    public static void Register()
    {
        lock (RegisterLock)
        {
            if (_registered)
                return;

            foreach (var block in All)
                ModuleBlocks.Register(block);

            _registered = true;
        }
    }

    // ---------------------------------------------------------------- the devices

    private static IReadOnlyList<ModuleBlock> Build() =>
    [
        new ModuleBlock
        {
            Type = Sht3x, DisplayName = "Temp + Humidity (SHT3x)", Category = "Sensors", Esp32Only = true, UsesI2c = true,
            Description = "Reads an SHT30/SHT31/SHT35 over I2C: temperature (°C) and relative humidity (%). More accurate than a DHT22.",
            Ports = [Trigger, Number("temperature"), Number("humidity")],
            Properties = _ => [Address(0x44, ("ADDR to GND", 0x44), ("ADDR to VCC", 0x45))],
            Run = r => Read(r, () =>
            {
                var sensor = Cached(r, "sht3x", r.Int("address"), bus => new Sht3x(bus, Resolution.High));
                return ModuleRun.Output(("temperature", Round(sensor.Temperature.DegreesCelsius)), ("humidity", Round(sensor.Humidity.Percent)));
            }),
            Emit = m =>
            {
                m.Line($"var {m.Name} = new Iot.Device.Sht3x.Sht3x(new {Bridge}(esp32.I2C, {m.Int("address")}), Iot.Device.Sht3x.Resolution.High);");
                m.Assign("temperature", $"Math.Round({m.Name}.Temperature.DegreesCelsius, 2)");
                m.Assign("humidity", $"Math.Round({m.Name}.Humidity.Percent, 2)");
            }
        },

        new ModuleBlock
        {
            Type = Mcp9808, DisplayName = "Precision Temp (MCP9808)", Category = "Sensors", Esp32Only = true, UsesI2c = true,
            Description = "Reads an MCP9808 over I2C: temperature in °C, accurate to half a degree over a wide range.",
            Ports = [Trigger, Number("temperature")],
            Properties = _ => [Address(0x18, ("A0-A2 to GND", 0x18), ("A0 to VCC", 0x19), ("A1 to VCC", 0x1A), ("A0 and A1 to VCC", 0x1B))],
            Run = r => Read(r, () =>
            {
                var sensor = Cached(r, "mcp9808", r.Int("address"), bus => new Mcp9808(bus));
                return ModuleRun.Output(("temperature", Round(sensor.Temperature.DegreesCelsius)));
            }),
            Emit = m =>
            {
                m.Line($"var {m.Name} = new Iot.Device.Mcp9808.Mcp9808(new {Bridge}(esp32.I2C, {m.Int("address")}));");
                m.Assign("temperature", $"Math.Round({m.Name}.Temperature.DegreesCelsius, 2)");
            }
        },

        new ModuleBlock
        {
            Type = Mlx90614, DisplayName = "Infrared Temp (MLX90614)", Category = "Sensors", Esp32Only = true, UsesI2c = true,
            Description = "Measures the temperature of an object without touching it (MLX90614), and of the air around the sensor, in °C.",
            Ports = [Trigger, Number("object"), Number("ambient")],
            Properties = _ => [Address(0x5A, ("Default", 0x5A))],
            Run = r => Read(r, () =>
            {
                var sensor = Cached(r, "mlx90614", r.Int("address"), bus => new Mlx90614(bus));
                return ModuleRun.Output(("object", Round(sensor.ReadObjectTemperature().DegreesCelsius)), ("ambient", Round(sensor.ReadAmbientTemperature().DegreesCelsius)));
            }),
            Emit = m =>
            {
                m.Line($"var {m.Name} = new Iot.Device.Mlx90614.Mlx90614(new {Bridge}(esp32.I2C, {m.Int("address")}));");
                m.Assign("object", $"Math.Round({m.Name}.ReadObjectTemperature().DegreesCelsius, 2)");
                m.Assign("ambient", $"Math.Round({m.Name}.ReadAmbientTemperature().DegreesCelsius, 2)");
            }
        },

        new ModuleBlock
        {
            Type = Vl53L0X, DisplayName = "Laser Distance (VL53L0X)", Category = "Sensors", Esp32Only = true, UsesI2c = true,
            Description = "Measures distance with a VL53L0X time-of-flight laser over I2C, up to about 2 m. Output in centimetres and millimetres.",
            Ports = [Trigger, Number("distance"), Number("millimetres")],
            Properties = _ => [Address(0x29, ("Default", 0x29))],
            Run = r => Read(r, () =>
            {
                // the first read starts the chip (a long exchange): the driver is kept for the next ones
                var sensor = Cached(r, "vl53l0x", r.Int("address"), bus => new Vl53L0X(bus, 500, false));
                double millimetres = sensor.Distance;
                return ModuleRun.Output(("distance", Round(millimetres / 10, 1)), ("millimetres", millimetres));
            }),
            Emit = m =>
            {
                m.Line($"var {m.Name} = new Iot.Device.Vl53L0X.Vl53L0X(new {Bridge}(esp32.I2C, {m.Int("address")}), 500, false);");
                m.Line($"double {m.Name}Mm = {m.Name}.Distance;");
                m.Assign("distance", $"Math.Round({m.Name}Mm / 10, 1)");
                m.Assign("millimetres", $"{m.Name}Mm");
            }
        },

        new ModuleBlock
        {
            Type = Ads1115, DisplayName = "Analog 16-bit (ADS1115)", Category = "Sensors", Esp32Only = true, UsesI2c = true,
            Description = "Reads one of the four inputs of an ADS1115 converter over I2C: a precise voltage (0 to 4.096 V) and the raw 16-bit value. Adds analog inputs that are not tied to Wi-Fi.",
            Ports = [Trigger, Number("voltage"), Number("raw")],
            Properties = _ =>
            [
                Address(0x48, ("ADDR to GND", 0x48), ("ADDR to VCC", 0x49), ("ADDR to SDA", 0x4A), ("ADDR to SCL", 0x4B)),
                new FlowPropertyDefinition("channel", FlowValueKind.Integer, Required: true, DefaultValue: 0,
                    Options: Choices(("A0", 0, "Input A0 against ground."), ("A1", 1, "Input A1 against ground."), ("A2", 2, "Input A2 against ground."), ("A3", 3, "Input A3 against ground.")))
            ],
            Run = r => Read(r, () =>
            {
                var converter = Cached(r, "ads1115", r.Int("address"), bus => new Ads1115(bus, InputMultiplexer.AIN0, MeasuringRange.FS4096, DataRate.SPS128));
                var channel = Channel(r.Int("channel"));
                var raw = converter.ReadRaw(channel);
                return ModuleRun.Output(("voltage", Round(converter.RawToVoltage(raw).Volts, 4)), ("raw", (double)raw));
            }),
            Emit = m =>
            {
                m.Line($"var {m.Name} = new Iot.Device.Ads1115.Ads1115(new {Bridge}(esp32.I2C, {m.Int("address")}), Iot.Device.Ads1115.InputMultiplexer.AIN0, Iot.Device.Ads1115.MeasuringRange.FS4096, Iot.Device.Ads1115.DataRate.SPS128);");
                m.Line($"var {m.Name}Counts = {m.Name}.ReadRaw(Iot.Device.Ads1115.InputMultiplexer.AIN{Math.Clamp(m.Int("channel"), 0, 3)});");
                m.Assign("voltage", $"Math.Round({m.Name}.RawToVoltage({m.Name}Counts).Volts, 4)");
                m.Assign("raw", $"(double){m.Name}Counts");
            }
        },

        new ModuleBlock
        {
            Type = Ds3231, DisplayName = "Clock (DS3231)", Category = "Sensors", Esp32Only = true, UsesI2c = true,
            Description = "Reads a DS3231 real-time clock over I2C: the date and time as text (yyyy-MM-dd HH:mm:ss), and the chip's own temperature. It keeps time with a coin cell when the board is off.",
            Ports = [Trigger, FlowPortDefinition.Output("time", FlowValueKind.String), Number("temperature")],
            Properties = _ => [Address(0x68, ("Default", 0x68))],
            Run = r => Read(r, () =>
            {
                var clock = Cached(r, "ds3231", r.Int("address"), bus => new Ds3231(bus));
                return ModuleRun.Output(("time", clock.DateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)), ("temperature", Round(clock.Temperature.DegreesCelsius)));
            }),
            Emit = m =>
            {
                m.Line($"var {m.Name} = new Iot.Device.Rtc.Ds3231(new {Bridge}(esp32.I2C, {m.Int("address")}));");
                m.Assign("time", $"{m.Name}.DateTime.ToString(\"yyyy-MM-dd HH:mm:ss\", System.Globalization.CultureInfo.InvariantCulture)");
                m.Assign("temperature", $"Math.Round({m.Name}.Temperature.DegreesCelsius, 2)");
            }
        },

        new ModuleBlock
        {
            Type = Pca9685, DisplayName = "PWM Driver (PCA9685)", Category = "Actuators", Esp32Only = true, UsesI2c = true,
            Description = "Sets the duty cycle (0 to 100 %) of one of the 16 PWM channels of a PCA9685 over I2C: servos, LEDs and motors beyond the pins of the board.",
            Ports = [Trigger, FlowPortDefinition.Input("duty", FlowValueKind.Number, required: false), Done],
            Properties = _ =>
            [
                Address(0x40, ("Default", 0x40)),
                new FlowPropertyDefinition("channel", FlowValueKind.Integer, Required: true, DefaultValue: 0),
                new FlowPropertyDefinition("duty", FlowValueKind.Number, DefaultValue: 7.5),
                new FlowPropertyDefinition("frequencyHz", FlowValueKind.Integer, DefaultValue: 50, IsAdvanced: true)
            ],
            Run = r => Read(r, () =>
            {
                var driver = Cached(r, "pca9685", r.Int("address") * 10000 + r.Int("frequencyHz"), bus => new Pca9685(bus, r.Int("frequencyHz"), 0, false));
                var duty = Math.Clamp(r.InputOr<double>("duty", "duty"), 0, 100) / 100.0;
                driver.SetDutyCycle(Math.Clamp(r.Int("channel"), 0, 15), duty);
                return ModuleRun.Output(("done", true));
            }),
            Emit = m =>
            {
                m.Line($"var {m.Name} = new Iot.Device.Pwm.Pca9685(new {Bridge}(esp32.I2C, {m.Int("address")}), {m.Int("frequencyHz")}, 0, false);");
                m.Line($"{m.Name}.SetDutyCycle({Math.Clamp(m.Int("channel"), 0, 15)}, Math.Clamp({m.Input("duty", ModuleCode.Literal(m.Double("duty")))}, 0, 100) / 100.0);");
                m.Assign("done", "true");
            }
        }
    ];

    // ---------------------------------------------------------------- helpers

    private static FlowPortDefinition Trigger => FlowPortDefinition.Input("trigger", FlowValueKind.Trigger, required: false);

    private static FlowPortDefinition Done => FlowPortDefinition.Output("done", FlowValueKind.Trigger);

    private static FlowPortDefinition Number(string name) => FlowPortDefinition.Output(name, FlowValueKind.Number);

    private static IReadOnlyList<FlowPropertyOption> Choices(params (string Label, object Value, string Help)[] items) =>
        items.Select(i => new FlowPropertyOption(i.Label, i.Value, i.Help)).ToArray();

    private static FlowPropertyDefinition Address(int defaultAddress, params (string Label, int Value)[] options) =>
        new("address", FlowValueKind.Integer, Required: true, DefaultValue: defaultAddress,
            Options: options.Select(o => new FlowPropertyOption($"{o.Label} (0x{o.Value:X2})", o.Value, $"I2C address 0x{o.Value:X2}.")).ToArray());

    private static double Round(double value, int decimals = 2) => Math.Round(value, decimals);

    private static InputMultiplexer Channel(int channel) => channel switch
    {
        1 => InputMultiplexer.AIN1,
        2 => InputMultiplexer.AIN2,
        3 => InputMultiplexer.AIN3,
        _ => InputMultiplexer.AIN0
    };

    /// <summary>The driver call is synchronous (it waits for the board): it runs on a pool thread, and any failure reaches the flow as one clear message.</summary>
    private static async ValueTask<IReadOnlyDictionary<string, object?>> Read(ModuleRun run, Func<IReadOnlyDictionary<string, object?>> work)
    {
        if (run.Board.I2C is null)
            throw new InvalidOperationException("This board has no I2C bus.");

        try
        {
            return await Task.Run(work, run.Ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"I2C: {ex.Message} Check the wiring, the power and the address of the chip.", ex);
        }
    }

    private static readonly ConditionalWeakTable<IBoard, ConcurrentDictionary<string, object>> Drivers = new();

    /// <summary>One driver per board, device and address: starting a driver is the slow part, and the chip keeps its state between reads.</summary>
    private static T Cached<T>(ModuleRun run, string kind, int key, Func<BridgeI2cDevice, T> create) where T : class
    {
        var board = run.Board;
        var cache = Drivers.GetOrCreateValue(board);
        var address = kind == "pca9685" ? key / 10000 : key;
        return (T)cache.GetOrAdd($"{kind}@{key}", _ => create(new BridgeI2cDevice(board.I2C, address)));
    }
}
