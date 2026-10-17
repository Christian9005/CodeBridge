<p align="center">
  <img src="src/CodeBridge.VisualStudio/Assets/CodeBridgePreview.png" alt="CodeBridge" width="220" />
</p>

# CodeBridge

[![CI](https://github.com/Christian9005/CodeBridge/actions/workflows/ci.yml/badge.svg)](https://github.com/Christian9005/CodeBridge/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/vpre/CodeBridge.ESP32?label=NuGet&logo=nuget)](https://www.nuget.org/packages/CodeBridge.ESP32)
[![Visual Studio 2022+](https://img.shields.io/badge/Visual%20Studio-2022%20%7C%202026-5C2D91?logo=visual-studio&logoColor=white)](https://marketplace.visualstudio.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**Control ESP32 and Arduino Uno boards from .NET: in C#, or with a visual flow editor inside Visual Studio.**
CodeBridge ships a bridge firmware for the board and a .NET SDK that talks to it over USB serial or Wi-Fi, so you write
C# instead of embedded C/C++.

## Two ways to use it

| You are... | Use | Start here |
|---|---|---|
| New to microcontrollers / prefer visual tools | **CodeBridge Visual Studio Tools** (VSIX) | [Getting started](docs/getting-started.md) |
| A .NET developer who wants the SDK directly | **NuGet packages** | [SDK quick start](#sdk-quick-start) |

## NuGet packages

| Package | Purpose | Target |
|---|---|---|
| `CodeBridge.Abstractions` | Abstractions (`IBoard`, `ITransport`), wire protocol, exceptions. The assembly and namespace are `CodeBridge.Core`; the NuGet id `CodeBridge.Core` belongs to another project | net8.0 |
| `CodeBridge.Transport` | Serial and TCP/Wi-Fi transports, USB board discovery | net8.0 (Windows for USB discovery) |
| `CodeBridge.ESP32` | ESP32 board + sensor/actuator drivers | net8.0 |
| `CodeBridge.Flow` | Flow document model, validation, runtime | net8.0 |
| `CodeBridge.Designer.WinForms` | WinForms Toolbox controls, designer, firmware flashing (ships the firmware) | net8.0-windows |

```powershell
dotnet add package CodeBridge.ESP32
```

## SDK quick start

Flash the firmware once (see [Getting started](docs/getting-started.md#flash-the-firmware)), then:

```csharp
using CodeBridge.Core;
using CodeBridge.Core.Enums;
using CodeBridge.ESP32;

await using var board = await CodeBridgeBuilder
    .Connect()
    .Serial("COM3", baudRate: 115200)   // or .WiFi("192.168.1.50")
    .ToESP32()
    .BuildAsync();

await board.Gpio.SetPinModeAsync(2, PinMode.Output);
await board.Gpio.DigitalWriteAsync(2, PinValue.High);

int raw = await board.Gpio.AnalogReadAsync(34);   // 12-bit ADC value
Console.WriteLine($"ADC34 = {raw}");
```

More runnable examples are in [`samples/`](samples) (blink, servo, motor/relay, Wi-Fi setup and diagnostics).
See the [API reference](docs/api-reference.md).

## Visual Studio extension

- **`.cbflow` visual flow editor**: drag blocks, wire ports, edit properties; follows the active Visual Studio theme.
  Its toolbar picks the board and port, tests the connection, uploads the firmware and runs the flow on the real board with live
  per-block status, undo/redo, copy/paste, multi-select and live validation. Hover any block for an animated explanation;
  five ready-made [examples](docs/examples.md) are available from *Add > New Item*, and *Tools > CodeBridge Tour*
  walks you from the first blink to using the SDK from a [console app, web API or Windows service](samples).
- **WinForms Toolbox components**: `CodeBridgeFlowControl` and `CodeBridgeBoardComponent` with Smart Tags to pick a COM port,
  detect the board and flash the firmware.
- **One-click firmware flashing for ESP32**: uses `esptool`, downloaded on first use over HTTPS and verified by SHA-256.
  No Python, PlatformIO or Arduino IDE needed. The firmware binaries ship inside `CodeBridge.Designer.WinForms`.
- **Serial monitor and plotter** inside Visual Studio, and **code mode**: create, build and upload C++ firmware with PlatformIO, with errors in the Error List.
- **Export C#**: turn any flow into readable SDK code (a class for your project, or a console `Program.cs`) from the editor toolbar.
- **Project and item templates**, a setup window (*Tools > CodeBridge Setup...*) and a starter form command.
- **Automatic USB board detection** for CP210x, CH340/CH9102, FTDI and official Arduino adapters.

## Hardware support

| Board | Status |
|---|---|
| **ESP32 DevKit (esp32dev)** | Supported and tested on hardware: GPIO, ADC, PWM, I2C, SPI, 1-Wire, interrupts, sensors, displays, Wi-Fi, one-click firmware |
| **ESP32-S3 DevKit**, **ESP32-C3 DevKit** | Firmware builds in CI and one-click flashing is wired up; pin maps follow the Espressif DevKit pinouts. Not yet verified on hardware: please report what you find |
| **Arduino Uno R3** | Basic: digital/analog I/O, PWM, servo via the bridge sketch. Flashing needs [`arduino-cli`](https://arduino.github.io/arduino-cli/) |
| **Arduino Nano**, **Arduino Mega 2560** | Same sketch (it builds in CI for both); Nano comes in two bootloader flavours (pick "old bootloader" for most clones). Not yet verified on hardware |
| Any board, no hardware | The built-in **simulator** (`.Simulator()`, or *Simulator* in the editor's Port list) runs flows, tests and demos without a cable |
| STM32, PIC | Planned; `ToSTM32()` exists in the builder but there is no driver yet and building throws `NotSupportedException` |

Drivers included for ESP32: DHT11/22, DS18B20, BME280, BH1750, MPU6050, TCS34725, INA219, HC-SR04, PIR, gas sensors,
relays, buzzers, servos, DC/stepper motors, RGB LED, WS2812B NeoPixel, SSD1306 OLED, HD44780 I2C LCD, MQTT, OTA.

## Use it from your app

| Package | What for |
|---|---|
| `CodeBridge.Hosting` | `services.AddCodeBridge()`: one shared, thread-safe board connection with reconnection and the simulator, for ASP.NET Core, workers and MAUI |
| `DeviceFleet` (in `CodeBridge.Hosting`) | Any number of named boards (USB, Wi-Fi, or boards reached over an [ESP-NOW mesh](docs/mesh.md) through a gateway that keeps its Wi-Fi) behind one object, with discovery, persistence and per-device reconnection |
| `CodeBridge.Blazor` | `<BoardStatus/>`, `<PinToggle/>`, `<PwmSlider/>`, `<AnalogGauge/>`, `<SensorChart/>` components for a live dashboard |
| `CodeBridge.Mcp` | An [MCP server](docs/mcp.md) so an AI assistant can read the board manifest, design and test flows on the simulator and export C# (hardware is opt-in and confirmed per action) |
| `CodeBridge.HomeAssistant` | Shows the board in Home Assistant through MQTT discovery (switches, lights, sensors) and finds Home Assistant on your network |

Samples: [`Integration.Blazor`](samples/Integration.Blazor), [`Integration.HomeAssistant`](samples/Integration.HomeAssistant),
[`Integration.Maui`](samples/Integration.Maui) (Android and Windows), plus console, web API and Windows service.

## Architecture

```
Visual Studio        .cbflow editor | WinForms Toolbox | Setup window
SDK (NuGet)          Flow -> Core <- Transport <- ESP32        (Designer.WinForms ties them together)
Board firmware       esp32-bridge (PlatformIO) | arduino-uno-bridge
```

## Security

- Downloaded tools (esptool) must use HTTPS and match a pinned SHA-256.
- Firmware flashing runs child processes that are killed (whole tree) on cancel, so serial ports are released.
- See [SECURITY.md](SECURITY.md) to report a vulnerability.

## Building from source

```powershell
./build/pack.ps1 -Vsix      # packs the 5 NuGet packages and builds the VSIX into artifacts/
dotnet test CodeBridge.slnx -c Release
```

Releasing is described in [docs/publishing.md](docs/publishing.md). Contributions are welcome: see [CONTRIBUTING.md](CONTRIBUTING.md)
and the [changelog](CHANGELOG.md).

## License

MIT, see [LICENSE](LICENSE).
