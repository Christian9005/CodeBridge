using System.Globalization;
using CodeBridge.Flow;

namespace CodeBridge.IotBindings;

public static partial class IotBindingBlocks
{
    // second batch: weather, air quality, light, direction, distance, clocks, a display, I/O expanders, touch keys, and three generic I2C blocks
    public const string Bmp280 = "iot.bmp280";
    public const string Bme280 = "iot.bme280";
    public const string Lm75 = "iot.lm75";
    public const string Sht4x = "iot.sht4x";
    public const string Aht20 = "iot.aht20";
    public const string Max44009 = "iot.max44009";
    public const string Hmc5883l = "iot.hmc5883l";
    public const string Scd4x = "iot.scd4x";
    public const string Vl53L1X = "iot.vl53l1x";
    public const string Ds1307 = "iot.ds1307";
    public const string Pcf8563 = "iot.pcf8563";
    public const string SevenSegment = "iot.ht16k33-7seg";
    public const string ExpanderWrite = "iot.pcf8574-write";
    public const string ExpanderRead = "iot.pcf8574-read";
    public const string Mpr121 = "iot.mpr121";
    public const string I2cScan = "iot.i2c-scan";
    public const string I2cRead = "iot.i2c-read";
    public const string I2cWrite = "iot.i2c-write";

