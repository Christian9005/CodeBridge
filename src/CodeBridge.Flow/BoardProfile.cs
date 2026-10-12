namespace CodeBridge.Flow;

/// <summary>
/// Groups the known pin map for a board family.
/// </summary>
public sealed record BoardProfile(
    string Id,
    string DisplayName,
    IReadOnlyList<BoardPinDefinition> Pins)
{
    public BoardRuntimeCapabilities Runtime { get; init; } = BoardRuntimeCapabilities.Default;

    /// <summary>The microcontroller family (decides the firmware, the flashing tool and the connection types).</summary>
    public CodeBridge.Core.Enums.BoardFamily Family { get; init; } = CodeBridge.Core.Enums.BoardFamily.ESP32;

    /// <summary>Folder under <c>firmware/</c> holding the firmware sources of this board.</summary>
    public string FirmwareProject { get; init; } = "esp32-bridge";

    /// <summary>ESP32 only: the <c>--chip</c> name esptool expects (esp32, esp32s3, esp32c3).</summary>
    public string? FlashChip { get; init; }

    /// <summary>ESP32 only: folder under <c>firmware/prebuilt/</c> with this chip's images.</summary>
    public string? PrebuiltFolder { get; init; }

    /// <summary>ESP32 only: flash offset of the bootloader (0x1000 on the original ESP32, 0 on the newer chips).</summary>
    public int BootloaderOffset { get; init; } = 0x1000;

    /// <summary>Arduino boards: the fully qualified board name Arduino CLI compiles and uploads for.</summary>
    public string? Fqbn { get; init; }

    /// <summary>The ESP32 family can also be controlled over Wi-Fi; the Arduino boards use USB only.</summary>
    public bool SupportsWifi => Family == CodeBridge.Core.Enums.BoardFamily.ESP32;

    /// <summary>The pin of the on-board LED that a plain blink test can use, or null when the board has none that way.</summary>
    public int? BuiltInLedPin { get; init; }

    /// <summary>Highest pin number of the board.</summary>
    public int MaxPin => Pins.Count == 0 ? 0 : Pins.Max(pin => pin.Number);

    /// <summary>Largest value Analog Read returns on this board (ESP32 12-bit ADC = 4095, Arduino Uno 10-bit ADC = 1023).</summary>
    public int AnalogMaxValue { get; init; } = 4095;

    public BoardPinDefinition? FindPin(int number) =>
        Pins.FirstOrDefault(pin => pin.Number == number);

    public IReadOnlyList<FlowPropertyOption> PinOptions =>
        PinOptionsFor(PinCapability.None);

    public IReadOnlyList<FlowPropertyOption> DigitalReadPinOptions =>
        PinOptionsFor(PinCapability.DigitalRead);

    public IReadOnlyList<FlowPropertyOption> DigitalWritePinOptions =>
        PinOptionsFor(PinCapability.DigitalWrite);

    public IReadOnlyList<FlowPropertyOption> AnalogReadPinOptions =>
        PinOptionsFor(PinCapability.AnalogRead);

    public IReadOnlyList<FlowPropertyOption> PwmPinOptions =>
        PinOptionsFor(PinCapability.Pwm | PinCapability.DigitalWrite);

    public IReadOnlyList<FlowPropertyOption> ServoPinOptions =>
        PinOptionsFor(
            PinCapability.ServoRecommended | PinCapability.Pwm | PinCapability.DigitalWrite,
            PinCapability.Reserved | PinCapability.BootStrap | PinCapability.InputOnly);

    public IReadOnlyList<FlowPropertyOption> InterruptPinOptions =>
        PinOptionsFor(PinCapability.Interrupt);

    public IReadOnlyList<FlowPropertyOption> SamplePinOptions =>
        ToOptions(pin => (pin.SupportsDigitalRead || pin.SupportsAnalogRead) && !pin.IsReserved);

    public IReadOnlyList<FlowPropertyOption> PinOptionsFor(
        PinCapability required,
        PinCapability disallowed = PinCapability.Reserved) =>
        ToOptions(pin =>
            pin.HasCapability(required) &&
            (pin.Capabilities & disallowed) == PinCapability.None);

    private IReadOnlyList<FlowPropertyOption> ToOptions(Func<BoardPinDefinition, bool> predicate) =>
        Pins.Where(predicate)
            .OrderBy(pin => pin.Number)
            .Select(pin => pin.ToOption())
            .ToArray();
}
