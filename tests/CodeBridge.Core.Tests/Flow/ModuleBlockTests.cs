using System.Reflection;
using CodeBridge.Core.Abstractions;
using CodeBridge.ESP32;
using CodeBridge.Flow;
using CodeBridge.Flow.CodeGeneration;
using CodeBridge.Flow.Execution;
using CodeBridge.Flow.Validation;
using CodeBridge.Transport.Simulation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeBridge.Core.Tests.Flow;

/// <summary>The sensor, module and helper blocks: each one is described once, and the catalog, the runtime, the exporter and the validator must agree.</summary>
public class ModuleBlockTests
{
    public static IEnumerable<object[]> Blocks() => ModuleBlocks.All.OrderBy(b => b.Type).Select(b => new object[] { b.Type });

    public static IEnumerable<object[]> Esp32Boards() =>
        BuiltInBoardProfiles.All.Where(b => b.Family == CodeBridge.Core.Enums.BoardFamily.ESP32).Select(b => new object[] { b.Id });

    private static readonly string[] HelperTypes = [ModuleBlocks.Threshold, ModuleBlocks.Gate, ModuleBlocks.Calc];

    /// <summary>Manual trigger -> the block (every input that needs a value gets a constant) -> one Debug per output, so every output is used.</summary>
    private static FlowDocument Flow(string type, string boardId = "esp32-devkit")
    {
        var profile = BuiltInBoardProfiles.FindById(boardId)!;
        var definition = BuiltInBlockCatalog.Create(profile).Get(type);
        var document = new FlowDocument { BoardId = boardId, Name = "Module" };
        document.Nodes.Add(new FlowNode { Id = "start", Type = BuiltInBlockCatalog.ManualTrigger });
        document.Nodes.Add(new FlowNode { Id = "block", Type = type });

        if (definition.FindInput("trigger") is not null)
            document.Connections.Add(new FlowConnection { Id = "t", FromNodeId = "start", FromPort = "trigger", ToNodeId = "block", ToPort = "trigger" });

        // required data inputs (the helpers) come from constants
        foreach (var input in definition.InputPorts.Where(p => p.Required && p.ValueKind != FlowValueKind.Trigger))
        {
            var constantType = input.ValueKind == FlowValueKind.Boolean ? BuiltInBlockCatalog.ConstantBoolean : BuiltInBlockCatalog.ConstantNumber;
            var id = "const-" + input.Name;
            document.Nodes.Add(new FlowNode { Id = id, Type = constantType, Parameters = { ["value"] = input.ValueKind == FlowValueKind.Boolean ? true : 30.0 } });
            document.Connections.Add(new FlowConnection { Id = "c-" + input.Name, FromNodeId = id, FromPort = "value", ToNodeId = "block", ToPort = input.Name });
        }

        foreach (var output in definition.OutputPorts.Where(p => p.ValueKind != FlowValueKind.Trigger))
        {
            var id = "debug-" + output.Name;
            document.Nodes.Add(new FlowNode { Id = id, Type = BuiltInBlockCatalog.DebugLog, Parameters = { ["label"] = output.Name } });
            document.Connections.Add(new FlowConnection { Id = "d-" + output.Name, FromNodeId = "block", FromPort = output.Name, ToNodeId = id, ToPort = "value" });
        }

        return document;
    }

    private static async Task<(IReadOnlyList<string> Commands, FlowExecutionResult Result)> RunRuntimeAsync(FlowDocument document)
    {
        var profile = BuiltInBoardProfiles.FindById(document.BoardId)!;
        var transport = new SimulatedTransport(new SimulatedBoardOptions { AnalogMax = profile.AnalogMaxValue, MaxPin = profile.MaxPin });
        var board = new ESP32Board(transport, profile.MaxPin);
        await board.ConnectAsync();
        var before = transport.CommandLog.Count;
        var result = await new FlowRuntime(BuiltInBlockCatalog.Create(profile)).ExecuteAsync(document, board);
        return (transport.CommandLog.Skip(before).ToList(), result);
    }

