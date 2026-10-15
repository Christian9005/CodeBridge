using CodeBridge.Flow.Streaming;

namespace CodeBridge.Flow;

/// <summary>
/// Built-in block definitions for the first visual flow MVP.
/// </summary>
public static class BuiltInBlockCatalog
{
    public const string ManualTrigger = "flow.manual-trigger";
    public const string Timer = "core.timer";
    public const string ConstantBoolean = "logic.constant-boolean";
    public const string ConstantNumber = "logic.constant-number";
    public const string Compare = "logic.compare";
    public const string GpioPinMode = "gpio.pin-mode";
    public const string GpioDigitalRead = "gpio.digital-read";
    public const string GpioAnalogRead = "gpio.analog-read";
    public const string GpioDigitalWrite = "gpio.digital-write";
    public const string GpioSetOutput = "gpio.set-output";
    public const string GpioBlinkLed = "gpio.blink-led";
    public const string SampleChannel = "acquisition.sample-channel";
    public const string InterruptInput = "acquisition.interrupt-input";
    public const string StreamDashboard = "dashboard.stream";
    public const string ServoWrite = "servo.write";
    public const string DebugLog = "debug.log";
    public const string MathMap = "math.map";
    public const string PwmWrite = "gpio.pwm-write";

    private static readonly IReadOnlyList<FlowPropertyOption> BooleanOptions =
    [
        new("True / HIGH", true, "Use HIGH for digital outputs."),
        new("False / LOW", false, "Use LOW for digital outputs.")
    ];

    private static readonly IReadOnlyList<FlowPropertyOption> CompareOperatorOptions =
    [
        new("Greater than >", ">", "True when left is greater than right."),
        new("Greater or equal >=", ">=", "True when left is greater than or equal to right."),
        new("Less than <", "<", "True when left is less than right."),
        new("Less or equal <=", "<=", "True when left is less than or equal to right."),
        new("Equals ==", "==", "True when left equals right."),
        new("Not equals !=", "!=", "True when left does not equal right.")
    ];

    private static readonly IReadOnlyList<FlowPropertyOption> SamplingModeOptions =
    [
        new("Polling", nameof(SamplingMode.Polling), "Read at a scheduled interval from the host/runtime."),
        new("Hardware Timer", nameof(SamplingMode.HardwareTimer), "Use board timer scheduling for stable acquisition."),
        new("Interrupt", nameof(SamplingMode.Interrupt), "Emit samples when the pin changes.")
    ];

    private static readonly IReadOnlyList<FlowPropertyOption> BackpressureOptions =
    [
        new("Drop Oldest", nameof(BackpressurePolicy.DropOldest), "Keep recent data when the dashboard falls behind."),
        new("Drop Newest", nameof(BackpressurePolicy.DropNewest), "Preserve the existing buffer and discard new overflow."),
        new("Aggregate", nameof(BackpressurePolicy.Aggregate), "Compress overflowing samples into summary windows."),
        new("Pause", nameof(BackpressurePolicy.Pause), "Ask the producer to slow down when possible.")
    ];

    public static FlowBlockCatalog Create(BoardProfile? boardProfile = null)
    {
        var board = boardProfile ?? BuiltInBoardProfiles.Default;

        var catalog = CreateCore(board);
        foreach (var module in ModuleBlocks.For(board))
            catalog.Register(module.ToDefinition(board));

        return catalog;
    }

