using CodeBridge.Core.Enums;

namespace CodeBridge.Flow;

/// <summary>One pin as an AI assistant (or any tool) needs to know it: what it can do and how safe it is to use.</summary>
/// <param name="Safety"><c>ok</c>, <c>boot-strap</c> (its level at power-up decides how the chip starts), <c>input-only</c> or <c>reserved</c> (USB, UART, flash, PSRAM: do not use).</param>
public sealed record PinManifest(int Number, string Name, string Description, IReadOnlyList<string> Capabilities, string Safety, IReadOnlyList<string> Notes);

/// <summary>
/// A machine-readable description of a board: its pins with capabilities and safety, the buses and their pins, electrical limits and
/// the rules that keep a design from damaging the board or silently not working. It is generated from the same profile the flow
/// validator uses, so what it says is what the runtime enforces.
/// </summary>
public sealed record BoardManifest(
    string Id,
    string DisplayName,
    string Family,
    string? Chip,
    int MaxPin,
    int AnalogMax,
    int PwmMax,
    double LogicLevelVolts,
    int SafePinCurrentMilliamps,
    bool SupportsWifi,
    int? BuiltInLedPin,
    IReadOnlyList<PinManifest> Pins,
    IReadOnlyDictionary<string, IReadOnlyList<int>> Buses,
    IReadOnlyList<string> Rules)
{
    public static BoardManifest For(BoardProfile board)
    {
        ArgumentNullException.ThrowIfNull(board);

        var esp32 = board.Family == BoardFamily.ESP32;
        var pins = board.Pins.OrderBy(p => p.Number).Select(pin => ToManifest(pin, esp32)).ToList();

        var buses = new Dictionary<string, IReadOnlyList<int>>();
        void Bus(string name, PinCapability capability)
        {
            var numbers = board.Pins.Where(p => p.HasCapability(capability)).Select(p => p.Number).OrderBy(n => n).ToList();
            if (numbers.Count > 0)
                buses[name] = numbers;
        }

        Bus("i2c-sda", PinCapability.I2cSda);
        Bus("i2c-scl", PinCapability.I2cScl);
        Bus("spi-mosi", PinCapability.SpiMosi);
        Bus("spi-miso", PinCapability.SpiMiso);
        Bus("spi-sck", PinCapability.SpiSck);
        Bus("spi-cs", PinCapability.SpiCs);
        Bus("uart-tx", PinCapability.UartTx);
        Bus("uart-rx", PinCapability.UartRx);

        return new BoardManifest(
            board.Id,
            board.DisplayName,
            board.Family.ToString(),
            board.FlashChip,
            board.MaxPin,
            board.AnalogMaxValue,
            255,
            esp32 ? 3.3 : 5.0,
            esp32 ? 12 : 20,
            board.SupportsWifi,
            board.BuiltInLedPin,
            pins,
            buses,
            BuildRules(board, esp32, pins));
    }

    private static PinManifest ToManifest(BoardPinDefinition pin, bool esp32)
    {
        var capabilities = new List<string>();
        void Add(PinCapability capability, string name)
        {
            if (pin.HasCapability(capability)) capabilities.Add(name);
        }

        Add(PinCapability.DigitalRead, "digital-read");
        Add(PinCapability.DigitalWrite, "digital-write");
        Add(PinCapability.AnalogRead, "analog-read");
        Add(PinCapability.Pwm, "pwm");
        Add(PinCapability.ServoRecommended, "servo-friendly");
        Add(PinCapability.Interrupt, "interrupt");
        Add(PinCapability.I2cSda, "i2c-sda");
        Add(PinCapability.I2cScl, "i2c-scl");
        Add(PinCapability.SpiMosi, "spi-mosi");
        Add(PinCapability.SpiMiso, "spi-miso");
        Add(PinCapability.SpiSck, "spi-sck");
        Add(PinCapability.SpiCs, "spi-cs");
        Add(PinCapability.UartTx, "uart-tx");
        Add(PinCapability.UartRx, "uart-rx");
        Add(PinCapability.Adc1, "adc1");
        Add(PinCapability.Adc2, "adc2");

        var safety = pin.IsReserved ? "reserved" : pin.IsInputOnly ? "input-only" : pin.IsBootStrap ? "boot-strap" : "ok";

        var notes = new List<string>();
        if (pin.IsReserved) notes.Add("Used by the USB serial link, flash, PSRAM or the USB port. Do not use it in a design.");
        if (pin.IsInputOnly) notes.Add("Input only: it cannot drive an output, and it has no internal pull-up or pull-down.");
        if (pin.IsBootStrap) notes.Add("Strapping pin: the level it has at power-up selects the boot mode. Avoid loads, buttons or pull-ups that could hold it in the wrong state.");
        if (esp32 && pin.HasCapability(PinCapability.Adc2)) notes.Add("ADC2: analog reads do not work reliably while Wi-Fi is on.");
        if (pin.HasCapability(PinCapability.ServoRecommended)) notes.Add("Good choice for a servo signal.");

        return new PinManifest(pin.Number, pin.Name, pin.Description, capabilities, safety, notes);
    }

    private static List<string> BuildRules(BoardProfile board, bool esp32, IReadOnlyList<PinManifest> pins)
    {
        var rules = new List<string>
        {
            "Only use pins whose safety is 'ok' unless the design really needs a 'boot-strap' pin and you know the circuit (the built-in LED pin is fine for tests). Never use 'reserved' pins.",
            $"Analog Read returns 0 to {board.AnalogMaxValue}. PWM duty is 0 to 255. Use the Map block to convert one range into the other before wiring an analog reading to PWM Output or a servo angle.",
            "A digital reading is not an analog one: do not wire an Analog Read straight into a boolean input. Compare it with a threshold (Compare block) or Map it.",
            $"Logic level is {(esp32 ? "3.3 V (pins are NOT 5 V tolerant)" : "5 V")}. A pin can safely source about {(esp32 ? 12 : 20)} mA: drive motors, relays, LED strips and buzzers through a transistor, driver or relay module, never directly from a pin.",
            "Two blocks that use the same pin for different purposes (for example PWM and a digital input) conflict; the validator reports pin conflicts."
        };

        var adc2 = pins.Where(p => p.Capabilities.Contains("adc2")).Select(p => p.Number).ToList();
        if (esp32 && adc2.Count > 0)
            rules.Add($"If the board uses Wi-Fi, read analog values only from ADC1 pins ({string.Join(", ", pins.Where(p => p.Capabilities.Contains("adc1")).Select(p => p.Number))}); the ADC2 pins ({string.Join(", ", adc2)}) fail while Wi-Fi is connected.");

        var inputOnly = pins.Where(p => p.Safety == "input-only").Select(p => p.Number).ToList();
        if (inputOnly.Count > 0)
            rules.Add($"Pins {string.Join(", ", inputOnly)} are input only.");

        if (board.BuiltInLedPin is { } led)
            rules.Add($"The built-in LED is on pin {led}: a good first test is to blink it.");

        if (!esp32)
            rules.Add("Pins 0 and 1 are the USB serial link on Arduino boards: using them breaks uploads and the connection to CodeBridge.");

        if (board.Id == "arduino-uno" || board.Id.StartsWith("arduino-nano", StringComparison.Ordinal))
            rules.Add("The Servo library disables PWM on pins 9 and 10: if a flow uses a servo, do not use PWM on those two pins.");

        return rules;
    }
}
