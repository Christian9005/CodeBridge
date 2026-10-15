using System.Text;
using System.Text.RegularExpressions;
using CodeBridge.Flow;

namespace CodeBridge.PlatformIO;

/// <summary>Ready-made starting points for a firmware project.</summary>
public enum FirmwareTemplate
{
    /// <summary>Blinks the built-in LED and prints what it does.</summary>
    Blink,

    /// <summary>Prints "light:&lt;analog reading&gt;,counter:&lt;n&gt;" so the CodeBridge plotter has something to draw.</summary>
    SerialPlot,

    /// <summary>An empty setup() and loop().</summary>
    Empty
}

/// <summary>PlatformIO settings of a CodeBridge board.</summary>
public sealed record PioBoard(string ProfileId, string PlatformIoBoard, string Platform, string AnalogPin, string Name);

/// <summary>Creates and inspects PlatformIO firmware projects.</summary>
public static class PioProject
{
    private static readonly Regex EnvSection = new(@"^\s*\[env:(?<name>[^\]\s]+)\]", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>The PlatformIO board id, platform and a sensible analog pin for each CodeBridge board.</summary>
    public static PioBoard? BoardFor(string profileId) => profileId switch
    {
        "esp32-devkit" => new("esp32-devkit", "esp32dev", "espressif32", "34", "ESP32 DevKit"),
        "esp32-s3-devkit" => new("esp32-s3-devkit", "esp32-s3-devkitc-1", "espressif32", "4", "ESP32-S3 DevKit"),
        "esp32-c3-devkit" => new("esp32-c3-devkit", "esp32-c3-devkitm-1", "espressif32", "0", "ESP32-C3 DevKit"),
        "xiao-esp32-s3" => new("xiao-esp32-s3", "seeed_xiao_esp32s3", "espressif32", "1", "XIAO ESP32-S3"),
        "esp32-s2-mini" => new("esp32-s2-mini", "lolin_s2_mini", "espressif32", "1", "ESP32-S2 Mini"),
        "arduino-uno" => new("arduino-uno", "uno", "atmelavr", "A0", "Arduino Uno"),
        "arduino-nano" => new("arduino-nano", "nanoatmega328new", "atmelavr", "A0", "Arduino Nano"),
        "arduino-nano-old" => new("arduino-nano-old", "nanoatmega328", "atmelavr", "A0", "Arduino Nano (old bootloader)"),
        "arduino-mega" => new("arduino-mega", "megaatmega2560", "atmelavr", "A0", "Arduino Mega 2560"),
        _ => null
    };

    public static bool IsProject(string? directory) =>
        !string.IsNullOrWhiteSpace(directory) && File.Exists(Path.Combine(directory, "platformio.ini"));

    /// <summary>Walks up from a file or folder to the folder that holds platformio.ini.</summary>
    public static string? FindRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var directory = File.Exists(path) ? Path.GetDirectoryName(path) : path;
        for (var depth = 0; directory is not null && depth < 12; depth++, directory = Path.GetDirectoryName(directory))
        {
            if (IsProject(directory))
                return directory;
        }

        return null;
    }

    /// <summary>The <c>[env:name]</c> sections of platformio.ini.</summary>
    public static IReadOnlyList<string> Environments(string directory) =>
        EnvSection.Matches(File.ReadAllText(Path.Combine(directory, "platformio.ini"))).Select(m => m.Groups["name"].Value).ToList();

    /// <summary>Creates a new project (platformio.ini, src/main.cpp, .gitignore, README) in an empty or new folder.</summary>
    public static IReadOnlyList<string> Create(string directory, string boardProfileId, FirmwareTemplate template = FirmwareTemplate.Blink)
    {
        var board = BoardFor(boardProfileId) ?? throw new ArgumentException($"Unknown board '{boardProfileId}'.", nameof(boardProfileId));
        var profile = BuiltInBoardProfiles.FindById(boardProfileId);

        Directory.CreateDirectory(directory);
        if (IsProject(directory))
            throw new InvalidOperationException($"'{directory}' already contains a PlatformIO project.");
        if (Directory.EnumerateFileSystemEntries(directory).Any(e => !Path.GetFileName(e).StartsWith('.')))
            throw new InvalidOperationException($"'{directory}' is not empty. Choose a new or empty folder.");

        var files = new List<string>();
        void Write(string relative, string content)
        {
            var full = Path.Combine(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content.Replace("\r\n", "\n"), new UTF8Encoding(false));
            files.Add(relative.Replace('\\', '/'));
        }

        Write("platformio.ini", Ini(board));
        Write("src/main.cpp", Source(template, board));
        Write(".gitignore", ".pio/\n.vscode/\n");
        Write("README.md", Readme(board, profile?.BuiltInLedPin));
        return files;
    }

    internal static string Ini(PioBoard board) =>
        $"""
        ; CodeBridge firmware project for {board.Name}.
        ; Build and upload from Visual Studio (Tools > CodeBridge Firmware) or with `pio run`.

        [env:{board.PlatformIoBoard}]
        platform = {board.Platform}
        board = {board.PlatformIoBoard}
        framework = arduino
        monitor_speed = 115200
        """ + "\n";

    internal static string Source(FirmwareTemplate template, PioBoard board) => template switch
    {
        FirmwareTemplate.Blink =>
            """
            #include <Arduino.h>

            // Blinks the built-in LED and reports it on the serial port (open Tools > CodeBridge Serial Monitor at 115200 baud).
            #ifndef LED_BUILTIN
            #define LED_BUILTIN 2
            #endif

            void setup() {
              Serial.begin(115200);
              pinMode(LED_BUILTIN, OUTPUT);
            }

            void loop() {
              digitalWrite(LED_BUILTIN, HIGH);
              Serial.println("LED on");
              delay(500);

              digitalWrite(LED_BUILTIN, LOW);
              Serial.println("LED off");
              delay(500);
            }
            """ + "\n",

        FirmwareTemplate.SerialPlot =>
            $$"""
            #include <Arduino.h>

            // Prints "light:<reading>,counter:<n>" ten times a second. Open the CodeBridge Serial Monitor, switch to Plotter and
            // both values are drawn. Any "name:value" pairs or comma-separated numbers work the same way.
            const int ANALOG_PIN = {{board.AnalogPin}};
            unsigned long counter = 0;

            void setup() {
              Serial.begin(115200);
            }

            void loop() {
              Serial.print("light:");
              Serial.print(analogRead(ANALOG_PIN));
              Serial.print(",counter:");
              Serial.println(counter++ % 1000);
              delay(100);
            }
            """ + "\n",

        _ =>
            """
            #include <Arduino.h>

            void setup() {
              Serial.begin(115200);
            }

            void loop() {
            }
            """ + "\n"
    };

    private static string Readme(PioBoard board, int? ledPin) =>
        $"""
        # {board.Name} firmware

        A PlatformIO project created by CodeBridge.

        - **Build / Upload / Clean**: Visual Studio > Tools > *CodeBridge Firmware: ...* (errors appear in the Error List), or `pio run` / `pio run -t upload`.
        - **See what the board prints**: Tools > *CodeBridge Serial Monitor* (115200 baud). It draws `name:value` numbers as a chart.
        - Built-in LED: {(ledPin is { } p ? $"pin {p}" : "see your board's documentation")}.
        - The pin rules of this board (what is safe, what is reserved) are in the CodeBridge board manifest: `codebridge-mcp` serves it to AI assistants too.
        """ + "\n";
}
