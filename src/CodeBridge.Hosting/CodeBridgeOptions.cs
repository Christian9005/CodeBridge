namespace CodeBridge.Hosting;

/// <summary>Which firmware the board runs.</summary>
public enum BoardKind
{
    /// <summary>ESP32 DevKit with the CodeBridge firmware.</summary>
    Esp32,

    /// <summary>Arduino Uno with the CodeBridge sketch (USB only).</summary>
    ArduinoUno,

    /// <summary>ESP32-S3 DevKit.</summary>
    Esp32S3,

    /// <summary>ESP32-C3 DevKit.</summary>
    Esp32C3,

    /// <summary>Seeed XIAO ESP32-S3 (USB serial only, no UART bridge).</summary>
    XiaoEsp32S3,

    /// <summary>ESP32-S2 Mini.</summary>
    Esp32S2,

    /// <summary>Arduino Nano (USB only).</summary>
    ArduinoNano,

    /// <summary>Arduino Mega 2560 (USB only).</summary>
    ArduinoMega
}

/// <summary>What the service needs to know about each <see cref="BoardKind"/>.</summary>
internal static class BoardKinds
{
    public static bool IsArduino(this BoardKind kind) => kind is BoardKind.ArduinoUno or BoardKind.ArduinoNano or BoardKind.ArduinoMega;

    public static int AnalogMax(this BoardKind kind) => kind.IsArduino() ? 1023 : 4095;

    public static int MaxPin(this BoardKind kind) => kind switch
    {
        BoardKind.ArduinoUno => 19,
        BoardKind.ArduinoNano => 21,
        BoardKind.ArduinoMega => 69,
        BoardKind.Esp32S3 => 48,
        BoardKind.XiaoEsp32S3 => 48,
        BoardKind.Esp32S2 => 46,
        BoardKind.Esp32C3 => 21,
        _ => 39
    };

    public static string ChipName(this BoardKind kind) => kind switch
    {
        BoardKind.ArduinoUno or BoardKind.ArduinoNano => "ATmega328P-SIM",
        BoardKind.ArduinoMega => "ATmega2560-SIM",
        BoardKind.Esp32S3 or BoardKind.XiaoEsp32S3 => "ESP32S3-SIM",
        BoardKind.Esp32S2 => "ESP32S2-SIM",
        BoardKind.Esp32C3 => "ESP32C3-SIM",
        _ => "ESP32-SIM"
    };
}

/// <summary>Settings of the shared board connection. Bind them from configuration (<c>"CodeBridge"</c> section) or set them in code.</summary>
public sealed class CodeBridgeOptions
{
    /// <summary>
    /// Where the board is: <c>"COM3"</c> (USB), an IP address or host name (Wi-Fi), <c>"simulator"</c> (virtual board),
    /// or <c>"auto"</c> / empty to use the first board found on USB.
    /// </summary>
    public string? Port { get; set; } = "auto";

    /// <summary>Board model. The Arduino boards work over USB only.</summary>
    public BoardKind Board { get; set; } = BoardKind.Esp32;

    /// <summary>Pairing token of a Wi-Fi board (see <c>Esp32WifiProvisioner</c>). Falls back to the <c>CODEBRIDGE_TOKEN</c> environment variable.</summary>
    public string? AccessToken { get; set; }

    /// <summary>TCP port of a Wi-Fi board.</summary>
    public int TcpPort { get; set; } = 8080;

    /// <summary>Baud rate of the USB connection.</summary>
    public int BaudRate { get; set; } = 115200;

    /// <summary>When <c>"auto"</c> finds no board, run on the virtual board instead of failing. Handy for demos and first runs.</summary>
    public bool FallbackToSimulator { get; set; } = true;

    /// <summary>
    /// Builds the transport to use instead of the USB/Wi-Fi/simulator choice made from <see cref="Port"/>. Used by <c>DeviceFleet</c> to reach a board over the
    /// ESP-NOW mesh; the returned transport is connected by the service. Set <see cref="TargetLabel"/> to name it in logs and in <c>BoardService.Target</c>.
    /// </summary>
    public Func<CancellationToken, Task<CodeBridge.Core.Abstractions.ITransport>>? TransportFactory { get; set; }

    /// <summary>What <c>BoardService.Target</c> shows when <see cref="TransportFactory"/> is used.</summary>
    public string? TargetLabel { get; set; }

    /// <summary>Called with every command sent to the board and its answer (to draw or log what the board is doing).</summary>
    public Action<string, string>? CommandObserver { get; set; }

    /// <summary>Connect when the application starts (as a hosted service).</summary>
    public bool AutoConnect { get; set; } = true;

    /// <summary>Reconnect in the background after the connection drops.</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>First wait before reconnecting; it doubles after each failure up to <see cref="MaxReconnectDelay"/>.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Longest wait between reconnection attempts.</summary>
    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);
}
