using CodeBridge.Flow;

namespace CodeBridge.Core.Tests.Flow;

public class BoardManifestTests
{
    public static IEnumerable<object[]> Boards() => BuiltInBoardProfiles.All.Select(b => new object[] { b.Id });

    [Theory]
    [MemberData(nameof(Boards))]
    public void Every_board_has_a_complete_manifest(string id)
    {
        var board = BuiltInBoardProfiles.FindById(id)!;

        var manifest = BoardManifest.For(board);

        Assert.Equal(board.Pins.Count, manifest.Pins.Count);
        Assert.Equal(board.AnalogMaxValue, manifest.AnalogMax);
        Assert.NotEmpty(manifest.Rules);
        Assert.All(manifest.Pins, pin => Assert.Contains(pin.Safety, new[] { "ok", "boot-strap", "input-only", "reserved" }));
        Assert.All(manifest.Pins.Where(p => p.Safety != "ok"), pin => Assert.NotEmpty(pin.Notes));
        Assert.Contains(manifest.Rules, r => r.Contains("Map block"));
    }

    [Fact]
    public void Electrical_limits_follow_the_family()
    {
        Assert.Equal(3.3, BoardManifest.For(BuiltInBoardProfiles.Esp32DevKit).LogicLevelVolts);
        Assert.Equal(5.0, BoardManifest.For(BuiltInBoardProfiles.ArduinoUno).LogicLevelVolts);
        Assert.Contains(BoardManifest.For(BuiltInBoardProfiles.Esp32DevKit).Rules, r => r.Contains("NOT 5 V tolerant"));
    }

    [Fact]
    public void The_ESP32_manifest_warns_about_ADC2_and_input_only_pins_and_names_the_buses()
    {
        var manifest = BoardManifest.For(BuiltInBoardProfiles.Esp32DevKit);

        Assert.Contains(manifest.Rules, r => r.Contains("ADC2") && r.Contains("Wi-Fi"));
        Assert.Contains(manifest.Rules, r => r.Contains("34") && r.Contains("input only"));
        Assert.Contains(21, manifest.Buses["i2c-sda"]);
        Assert.Contains(22, manifest.Buses["i2c-scl"]);
        Assert.Equal(2, manifest.BuiltInLedPin);
    }

    [Fact]
    public void Arduino_manifests_warn_about_the_serial_pins_and_servo_pwm_conflict()
    {
        var manifest = BoardManifest.For(BuiltInBoardProfiles.ArduinoUno);

        Assert.Contains(manifest.Rules, r => r.Contains("Pins 0 and 1"));
        Assert.Contains(manifest.Rules, r => r.Contains("Servo library"));
        Assert.Equal("reserved", manifest.Pins.Single(p => p.Number == 0).Safety);
        Assert.Equal(13, manifest.BuiltInLedPin);
    }

    [Fact]
    public void The_manifest_never_offers_reserved_pins_as_usable_in_the_block_catalog()
    {
        foreach (var board in BuiltInBoardProfiles.All)
        {
            var reserved = BoardManifest.For(board).Pins.Where(p => p.Safety == "reserved").Select(p => p.Number).ToHashSet();
            foreach (var option in board.DigitalWritePinOptions)
                Assert.DoesNotContain((int)option.Value!, reserved);
        }
    }
}
