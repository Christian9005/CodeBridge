namespace CodeBridge.Flow;

/// <summary>
/// Known board profiles used by designers to present friendly pin selectors.
/// </summary>
public static partial class BuiltInBoardProfiles
{
    private const PinCapability Digital = PinCapability.DigitalRead | PinCapability.DigitalWrite;
    private const PinCapability AnalogDigital = Digital | PinCapability.AnalogRead;

    public static BoardProfile Esp32DevKit { get; } = new(
        "esp32-devkit",
        "ESP32 DevKit",
        [
            new(0, "BOOT", "Boot strap pin. Use only when you know the boot circuit state.", Digital | PinCapability.Pwm | PinCapability.BootStrap),
            new(1, "UART TX0", "Programming/debug serial TX pin. Avoid for normal GPIO while using USB serial.", PinCapability.DigitalWrite | PinCapability.UartTx | PinCapability.Reserved),
            new(2, "LED", "On-board LED on many ESP32 dev kits. Also a boot strap pin.", Digital | PinCapability.Pwm | PinCapability.BootStrap),
            new(3, "UART RX0", "Programming/debug serial RX pin. Avoid for normal GPIO while using USB serial.", PinCapability.DigitalRead | PinCapability.UartRx | PinCapability.Reserved),
            new(4, "GPIO", "General purpose GPIO. Shares ADC2 with Wi-Fi, so prefer another pin for analog readings.", Digital | PinCapability.AnalogRead | PinCapability.Pwm | PinCapability.Interrupt | PinCapability.Adc2, MaxDigitalSampleRateHz: 10000, MaxAnalogSampleRateHz: 2000),
            new(5, "GPIO", "General purpose GPIO. Also a boot strap pin on many boards.", Digital | PinCapability.Pwm | PinCapability.BootStrap | PinCapability.Interrupt, MaxDigitalSampleRateHz: 10000),
            new(12, "GPIO", "ADC2 pin and boot strap pin. Avoid if boot behavior becomes unstable.", Digital | PinCapability.AnalogRead | PinCapability.Pwm | PinCapability.BootStrap | PinCapability.Adc2),
            new(13, "GPIO", "General purpose GPIO with ADC2/PWM support. Recommended for servo/PWM output.", Digital | PinCapability.AnalogRead | PinCapability.Pwm | PinCapability.ServoRecommended | PinCapability.Interrupt | PinCapability.Adc2, MaxDigitalSampleRateHz: 10000, MaxAnalogSampleRateHz: 2000),
            new(14, "GPIO", "General purpose GPIO with ADC2/PWM support. Recommended for servo/PWM output.", Digital | PinCapability.AnalogRead | PinCapability.Pwm | PinCapability.ServoRecommended | PinCapability.Interrupt | PinCapability.Adc2, MaxDigitalSampleRateHz: 10000, MaxAnalogSampleRateHz: 2000),
            new(15, "GPIO", "ADC2 pin and boot strap pin. Avoid if boot behavior becomes unstable.", Digital | PinCapability.AnalogRead | PinCapability.Pwm | PinCapability.BootStrap | PinCapability.Adc2),
            new(16, "GPIO", "General purpose digital GPIO. Recommended for servo/PWM output.", Digital | PinCapability.Pwm | PinCapability.ServoRecommended | PinCapability.Interrupt, MaxDigitalSampleRateHz: 10000),
            new(17, "GPIO", "General purpose digital GPIO. Recommended for servo/PWM output.", Digital | PinCapability.Pwm | PinCapability.ServoRecommended | PinCapability.Interrupt, MaxDigitalSampleRateHz: 10000),
            new(18, "SPI SCK", "General purpose GPIO, often used as SPI clock. Works for servo/PWM when SPI is not used.", Digital | PinCapability.Pwm | PinCapability.ServoRecommended | PinCapability.Interrupt | PinCapability.SpiSck, MaxDigitalSampleRateHz: 10000),
            new(19, "SPI MISO", "General purpose GPIO, often used as SPI MISO. Works for servo/PWM when SPI is not used.", Digital | PinCapability.Pwm | PinCapability.ServoRecommended | PinCapability.Interrupt | PinCapability.SpiMiso, MaxDigitalSampleRateHz: 10000),
            new(21, "I2C SDA", "General purpose GPIO, commonly used as I2C SDA. Works for servo/PWM when I2C is not used.", Digital | PinCapability.Pwm | PinCapability.ServoRecommended | PinCapability.Interrupt | PinCapability.I2cSda, MaxDigitalSampleRateHz: 10000),
            new(22, "I2C SCL", "General purpose GPIO, commonly used as I2C SCL. Works for servo/PWM when I2C is not used.", Digital | PinCapability.Pwm | PinCapability.ServoRecommended | PinCapability.Interrupt | PinCapability.I2cScl, MaxDigitalSampleRateHz: 10000),
            new(23, "SPI MOSI", "General purpose GPIO, often used as SPI MOSI. Works for servo/PWM when SPI is not used.", Digital | PinCapability.Pwm | PinCapability.ServoRecommended | PinCapability.Interrupt | PinCapability.SpiMosi, MaxDigitalSampleRateHz: 10000),
            new(25, "DAC1", "GPIO with DAC, ADC2 and PWM support. Recommended for servo/PWM output.", Digital | PinCapability.AnalogRead | PinCapability.Pwm | PinCapability.ServoRecommended | PinCapability.Interrupt | PinCapability.Adc2, MaxDigitalSampleRateHz: 10000, MaxAnalogSampleRateHz: 2000),
            new(26, "DAC2", "GPIO with DAC, ADC2 and PWM support. Recommended for servo/PWM output.", Digital | PinCapability.AnalogRead | PinCapability.Pwm | PinCapability.ServoRecommended | PinCapability.Interrupt | PinCapability.Adc2, MaxDigitalSampleRateHz: 10000, MaxAnalogSampleRateHz: 2000),
            new(27, "GPIO", "General purpose GPIO with ADC2/PWM support. Recommended for servo/PWM output.", Digital | PinCapability.AnalogRead | PinCapability.Pwm | PinCapability.ServoRecommended | PinCapability.Interrupt | PinCapability.Adc2, MaxDigitalSampleRateHz: 10000, MaxAnalogSampleRateHz: 2000),
            new(32, "ADC1", "GPIO with ADC1 and PWM support. Good analog input and servo/PWM candidate.", Digital | PinCapability.AnalogRead | PinCapability.Pwm | PinCapability.ServoRecommended | PinCapability.Interrupt | PinCapability.Adc1, MaxDigitalSampleRateHz: 10000, MaxAnalogSampleRateHz: 5000),
            new(33, "ADC1", "GPIO with ADC1 and PWM support. Good analog input and servo/PWM candidate.", Digital | PinCapability.AnalogRead | PinCapability.Pwm | PinCapability.ServoRecommended | PinCapability.Interrupt | PinCapability.Adc1, MaxDigitalSampleRateHz: 10000, MaxAnalogSampleRateHz: 5000),
            new(34, "ADC1", "Input-only ADC1 pin.", PinCapability.DigitalRead | PinCapability.AnalogRead | PinCapability.InputOnly | PinCapability.Interrupt | PinCapability.Adc1, MaxDigitalSampleRateHz: 10000, MaxAnalogSampleRateHz: 5000),
            new(35, "ADC1", "Input-only ADC1 pin.", PinCapability.DigitalRead | PinCapability.AnalogRead | PinCapability.InputOnly | PinCapability.Interrupt | PinCapability.Adc1, MaxDigitalSampleRateHz: 10000, MaxAnalogSampleRateHz: 5000),
            new(36, "ADC1 VP", "Input-only ADC1 pin.", PinCapability.DigitalRead | PinCapability.AnalogRead | PinCapability.InputOnly | PinCapability.Interrupt | PinCapability.Adc1, MaxDigitalSampleRateHz: 10000, MaxAnalogSampleRateHz: 5000),
            new(39, "ADC1 VN", "Input-only ADC1 pin.", PinCapability.DigitalRead | PinCapability.AnalogRead | PinCapability.InputOnly | PinCapability.Interrupt | PinCapability.Adc1, MaxDigitalSampleRateHz: 10000, MaxAnalogSampleRateHz: 5000)
        ])
    {
        FirmwareProject = "esp32-bridge",
        BuiltInLedPin = 2,
        FlashChip = "esp32",
        PrebuiltFolder = "esp32",
        BootloaderOffset = 0x1000,
        Runtime = new BoardRuntimeCapabilities(
            MaxDigitalSampleRateHz: 10000,
            MaxAnalogSampleRateHz: 5000,
            MaxStreamChannels: 8,
            MaxBufferCapacity: 4096,
            SupportsInterrupts: true,
            SupportsHardwareTimers: true,
            MinimumTimerIntervalMicroseconds: 100)
    };

