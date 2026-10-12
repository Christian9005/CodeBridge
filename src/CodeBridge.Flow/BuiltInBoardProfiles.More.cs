using CodeBridge.Core.Enums;

namespace CodeBridge.Flow;

/// <summary>More boards: the newer ESP32 chips and the other AVR Arduinos. They run the same CodeBridge protocol as the originals.</summary>
public static partial class BuiltInBoardProfiles
{
    private static readonly BoardRuntimeCapabilities Esp32Runtime = new(
        MaxDigitalSampleRateHz: 10000,
        MaxAnalogSampleRateHz: 5000,
        MaxStreamChannels: 8,
        MaxBufferCapacity: 4096,
        SupportsInterrupts: true,
        SupportsHardwareTimers: true,
        MinimumTimerIntervalMicroseconds: 100);

    private static readonly BoardRuntimeCapabilities AvrRuntime = new(
        MaxDigitalSampleRateHz: 1000,
        MaxAnalogSampleRateHz: 200,
        MaxStreamChannels: 4,
        MaxBufferCapacity: 512,
        SupportsInterrupts: true,
        SupportsHardwareTimers: false,
        MinimumTimerIntervalMicroseconds: 1000);

    // ---------------------------------------------------------------- ESP32-S3

    /// <summary>ESP32-S3-DevKitC-1. Use its UART USB-C port (the one next to the antenna side labelled UART) for CodeBridge.</summary>
    public static BoardProfile Esp32S3DevKit { get; } = new("esp32-s3-devkit", "ESP32-S3 DevKit", BuildEsp32S3Pins())
    {
        Family = BoardFamily.ESP32,
        FirmwareProject = "esp32-bridge",
        FlashChip = "esp32s3",
        PrebuiltFolder = "esp32s3",
        BootloaderOffset = 0,
        Runtime = Esp32Runtime
    };

    private static IReadOnlyList<BoardPinDefinition> BuildEsp32S3Pins()
    {
        const PinCapability io = PinCapability.DigitalRead | PinCapability.DigitalWrite | PinCapability.Pwm | PinCapability.Interrupt;
        var pins = new List<BoardPinDefinition>
        {
            new(0, "BOOT", "Boot button / strapping pin. Use only when you know the boot circuit state.", io | PinCapability.BootStrap)
        };

        for (var n = 1; n <= 10; n++)
            pins.Add(new(n, "ADC1", "General purpose GPIO with ADC1 (reliable analog input, also with Wi-Fi on).", io | PinCapability.AnalogRead | PinCapability.Adc1, MaxDigitalSampleRateHz: 10000, MaxAnalogSampleRateHz: 5000));

        for (var n = 11; n <= 18; n++)
            pins.Add(new(n, "ADC2", "General purpose GPIO with ADC2 (analog reads are unreliable while Wi-Fi is on).", io | PinCapability.AnalogRead | PinCapability.Adc2, MaxDigitalSampleRateHz: 10000, MaxAnalogSampleRateHz: 2000));

        pins.Add(new(19, "USB D-", "Native USB D-. Avoid unless you do not use the native USB port.", PinCapability.DigitalRead | PinCapability.DigitalWrite | PinCapability.Reserved));
        pins.Add(new(20, "USB D+", "Native USB D+. Avoid unless you do not use the native USB port.", PinCapability.DigitalRead | PinCapability.DigitalWrite | PinCapability.Reserved));
        pins.Add(new(21, "GPIO", "General purpose GPIO.", io, MaxDigitalSampleRateHz: 10000));

        foreach (var n in new[] { 35, 36, 37 })
            pins.Add(new(n, "PSRAM", "Used by the octal PSRAM of the N8R8/N16R8 modules. Avoid.", PinCapability.DigitalRead | PinCapability.DigitalWrite | PinCapability.Reserved));

        foreach (var n in new[] { 38, 39, 40, 41, 42 })
            pins.Add(new(n, "GPIO", "General purpose GPIO (39-42 are the default JTAG pins).", io, MaxDigitalSampleRateHz: 10000));

        pins.Add(new(43, "UART TX0", "Programming/debug serial TX. Avoid while using USB serial.", PinCapability.DigitalWrite | PinCapability.UartTx | PinCapability.Reserved));
        pins.Add(new(44, "UART RX0", "Programming/debug serial RX. Avoid while using USB serial.", PinCapability.DigitalRead | PinCapability.UartRx | PinCapability.Reserved));
        pins.Add(new(45, "STRAP", "Strapping pin (flash voltage). Avoid driving it at boot.", io | PinCapability.BootStrap));
        pins.Add(new(46, "STRAP", "Strapping pin (boot mode / ROM messages). Avoid driving it at boot.", io | PinCapability.BootStrap));
        pins.Add(new(47, "GPIO", "General purpose GPIO.", io, MaxDigitalSampleRateHz: 10000));
        pins.Add(new(48, "RGB LED", "On-board RGB LED on many DevKitC-1 boards (GPIO 38 on newer revisions).", io, MaxDigitalSampleRateHz: 10000));

        // Servo-friendly pins: PWM capable, not strapping, not shared with USB/UART/PSRAM.
        var servo = new HashSet<int> { 4, 5, 6, 7, 15, 16, 17, 18, 21, 38, 39, 40, 41, 42 };
        return pins.Select(pin => servo.Contains(pin.Number) ? pin with { Capabilities = pin.Capabilities | PinCapability.ServoRecommended } : pin).ToArray();
    }

