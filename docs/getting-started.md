# Getting Started with CodeBridge

CodeBridge allows you to control hardware such as the ESP32 directly from .NET applications, including WinForms, without writing C/C++ firmware. This guide will walk you through setting up your environment, flashing the microcontroller, and writing your first CodeBridge application.

## Prerequisites

1. **Visual Studio 2022 (17.x) or later** with the **.NET desktop development** workload
2. **.NET 8 SDK**
3. An **ESP32 development board** (ESP32 DevKit) and a USB **data** cable
4. *Optional:* [`arduino-cli`](https://arduino.github.io/arduino-cli/) if you use an Arduino Uno

You do **not** need Python, PlatformIO or the Arduino IDE to use an ESP32: CodeBridge flashes the prebuilt firmware itself.

## Flash the firmware

Before the board can talk to your app it needs the CodeBridge bridge firmware. There are three ways:

- **From Visual Studio (recommended):** in a WinForms form, select `CodeBridgeBoardComponent` (or the flow control), choose the
  COM port in the Smart Tag and click **Upload firmware**. On first use CodeBridge downloads `esptool` (about 8 MB, HTTPS + SHA-256 verified).
- **From code:** `await CodeBridgeBoardComponent.UploadFirmwareAsync()`.
- **Build it yourself (other ESP32 variants):** install PlatformIO, then
  ```bash
  cd firmware/esp32-bridge
  pio run --target upload
  ```

The firmware reports version `0.9.x`; the SDK refuses to connect to an incompatible major/minor version and tells you to update.

## See what the board prints: the serial monitor

*Tools > CodeBridge Serial Monitor* (or the **Monitor** button of the flow editor) reads the board's USB port. Pick the port and baud rate (the one in the board's
`Serial.begin()`), press **Connect**, and type in the box at the bottom to send text. Switch to **Plotter** to see numbers drawn as a chart: print
`temp:23.5,light:1800` (or just `23.5,1800`) from the board and each value gets its own line. **Reset board** restarts the board so you can read its start-up messages.
Choose *Simulator* as the port to try it without a board.

## Write firmware in C++ (code mode)

For people who want the full Arduino-style experience inside Visual Studio: *Tools > CodeBridge Firmware: New Project...* creates a PlatformIO project for your board
(Blink, Serial plot, the customizable CodeBridge firmware, or empty). Then *Build*, *Upload* and *Clean* from the same menu; compiler errors show in the Error List with a link to the line.
PlatformIO must be installed once (`pip install platformio`); Visual Studio tells you if it is missing.

## Use the board over Wi-Fi

The ESP32 can be controlled without the USB cable once it has joined your network. Pairing needs the cable one time:

1. In the flow editor choose the ESP32's COM port and click **Wi-Fi**.
2. Pick your network (2.4 GHz only), type the password and click **Pair and connect**.
3. The Port box now shows the board's IP address. Click **Connect** or **Run** as usual.

Boards announce themselves on the network, so the Port box lists them by itself (look for entries starting with "Wi-Fi") and keeps working when the router
gives the board a new address. With a Wi-Fi board selected, **Upload Firmware** updates it over the air: no cable needed.

The board answers network clients only if they present the private token created during pairing, which CodeBridge keeps encrypted
for your Windows user. From code, see `Esp32WifiProvisioner` in the [API reference](api-reference.md#wi-fi-pairing-esp32wifiprovisioner-codebridgetransport).

## Create a new CodeBridge project

The easiest way to get started is by using the Visual Studio Extension.

1. Install **CodeBridge Visual Studio Tools** from *Extensions > Manage Extensions* (or double-click the `.vsix` from the GitHub release).
2. Restart Visual Studio.
3. Click **Create a new project**.
4. Search for "CodeBridge" and select the **CodeBridge WinForms App** template.
5. Click **Next**, name your project, and click **Create**.

Then open *Tools > CodeBridge Setup...* and click **Install / Update** (packages are bundled in the extension, no NuGet feed configuration needed) followed by **Add Starter Form**.

## Use the `.cbflow` editor (Visual Studio)

1. **Add > New Item > CodeBridge Visual Flow** (or double-click any `.cbflow` file).
2. In the toolbar pick the **Board** (ESP32 DevKit or Arduino Uno) and the **Port** (the list refreshes when you open it; you can also type a Wi-Fi IP address).
3. Click **Upload Firmware** once per board (installs the CodeBridge bridge firmware), then **Connect** to test: the status bar shows the firmware version.
4. Build the flow: drag blocks from the Toolbox (or double-click the canvas to search), drag from a port to another port to connect.
5. Press **Run**. Each block shows RUN / OK / FAIL while it executes and debug output appears in the **CodeBridge Output** panel. **Loop** repeats the flow until you press **Stop**.

New to this? Open **Tools > CodeBridge Tour** for a guided story, or add one of the [five examples](examples.md) with **Add > New Item > CodeBridge Example...**, and hover the blocks to see what they do.

**Outgrown the blocks?** Press **Export C#** in the toolbar: the flow becomes a readable class (`RunAsync(board, ct)`) that you can add to any project, or a console `Program.cs`, and keep editing as normal code.

Shortcuts: `Ctrl+Z / Ctrl+Y` undo/redo, `Ctrl+C / X / V / D` copy, cut, paste, duplicate, `Del` delete, `Ctrl+A` select all, `Ctrl+Space` quick-add, `Ctrl+0` reset zoom, mouse wheel zoom, middle button or `Space`+drag to pan, `Alt` while dragging disables grid snapping.

The toolbar talks to the board through `CodeBridge.FlowHost`, a helper bundled in the extension; it needs the .NET 8 (or newer) Desktop Runtime, which Visual Studio installs with the .NET desktop workload.

## Use the visual designer (WinForms)

CodeBridge includes a powerful visual flow designer for WinForms. 

1. Build your project to make sure the CodeBridge Toolbox items appear.
2. Open your Form's designer.
3. Drag the `CodeBridgeBoardComponent` from the Toolbox onto your form to manage the hardware connection.
4. Drag the `CodeBridgeFlowControl` onto your form to visually design your hardware logic with blocks (like reading sensors or turning on LEDs).

## Connecting via code (optional)

If you prefer to write code manually instead of using the visual designer:

```csharp
using CodeBridge.Core;
using CodeBridge.Core.Enums;
using CodeBridge.ESP32;

// Create the connection using the builder
var board = await CodeBridgeBuilder
    .Connect()
    .Serial("COM3") // Replace with your COM port
    .ToESP32()
    .BuildAsync();

// Turn on the built-in LED (usually GPIO 2 on ESP32)
await board.Gpio.SetPinModeAsync(2, PinMode.Output);
await board.Gpio.DigitalWriteAsync(2, PinValue.High);

// Clean up when done
await board.DisposeAsync();
```

## Next Steps

- Explore the [API Reference](api-reference.md) to see what other hardware features you can control.
- Check out the `samples/` directory for full working examples of WiFi and Serial communication.
