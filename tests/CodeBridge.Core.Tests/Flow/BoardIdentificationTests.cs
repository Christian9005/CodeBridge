using CodeBridge.Flow;
using Xunit;

namespace CodeBridge.Core.Tests.Flow;

public class BoardIdentificationTests
{
    [Theory]
    [InlineData("ESP32-D0WDQ6", "esp32-devkit")]
    [InlineData("ESP32-D0WD-V3 (revision v3.0)", "esp32-devkit")]
    [InlineData("ESP32-PICO-D4", "esp32-devkit")]
    [InlineData("ESP32", "esp32-devkit")]
    [InlineData("ESP32-SIM", "esp32-devkit")]
    [InlineData("ESP32-S3", "esp32-s3-devkit")]
    [InlineData("ESP32-S3 (QFN56) rev 0.1", "esp32-s3-devkit")]
    [InlineData("esp32s3-sim", "esp32-s3-devkit")]
    [InlineData("ESP32-S2", "esp32-s2-mini")]
    [InlineData("ESP32-C3 (QFN32)", "esp32-c3-devkit")]
    [InlineData("ATmega328P", "arduino-uno")]
    [InlineData("ATmega2560", "arduino-mega")]
    public void FromChip_KnowsTheChipsOfTheProfiles(string chip, string expectedBoardId)
    {
        Assert.Equal(expectedBoardId, BoardIdentification.FromChip(chip)?.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ESP32-C6")] // a chip without a profile: no guess is better than the wrong profile
    [InlineData("ESP32-H2")]
    [InlineData("ESP32-P4")]
    [InlineData("STM32F103")]
    public void FromChip_DoesNotGuessWhatItDoesNotKnow(string? chip)
    {
        Assert.Null(BoardIdentification.FromChip(chip));
    }

    [Theory]
    [InlineData("2341", "0043", "arduino-uno")]
    [InlineData("2341", "0001", "arduino-uno")]
    [InlineData("2A03", "0043", "arduino-uno")]
    [InlineData("2341", "0042", "arduino-mega")]
    [InlineData("2341", "0010", "arduino-mega")]
    [InlineData("2341", "8036", "arduino-uno")] // any other Arduino keeps the old answer
    [InlineData("10C4", "EA60", "esp32-devkit")]
    [InlineData("10c4", "ea60", "esp32-devkit")]
    [InlineData("303A", "1001", "esp32-devkit")]
    [InlineData("1A86", "55D4", "esp32-devkit")]
    public void FromUsb_RecognisesTheBoardsByTheirUsbIds(string vendor, string product, string expectedBoardId)
    {
        Assert.Equal(expectedBoardId, BoardIdentification.FromUsb(vendor, product)?.Id);
    }

    [Theory]
    [InlineData("1A86", "7523")] // CH340: Nano clones and ESP32 boards alike
    [InlineData("0403", "6001")] // an FTDI cable
    [InlineData("0483", "5740")] // STM32
    [InlineData(null, null)]
    public void FromUsb_DoesNotGuessForABridgeThatSaysNothing(string? vendor, string? product)
    {
        Assert.Null(BoardIdentification.FromUsb(vendor, product));
    }

    [Theory]
    [InlineData(@"Silicon Labs CP210x USB to UART Bridge USB\VID_10C4&PID_EA60\0001", "esp32-devkit")]
    [InlineData(@"USB Serial Device USB\VID_2341&PID_0042\85735", "arduino-mega")]
    [InlineData("Arduino Uno", "arduino-uno")]
    [InlineData("Arduino Mega 2560", "arduino-mega")]
    [InlineData("USB JTAG/serial debug unit", "esp32-devkit")]
    [InlineData("Espressif ESP32 board", "esp32-devkit")]
    public void FromUsb_ReadsTheIdsOrTheNameFromTheText(string text, string expectedBoardId)
    {
        Assert.Equal(expectedBoardId, BoardIdentification.FromUsb(null, null, text)?.Id);
    }

    [Theory]
    [InlineData("Communications Port")]
    [InlineData(@"Standard Serial over Bluetooth link BTHENUM\{00001101}")]
    [InlineData("")]
    public void FromUsb_APortThatIsNotABoardIsNotOne(string text)
    {
        Assert.Null(BoardIdentification.FromUsb(null, null, text));
    }

    [Theory]
    [InlineData("esp32-devkit", "esp32-devkit", true)]
    [InlineData("esp32-s3-devkit", "xiao-esp32-s3", true)] // same chip, another shape
    [InlineData("xiao-esp32-s3", "esp32-s3-devkit", true)]
    [InlineData("esp32-devkit", "esp32-s3-devkit", false)] // other chip, other pins
    [InlineData("esp32-s3-devkit", "esp32-c3-devkit", false)]
    [InlineData("esp32-devkit", "arduino-uno", false)] // other family
    [InlineData("arduino-uno", "esp32-devkit", false)]
    [InlineData("arduino-uno", "arduino-mega", true)] // a Mega has every pin of an Uno
    [InlineData("arduino-mega", "arduino-uno", false)] // an Uno does not have the pins of a Mega
    public void IsCompatible_ToldByTheChipAndThePins(string flowBoardId, string connectedBoardId, bool expected)
    {
        var flow = BuiltInBoardProfiles.FindById(flowBoardId)!;
        var connected = BuiltInBoardProfiles.FindById(connectedBoardId)!;

        Assert.Equal(expected, BoardIdentification.IsCompatible(flow, connected));
    }
}
