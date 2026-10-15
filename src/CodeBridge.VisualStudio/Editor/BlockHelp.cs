#nullable enable
using System;
using System.Collections.Generic;
using CodeBridge.Flow;

namespace CodeBridge.VisualStudio.Editor
{
    internal enum DemoKind
    {
        None,
        Trigger,
        Timer,
        Boolean,
        Number,
        Compare,
        PinMode,
        DigitalRead,
        AnalogRead,
        DigitalWrite,
        Blink,
        Sample,
        Interrupt,
        Dashboard,
        Servo,
        Debug,
        Map,
        Pwm,
        Temperature,
        Light,
        Distance,
        Motion,
        Press,
        Tilt,
        Power,
        ColorSense,
        Level,
        Relay,
        Sound,
        RgbLed,
        Pixels,
        Screen,
        Motor,
        Stepper,
        Gate,
        Calc
    }

    /// <summary>Plain-language help shown in tooltips and in the properties panel for one block.</summary>
    internal sealed class BlockHelpEntry
    {
        public BlockHelpEntry(string summary, string tip, DemoKind demo, params string[] ports)
        {
            Summary = summary;
            Tip = tip;
            Demo = demo;
            for (var i = 0; i + 1 < ports.Length; i += 2)
                Ports[ports[i]] = ports[i + 1];
        }