    public static BoardProfile ArduinoUno { get; } = new(
        "arduino-uno",
        "Arduino Uno",
        [
            new(0, "UART RX", "USB serial receive pin. Avoid while uploading sketches or using Serial Monitor.", Digital | PinCapability.UartRx | PinCapability.Reserved, DisplayLabel: "D0 - UART RX"),
            new(1, "UART TX", "USB serial transmit pin. Avoid while uploading sketches or using Serial Monitor.", Digital | PinCapability.UartTx | PinCapability.Reserved, DisplayLabel: "D1 - UART TX"),
            new(2, "INT0", "Digital I/O pin with external interrupt support.", Digital | PinCapability.Interrupt, DisplayLabel: "D2 - Interrupt"),
            new(3, "PWM INT1", "Digital I/O pin with PWM and external interrupt support.", Digital | PinCapability.Pwm | PinCapability.Interrupt, DisplayLabel: "D3 - PWM / Interrupt"),
            new(4, "Digital", "General purpose digital I/O pin.", Digital, DisplayLabel: "D4 - Digital"),
            new(5, "PWM", "General purpose digital I/O pin with PWM output.", Digital | PinCapability.Pwm, DisplayLabel: "D5 - PWM"),
            new(6, "PWM", "General purpose digital I/O pin with PWM output.", Digital | PinCapability.Pwm, DisplayLabel: "D6 - PWM"),
            new(7, "Digital", "General purpose digital I/O pin.", Digital, DisplayLabel: "D7 - Digital"),
            new(8, "Digital", "General purpose digital I/O pin.", Digital, DisplayLabel: "D8 - Digital"),
            new(9, "PWM", "Recommended servo/PWM output pin.", Digital | PinCapability.Pwm | PinCapability.ServoRecommended, DisplayLabel: "D9 - Servo / PWM"),
            new(10, "PWM SPI CS", "Recommended servo/PWM output pin. Also commonly used as SPI chip select.", Digital | PinCapability.Pwm | PinCapability.ServoRecommended | PinCapability.SpiCs, DisplayLabel: "D10 - Servo / PWM / SPI CS"),
            new(11, "PWM SPI MOSI", "PWM output pin and SPI MOSI.", Digital | PinCapability.Pwm | PinCapability.SpiMosi, DisplayLabel: "D11 - PWM / SPI MOSI"),
            new(12, "SPI MISO", "Digital I/O pin and SPI MISO.", Digital | PinCapability.SpiMiso, DisplayLabel: "D12 - SPI MISO"),
            new(13, "LED SPI SCK", "Built-in LED pin and SPI SCK. Useful for blink tests.", Digital | PinCapability.SpiSck, DisplayLabel: "D13 - LED / SPI SCK"),
            new(14, "A0", "Analog input A0. Can also be used as digital pin 14.", AnalogDigital, DisplayLabel: "A0 - Analog 0", MaxAnalogSampleRateHz: 200),
            new(15, "A1", "Analog input A1. Can also be used as digital pin 15.", AnalogDigital, DisplayLabel: "A1 - Analog 1", MaxAnalogSampleRateHz: 200),
            new(16, "A2", "Analog input A2. Can also be used as digital pin 16.", AnalogDigital, DisplayLabel: "A2 - Analog 2", MaxAnalogSampleRateHz: 200),
            new(17, "A3", "Analog input A3. Can also be used as digital pin 17.", AnalogDigital, DisplayLabel: "A3 - Analog 3", MaxAnalogSampleRateHz: 200),
            new(18, "A4 SDA", "Analog input A4 and I2C SDA. Can also be used as digital pin 18.", AnalogDigital | PinCapability.I2cSda, DisplayLabel: "A4 - SDA / Analog 4", MaxAnalogSampleRateHz: 200),
            new(19, "A5 SCL", "Analog input A5 and I2C SCL. Can also be used as digital pin 19.", AnalogDigital | PinCapability.I2cScl, DisplayLabel: "A5 - SCL / Analog 5", MaxAnalogSampleRateHz: 200)
        ])
    {
        Runtime = new BoardRuntimeCapabilities(
            MaxDigitalSampleRateHz: 1000,
            MaxAnalogSampleRateHz: 200,
            MaxStreamChannels: 4,
            MaxBufferCapacity: 512,
            SupportsInterrupts: true,
            SupportsHardwareTimers: false,
            MinimumTimerIntervalMicroseconds: 1000),
        AnalogMaxValue = 1023,
        Family = CodeBridge.Core.Enums.BoardFamily.Arduino,
        FirmwareProject = "arduino-uno-bridge",
        BuiltInLedPin = 13,
        Fqbn = "arduino:avr:uno"
    };

    public static BoardProfile Default => Esp32DevKit;

    private static IReadOnlyList<BoardProfile>? _all;

    // Built on first use: the profiles live in two files and static initializers of partial classes run in no fixed order.
    public static IReadOnlyList<BoardProfile> All => _all ??=
    [
        Esp32DevKit,
        Esp32S3DevKit,
        XiaoEsp32S3,
        Esp32S2Mini,
        Esp32C3DevKit,
        ArduinoUno,
        ArduinoNano,
        ArduinoNanoOldBootloader,
        ArduinoMega
    ];

    public static BoardProfile? FindById(string? id) =>
        All.FirstOrDefault(board => string.Equals(board.Id, id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<FlowPropertyOption> PinModeOptions { get; } =
    [
        new("Output", "Output", "Drive the pin HIGH or LOW."),
        new("Input", "Input", "Read a digital signal."),
        new("Input PullUp", "InputPullUp", "Read a digital signal with the internal pull-up enabled."),
        new("Input PullDown", "InputPullDown", "Read a digital signal with the internal pull-down enabled."),
        new("Analog", "Analog", "Read an analog-capable pin.")
    ];
}
