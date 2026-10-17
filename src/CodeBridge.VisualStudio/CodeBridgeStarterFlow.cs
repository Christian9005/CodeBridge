using System.IO;
using System.Text;

namespace CodeBridge.VisualStudio;

/// <summary>
/// The flow the starter form runs. It is a file of the project (Flows\Blink.cbflow), the same kind of file the CodeBridge editor opens,
/// so the first thing a person does after creating a project is to open it, change it and save it.
/// </summary>
internal static class CodeBridgeStarterFlow
{
    /// <summary>Writes Flows\Blink.cbflow into the project folder unless it is already there, and returns its path.</summary>
    public static string EnsureIn(string projectDirectory)
    {
        var path = Path.Combine(projectDirectory, "Flows", "Blink.cbflow");
        if (File.Exists(path))
            return path;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Blink, new UTF8Encoding(false));
        return path;
    }

    /// <summary>The LED on GPIO 2 (the on-board LED of most ESP32 boards) on for a second, then off for a second.</summary>
    public const string Blink = """
        {
          "Id": "starter-blink",
          "Name": "Blink",
          "BoardId": "esp32-devkit",
          "Version": 1,
          "Nodes": [
            { "Id": "start", "Type": "flow.manual-trigger", "Position": { "X": 80, "Y": 120 } },
            { "Id": "led-on", "Type": "gpio.digital-write", "Position": { "X": 340, "Y": 120 }, "Parameters": { "pin": 2, "value": true } },
            { "Id": "wait-on", "Type": "core.timer", "Position": { "X": 600, "Y": 120 }, "Parameters": { "intervalMs": 1000 } },
            { "Id": "led-off", "Type": "gpio.digital-write", "Position": { "X": 860, "Y": 120 }, "Parameters": { "pin": 2, "value": false } },
            { "Id": "wait-off", "Type": "core.timer", "Position": { "X": 1120, "Y": 120 }, "Parameters": { "intervalMs": 1000 } }
          ],
          "Connections": [
            { "Id": "c1", "FromNodeId": "start", "FromPort": "trigger", "ToNodeId": "led-on", "ToPort": "trigger" },
            { "Id": "c2", "FromNodeId": "led-on", "FromPort": "done", "ToNodeId": "wait-on", "ToPort": "trigger" },
            { "Id": "c3", "FromNodeId": "wait-on", "FromPort": "tick", "ToNodeId": "led-off", "ToPort": "trigger" },
            { "Id": "c4", "FromNodeId": "led-off", "FromPort": "done", "ToNodeId": "wait-off", "ToPort": "trigger" }
          ]
        }
        """;
}
