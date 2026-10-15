using System.Globalization;
using CodeBridge.Core.Enums;
using CodeBridge.ESP32;

namespace CodeBridge.Flow;

/// <summary>
/// The sensors, modules and helpers beyond plain GPIO: temperature and humidity, distance, light, motion, motion sensors, current, colour, relays,
/// buzzers, LEDs, displays and motors. See <see cref="ModuleBlock"/>: every block is described once here.
/// </summary>
public static class ModuleBlocks
{
    // sensors
    public const string Dht = "sensor.dht";
    public const string Ultrasonic = "sensor.ultrasonic";
    public const string Ds18b20 = "sensor.ds18b20";
    public const string Bme280 = "sensor.bme280";
    public const string Bh1750 = "sensor.bh1750";
    public const string Mpu6050 = "sensor.mpu6050";
    public const string Ina219 = "sensor.ina219";
    public const string ColorSensor = "sensor.color";
    public const string Pir = "sensor.pir";
    public const string Button = "sensor.button";
    public const string AnalogPercent = "sensor.analog-percent";

    // modules
    public const string Relay = "module.relay";
    public const string Buzzer = "module.buzzer";
    public const string RgbLed = "module.rgb-led";
    public const string NeoPixel = "module.neopixel";
    public const string Lcd = "module.lcd";
    public const string Oled = "module.oled";
    public const string DcMotor = "module.dc-motor";
    public const string Stepper = "module.stepper";

    // helpers
    public const string Threshold = "logic.threshold";
    public const string Gate = "logic.gate";
    public const string Calc = "math.calc";

    private static readonly Dictionary<string, ModuleBlock> ByType = new(StringComparer.OrdinalIgnoreCase);

    static ModuleBlocks()
    {
        foreach (var block in Build())
            ByType[block.Type] = block;
    }

    public static IReadOnlyCollection<ModuleBlock> All => ByType.Values;

    public static bool TryGet(string type, out ModuleBlock block) => ByType.TryGetValue(type, out block!);

    /// <summary>The blocks offered on <paramref name="board"/>: the ones that only need GPIO everywhere, the rest on the ESP32 family.</summary>
    public static IEnumerable<ModuleBlock> For(BoardProfile board) =>
        ByType.Values.Where(block => !block.Esp32Only || board.Family == BoardFamily.ESP32);

    // ---------------------------------------------------------------- helpers for the specs

    private static FlowPortDefinition Trigger => FlowPortDefinition.Input("trigger", FlowValueKind.Trigger, required: false);

    private static FlowPortDefinition Done => FlowPortDefinition.Output("done", FlowValueKind.Trigger);

    private static FlowPropertyDefinition PinProperty(string name, IReadOnlyList<FlowPropertyOption> options, params int[] preferred) =>
        new(name, FlowValueKind.Integer, Required: true, DefaultValue: Pick(options, preferred), Options: options);

    /// <summary>
    /// Chooses a different default pin for each wire of one block: the first of its preferred pins the board offers, never one already taken,
    /// so a block starts valid on every board (a stepper must not get the same GPIO for two coils).
    /// </summary>
    private static int[] Distinct(params (IReadOnlyList<FlowPropertyOption> Options, int[] Preferred)[] wires)
    {
        var chosen = new List<int>();
        foreach (var (options, preferred) in wires)
        {
            var available = options.Select(o => Convert.ToInt32(o.Value, CultureInfo.InvariantCulture)).Where(pin => !chosen.Contains(pin)).ToList();
            chosen.Add(preferred.FirstOrDefault(available.Contains, available.FirstOrDefault()));
        }

        return chosen.ToArray();
    }

    private static FlowPropertyDefinition PinProperty(string name, IReadOnlyList<FlowPropertyOption> options, int defaultPin) =>
        new(name, FlowValueKind.Integer, Required: true, DefaultValue: defaultPin, Options: options);

    private static int Pick(IReadOnlyList<FlowPropertyOption> options, params int[] preferred)
    {
        var available = options.Select(o => Convert.ToInt32(o.Value, CultureInfo.InvariantCulture)).ToList();
        foreach (var pin in preferred)
        {
            if (available.Contains(pin))
                return pin;
        }

        return available.FirstOrDefault();
    }

    private static IReadOnlyList<FlowPropertyOption> Choices(params (string Label, object Value, string Help)[] items) =>
        items.Select(i => new FlowPropertyOption(i.Label, i.Value, i.Help)).ToArray();

    private static readonly IReadOnlyList<FlowPropertyOption> YesNo = Choices(("Yes", true, "On."), ("No", false, "Off."));

    private static string Hex(int value) => "0x" + value.ToString("X2", CultureInfo.InvariantCulture);

    private static IReadOnlyList<FlowPropertyOption> Addresses(params (string Label, int Value)[] items) =>
        items.Select(i => new FlowPropertyOption($"{i.Label} ({Hex(i.Value)})", i.Value, $"I2C address {Hex(i.Value)}.")).ToArray();

    private static IReadOnlyDictionary<string, object?> Out(params (string Name, object? Value)[] values) => ModuleRun.Output(values);

    private static double Round(double value, int decimals = 2) => Math.Round(value, decimals);

    // ---------------------------------------------------------------- the blocks

