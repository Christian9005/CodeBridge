using CodeBridge.Core.Enums;
using CodeBridge.Flow;

namespace CodeBridge.Core.Tests.Flow;

public class BoardProfilesTests
{
    public static IEnumerable<object[]> Boards() => BuiltInBoardProfiles.All.Select(b => new object[] { b.Id });

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "firmware")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("firmware folder not found.");
    }

    [Fact]
    public void Every_board_has_a_unique_id_and_name()
    {
        var boards = BuiltInBoardProfiles.All;

        Assert.Equal(boards.Count, boards.Select(b => b.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(boards.Count, boards.Select(b => b.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(boards, b => b.Id == "esp32-s3-devkit");
        Assert.Contains(boards, b => b.Id == "esp32-c3-devkit");
        Assert.Contains(boards, b => b.Id == "xiao-esp32-s3");
        Assert.Contains(boards, b => b.Id == "esp32-s2-mini");
        Assert.Contains(boards, b => b.Id == "arduino-nano");
        Assert.Contains(boards, b => b.Id == "arduino-mega");
    }

    [Theory]
    [MemberData(nameof(Boards))]
    public void Pin_definitions_are_consistent(string id)
    {
        var board = BuiltInBoardProfiles.FindById(id)!;

        Assert.Equal(board.Pins.Count, board.Pins.Select(p => p.Number).Distinct().Count());
        Assert.All(board.Pins, pin => Assert.InRange(pin.Number, 0, board.MaxPin));
        Assert.True(board.AnalogMaxValue is 4095 or 1023);
        Assert.NotEmpty(board.AnalogReadPinOptions);
        Assert.NotEmpty(board.DigitalWritePinOptions);
        Assert.NotEmpty(board.PwmPinOptions);

        // reserved pins (USB, UART, flash, PSRAM) are never offered for output
        foreach (var pin in board.Pins.Where(p => p.IsReserved))
            Assert.DoesNotContain(board.DigitalWritePinOptions, option => Equals(option.Value, pin.Number));
    }

    [Theory]
    [MemberData(nameof(Boards))]
    public void The_block_catalog_can_be_built_and_flows_validate_against_it(string id)
    {
        var board = BuiltInBoardProfiles.FindById(id)!;
        var catalog = BuiltInBlockCatalog.Create(board);
        var pin = board.DigitalWritePinOptions.First(o => !board.FindPin((int)o.Value!)!.IsBootStrap).Value;

        var document = new FlowDocument { Name = "Check", BoardId = id };
        document.Nodes.Add(new FlowNode { Id = "start", Type = BuiltInBlockCatalog.ManualTrigger });
        document.Nodes.Add(new FlowNode { Id = "led", Type = BuiltInBlockCatalog.GpioSetOutput, Parameters = { ["pin"] = pin } });
        document.Connections.Add(new FlowConnection { Id = "c", FromNodeId = "start", FromPort = "trigger", ToNodeId = "led", ToPort = "trigger" });

        var result = new CodeBridge.Flow.Validation.FlowValidator().Validate(document, catalog);

        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(i => i.Message)));
        Assert.Equal(board.AnalogMaxValue, Convert.ToInt32(catalog.Get(BuiltInBlockCatalog.MathMap).Properties.Single(p => p.Name == "inMax").DefaultValue));
    }

    [Theory]
    [MemberData(nameof(Boards))]
    public void Every_board_can_be_flashed_from_files_that_exist_in_the_repository(string id)
    {
        var board = BuiltInBoardProfiles.FindById(id)!;
        var firmware = Path.Combine(RepoRoot(), "firmware");

        Assert.True(Directory.Exists(Path.Combine(firmware, board.FirmwareProject)), $"{board.FirmwareProject} is missing.");

        if (board.Family == BoardFamily.ESP32)
        {
            Assert.False(string.IsNullOrEmpty(board.FlashChip));
            var folder = Path.Combine(firmware, "prebuilt", board.PrebuiltFolder!);
            foreach (var file in new[] { "firmware.bin", "bootloader.bin", "partitions.bin", "boot_app0.bin" })
                Assert.True(File.Exists(Path.Combine(folder, file)), $"{board.PrebuiltFolder}/{file} is missing.");
        }
        else
        {
            Assert.False(string.IsNullOrEmpty(board.Fqbn));
            Assert.StartsWith("arduino:avr:", board.Fqbn);
        }
    }

    [Fact]
    public void The_original_ESP32_keeps_the_classic_flash_layout_and_the_newer_chips_flash_the_bootloader_at_zero()
    {
        Assert.Equal(0x1000, BuiltInBoardProfiles.Esp32DevKit.BootloaderOffset);
        Assert.Equal("esp32", BuiltInBoardProfiles.Esp32DevKit.FlashChip);
        Assert.Equal(0, BuiltInBoardProfiles.Esp32S3DevKit.BootloaderOffset);
        Assert.Equal("esp32s3", BuiltInBoardProfiles.Esp32S3DevKit.FlashChip);
        Assert.Equal(0, BuiltInBoardProfiles.Esp32C3DevKit.BootloaderOffset);
        Assert.Equal("esp32c3", BuiltInBoardProfiles.Esp32C3DevKit.FlashChip);
        Assert.Equal(0, BuiltInBoardProfiles.XiaoEsp32S3.BootloaderOffset);
        Assert.Equal("esp32s3", BuiltInBoardProfiles.XiaoEsp32S3.FlashChip);
        Assert.Equal("xiaoesp32s3", BuiltInBoardProfiles.XiaoEsp32S3.PrebuiltFolder);
        Assert.Equal(0x1000, BuiltInBoardProfiles.Esp32S2Mini.BootloaderOffset);
        Assert.Equal("esp32s2", BuiltInBoardProfiles.Esp32S2Mini.FlashChip);
    }

    [Fact]
    public void Nano_extends_the_Uno_and_Mega_has_all_its_pins()
    {
        var uno = BuiltInBoardProfiles.ArduinoUno;
        var nano = BuiltInBoardProfiles.ArduinoNano;
        var mega = BuiltInBoardProfiles.ArduinoMega;

        Assert.All(uno.Pins, pin => Assert.NotNull(nano.FindPin(pin.Number)));
        Assert.True(nano.FindPin(20)!.SupportsAnalogRead && !nano.FindPin(20)!.SupportsDigitalWrite); // A6 is analog only
        Assert.Contains("atmega328old", BuiltInBoardProfiles.ArduinoNanoOldBootloader.Fqbn);

        Assert.Equal(70, mega.Pins.Count);
        Assert.Equal(16, mega.Pins.Count(p => p.SupportsAnalogRead));
        Assert.Equal(15, mega.Pins.Count(p => p.SupportsPwm));
        Assert.Equal(6, mega.Pins.Count(p => p.SupportsInterrupts));
        Assert.Equal(69, mega.MaxPin);
    }

    [Fact]
    public void Only_the_ESP32_family_supports_Wi_Fi()
    {
        Assert.All(BuiltInBoardProfiles.All, board => Assert.Equal(board.Family == BoardFamily.ESP32, board.SupportsWifi));
    }

    [Fact]
    public void Reserved_ESP32_S3_pins_are_kept_away_from_outputs_but_the_free_high_pins_work()
    {
        var s3 = BuiltInBoardProfiles.Esp32S3DevKit;

        foreach (var reserved in new[] { 19, 20, 35, 36, 37, 43, 44 })
            Assert.DoesNotContain(s3.DigitalWritePinOptions, o => Equals(o.Value, reserved));

        foreach (var free in new[] { 21, 38, 42, 47, 48 })
            Assert.Contains(s3.DigitalWritePinOptions, o => Equals(o.Value, free));

        Assert.Contains(s3.AnalogReadPinOptions, o => Equals(o.Value, 5));
    }
}
