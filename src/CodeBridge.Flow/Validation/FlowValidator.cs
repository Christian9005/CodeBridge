using CodeBridge.Flow;
using CodeBridge.Flow.Execution;
using CodeBridge.Flow.Streaming;

namespace CodeBridge.Flow.Validation;

/// <summary>
/// Validates graph shape, block availability, port compatibility, and cycles.
/// </summary>
public sealed class FlowValidator
{
    public FlowValidationResult Validate(FlowDocument document, FlowBlockCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);

        var issues = new List<FlowValidationIssue>();
        var board = ResolveBoard(document, issues);
        var nodesById = BuildNodeIndex(document, issues);
        ValidateNodes(document, catalog, board, issues);
        ValidatePinConflicts(document, catalog, board, issues);
        ValidateConnections(document, catalog, nodesById, issues);
        ValidateRequiredInputs(document, catalog, issues);
        ValidateCycles(document, nodesById, issues);

        return new FlowValidationResult(issues);
    }

    private static Dictionary<string, FlowNode> BuildNodeIndex(
        FlowDocument document,
        List<FlowValidationIssue> issues)
    {
        var nodesById = new Dictionary<string, FlowNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in document.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
            {
                issues.Add(new FlowValidationIssue(FlowValidationSeverity.Error, "Node id is required."));
                continue;
            }

            if (!nodesById.TryAdd(node.Id, node))
            {
                issues.Add(new FlowValidationIssue(
                    FlowValidationSeverity.Error,
                    $"Duplicate node id '{node.Id}'.",
                    node.Id));
            }
        }

        return nodesById;
    }

    private static void ValidateNodes(
        FlowDocument document,
        FlowBlockCatalog catalog,
        BoardProfile board,
        List<FlowValidationIssue> issues)
    {
        foreach (var node in document.Nodes)
        {
            if (!catalog.TryGet(node.Type, out var definition))
            {
                issues.Add(new FlowValidationIssue(
                    FlowValidationSeverity.Error,
                    $"Unknown block type '{node.Type}'.",
                    node.Id));
                continue;
            }

            foreach (var property in definition.Properties)
            {
                var hasValue = node.Parameters.ContainsKey(property.Name) || property.DefaultValue is not null;
                if (property.Required && !hasValue)
                {
                    issues.Add(new FlowValidationIssue(
                        FlowValidationSeverity.Error,
                        $"Required property '{property.Name}' is missing on block '{definition.Type}'.",
                        node.Id));
                }
            }

            foreach (var parameterName in node.Parameters.Keys)
            {
                if (definition.FindProperty(parameterName) is null)
                {
                    issues.Add(new FlowValidationIssue(
                        FlowValidationSeverity.Warning,
                        $"Parameter '{parameterName}' is not defined by block '{definition.Type}'.",
                        node.Id));
                }
            }

            ValidateBoardAwareNode(node, definition, board, issues);
        }
    }

    private static void ValidateBoardAwareNode(
        FlowNode node,
        FlowBlockDefinition definition,
        BoardProfile board,
        List<FlowValidationIssue> issues)
    {
        if (string.Equals(definition.Type, BuiltInBlockCatalog.GpioPinMode, StringComparison.OrdinalIgnoreCase))
        {
            ValidatePinMode(node, definition, board, issues);
            return;
        }

        if (string.Equals(definition.Type, BuiltInBlockCatalog.GpioDigitalRead, StringComparison.OrdinalIgnoreCase))
        {
            ValidatePinCapability(node, definition, board, "pin", PinCapability.DigitalRead, "digital read", issues);
            return;
        }

        if (string.Equals(definition.Type, BuiltInBlockCatalog.GpioAnalogRead, StringComparison.OrdinalIgnoreCase))
        {
            ValidatePinCapability(node, definition, board, "pin", PinCapability.AnalogRead, "analog read", issues);
            WarnForAdc2AnalogUse(node, definition, board, issues);
            return;
        }

        if (string.Equals(definition.Type, BuiltInBlockCatalog.GpioDigitalWrite, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(definition.Type, BuiltInBlockCatalog.GpioSetOutput, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(definition.Type, BuiltInBlockCatalog.GpioBlinkLed, StringComparison.OrdinalIgnoreCase))
        {
            ValidatePinCapability(node, definition, board, "pin", PinCapability.DigitalWrite, "digital output", issues);
            return;
        }

        if (string.Equals(definition.Type, BuiltInBlockCatalog.SampleChannel, StringComparison.OrdinalIgnoreCase))
        {
            ValidateSampleChannel(node, definition, board, issues);
            return;
        }

        if (string.Equals(definition.Type, BuiltInBlockCatalog.InterruptInput, StringComparison.OrdinalIgnoreCase))
        {
            ValidateInterruptInput(node, definition, board, issues);
            return;
        }

        if (string.Equals(definition.Type, BuiltInBlockCatalog.PwmWrite, StringComparison.OrdinalIgnoreCase))
        {
            ValidatePinCapability(node, definition, board, "pin", PinCapability.Pwm, "PWM output", issues);
            return;
        }

        if (string.Equals(definition.Type, BuiltInBlockCatalog.MathMap, StringComparison.OrdinalIgnoreCase))
        {
            if (GetValue<double>(node, definition, "inMin") == GetValue<double>(node, definition, "inMax"))
            {
                issues.Add(new FlowValidationIssue(
                    FlowValidationSeverity.Error,
                    "Map: Input min and Input max are equal, so the input range is empty.",
                    node.Id));
            }

            return;
        }

        if (string.Equals(definition.Type, BuiltInBlockCatalog.ServoWrite, StringComparison.OrdinalIgnoreCase))
            ValidateServoWrite(node, definition, board, issues);

        if (ModuleBlocks.TryGet(definition.Type, out var module))
        {
            foreach (var modulePin in module.Pins)
                ValidatePinCapability(node, definition, board, modulePin.Property, modulePin.Needs, modulePin.What, issues);

            ValidateModuleSpecifics(node, definition, board, module, issues);
        }
    }

    private static void ValidateModuleSpecifics(
        FlowNode node,
        FlowBlockDefinition definition,
        BoardProfile board,
        ModuleBlock module,
        List<FlowValidationIssue> issues)
    {
        // The same pin cannot be two things inside one block (for example the three coils of a stepper on one GPIO).
        var own = module.Pins.Select(pin => (pin.Property, Number: GetValue<int>(node, definition, pin.Property))).ToList();
        foreach (var duplicate in own.GroupBy(p => p.Number).Where(g => g.Count() > 1))
        {
            issues.Add(new FlowValidationIssue(
                FlowValidationSeverity.Error,
                $"{module.DisplayName}: GPIO {duplicate.Key} is used for {string.Join(" and ", duplicate.Select(d => d.Property))}. Each wire needs its own pin.",
                node.Id));
        }

        if ((module.UsesI2c || module.Type is ModuleBlocks.Bme280 or ModuleBlocks.Bh1750 or ModuleBlocks.Mpu6050 or ModuleBlocks.Ina219 or ModuleBlocks.ColorSensor or ModuleBlocks.Lcd or ModuleBlocks.Oled) &&
            board.Family == CodeBridge.Core.Enums.BoardFamily.ESP32 && board.Id != "esp32-devkit")
        {
            issues.Add(new FlowValidationIssue(
                FlowValidationSeverity.Warning,
                $"{module.DisplayName} uses the I2C pins of {board.DisplayName}; check that SDA and SCL are wired to the pins this board's I2C bus uses.",
                node.Id));
        }

        foreach (var analogPin in module.Pins.Where(pin => pin.Role == ModulePinRole.AnalogInput))
        {
            if (board.FindPin(GetValue<int>(node, definition, analogPin.Property))?.HasCapability(PinCapability.Adc2) == true)
            {
                issues.Add(new FlowValidationIssue(
                    FlowValidationSeverity.Warning,
                    $"{module.DisplayName}: GPIO {GetValue<int>(node, definition, analogPin.Property)} is an ADC2 pin; its analog reads are unreliable while Wi-Fi is on. Prefer an ADC1 pin.",
                    node.Id));
            }
        }
    }

    private static BoardProfile ResolveBoard(
        FlowDocument document,
        List<FlowValidationIssue> issues)
    {
        var board = BuiltInBoardProfiles.FindById(document.BoardId);
        if (board is not null)
            return board;

        issues.Add(new FlowValidationIssue(
            FlowValidationSeverity.Error,
            $"Unknown board profile '{document.BoardId}'."));

        return BuiltInBoardProfiles.Default;
    }

    private static void ValidatePinMode(
        FlowNode node,
        FlowBlockDefinition definition,
        BoardProfile board,
        List<FlowValidationIssue> issues)
    {
        var mode = GetValue<string>(node, definition, "mode");
        var required = mode?.ToLowerInvariant() switch
        {
            "output" => PinCapability.DigitalWrite,
            "input" or "inputpullup" or "inputpulldown" => PinCapability.DigitalRead,
            "analog" => PinCapability.AnalogRead,
            _ => PinCapability.None
        };

        if (required == PinCapability.None)
        {
            issues.Add(new FlowValidationIssue(
                FlowValidationSeverity.Error,
                $"Unsupported pin mode '{mode}'.",
                node.Id));
            return;
        }

        ValidatePinCapability(node, definition, board, "pin", required, $"pin mode {mode}", issues);

        if (required == PinCapability.AnalogRead)
            WarnForAdc2AnalogUse(node, definition, board, issues);
    }

    private static void ValidateSampleChannel(
        FlowNode node,
        FlowBlockDefinition definition,
        BoardProfile board,
        List<FlowValidationIssue> issues)
    {
        var pin = GetValue<int>(node, definition, "pin");
        var modeValue = GetValue<string>(node, definition, "mode");
        var sampleRateHz = GetValue<int>(node, definition, "sampleRateHz");
        var bufferCapacity = GetValue<int>(node, definition, "bufferCapacity");
        var backpressureValue = GetValue<string>(node, definition, "backpressure");
        var analog = GetValue<bool>(node, definition, "analog");
        var batchSize = GetValue<int>(node, definition, "batchSize");

        if (!TryParseEnum(modeValue, out SamplingMode mode))
        {
            issues.Add(new FlowValidationIssue(
                FlowValidationSeverity.Error,
                $"Unsupported sampling mode '{modeValue}'.",
                node.Id));
            return;
        }

        if (!TryParseEnum(backpressureValue, out BackpressurePolicy backpressure))
        {
            issues.Add(new FlowValidationIssue(
                FlowValidationSeverity.Error,
                $"Unsupported backpressure policy '{backpressureValue}'.",
                node.Id));
            return;
        }

        var plan = new SamplingPlan(
            pin,
            mode,
            sampleRateHz,
            bufferCapacity,
            backpressure,
            analog,
            batchSize);

        foreach (var error in plan.Validate(board))
        {
            issues.Add(new FlowValidationIssue(FlowValidationSeverity.Error, error, node.Id));
        }

        if (analog)
            WarnForAdc2AnalogUse(node, definition, board, issues);
    }

    private static void ValidateInterruptInput(
        FlowNode node,
        FlowBlockDefinition definition,
        BoardProfile board,
        List<FlowValidationIssue> issues)
    {
        var pinNumber = GetValue<int>(node, definition, "pin");
        var pin = board.FindPin(pinNumber);

        if (pin is null || pin.IsReserved)
        {
            issues.Add(new FlowValidationIssue(
                FlowValidationSeverity.Error,
                $"GPIO {pinNumber} is not available on {board.DisplayName}.",
                node.Id));
            return;
        }

        if (!pin.SupportsInterrupts)
        {
            issues.Add(new FlowValidationIssue(
                FlowValidationSeverity.Error,
                $"GPIO {pinNumber} does not support interrupts.",
                node.Id));
        }

        var debounceMs = GetValue<int>(node, definition, "debounceMs");
        if (debounceMs < 0)
        {
            issues.Add(new FlowValidationIssue(
                FlowValidationSeverity.Error,
                "Interrupt debounce must be zero or greater.",
                node.Id));
        }
    }

    private static void ValidateServoWrite(
        FlowNode node,
        FlowBlockDefinition definition,
        BoardProfile board,
        List<FlowValidationIssue> issues)
    {
        var pinNumber = GetValue<int>(node, definition, "pin");
        var pin = board.FindPin(pinNumber);

        if (pin is null || pin.IsReserved)
        {
            issues.Add(new FlowValidationIssue(
                FlowValidationSeverity.Error,
                $"GPIO {pinNumber} is not available on {board.DisplayName}.",
                node.Id));
            return;
        }

        if (!pin.SupportsServo || !pin.SupportsPwm || !pin.SupportsDigitalWrite || pin.IsBootStrap || pin.IsInputOnly)
        {
            issues.Add(new FlowValidationIssue(
                FlowValidationSeverity.Error,
                $"GPIO {pinNumber} is not a recommended servo/PWM pin on {board.DisplayName}. Use one of: {GetRecommendedPins(board.ServoPinOptions)}.",
                node.Id));
        }
    }

    private static void ValidatePinCapability(
        FlowNode node,
        FlowBlockDefinition definition,
        BoardProfile board,
        string propertyName,
        PinCapability required,
        string operation,
        List<FlowValidationIssue> issues)
    {
        var pinNumber = GetValue<int>(node, definition, propertyName);
        var pin = board.FindPin(pinNumber);

        if (pin is null || pin.IsReserved)
        {
            issues.Add(new FlowValidationIssue(
                FlowValidationSeverity.Error,
                $"GPIO {pinNumber} is not available on {board.DisplayName}.",
                node.Id));
            return;
        }

        if (!pin.HasCapability(required))
        {
            issues.Add(new FlowValidationIssue(
                FlowValidationSeverity.Error,
                $"GPIO {pinNumber} does not support {operation} on {board.DisplayName}.",
                node.Id));
        }
    }

    private static void WarnForAdc2AnalogUse(
        FlowNode node,
        FlowBlockDefinition definition,
        BoardProfile board,
        List<FlowValidationIssue> issues)
    {
        var pinNumber = GetValue<int>(node, definition, "pin");
        var pin = board.FindPin(pinNumber);

        if (pin?.HasCapability(PinCapability.Adc2) == true)
        {
            issues.Add(new FlowValidationIssue(
                FlowValidationSeverity.Warning,
                $"GPIO {pinNumber} uses ADC2 on {board.DisplayName}; analog reads can conflict with WiFi.",
                node.Id));
        }
    }

    private static void ValidatePinConflicts(
        FlowDocument document,
        FlowBlockCatalog catalog,
        BoardProfile board,
        List<FlowValidationIssue> issues)
    {
        var usages = document.Nodes
            .SelectMany(node => GetPinUsages(node, catalog, board))
            .GroupBy(usage => usage.Pin)
            .ToArray();

        foreach (var pinUsages in usages)
        {
            var roles = pinUsages.Select(usage => usage.Role).Distinct().ToArray();
            if (roles.Length <= 1)
                continue;

            if (roles.Contains(PinUsageRole.Servo) && roles.Any(role => role != PinUsageRole.Servo))
            {
                AddPinConflict(pinUsages, issues, "Servo pins should be dedicated. Remove the other use of this GPIO or pick another recommended servo pin.");
                continue;
            }

            if (roles.Contains(PinUsageRole.DigitalOutput) &&
                (roles.Contains(PinUsageRole.DigitalInput) ||
                 roles.Contains(PinUsageRole.AnalogInput) ||
                 roles.Contains(PinUsageRole.InterruptInput)))
            {
                AddPinConflict(pinUsages, issues, "The same GPIO is configured as both output and input.");
                continue;
            }

            if (roles.Contains(PinUsageRole.AnalogInput) && roles.Contains(PinUsageRole.DigitalInput))
            {
                AddPinConflict(pinUsages, issues, "The same GPIO is used as both analog and digital input.");
            }
        }
    }

    private static IEnumerable<PinUsage> GetPinUsages(
        FlowNode node,
        FlowBlockCatalog catalog,
        BoardProfile board)
    {
        if (!catalog.TryGet(node.Type, out var definition))
            yield break;

        if (ModuleBlocks.TryGet(definition.Type, out var module))
        {
            foreach (var modulePin in module.Pins)
            {
                var modulePinNumber = GetValue<int>(node, definition, modulePin.Property);
                if (board.FindPin(modulePinNumber) is not null)
                {
                    yield return new PinUsage(modulePinNumber, node.Id, modulePin.Role switch
                    {
                        ModulePinRole.DigitalInput => PinUsageRole.DigitalInput,
                        ModulePinRole.AnalogInput => PinUsageRole.AnalogInput,
                        _ => PinUsageRole.DigitalOutput
                    });
                }
            }

            yield break;
        }

        PinUsageRole? role = definition.Type switch
        {
            BuiltInBlockCatalog.GpioDigitalRead => PinUsageRole.DigitalInput,
            BuiltInBlockCatalog.GpioAnalogRead => PinUsageRole.AnalogInput,
            BuiltInBlockCatalog.GpioDigitalWrite => PinUsageRole.DigitalOutput,
            BuiltInBlockCatalog.GpioSetOutput => PinUsageRole.DigitalOutput,
            BuiltInBlockCatalog.GpioBlinkLed => PinUsageRole.DigitalOutput,
            BuiltInBlockCatalog.SampleChannel => GetValue<bool>(node, definition, "analog") ? PinUsageRole.AnalogInput : PinUsageRole.DigitalInput,
            BuiltInBlockCatalog.InterruptInput => PinUsageRole.InterruptInput,
            BuiltInBlockCatalog.ServoWrite => PinUsageRole.Servo,
            BuiltInBlockCatalog.GpioPinMode => GetPinModeUsage(node, definition),
            _ => null
        };

        if (role is null)
            yield break;

        var pin = GetValue<int>(node, definition, "pin");
        if (board.FindPin(pin) is not null)
            yield return new PinUsage(pin, node.Id, role.Value);
    }

    private static PinUsageRole? GetPinModeUsage(FlowNode node, FlowBlockDefinition definition)
    {
        return GetValue<string>(node, definition, "mode")?.ToLowerInvariant() switch
        {
            "output" => PinUsageRole.DigitalOutput,
            "input" or "inputpullup" or "inputpulldown" => PinUsageRole.DigitalInput,
            "analog" => PinUsageRole.AnalogInput,
            _ => null
        };
    }

    private static void AddPinConflict(
        IEnumerable<PinUsage> pinUsages,
        List<FlowValidationIssue> issues,
        string detail)
    {
        var usages = pinUsages.ToArray();
        var pin = usages[0].Pin;
        var roleList = string.Join(", ", usages.Select(usage => GetRoleDisplayName(usage.Role)).Distinct());

        foreach (var usage in usages)
        {
            issues.Add(new FlowValidationIssue(
                FlowValidationSeverity.Error,
                $"GPIO {pin} has conflicting uses: {roleList}. {detail}",
                usage.NodeId));
        }
    }

    private static string GetRoleDisplayName(PinUsageRole role) =>
        role switch
        {
            PinUsageRole.DigitalInput => "digital input",
            PinUsageRole.DigitalOutput => "digital output",
            PinUsageRole.AnalogInput => "analog input",
            PinUsageRole.InterruptInput => "interrupt input",
            PinUsageRole.Servo => "servo",
            _ => role.ToString()
        };

    private static string GetRecommendedPins(IReadOnlyList<FlowPropertyOption> options) =>
        string.Join(", ", options.Select(option => option.Value));

    private static void ValidateConnections(
        FlowDocument document,
        FlowBlockCatalog catalog,
        Dictionary<string, FlowNode> nodesById,
        List<FlowValidationIssue> issues)
    {
        var connectionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connectedInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var connection in document.Connections)
        {
            if (!connectionIds.Add(connection.Id))
            {
                issues.Add(new FlowValidationIssue(
                    FlowValidationSeverity.Error,
                    $"Duplicate connection id '{connection.Id}'.",
                    ConnectionId: connection.Id));
            }

            if (!nodesById.TryGetValue(connection.FromNodeId, out var fromNode))
            {
                issues.Add(new FlowValidationIssue(
                    FlowValidationSeverity.Error,
                    $"Connection source node '{connection.FromNodeId}' does not exist.",
                    ConnectionId: connection.Id));
                continue;
            }

            if (!nodesById.TryGetValue(connection.ToNodeId, out var toNode))
            {
                issues.Add(new FlowValidationIssue(
                    FlowValidationSeverity.Error,
                    $"Connection target node '{connection.ToNodeId}' does not exist.",
                    ConnectionId: connection.Id));
                continue;
            }

            if (!catalog.TryGet(fromNode.Type, out var fromDefinition) ||
                !catalog.TryGet(toNode.Type, out var toDefinition))
            {
                continue;
            }

            var fromPort = fromDefinition.FindOutput(connection.FromPort);
            if (fromPort is null)
            {
                issues.Add(new FlowValidationIssue(
                    FlowValidationSeverity.Error,
                    $"Output port '{connection.FromPort}' does not exist on block '{fromNode.Type}'.",
                    fromNode.Id,
                    connection.Id));
                continue;
            }

            var toPort = toDefinition.FindInput(connection.ToPort);
            if (toPort is null)
            {
                issues.Add(new FlowValidationIssue(
                    FlowValidationSeverity.Error,
                    $"Input port '{connection.ToPort}' does not exist on block '{toNode.Type}'.",
                    toNode.Id,
                    connection.Id));
                continue;
            }

            if (!FlowTypeCompatibility.AreCompatible(fromPort.ValueKind, toPort.ValueKind))
            {
                issues.Add(new FlowValidationIssue(
                    FlowValidationSeverity.Error,
                    $"Cannot connect '{fromPort.ValueKind}' output to '{toPort.ValueKind}' input.",
                    toNode.Id,
                    connection.Id));
            }

            var inputKey = $"{connection.ToNodeId}:{connection.ToPort}";
            if (!connectedInputs.Add(inputKey))
            {
                issues.Add(new FlowValidationIssue(
                    FlowValidationSeverity.Error,
                    $"Input '{connection.ToPort}' on node '{connection.ToNodeId}' has multiple connections.",
                    toNode.Id,
                    connection.Id));
            }
        }
    }

    private static void ValidateRequiredInputs(
        FlowDocument document,
        FlowBlockCatalog catalog,
        List<FlowValidationIssue> issues)
    {
        foreach (var node in document.Nodes)
        {
            if (!catalog.TryGet(node.Type, out var definition))
                continue;

            foreach (var input in definition.InputPorts.Where(port => port.Required))
            {
                var hasConnection = document.Connections.Any(connection =>
                    string.Equals(connection.ToNodeId, node.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(connection.ToPort, input.Name, StringComparison.OrdinalIgnoreCase));

                if (!hasConnection)
                {
                    issues.Add(new FlowValidationIssue(
                        FlowValidationSeverity.Error,
                        $"Required input '{input.Name}' is not connected on block '{node.Type}'.",
                        node.Id));
                }
            }
        }
    }

    private static void ValidateCycles(
        FlowDocument document,
        Dictionary<string, FlowNode> nodesById,
        List<FlowValidationIssue> issues)
    {
        try
        {
            FlowGraphSorter.Sort(document, nodesById);
        }
        catch (InvalidOperationException ex)
        {
            issues.Add(new FlowValidationIssue(FlowValidationSeverity.Error, ex.Message));
        }
    }

    private static T GetValue<T>(FlowNode node, FlowBlockDefinition definition, string propertyName)
    {
        if (node.Parameters.TryGetValue(propertyName, out var value))
            return FlowValueConverter.ConvertTo<T>(value, propertyName);

        var property = definition.FindProperty(propertyName);
        if (property?.DefaultValue is not null)
            return FlowValueConverter.ConvertTo<T>(property.DefaultValue, propertyName);

        return default!;
    }

    private static bool TryParseEnum<TEnum>(string? value, out TEnum result)
        where TEnum : struct
    {
        return Enum.TryParse(value, ignoreCase: true, out result);
    }

    private sealed record PinUsage(int Pin, string NodeId, PinUsageRole Role);

    private enum PinUsageRole
    {
        DigitalInput,
        DigitalOutput,
        AnalogInput,
        InterruptInput,
        Servo
    }
}