    private static IReadOnlyList<ModuleBlock> More() =>
    [
        // ------------------------------------------------------------ weather and air
        new ModuleBlock
        {
            Type = Bmp280, DisplayName = "Pressure (BMP280)", Category = "Sensors", Esp32Only = true, UsesI2c = true,
            Description = "Reads a BMP280 over I2C: air pressure (hPa), temperature (°C) and the altitude that pressure means (m, from sea-level pressure).",
            Ports = [Trigger, Number("pressure"), Number("temperature"), Number("altitude")],
            Properties = _ =>
            [
                Address(0x76, ("SDO to GND", 0x76), ("SDO to VCC", 0x77)),
                new FlowPropertyDefinition("seaLevelHpa", FlowValueKind.Number, DefaultValue: 1013.25, IsAdvanced: true)
            ],
            Run = r => Read(r, () =>
            {
                Iot.Device.Bmxx80.Bmp280 sensor;
                try
                {
                    sensor = Cached(r, "bmp280", r.Int("address"), bus => new Iot.Device.Bmxx80.Bmp280(bus)
                    {
                        TemperatureSampling = Iot.Device.Bmxx80.Sampling.LowPower,
                        PressureSampling = Iot.Device.Bmxx80.Sampling.UltraHighResolution
                    });
                }
                catch (Exception ex) when (ex.Message.Contains("id 96", StringComparison.Ordinal))
                {
                    // the driver reads the chip id (0x60 is a BME280): say which block the user wants instead of an id number
                    throw new I2cChipException($"The chip at 0x{r.Int("address"):X2} is a BME280 (it also measures humidity), not a BMP280: use a BME280 block (Weather (BME280) or Weather + Altitude (BME280)).", ex);
                }

                sensor.SetPowerMode(Iot.Device.Bmxx80.PowerMode.Bmx280PowerMode.Forced);
                Thread.Sleep(sensor.GetMeasurementDuration());
                if (!sensor.TryReadTemperature(out var temperature) || !sensor.TryReadPressure(out var pressure))
                    throw new InvalidOperationException("The BMP280 did not give a reading.");

                sensor.TryReadAltitude(UnitsNet.Pressure.FromHectopascals(r.Double("seaLevelHpa")), out var altitude);
                return ModuleRun.Output(("pressure", Round(pressure.Hectopascals)), ("temperature", Round(temperature.DegreesCelsius)), ("altitude", Round(altitude.Meters, 1)));
            }),
            Emit = m =>
            {
                m.Line($"var {m.Name} = new Iot.Device.Bmxx80.Bmp280(new {Bridge}(esp32.I2C, {m.Int("address")})) {{ TemperatureSampling = Iot.Device.Bmxx80.Sampling.LowPower, PressureSampling = Iot.Device.Bmxx80.Sampling.UltraHighResolution }};");
                m.Line($"{m.Name}.SetPowerMode(Iot.Device.Bmxx80.PowerMode.Bmx280PowerMode.Forced);");
                m.Line($"await Task.Delay({m.Name}.GetMeasurementDuration(), ct);");
                m.Line($"if (!{m.Name}.TryReadTemperature(out UnitsNet.Temperature {m.Name}T) || !{m.Name}.TryReadPressure(out UnitsNet.Pressure {m.Name}P)) throw new InvalidOperationException(\"The BMP280 did not give a reading.\");");
                m.Line($"{m.Name}.TryReadAltitude(UnitsNet.Pressure.FromHectopascals({ModuleCode.Literal(m.Double("seaLevelHpa"))}), out UnitsNet.Length {m.Name}A);");
                m.Assign("pressure", $"Math.Round({m.Name}P.Hectopascals, 2)");
                m.Assign("temperature", $"Math.Round({m.Name}T.DegreesCelsius, 2)");
                m.Assign("altitude", $"Math.Round({m.Name}A.Meters, 1)");
            }
        },

        new ModuleBlock
        {
            Type = Bme280, DisplayName = "Weather + Altitude (BME280)", Category = "Sensors", Esp32Only = true, UsesI2c = true,
            Description = "Reads a BME280 over I2C with the .NET IoT driver: temperature (°C), humidity (%), pressure (hPa) and the altitude that pressure means (m, from sea-level pressure).",
            Ports = [Trigger, Number("temperature"), Number("humidity"), Number("pressure"), Number("altitude")],
            Properties = _ =>
            [
                Address(0x76, ("SDO to GND", 0x76), ("SDO to VCC", 0x77)),
                new FlowPropertyDefinition("seaLevelHpa", FlowValueKind.Number, DefaultValue: 1013.25, IsAdvanced: true)
            ],
            Run = r => Read(r, () =>
            {
                var sensor = Cached(r, "bme280", r.Int("address"), bus => new Iot.Device.Bmxx80.Bme280(bus)
                {
                    TemperatureSampling = Iot.Device.Bmxx80.Sampling.UltraHighResolution,
                    PressureSampling = Iot.Device.Bmxx80.Sampling.UltraHighResolution,
                    HumiditySampling = Iot.Device.Bmxx80.Sampling.UltraHighResolution
                });
                sensor.SetPowerMode(Iot.Device.Bmxx80.PowerMode.Bmx280PowerMode.Forced);
                Thread.Sleep(sensor.GetMeasurementDuration());
                if (!sensor.TryReadTemperature(out var temperature) || !sensor.TryReadPressure(out var pressure) || !sensor.TryReadHumidity(out var humidity))
                    throw new InvalidOperationException("The BME280 did not give a reading.");

                sensor.TryReadAltitude(UnitsNet.Pressure.FromHectopascals(r.Double("seaLevelHpa")), out var altitude);
                return ModuleRun.Output(("temperature", Round(temperature.DegreesCelsius)), ("humidity", Round(humidity.Percent)), ("pressure", Round(pressure.Hectopascals)), ("altitude", Round(altitude.Meters, 1)));
            }),
            Emit = m =>
            {
                m.Line($"var {m.Name} = new Iot.Device.Bmxx80.Bme280(new {Bridge}(esp32.I2C, {m.Int("address")})) {{ TemperatureSampling = Iot.Device.Bmxx80.Sampling.UltraHighResolution, PressureSampling = Iot.Device.Bmxx80.Sampling.UltraHighResolution, HumiditySampling = Iot.Device.Bmxx80.Sampling.UltraHighResolution }};");
                m.Line($"{m.Name}.SetPowerMode(Iot.Device.Bmxx80.PowerMode.Bmx280PowerMode.Forced);");
                m.Line($"await Task.Delay({m.Name}.GetMeasurementDuration(), ct);");
                m.Line($"if (!{m.Name}.TryReadTemperature(out UnitsNet.Temperature {m.Name}T) || !{m.Name}.TryReadPressure(out UnitsNet.Pressure {m.Name}P) || !{m.Name}.TryReadHumidity(out UnitsNet.RelativeHumidity {m.Name}H)) throw new InvalidOperationException(\"The BME280 did not give a reading.\");");
                m.Line($"{m.Name}.TryReadAltitude(UnitsNet.Pressure.FromHectopascals({ModuleCode.Literal(m.Double("seaLevelHpa"))}), out UnitsNet.Length {m.Name}A);");
                m.Assign("temperature", $"Math.Round({m.Name}T.DegreesCelsius, 2)");
                m.Assign("humidity", $"Math.Round({m.Name}H.Percent, 2)");
                m.Assign("pressure", $"Math.Round({m.Name}P.Hectopascals, 2)");
                m.Assign("altitude", $"Math.Round({m.Name}A.Meters, 1)");
            }
        },

        new ModuleBlock
        {
            Type = Lm75, DisplayName = "Temperature (LM75)", Category = "Sensors", Esp32Only = true, UsesI2c = true,
            Description = "Reads an LM75 (or compatible: TMP75, PCT2075) temperature sensor over I2C, in °C. Eight can share the two wires.",
            Ports = [Trigger, Number("temperature")],
            Properties = _ => [Address(0x48, ("A0-A2 to GND", 0x48), ("A0 to VCC", 0x49), ("A1 to VCC", 0x4A), ("A0 and A1 to VCC", 0x4B), ("A2 to VCC", 0x4C))],
            Run = r => Read(r, () =>
            {
                var sensor = Cached(r, "lm75", r.Int("address"), bus => new Iot.Device.Lm75.Lm75(bus));
                return ModuleRun.Output(("temperature", Round(sensor.Temperature.DegreesCelsius)));
            }),
            Emit = m =>
            {
                m.Line($"var {m.Name} = new Iot.Device.Lm75.Lm75(new {Bridge}(esp32.I2C, {m.Int("address")}));");
                m.Assign("temperature", $"Math.Round({m.Name}.Temperature.DegreesCelsius, 2)");
            }
        },

        new ModuleBlock
        {
            Type = Sht4x, DisplayName = "Temp + Humidity (SHT4x)", Category = "Sensors", Esp32Only = true, UsesI2c = true,
            Description = "Reads an SHT40, SHT41 or SHT45 over I2C: temperature (°C) and relative humidity (%), in one measurement.",
            Ports = [Trigger, Number("temperature"), Number("humidity")],
            Properties = _ => [Address(0x44, ("SHT40-A / SHT41 / SHT45", 0x44), ("SHT40-B", 0x45), ("SHT40-C", 0x46))],
            Run = r => Read(r, () =>
            {
                var sensor = Cached(r, "sht4x", r.Int("address"), bus => new Iot.Device.Sht4x.Sht4x(bus));
                var (humidity, temperature) = sensor.ReadHumidityAndTemperature(Iot.Device.Sht4x.Sht4xRepeatability.High);
                if (humidity is null || temperature is null)
                    throw new InvalidOperationException("The SHT4x did not give a valid reading.");

                return ModuleRun.Output(("temperature", Round(temperature.Value.DegreesCelsius)), ("humidity", Round(humidity.Value.Percent)));
            }),
            Emit = m =>
            {
                m.Line($"var {m.Name} = new Iot.Device.Sht4x.Sht4x(new {Bridge}(esp32.I2C, {m.Int("address")}));");
                m.Line($"var ({m.Name}H, {m.Name}T) = {m.Name}.ReadHumidityAndTemperature(Iot.Device.Sht4x.Sht4xRepeatability.High);");
                m.Line($"if ({m.Name}H is null || {m.Name}T is null) throw new InvalidOperationException(\"The SHT4x did not give a valid reading.\");");
                m.Assign("temperature", $"Math.Round({m.Name}T.Value.DegreesCelsius, 2)");
                m.Assign("humidity", $"Math.Round({m.Name}H.Value.Percent, 2)");
            }
        },

        new ModuleBlock
        {
            Type = Aht20, DisplayName = "Temp + Humidity (AHT20)", Category = "Sensors", Esp32Only = true, UsesI2c = true,
            Description = "Reads an AHT20 (also sold as AHT21) over I2C: temperature (°C) and relative humidity (%). Small, cheap and good enough for a room.",
            Ports = [Trigger, Number("temperature"), Number("humidity")],
            Properties = _ => [Address(0x38, ("Default", 0x38))],
            Run = r => Read(r, () =>
            {
                var sensor = Cached(r, "aht20", r.Int("address"), bus => new Iot.Device.Ahtxx.Aht20(bus));
                return ModuleRun.Output(("temperature", Round(sensor.GetTemperature().DegreesCelsius)), ("humidity", Round(sensor.GetHumidity().Percent)));
            }),
            Emit = m =>
            {
                m.Line($"var {m.Name} = new Iot.Device.Ahtxx.Aht20(new {Bridge}(esp32.I2C, {m.Int("address")}));");
                m.Assign("temperature", $"Math.Round({m.Name}.GetTemperature().DegreesCelsius, 2)");
                m.Assign("humidity", $"Math.Round({m.Name}.GetHumidity().Percent, 2)");
            }
        },

        new ModuleBlock
        {
            Type = Scd4x, DisplayName = "CO₂ (SCD4x)", Category = "Sensors", Esp32Only = true, UsesI2c = true,
            Description = "Measures carbon dioxide (ppm) with an SCD40 or SCD41 over I2C, and the temperature (°C) and humidity (%) beside it. Good for telling when a room needs fresh air.",
            Ports = [Trigger, Number("co2"), Number("temperature"), Number("humidity")],
            Properties = _ => [Address(0x62, ("Default", 0x62))],
            Run = r => Read(r, () =>
            {
                // the sensor measures by itself every 5 seconds once started: the first reading is ready about 5 seconds after the first run
                var sensor = Cached(r, "scd4x", r.Int("address"), bus =>
                {
                    var device = new Iot.Device.Scd4x.Scd4x(bus);
                    device.StartPeriodicMeasurements();
                    return device;
                });
                var (co2, humidity, temperature) = sensor.ReadPeriodicMeasurement();
                if (co2 is null || humidity is null || temperature is null)
                    throw new InvalidOperationException("The SCD4x has no reading yet: it measures every 5 seconds, so try again in a moment.");

                return ModuleRun.Output(("co2", Round(co2.Value.PartsPerMillion, 0)), ("temperature", Round(temperature.Value.DegreesCelsius)), ("humidity", Round(humidity.Value.Percent)));
            }),
            Emit = m =>
            {
                m.Line($"var {m.Name} = new Iot.Device.Scd4x.Scd4x(new {Bridge}(esp32.I2C, {m.Int("address")}));");
                m.Line($"{m.Name}.StartPeriodicMeasurements();");
                m.Line($"await Task.Delay(5500, ct);   // the first reading is ready after about 5 seconds");
                m.Line($"var ({m.Name}Ppm, {m.Name}Rh, {m.Name}Temp) = {m.Name}.ReadPeriodicMeasurement();");
                m.Line($"if ({m.Name}Ppm is null || {m.Name}Rh is null || {m.Name}Temp is null) throw new InvalidOperationException(\"The SCD4x has no reading yet.\");");
                m.Assign("co2", $"Math.Round({m.Name}Ppm.Value.PartsPerMillion, 0)");
                m.Assign("temperature", $"Math.Round({m.Name}Temp.Value.DegreesCelsius, 2)");
                m.Assign("humidity", $"Math.Round({m.Name}Rh.Value.Percent, 2)");
            }
        },

        // ------------------------------------------------------------ light, direction, distance
        new ModuleBlock
        {
            Type = Max44009, DisplayName = "Light (MAX44009)", Category = "Sensors", Esp32Only = true, UsesI2c = true,
            Description = "Measures the ambient light with a MAX44009 over I2C, in lux, from moonlight to full sun.",
            Ports = [Trigger, Number("lux")],
            Properties = _ => [Address(0x4A, ("A0 to GND", 0x4A), ("A0 to VCC", 0x4B))],
            Run = r => Read(r, () =>
            {
                var sensor = Cached(r, "max44009", r.Int("address"), bus => new Iot.Device.Max44009.Max44009(bus));
                return ModuleRun.Output(("lux", Round(sensor.Illuminance)));
            }),
            Emit = m =>
            {
                m.Line($"var {m.Name} = new Iot.Device.Max44009.Max44009(new {Bridge}(esp32.I2C, {m.Int("address")}));");
                m.Assign("lux", $"Math.Round({m.Name}.Illuminance, 2)");
            }
        },

        new ModuleBlock
        {
            Type = Hmc5883l, DisplayName = "Compass (HMC5883L)", Category = "Sensors", Esp32Only = true, UsesI2c = true,
            Description = "Reads a magnetic compass (HMC5883L or its clone QMC5883L's older sibling) over I2C: the heading in degrees (0 = north) and the field on each axis.",
            Ports = [Trigger, Number("heading"), Number("x"), Number("y"), Number("z")],
            Properties = _ => [Address(0x1E, ("Default", 0x1E))],
            Run = r => Read(r, () =>
            {
                var sensor = Cached(r, "hmc5883l", r.Int("address"), bus => new Iot.Device.Hmc5883l.Hmc5883l(bus));
                var field = sensor.DirectionVector;
                return ModuleRun.Output(("heading", Round(sensor.Heading, 1)), ("x", Round(field.X, 3)), ("y", Round(field.Y, 3)), ("z", Round(field.Z, 3)));
            }),
            Emit = m =>
            {
                m.Line($"var {m.Name} = new Iot.Device.Hmc5883l.Hmc5883l(new {Bridge}(esp32.I2C, {m.Int("address")}));");
                m.Line($"var {m.Name}Field = {m.Name}.DirectionVector;");
                m.Assign("heading", $"Math.Round({m.Name}.Heading, 1)");
                m.Assign("x", $"Math.Round((double){m.Name}Field.X, 3)");
                m.Assign("y", $"Math.Round((double){m.Name}Field.Y, 3)");
                m.Assign("z", $"Math.Round((double){m.Name}Field.Z, 3)");
            }
        },

        new ModuleBlock
        {
            Type = Vl53L1X, DisplayName = "Laser Distance 4 m (VL53L1X)", Category = "Sensors", Esp32Only = true, UsesI2c = true,
            Description = "Measures distance with a VL53L1X time-of-flight laser over I2C, up to about 4 m. Output in centimetres and millimetres.",
            Ports = [Trigger, Number("distance"), Number("millimetres")],
            Properties = _ => [Address(0x29, ("Default", 0x29))],
            Run = r => Read(r, () =>
            {
                var sensor = Cached(r, "vl53l1x", r.Int("address"), bus =>
                {
                    var device = new Iot.Device.Vl53L1X.Vl53L1X(bus);
                    device.StartRanging();
                    return device;
                });
                var millimetres = sensor.GetDistance().Millimeters;
                return ModuleRun.Output(("distance", Round(millimetres / 10, 1)), ("millimetres", Round(millimetres, 0)));
            }),
            Emit = m =>
            {
                m.Line($"var {m.Name} = new Iot.Device.Vl53L1X.Vl53L1X(new {Bridge}(esp32.I2C, {m.Int("address")}));");
                m.Line($"{m.Name}.StartRanging();");
                m.Line($"double {m.Name}Mm = {m.Name}.GetDistance().Millimeters;");
                m.Assign("distance", $"Math.Round({m.Name}Mm / 10, 1)");
                m.Assign("millimetres", $"Math.Round({m.Name}Mm, 0)");
            }
        },

        // ------------------------------------------------------------ clocks
        Clock(Ds1307, "Clock (DS1307)", "Reads a DS1307 real-time clock over I2C: the date and time as text (yyyy-MM-dd HH:mm:ss). It keeps time with a coin cell when the board is off.", 0x68,
            "ds1307", bus => new Iot.Device.Rtc.Ds1307(bus), "Iot.Device.Rtc.Ds1307"),
        Clock(Pcf8563, "Clock (PCF8563)", "Reads a PCF8563 real-time clock over I2C: the date and time as text (yyyy-MM-dd HH:mm:ss). It keeps time with a coin cell when the board is off.", 0x51,
            "pcf8563", bus => new Iot.Device.Rtc.Pcf8563(bus), "Iot.Device.Rtc.Pcf8563"),

        // ------------------------------------------------------------ outputs and expanders
        new ModuleBlock
        {
            Type = SevenSegment, DisplayName = "Number Display (HT16K33)", Category = "Modules", Esp32Only = true, UsesI2c = true,
            Description = "Shows a number on a 4-digit 7-segment display driven by an HT16K33 over I2C (the Adafruit and many clone backpacks).",
            Ports = [Trigger, FlowPortDefinition.Input("value", FlowValueKind.Number, required: false), Done],
            Properties = _ =>
            [
                Address(0x70, ("Default", 0x70), ("A0 bridged", 0x71), ("A1 bridged", 0x72), ("A0 and A1 bridged", 0x73)),
                new FlowPropertyDefinition("value", FlowValueKind.Number, DefaultValue: 0),
                new FlowPropertyDefinition("brightness", FlowValueKind.Integer, DefaultValue: 8, IsAdvanced: true)
            ],
            Run = r => Read(r, () =>
            {
                var display = Cached(r, "ht16k33", r.Int("address"), bus => new Iot.Device.Display.Large4Digit7SegmentDisplay(bus));
                display.Brightness = (byte)Math.Clamp(r.Int("brightness"), 0, 15);
                display.Write(IotHelpers.Digits(r.InputOr<double>("value", "value")), 0);
                display.Flush();
                return ModuleRun.Output(("done", true));
            }),
            Emit = m =>
            {
                m.Line($"var {m.Name} = new Iot.Device.Display.Large4Digit7SegmentDisplay(new {Bridge}(esp32.I2C, {m.Int("address")})) {{ Brightness = {Math.Clamp(m.Int("brightness"), 0, 15)} }};");
                m.Line($"{m.Name}.Write(CodeBridge.IotBindings.IotHelpers.Digits({m.Input("value", ModuleCode.Literal(m.Double("value")))}), 0);");
                m.Line($"{m.Name}.Flush();");
                m.Assign("done", "true");
            }
        },

        new ModuleBlock
        {
            Type = ExpanderWrite, DisplayName = "Expander Write (PCF8574)", Category = "Modules", Esp32Only = true, UsesI2c = true,
            Description = "Sets the 8 pins of a PCF8574 I/O expander over I2C with one number (0 to 255, one bit per pin). Adds relays and LEDs without using the board's own pins.",
            Ports = [Trigger, FlowPortDefinition.Input("value", FlowValueKind.Number, required: false), Done],
            Properties = _ => [ExpanderAddress(), new FlowPropertyDefinition("value", FlowValueKind.Integer, DefaultValue: 255)],
            Run = r => Read(r, () =>
            {
                var expander = Cached(r, "pcf8574", r.Int("address"), bus => new Iot.Device.Pcx857x.Pcf8574(bus, -1, null, false));
                expander.WriteByte((byte)Math.Clamp((int)Math.Round(r.InputOr<double>("value", "value")), 0, 255));
                return ModuleRun.Output(("done", true));
            }),
            Emit = m =>
            {
                m.Line($"var {m.Name} = new Iot.Device.Pcx857x.Pcf8574(new {Bridge}(esp32.I2C, {m.Int("address")}), -1, null, false);");
                m.Line($"{m.Name}.WriteByte((byte)Math.Clamp((int)Math.Round({m.Input("value", ModuleCode.Literal(m.Double("value")))}), 0, 255));");
                m.Assign("done", "true");
            }
        },

        new ModuleBlock
        {
            Type = ExpanderRead, DisplayName = "Expander Read (PCF8574)", Category = "Modules", Esp32Only = true, UsesI2c = true,
            Description = "Reads the 8 pins of a PCF8574 I/O expander over I2C as one number (0 to 255, one bit per pin). A pin must have been written as 1 to be read as an input.",
            Ports = [Trigger, Number("value")],
            Properties = _ => [ExpanderAddress()],
            Run = r => Read(r, () =>
            {
                var expander = Cached(r, "pcf8574", r.Int("address"), bus => new Iot.Device.Pcx857x.Pcf8574(bus, -1, null, false));
                return ModuleRun.Output(("value", (double)expander.ReadByte()));
            }),
            Emit = m =>
            {
                m.Line($"var {m.Name} = new Iot.Device.Pcx857x.Pcf8574(new {Bridge}(esp32.I2C, {m.Int("address")}), -1, null, false);");
                m.Assign("value", $"(double){m.Name}.ReadByte()");
            }
        },

        new ModuleBlock
        {
            Type = Mpr121, DisplayName = "Touch Keys (MPR121)", Category = "Sensors", Esp32Only = true, UsesI2c = true,
            Description = "Reads a 12-key capacitive touch sensor (MPR121) over I2C: which keys are touched, as one number (bit 0 is key 0), and whether any is.",
            Ports = [Trigger, Number("keys"), FlowPortDefinition.Output("touched", FlowValueKind.Boolean)],
            Properties = _ => [Address(0x5A, ("ADDR to GND", 0x5A), ("ADDR to VCC", 0x5B), ("ADDR to SDA", 0x5C), ("ADDR to SCL", 0x5D))],
            Run = r => Read(r, () =>
            {
                var sensor = Cached(r, "mpr121", r.Int("address"), bus => new Iot.Device.Mpr121.Mpr121(bus, -1));
                var mask = IotHelpers.TouchMask(sensor.ReadChannelStatuses());
                return ModuleRun.Output(("keys", (double)mask), ("touched", mask != 0));
            }),
            Emit = m =>
            {
                m.Line($"var {m.Name} = new Iot.Device.Mpr121.Mpr121(new {Bridge}(esp32.I2C, {m.Int("address")}), -1);");
                m.Line($"var {m.Name}Mask = CodeBridge.IotBindings.IotHelpers.TouchMask({m.Name}.ReadChannelStatuses());");
                m.Assign("keys", $"(double){m.Name}Mask");
                m.Assign("touched", $"{m.Name}Mask != 0");
            }
        },

        // ------------------------------------------------------------ any chip
        new ModuleBlock
        {
            Type = I2cScan, DisplayName = "I2C Scan", Category = "Modules", Esp32Only = true, UsesI2c = true,
            Description = "Lists the chips that answer on the I2C bus, as addresses like 0x44, 0x76. The quickest way to see whether a sensor is wired and which address it has.",
            Ports = [Trigger, FlowPortDefinition.Output("devices", FlowValueKind.String), Number("count")],
            Properties = _ => [],
            Run = async r =>
            {
                var found = await r.Board.I2C.ScanAsync(r.Ct);
                return ModuleRun.Output(("devices", IotHelpers.Describe(found)), ("count", (double)found.Count));
            },
            Emit = m =>
            {
                m.Line($"var {m.Name}Found = await esp32.I2C.ScanAsync(ct);");
                m.Assign("devices", $"CodeBridge.IotBindings.IotHelpers.Describe({m.Name}Found)");
                m.Assign("count", $"(double){m.Name}Found.Count");
            }
        },

        new ModuleBlock
        {
            Type = I2cRead, DisplayName = "I2C Read Register", Category = "Modules", Esp32Only = true, UsesI2c = true,
            Description = "Reads 1 to 4 bytes from a register of any I2C chip and gives them as one number. For a chip that has no block yet: the registers are in its datasheet.",
            Ports = [Trigger, Number("value"), FlowPortDefinition.Output("hex", FlowValueKind.String)],
            Properties = _ =>
            [
                new FlowPropertyDefinition("address", FlowValueKind.Integer, Required: true, DefaultValue: 0x40),
                new FlowPropertyDefinition("register", FlowValueKind.Integer, Required: true, DefaultValue: 0),
                new FlowPropertyDefinition("length", FlowValueKind.Integer, DefaultValue: 1, Options: Choices(("1 byte", 1, "8 bits."), ("2 bytes", 2, "16 bits."), ("3 bytes", 3, "24 bits."), ("4 bytes", 4, "32 bits."))),
                new FlowPropertyDefinition("bigEndian", FlowValueKind.Boolean, DefaultValue: true, Options: Choices(("Most significant byte first", true, "The usual order in datasheets."), ("Least significant byte first", false, "Little endian."))),
                new FlowPropertyDefinition("signed", FlowValueKind.Boolean, DefaultValue: false, Options: YesNo, IsAdvanced: true)
            ],
            Run = async r =>
            {
                var address = Math.Clamp(r.Int("address"), 0, 0x7F);
                var length = Math.Clamp(r.Int("length"), 1, 4);
                var bytes = IotHelpers.Answered(await r.Board.I2C.ReadRegisterAsync((byte)address, (byte)Math.Clamp(r.Int("register"), 0, 255), length, r.Ct), length, address);
                return ModuleRun.Output(("value", IotHelpers.ToNumber(bytes, r.Bool("bigEndian"), r.Bool("signed"))), ("hex", Convert.ToHexString(bytes)));
            },
            Emit = m =>
            {
                m.Line($"var {m.Name}Bytes = CodeBridge.IotBindings.IotHelpers.Answered(await esp32.I2C.ReadRegisterAsync((byte){Math.Clamp(m.Int("address"), 0, 0x7F)}, (byte){Math.Clamp(m.Int("register"), 0, 255)}, {Math.Clamp(m.Int("length"), 1, 4)}, ct), {Math.Clamp(m.Int("length"), 1, 4)}, {Math.Clamp(m.Int("address"), 0, 0x7F)});");
                m.Assign("value", $"CodeBridge.IotBindings.IotHelpers.ToNumber({m.Name}Bytes, {ModuleCode.Literal(m.Bool("bigEndian"))}, {ModuleCode.Literal(m.Bool("signed"))})");
                m.Assign("hex", $"Convert.ToHexString({m.Name}Bytes)");
            }
        },

        new ModuleBlock
        {
            Type = I2cWrite, DisplayName = "I2C Write Register", Category = "Modules", Esp32Only = true, UsesI2c = true,
            Description = "Writes a number (1 to 4 bytes) to a register of any I2C chip: set a mode, start a measurement, change a setting. The registers are in the chip's datasheet.",
            Ports = [Trigger, FlowPortDefinition.Input("value", FlowValueKind.Number, required: false), Done],
            Properties = _ =>
            [
                new FlowPropertyDefinition("address", FlowValueKind.Integer, Required: true, DefaultValue: 0x40),
                new FlowPropertyDefinition("register", FlowValueKind.Integer, Required: true, DefaultValue: 0),
                new FlowPropertyDefinition("value", FlowValueKind.Integer, DefaultValue: 0),
                new FlowPropertyDefinition("length", FlowValueKind.Integer, DefaultValue: 1, Options: Choices(("1 byte", 1, "8 bits."), ("2 bytes", 2, "16 bits."), ("3 bytes", 3, "24 bits."), ("4 bytes", 4, "32 bits."))),
                new FlowPropertyDefinition("bigEndian", FlowValueKind.Boolean, DefaultValue: true, Options: Choices(("Most significant byte first", true, "The usual order in datasheets."), ("Least significant byte first", false, "Little endian.")))
            ],
            Run = async r =>
            {
                var bytes = IotHelpers.ToBytes(r.InputOr<double>("value", "value"), Math.Clamp(r.Int("length"), 1, 4), r.Bool("bigEndian"));
                await r.Board.I2C.WriteRegisterAsync((byte)Math.Clamp(r.Int("address"), 0, 0x7F), (byte)Math.Clamp(r.Int("register"), 0, 255), bytes, r.Ct);
                return ModuleRun.Output(("done", true));
            },
            Emit = m =>
            {
                m.Line($"await esp32.I2C.WriteRegisterAsync((byte){Math.Clamp(m.Int("address"), 0, 0x7F)}, (byte){Math.Clamp(m.Int("register"), 0, 255)}, CodeBridge.IotBindings.IotHelpers.ToBytes({m.Input("value", ModuleCode.Literal(m.Double("value")))}, {Math.Clamp(m.Int("length"), 1, 4)}, {ModuleCode.Literal(m.Bool("bigEndian"))}), ct);");
                m.Assign("done", "true");
            }
        }
    ];

