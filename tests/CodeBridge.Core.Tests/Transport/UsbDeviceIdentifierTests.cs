using CodeBridge.Transport;
using Xunit;

namespace CodeBridge.Core.Tests.Transport;

public class UsbDeviceIdentifierTests
{
    [Theory]
    [InlineData("10C4", "EA60", "Silicon Labs CP210x (ESP32 DevKit)", "CP210x")]
    [InlineData("10c4", "ea60", "Silicon Labs CP210x (ESP32 DevKit)", "CP210x")]
    [InlineData("1A86", "7523", "CH340 (Arduino/ESP32 clone)", "CH340")]
    [InlineData("1a86", "55d4", "CH9102 (ESP32-S3)", "CH9102")]
    [InlineData("2341", "0043", "Arduino Uno (Official)", "ATMega328P")]
    [InlineData("2341", "0001", "Arduino Uno (Official)", "ATMega328P")]
    [InlineData("2341", "0010", "Arduino Mega 2560 (Official)", "ATMega2560")]
    [InlineData("2341", "0042", "Arduino Mega 2560 (Official)", "ATMega2560")]
    [InlineData("0483", "5740", "STM32 Virtual COM Port", "STM32")]
    [InlineData("303A", "1001", "Espressif ESP32-S2", "ESP32-S2")]
    [InlineData("303A", "1002", "Espressif ESP32-S3 (native USB)", "ESP32-S3")]
    public void Identify_KnownDevices_ReturnsExpectedBoardHintAndFamily(string vid, string pid, string expectedHint, string expectedFamily)
    {
        var (hint, family) = UsbDeviceIdentifier.Identify(vid, pid);

        Assert.Equal(expectedHint, hint);
        Assert.Equal(expectedFamily, family);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData("FFFF", "FFFF")]
    [InlineData("1234", "5678")]
    public void Identify_UnknownOrEmpty_ReturnsNull(string? vid, string? pid)
    {
        var (hint, family) = UsbDeviceIdentifier.Identify(vid, pid);

        Assert.Null(hint);
        Assert.Null(family);
    }
}