        public string Summary { get; }
        public string Tip { get; }
        public DemoKind Demo { get; }
        public Dictionary<string, string> Ports { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Beginner-friendly explanations for every built-in block. The runtime catalog only carries a one-line technical
    /// description; this is what a person who has never programmed a microcontroller needs to read.
    /// </summary>
    internal static class BlockHelp
    {
        private static readonly Dictionary<string, BlockHelpEntry> Entries = new Dictionary<string, BlockHelpEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["flow.manual-trigger"] = new BlockHelpEntry(
                "The start button of your flow. When you press Run in the toolbar, this block fires and everything connected after it begins.",
                "Every flow needs a starting point. Blocks whose trigger input has nothing connected also run on their own.",
                DemoKind.Trigger,
                "trigger", "Fires once when the flow starts."),

            ["core.timer"] = new BlockHelpEntry(
                "Waits for the time you choose, then lets the next blocks continue.",
                "Use it to space things out, for example keep an LED on for one second. 1000 ms = 1 second.",
                DemoKind.Timer,
                "trigger", "Start waiting when this fires.",
                "tick", "Fires when the time is up."),

            ["logic.constant-boolean"] = new BlockHelpEntry(
                "A fixed True / False value that you can plug into other blocks.",
                "Plug it into the value input of Digital Write to switch a pin on or off.",
                DemoKind.Boolean,
                "value", "The True / False value."),

            ["logic.constant-number"] = new BlockHelpEntry(
                "A fixed number that you can plug into other blocks.",
                "Use it as the threshold of a Compare block, for example 2000.",
                DemoKind.Number,
                "value", "The number."),

            ["logic.compare"] = new BlockHelpEntry(
                "Compares two numbers and tells you whether the condition is true.",
                "Wire a sensor reading to left and a Number block to right, then send the result to a Digital Write to switch an LED.",
                DemoKind.Compare,
                "left", "First number (for example a sensor reading).",
                "right", "Second number (the threshold).",
                "result", "True when the comparison holds."),

            ["gpio.pin-mode"] = new BlockHelpEntry(
                "Tells a pin whether it will send signals (Output) or listen to them (Input, optionally with a pull-up or pull-down resistor).",
                "Digital Write and Blink LED set the pin as output for you. Use Pin Mode for inputs such as buttons.",
                DemoKind.PinMode,
                "trigger", "Configure the pin when this fires.",
                "done", "Fires once the pin is configured."),

            ["gpio.digital-read"] = new BlockHelpEntry(
                "Reads a pin: True when it is HIGH (voltage present) and False when it is LOW.",
                "Great for buttons and switches. Set the pin as an input first with Pin Mode.",
                DemoKind.DigitalRead,
                "trigger", "Read the pin when this fires.",
                "value", "True when the pin is HIGH."),

            ["gpio.analog-read"] = new BlockHelpEntry(
                "Measures a voltage on an analog pin. The ESP32 returns 0 (0 V) up to 4095 (3.3 V).",
                "Light sensors, potentiometers and moisture probes. With Wi-Fi on, prefer ADC1 pins (32 to 39).",
                DemoKind.AnalogRead,
                "trigger", "Measure when this fires.",
                "value", "The reading, 0 to 4095 on ESP32."),

            ["gpio.digital-write"] = new BlockHelpEntry(
                "Turns a pin ON (HIGH, 3.3 V) or OFF (LOW, 0 V). The pin is set as an output for you.",
                "GPIO 2 drives the on-board LED on most ESP32 DevKit boards. Set the value here, or plug a Boolean or Compare block into value.",
                DemoKind.DigitalWrite,
                "trigger", "Write the pin when this fires.",
                "value", "Optional: True = HIGH, False = LOW. Overrides the value property.",
                "done", "Fires after the pin was written."),

            ["gpio.set-output"] = new BlockHelpEntry(
                "Sets a pin as an output and writes HIGH or LOW in a single block.",
                "Turn on Active Low for LEDs wired between the supply and the pin instead of between the pin and ground.",
                DemoKind.DigitalWrite,
                "trigger", "Run when this fires.",
                "done", "Fires after the pin was written."),

            ["gpio.blink-led"] = new BlockHelpEntry(
                "Turns an LED on, waits, then turns it off. The quickest way to see your board react.",
                "Chain several Blink LED blocks to make patterns. Enable Leave On to keep the LED lit afterwards.",
                DemoKind.Blink,
                "trigger", "Blink when this fires.",
                "done", "Fires when the blink is finished."),

            ["acquisition.sample-channel"] = new BlockHelpEntry(
                "Reads a pin many times per second into a buffer, so fast signals are not missed.",
                "Connect the samples output to Stream To Dashboard to watch the signal live.",
                DemoKind.Sample,
                "trigger", "Start sampling when this fires.",
                "samples", "The buffered readings.",
                "status", "Text describing the sampling state."),

            ["acquisition.interrupt-input"] = new BlockHelpEntry(
                "Reacts the instant a pin changes (a button press, a pulse) without checking it over and over.",
                "Choose Rising, Falling or Change. Debounce ignores the electrical noise of mechanical buttons.",
                DemoKind.Interrupt,
                "changed", "Fires every time the pin changes.",
                "value", "The pin level after the change."),

            ["dashboard.stream"] = new BlockHelpEntry(
                "Sends samples to a live chart without keeping unlimited history in memory.",
                "Refresh rate and max points keep the chart smooth even with very fast signals.",
                DemoKind.Dashboard,
                "samples", "The samples to draw.",
                "done", "Fires when streaming starts."),

            ["servo.write"] = new BlockHelpEntry(
                "Moves a hobby servo to an angle between 0 and 180 degrees.",
                "Power the servo from an external 5 V supply that shares ground with the board, or the board may reset.",
                DemoKind.Servo,
                "trigger", "Move when this fires.",
                "angle", "Optional: the angle (0 to 180). Overrides the angle property.",
                "done", "Fires after the servo was commanded."),

            ["math.map"] = new BlockHelpEntry(
                "Converts a number from one range to another. Example: a light sensor gives 0 to 4095, but an LED brightness is 0 to 255.",
                "An analog reading is NOT a valid digital value. Use Map to scale it, then feed the result to PWM Output. Keep Clamp on so out-of-range readings cannot overshoot.",
                DemoKind.Map,
                "value", "The number to convert, for example an Analog Read.",
                "result", "The converted number, ready for PWM Output or a Servo angle."),

            ["gpio.pwm-write"] = new BlockHelpEntry(
                "Writes a PWM signal from 0 (always off) to 255 (always on): it dims an LED, sets a motor speed or a buzzer volume.",
                "Wire a Map block into Duty to make the output follow a sensor. Advanced: Frequency changes how fast the signal switches (5000 Hz suits LEDs; motors and buzzers may prefer other values).",
                DemoKind.Pwm,
                "trigger", "Write when this fires.",
                "duty", "Optional: the duty (0 to 255). Overrides the Duty property.",
                "done", "Fires after the pin was updated."),

            ["debug.log"] = new BlockHelpEntry(
                "Prints a value or message in the CodeBridge Output window so you can see what is happening.",
                "Drop one after any block to inspect its value while the flow runs.",
                DemoKind.Debug,
                "trigger", "Print when this fires.",
                "value", "Optional: any value to print.",
                "done", "Fires after printing.",
                "message", "The text that was printed.")
        };

        public static BlockHelpEntry? Get(string blockType) =>
            Entries.TryGetValue(blockType, out var entry) ? entry
            : ModuleBlocksHelp.Entries.TryGetValue(blockType, out var module) ? module
            : null;

        /// <summary>The summary to show for a block: the friendly one when we have it, the catalog text otherwise.</summary>
        public static string SummaryFor(FlowBlockDefinition definition) =>
            Get(definition.Type)?.Summary ?? definition.Description;

        public static IEnumerable<string> KnownTypes => System.Linq.Enumerable.Concat(Entries.Keys, ModuleBlocksHelp.Entries.Keys);
    }
}