    private static readonly IReadOnlyList<FlowPropertyOption> YesNo = Choices(("Yes", true, "On."), ("No", false, "Off."));

    private static FlowPropertyDefinition ExpanderAddress() =>
        Address(0x20, ("A0-A2 to GND", 0x20), ("A0 to VCC", 0x21), ("A1 to VCC", 0x22), ("A0 and A1 to VCC", 0x23), ("A2 to VCC", 0x24), ("PCF8574A: all to GND", 0x38), ("PCF8574A: A0 to VCC", 0x39));

    /// <summary>The three real-time clocks read the same way: the date and time as text.</summary>
    private static ModuleBlock Clock(string type, string name, string description, int address, string kind, Func<BridgeI2cDevice, Iot.Device.Rtc.RtcBase> create, string className) => new()
    {
        Type = type, DisplayName = name, Category = "Sensors", Esp32Only = true, UsesI2c = true,
        Description = description,
        Ports = [Trigger, FlowPortDefinition.Output("time", FlowValueKind.String)],
        Properties = _ => [Address(address, ("Default", address))],
        Run = r => Read(r, () =>
        {
            var clock = Cached(r, kind, r.Int("address"), create);
            return ModuleRun.Output(("time", clock.DateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
        }),
        Emit = m =>
        {
            m.Line($"var {m.Name} = new {className}(new {Bridge}(esp32.I2C, {m.Int("address")}));");
            m.Assign("time", $"{m.Name}.DateTime.ToString(\"yyyy-MM-dd HH:mm:ss\", System.Globalization.CultureInfo.InvariantCulture)");
        }
    };
}
