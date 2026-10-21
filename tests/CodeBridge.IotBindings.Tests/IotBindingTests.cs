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

namespace CodeBridge.IotBindings.Tests;

/// <summary>The .NET IoT drivers run on a board through <see cref="BridgeI2cDevice"/>, and their blocks behave like the built-in ones.</summary>
public class IotBindingTests
{
    static IotBindingTests() => IotBindingBlocks.Register();

    private static readonly DateTime Clock = new(2026, 10, 14, 10, 15, 30);

    public static IEnumerable<object[]> AllBlocks() => IotBindingBlocks.All.Select(b => new object[] { b.Type });

    /// <summary>The blocks whose virtual chip is modelled here (the laser distance sensor has a start-up exchange of dozens of registers).</summary>
    public static IEnumerable<object[]> RunnableBlocks() =>
        IotBindingBlocks.All.Where(b => b.Type != IotBindingBlocks.Vl53L0X).Select(b => new object[] { b.Type });

    private static SimulatedTransport NewTransport(out Pca9685Chip pwm)
    {
        var transport = new SimulatedTransport(new SimulatedBoardOptions());
        pwm = new Pca9685Chip();
        transport.AddI2cDevice(0x44, new Sht3xChip(23.5, 48));
        transport.AddI2cDevice(0x18, new Mcp9808Chip(21.25));
        transport.AddI2cDevice(0x5A, new Mlx90614Chip(0x5A, 24.0, 36.6));
        transport.AddI2cDevice(0x48, new Ads1115Chip());
        transport.AddI2cDevice(0x68, new Ds3231Chip(Clock, 24.25));
        transport.AddI2cDevice(0x40, pwm);
        return transport;
    }

    private static async Task<ESP32Board> ConnectAsync(SimulatedTransport transport)
    {
        var board = new ESP32Board(transport, 39);
        await board.ConnectAsync();
        return board;
    }

    /// <summary>Manual trigger -> the block -> one Debug per data output, so every output is used.</summary>
    private static FlowDocument Flow(string type, string boardId = "esp32-devkit", Action<FlowNode>? configure = null)
    {
        var definition = BuiltInBlockCatalog.Create(BuiltInBoardProfiles.FindById(boardId)!).Get(type);
        var document = new FlowDocument { BoardId = boardId, Name = "Iot" };
        document.Nodes.Add(new FlowNode { Id = "start", Type = BuiltInBlockCatalog.ManualTrigger });
        var node = new FlowNode { Id = "block", Type = type };
        configure?.Invoke(node);
        document.Nodes.Add(node);
        document.Connections.Add(new FlowConnection { Id = "t", FromNodeId = "start", FromPort = "trigger", ToNodeId = "block", ToPort = "trigger" });

        foreach (var output in definition.OutputPorts.Where(p => p.ValueKind != FlowValueKind.Trigger))
        {
            var id = "debug-" + output.Name;
            document.Nodes.Add(new FlowNode { Id = id, Type = BuiltInBlockCatalog.DebugLog, Parameters = { ["label"] = output.Name } });
            document.Connections.Add(new FlowConnection { Id = "d-" + output.Name, FromNodeId = "block", FromPort = output.Name, ToNodeId = id, ToPort = "value" });
        }

        return document;
    }

    private static async Task<FlowExecutionResult> RunAsync(FlowDocument document, ESP32Board board) =>
        await new FlowRuntime(BuiltInBlockCatalog.Create(BuiltInBoardProfiles.FindById(document.BoardId)!)).ExecuteAsync(document, board);

