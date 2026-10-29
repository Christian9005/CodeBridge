# .NET IoT drivers on a board (`CodeBridge.IotBindings`)

Microsoft's [.NET IoT bindings](https://github.com/dotnet/iot) (`Iot.Device.Bindings`) are drivers for hundreds of chips, written for a computer that has its own I2C pins. `CodeBridge.IotBindings` makes **a CodeBridge board the I2C bus** of those drivers, so they run from your PC over USB or Wi-Fi without touching the firmware, and puts the most useful ones in the flow editor as blocks.

```csharp
using var board = new ESP32Board(new SerialTransport("COM3"));
await board.ConnectAsync();

using var sensor = new Iot.Device.Lm75.Lm75(new BridgeI2cDevice(board.I2C, 0x48));
Console.WriteLine(sensor.Temperature.DegreesCelsius);
```

Any driver of the package whose constructor takes an `I2cDevice` works this way (119 of them in version 4.2: sensors, converters, expanders, clocks, displays...). Each read and write travels to the board as one I2C command.

## The blocks

Call `IotBindingBlocks.Register()` once at start-up (`FlowHost` and `codebridge-web` do). The blocks then show up in the toolbox of ESP32, S3, C3, S2 and XIAO boards, run, validate and export to C# like the built-in ones.

| Block | Chip | Gives |
|---|---|---|
| Temp + Humidity (SHT3x) | SHT30 / 31 / 35 | temperature, humidity |
| Temp + Humidity (SHT4x) | SHT40 / 41 / 45 | temperature, humidity |
| Temp + Humidity (AHT20) | AHT20 / AHT21 | temperature, humidity |
| Precision Temp (MCP9808) | MCP9808 | temperature |
| Temperature (LM75) | LM75, TMP75, PCT2075 | temperature |
| Infrared Temp (MLX90614) | MLX90614 | object and ambient temperature |
| Pressure (BMP280) | BMP280 | pressure, temperature, altitude |
| Weather + Altitude (BME280) | BME280 | temperature, humidity, pressure, altitude |
| CO₂ (SCD4x) | SCD40 / SCD41 | CO₂, temperature, humidity |
| Light (MAX44009) | MAX44009 | lux |
| Compass (HMC5883L) | HMC5883L | heading, x, y, z |
| Laser Distance (VL53L0X) | VL53L0X | distance up to about 2 m |
| Laser Distance 4 m (VL53L1X) | VL53L1X | distance up to about 4 m |
| Analog 16-bit (ADS1115) | ADS1115 | voltage, raw (4 channels) |
| Touch Keys (MPR121) | MPR121 | keys (one bit each), touched |
| Clock (DS3231 / DS1307 / PCF8563) | the three RTCs | date and time as text |
| PWM Driver (PCA9685) | PCA9685 | 16 PWM channels out |
| Number Display (HT16K33) | 4-digit 7-segment backpack | shows a number |
| Expander Write / Read (PCF8574) | PCF8574 | 8 extra pins as one number |
| **I2C Scan** | any | the addresses that answer |
| **I2C Read Register** | any | 1 to 4 bytes of a register, as a number |
| **I2C Write Register** | any | writes a number to a register |

The last three work with **any chip**, with or without a block: scan to find the address, then read and write registers as the datasheet says.

## Add a device

A device is one `ModuleBlock` of about twenty lines in `IotBindingBlocks` (or in your own package, registered with `ModuleBlocks.Register`):

```csharp
new ModuleBlock
{
    Type = "iot.lm75", DisplayName = "Temperature (LM75)", Category = "Sensors", Esp32Only = true, UsesI2c = true,
    Description = "Reads an LM75 over I2C, in °C.",
    Ports = [Trigger, Number("temperature")],
    Properties = _ => [Address(0x48, ("A0-A2 to GND", 0x48), ("A0 to VCC", 0x49))],
    Run = r => Read(r, () =>                           // runs on a pool thread: the driver is synchronous
    {
        var sensor = Cached(r, "lm75", r.Int("address"), bus => new Iot.Device.Lm75.Lm75(bus));   // one driver per board and address
        return ModuleRun.Output(("temperature", Round(sensor.Temperature.DegreesCelsius)));
    }),
    Emit = m =>                                         // the C# a flow is exported to
    {
        m.Line($"var {m.Name} = new Iot.Device.Lm75.Lm75(new {Bridge}(esp32.I2C, {m.Int("address")}));");
        m.Assign("temperature", $"Math.Round({m.Name}.Temperature.DegreesCelsius, 2)");
    }
}
```

Then add its help text to `BlockHelp.Modules.cs` in the Visual Studio extension (a test fails if a catalog block has none) and regenerate the catalogs (`CodeBridge.FlowHost catalog --out src/CodeBridge.VisualStudio/Assets`). A virtual chip in the tests (`VirtualChips.cs`) lets the driver run in the simulator: `SimulatedTransport.AddI2cDevice(address, chip)`.

## Test without hardware

The simulator accepts virtual I2C chips. The tests give each driver a chip that answers like the real one (with its checksums, registers and calibration from the datasheet), so the real driver code runs, and the exported C# is compiled and must send the same commands as the flow.

## What is and is not verified

- Verified in the simulator, with the real drivers: SHT3x, SHT4x, MCP9808, MLX90614, BMP280 (datasheet example), LM75, MAX44009, ADS1115, three clocks, PCA9685, HT16K33, PCF8574 and the three generic blocks.
- Verified for validity and export only (their start-up or protocol is long, no virtual chip yet): VL53L0X, VL53L1X, AHT20, SCD4x, HMC5883L and MPR121.
- **Tried on a real chip** (ESP32 DevKit, firmware 0.9.1, a BME280 at 0x76): the I2C Scan and I2C Read Register blocks, and the real `Bme280` driver of the bindings through `BridgeI2cDevice`, which read 19.4 °C, 70.3 % and 716.1 hPa, the same as the built-in Weather (BME280) block. A transfer takes about 4 ms and the driver starts in about 0.2 s. The BMP280 block on that chip says it is a BME280 and which block to use. The Weather + Altitude (BME280) block is tested against the registers of that chip (calibration and raw reading captured from it) and the datasheet formulas.
- **Not tried on a real chip.** The other drivers: the virtual chips follow the datasheets, but a real sensor can differ. Try a block with the chip in your hand before relying on it, and tell us what you find.

## Limits

- I2C only, and on the ESP32 family (the Arduino firmware has no generic I2C commands yet). The pins are the board's I2C pins (SDA 21 and SCL 22 on the ESP32 DevKit); the editor reminds you to check them on other boards.
- The drivers are synchronous and each I2C transfer is one command to the board, a few milliseconds over USB. A driver that talks a lot when it starts (the laser sensors) is kept for the next reads, so only the first read is slow.
- A driver that needs a GPIO pin of its own (the CCS811 wake pin, the MFRC522 reset) or other I2C buses (the TCA9548A multiplexer) is not covered: use its pins through the board's GPIO blocks, or the SDK.
