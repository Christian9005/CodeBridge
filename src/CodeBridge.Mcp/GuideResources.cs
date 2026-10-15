using System.ComponentModel;
using CodeBridge.Flow;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CodeBridge.Mcp;

/// <summary>Reference material an assistant can read on demand: how flows are written and how to work safely.</summary>
[McpServerResourceType]
public sealed class GuideResources
{
    [McpServerResource(UriTemplate = "codebridge://guide/flow-format", Name = "flow-format", Title = "How to write a CodeBridge flow", MimeType = "text/markdown")]
    [Description("The .cbflow JSON format with a complete example and the patterns that work.")]
    public static string FlowFormat() => """
        # Writing a CodeBridge flow (.cbflow)

        A flow is JSON: blocks (**Nodes**) and wires between them (**Connections**). Get valid block types, ports and pins from
        `list_block_types` and `get_board_manifest` - do not guess them. Always run `validate_flow` before `run_flow`.

        ```json
        {
          "Name": "Night light",
          "BoardId": "esp32-devkit",
          "Version": 1,
          "Nodes": [
            { "Id": "start", "Type": "flow.manual-trigger", "Position": { "X": 60,  "Y": 120 }, "Parameters": {} },
            { "Id": "read",  "Type": "gpio.analog-read",    "Position": { "X": 300, "Y": 120 }, "Parameters": { "pin": 34, "samples": 4 } },
            { "Id": "map",   "Type": "math.map",            "Position": { "X": 540, "Y": 120 }, "Parameters": { "inMin": 0, "inMax": 4095, "outMin": 255, "outMax": 0 } },
            { "Id": "lamp",  "Type": "gpio.pwm-write",      "Position": { "X": 780, "Y": 120 }, "Parameters": { "pin": 2 } }
          ],
          "Connections": [
            { "Id": "c1", "FromNodeId": "start", "FromPort": "trigger", "ToNodeId": "read", "ToPort": "trigger" },
            { "Id": "c2", "FromNodeId": "read",  "FromPort": "value",   "ToNodeId": "map",  "ToPort": "value" },
            { "Id": "c3", "FromNodeId": "map",   "FromPort": "result",  "ToNodeId": "lamp", "ToPort": "duty" }
          ]
        }
        ```

        ## Rules
        - `Id` values are unique strings; `Type` comes from `list_block_types`; `BoardId` from `list_boards`.
        - A wire goes from an **output** port to an **input** port of compatible kind. A `trigger` only connects to a `trigger`; a number can feed a `number`
          input; a boolean feeds a boolean input. An analog reading is a number, not a boolean: compare it (`logic.compare`) or map it (`math.map`).
        - A block whose `trigger` input has no wire runs on its own, in dependency order. `flow.manual-trigger` is the usual start.
        - Parameters are the block's properties (pin numbers, thresholds, durations in milliseconds). Omitted ones use their default.
        - `Position` only matters for the editor (about 240 px between columns keeps wires readable).

        ## Patterns
        - **Blink**: `flow.manual-trigger` -> `gpio.blink-led` (pin = the board's built-in LED pin from the manifest).
        - **Sensor to brightness**: `gpio.analog-read` -> `math.map` (0..analogMax to 0..255) -> `gpio.pwm-write` (duty).
        - **Threshold**: `gpio.analog-read` + `logic.constant-number` -> `logic.compare` -> `gpio.digital-write` (value).
        - **Servo**: `math.map` (to 0..180) -> `servo.write` (angle).
        - **Sensors and modules** (ESP32 family; always read `list_block_types` for the exact ports and pins): `sensor.dht` (temperature, humidity), `sensor.ultrasonic` (distance),
          `sensor.ds18b20`, `sensor.bme280`, `sensor.bh1750` (lux), `sensor.mpu6050`, `sensor.ina219`, `sensor.color`, `sensor.pir`, `sensor.button`, `sensor.analog-percent`;
          `module.relay`, `module.buzzer`, `module.rgb-led`, `module.neopixel`, `module.lcd` / `module.oled` (use `{value}` in the text), `module.dc-motor`, `module.stepper`.
          Helpers: `logic.threshold`, `logic.gate`, `math.calc`.
        - **Thermostat**: `sensor.dht` (temperature) -> `logic.threshold` (limit 28) -> `module.relay` (state) for a fan.
        - **Night light**: `sensor.bh1750` (lux) -> `logic.threshold` (mode below, limit 50) -> `gpio.digital-write` (value).
        - **Show a reading**: `sensor.dht` (temperature) -> `module.lcd` (value, text `Temp: {value} C`).
        - **Debug**: wire any value into `debug.log` to see it in the run result.

        ## Working loop
        1. `get_board_manifest` -> pick pins whose safety is `ok` and that have the capability you need.
        2. Write the flow, `validate_flow`, fix errors.
        3. `simulate_input` to set sensor values, `run_flow`, read `pinActivity` to confirm the right pins moved.
        4. `export_csharp` if the user wants code instead of a flow.
        5. Only after the user agrees, real hardware (see `codebridge://guide/safety`).
        """;

    [McpServerResource(UriTemplate = "codebridge://guide/safety", Name = "safety", Title = "Working safely with real boards", MimeType = "text/markdown")]
    [Description("What can damage a board or the things connected to it, and how this server protects against it.")]
    public static string Safety() => """
        # Safety

        - The server starts on the **simulator**. Real hardware needs the user to start it with `CODEBRIDGE_MCP_ALLOW_HARDWARE=1`, and every action that moves
          something needs `confirmed=true` after the user approved it. Never pass `confirmed=true` on your own.
        - Describe the physical effect before asking: which pin, what it does, what is connected to it.
        - Do not drive motors, relays, LED strips or buzzers straight from a pin: the manifest lists the safe current per pin. Use a driver or transistor.
        - Never use pins whose safety is `reserved` (USB/UART/flash/PSRAM). Treat `boot-strap` pins as off limits unless the user knows the circuit (the built-in LED pin is the usual exception: it is meant for blink tests).
        - 3.3 V boards (ESP32) are not 5 V tolerant: do not connect 5 V signals directly.
        - The Wi-Fi pairing token never passes through you; if a board asks for one, tell the user to pair it in Visual Studio.
        """;

    [McpServerResource(UriTemplate = "codebridge://boards/{boardId}", Name = "board-manifest", Title = "Board manifest", MimeType = "application/json")]
    [Description("The pins, capabilities, safety and rules of one board (same content as the get_board_manifest tool).")]
    public static string BoardManifestResource([Description("A board id such as 'esp32-devkit'.")] string boardId)
    {
        var board = BuiltInBoardProfiles.FindById(boardId) ?? throw new McpException($"Unknown board '{boardId}'.");
        return McpSession.ToJson(BoardManifest.For(board));
    }
}
