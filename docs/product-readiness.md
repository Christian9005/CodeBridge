# Product readiness

Where CodeBridge stands, what is verified, and what is still missing before it can be called finished. Updated for 0.8.x.

## What a user gets today

- **Visual Studio extension**: the `.cbflow` editor (toolbox of 65 blocks with help and animated demos, live per-block status, undo/redo, Export C#), the
  *CodeBridge WinForms App* project template that runs a flow on the first F5, item templates and five examples, the Tour, one-click firmware upload
  (USB and over the air), Wi-Fi pairing, the board view, a serial monitor and plotter, PlatformIO code mode, Home Assistant publishing and the mesh window.
- **SDK** (NuGet): boards over USB, Wi-Fi (token-paired) and ESP-NOW mesh, the simulator, `CodeBridge.Hosting` with `DeviceFleet`, Blazor components,
  Home Assistant, the MCP server for AI assistants.
- **Proofs of concept**: the .NET IoT drivers as blocks (`CodeBridge.IotBindings`), flows that run by themselves (`FlowHost serve`), and the browser
  editor (`codebridge-web`).
- **A complete project** to show it all: the [smart greenhouse](greenhouse.md).

## Verified

| | How |
|---|---|
| Builds and tests | Every push: the solution, ~1,500 automated tests (SDK, flows, exporter parity, editor UI on STA threads, MCP, Home Assistant, web editor), firmware for every board |
| Files of earlier releases | Golden `.cbflow` files saved by 0.5.4, 0.6.0 and 0.7.0, by the web editor and by hand must still load, validate, run, export and survive a save in the SDK, the web editor and the real Visual Studio editor ([tests/golden](../tests/golden/cbflow)) |
| Damaged and hostile files | Every golden file cut at 120 places and damaged 150 ways, text that is not a flow, nesting 100,000 deep, wires in rings, names built to inject code into the exported C# |
| Every block on every board | The 65 blocks of the ESP32 boards and the 24 of the Arduinos on all nine boards: valid, run on the virtual board, exported and compiled ([block matrix](block-board-matrix.md)); on an ESP32 DevKit with nothing wired, none of the 65 hangs or resets it |
| Firmware commands | `tests/firmware_robustness.ps1` on an ESP32 DevKit (firmware 0.9.2): 75 checks (flash pins, numbers that overflow, malformed and oversized lines, a burst of 300 lines, 1,000 hostile commands), no failure, no reset |
| `serve` for a long time | `tests/serve_soak.ps1`, 30 minutes on the virtual board (28,981 passes of the greenhouse flow) and on an ESP32 DevKit (1,741 passes; and 15 minutes at ten times the rate, 8,221 passes): no failed pass, memory flat at 21-23 MB, handles and threads steady, the log stays small, Ctrl+C stops it in under 2 seconds |
| ESP32 DevKit | Real hardware: GPIO, sensors, Wi-Fi, OTA, mesh gateway, I2C with a BME280 through the built-in block and the .NET IoT driver |
| XIAO ESP32-S3 | Real hardware: flashing, USB, mesh node (heard by the gateway) |
| Visual Studio 2026 | The extension installed from the VSIX in a clean experimental instance: it loads, a `.cbflow` opens, the project template creates a project that restores from NuGet, builds and runs on a real board |

## Still missing

**To publish**
- The NuGet packages are published (0.8.0: Abstractions, Transport, ESP32, Flow, Designer.WinForms, Hosting, Blazor, HomeAssistant, PlatformIO and the
  `CodeBridge.Mcp` tool); `CodeBridge.IotBindings` and the browser editor are not yet.
- The extension is on the Visual Studio Marketplace (0.8.0) and in the GitHub releases (see [publishing](publishing.md)); code signing is still missing.
- A name that stands out on the Marketplace: several unrelated extensions are already called "CodeBridge" (one of them an MCP tool), and on NuGet the id
  `CodeBridge.Core` belongs to another project. The display name should say what it is, for example "CodeBridge for .NET hardware (ESP32, Arduino)".
- Screenshots and a short video for the Marketplace page and the README.
- Third-party notices for the bundled tools and packages, and a privacy note (CodeBridge sends nothing anywhere; say so).

**To trust it on more hardware**
- ESP32-S3 DevKit, ESP32-C3, ESP32-S2 Mini, Arduino Nano and Mega on real boards.
- The mesh relay from gateway to node over the air (the XIAO needs its antenna).
- The .NET IoT blocks with real chips other than the BME280 (they pass against virtual chips that follow the datasheets).
- `serve --service` installed as a Windows service, and reconnection after unplugging a real board (the long run above never lost the board, so the reconnection of a real one is still unseen; the loop itself is covered by tests with a board that fails).

**To make it complete**
- Running a flow on the board with no PC (compiling a flow to firmware): today a flow needs the PC or `serve`.
- Visual Studio Light, Blue and High Contrast themes checked by eye; MAUI on Android on a device.
- The browser editor: undo, copy/paste, the board view, keyboard wiring, and a design pass.
- Home Assistant publishing for sensor and relay blocks (today: GPIO blocks).
- LCD and OLED blocks that say so when no display answers (today they end without an error with nothing connected).

## Next

1. Give the Marketplace page what it lacks (a name that says what it is, screenshots, the greenhouse and a short video).
2. The hardware matrix on the other boards: one afternoon per board with `HardwareMatrixTests`, results in [block-board-matrix.md](block-board-matrix.md).
3. Flow-to-firmware, which is the one thing competitors such as ESPHome do that CodeBridge does not.