    // ---------------------------------------------------------------- ESP32-C3

    /// <summary>ESP32-C3-DevKitM-1.</summary>
    public static BoardProfile Esp32C3DevKit { get; } = new("esp32-c3-devkit", "ESP32-C3 DevKit", BuildEsp32C3Pins())
    {
        Family = BoardFamily.ESP32,
        FirmwareProject = "esp32-bridge",
        FlashChip = "esp32c3",
        PrebuiltFolder = "esp32c3",
        BootloaderOffset = 0,
        Runtime = Esp32Runtime with { MaxStreamChannels = 4, SupportsHardwareTimers = true }
    };

    private static IReadOnlyList<BoardPinDefinition> BuildEsp32C3Pins()
    {
        const PinCapability io = PinCapability.DigitalRead | PinCapability.DigitalWrite | PinCapability.Pwm | PinCapability.Interrupt;
        var pins = new List<BoardPinDefinition>
        {
            new(0, "ADC1", "General purpose GPIO with ADC1.", io | PinCapability.AnalogRead | PinCapability.Adc1 | PinCapability.ServoRecommended, MaxDigitalSampleRateHz: 10000, MaxAnalogSampleRateHz: 5000),
            new(1, "ADC1", "General purpose GPIO with ADC1.", io | PinCapability.AnalogRead | PinCapability.Adc1 | PinCapability.ServoRecommended, MaxDigitalSampleRateHz: 10000, MaxAnalogSampleRateHz: 5000),
            new(2, "ADC1 STRAP", "ADC1 pin that is also a strapping pin. Avoid driving it at boot.", io | PinCapability.AnalogRead | PinCapability.Adc1 | PinCapability.BootStrap, MaxDigitalSampleRateHz: 10000, MaxAnalogSampleRateHz: 5000),
            new(3, "ADC1", "General purpose GPIO with ADC1.", io | PinCapability.AnalogRead | PinCapability.Adc1 | PinCapability.ServoRecommended, MaxDigitalSampleRateHz: 10000, MaxAnalogSampleRateHz: 5000),
            new(4, "ADC1", "General purpose GPIO with ADC1.", io | PinCapability.AnalogRead | PinCapability.Adc1 | PinCapability.ServoRecommended, MaxDigitalSampleRateHz: 10000, MaxAnalogSampleRateHz: 5000),
            new(5, "ADC2", "ADC2 pin (analog reads are unreliable while Wi-Fi is on).", io | PinCapability.AnalogRead | PinCapability.Adc2, MaxDigitalSampleRateHz: 10000, MaxAnalogSampleRateHz: 2000),
            new(6, "GPIO", "General purpose GPIO.", io | PinCapability.ServoRecommended, MaxDigitalSampleRateHz: 10000),
            new(7, "GPIO", "General purpose GPIO.", io | PinCapability.ServoRecommended, MaxDigitalSampleRateHz: 10000),
            new(8, "RGB LED STRAP", "On-board RGB LED, I2C SDA by default and a strapping pin.", io | PinCapability.BootStrap | PinCapability.I2cSda, MaxDigitalSampleRateHz: 10000),
            new(9, "BOOT", "Boot button / strapping pin, I2C SCL by default.", io | PinCapability.BootStrap | PinCapability.I2cScl, MaxDigitalSampleRateHz: 10000),
            new(10, "GPIO", "General purpose GPIO.", io | PinCapability.ServoRecommended, MaxDigitalSampleRateHz: 10000),
            new(18, "USB D-", "Native USB D-. Avoid unless you do not use the native USB port.", PinCapability.DigitalRead | PinCapability.DigitalWrite | PinCapability.Reserved),
            new(19, "USB D+", "Native USB D+. Avoid unless you do not use the native USB port.", PinCapability.DigitalRead | PinCapability.DigitalWrite | PinCapability.Reserved),
            new(20, "UART RX0", "Programming/debug serial RX. Avoid while using USB serial.", PinCapability.DigitalRead | PinCapability.UartRx | PinCapability.Reserved),
            new(21, "UART TX0", "Programming/debug serial TX. Avoid while using USB serial.", PinCapability.DigitalWrite | PinCapability.UartTx | PinCapability.Reserved)
        };

        return pins;
    }

    // ---------------------------------------------------------------- Arduino Nano / Mega

    /// <summary>Arduino Nano (ATmega328P, new bootloader): an Uno in a small body, plus the analog-only pins A6 and A7.</summary>
    private static BoardProfile? _nano;
    public static BoardProfile ArduinoNano => _nano ??= NanoProfile("arduino-nano", "Arduino Nano", "arduino:avr:nano:cpu=atmega328");

