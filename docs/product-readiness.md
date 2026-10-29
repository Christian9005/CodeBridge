# Product readiness

Where CodeBridge stands, what is verified, and what is still missing before it can be called finished. Updated for 0.7.x and the work after it.

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
| Builds and tests | Every push: the solution, ~1,200 automated tests (SDK, flows, exporter parity, editor UI on STA threads, MCP, Home Assistant, web editor), firmware for every board |
| ESP32 DevKit | Real hardware: GPIO, sensors, Wi-Fi, OTA, mesh gateway, I2C with a BME280 through the built-in block and the .NET IoT driver |
| XIAO ESP32-S3 | Real hardware: flashing, USB, mesh node (heard by the gateway) |
| Visual Studio 2026 | The extension installed from the VSIX, flows run on a real board from the editor |

## Still missing

**To publish**
- Marketplace publisher and NuGet API key in the repository secrets (see [publishing](publishing.md)); code signing for the VSIX.
- Screenshots and a short video for the Marketplace page and the README.
- Third-party notices for the bundled tools and packages, and a privacy note (CodeBridge sends nothing anywhere; say so).

**To trust it on more hardware**
- ESP32-S3 DevKit, ESP32-C3, ESP32-S2 Mini, Arduino Nano and Mega on real boards.
- The mesh relay from gateway to node over the air (the XIAO needs its antenna).
- The .NET IoT blocks with real chips other than the BME280 (they pass against virtual chips that follow the datasheets).
- `serve --service` installed as a Windows service, and reconnection after unplugging a real board.

**To make it complete**
- Running a flow on the board with no PC (compiling a flow to firmware): today a flow needs the PC or `serve`.
- Visual Studio Light, Blue and High Contrast themes checked by eye; MAUI on Android on a device.
- The browser editor: undo, copy/paste, the board view, keyboard wiring, and a design pass.
- Home Assistant publishing for sensor and relay blocks (today: GPIO blocks).

## Next

1. Publish (the first group above) with the greenhouse as the front page.
2. A hardware matrix: one afternoon per board, results in the README table.
3. Flow-to-firmware, which is the one thing competitors such as ESPHome do that CodeBridge does not.