    private static Assembly Compile(string code)
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).ToList();
        foreach (var assembly in new[] { typeof(IBoard).Assembly, typeof(ESP32Board).Assembly, typeof(CodeBridge.Transport.BoardDiscovery).Assembly, typeof(FlowDocument).Assembly })
            paths.Add(assembly.Location);

        var compilation = CSharpCompilation.Create(
            "Exported_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.Latest)) },
            paths.Distinct().Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToList(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        var problems = result.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString()).ToList();
        Assert.True(result.Success && problems.Count == 0, "Generated code does not compile cleanly:" + Environment.NewLine + string.Join(Environment.NewLine, problems) + Environment.NewLine + code);
        return Assembly.Load(stream.ToArray());
    }

    [Fact]
    public void The_catalog_offers_sensors_and_modules_on_ESP32_boards_and_only_the_GPIO_ones_on_Arduino()
    {
        var esp = BuiltInBlockCatalog.Create(BuiltInBoardProfiles.Esp32DevKit);
        var uno = BuiltInBlockCatalog.Create(BuiltInBoardProfiles.ArduinoUno);

        Assert.All(ModuleBlocks.All, block => Assert.NotNull(esp.Get(block.Type)));
        Assert.Contains(uno.Blocks, b => b.Type == ModuleBlocks.Button);
        Assert.Contains(uno.Blocks, b => b.Type == ModuleBlocks.Relay);
        Assert.Contains(uno.Blocks, b => b.Type == ModuleBlocks.Calc);
        Assert.DoesNotContain(uno.Blocks, b => b.Type == ModuleBlocks.Dht);
        Assert.DoesNotContain(uno.Blocks, b => b.Type == ModuleBlocks.Lcd);
        Assert.All(ModuleBlocks.All.Where(b => !b.Esp32Only), block => Assert.NotNull(uno.Get(block.Type)));
        Assert.True(ModuleBlocks.All.Count >= 20);
    }

    [Fact]
    public void Blocks_are_grouped_so_the_toolbox_stays_readable()
    {
        var categories = ModuleBlocks.All.Select(b => b.Category).Distinct().OrderBy(c => c).ToArray();

        Assert.Contains("Sensors", categories);
        Assert.Contains("Modules", categories);
        Assert.Equal(11, ModuleBlocks.All.Count(b => b.Category == "Sensors"));
        Assert.Equal(8, ModuleBlocks.All.Count(b => b.Category == "Modules"));
    }

    [Theory]
    [MemberData(nameof(Blocks))]
    public void Every_property_and_port_has_a_type_and_the_defaults_make_sense(string type)
    {
        var block = ModuleBlocks.All.Single(b => b.Type == type);
        var definition = BuiltInBlockCatalog.Create(BuiltInBoardProfiles.Esp32DevKit).Get(type);

        Assert.False(string.IsNullOrWhiteSpace(block.Description));
        Assert.NotEmpty(definition.Ports);
        Assert.Equal(definition.Ports.Count, definition.Ports.Select(p => p.Name + p.Direction).Distinct().Count());
        foreach (var property in definition.Properties.Where(p => p.Options is { Count: > 0 }))
            Assert.Contains(property.Options!, o => Equals(Convert.ToString(o.Value, System.Globalization.CultureInfo.InvariantCulture), Convert.ToString(property.DefaultValue, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Theory]
    [MemberData(nameof(Blocks))]
    public void A_block_with_its_defaults_is_a_valid_flow_on_every_ESP32_board(string type)
    {
        foreach (var board in Esp32Boards().Select(o => (string)o[0]))
        {
            var validation = new FlowValidator().Validate(Flow(type, board), BuiltInBlockCatalog.Create(BuiltInBoardProfiles.FindById(board)!));

            Assert.True(validation.IsValid, $"{type} on {board}: " + string.Join("; ", validation.Errors.Select(e => e.Message)));
        }
    }

    [Theory]
    [MemberData(nameof(Blocks))]
    public async Task A_block_runs_on_the_virtual_board_and_returns_the_outputs_it_declares(string type)
    {
        var document = Flow(type);
        var definition = BuiltInBlockCatalog.Create(BuiltInBoardProfiles.Esp32DevKit).Get(type);

        var (commands, result) = await RunRuntimeAsync(document);

        foreach (var output in definition.OutputPorts.Where(p => p.ValueKind != FlowValueKind.Trigger))
        {
            Assert.True(result.NodeOutputs["block"].TryGetValue(output.Name, out var value), $"{type} did not produce '{output.Name}'.");
            Assert.NotNull(value);
            switch (output.ValueKind)
            {
                case FlowValueKind.Boolean: Assert.IsType<bool>(value); break;
                case FlowValueKind.Integer: Assert.IsType<int>(value); break;
                case FlowValueKind.Number: Assert.IsType<double>(value); break;
            }
        }

        if (!HelperTypes.Contains(type))
            Assert.NotEmpty(commands); // a real sensor or module talks to the board
    }

    [Theory]
    [MemberData(nameof(Blocks))]
    public async Task The_exported_code_compiles_and_sends_what_the_flow_sends(string type)
    {
        var document = Flow(type);
        var generated = FlowCSharpGenerator.Generate(document);

        var assembly = Compile(generated.Code);
        Assert.Empty(generated.Warnings);

        var (expected, _) = await RunRuntimeAsync(document);

        var profile = BuiltInBoardProfiles.Esp32DevKit;
        var transport = new SimulatedTransport(new SimulatedBoardOptions { AnalogMax = profile.AnalogMaxValue, MaxPin = profile.MaxPin });
        var board = new ESP32Board(transport, profile.MaxPin);
        await board.ConnectAsync();
        var before = transport.CommandLog.Count;
        var run = assembly.GetTypes().Single(t => t.Name == generated.ClassName).GetMethod("RunAsync")!;
        await (Task)run.Invoke(null, new object[] { board, CancellationToken.None })!;

        Assert.Equal(expected, transport.CommandLog.Skip(before).ToList());
    }

    [Fact]
    public void A_sensor_on_an_input_only_pin_that_must_also_drive_it_is_refused()
    {
        var document = Flow(ModuleBlocks.Dht);
        document.Nodes.Single(n => n.Id == "block").Parameters["pin"] = 34; // input only on the ESP32

        var validation = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create(BuiltInBoardProfiles.Esp32DevKit));

        Assert.Contains(validation.Errors, e => e.Message.Contains("GPIO 34"));
    }

    [Fact]
    public void Two_blocks_cannot_drive_the_same_pin_and_a_stepper_needs_four_different_pins()
    {
        var document = Flow(ModuleBlocks.Relay);
        document.Nodes.Add(new FlowNode { Id = "led", Type = BuiltInBlockCatalog.GpioSetOutput, Parameters = { ["pin"] = 2, ["value"] = true } });
        document.Nodes.Single(n => n.Id == "block").Parameters["pin"] = 2;
        document.Connections.Add(new FlowConnection { Id = "t2", FromNodeId = "start", FromPort = "trigger", ToNodeId = "led", ToPort = "trigger" });
        // a relay and a digital output on one pin are both outputs: allowed. A relay and an analog read are not.
        document.Nodes.Add(new FlowNode { Id = "adc", Type = BuiltInBlockCatalog.GpioAnalogRead, Parameters = { ["pin"] = 2 } });
        var catalog = BuiltInBlockCatalog.Create(BuiltInBoardProfiles.Esp32DevKit);
        Assert.Contains(new FlowValidator().Validate(document, catalog).Errors, e => e.Message.Contains("GPIO 2") && e.Message.Contains("conflicting"));

        var stepper = Flow(ModuleBlocks.Stepper);
        stepper.Nodes.Single(n => n.Id == "block").Parameters["pin3"] = stepper.Nodes.Single(n => n.Id == "block").Parameters.TryGetValue("pin1", out var p1) ? p1 : 19;
        stepper.Nodes.Single(n => n.Id == "block").Parameters["pin1"] = 19;
        stepper.Nodes.Single(n => n.Id == "block").Parameters["pin3"] = 19;
        Assert.Contains(new FlowValidator().Validate(stepper, catalog).Errors, e => e.Message.Contains("Each wire needs its own pin"));
    }

    [Fact]
    public async Task The_relay_obeys_active_low_and_a_wired_state()
    {
        var document = Flow(ModuleBlocks.Relay);
        document.Nodes.Single(n => n.Id == "block").Parameters["pin"] = 26;
        document.Nodes.Add(new FlowNode { Id = "on", Type = BuiltInBlockCatalog.ConstantBoolean, Parameters = { ["value"] = true } });
        document.Connections.Add(new FlowConnection { Id = "s", FromNodeId = "on", FromPort = "value", ToNodeId = "block", ToPort = "state" });

        var (commands, _) = await RunRuntimeAsync(document);

        Assert.Contains("DW:26:0", commands.Select(c => c.Trim())); // active low: ON is LOW
        Assert.Contains("PM:26:1", commands.Select(c => c.Trim()));
    }

    [Fact]
    public async Task The_LCD_shows_the_wired_value_in_its_text_and_pads_the_line()
    {
        var document = Flow(ModuleBlocks.Lcd);
        document.Nodes.Single(n => n.Id == "block").Parameters["text"] = "T={value}C";
        document.Nodes.Add(new FlowNode { Id = "number", Type = BuiltInBlockCatalog.ConstantNumber, Parameters = { ["value"] = 23.456 } });
        document.Connections.Add(new FlowConnection { Id = "v", FromNodeId = "number", FromPort = "value", ToNodeId = "block", ToPort = "value" });

        var (commands, _) = await RunRuntimeAsync(document);

        Assert.Contains(commands, c => c.Contains(Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes("T=23.46C")), StringComparison.OrdinalIgnoreCase) || c.Contains("T=23.46C"));
    }

    [Theory]
    [InlineData("Hello {value}", 3.14159, 0, "Hello 3.14")]
    [InlineData("{value}%", 100.0, 8, "100%    ")]
    [InlineData("Door {value}", true, 0, "Door true")]
    [InlineData("No value", null, 0, "No value")]
    [InlineData("A very long line", null, 6, "A very")]
    public void Display_text_formats_the_value_and_fits_the_width(string template, object? value, int width, string expected) =>
        Assert.Equal(expected, ModuleText.Format(template, value, width));

    [Fact]
    public async Task The_helpers_compute_what_their_names_say()
    {
        async Task<object?> Result(string type, Action<FlowDocument> tweak, string port = "result")
        {
            var document = Flow(type);
            tweak(document);
            var (_, result) = await RunRuntimeAsync(document);
            return result.NodeOutputs["block"][port];
        }

        Assert.Equal(true, await Result(ModuleBlocks.Threshold, d => d.Nodes.Single(n => n.Id == "block").Parameters["limit"] = 25.0));              // 30 > 25
        Assert.Equal(false, await Result(ModuleBlocks.Threshold, d => d.Nodes.Single(n => n.Id == "block").Parameters["mode"] = "below"));            // 30 < 25 is false
        Assert.Equal(true, await Result(ModuleBlocks.Gate, d => d.Nodes.Single(n => n.Id == "block").Parameters["operation"] = "not") is false);        // NOT true = false
        Assert.Equal(60.0, await Result(ModuleBlocks.Calc, d => d.Nodes.Single(n => n.Id == "block").Parameters["b"] = 2.0));                          // 30 x 2
        Assert.Equal(15.0, await Result(ModuleBlocks.Calc, d => { var n = d.Nodes.Single(x => x.Id == "block"); n.Parameters["operation"] = "divide"; n.Parameters["b"] = 2.0; }));
    }

    /// <summary>The pattern the guides suggest: temperature -> threshold -> relay, plus the reading on an LCD.</summary>
    private static FlowDocument Thermostat()
    {
        var document = new FlowDocument { BoardId = "esp32-devkit", Name = "Thermostat" };
        document.Nodes.Add(new FlowNode { Id = "start", Type = BuiltInBlockCatalog.ManualTrigger });
        document.Nodes.Add(new FlowNode { Id = "dht", Type = ModuleBlocks.Dht, Parameters = { ["pin"] = 4 } });
        document.Nodes.Add(new FlowNode { Id = "hot", Type = ModuleBlocks.Threshold, Parameters = { ["limit"] = 10.0 } });
        document.Nodes.Add(new FlowNode { Id = "fan", Type = ModuleBlocks.Relay, Parameters = { ["pin"] = 26 } });
        document.Nodes.Add(new FlowNode { Id = "screen", Type = ModuleBlocks.Lcd, Parameters = { ["text"] = "Temp: {value} C" } });
        document.Connections.Add(new FlowConnection { Id = "1", FromNodeId = "start", FromPort = "trigger", ToNodeId = "dht", ToPort = "trigger" });
        document.Connections.Add(new FlowConnection { Id = "2", FromNodeId = "dht", FromPort = "temperature", ToNodeId = "hot", ToPort = "value" });
        document.Connections.Add(new FlowConnection { Id = "3", FromNodeId = "hot", FromPort = "result", ToNodeId = "fan", ToPort = "state" });
        document.Connections.Add(new FlowConnection { Id = "4", FromNodeId = "start", FromPort = "trigger", ToNodeId = "fan", ToPort = "trigger" });
        document.Connections.Add(new FlowConnection { Id = "5", FromNodeId = "dht", FromPort = "temperature", ToNodeId = "screen", ToPort = "value" });
        document.Connections.Add(new FlowConnection { Id = "6", FromNodeId = "start", FromPort = "trigger", ToNodeId = "screen", ToPort = "trigger" });
        return document;
    }

    [Fact]
    public async Task A_sensor_can_drive_a_decision_a_relay_and_a_display_and_the_export_does_the_same()
    {
        var document = Thermostat();
        var validation = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create(BuiltInBoardProfiles.Esp32DevKit));
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors.Select(e => e.Message)));

        var (commands, result) = await RunRuntimeAsync(document);
        Assert.Equal(true, result.NodeOutputs["hot"]["result"]);               // the simulated room is warmer than 10 degrees
        Assert.Contains("DW:26:0", commands.Select(c => c.Trim()));            // so the fan relay (active low) is switched on
        Assert.Contains(commands, c => c.StartsWith("LT:"));                   // and the text reached the LCD

        var generated = FlowCSharpGenerator.Generate(document);
        Assert.Empty(generated.Warnings);
        Compile(generated.Code);
    }

    [Fact]
    public async Task Dividing_by_zero_is_an_error_not_infinity()
    {
        var document = Flow(ModuleBlocks.Calc);
        var node = document.Nodes.Single(n => n.Id == "block");
        node.Parameters["operation"] = "divide";
        node.Parameters["b"] = 0.0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunRuntimeAsync(document));
    }
}
