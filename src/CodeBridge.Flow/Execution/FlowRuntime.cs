using CodeBridge.Core.Abstractions;
using CodeBridge.Core.Abstractions.Acquisition;
using CodeBridge.Core.Enums;
using CodeBridge.Core.Protocol;
using CodeBridge.Flow;
using CodeBridge.Flow.Streaming;
using CodeBridge.Flow.Validation;

namespace CodeBridge.Flow.Execution;

/// <summary>
/// Executes a valid acyclic flow document against registered block handlers.
/// </summary>
public sealed class FlowRuntime
{
    private readonly FlowBlockCatalog _catalog;
    private readonly Dictionary<string, FlowBlockHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly FlowValidator _validator = new();

    public FlowRuntime(FlowBlockCatalog catalog, bool registerBuiltIns = true)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

        if (registerBuiltIns)
            RegisterBuiltInHandlers();
    }

    public FlowRuntime RegisterHandler(string blockType, FlowBlockHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blockType);
        ArgumentNullException.ThrowIfNull(handler);

        _handlers[blockType] = handler;
        return this;
    }

    public async Task<FlowExecutionResult> ExecuteAsync(
        FlowDocument document,
        FlowExecutionContext? executionContext = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var validation = _validator.Validate(document, _catalog);
        if (!validation.IsValid)
        {
            var message = string.Join(Environment.NewLine, validation.Errors.Select(issue => issue.Message));
            throw new InvalidOperationException($"Flow document is invalid:{Environment.NewLine}{message}");
        }

        executionContext ??= new FlowExecutionContext();

        var nodesById = document.Nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        var sortedNodes = FlowGraphSorter.Sort(document, nodesById);
        var outputsByNode = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in sortedNodes)
        {
            ct.ThrowIfCancellationRequested();

            if (!_handlers.TryGetValue(node.Type, out var handler))
                throw new InvalidOperationException($"No runtime handler registered for block type '{node.Type}'.");

            var definition = _catalog.Get(node.Type);
            var inputs = ResolveInputs(document, node, definition, outputsByNode);

            if (!ShouldExecute(document, node, definition, inputs))
            {
                outputsByNode[node.Id] = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                await ReportAsync(
                    executionContext,
                    new FlowExecutionEvent(FlowExecutionEventKind.NodeSkipped, node.Id, node.Type),
                    ct);
                continue;
            }

            await ReportAsync(
                executionContext,
                new FlowExecutionEvent(FlowExecutionEventKind.NodeStarted, node.Id, node.Type),
                ct);

            var nodeContext = new FlowNodeExecutionContext(
                document,
                node,
                definition,
                executionContext.Board,
                inputs,
                ct);

            try
            {
                var outputs = await handler(nodeContext);
                var nodeOutputs = new Dictionary<string, object?>(outputs, StringComparer.OrdinalIgnoreCase);
                outputsByNode[node.Id] = nodeOutputs;
                await ReportAsync(
                    executionContext,
                    new FlowExecutionEvent(
                        FlowExecutionEventKind.NodeCompleted,
                        node.Id,
                        node.Type,
                        nodeOutputs),
                    ct);
            }
            catch (Exception ex)
            {
                await ReportAsync(
                    executionContext,
                    new FlowExecutionEvent(
                        FlowExecutionEventKind.NodeFailed,
                        node.Id,
                        node.Type,
                        Message: ex.Message),
                    ct);
                throw;
            }
        }

        return new FlowExecutionResult(outputsByNode);
    }

    public Task<FlowExecutionResult> ExecuteAsync(
        FlowDocument document,
        IBoard board,
        CancellationToken ct = default)
    {
        return ExecuteAsync(document, new FlowExecutionContext { Board = board }, ct);
    }

    private static IReadOnlyDictionary<string, object?> ResolveInputs(
        FlowDocument document,
        FlowNode node,
        FlowBlockDefinition definition,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> outputsByNode)
    {
        var inputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var connection in document.Connections.Where(connection =>
            string.Equals(connection.ToNodeId, node.Id, StringComparison.OrdinalIgnoreCase)))
        {
            if (!outputsByNode.TryGetValue(connection.FromNodeId, out var sourceOutputs))
                throw new InvalidOperationException($"Source node '{connection.FromNodeId}' has not executed.");

            if (!sourceOutputs.TryGetValue(connection.FromPort, out var value))
            {
                var targetPort = definition.FindInput(connection.ToPort);
                if (targetPort?.ValueKind == FlowValueKind.Trigger)
                {
                    inputs[connection.ToPort] = false;
                    continue;
                }

                throw new InvalidOperationException(
                    $"Source node '{connection.FromNodeId}' did not produce output '{connection.FromPort}'.");
            }

            inputs[connection.ToPort] = value;
        }

        return inputs;
    }

    private static bool ShouldExecute(
        FlowDocument document,
        FlowNode node,
        FlowBlockDefinition definition,
        IReadOnlyDictionary<string, object?> inputs)
    {
        var triggerInputs = definition.InputPorts
            .Where(port => port.ValueKind == FlowValueKind.Trigger)
            .ToList();

        if (triggerInputs.Count == 0)
            return true;

        var connectedTriggers = triggerInputs
            .Where(port => document.Connections.Any(connection =>
                string.Equals(connection.ToNodeId, node.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(connection.ToPort, port.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (connectedTriggers.Count == 0)
            return true;

        return connectedTriggers.Any(port =>
            inputs.TryGetValue(port.Name, out var value) &&
            FlowValueConverter.ConvertTo<bool>(value, port.Name));
    }

    private void RegisterBuiltInHandlers()
    {
        RegisterHandler(BuiltInBlockCatalog.ManualTrigger, HandleManualTriggerAsync);
        RegisterHandler(BuiltInBlockCatalog.Timer, HandleTimerAsync);
        RegisterHandler(BuiltInBlockCatalog.ConstantBoolean, HandleConstantBooleanAsync);
        RegisterHandler(BuiltInBlockCatalog.ConstantNumber, HandleConstantNumberAsync);
        RegisterHandler(BuiltInBlockCatalog.Compare, HandleCompareAsync);
        RegisterHandler(BuiltInBlockCatalog.GpioPinMode, HandleGpioPinModeAsync);
        RegisterHandler(BuiltInBlockCatalog.GpioDigitalRead, HandleGpioDigitalReadAsync);
        RegisterHandler(BuiltInBlockCatalog.GpioAnalogRead, HandleGpioAnalogReadAsync);
        RegisterHandler(BuiltInBlockCatalog.GpioDigitalWrite, HandleGpioDigitalWriteAsync);
        RegisterHandler(BuiltInBlockCatalog.GpioSetOutput, HandleGpioSetOutputAsync);
        RegisterHandler(BuiltInBlockCatalog.GpioBlinkLed, HandleGpioBlinkLedAsync);
        RegisterHandler(BuiltInBlockCatalog.SampleChannel, HandleSampleChannelAsync);
        RegisterHandler(BuiltInBlockCatalog.InterruptInput, HandleInterruptInputAsync);
        RegisterHandler(BuiltInBlockCatalog.StreamDashboard, HandleStreamDashboardAsync);
        RegisterHandler(BuiltInBlockCatalog.ServoWrite, HandleServoWriteAsync);
        RegisterHandler(BuiltInBlockCatalog.DebugLog, HandleDebugLogAsync);
        RegisterHandler(BuiltInBlockCatalog.MathMap, HandleMapAsync);
        RegisterHandler(BuiltInBlockCatalog.PwmWrite, HandlePwmWriteAsync);

        foreach (var module in ModuleBlocks.All)
        {
            var run = module.Run;
            RegisterHandler(module.Type, context => run(new ModuleRun(context)));
        }
    }

    private static async ValueTask ReportAsync(
        FlowExecutionContext context,
        FlowExecutionEvent executionEvent,
        CancellationToken ct)
    {
        if (context.EventHandler is not null)
            await context.EventHandler(executionEvent, ct);

        if (context.TraceDelay > TimeSpan.Zero)
            await Task.Delay(context.TraceDelay, ct);
    }

    private static ValueTask<IReadOnlyDictionary<string, object?>> HandleManualTriggerAsync(
        FlowNodeExecutionContext context)
    {
        return ValueTask.FromResult(Output("trigger", true));
    }

    private static async ValueTask<IReadOnlyDictionary<string, object?>> HandleTimerAsync(
        FlowNodeExecutionContext context)
    {
        var intervalMs = context.GetParameter<int>("intervalMs");
        if (intervalMs < 0)
            throw new InvalidOperationException("Timer interval must be zero or greater.");

        if (intervalMs > 0)
            await Task.Delay(intervalMs, context.CancellationToken);

        return Output("tick", true);
    }

    private static ValueTask<IReadOnlyDictionary<string, object?>> HandleConstantBooleanAsync(
        FlowNodeExecutionContext context)
    {
        return ValueTask.FromResult(Output("value", context.GetParameter<bool>("value")));
    }

    private static ValueTask<IReadOnlyDictionary<string, object?>> HandleConstantNumberAsync(
        FlowNodeExecutionContext context)
    {
        return ValueTask.FromResult(Output("value", context.GetParameter<double>("value")));
    }

    private static ValueTask<IReadOnlyDictionary<string, object?>> HandleCompareAsync(
        FlowNodeExecutionContext context)
    {
        var left = context.GetInput<double>("left");
        var right = context.GetInput<double>("right");
        var op = context.GetParameter<string>("operator");

        var result = op switch
        {
            ">" => left > right,
            ">=" => left >= right,
            "<" => left < right,
            "<=" => left <= right,
            "==" => Math.Abs(left - right) < double.Epsilon,
            "!=" => Math.Abs(left - right) >= double.Epsilon,
            _ => throw new InvalidOperationException($"Unsupported compare operator '{op}'.")
        };

        return ValueTask.FromResult(Output("result", result));
    }

    private static async ValueTask<IReadOnlyDictionary<string, object?>> HandleGpioPinModeAsync(
        FlowNodeExecutionContext context)
    {
        var board = RequireBoard(context);
        var pin = context.GetParameter<int>("pin");
        var mode = ParsePinMode(context.GetParameter<string>("mode"));
        await board.Gpio.SetPinModeAsync(pin, mode, context.CancellationToken);
        return Output(("done", true), ("mode", mode.ToString()));
    }

    private static async ValueTask<IReadOnlyDictionary<string, object?>> HandleGpioDigitalReadAsync(
        FlowNodeExecutionContext context)
    {
        var board = RequireBoard(context);
        var pin = context.GetParameter<int>("pin");
        var value = await board.Gpio.DigitalReadAsync(pin, context.CancellationToken);
        return Output("value", value == PinValue.High);
    }

    private static async ValueTask<IReadOnlyDictionary<string, object?>> HandleGpioAnalogReadAsync(
        FlowNodeExecutionContext context)
    {
        var board = RequireBoard(context);
        var pin = context.GetParameter<int>("pin");

        // Averaging several readings tames the ESP32 ADC noise (it commonly jitters by +-30 counts).
        var samples = Math.Clamp(context.GetParameter<int>("samples"), 1, 64);
        if (samples == 1)
            return Output("value", await board.Gpio.AnalogReadAsync(pin, context.CancellationToken));

        long sum = 0;
        for (var i = 0; i < samples; i++)
            sum += await board.Gpio.AnalogReadAsync(pin, context.CancellationToken);

        return Output("value", (int)Math.Round(sum / (double)samples));
    }

    /// <summary>Linear range conversion shared by the runtime and the C# exporter's tests.</summary>
    internal static double MapRange(double value, double inMin, double inMax, double outMin, double outMax, bool clamp)
    {
        if (inMin == inMax)
            throw new InvalidOperationException("Map: the input range must not be empty (Input min equals Input max).");

        var t = (value - inMin) / (inMax - inMin);
        if (clamp)
            t = Math.Clamp(t, 0.0, 1.0);

        return outMin + t * (outMax - outMin);
    }

    private static ValueTask<IReadOnlyDictionary<string, object?>> HandleMapAsync(FlowNodeExecutionContext context)
    {
        var result = MapRange(
            context.GetInput<double>("value"),
            context.GetParameter<double>("inMin"),
            context.GetParameter<double>("inMax"),
            context.GetParameter<double>("outMin"),
            context.GetParameter<double>("outMax"),
            context.GetParameter<bool>("clamp"));

        if (context.GetParameter<bool>("round"))
            result = Math.Round(result);

        return ValueTask.FromResult(Output("result", result));
    }

    private static async ValueTask<IReadOnlyDictionary<string, object?>> HandlePwmWriteAsync(FlowNodeExecutionContext context)
    {
        var board = RequireBoard(context);
        var pin = context.GetParameter<int>("pin");
        var duty = (int)Math.Round(Math.Clamp(context.GetInputOrParameter<double>("duty", "duty"), 0, 255));
        var frequency = Math.Clamp(context.GetParameter<int>("frequencyHz"), 1, 40000);

        await board.Gpio.PwmWriteAsync(pin, duty, frequency, context.CancellationToken);
        return Output(("done", true), ("duty", duty));
    }

    private static async ValueTask<IReadOnlyDictionary<string, object?>> HandleGpioDigitalWriteAsync(
        FlowNodeExecutionContext context)
    {
        var board = RequireBoard(context);
        var pin = context.GetParameter<int>("pin");
        var value = context.GetInputOrParameter<bool>("value", "value");

        // Writing to a pin only drives it when it is an output (Arduino/ESP32 behaviour), so make sure it is one.
        // Without this the LED never lights unless the flow contains a separate Pin Mode block.
        if (!IsPinConfiguredByBlock(context, pin))
            await board.Gpio.SetPinModeAsync(pin, PinMode.Output, context.CancellationToken);

        await board.Gpio.DigitalWriteAsync(pin, value ? PinValue.High : PinValue.Low, context.CancellationToken);
        return Output(("done", true), ("value", value));
    }

    /// <summary>True when the flow already configures this pin with a Pin Mode, Digital Output or Blink LED block.</summary>
    private static bool IsPinConfiguredByBlock(FlowNodeExecutionContext context, int pin) =>
        context.Document.Nodes.Any(node =>
            (string.Equals(node.Type, BuiltInBlockCatalog.GpioPinMode, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(node.Type, BuiltInBlockCatalog.GpioSetOutput, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(node.Type, BuiltInBlockCatalog.GpioBlinkLed, StringComparison.OrdinalIgnoreCase)) &&
            node.Parameters.TryGetValue("pin", out var value) &&
            ParsesAs(value, pin));

    private static bool ParsesAs(object? value, int pin)
    {
        try
        {
            return FlowValueConverter.ConvertTo<int>(value, "pin") == pin;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async ValueTask<IReadOnlyDictionary<string, object?>> HandleGpioSetOutputAsync(
        FlowNodeExecutionContext context)
    {
        var board = RequireBoard(context);
        var pin = context.GetParameter<int>("pin");
        var requestedValue = context.GetParameter<bool>("value");
        var activeLow = context.GetParameter<bool>("activeLow");
        var outputValue = activeLow ? !requestedValue : requestedValue;

        await board.Gpio.SetPinModeAsync(pin, PinMode.Output, context.CancellationToken);
        await board.Gpio.DigitalWriteAsync(pin, outputValue ? PinValue.High : PinValue.Low, context.CancellationToken);
        return Output(
            ("done", true),
            ("value", requestedValue),
            ("pin", pin));
    }

    private static async ValueTask<IReadOnlyDictionary<string, object?>> HandleGpioBlinkLedAsync(
        FlowNodeExecutionContext context)
    {
        var board = RequireBoard(context);
        var pin = context.GetParameter<int>("pin");
        var durationMs = Math.Max(0, context.GetParameter<int>("durationMs"));
        var activeLow = context.GetParameter<bool>("activeLow");
        var leaveOn = context.GetParameter<bool>("leaveOn");
        var onValue = activeLow ? PinValue.Low : PinValue.High;
        var offValue = activeLow ? PinValue.High : PinValue.Low;

        await board.Gpio.SetPinModeAsync(pin, PinMode.Output, context.CancellationToken);
        await board.Gpio.DigitalWriteAsync(pin, onValue, context.CancellationToken);
        if (durationMs > 0)
            await Task.Delay(durationMs, context.CancellationToken);

        if (!leaveOn)
            await board.Gpio.DigitalWriteAsync(pin, offValue, context.CancellationToken);

        return Output(
            ("done", true),
            ("pin", pin),
            ("durationMs", durationMs),
            ("value", leaveOn));
    }

    private static ValueTask<IReadOnlyDictionary<string, object?>> HandleSampleChannelAsync(
        FlowNodeExecutionContext context)
    {
        var plan = new SamplingPlan(
            context.GetParameter<int>("pin"),
            Enum.Parse<SamplingMode>(context.GetParameter<string>("mode"), ignoreCase: true),
            context.GetParameter<int>("sampleRateHz"),
            context.GetParameter<int>("bufferCapacity"),
            Enum.Parse<BackpressurePolicy>(context.GetParameter<string>("backpressure"), ignoreCase: true),
            context.GetParameter<bool>("analog"),
            context.GetParameter<int>("batchSize"));

        var board = BuiltInBoardProfiles.FindById(context.Document.BoardId) ?? BuiltInBoardProfiles.Default;
        var errors = plan.Validate(board);
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

        var status = $"{(plan.Analog ? "Analog" : "Digital")} {plan.Mode} sampling on GPIO {plan.Pin} at {plan.SampleRateHz} Hz.";

        if (context.Board is IBoardWithAcquisition acquisitionBoard)
        {
            return StartBoardSampleChannelAsync(acquisitionBoard, plan, status, context.CancellationToken);
        }

        return ValueTask.FromResult(Output(
            ("samples", plan),
            ("status", status)));
    }

    private static async ValueTask<IReadOnlyDictionary<string, object?>> StartBoardSampleChannelAsync(
        IBoardWithAcquisition acquisitionBoard,
        SamplingPlan plan,
        string status,
        CancellationToken ct)
    {
        var channel = await acquisitionBoard.Acquisition.StartSamplingAsync(
            new BoardSampleChannelRequest(
                plan.Pin,
                plan.Analog,
                MapSamplingMode(plan.Mode),
                plan.SampleRateHz,
                plan.BufferCapacity,
                MapBackpressure(plan.Backpressure),
                plan.BatchSize),
            ct);

        return Output(
            ("samples", channel),
            ("status", $"{status} Channel {channel.Id} started."));
    }

    private static ValueTask<IReadOnlyDictionary<string, object?>> HandleInterruptInputAsync(
        FlowNodeExecutionContext context)
    {
        var pin = context.GetParameter<int>("pin");
        var edge = context.GetParameter<string>("edge");
        var debounceMs = context.GetParameter<int>("debounceMs");
        var status = $"Interrupt armed on GPIO {pin}, edge {edge}, debounce {debounceMs} ms.";

        return ValueTask.FromResult(Output(
            ("changed", true),
            ("value", false),
            ("status", status)));
    }

    private static ValueTask<IReadOnlyDictionary<string, object?>> HandleStreamDashboardAsync(
        FlowNodeExecutionContext context)
    {
        var channelName = context.GetParameter<string>("channelName");
        var maxPoints = context.GetParameter<int>("maxPoints");
        var samples = context.Inputs.TryGetValue("samples", out var value) ? value : null;

        if (samples is BoardSampleChannel channel && context.Board is IBoardWithAcquisition acquisitionBoard)
            return ReadDashboardSamplesAsync(acquisitionBoard, channel, channelName, maxPoints, context.CancellationToken);

        return ValueTask.FromResult(Output(
            ("done", true),
            ("channelName", channelName),
            ("maxPoints", maxPoints),
            ("samples", samples)));
    }

    private static async ValueTask<IReadOnlyDictionary<string, object?>> HandleServoWriteAsync(
        FlowNodeExecutionContext context)
    {
        var board = RequireBoard(context);
        var pin = context.GetParameter<int>("pin");
        var angle = Math.Clamp(context.GetInputOrParameter<double>("angle", "angle"), 0, 180);
        var minPulseUs = context.GetParameter<int>("minPulseUs");
        var maxPulseUs = context.GetParameter<int>("maxPulseUs");

        var attachResponse = await board.Transport.SendCommandAsync(
            BridgeProtocol.BuildCommand(BridgeProtocol.CMD_SERVO_ATTACH, pin, minPulseUs, maxPulseUs),
            context.CancellationToken);
        var attach = BridgeProtocol.ParseResponse(attachResponse);
        if (!attach.Success)
            throw new InvalidOperationException($"Servo attach failed: {attach.Data}");

        var writeResponse = await board.Transport.SendCommandAsync(
            BridgeProtocol.BuildCommand(BridgeProtocol.CMD_SERVO_WRITE, pin, (int)Math.Round(angle)),
            context.CancellationToken);
        var write = BridgeProtocol.ParseResponse(writeResponse);
        if (!write.Success)
            throw new InvalidOperationException($"Servo write failed: {write.Data}");

        return Output(
            ("done", true),
            ("pin", pin),
            ("angle", angle));
    }

    private static async ValueTask<IReadOnlyDictionary<string, object?>> ReadDashboardSamplesAsync(
        IBoardWithAcquisition acquisitionBoard,
        BoardSampleChannel channel,
        string channelName,
        int maxPoints,
        CancellationToken ct)
    {
        var maxFrames = Math.Clamp(maxPoints, 1, channel.Request.BatchSize);
        var frames = await acquisitionBoard.Acquisition.ReadSamplesAsync(channel.Id, maxFrames, ct);

        return Output(
            ("done", true),
            ("channelName", channelName),
            ("maxPoints", maxPoints),
            ("samples", frames),
            ("sampleCount", frames.Count));
    }

    private static BoardSamplingMode MapSamplingMode(SamplingMode mode) => mode switch
    {
        SamplingMode.Polling => BoardSamplingMode.Polling,
        SamplingMode.HardwareTimer => BoardSamplingMode.HardwareTimer,
        SamplingMode.Interrupt => BoardSamplingMode.Interrupt,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    private static BoardBackpressurePolicy MapBackpressure(BackpressurePolicy policy) => policy switch
    {
        BackpressurePolicy.DropOldest => BoardBackpressurePolicy.DropOldest,
        BackpressurePolicy.DropNewest => BoardBackpressurePolicy.DropNewest,
        BackpressurePolicy.Aggregate => BoardBackpressurePolicy.Aggregate,
        BackpressurePolicy.Pause => BoardBackpressurePolicy.Pause,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
    };

    private static ValueTask<IReadOnlyDictionary<string, object?>> HandleDebugLogAsync(
        FlowNodeExecutionContext context)
    {
        var label = context.GetParameter<string>("label");
        var fallbackMessage = context.GetParameter<string>("message");
        var hasValue = context.Inputs.TryGetValue("value", out var value);
        var message = hasValue
            ? $"{label}: {FormatDebugValue(value)}"
            : string.IsNullOrWhiteSpace(fallbackMessage) ? label : fallbackMessage;

        return ValueTask.FromResult(Output(
            ("done", true),
            ("message", message),
            ("value", hasValue ? value : null)));
    }

    private static IBoard RequireBoard(FlowNodeExecutionContext context)
    {
        return context.Board ?? throw new InvalidOperationException(
            $"Block '{context.Node.Type}' requires a connected board.");
    }

    private static PinMode ParsePinMode(string mode)
    {
        var normalized = mode.Trim().Replace("-", "", StringComparison.OrdinalIgnoreCase);

        return normalized.ToLowerInvariant() switch
        {
            "input" or "in" or "0" => PinMode.Input,
            "output" or "out" or "1" => PinMode.Output,
            "inputpullup" or "pullup" or "2" => PinMode.InputPullUp,
            "inputpulldown" or "pulldown" or "3" => PinMode.InputPullDown,
            "analog" or "adc" or "4" => PinMode.Analog,
            _ => throw new InvalidOperationException(
                $"Unsupported pin mode '{mode}'. Use Input, Output, InputPullUp, InputPullDown, or Analog.")
        };
    }

    private static IReadOnlyDictionary<string, object?> Output(string name, object? value)
    {
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [name] = value
        };
    }

    private static IReadOnlyDictionary<string, object?> Output(params (string Name, object? Value)[] values)
    {
        return values.ToDictionary(value => value.Name, value => value.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string FormatDebugValue(object? value)
    {
        return value switch
        {
            null => "<null>",
            bool boolean => boolean ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }
}