    /// <summary>Arduino Nano clones and boards sold before 2018 use the old bootloader (slower upload speed).</summary>
    private static BoardProfile? _nanoOld;
    public static BoardProfile ArduinoNanoOldBootloader => _nanoOld ??= NanoProfile("arduino-nano-old", "Arduino Nano (old bootloader)", "arduino:avr:nano:cpu=atmega328old");

    private static BoardProfile NanoProfile(string id, string name, string fqbn)
    {
        var pins = ArduinoUno.Pins.Concat(new BoardPinDefinition[]
        {
            new(20, "A6", "Analog-only input A6 (it cannot be used as a digital pin).", PinCapability.AnalogRead | PinCapability.InputOnly, DisplayLabel: "A6 - Analog 6", MaxAnalogSampleRateHz: 200),
            new(21, "A7", "Analog-only input A7 (it cannot be used as a digital pin).", PinCapability.AnalogRead | PinCapability.InputOnly, DisplayLabel: "A7 - Analog 7", MaxAnalogSampleRateHz: 200)
        }).ToArray();

        return new BoardProfile(id, name, pins)
        {
            Family = BoardFamily.Arduino,
            FirmwareProject = "arduino-uno-bridge",
            Fqbn = fqbn,
            BuiltInLedPin = 13,
            AnalogMaxValue = 1023,
            Runtime = AvrRuntime
        };
    }

    /// <summary>Arduino Mega 2560: 54 digital pins (15 PWM), 16 analog inputs and four hardware serial ports.</summary>
    public static BoardProfile ArduinoMega { get; } = new("arduino-mega", "Arduino Mega 2560", BuildMegaPins())
    {
        Family = BoardFamily.Arduino,
        FirmwareProject = "arduino-uno-bridge",
        Fqbn = "arduino:avr:mega:cpu=atmega2560",
        BuiltInLedPin = 13,
        AnalogMaxValue = 1023,
        Runtime = AvrRuntime
    };

    private static IReadOnlyList<BoardPinDefinition> BuildMegaPins()
    {
        const PinCapability digital = PinCapability.DigitalRead | PinCapability.DigitalWrite;
        var pwm = new HashSet<int> { 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 44, 45, 46 };
        var interrupts = new HashSet<int> { 2, 3, 18, 19, 20, 21 };
        var servo = new HashSet<int> { 7, 8, 9, 10, 11, 12 };
        var pins = new List<BoardPinDefinition>
        {
            new(0, "UART RX0", "USB serial receive pin. Avoid while uploading sketches or using Serial Monitor.", digital | PinCapability.UartRx | PinCapability.Reserved, DisplayLabel: "D0 - UART RX"),
            new(1, "UART TX0", "USB serial transmit pin. Avoid while uploading sketches or using Serial Monitor.", digital | PinCapability.UartTx | PinCapability.Reserved, DisplayLabel: "D1 - UART TX")
        };

        for (var n = 2; n <= 53; n++)
        {
            var caps = digital;
            var name = "Digital";
            if (pwm.Contains(n)) { caps |= PinCapability.Pwm; name = "PWM"; }
            if (interrupts.Contains(n)) caps |= PinCapability.Interrupt;
            if (servo.Contains(n)) caps |= PinCapability.ServoRecommended;
            var description = "General purpose digital I/O pin" + (pwm.Contains(n) ? " with PWM output" : string.Empty) + (interrupts.Contains(n) ? " and external interrupt" : string.Empty) + ".";
            var label = $"D{n} - {name}";

            switch (n)
            {
                case 13: description = "Built-in LED pin with PWM output. Useful for blink tests."; label = "D13 - LED / PWM"; break;
                case 20: caps |= PinCapability.I2cSda; description = "I2C SDA and external interrupt."; label = "D20 - I2C SDA / Interrupt"; break;
                case 21: caps |= PinCapability.I2cScl; description = "I2C SCL and external interrupt."; label = "D21 - I2C SCL / Interrupt"; break;
                case 50: caps |= PinCapability.SpiMiso; description = "SPI MISO."; label = "D50 - SPI MISO"; break;
                case 51: caps |= PinCapability.SpiMosi; description = "SPI MOSI."; label = "D51 - SPI MOSI"; break;
                case 52: caps |= PinCapability.SpiSck; description = "SPI SCK."; label = "D52 - SPI SCK"; break;
                case 53: caps |= PinCapability.SpiCs; description = "SPI chip select (SS). Keep it an output for SPI to work."; label = "D53 - SPI CS"; break;
            }

            pins.Add(new(n, name, description, caps, DisplayLabel: label));
        }

        for (var i = 0; i < 16; i++)
            pins.Add(new(54 + i, $"A{i}", $"Analog input A{i}. Can also be used as digital pin {54 + i}.", digital | PinCapability.AnalogRead, DisplayLabel: $"A{i} - Analog {i}", MaxAnalogSampleRateHz: 200));

        return pins;
    }
}