    private static Assembly Compile(string code)
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).ToList();
        foreach (var assembly in new[] { typeof(IBoard).Assembly, typeof(ESP32Board).Assembly, typeof(CodeBridge.Transport.BoardDiscovery).Assembly, typeof(FlowDocument).Assembly, typeof(BridgeI2cDevice).Assembly })
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

    // ---------------------------------------------------------------- the adapter

    [Fact]
    public async Task A_register_read_is_one_command_and_a_longer_write_is_a_write_then_a_read()
    {
        var transport = NewTransport(out _);
        var board = await ConnectAsync(transport);
        var device = new BridgeI2cDevice(board.I2C, 0x18);
        var before = transport.CommandLog.Count;

        var word = new byte[2];
        device.WriteRead([0x05], word);
        device.Write([0x01, 0x02]);
        device.Read(word);

        var commands = transport.CommandLog.Skip(before).ToList();
        Assert.Equal("IRR:24:5:2", commands[0]);
        Assert.StartsWith("IW:24:", commands[1]);
        Assert.Equal("IR:24:2", commands[2]);
        Assert.Equal(0x18, device.ConnectionSettings.DeviceAddress);
    }

    [Fact]
    public async Task An_address_must_fit_in_seven_bits()
    {
        var board = await ConnectAsync(NewTransport(out _));

        Assert.Throws<ArgumentOutOfRangeException>(() => new BridgeI2cDevice(board.I2C, 0x80));
    }

    [Fact]
    public async Task The_real_driver_reads_a_chip_through_the_board()
    {
        var board = await ConnectAsync(NewTransport(out _));
        using var sensor = new Iot.Device.Mcp9808.Mcp9808(new BridgeI2cDevice(board.I2C, 0x18));

        Assert.Equal(21.25, sensor.Temperature.DegreesCelsius, 2);
    }

    // ---------------------------------------------------------------- the blocks

    [Fact]
    public void The_blocks_join_the_catalog_of_ESP32_boards_and_not_of_Arduino_ones()
    {
        var esp = BuiltInBlockCatalog.Create(BuiltInBoardProfiles.Esp32DevKit);
        var uno = BuiltInBlockCatalog.Create(BuiltInBoardProfiles.ArduinoUno);

        Assert.All(IotBindingBlocks.All, block => Assert.NotNull(esp.Get(block.Type)));
        Assert.All(IotBindingBlocks.All, block => Assert.DoesNotContain(uno.Blocks, b => b.Type == block.Type));
        Assert.True(ModuleBlocks.TryGet(IotBindingBlocks.Sht3x, out _));
    }

    [Fact]
    public void Registering_twice_changes_nothing()
    {
        var before = ModuleBlocks.All.Count;

        IotBindingBlocks.Register();
        IotBindingBlocks.Register();

        Assert.Equal(before, ModuleBlocks.All.Count);
        Assert.Equal(7, IotBindingBlocks.All.Count);
    }

    [Theory]
    [MemberData(nameof(AllBlocks))]
    public void A_block_with_its_defaults_is_a_valid_flow_and_every_default_is_one_of_its_options(string type)
    {
        var definition = BuiltInBlockCatalog.Create(BuiltInBoardProfiles.Esp32DevKit).Get(type);
        foreach (var property in definition.Properties.Where(p => p.Options is { Count: > 0 }))
            Assert.Contains(property.Options!, o => Equals(Convert.ToString(o.Value, System.Globalization.CultureInfo.InvariantCulture), Convert.ToString(property.DefaultValue, System.Globalization.CultureInfo.InvariantCulture)));

        var validation = new FlowValidator().Validate(Flow(type), BuiltInBlockCatalog.Create(BuiltInBoardProfiles.Esp32DevKit));

        Assert.True(validation.IsValid, string.Join("; ", validation.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void On_a_board_whose_I2C_pins_differ_the_user_is_reminded_to_check_the_wiring()
    {
        var validation = new FlowValidator().Validate(Flow(IotBindingBlocks.Sht3x, "esp32-s3-devkit"), BuiltInBlockCatalog.Create(BuiltInBoardProfiles.FindById("esp32-s3-devkit")!));

        Assert.Contains(validation.Warnings, w => w.Message.Contains("I2C pins"));
    }

    [Theory]
    [InlineData(IotBindingBlocks.Sht3x, "temperature", 23.5)]
    [InlineData(IotBindingBlocks.Sht3x, "humidity", 48.0)]
    [InlineData(IotBindingBlocks.Mcp9808, "temperature", 21.25)]
    [InlineData(IotBindingBlocks.Mlx90614, "object", 36.6)]
    [InlineData(IotBindingBlocks.Mlx90614, "ambient", 24.0)]
    [InlineData(IotBindingBlocks.Ds3231, "temperature", 24.25)]
    public async Task A_sensor_block_returns_what_the_chip_measures(string type, string output, double expected)
    {
        var board = await ConnectAsync(NewTransport(out _));

        var result = await RunAsync(Flow(type), board);

        Assert.Equal(expected, Assert.IsType<double>(result.NodeOutputs["block"][output]), 1);
    }

    [Fact]
    public async Task The_clock_block_reads_the_date_and_time()
    {
        var board = await ConnectAsync(NewTransport(out _));

        var result = await RunAsync(Flow(IotBindingBlocks.Ds3231), board);

        Assert.Equal("2026-10-14 10:15:30", result.NodeOutputs["block"]["time"]);
    }

    [Theory]
    [InlineData(0, 8192, 1.024)]
    [InlineData(2, 24576, 3.072)]
    public async Task The_converter_block_reads_the_chosen_channel(int channel, int raw, double volts)
    {
        var board = await ConnectAsync(NewTransport(out _));

        var result = await RunAsync(Flow(IotBindingBlocks.Ads1115, configure: n => n.Parameters["channel"] = channel), board);

        Assert.Equal(raw, Assert.IsType<double>(result.NodeOutputs["block"]["raw"]));
        Assert.Equal(volts, Assert.IsType<double>(result.NodeOutputs["block"]["voltage"]), 3);
    }

    [Fact]
    public async Task The_PWM_driver_block_sets_the_duty_cycle_of_the_chosen_channel()
    {
        var board = await ConnectAsync(NewTransport(out var chip));

        await RunAsync(Flow(IotBindingBlocks.Pca9685, configure: n => { n.Parameters["channel"] = 2; n.Parameters["duty"] = 7.5; }), board);

        Assert.InRange(chip.OffCount(2), 305, 310);   // 7.5 % of 4096
    }

    [Fact]
    public async Task A_second_run_does_not_start_the_driver_again()
    {
        var transport = NewTransport(out _);
        var board = await ConnectAsync(transport);
        var document = Flow(IotBindingBlocks.Mcp9808);

        await RunAsync(document, board);
        var first = transport.CommandLog.Count;
        await RunAsync(document, board);
        var second = transport.CommandLog.Count - first;

        Assert.True(second <= first, $"The first run used {first} commands, the second {second}.");
    }

    [Fact]
    public async Task A_board_without_the_chip_gives_a_message_the_user_can_act_on()
    {
        var transport = new SimulatedTransport(new SimulatedBoardOptions());
        transport.AddI2cDevice(0x77, new Mcp9808Chip(20));   // something is on the bus, but not at 0x44
        var board = await ConnectAsync(transport);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(Flow(IotBindingBlocks.Sht3x), board));

        Assert.Contains("Check the wiring", failure.ToString());
    }

    // ---------------------------------------------------------------- the exporter

    [Theory]
    [MemberData(nameof(AllBlocks))]
    public void The_exported_code_compiles(string type)
    {
        var generated = FlowCSharpGenerator.Generate(Flow(type));

        Compile(generated.Code);

        Assert.Empty(generated.Warnings);
    }

    [Theory]
    [MemberData(nameof(RunnableBlocks))]
    public async Task The_exported_code_talks_to_the_board_like_the_flow_does(string type)
    {
        var document = Flow(type);
        var generated = FlowCSharpGenerator.Generate(document);
        var assembly = Compile(generated.Code);

        var expectedTransport = NewTransport(out _);
        await RunAsync(document, await ConnectAsync(expectedTransport));
        var expected = expectedTransport.CommandLog.Where(c => c[0] == 'I').ToList();

        var transport = NewTransport(out _);
        var board = await ConnectAsync(transport);
        var run = assembly.GetTypes().Single(t => t.Name == generated.ClassName).GetMethod("RunAsync")!;
        await (Task)run.Invoke(null, new object[] { board, CancellationToken.None })!;

        Assert.NotEmpty(expected);
        Assert.Equal(expected, transport.CommandLog.Where(c => c[0] == 'I').ToList());
    }
}
