# Changelog

All notable changes to the **CodeBridge** project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Added
- **The board says which board it is.** `BoardIdentification` (`CodeBridge.Flow`) is the one place that knows which profile is which: `FromChip` for the chip the firmware reports (`ESP32-D0WD-V3`, `ESP32-S3`, `ATmega328P`), `FromUsb` for the USB identity of a port (a guess, since a CP210x bridge is on many boards), and `IsCompatible` for whether a flow made for one board can run on another. A chip without a profile (an ESP32-C6) gives no answer instead of the wrong profile.
- `CodeBridgeBoardComponent` asks the board what it is once connected: `DetectedChip`, `DetectedBoard`, and `DescribeMismatch(flowBoardId)` for a sentence when the flow was made for another chip (an ESP32-S3 where the flow says ESP32 DevKit). `ConnectionDescription` names the board that is really plugged in. The starter form shows it in the log and in the status line.
- **Flows are files.** `CodeBridgeFlowControl.FlowFile` points the control at a `.cbflow` file of the project, the one the Visual Studio editor opens. The control reloads it when it is saved (`FlowFileLoaded`, `FlowFileFailed`, `WatchFlowFile`, `ReloadFlowFile()`) and keeps the flow that works when the file is half saved. `OpenFlowFile()` opens it in Visual Studio (or with the program Windows has for `.cbflow`). The package copies every `.cbflow` of the project next to the program when it builds, and a debug build remembers the project folder, so the running app reads the file you just saved instead of the older copy in `bin\`.
- The starter form comes with `Flows\Blink.cbflow`, runs it as soon as it is shown and runs it again every time the file is saved: edit the blink in Visual Studio, save, and the board follows. *Add CodeBridge Starter Form* adds the file to existing projects too.

### Changed
- **One board component.** `CodeBridgeEsp32Component`, the older ESP32-only component, is obsolete (it still works and is still not in the Toolbox). `CodeBridgeBoardComponent` is the one to use: it also works with Arduino boards, finds the board by itself and runs on the simulator. The docs, the tour, the Toolbox manifest and the WinForms sample use it.
- The Port box of the Visual Studio editor lists the ports that look like a board first, so the editor starts on your board and not on the built-in COM1 of the PC; its board suggestions come from `BoardIdentification` (the Wi-Fi boards by the chip they announce, the USB ports by their identity, also for the Arduino Mega).
- Fixed the USB ids of the Arduino boards: `2341:0001` is an Uno, and the Mega 2560 is `2341:0010` / `2341:0042`.
- **A new WinForms project works the moment it runs.** The *CodeBridge WinForms App* template now carries the `CodeBridge.Designer.WinForms` package in its project file (the version is filled in by the template), instead of installing it from a local folder after the project is created.
- **The starter form needs no set-up:** its *Port* box starts on *Auto*, which finds the board on USB; with no board plugged in, *Run* uses the simulator. *Run* connects by itself (the *Connect* button is gone) and a one-line status says what it found and what it is connected to. The pin and the presets are gone from the form: the flow is the `.cbflow` file.
- A board that answers but has no CodeBridge firmware is now offered the upload (*Upload it now?*) instead of a message about a timeout; after the upload the flow runs again.
- `CodeBridgeBoardComponent`: `PortName` defaults to `"auto"` and the new `FallbackToSimulator` property runs on the virtual board when no board is found. `ConnectionDescription` and `IsSimulated` describe the connection ("ESP32 DevKit on COM5"). Connection failures are now reported as `BoardConnectionException`, with a message for people and `FirmwareMissing` for apps that want to offer the upload (it was a raw `TimeoutException`).
- Finding a board skips the built-in COM1 of a desktop PC and Bluetooth ports: a recognised board comes first, then any other USB serial adapter (`BoardDiscovery.FindBoardCandidates`, `PortInfo.IsUsb`). The *Detect Serial Ports* verb of the designer uses the same order.

## [0.6.0] - 2026-10-16

First public release: the NuGet packages and the Visual Studio extension are published from this version. It brings together everything below with the first assembled 0.6.0 (2026-10-04, listed last).

### Added
- **Serial monitor and plotter** (*Tools > CodeBridge Serial Monitor*, or the *Monitor* button of the flow editor): connect to a board's USB port, read what it prints, send text with the line ending you choose (history with Up/Down), pause, clear, save, timestamps, reset the board, and switch to the **Plotter**, which draws `name:value` pairs or comma-separated numbers as a live chart like the Arduino Serial Plotter. A port held by the monitor is released automatically when you run a flow or upload firmware on it. `--port simulator` gives demo data. Host: `monitor`.
- **Code mode with PlatformIO** (*Tools > CodeBridge Firmware: New Project / Build / Upload / Clean*): create a C++ project for any supported board from a template (Blink, Serial plot, the customizable CodeBridge bridge firmware, Empty), build and upload it, with the compiler output in the CodeBridge Output pane and every error and warning in the **Error List** with a link to the line. New package `CodeBridge.PlatformIO` (`PioLocator`, `PioProject`, `PioRunner`); host: `pio-info`, `pio-new`, `pio-build`, `pio-upload`, `pio-clean`.
- **Code mode for AI assistants** (MCP): `firmware_status`, `create_firmware_project`, `read_firmware_file`, `write_firmware_file`, `build_firmware`, `upload_firmware`: the assistant writes C++, the compiler reports file, line and message, and it fixes them. Everything is confined to one workspace folder (`CODEBRIDGE_MCP_WORKSPACE`), `platformio.ini` may not use `extra_scripts`, scripts cannot be written, and uploading needs the user's hardware switch plus `confirmed=true`.
- **`CodeBridge.Mcp`: an MCP server for AI assistants** (`codebridge-mcp`, a dotnet tool). It publishes a machine-readable **board manifest** (pins, capabilities, safety, buses, electrical limits, rules) and tools to list blocks, validate, run, simulate and export flows; it works on the built-in simulator by default, real hardware needs the user's opt-in (`CODEBRIDGE_MCP_ALLOW_HARDWARE=1`) and a per-action `confirmed=true`. See [docs/mcp.md](docs/mcp.md). Tested end to end through stdio with the official MCP client.
- `BoardManifest` (SDK), `BoardProfile.BuiltInLedPin`, `CodeBridgeOptions.CommandObserver`.
- **Firmware update over Wi-Fi (OTA)**: with a Wi-Fi address in the Port box, *Upload Firmware* updates the board without a cable. This PC serves the image for a moment, the board downloads it, restarts and the editor waits until it is back and reports the new version. SDK: `Esp32OtaUpdater.UpdateAsync`; host: `ota`. `WifiTransport.CommandTimeout` lets slow commands run longer than 5 seconds.
- **Boards are found automatically**: the firmware announces itself on the network (mDNS, `codebridge-xxxx.local`, with its MAC address, version and chip) and the editor lists the boards it finds in the Port box. Pairing tokens are filed under the board's identity as well as its IP address, so a new IP from the router no longer loses the token. SDK: `WifiBoardFinder`, `MdnsClient`; host: `wifi-boards`.
- **Board view** in the flow editor (*Pins* button): a picture of the board that shows what the flow does to it while it runs, on real boards and on the simulator. Outputs light up (HIGH/LOW), PWM pins fill like a bar, servos show their angle, analog inputs their reading, tones their pitch, and the built-in LED glows or dims. Fed by `pin` messages from the host (`CommandTapTransport` / `PinActivity` report every command with the board's answer).
- **Simulator**: a virtual board that speaks the firmware protocol (`SimulatedTransport`, `.Simulator()` in the builder, *Simulator* in the editor's Port list and `--port simulator` in the host). Analog inputs follow a wave, digital inputs, sensors and distances can be scripted, outputs and PWM can be inspected.
- **`CodeBridge.Hosting`**: `AddCodeBridge()` registers a shared `BoardService` (serialized commands, automatic connection, background reconnection with backoff, simulator fallback, `Changed` event).
- **`CodeBridge.Blazor`**: `BoardStatus`, `PinToggle`, `PwmSlider`, `AnalogGauge`, `SensorChart` components (bUnit-tested) and the `Integration.Blazor` dashboard sample.
- **`CodeBridge.HomeAssistant`**: MQTT discovery bridge (switch, light, sensor, binary sensor), availability and last will, command handling, re-announcement when Home Assistant restarts, mDNS finder for Home Assistant; tested against a real embedded MQTT broker. Sample `Integration.HomeAssistant`.
- **Home Assistant from the editor**: a Home Assistant button next to Wi-Fi finds Home Assistant on the network, tests the MQTT broker (tells apart an unreachable machine, a missing broker and a wrong password) and publishes the flow's Digital Output, PWM Output, Analog Read and Digital Read blocks as a switch, a dimmable light, a sensor and a binary sensor. The broker password is stored encrypted for the Windows user. New `ha-find`, `ha-test` and `ha-bridge` FlowHost commands, `HomeAssistantFlow.EntitiesFor` and `HomeAssistantBrokerCheck` in the SDK package, `docs/home-assistant.md` (including Mosquitto for Home Assistant in Docker).
- **ESP-NOW mesh and device fleets** ([docs/mesh.md](docs/mesh.md)): boards without a cable or Wi-Fi are reached through a gateway board that keeps its Wi-Fi. Firmware 0.9.1: authenticated frames (HMAC-SHA256 with a shared key, single-use challenge against replay), `MESH`, `MESHKEY`, `MESHNAME`, `MESHCH`, `MESHON`/`MESHOFF`, `MSCAN`, `MRLY`; Wi-Fi/token/OTA/MQTT commands are never relayed; modem sleep is off while the mesh is on. SDK: `MeshGateway`, `MeshNodeTransport`, `SimulatedMeshNetwork`, and `DeviceFleet` in `CodeBridge.Hosting` (named devices, discovery, persistence, `ForEachAsync`, `AddCodeBridgeFleet`). Editor: a **Mesh** button (set boards up, scan, pick one; the Port box takes `mesh://gateway/MAC`), host commands `mesh-status`, `mesh-scan`, `mesh-provision`, and a virtual gateway with three nodes at `mesh://simulator/...`.
- **22 new blocks for sensors, modules and helpers**: DHT11/22, HC-SR04 distance, DS18B20, BME280, BH1750, MPU6050, INA219, TCS34725 colour, PIR motion, Button (debounced, internal pull-up), Analog Sensor (%); Relay, Buzzer, RGB LED, NeoPixel strip, LCD and OLED text (`{value}` in the text), DC motor, Stepper; Threshold, Logic Gate and Calculate. Each is described once (`ModuleBlocks`), so the catalog, the runtime, the C# export and the pin validation always agree; every block has beginner help and an animated demo, and the exported code is checked against the runtime command by command. The Arduino boards get the ones that only need GPIO.
- **XIAO ESP32-S3 and ESP32-S2 Mini** boards, each with its own firmware build (the generic ESP32-S3 image talks on the UART pins, not on the chip's USB port).
- **MAUI sample** (`Integration.Maui`, Android and Windows).
- **One design language across Blazor, MAUI, WinForms and the Visual Studio editor** (see `PRODUCT.md`): shared state colours (green live, blue value, amber simulator, red fault), a pin chip on every control, an explicit state word beside every status dot and an amber SIMULATOR chip. Blazor: new `codebridge.css` tokens (`--cb-*`, `data-cb-theme` override, focus rings, reduced motion), cards with a `meter`/`switch` role, a chart with axis, area and empty state, `SensorChart` gains `Unit` and `ScaledMax`, and `.cb-page` / `.cb-grid` helpers. MAUI sample re-skinned to the same palette with card, chip and meter styles. WinForms designer: shared dark palette, really dark drop-downs and buttons, a state-aware flow badge (ready, running, completed, failed). Fixed: the WinForms flow editor pushed its inspector off the window (the layout column grew to the toolbar width) and the Visual Studio editor hid the Monitor, Pins and zoom buttons below about 1500 px (the toolbar now wraps).
- **More boards**: ESP32-S3 DevKit, ESP32-C3 DevKit, Arduino Nano (both bootloaders) and Arduino Mega 2560, with pin maps, ADC ranges, per-chip firmware images (`firmware/prebuilt/<chip>`), esptool chip/offset and Arduino FQBN taken from the board profile. The firmware builds for all of them in CI.

### Fixed
- The extension tags in the VSIX manifest are separated by semicolons, as the manifest schema asks. They were comma-separated, which the Marketplace read as one 133-character tag and refused.
- The author of the extension in the VSIX manifest is the Marketplace publisher's display name ("Christian Albarracin"), which the Marketplace requires to match; the publisher ID stays in `vs-publish.json`.
- The installed extension only shipped the firmware images of the original ESP32, so ESP32-S3 and ESP32-C3 boards could not be flashed from it: every chip's images are packaged now.
- PlatformIO child processes inherited the caller's standard input; under an MCP server that is the protocol pipe, so the build could swallow protocol bytes and the PlatformIO probe timed out. Their input is now closed.
- The validator did not check the pin of the PWM Output block (a missing or input-only pin was accepted and only failed on the board) nor an empty Map range; both are errors now.
- A board that lost its Wi-Fi (router restart, out of range, slow start) stayed unreachable until reset: it gave up joining after 15 seconds at boot and never restarted its network server. Firmware 0.9.0 now keeps trying for as long as it is on, and starts and stops the TCP server and mDNS with the link.
- The Wi-Fi scan returned no networks while the board was still trying to join a saved network that was not around (found on real hardware); the attempt is now paused for the scan and resumed afterwards.
- Pairing a Wi-Fi network no longer overwrites the saved one unless the new network was joined successfully (found on real hardware: a failed attempt used to erase the working network).
- Deep-sleep wake-up by pin validated the pin and now builds on chips without ext1 wake-up (ESP32-C3).
- GPIO limits follow the chip (ESP32-S3 has pins up to 48) instead of a fixed 0-39.

---

### Changed
- **NuGet id of the abstractions: `CodeBridge.Abstractions`.** The id `CodeBridge.Core` on nuget.org belongs to another project, so the package built from `src/CodeBridge.Core` is published as `CodeBridge.Abstractions`. The assembly and the namespace are still `CodeBridge.Core`; only the package id changes.

### Also in 0.6.0 (first assembled on 2026-10-04)

Security and stability hardening, Wi-Fi pairing from the editor, and the first math/PWM blocks.

#### Security (firmware 0.9.0)
- **The Wi-Fi/TCP port is closed by default.** Clients must authenticate with a pairing token (`AUTH`) set over USB (`WTOK`); before, anyone on the network could drive the GPIO pins, change the Wi-Fi settings or start an OTA update.
- OTA only accepts `http(s)` URLs, checks every flash write, aborts on a stalled download and cannot be started without authentication.
- Constant-time token check, unauthenticated clients are dropped after 5 s, three failures drop the connection.

#### Fixed
- Command lines longer than the buffer were executed truncated; they are now discarded with `ERR:Line too long` (USB and TCP).
- `R`, `RS`, `P`, `I`, `V`... matched `RST`, `PING`, `INFO`, `VER` by prefix (a stray `R` rebooted the board); commands now match exactly.
- I2C commands validate address and length (`IR`/`IRR` accepted any length, `IW`/`IWR` allocated whatever the line said).
- Wi-Fi passwords containing `:` were cut; `WCFG` takes the rest of the line and the new `WCFGX` accepts hex so any character works.
- `WifiTransport`: a late answer to a timed-out command could be taken as the answer to the next one (the connection now re-synchronizes with `PING` first); lines are length-limited; a dropped connection is reported instead of hanging; authentication errors are explained.
- One chatty TCP client can no longer starve the board's main loop.

#### Added
- **Wi-Fi setup in the flow editor** (toolbar button): scan networks, enter the password, pair. Tokens are stored DPAPI-encrypted; the Port box accepts the board's IP afterwards.
- `Esp32WifiProvisioner` (`ScanAsync`, `GetStatusAsync`, `ProvisionAsync`, `ClearTokenAsync`), `.WiFi(ip, port, token)` and `WifiTransport(ip, port, token)`; `FlowHost wifi-scan|wifi-status|wifi-config|wifi-unpair`.
- **Map** block (convert a range, e.g. a 0-4095 reading into 0-255, with clamp/round) and **PWM Output** block (LED brightness, motor speed), both in the editor, the runtime and *Export C#*.
- Analog Read `samples` option (average several readings) to calm ADC noise.
- Tests: Wi-Fi transport against a fake 0.9 board (token, oversized lines, late answers), Map/PWM runtime-vs-export equivalence.

#### Changed
- Firmware protocol 0.9: the SDK refuses older firmware (upload the new one from the editor). Wi-Fi clients without a token are rejected with a clear message.
- Samples `WiFiSetup` and `WiFiBlink` follow the pairing flow.

---

## [0.5.7] - 2026-09-30

First publishable release: the VSIX and the NuGet packages are now built from a single version.

### Added
- `build/pack.ps1` builds all NuGet packages (with symbols/SourceLink) and the VSIX; `release.yml` pushes to nuget.org and creates the GitHub release on `v*` tags.
- `CodeBridge.Designer.WinForms` now ships the ESP32 firmware (sources + prebuilt binaries) and copies it next to consuming apps.
- Automatic, pinned (esptool v5.4.0, SHA-256 verified) esptool download the first time firmware is flashed.
- Visual Studio theme integration (Light, Dark, Blue, High Contrast) for the `.cbflow` editor and the setup window.
- Release-consistency tests (versions, no developer paths, editor/runtime block parity) and Arduino firmware compatibility tests.
- CI builds both firmwares with PlatformIO; `SECURITY.md`, `docs/publishing.md`.

- **`.cbflow` editor rebuilt to behave like a Visual Studio tool**:
  - Toolbar with Board and Port pickers, *Connect* (connection test with firmware version), *Upload Firmware*, *Run / Stop* with optional *Loop*, Undo/Redo, zoom and an Output panel; status bar with connection state and problem counts.
  - Blocks run on the real board with live per-block badges (RUN / OK / SKIP / FAIL) and debug output.
  - Live validation through the runtime validator, with ERR/WARN badges and tooltips on the blocks.
  - Drag blocks from the Toolbox, double-click them, double-click the canvas (or Ctrl+Space) for a quick-add search, or use the context menu.
  - Multi-select (Ctrl/Shift click, selection rectangle), move together, snap to grid (Alt disables), arrow-key nudging, copy / cut / paste / duplicate, delete wires, undo/redo (100 steps) wired into the Edit menu.
  - Connections check types and loops, replace an occupied input, and snap to the nearest compatible port.
  - Pin options and blocks follow the selected board (ESP32 DevKit / Arduino Uno); the catalog is generated from the runtime catalog so it can no longer drift.
  - Zoom towards the cursor, pan with middle button or Space + drag, fit to view, infinite themed grid; right-click no longer resets the zoom.
  - Properties panel with readable labels and units, per-type validation, advanced section and a flow overview.
  - Every control (combo boxes, scroll bars, menus, check boxes) is themed with the active Visual Studio theme.
- The editor writes its messages to a **CodeBridge** pane of the Visual Studio Output window (the embedded panel is only a fallback outside Visual Studio).
- **Export C#**: the flow editor's *Export C#* button turns a flow into readable C# that uses the SDK: a static class with `RunAsync(IBoard board, CancellationToken ct)` that is added to the project next to the flow (or copied to the clipboard), or a complete console `Program.cs`. It follows the Loop checkbox, picks the project's namespace and names the class after the file. The generator is a public API (`CodeBridge.Flow.CodeGeneration.FlowCSharpGenerator`), so build tools can use it too. Tests compile the exported code with Roslyn and check that it sends exactly the same commands to the board as the flow runtime, for every shipped example. Blocks without an export yet (Sample Channel, Interrupt Input, Stream To Dashboard) become `// TODO` comments with a warning.
- **CodeBridge Tour** (Tools > CodeBridge Tour, offered once the first time a flow is opened): eight chapters that tell one story, from the first blink to using the SDK in a web API or a Windows service. Each chapter explains the idea, shows the animated blocks involved, lists the steps, has an **Open this example in my project** button and a **The same thing in C#** viewer (copy, or open in the editor). The C# shown is real, compiled sample code, so it cannot go stale.
- New SDK samples that compile in CI: `Integration.Console`, `Integration.Api` (ASP.NET minimal API: `POST /led/on`, `GET /sensor/34`, tested against a real ESP32) and `Integration.WindowsService` (Worker Service that logs a reading to CSV and reconnects by itself), plus `TourSnippets` (the five examples in C#).
- **Every block now explains itself**: hovering a block (Toolbox or canvas) shows a plain-language description, a looping vector animation of what it does (LED lighting, timer counting, servo sweeping, sensor wave...), the meaning of each input/output port and a tip. The Properties panel shows the same explanation. Animations are drawn with WPF shapes, so they follow the theme and add nothing to the VSIX.
- **Five example flows** in *Add > New Item* (Blink an LED, Blink pattern, Night light with a sensor, Servo sweep, Button controls LED), documented in `docs/examples.md`. Tests keep them valid.
- **Arrange** button lays the blocks out left to right by execution order with even spacing; blocks are more compact (narrower, tighter rows); the Toolbox keeps the basics up front and moves Pin Mode, Digital Output, Sample Channel, Interrupt Input and Stream To Dashboard to a collapsed *Advanced* group; backward wires bend less.
- `CodeBridge.FlowHost`, a small .NET 8 helper shipped inside the VSIX, gives the net472 extension access to ports, firmware upload (live esptool output), connection test, validation and flow execution.

### Fixed
- Editing a `.cbflow` after saving never marked it modified again (the dirty flag was never reset).
- **VSIX shipped without its NuGet packages**, so "Install CodeBridge WinForms Package" failed on any other machine. The Release build now fails if packages are missing.
- Hard-coded `C:\Projects\CodeBridge` paths removed from the installer and firmware lookup.
- Package installation restores CodeBridge packages from the bundled feed and their dependencies from nuget.org (previously only the local feed).
- Firmware flashing looked for `.pio/build/esp32dev` while PlatformIO builds `esp32`; it also skipped `boot_app0.bin`.
- Arduino Uno firmware reported version `0.1.0-uno`, which the SDK always rejected as a protocol mismatch.
- `.ToSTM32()`/`.ToArduino()` followed by `BuildAsync()` silently returned an ESP32 board; it now throws `NotSupportedException`.
- `SerialTransport` left the COM port open (locked) after a failed connect, leaked the port on disconnect, and could miss responses when several lines arrived in one read.
- Version drift between `Directory.Build.props`, the VSIX manifest and the toolbox manifest; Visual Studio range is now `[17.0, 19.0)`.

### Changed
- README only claims what is implemented (ESP32 supported, Arduino Uno basic, STM32/PIC planned).
- Stray test project `CodeBridge.ESP32.Tests` merged into `CodeBridge.Core.Tests`.

---

## [0.4.2] - 2026-09-16

### Added
- Visual Studio Marketplace 400x400 brand preview image (`Assets/CodeBridgePreview.png`).
- Debug symbols (`.pdb`) packaging inside VSIX for enhanced diagnostics.
- Comprehensive offline unit tests for USB VID/PID detection and hardware identification.
- Community health files: `CHANGELOG.md` and `CONTRIBUTING.md`.

### Fixed
- Fixed Visual Studio 2022 compatibility target in `source.extension.vsixmanifest` (`[17.0, 18.0)`).
- Prevented data loss in `.cbflow` editor when loading corrupted or malformed files.
- Synchronized visual flow block catalog properties 100% with runtime engine.
- Replaced `async void` anti-pattern in WinForms designer action list with safe exception handling.
- Removed hardcoded local developer paths from NuGet package installer error prompts.
- Optimized VSIX package size by eliminating duplicate uncompressed template files.

---

## [0.4.1] - 2026-08-25

### Added
- Integrated WPF Visual Flow Editor (`.cbflow`) as a native document window in Visual Studio.
- Live Properties Inspector with interactive controls for GPIO pin pickers, timings, and boolean toggles.
- Two-way parameter synchronization and dirty tracking for visual flows.
- Automatic alias resolution for legacy and shortened block names (`Start`, `DigitalWrite`, `Delay`, etc.).

---

## [0.4.0] - 2026-08-10

### Added
- Native firmware upload system via direct `esptool.exe` execution, removing PlatformIO CLI dependency.
- Automatic USB board discovery with VID/PID recognition for CP210x, CH340, CH9102, Arduino, and STM32.
- Item template for `.cbflow` visual flow graphs.
- Visual Studio 2022 project template for CodeBridge WinForms applications.

---

## [0.3.0] - 2026-08-01

### Added
- Extended hardware driver catalog for ESP32: DHT11/22, DS18B20, BMP280, HC-SR04 sensors.
- Actuator support: DC Motors, Steppers, Servos, Relays.
- Display drivers: I2C Character LCD, SSD1306 OLED.
- High-performance sampling engine with buffered ring buffers and backpressure management.

---

## [0.2.0] - 2026-07-15

### Added
- Binary Bridge Protocol implementation with frame validation and checksum verification.
- Dual-transport architecture: USB Serial and TCP/WiFi transports.
- WinForms toolbox designer integration with Smart Tags and live property editors.

---

## [0.1.0] - 2026-06-30

### Added
- Initial project architecture and core hardware abstractions (`IBoard`, `ITransport`, `IGpioController`).
- Base protocol definition and board profile catalog.