    private static FlowBlockCatalog CreateCore(BoardProfile board)
    {
        return new FlowBlockCatalog()
            .Register(new FlowBlockDefinition
            {
                Type = ManualTrigger,
                DisplayName = "Manual Trigger",
                Category = "Flow",
                Description = "Starts a flow when the designer Run button is pressed.",
                Ports =
                [
                    FlowPortDefinition.Output("trigger", FlowValueKind.Trigger)
                ]
            })
            .Register(new FlowBlockDefinition
            {
                Type = Timer,
                DisplayName = "Timer",
                Category = "Flow",
                Description = "Waits for a delay, then emits a trigger.",
                Ports =
                [
                    FlowPortDefinition.Input("trigger", FlowValueKind.Trigger, required: false),
                    FlowPortDefinition.Output("tick", FlowValueKind.Trigger)
                ],
                Properties =
                [
                    new FlowPropertyDefinition("intervalMs", FlowValueKind.Integer, DefaultValue: 1000)
                ]
            })
            .Register(new FlowBlockDefinition
            {
                Type = ConstantBoolean,
                DisplayName = "Boolean",
                Category = "Logic",
                Description = "Outputs a fixed boolean value.",
                Ports =
                [
                    FlowPortDefinition.Output("value", FlowValueKind.Boolean)
                ],
                Properties =
                [
                    new FlowPropertyDefinition("value", FlowValueKind.Boolean, DefaultValue: false, Options: BooleanOptions)
                ]
            })
            .Register(new FlowBlockDefinition
            {
                Type = ConstantNumber,
                DisplayName = "Number",
                Category = "Logic",
                Description = "Outputs a fixed numeric value.",
                Ports =
                [
                    FlowPortDefinition.Output("value", FlowValueKind.Number)
                ],
                Properties =
                [
                    new FlowPropertyDefinition("value", FlowValueKind.Number, DefaultValue: 0)
                ]
            })
            .Register(new FlowBlockDefinition
            {
                Type = Compare,
                DisplayName = "Compare",
                Category = "Logic",
                Description = "Compares two numeric inputs.",
                Ports =
                [
                    FlowPortDefinition.Input("left", FlowValueKind.Number),
                    FlowPortDefinition.Input("right", FlowValueKind.Number),
                    FlowPortDefinition.Output("result", FlowValueKind.Boolean)
                ],
                Properties =
                [
                    new FlowPropertyDefinition("operator", FlowValueKind.String, DefaultValue: ">", Options: CompareOperatorOptions)
                ]
            })
            .Register(new FlowBlockDefinition
            {
                Type = GpioPinMode,
                DisplayName = "Pin Mode",
                Category = "GPIO",
                Description = "Configures a GPIO pin mode before reading or writing.",
                Ports =
                [
                    FlowPortDefinition.Input("trigger", FlowValueKind.Trigger, required: false),
                    FlowPortDefinition.Output("done", FlowValueKind.Trigger)
                ],
                Properties =
                [
                    new FlowPropertyDefinition("pin", FlowValueKind.Integer, Required: true, Options: board.PinOptions),
                    new FlowPropertyDefinition("mode", FlowValueKind.String, DefaultValue: "Output", Options: BuiltInBoardProfiles.PinModeOptions)
                ]
            })
            .Register(new FlowBlockDefinition
            {
                Type = GpioDigitalRead,
                DisplayName = "Digital Read",
                Category = "GPIO",
                Description = "Reads a digital GPIO pin.",
                Ports =
                [
                    FlowPortDefinition.Input("trigger", FlowValueKind.Trigger, required: false),
                    FlowPortDefinition.Output("value", FlowValueKind.Boolean)
                ],
                Properties =
                [
                    new FlowPropertyDefinition("pin", FlowValueKind.Integer, Required: true, Options: board.DigitalReadPinOptions)
                ]
            })
            .Register(new FlowBlockDefinition
            {
                Type = GpioAnalogRead,
                DisplayName = "Analog Read",
                Category = "GPIO",
                Description = "Reads an analog GPIO pin.",
                Ports =
                [
                    FlowPortDefinition.Input("trigger", FlowValueKind.Trigger, required: false),
                    FlowPortDefinition.Output("value", FlowValueKind.Integer)
                ],
                Properties =
                [
                    new FlowPropertyDefinition("pin", FlowValueKind.Integer, Required: true, Options: board.AnalogReadPinOptions),
                    new FlowPropertyDefinition("samples", FlowValueKind.Integer, DefaultValue: 1, IsAdvanced: true)
                ]
            })
            .Register(new FlowBlockDefinition
            {
                Type = GpioDigitalWrite,
                DisplayName = "Digital Write",
                Category = "GPIO",
                Description = "Writes a digital GPIO pin.",
                Ports =
                [
                    FlowPortDefinition.Input("trigger", FlowValueKind.Trigger, required: false),
                    FlowPortDefinition.Input("value", FlowValueKind.Boolean, required: false),
                    FlowPortDefinition.Output("done", FlowValueKind.Trigger)
                ],
                Properties =
                [
                    new FlowPropertyDefinition("pin", FlowValueKind.Integer, Required: true, Options: board.DigitalWritePinOptions),
                    new FlowPropertyDefinition("value", FlowValueKind.Boolean, DefaultValue: false, Options: BooleanOptions)
                ]
            })
            .Register(new FlowBlockDefinition
            {
                Type = GpioSetOutput,
                DisplayName = "Digital Output",
                Category = "GPIO",
                Description = "Configures a GPIO as output and writes HIGH or LOW in one action.",
                Ports =
                [
                    FlowPortDefinition.Input("trigger", FlowValueKind.Trigger, required: false),
                    FlowPortDefinition.Output("done", FlowValueKind.Trigger)
                ],
                Properties =
                [
                    new FlowPropertyDefinition("pin", FlowValueKind.Integer, Required: true, Options: board.DigitalWritePinOptions),
                    new FlowPropertyDefinition("value", FlowValueKind.Boolean, DefaultValue: true, Options: BooleanOptions),
                    new FlowPropertyDefinition("activeLow", FlowValueKind.Boolean, DefaultValue: false, Options: BooleanOptions, IsAdvanced: true)
                ]
            })
            .Register(new FlowBlockDefinition
            {
                Type = GpioBlinkLed,
                DisplayName = "Blink LED",
                Category = "GPIO",
                Description = "Turns a GPIO LED on, waits, then turns it off.",
                Ports =
                [
                    FlowPortDefinition.Input("trigger", FlowValueKind.Trigger, required: false),
                    FlowPortDefinition.Output("done", FlowValueKind.Trigger)
                ],
                Properties =
                [
                    new FlowPropertyDefinition("pin", FlowValueKind.Integer, Required: true, DefaultValue: 2, Options: board.DigitalWritePinOptions),
                    new FlowPropertyDefinition("durationMs", FlowValueKind.Integer, DefaultValue: 500),
                    new FlowPropertyDefinition("activeLow", FlowValueKind.Boolean, DefaultValue: false, Options: BooleanOptions, IsAdvanced: true),
                    new FlowPropertyDefinition("leaveOn", FlowValueKind.Boolean, DefaultValue: false, Options: BooleanOptions, IsAdvanced: true)
                ]
            })
            .Register(new FlowBlockDefinition
            {
                Type = SampleChannel,
                DisplayName = "Sample Channel",
                Category = "Acquisition",
                Description = "Configures a buffered high-rate GPIO acquisition channel for dashboards or downstream blocks.",
                Ports =
                [
                    FlowPortDefinition.Input("trigger", FlowValueKind.Trigger, required: false),
                    FlowPortDefinition.Output("samples", FlowValueKind.Any),
                    FlowPortDefinition.Output("status", FlowValueKind.String)
                ],
                Properties =
                [
                    new FlowPropertyDefinition("pin", FlowValueKind.Integer, Required: true, Options: board.SamplePinOptions),
                    new FlowPropertyDefinition("analog", FlowValueKind.Boolean, DefaultValue: false, Options: BooleanOptions),
                    new FlowPropertyDefinition("mode", FlowValueKind.String, DefaultValue: nameof(SamplingMode.Polling), Options: SamplingModeOptions),
                    new FlowPropertyDefinition("sampleRateHz", FlowValueKind.Integer, DefaultValue: 1000, IsAdvanced: true),
                    new FlowPropertyDefinition("bufferCapacity", FlowValueKind.Integer, DefaultValue: 4096, IsAdvanced: true),
                    new FlowPropertyDefinition("backpressure", FlowValueKind.String, DefaultValue: nameof(BackpressurePolicy.DropOldest), Options: BackpressureOptions, IsAdvanced: true),
                    new FlowPropertyDefinition("batchSize", FlowValueKind.Integer, DefaultValue: 32, IsAdvanced: true)
                ]
            })
            .Register(new FlowBlockDefinition
            {
                Type = InterruptInput,
                DisplayName = "Interrupt Input",
                Category = "Acquisition",
                Description = "Listens to GPIO edge changes without polling.",
                Ports =
                [
                    FlowPortDefinition.Output("changed", FlowValueKind.Trigger),
                    FlowPortDefinition.Output("value", FlowValueKind.Boolean)
                ],
                Properties =
                [
                    new FlowPropertyDefinition("pin", FlowValueKind.Integer, Required: true, Options: board.InterruptPinOptions),
                    new FlowPropertyDefinition("edge", FlowValueKind.String, DefaultValue: "Change", Options:
                    [
                        new("Change", "Change", "Trigger on rising or falling edge."),
                        new("Rising", "Rising", "Trigger when the signal moves LOW to HIGH."),
                        new("Falling", "Falling", "Trigger when the signal moves HIGH to LOW.")
                    ]),
                    new FlowPropertyDefinition("debounceMs", FlowValueKind.Integer, DefaultValue: 5)
                ]
            })
            .Register(new FlowBlockDefinition
            {
                Type = StreamDashboard,
                DisplayName = "Stream To Dashboard",
                Category = "Dashboard",
                Description = "Feeds a bounded sample stream to a UI dashboard without retaining unbounded history.",
                Ports =
                [
                    FlowPortDefinition.Input("samples", FlowValueKind.Any, required: false),
                    FlowPortDefinition.Output("done", FlowValueKind.Trigger)
                ],
                Properties =
                [
                    new FlowPropertyDefinition("channelName", FlowValueKind.String, DefaultValue: "channel-1"),
                    new FlowPropertyDefinition("refreshHz", FlowValueKind.Integer, DefaultValue: 20, IsAdvanced: true),
                    new FlowPropertyDefinition("retentionSeconds", FlowValueKind.Integer, DefaultValue: 30, IsAdvanced: true),
                    new FlowPropertyDefinition("maxPoints", FlowValueKind.Integer, DefaultValue: 2000, IsAdvanced: true)
                ]
            })
            .Register(new FlowBlockDefinition
            {
                Type = ServoWrite,
                DisplayName = "Servo Write",
                Category = "Actuators",
                Description = "Moves a servo to an angle.",
                Ports =
                [
                    FlowPortDefinition.Input("trigger", FlowValueKind.Trigger, required: false),
                    FlowPortDefinition.Input("angle", FlowValueKind.Number, required: false),
                    FlowPortDefinition.Output("done", FlowValueKind.Trigger)
                ],
                Properties =
                [
                    new FlowPropertyDefinition("pin", FlowValueKind.Integer, Required: true, DefaultValue: 13, Options: board.ServoPinOptions),
                    new FlowPropertyDefinition("angle", FlowValueKind.Number, DefaultValue: 90),
                    new FlowPropertyDefinition("minPulseUs", FlowValueKind.Integer, DefaultValue: 500, IsAdvanced: true),
                    new FlowPropertyDefinition("maxPulseUs", FlowValueKind.Integer, DefaultValue: 2500, IsAdvanced: true)
                ]
            })
            .Register(new FlowBlockDefinition
            {
                Type = MathMap,
                DisplayName = "Map",
                Category = "Math",
                Description = "Converts a number from one range to another, for example a 0-4095 sensor reading into a 0-255 brightness.",
                Ports =
                [
                    FlowPortDefinition.Input("value", FlowValueKind.Number),
                    FlowPortDefinition.Output("result", FlowValueKind.Number)
                ],
                Properties =
                [
                    new FlowPropertyDefinition("inMin", FlowValueKind.Number, DefaultValue: 0),
                    new FlowPropertyDefinition("inMax", FlowValueKind.Number, DefaultValue: board.AnalogMaxValue),
                    new FlowPropertyDefinition("outMin", FlowValueKind.Number, DefaultValue: 0),
                    new FlowPropertyDefinition("outMax", FlowValueKind.Number, DefaultValue: 255),
                    new FlowPropertyDefinition("clamp", FlowValueKind.Boolean, DefaultValue: true, Options: BooleanOptions),
                    new FlowPropertyDefinition("round", FlowValueKind.Boolean, DefaultValue: false, Options: BooleanOptions, IsAdvanced: true)
                ]
            })
            .Register(new FlowBlockDefinition
            {
                Type = PwmWrite,
                DisplayName = "PWM Output",
                Category = "GPIO",
                Description = "Writes a PWM duty cycle (0-255) to a pin: LED brightness, motor speed, buzzer volume.",
                Ports =
                [
                    FlowPortDefinition.Input("trigger", FlowValueKind.Trigger, required: false),
                    FlowPortDefinition.Input("duty", FlowValueKind.Number, required: false),
                    FlowPortDefinition.Output("done", FlowValueKind.Trigger)
                ],
                Properties =
                [
                    new FlowPropertyDefinition("pin", FlowValueKind.Integer, Required: true, Options: board.PwmPinOptions),
                    new FlowPropertyDefinition("duty", FlowValueKind.Integer, DefaultValue: 128),
                    new FlowPropertyDefinition("frequencyHz", FlowValueKind.Integer, DefaultValue: 5000, IsAdvanced: true)
                ]
            })
            .Register(new FlowBlockDefinition
            {
                Type = DebugLog,
                DisplayName = "Debug",
                Category = "Debug",
                Description = "Writes a flow value to the designer output panel.",
                Ports =
                [
                    FlowPortDefinition.Input("trigger", FlowValueKind.Trigger, required: false),
                    FlowPortDefinition.Input("value", FlowValueKind.Any, required: false),
                    FlowPortDefinition.Output("done", FlowValueKind.Trigger),
                    FlowPortDefinition.Output("message", FlowValueKind.String)
                ],
                Properties =
                [
                    new FlowPropertyDefinition("label", FlowValueKind.String, DefaultValue: "debug"),
                    new FlowPropertyDefinition("message", FlowValueKind.String, DefaultValue: "")
                ]
            });
    }
}