    private static IEnumerable<ModuleBlock> Build()
    {
        // ------------------------------------------------------------ sensors
        yield return new ModuleBlock
        {
            Type = Dht, DisplayName = "DHT Temp + Humidity", Category = "Sensors", Esp32Only = true,
            Description = "Reads a DHT11 or DHT22 sensor: temperature in °C and relative humidity in %.",
            Ports = [Trigger, FlowPortDefinition.Output("temperature", FlowValueKind.Number), FlowPortDefinition.Output("humidity", FlowValueKind.Number)],
            Properties = b => [PinProperty("pin", b.DigitalReadPinOptions, 4, 5, 2, 1), new FlowPropertyDefinition("model", FlowValueKind.Integer, DefaultValue: 22, Options: Choices(("DHT22 (AM2302)", 22, "The white, bigger one."), ("DHT11", 11, "The blue, cheaper one.")))],
            Pins = [new("pin", PinCapability.DigitalRead | PinCapability.DigitalWrite, "DHT data", ModulePinRole.DigitalInput)],
            Run = RunDht,
            Emit = m =>
            {
                m.Line($"var {m.Name} = esp32.CreateDhtSensor({m.Int("pin")}, {m.Int("model")});");
                m.Line($"await {m.Name}.InitAsync(ct);");
                m.Line($"var {m.Name}Reading = await {m.Name}.ReadBothAsync(ct);");
                m.Assign("temperature", $"{m.Name}Reading.TemperatureC");
                m.Assign("humidity", $"{m.Name}Reading.HumidityPercent");
            }
        };

        yield return new ModuleBlock
        {
            Type = Ultrasonic, DisplayName = "Distance (HC-SR04)", Category = "Sensors", Esp32Only = true,
            Description = "Measures the distance to an object with an HC-SR04 ultrasonic sensor, in centimetres.",
            Ports = [Trigger, FlowPortDefinition.Output("distance", FlowValueKind.Number)],
            Properties = b =>
            {
                var pins = Distinct((b.DigitalWritePinOptions, [5, 4, 2]), (b.DigitalReadPinOptions, [18, 19, 3]));
                return [PinProperty("trigPin", b.DigitalWritePinOptions, pins[0]), PinProperty("echoPin", b.DigitalReadPinOptions, pins[1])];
            },
            Pins = [new("trigPin", PinCapability.DigitalWrite, "ultrasonic trigger", ModulePinRole.DigitalOutput), new("echoPin", PinCapability.DigitalRead, "ultrasonic echo", ModulePinRole.DigitalInput)],
            Run = async r =>
            {
                var sensor = r.Esp32.CreateUltrasonicSensor(r.Int("trigPin"), r.Int("echoPin"));
                await sensor.InitAsync(r.Ct);
                return Out(("distance", Round(await sensor.ReadDistanceCmAsync(r.Ct))));
            },
            Emit = m =>
            {
                m.Line($"var {m.Name} = esp32.CreateUltrasonicSensor({m.Int("trigPin")}, {m.Int("echoPin")});");
                m.Line($"await {m.Name}.InitAsync(ct);");
                m.Assign("distance", $"Math.Round(await {m.Name}.ReadDistanceCmAsync(ct), 2)");
            }
        };

        yield return new ModuleBlock
        {
            Type = Ds18b20, DisplayName = "Temperature (DS18B20)", Category = "Sensors", Esp32Only = true,
            Description = "Reads a DS18B20 waterproof temperature probe, in °C.",
            Ports = [Trigger, FlowPortDefinition.Output("temperature", FlowValueKind.Number)],
            Properties = b => [PinProperty("pin", b.DigitalReadPinOptions, 4, 5, 2, 1)],
            Pins = [new("pin", PinCapability.DigitalRead | PinCapability.DigitalWrite, "OneWire data", ModulePinRole.DigitalInput)],
            Run = async r =>
            {
                var sensor = r.Esp32.CreateDs18b20(r.Int("pin"));
                return Out(("temperature", Round(await sensor.ReadTemperatureCelsiusAsync(r.Ct))));
            },
            Emit = m =>
            {
                m.Line($"var {m.Name} = esp32.CreateDs18b20({m.Int("pin")});");
                m.Assign("temperature", $"Math.Round(await {m.Name}.ReadTemperatureCelsiusAsync(ct), 2)");
            }
        };

        yield return new ModuleBlock
        {
            Type = Bme280, DisplayName = "Weather (BME280)", Category = "Sensors", Esp32Only = true,
            Description = "Reads a BME280 over I2C: temperature (°C), humidity (%) and pressure (hPa).",
            Ports = [Trigger, FlowPortDefinition.Output("temperature", FlowValueKind.Number), FlowPortDefinition.Output("humidity", FlowValueKind.Number), FlowPortDefinition.Output("pressure", FlowValueKind.Number)],
            Properties = _ => [new FlowPropertyDefinition("address", FlowValueKind.Integer, DefaultValue: 0x76, Options: Addresses(("Default", 0x76), ("SDO to VCC", 0x77)))],
            Run = async r =>
            {
                var sensor = r.Esp32.CreateBme280(r.Int("address"));
                await sensor.InitAsync(r.Ct);
                var reading = await sensor.ReadAllAsync(r.Ct);
                return Out(("temperature", Round(reading.TemperatureCelsius)), ("humidity", Round(reading.HumidityPercent ?? 0)), ("pressure", Round(reading.PressureHpa ?? 0)));
            },
            Emit = m =>
            {
                m.Line($"var {m.Name} = esp32.CreateBme280({m.Int("address")});");
                m.Line($"await {m.Name}.InitAsync(ct);");
                m.Line($"var {m.Name}Reading = await {m.Name}.ReadAllAsync(ct);");
                m.Assign("temperature", $"Math.Round({m.Name}Reading.TemperatureCelsius, 2)");
                m.Assign("humidity", $"Math.Round({m.Name}Reading.HumidityPercent ?? 0, 2)");
                m.Assign("pressure", $"Math.Round({m.Name}Reading.PressureHpa ?? 0, 2)");
            }
        };

        yield return new ModuleBlock
        {
            Type = Bh1750, DisplayName = "Light (BH1750)", Category = "Sensors", Esp32Only = true,
            Description = "Reads the ambient light with a BH1750 over I2C, in lux.",
            Ports = [Trigger, FlowPortDefinition.Output("lux", FlowValueKind.Number)],
            Properties = _ => [new FlowPropertyDefinition("address", FlowValueKind.Integer, DefaultValue: 0x23, Options: Addresses(("Default", 0x23), ("ADDR to VCC", 0x5C)))],
            Run = async r =>
            {
                var sensor = r.Esp32.CreateBh1750(r.Int("address"));
                await sensor.InitAsync(r.Ct);
                return Out(("lux", Round(await sensor.ReadLuxAsync(r.Ct), 1)));
            },
            Emit = m =>
            {
                m.Line($"var {m.Name} = esp32.CreateBh1750({m.Int("address")});");
                m.Line($"await {m.Name}.InitAsync(ct);");
                m.Assign("lux", $"Math.Round(await {m.Name}.ReadLuxAsync(ct), 1)");
            }
        };

        yield return new ModuleBlock
        {
            Type = Mpu6050, DisplayName = "Motion/Tilt (MPU6050)", Category = "Sensors", Esp32Only = true,
            Description = "Reads a MPU6050 over I2C: acceleration in m/s² and rotation speed in rad/s on three axes.",
            Ports =
            [
                Trigger,
                FlowPortDefinition.Output("ax", FlowValueKind.Number), FlowPortDefinition.Output("ay", FlowValueKind.Number), FlowPortDefinition.Output("az", FlowValueKind.Number),
                FlowPortDefinition.Output("gx", FlowValueKind.Number), FlowPortDefinition.Output("gy", FlowValueKind.Number), FlowPortDefinition.Output("gz", FlowValueKind.Number)
            ],
            Properties = _ => [new FlowPropertyDefinition("address", FlowValueKind.Integer, DefaultValue: 0x68, Options: Addresses(("Default", 0x68), ("AD0 to VCC", 0x69)))],
            Run = async r =>
            {
                var sensor = r.Esp32.CreateMpu6050(r.Int("address"));
                await sensor.InitAsync(r.Ct);
                var imu = await sensor.ReadAsync(r.Ct);
                return Out(("ax", Round(imu.Acceleration.X)), ("ay", Round(imu.Acceleration.Y)), ("az", Round(imu.Acceleration.Z)),
                    ("gx", Round(imu.Gyroscope.X)), ("gy", Round(imu.Gyroscope.Y)), ("gz", Round(imu.Gyroscope.Z)));
            },
            Emit = m =>
            {
                m.Line($"var {m.Name} = esp32.CreateMpu6050({m.Int("address")});");
                m.Line($"await {m.Name}.InitAsync(ct);");
                m.Line($"var {m.Name}Reading = await {m.Name}.ReadAsync(ct);");
                m.Assign("ax", $"Math.Round({m.Name}Reading.Acceleration.X, 2)");
                m.Assign("ay", $"Math.Round({m.Name}Reading.Acceleration.Y, 2)");
                m.Assign("az", $"Math.Round({m.Name}Reading.Acceleration.Z, 2)");
                m.Assign("gx", $"Math.Round({m.Name}Reading.Gyroscope.X, 2)");
                m.Assign("gy", $"Math.Round({m.Name}Reading.Gyroscope.Y, 2)");
                m.Assign("gz", $"Math.Round({m.Name}Reading.Gyroscope.Z, 2)");
            }
        };

        yield return new ModuleBlock
        {
            Type = Ina219, DisplayName = "Power (INA219)", Category = "Sensors", Esp32Only = true,
            Description = "Measures voltage (V), current (mA) and power (mW) with an INA219 over I2C.",
            Ports = [Trigger, FlowPortDefinition.Output("voltage", FlowValueKind.Number), FlowPortDefinition.Output("current", FlowValueKind.Number), FlowPortDefinition.Output("power", FlowValueKind.Number)],
            Properties = _ => [new FlowPropertyDefinition("address", FlowValueKind.Integer, DefaultValue: 0x40, Options: Addresses(("Default", 0x40), ("A0 to VCC", 0x41), ("A1 to VCC", 0x44), ("A0 and A1 to VCC", 0x45)))],
            Run = async r =>
            {
                var sensor = r.Esp32.CreateCurrentSensor(r.Int("address"));
                await sensor.InitAsync(r.Ct);
                var power = await sensor.ReadPowerReadingAsync(r.Ct);
                return Out(("voltage", Round(power.VoltageV)), ("current", Round(power.CurrentMa, 1)), ("power", Round(power.PowerMw, 1)));
            },
            Emit = m =>
            {
                m.Line($"var {m.Name} = esp32.CreateCurrentSensor({m.Int("address")});");
                m.Line($"await {m.Name}.InitAsync(ct);");
                m.Line($"var {m.Name}Reading = await {m.Name}.ReadPowerReadingAsync(ct);");
                m.Assign("voltage", $"Math.Round({m.Name}Reading.VoltageV, 2)");
                m.Assign("current", $"Math.Round({m.Name}Reading.CurrentMa, 1)");
                m.Assign("power", $"Math.Round({m.Name}Reading.PowerMw, 1)");
            }
        };

        yield return new ModuleBlock
        {
            Type = ColorSensor, DisplayName = "Colour (TCS34725)", Category = "Sensors", Esp32Only = true,
            Description = "Reads the colour of what is in front of a TCS34725 over I2C: red, green and blue from 0 to 255.",
            Ports = [Trigger, FlowPortDefinition.Output("red", FlowValueKind.Integer), FlowPortDefinition.Output("green", FlowValueKind.Integer), FlowPortDefinition.Output("blue", FlowValueKind.Integer)],
            Properties = _ => [new FlowPropertyDefinition("integrationMs", FlowValueKind.Integer, DefaultValue: 50, IsAdvanced: true)],
            Run = async r =>
            {
                var sensor = r.Esp32.CreateColorSensor(r.Int("integrationMs"));
                await sensor.InitAsync(r.Ct);
                var color = await sensor.ReadColorAsync(r.Ct);
                return Out(("red", (int)color.R), ("green", (int)color.G), ("blue", (int)color.B));
            },
            Emit = m =>
            {
                m.Line($"var {m.Name} = esp32.CreateColorSensor({m.Int("integrationMs")});");
                m.Line($"await {m.Name}.InitAsync(ct);");
                m.Line($"var {m.Name}Color = await {m.Name}.ReadColorAsync(ct);");
                m.Assign("red", $"(int){m.Name}Color.R");
                m.Assign("green", $"(int){m.Name}Color.G");
                m.Assign("blue", $"(int){m.Name}Color.B");
            }
        };

        yield return new ModuleBlock
        {
            Type = Pir, DisplayName = "Motion (PIR)", Category = "Sensors", Esp32Only = true,
            Description = "True while a PIR sensor (HC-SR501) sees someone move.",
            Ports = [Trigger, FlowPortDefinition.Output("motion", FlowValueKind.Boolean)],
            Properties = b => [PinProperty("pin", b.DigitalReadPinOptions, 27, 14, 4, 2, 1)],
            Pins = [new("pin", PinCapability.DigitalRead, "PIR signal", ModulePinRole.DigitalInput)],
            Run = async r =>
            {
                var sensor = r.Esp32.CreatePir(r.Int("pin"));
                await sensor.InitAsync(r.Ct);
                return Out(("motion", await sensor.IsMotionDetectedAsync(r.Ct)));
            },
            Emit = m =>
            {
                m.Line($"var {m.Name} = esp32.CreatePir({m.Int("pin")});");
                m.Line($"await {m.Name}.InitAsync(ct);");
                m.Assign("motion", $"await {m.Name}.IsMotionDetectedAsync(ct)");
            }
        };

        yield return new ModuleBlock
        {
            Type = Button, DisplayName = "Button", Category = "Sensors",
            Description = "True while a push button is pressed. Wired between the pin and GND it uses the board's internal pull-up, so no resistor is needed.",
            Ports = [Trigger, FlowPortDefinition.Output("pressed", FlowValueKind.Boolean)],
            Properties = b => [PinProperty("pin", b.DigitalReadPinOptions, 4, 5, 2, 12, 3), new FlowPropertyDefinition("pullUp", FlowValueKind.Boolean, DefaultValue: true, Options: Choices(("Button to GND (pull-up)", true, "Pressed = LOW. The usual wiring."), ("Button to 3.3 V", false, "Pressed = HIGH. Needs an external pull-down resistor.")))],
            Pins = [new("pin", PinCapability.DigitalRead, "button input", ModulePinRole.DigitalInput)],
            Run = async r =>
            {
                var pin = r.Int("pin");
                var pullUp = r.Bool("pullUp");
                await r.Board.Gpio.SetPinModeAsync(pin, pullUp ? PinMode.InputPullUp : PinMode.Input, r.Ct);
                var first = await r.Board.Gpio.DigitalReadAsync(pin, r.Ct);
                await Task.Delay(15, r.Ct); // two reads a moment apart ignore the contact bounce of a mechanical button
                var second = await r.Board.Gpio.DigitalReadAsync(pin, r.Ct);
                var pressedLevel = pullUp ? PinValue.Low : PinValue.High;
                return Out(("pressed", first == pressedLevel && second == pressedLevel));
            },
            Emit = m =>
            {
                var pullUp = m.Bool("pullUp");
                m.Line($"await board.Gpio.SetPinModeAsync({m.Int("pin")}, PinMode.{(pullUp ? "InputPullUp" : "Input")}, ct);");
                m.Line($"var {m.Name}First = await board.Gpio.DigitalReadAsync({m.Int("pin")}, ct);");
                m.Line("await Task.Delay(15, ct); // two reads a moment apart ignore the contact bounce");
                m.Line($"var {m.Name}Second = await board.Gpio.DigitalReadAsync({m.Int("pin")}, ct);");
                m.Assign("pressed", $"{m.Name}First == PinValue.{(pullUp ? "Low" : "High")} && {m.Name}Second == PinValue.{(pullUp ? "Low" : "High")}");
            }
        };

        yield return new ModuleBlock
        {
            Type = AnalogPercent, DisplayName = "Analog Sensor (%)", Category = "Sensors",
            Description = "Reads a light sensor, soil probe, potentiometer or any analog sensor as a percentage from 0 to 100.",
            Ports = [Trigger, FlowPortDefinition.Output("percent", FlowValueKind.Number), FlowPortDefinition.Output("raw", FlowValueKind.Integer)],
            Properties = b => [PinProperty("pin", b.AnalogReadPinOptions, 34, 35, 32, 33, 1, 2), new FlowPropertyDefinition("invert", FlowValueKind.Boolean, DefaultValue: false, Options: Choices(("Normal", false, "More voltage = higher percentage."), ("Inverted", true, "More voltage = lower percentage (typical for soil moisture probes).")))],
            Pins = [new("pin", PinCapability.AnalogRead, "analog read", ModulePinRole.AnalogInput)],
            Run = async r =>
            {
                var max = (BuiltInBoardProfiles.FindById(r.BoardId) ?? BuiltInBoardProfiles.Default).AnalogMaxValue;
                var raw = await r.Board.Gpio.AnalogReadAsync(r.Int("pin"), r.Ct);
                var percent = Math.Clamp(raw * 100.0 / Math.Max(1, max), 0, 100);
                return Out(("percent", Round(r.Bool("invert") ? 100 - percent : percent, 1)), ("raw", raw));
            },
            Emit = m =>
            {
                var profile = BuiltInBoardProfiles.FindById(m.BoardId) ?? BuiltInBoardProfiles.Default;
                var invert = m.Bool("invert");
                m.Line($"var {m.Name}_reading = await board.Gpio.AnalogReadAsync({m.Int("pin")}, ct);");
                m.Line($"var {m.Name}_share = Math.Clamp({m.Name}_reading * 100.0 / {profile.AnalogMaxValue}, 0, 100);");
                m.Assign("percent", invert ? $"Math.Round(100 - {m.Name}_share, 1)" : $"Math.Round({m.Name}_share, 1)");
                m.Assign("raw", $"{m.Name}_reading");
            }
        };

        // ------------------------------------------------------------ modules
        yield return new ModuleBlock
        {
            Type = Relay, DisplayName = "Relay", Category = "Modules",
            Description = "Switches a relay module on or off. Most relay boards switch ON when the pin is LOW (active low).",
            Ports = [Trigger, FlowPortDefinition.Input("state", FlowValueKind.Boolean, required: false), Done, FlowPortDefinition.Output("state", FlowValueKind.Boolean)],
            Properties = b => [PinProperty("pin", b.DigitalWritePinOptions, 26, 25, 23, 2, 4), new FlowPropertyDefinition("state", FlowValueKind.Boolean, DefaultValue: true, Options: Choices(("On", true, "Switch the load on."), ("Off", false, "Switch the load off."))), new FlowPropertyDefinition("activeLow", FlowValueKind.Boolean, DefaultValue: true, Options: YesNo, IsAdvanced: true)],
            Pins = [new("pin", PinCapability.DigitalWrite, "relay output", ModulePinRole.DigitalOutput)],
            Run = async r =>
            {
                var pin = r.Int("pin");
                var on = r.InputOr<bool>("state", "state");
                var level = on != r.Bool("activeLow") ? PinValue.High : PinValue.Low;
                // the level is decided before the pin becomes an output, so a relay never clicks on for an instant
                await r.Board.Gpio.DigitalWriteAsync(pin, level, r.Ct);
                await r.Board.Gpio.SetPinModeAsync(pin, PinMode.Output, r.Ct);
                await r.Board.Gpio.DigitalWriteAsync(pin, level, r.Ct);
                return Out(("done", true), ("state", on));
            },
            Emit = m =>
            {
                var activeLow = m.Bool("activeLow");
                var state = m.Input("state", ModuleCode.Literal(m.Bool("state")));
                m.Line($"var {m.Name}On = {state};");
                m.Line($"var {m.Name}Level = {m.Name}On != {ModuleCode.Literal(activeLow)} ? PinValue.High : PinValue.Low;");
                m.Line($"await board.Gpio.DigitalWriteAsync({m.Int("pin")}, {m.Name}Level, ct);");
                m.Line($"await board.Gpio.SetPinModeAsync({m.Int("pin")}, PinMode.Output, ct);");
                m.Line($"await board.Gpio.DigitalWriteAsync({m.Int("pin")}, {m.Name}Level, ct);");
                m.Assign("done", "true");
                m.Assign("state", $"{m.Name}On");
            }
        };

        yield return new ModuleBlock
        {
            Type = Buzzer, DisplayName = "Buzzer", Category = "Modules", Esp32Only = true,
            Description = "Plays a tone on a passive buzzer or speaker.",
            Ports = [Trigger, FlowPortDefinition.Input("frequency", FlowValueKind.Number, required: false), Done],
            Properties = b => [PinProperty("pin", b.DigitalWritePinOptions, 25, 26, 23, 4), new FlowPropertyDefinition("frequencyHz", FlowValueKind.Integer, DefaultValue: 1000), new FlowPropertyDefinition("durationMs", FlowValueKind.Integer, DefaultValue: 300)],
            Pins = [new("pin", PinCapability.DigitalWrite, "buzzer output", ModulePinRole.DigitalOutput)],
            Run = async r =>
            {
                var frequency = Math.Clamp((int)Math.Round(r.InputOr<double>("frequency", "frequencyHz")), 20, 20000);
                var duration = Math.Clamp(r.Int("durationMs"), 1, 10000);
                await r.Esp32.CreateBuzzer(r.Int("pin")).ToneAsync(frequency, TimeSpan.FromMilliseconds(duration), r.Ct);
                await Task.Delay(duration, r.Ct); // the tone plays on its own: wait so the next block starts after it
                return Out(("done", true));
            },
            Emit = m =>
            {
                var frequency = m.Input("frequency", m.Int("frequencyHz").ToString(CultureInfo.InvariantCulture));
                var duration = Math.Clamp(m.Int("durationMs"), 1, 10000);
                m.Line($"await esp32.CreateBuzzer({m.Int("pin")}).ToneAsync(Math.Clamp((int)Math.Round((double){frequency}), 20, 20000), TimeSpan.FromMilliseconds({duration}), ct);");
                m.Line($"await Task.Delay({duration}, ct);");
                m.Assign("done", "true");
            }
        };

        yield return new ModuleBlock
        {
            Type = RgbLed, DisplayName = "RGB LED", Category = "Modules", Esp32Only = true,
            Description = "Sets the colour of an RGB LED wired to three PWM pins (red, green, blue from 0 to 255).",
            Ports = [Trigger, FlowPortDefinition.Input("red", FlowValueKind.Number, required: false), FlowPortDefinition.Input("green", FlowValueKind.Number, required: false), FlowPortDefinition.Input("blue", FlowValueKind.Number, required: false), Done],
            Properties = b =>
            {
                var pins = Distinct((b.PwmPinOptions, [25, 4, 1]), (b.PwmPinOptions, [26, 5, 2]), (b.PwmPinOptions, [27, 18, 3]));
                return [PinProperty("redPin", b.PwmPinOptions, pins[0]), PinProperty("greenPin", b.PwmPinOptions, pins[1]), PinProperty("bluePin", b.PwmPinOptions, pins[2]),
                    new FlowPropertyDefinition("red", FlowValueKind.Integer, DefaultValue: 255), new FlowPropertyDefinition("green", FlowValueKind.Integer, DefaultValue: 120), new FlowPropertyDefinition("blue", FlowValueKind.Integer, DefaultValue: 0),
                    new FlowPropertyDefinition("commonAnode", FlowValueKind.Boolean, DefaultValue: false, Options: YesNo, IsAdvanced: true)];
            },
            Pins = [new("redPin", PinCapability.Pwm | PinCapability.DigitalWrite, "PWM output", ModulePinRole.Pwm), new("greenPin", PinCapability.Pwm | PinCapability.DigitalWrite, "PWM output", ModulePinRole.Pwm), new("bluePin", PinCapability.Pwm | PinCapability.DigitalWrite, "PWM output", ModulePinRole.Pwm)],
            Run = async r =>
            {
                var led = r.Esp32.CreateRgbLed(r.Int("redPin"), r.Int("greenPin"), r.Int("bluePin"), r.Bool("commonAnode"));
                await led.InitAsync(r.Ct);
                byte Channel(string name) => (byte)Math.Clamp((int)Math.Round(r.InputOr<double>(name, name)), 0, 255);
                await led.SetColorAsync(Channel("red"), Channel("green"), Channel("blue"), r.Ct);
                return Out(("done", true));
            },
            Emit = m =>
            {
                string Channel(string name) => $"(byte)Math.Clamp((int)Math.Round((double){m.Input(name, m.Int(name).ToString(CultureInfo.InvariantCulture))}), 0, 255)";
                m.Line($"var {m.Name} = esp32.CreateRgbLed({m.Int("redPin")}, {m.Int("greenPin")}, {m.Int("bluePin")}, {ModuleCode.Literal(m.Bool("commonAnode"))});");
                m.Line($"await {m.Name}.InitAsync(ct);");
                m.Line($"await {m.Name}.SetColorAsync({Channel("red")}, {Channel("green")}, {Channel("blue")}, ct);");
                m.Assign("done", "true");
            }
        };

        yield return new ModuleBlock
        {
            Type = NeoPixel, DisplayName = "LED Strip (NeoPixel)", Category = "Modules", Esp32Only = true,
            Description = "Lights a WS2812 / NeoPixel strip or ring: every pixel, or one pixel, in the colour you choose.",
            Ports = [Trigger, FlowPortDefinition.Input("red", FlowValueKind.Number, required: false), FlowPortDefinition.Input("green", FlowValueKind.Number, required: false), FlowPortDefinition.Input("blue", FlowValueKind.Number, required: false), Done],
            Properties = b => [PinProperty("pin", b.DigitalWritePinOptions, 5, 4, 2, 1), new FlowPropertyDefinition("count", FlowValueKind.Integer, DefaultValue: 8),
                new FlowPropertyDefinition("red", FlowValueKind.Integer, DefaultValue: 0), new FlowPropertyDefinition("green", FlowValueKind.Integer, DefaultValue: 120), new FlowPropertyDefinition("blue", FlowValueKind.Integer, DefaultValue: 255),
                new FlowPropertyDefinition("pixel", FlowValueKind.Integer, DefaultValue: -1, IsAdvanced: true), new FlowPropertyDefinition("brightness", FlowValueKind.Integer, DefaultValue: 64, IsAdvanced: true)],
            Pins = [new("pin", PinCapability.DigitalWrite, "NeoPixel data", ModulePinRole.DigitalOutput)],
            Run = async r =>
            {
                var strip = r.Esp32.CreateNeoPixel(r.Int("pin"), Math.Clamp(r.Int("count"), 1, 1024));
                await strip.InitAsync(r.Ct);
                await strip.SetBrightnessAsync((byte)Math.Clamp(r.Int("brightness"), 0, 255), r.Ct);
                byte Channel(string name) => (byte)Math.Clamp((int)Math.Round(r.InputOr<double>(name, name)), 0, 255);
                var pixel = r.Int("pixel");
                if (pixel < 0)
                    await strip.SetAllAsync(Channel("red"), Channel("green"), Channel("blue"), r.Ct);
                else
                    await strip.SetPixelAsync(pixel, Channel("red"), Channel("green"), Channel("blue"), r.Ct);

                await strip.ShowAsync(r.Ct);
                return Out(("done", true));
            },
            Emit = m =>
            {
                string Channel(string name) => $"(byte)Math.Clamp((int)Math.Round((double){m.Input(name, m.Int(name).ToString(CultureInfo.InvariantCulture))}), 0, 255)";
                m.Line($"var {m.Name} = esp32.CreateNeoPixel({m.Int("pin")}, {Math.Clamp(m.Int("count"), 1, 1024)});");
                m.Line($"await {m.Name}.InitAsync(ct);");
                m.Line($"await {m.Name}.SetBrightnessAsync({Math.Clamp(m.Int("brightness"), 0, 255)}, ct);");
                var pixel = m.Int("pixel");
                m.Line(pixel < 0
                    ? $"await {m.Name}.SetAllAsync({Channel("red")}, {Channel("green")}, {Channel("blue")}, ct);"
                    : $"await {m.Name}.SetPixelAsync({pixel}, {Channel("red")}, {Channel("green")}, {Channel("blue")}, ct);");
                m.Line($"await {m.Name}.ShowAsync(ct);");
                m.Assign("done", "true");
            }
        };

        yield return new ModuleBlock
        {
            Type = Lcd, DisplayName = "LCD Text (I2C)", Category = "Modules", Esp32Only = true,
            Description = "Writes a line of text on a 16x2 or 20x4 character LCD with an I2C backpack. Use {value} in the text to show a number.",
            Ports = [Trigger, FlowPortDefinition.Input("value", FlowValueKind.Any, required: false), Done],
            Properties = _ => [new FlowPropertyDefinition("address", FlowValueKind.Integer, DefaultValue: 0x27, Options: Addresses(("PCF8574", 0x27), ("PCF8574A", 0x3F))),
                new FlowPropertyDefinition("text", FlowValueKind.String, DefaultValue: "Temp: {value} C"), new FlowPropertyDefinition("row", FlowValueKind.Integer, DefaultValue: 0),
                new FlowPropertyDefinition("columns", FlowValueKind.Integer, DefaultValue: 16, Options: Choices(("16 columns", 16, "16x2 display."), ("20 columns", 20, "20x4 display.")), IsAdvanced: true),
                new FlowPropertyDefinition("rows", FlowValueKind.Integer, DefaultValue: 2, Options: Choices(("2 rows", 2, "16x2 display."), ("4 rows", 4, "20x4 display.")), IsAdvanced: true)],
            Run = async r =>
            {
                var columns = Math.Clamp(r.Int("columns"), 8, 40);
                var lcd = r.Esp32.CreateLcd(columns, Math.Clamp(r.Int("rows"), 1, 4), r.Int("address"));
                await lcd.InitAsync(r.Ct);
                var text = ModuleText.Format(r.String("text"), r.HasInput("value") ? r.RawInput("value") : null, columns);
                await lcd.WriteTextAsync(Math.Clamp(r.Int("row"), 0, 3), 0, text, r.Ct);
                return Out(("done", true));
            },
            Emit = m =>
            {
                var columns = Math.Clamp(m.Int("columns"), 8, 40);
                m.Line($"var {m.Name} = esp32.CreateLcd({columns}, {Math.Clamp(m.Int("rows"), 1, 4)}, {m.Int("address")});");
                m.Line($"await {m.Name}.InitAsync(ct);");
                var value = m.HasInput("value") ? m.Input("value", "null") : "null";
                m.Line($"await {m.Name}.WriteTextAsync({Math.Clamp(m.Int("row"), 0, 3)}, 0, {ModuleCode.Literal(m.String("text"))}.Replace(\"{{value}}\", FormattableString.Invariant($\"{{{value}}}\")).PadRight({columns}).Substring(0, {columns}), ct);");
                m.Assign("done", "true");
            }
        };

        yield return new ModuleBlock
        {
            Type = Oled, DisplayName = "OLED Text (SSD1306)", Category = "Modules", Esp32Only = true,
            Description = "Draws text on a 128x64 or 128x32 OLED display (SSD1306, I2C). Use {value} in the text to show a number.",
            Ports = [Trigger, FlowPortDefinition.Input("value", FlowValueKind.Any, required: false), Done],
            Properties = _ => [new FlowPropertyDefinition("address", FlowValueKind.Integer, DefaultValue: 0x3C, Options: Addresses(("Default", 0x3C), ("Alternate", 0x3D))),
                new FlowPropertyDefinition("text", FlowValueKind.String, DefaultValue: "Hello {value}"), new FlowPropertyDefinition("x", FlowValueKind.Integer, DefaultValue: 0), new FlowPropertyDefinition("y", FlowValueKind.Integer, DefaultValue: 0),
                new FlowPropertyDefinition("size", FlowValueKind.Integer, DefaultValue: 2, Options: Choices(("Small", 1, "6x8 pixels per letter."), ("Medium", 2, "12x16 pixels per letter."), ("Large", 3, "18x24 pixels per letter."))),
                new FlowPropertyDefinition("clear", FlowValueKind.Boolean, DefaultValue: true, Options: YesNo),
                new FlowPropertyDefinition("height", FlowValueKind.Integer, DefaultValue: 64, Options: Choices(("128 x 64", 64, "The bigger one."), ("128 x 32", 32, "The slim one.")), IsAdvanced: true)],
            Run = async r =>
            {
                var oled = r.Esp32.CreateOled(128, r.Int("height"), r.Int("address"));
                await oled.InitAsync(r.Ct);
                if (r.Bool("clear"))
                    await oled.ClearAsync(r.Ct);

                var text = ModuleText.Format(r.String("text"), r.HasInput("value") ? r.RawInput("value") : null, 0);
                await oled.DrawTextAsync(r.Int("x"), r.Int("y"), text, 1, Math.Clamp(r.Int("size"), 1, 4), r.Ct);
                await oled.FlushAsync(r.Ct);
                return Out(("done", true));
            },
            Emit = m =>
            {
                m.Line($"var {m.Name} = esp32.CreateOled(128, {m.Int("height")}, {m.Int("address")});");
                m.Line($"await {m.Name}.InitAsync(ct);");
                if (m.Bool("clear"))
                    m.Line($"await {m.Name}.ClearAsync(ct);");

                var value = m.HasInput("value") ? m.Input("value", "null") : "null";
                m.Line($"await {m.Name}.DrawTextAsync({m.Int("x")}, {m.Int("y")}, {ModuleCode.Literal(m.String("text"))}.Replace(\"{{value}}\", FormattableString.Invariant($\"{{{value}}}\")), 1, {Math.Clamp(m.Int("size"), 1, 4)}, ct);");
                m.Line($"await {m.Name}.FlushAsync(ct);");
                m.Assign("done", "true");
            }
        };

        yield return new ModuleBlock
        {
            Type = DcMotor, DisplayName = "DC Motor (H-bridge)", Category = "Modules", Esp32Only = true,
            Description = "Runs a DC motor through an L298N / L9110 H-bridge. Speed goes from -100 (full reverse) to 100 (full forward); 0 stops.",
            Ports = [Trigger, FlowPortDefinition.Input("speed", FlowValueKind.Number, required: false), Done],
            Properties = b =>
            {
                var pins = Distinct((b.PwmPinOptions, [25, 4, 1]), (b.DigitalWritePinOptions, [26, 5, 2]));
                return [PinProperty("in1Pin", b.PwmPinOptions, pins[0]), PinProperty("in2Pin", b.DigitalWritePinOptions, pins[1]),
                    new FlowPropertyDefinition("enablePin", FlowValueKind.Integer, DefaultValue: -1, Options: new[] { new FlowPropertyOption("No enable pin", -1, "The ENA jumper stays on the board.") }.Concat(b.PwmPinOptions).ToArray()),
                    new FlowPropertyDefinition("speed", FlowValueKind.Integer, DefaultValue: 60)];
            },
            Pins = [new("in1Pin", PinCapability.Pwm | PinCapability.DigitalWrite, "motor input 1", ModulePinRole.Pwm), new("in2Pin", PinCapability.DigitalWrite, "motor input 2", ModulePinRole.DigitalOutput)],
            Run = async r =>
            {
                var motor = r.Esp32.CreateMotor(r.Int("in1Pin"), r.Int("in2Pin"), r.Int("enablePin"));
                await motor.InitAsync(r.Ct);
                var speed = Math.Clamp((int)Math.Round(r.InputOr<double>("speed", "speed")), -100, 100);
                await motor.SetSpeedAsync(speed, r.Ct);
                return Out(("done", true));
            },
            Emit = m =>
            {
                var speed = m.Input("speed", m.Int("speed").ToString(CultureInfo.InvariantCulture));
                m.Line($"var {m.Name} = esp32.CreateMotor({m.Int("in1Pin")}, {m.Int("in2Pin")}, {m.Int("enablePin")});");
                m.Line($"await {m.Name}.InitAsync(ct);");
                m.Line($"await {m.Name}.SetSpeedAsync(Math.Clamp((int)Math.Round((double){speed}), -100, 100), ct);");
                m.Assign("done", "true");
            }
        };

        yield return new ModuleBlock
        {
            Type = Stepper, DisplayName = "Stepper Motor", Category = "Modules", Esp32Only = true,
            Description = "Turns a 28BYJ-48 (ULN2003) stepper motor a number of steps. Positive turns one way, negative the other.",
            Ports = [Trigger, FlowPortDefinition.Input("steps", FlowValueKind.Number, required: false), Done],
            Properties = b =>
            {
                var pins = Distinct((b.DigitalWritePinOptions, [19, 4, 1]), (b.DigitalWritePinOptions, [18, 5, 2]), (b.DigitalWritePinOptions, [5, 6, 3]), (b.DigitalWritePinOptions, [17, 7, 4]));
                return [PinProperty("pin1", b.DigitalWritePinOptions, pins[0]), PinProperty("pin2", b.DigitalWritePinOptions, pins[1]), PinProperty("pin3", b.DigitalWritePinOptions, pins[2]), PinProperty("pin4", b.DigitalWritePinOptions, pins[3]),
                    new FlowPropertyDefinition("steps", FlowValueKind.Integer, DefaultValue: 512), new FlowPropertyDefinition("rpm", FlowValueKind.Integer, DefaultValue: 10),
                    new FlowPropertyDefinition("stepsPerRevolution", FlowValueKind.Integer, DefaultValue: 2048, IsAdvanced: true)];
            },
            Pins = [new("pin1", PinCapability.DigitalWrite, "stepper coil 1", ModulePinRole.DigitalOutput), new("pin2", PinCapability.DigitalWrite, "stepper coil 2", ModulePinRole.DigitalOutput), new("pin3", PinCapability.DigitalWrite, "stepper coil 3", ModulePinRole.DigitalOutput), new("pin4", PinCapability.DigitalWrite, "stepper coil 4", ModulePinRole.DigitalOutput)],
            Run = async r =>
            {
                var stepper = r.Esp32.CreateStepper(r.Int("pin1"), r.Int("pin2"), r.Int("pin3"), r.Int("pin4"), r.Int("stepsPerRevolution"));
                await stepper.InitAsync(r.Ct);
                await stepper.SetSpeedRpmAsync(Math.Max(1, r.Int("rpm")), r.Ct);
                await stepper.StepAsync((int)Math.Round(r.InputOr<double>("steps", "steps")), r.Ct);
                return Out(("done", true));
            },
            Emit = m =>
            {
                var steps = m.Input("steps", m.Int("steps").ToString(CultureInfo.InvariantCulture));
                m.Line($"var {m.Name} = esp32.CreateStepper({m.Int("pin1")}, {m.Int("pin2")}, {m.Int("pin3")}, {m.Int("pin4")}, {m.Int("stepsPerRevolution")});");
                m.Line($"await {m.Name}.InitAsync(ct);");
                m.Line($"await {m.Name}.SetSpeedRpmAsync({Math.Max(1, m.Int("rpm"))}, ct);");
                m.Line($"await {m.Name}.StepAsync((int)Math.Round((double){steps}), ct);");
                m.Assign("done", "true");
            }
        };

        // ------------------------------------------------------------ helpers
        yield return new ModuleBlock
        {
            Type = Threshold, DisplayName = "Threshold", Category = "Logic",
            Description = "True when a number is above (or below) a limit: the quickest way to turn a sensor reading into an on/off decision.",
            Ports = [FlowPortDefinition.Input("value", FlowValueKind.Number), FlowPortDefinition.Output("result", FlowValueKind.Boolean)],
            Properties = _ => [new FlowPropertyDefinition("limit", FlowValueKind.Number, DefaultValue: 25), new FlowPropertyDefinition("mode", FlowValueKind.String, DefaultValue: "above", Options: Choices(("Above the limit", "above", "True when the value is greater than the limit."), ("Below the limit", "below", "True when the value is lower than the limit.")))],
            Run = r =>
            {
                var value = r.Input<double>("value");
                var limit = r.Double("limit");
                return ValueTask.FromResult(Out(("result", r.String("mode") == "below" ? value < limit : value > limit)));
            },
            Emit = m => m.Assign("result", $"(double){m.Input("value", "0.0")} {(m.String("mode") == "below" ? "<" : ">")} {ModuleCode.Literal(m.Double("limit"))}")
        };

        yield return new ModuleBlock
        {
            Type = Gate, DisplayName = "Logic Gate", Category = "Logic",
            Description = "Combines two true/false values: AND, OR, XOR, NAND, NOR, or NOT of the first one.",
            Ports = [FlowPortDefinition.Input("a", FlowValueKind.Boolean), FlowPortDefinition.Input("b", FlowValueKind.Boolean, required: false), FlowPortDefinition.Output("result", FlowValueKind.Boolean)],
            Properties = _ => [new FlowPropertyDefinition("operation", FlowValueKind.String, DefaultValue: "and", Options: Choices(("AND", "and", "True when both are true."), ("OR", "or", "True when at least one is true."), ("XOR", "xor", "True when exactly one is true."), ("NAND", "nand", "False only when both are true."), ("NOR", "nor", "True only when both are false."), ("NOT a", "not", "The opposite of the first one.")))],
            Run = r =>
            {
                var a = r.Input<bool>("a");
                var b = r.HasInput("b") && r.Input<bool>("b");
                var result = r.String("operation") switch
                {
                    "or" => a || b,
                    "xor" => a ^ b,
                    "nand" => !(a && b),
                    "nor" => !(a || b),
                    "not" => !a,
                    _ => a && b
                };
                return ValueTask.FromResult(Out(("result", result)));
            },
            Emit = m =>
            {
                var a = m.Input("a", "false");
                var b = m.Input("b", "false");
                m.Assign("result", m.String("operation") switch
                {
                    "or" => $"{a} || {b}",
                    "xor" => $"{a} ^ {b}",
                    "nand" => $"!({a} && {b})",
                    "nor" => $"!({a} || {b})",
                    "not" => $"!{a}",
                    _ => $"{a} && {b}"
                });
            }
        };

        yield return new ModuleBlock
        {
            Type = Calc, DisplayName = "Calculate", Category = "Math",
            Description = "Adds, subtracts, multiplies or divides two numbers (also minimum, maximum and remainder). Handy for unit changes such as °C to °F.",
            Ports = [FlowPortDefinition.Input("a", FlowValueKind.Number), FlowPortDefinition.Input("b", FlowValueKind.Number, required: false), FlowPortDefinition.Output("result", FlowValueKind.Number)],
            Properties = _ => [new FlowPropertyDefinition("operation", FlowValueKind.String, DefaultValue: "multiply", Options: Choices(("Add +", "add", "a + b"), ("Subtract -", "subtract", "a - b"), ("Multiply ×", "multiply", "a × b"), ("Divide ÷", "divide", "a ÷ b"), ("Remainder %", "modulo", "The remainder of a ÷ b."), ("Smaller of the two", "min", "The lower one."), ("Larger of the two", "max", "The higher one."))),
                new FlowPropertyDefinition("b", FlowValueKind.Number, DefaultValue: 1)],
            Run = r =>
            {
                var a = r.Input<double>("a");
                var b = r.InputOr<double>("b", "b");
                var op = r.String("operation");
                if ((op is "divide" or "modulo") && b == 0)
                    throw new InvalidOperationException("Calculate: cannot divide by zero.");

                var result = op switch
                {
                    "add" => a + b,
                    "subtract" => a - b,
                    "divide" => a / b,
                    "modulo" => a % b,
                    "min" => Math.Min(a, b),
                    "max" => Math.Max(a, b),
                    _ => a * b
                };
                return ValueTask.FromResult(Out(("result", result)));
            },
            Emit = m =>
            {
                var a = $"(double){m.Input("a", "0.0")}";
                var b = $"(double){m.Input("b", ModuleCode.Literal(m.Double("b")))}";
                m.Assign("result", m.String("operation") switch
                {
                    "add" => $"{a} + {b}",
                    "subtract" => $"{a} - {b}",
                    "divide" => $"{a} / {b}",
                    "modulo" => $"{a} % {b}",
                    "min" => $"Math.Min({a}, {b})",
                    "max" => $"Math.Max({a}, {b})",
                    _ => $"{a} * {b}"
                });
            }
        };
    }

    private static async ValueTask<IReadOnlyDictionary<string, object?>> RunDht(ModuleRun r)
    {
        var sensor = r.Esp32.CreateDhtSensor(r.Int("pin"), r.Int("model"));
        await sensor.InitAsync(r.Ct);
        var (temperature, humidity) = await sensor.ReadBothAsync(r.Ct);
        return Out(("temperature", Round(temperature)), ("humidity", Round(humidity)));
    }
}

/// <summary>Turns the text of a display block into what is drawn: <c>{value}</c> becomes the wired value, and a fixed-width line is padded.</summary>
public static class ModuleText
{
    public static string Format(string template, object? value, int width)
    {
        var shown = value switch
        {
            null => string.Empty,
            bool flag => flag ? "true" : "false",
            double number => Math.Round(number, 2).ToString("0.##", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

        var text = template.Replace("{value}", shown, StringComparison.Ordinal);
        return width <= 0 ? text : text.Length >= width ? text[..width] : text.PadRight(width);
    }
}
