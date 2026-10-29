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

    /// <summary>The blocks that have a virtual chip below. The others (laser distance sensors, SCD4x, AHT20, compass and touch keys, whose start-up or protocol is long) are only checked for validity and export.</summary>
    private static readonly string[] Modelled =
    [
        IotBindingBlocks.Sht3x, IotBindingBlocks.Mcp9808, IotBindingBlocks.Mlx90614, IotBindingBlocks.Ads1115, IotBindingBlocks.Ds3231, IotBindingBlocks.Pca9685,
        IotBindingBlocks.Bmp280, IotBindingBlocks.Bme280, IotBindingBlocks.Lm75, IotBindingBlocks.Sht4x, IotBindingBlocks.Max44009, IotBindingBlocks.Ds1307, IotBindingBlocks.Pcf8563,
        IotBindingBlocks.SevenSegment, IotBindingBlocks.ExpanderWrite, IotBindingBlocks.ExpanderRead, IotBindingBlocks.I2cScan, IotBindingBlocks.I2cRead, IotBindingBlocks.I2cWrite
    ];

    public static IEnumerable<object[]> RunnableBlocks() => Modelled.Select(t => new object[] { t });

    /// <summary>The address a block uses when nothing is changed.</summary>
    private static int DefaultAddress(string type) =>
        Convert.ToInt32(BuiltInBlockCatalog.Create(BuiltInBoardProfiles.Esp32DevKit).Get(type).FindProperty("address")?.DefaultValue ?? 0x2A, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A bus with the chip a block talks to, at the address the block uses by default.</summary>
    private static SimulatedTransport NewTransport(string type, out Pca9685Chip pwm)
    {
        var transport = new SimulatedTransport(new SimulatedBoardOptions());
        pwm = new Pca9685Chip();
        var address = DefaultAddress(type);
        ISimulatedI2cDevice chip = type switch
        {
            IotBindingBlocks.Sht3x => new Sht3xChip(23.5, 48),
            IotBindingBlocks.Sht4x => new Sht3xChip(23.5, 48, sht4x: true),
            IotBindingBlocks.Mcp9808 => new Mcp9808Chip(21.25),
            IotBindingBlocks.Mlx90614 => new Mlx90614Chip((byte)address, 24.0, 36.6),
            IotBindingBlocks.Ads1115 => new Ads1115Chip(),
            IotBindingBlocks.Ds3231 => new Ds3231Chip(Clock, 24.25),
            IotBindingBlocks.Ds1307 => new Ds1307Chip(Clock),
            IotBindingBlocks.Pcf8563 => new Pcf8563Chip(Clock),
            IotBindingBlocks.Pca9685 => pwm,
            IotBindingBlocks.Bmp280 => new Bmp280Chip(),
            IotBindingBlocks.Bme280 => new Bme280Chip(),
            IotBindingBlocks.Lm75 => new Lm75Chip(21.5),
            IotBindingBlocks.Max44009 => new Max44009Chip(5, 0xAC),
            IotBindingBlocks.SevenSegment => new RecordingChip(),
            IotBindingBlocks.ExpanderWrite or IotBindingBlocks.ExpanderRead => new Pcf8574Chip(),
            _ => new PlainChip((0, 0x12), (1, 0x34), (2, 0x56), (3, 0x9A))
        };
        transport.AddI2cDevice(address, chip);
        if (type is IotBindingBlocks.I2cScan)
            transport.AddI2cDevice(0x76, new PlainChip());

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
        var transport = NewTransport(IotBindingBlocks.Mcp9808, out _);
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
        var board = await ConnectAsync(NewTransport(IotBindingBlocks.Mcp9808, out _));

        Assert.Throws<ArgumentOutOfRangeException>(() => new BridgeI2cDevice(board.I2C, 0x80));
    }

    [Fact]
    public async Task The_real_driver_reads_a_chip_through_the_board()
    {
        var board = await ConnectAsync(NewTransport(IotBindingBlocks.Mcp9808, out _));
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
        Assert.Equal(25, IotBindingBlocks.All.Count);
        Assert.Equal(IotBindingBlocks.All.Count, IotBindingBlocks.All.Select(b => b.Type).Distinct().Count());
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
        var board = await ConnectAsync(NewTransport(type, out _));

        var result = await RunAsync(Flow(type), board);

        Assert.Equal(expected, Assert.IsType<double>(result.NodeOutputs["block"][output]), 1);
    }

    [Fact]
    public async Task The_clock_block_reads_the_date_and_time()
    {
        var board = await ConnectAsync(NewTransport(IotBindingBlocks.Ds3231, out _));

        var result = await RunAsync(Flow(IotBindingBlocks.Ds3231), board);

        Assert.Equal("2026-10-14 10:15:30", result.NodeOutputs["block"]["time"]);
    }

    [Theory]
    [InlineData(0, 8192, 1.024)]
    [InlineData(2, 24576, 3.072)]
    public async Task The_converter_block_reads_the_chosen_channel(int channel, int raw, double volts)
    {
        var board = await ConnectAsync(NewTransport(IotBindingBlocks.Ads1115, out _));

        var result = await RunAsync(Flow(IotBindingBlocks.Ads1115, configure: n => n.Parameters["channel"] = channel), board);

        Assert.Equal(raw, Assert.IsType<double>(result.NodeOutputs["block"]["raw"]));
        Assert.Equal(volts, Assert.IsType<double>(result.NodeOutputs["block"]["voltage"]), 3);
    }

    [Fact]
    public async Task The_PWM_driver_block_sets_the_duty_cycle_of_the_chosen_channel()
    {
        var board = await ConnectAsync(NewTransport(IotBindingBlocks.Pca9685, out var chip));

        await RunAsync(Flow(IotBindingBlocks.Pca9685, configure: n => { n.Parameters["channel"] = 2; n.Parameters["duty"] = 7.5; }), board);

        Assert.InRange(chip.OffCount(2), 305, 310);   // 7.5 % of 4096
    }

    [Fact]
    public async Task A_second_run_does_not_start_the_driver_again()
    {
        var transport = NewTransport(IotBindingBlocks.Mcp9808, out _);
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

    [Theory]
    [InlineData(IotBindingBlocks.Bmp280, "pressure", 1006.53)]
    [InlineData(IotBindingBlocks.Bmp280, "temperature", 25.08)]
    [InlineData(IotBindingBlocks.Lm75, "temperature", 21.5)]
    [InlineData(IotBindingBlocks.Sht4x, "temperature", 23.5)]
    [InlineData(IotBindingBlocks.Sht4x, "humidity", 48.0)]
    [InlineData(IotBindingBlocks.Max44009, "lux", 247.68)]
    public async Task The_new_sensors_return_what_their_chips_measure(string type, string output, double expected)
    {
        var board = await ConnectAsync(NewTransport(type, out _));

        var result = await RunAsync(Flow(type), board);

        Assert.Equal(expected, Assert.IsType<double>(result.NodeOutputs["block"][output]), 1);
    }

    [Fact]
    public async Task The_weather_block_agrees_with_the_datasheet_formulas_on_the_registers_of_a_real_chip()
    {
        var chip = new Bme280Chip();
        var expected = Bme280Reference.Compute(chip.Snapshot());
        var transport = new SimulatedTransport(new SimulatedBoardOptions());
        transport.AddI2cDevice(0x76, chip);
        var board = await ConnectAsync(transport);

        var result = await RunAsync(Flow(IotBindingBlocks.Bme280), board);
        var outputs = result.NodeOutputs["block"];

        Assert.Equal(expected.Celsius, Assert.IsType<double>(outputs["temperature"]), 1);
        Assert.Equal(expected.Hectopascals, Assert.IsType<double>(outputs["pressure"]), 1);
        Assert.Equal(expected.Humidity, Assert.IsType<double>(outputs["humidity"]), 1);
        // and it is what the real chip showed on the desk: a cool room at the altitude of a high city
        Assert.InRange(Assert.IsType<double>(outputs["temperature"]), 19, 20);
        Assert.InRange(Assert.IsType<double>(outputs["humidity"]), 69, 72);
        Assert.InRange(Assert.IsType<double>(outputs["pressure"]), 715, 718);
        Assert.InRange(Assert.IsType<double>(outputs["altitude"]), 2500, 3200);   // the driver uses the temperature as well
    }

    [Fact]
    public async Task A_BME280_where_a_BMP280_was_expected_is_named_as_what_it_is()
    {
        var chip = new Bmp280Chip();
        chip.SetRegister(0xD0, 0x60);   // the id of a BME280
        var transport = new SimulatedTransport(new SimulatedBoardOptions());
        transport.AddI2cDevice(0x76, chip);
        var board = await ConnectAsync(transport);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(Flow(IotBindingBlocks.Bmp280, configure: n => n.Parameters["address"] = 0x76), board));

        Assert.Contains("BME280", failure.ToString());
        Assert.Contains("Weather (BME280)", failure.ToString());
    }

    [Fact]
    public async Task The_pressure_block_works_out_an_altitude_from_the_sea_level_pressure()
    {
        var board = await ConnectAsync(NewTransport(IotBindingBlocks.Bmp280, out _));

        var low = await RunAsync(Flow(IotBindingBlocks.Bmp280, configure: n => n.Parameters["seaLevelHpa"] = 1013.25), board);
        var high = await RunAsync(Flow(IotBindingBlocks.Bmp280, configure: n => n.Parameters["seaLevelHpa"] = 1030.0), board);

        // 1006.53 hPa is a little above sea level when the sea-level pressure is 1013.25 hPa, and higher still when it is 1030
        Assert.InRange(Assert.IsType<double>(low.NodeOutputs["block"]["altitude"]), 40, 80);
        Assert.True(Assert.IsType<double>(high.NodeOutputs["block"]["altitude"]) > Assert.IsType<double>(low.NodeOutputs["block"]["altitude"]));
    }

    [Theory]
    [InlineData(IotBindingBlocks.Ds1307)]
    [InlineData(IotBindingBlocks.Pcf8563)]
    public async Task The_other_clocks_read_the_date_and_time_too(string type)
    {
        var board = await ConnectAsync(NewTransport(type, out _));

        var result = await RunAsync(Flow(type), board);

        Assert.Equal("2026-10-14 10:15:30", result.NodeOutputs["block"]["time"]);
    }

    [Fact]
    public async Task The_expander_blocks_write_and_read_the_eight_pins()
    {
        var transport = NewTransport(IotBindingBlocks.ExpanderWrite, out _);
        var board = await ConnectAsync(transport);

        await RunAsync(Flow(IotBindingBlocks.ExpanderWrite, configure: n => n.Parameters["value"] = 0b1010_0101), board);
        var read = await RunAsync(Flow(IotBindingBlocks.ExpanderRead), board);

        Assert.Equal(0b1010_0101, Assert.IsType<double>(read.NodeOutputs["block"]["value"]));
    }

    [Theory]
    [InlineData(23.456, "23.46")]
    [InlineData(5, "5")]
    [InlineData(-4.5, "-4.5")]
    [InlineData(1234.5, "1235")]
    [InlineData(12345, "----")]
    [InlineData(0.1234, "0.123")]
    public void A_number_is_shown_with_as_many_digits_as_the_display_has(double value, string expected)
    {
        Assert.Equal(expected, IotHelpers.Format(value));
    }

    [Fact]
    public async Task The_number_display_sets_its_brightness_and_sends_the_digits()
    {
        var transport = NewTransport(IotBindingBlocks.SevenSegment, out _);
        var board = await ConnectAsync(transport);

        await RunAsync(Flow(IotBindingBlocks.SevenSegment, configure: n => { n.Parameters["value"] = 23.46; n.Parameters["brightness"] = 8; }), board);

        var writes = transport.CommandLog.Where(c => c.StartsWith("IW:112:")).ToList();
        Assert.Contains("IW:112:E8", writes);   // brightness 8 of 15

        // the buffer holds the four digits in order: 2, 3 with its decimal point (segment 0x80), 4 and 6 (the driver puts other bytes between them)
        var buffer = Convert.FromHexString(writes.Select(w => w["IW:112:".Length..]).Last(w => w.Length >= 18));
        byte Digit(int d) => (byte)Enum.Parse<Iot.Device.Display.Font>("Digit_" + d);
        var expected = new[] { Digit(2), (byte)(Digit(3) | 0x80), Digit(4), Digit(6) };
        var at = 0;
        foreach (var b in buffer.Skip(1))
        {
            if (at < expected.Length && b == expected[at])
                at++;
        }

        Assert.Equal(expected.Length, at);
    }

    [Fact]
    public async Task The_scan_block_lists_the_chips_that_answer()
    {
        var transport = new SimulatedTransport(new SimulatedBoardOptions());
        transport.AddI2cDevice(0x44, new PlainChip());
        transport.AddI2cDevice(0x76, new PlainChip());
        var board = await ConnectAsync(transport);

        var scan = await RunAsync(Flow(IotBindingBlocks.I2cScan), board);

        Assert.Equal("0x44, 0x76", scan.NodeOutputs["block"]["devices"]);
        Assert.Equal(2.0, scan.NodeOutputs["block"]["count"]);
    }

    [Fact]
    public async Task Read_register_gives_the_bytes_as_a_number_in_the_order_asked()
    {
        var transport = new SimulatedTransport(new SimulatedBoardOptions());
        transport.AddI2cDevice(0x40, new PlainChip((0, 0x12), (1, 0x34), (2, 0x56), (3, 0x9A)));
        var board = await ConnectAsync(transport);

        var big = await RunAsync(Flow(IotBindingBlocks.I2cRead, configure: n => { n.Parameters["address"] = 0x40; n.Parameters["register"] = 0; n.Parameters["length"] = 2; }), board);
        var little = await RunAsync(Flow(IotBindingBlocks.I2cRead, configure: n => { n.Parameters["address"] = 0x40; n.Parameters["register"] = 0; n.Parameters["length"] = 2; n.Parameters["bigEndian"] = false; }), board);
        var signed = await RunAsync(Flow(IotBindingBlocks.I2cRead, configure: n => { n.Parameters["address"] = 0x40; n.Parameters["register"] = 3; n.Parameters["length"] = 1; n.Parameters["signed"] = true; }), board);

        Assert.Equal(4660.0, big.NodeOutputs["block"]["value"]);
        Assert.Equal("1234", big.NodeOutputs["block"]["hex"]);
        Assert.Equal(13330.0, little.NodeOutputs["block"]["value"]);
        Assert.Equal(0x9A - 256.0, signed.NodeOutputs["block"]["value"]);
    }

    [Fact]
    public void An_empty_answer_from_an_address_nobody_listens_to_says_so()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => IotHelpers.Answered([], 2, 0x44));

        Assert.Contains("0x44", failure.Message);
        Assert.Contains("Check the wiring", failure.Message);
        Assert.Equal(new byte[] { 1, 2 }, IotHelpers.Answered([1, 2], 2, 0x44));
    }

    [Fact]
    public async Task Write_register_puts_the_number_in_the_chip_most_significant_byte_first()
    {
        var chip = new PlainChip();
        var transport = new SimulatedTransport(new SimulatedBoardOptions());
        transport.AddI2cDevice(0x40, chip);
        var board = await ConnectAsync(transport);

        await RunAsync(Flow(IotBindingBlocks.I2cWrite, configure: n => { n.Parameters["address"] = 0x40; n.Parameters["register"] = 4; n.Parameters["value"] = 0xABCD; n.Parameters["length"] = 2; }), board);

        Assert.Equal(0xAB, chip[4]);
        Assert.Equal(0xCD, chip[5]);
    }

    [Theory]
    [InlineData(new byte[] { 0x12, 0x34 }, true, false, 0x1234)]
    [InlineData(new byte[] { 0x12, 0x34 }, false, false, 0x3412)]
    [InlineData(new byte[] { 0xFF, 0xFE }, true, true, -2)]
    [InlineData(new byte[] { 0x80 }, true, true, -128)]
    [InlineData(new byte[] { 1, 2, 3, 4 }, true, false, 0x01020304)]
    public void Bytes_become_a_number_and_back(byte[] bytes, bool bigEndian, bool signed, double number)
    {
        Assert.Equal(number, IotHelpers.ToNumber(bytes, bigEndian, signed));
        if (!signed)
            Assert.Equal(bytes, IotHelpers.ToBytes(number, bytes.Length, bigEndian));
    }

    [Fact]
    public void The_touch_keys_become_one_number()
    {
        var channels = new Dictionary<string, bool> { ["Channel00"] = true, ["Channel03"] = true, ["Channel07"] = false, ["Channel11"] = true };

        Assert.Equal(1 + 8 + 2048, IotHelpers.TouchMask(channels));
    }

    [Fact]
    public void Each_block_has_a_type_that_starts_with_iot_and_a_description_worth_showing()
    {
        Assert.All(IotBindingBlocks.All, block =>
        {
            Assert.StartsWith("iot.", block.Type);
            Assert.True(block.Description.Length > 40, block.Type);
        });
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

        var expectedTransport = NewTransport(type, out _);
        await RunAsync(document, await ConnectAsync(expectedTransport));
        var expected = expectedTransport.CommandLog.Where(c => c[0] == 'I').ToList();

        var transport = NewTransport(type, out _);
        var board = await ConnectAsync(transport);
        var run = assembly.GetTypes().Single(t => t.Name == generated.ClassName).GetMethod("RunAsync")!;
        await (Task)run.Invoke(null, new object[] { board, CancellationToken.None })!;

        Assert.NotEmpty(expected);
        Assert.Equal(expected, transport.CommandLog.Where(c => c[0] == 'I').ToList());
    }
}
