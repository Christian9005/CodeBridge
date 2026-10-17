namespace CodeBridge.Transport;

public static class UsbDeviceIdentifier
{
    public static (string? BoardHint, string? ChipFamily) Identify(string? vid, string? pid)
    {
        if (string.IsNullOrWhiteSpace(vid) || string.IsNullOrWhiteSpace(pid))
            return (null, null);

        var key = $"{vid.ToUpperInvariant()}:{pid.ToUpperInvariant()}";
        
        return key switch
        {
            "10C4:EA60" => ("Silicon Labs CP210x (ESP32 DevKit)", "CP210x"),
            "10C4:EA70" => ("Silicon Labs CP2105 (ESP32)", "CP2105"),
            "1A86:7523" => ("CH340 (Arduino/ESP32 clone)", "CH340"),
            "1A86:55D4" => ("CH9102 (ESP32-S3)", "CH9102"),
            "2341:0043" => ("Arduino Uno (Official)", "ATMega328P"),
            "2341:0001" => ("Arduino Uno (Official)", "ATMega328P"),
            "2341:0010" => ("Arduino Mega 2560 (Official)", "ATMega2560"),
            "2341:0042" => ("Arduino Mega 2560 (Official)", "ATMega2560"),
            "2341:8036" => ("Arduino Leonardo (Official)", "ATMega32U4"),
            "0483:5740" => ("STM32 Virtual COM Port", "STM32"),
            "303A:1001" => ("Espressif ESP32-S2", "ESP32-S2"),
            "303A:1002" => ("Espressif ESP32-S3 (native USB)", "ESP32-S3"),
            _ => (null, null)
        };
    }
}
