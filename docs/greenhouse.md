# Build a smart greenhouse

A small greenhouse that looks after itself: it reads the air and the soil, switches a fan on when it gets too hot, waters the plants when the soil is dry, and shows both readings on a display. It is one flow of ten blocks, it runs on the simulator before you buy anything, and the same file runs on a real ESP32.

![The greenhouse flow in the Visual Studio editor](media/greenhouse-flow.png)

It is also a tour of what CodeBridge can do, one step at a time:

| Step | What you get | CodeBridge feature |
|---|---|---|
| [1. Try it](#1-try-it-with-no-hardware) | The greenhouse running on a virtual board | Simulator, flow editor |
| [2. Build it](#2-build-it) | The real thing, about 30 USD of parts | One-click firmware, sensor and relay blocks |
| [3. Tune it](#3-tune-it) | Thresholds that fit your plants | Properties, Debug, Loop |
| [4. Leave it running](#4-leave-it-running) | A greenhouse that runs day and night | `FlowHost serve`, Windows service |
| [5. See it from your phone](#5-see-it-from-your-phone) | Pump, fan and soil in Home Assistant | `CodeBridge.HomeAssistant` |
| [6. Grow it](#6-grow-it) | More beds, more sensors, your own app | ESP-NOW mesh, .NET IoT drivers, Export C#, AI assistant |

The flow is [`samples/SmartGreenhouse/greenhouse.cbflow`](../samples/SmartGreenhouse/greenhouse.cbflow).

## How it works

```
                 ┌─ temperature ─> Hot? (above 28 °C) ──> Fan relay   (GPIO 27)
Manual Trigger ─>│  Weather (BME280) ─ temperature ─────> LCD row 1  "Air 24.5 C"
   (each pass)   │                   └ humidity ────────> Debug
                 └─ Soil probe (%) ─> Dry? (below 35 %) ─> Pump relay  (GPIO 26)
                                    └──────────────────> LCD row 2  "Soil 62 %"
```

Every pass reads both sensors, decides, and switches the relays. With **Loop** (or `serve`, step 4) a pass runs every second, so the greenhouse reacts within a second.

| Block | Settings | Why |
|---|---|---|
| Weather (BME280) | address 0x76 | Air temperature, humidity and pressure from one I2C chip |
| Analog Sensor (%) | GPIO 34, inverted | A capacitive soil probe gives a *higher* voltage when *dry*: inverted, 100 % means wet |
| Threshold "hot" | above 28 | True when the air is above 28 °C |
| Threshold "dry" | below 35 | True when the soil is below 35 % |
| Relay "fan", "pump" | GPIO 27 and 26, active low | Most relay boards switch on when the pin is LOW; the block handles it |
| LCD Text ×2 | 0x27, rows 0 and 1 | `Air {value} C` and `Soil {value} %`: `{value}` is replaced by the wired number |
| Debug | | Prints the humidity in the Output window |

## 1. Try it with no hardware

**In Visual Studio:** open `samples/SmartGreenhouse/greenhouse.cbflow`, choose **Simulator** as the port, tick **Loop** and press **Run**. Each block shows its values as it runs; the virtual board answers the BME280 and the LCD, and the soil probe follows a slow wave, so you see the pump relay switch on and off.

**Without Visual Studio** (Windows, Linux or macOS, .NET 8):

```bash
dotnet run --project src/CodeBridge.WebEditor -- --flow samples/SmartGreenhouse/greenhouse.cbflow --port simulator
```

Open the address it prints. It is the same flow in a [browser editor](web-editor.md): move blocks, change a threshold, press **Run**.

## 2. Build it

### Parts

Approximate prices from the usual online shops; any compatible module works.

| Part | About | Notes |
|---|---|---|
| ESP32 DevKit (ESP32-WROOM-32) | 6 USD | Any board with the CodeBridge firmware; the XIAO ESP32-S3 and others work with other pins |
| BME280 module (I2C) | 4 USD | Check it says **BME**280: the cheaper BMP280 has no humidity |
| Capacitive soil moisture sensor v1.2 | 2 USD | Capacitive, not the two-prong resistive kind, which corrodes in weeks |
| 2-channel 5 V relay module | 2 USD | With optocouplers, "low level trigger" |
| 16×2 LCD with I2C backpack (PCF8574) | 4 USD | Optional: the flow runs without it |
| 5 V mini water pump + tube | 3 USD | |
| 5 V fan (40 or 50 mm) | 3 USD | |
| 5 V 2 A power supply | 4 USD | Powers the pump and fan through the relays |

### Wiring

| From | To ESP32 | Notes |
|---|---|---|
| BME280 VCC, GND | 3V3, GND | |
| BME280 SDA, SCL | GPIO 21, GPIO 22 | The ESP32's I2C pins |
| LCD backpack VCC, GND | 5V (VIN), GND | |
| LCD backpack SDA, SCL | GPIO 21, GPIO 22 | Shares the bus with the BME280. Most backpacks pull SDA/SCL up to 5 V; the ESP32 usually copes, but a level shifter (or removing the backpack's pull-ups) is the safe way |
| Soil probe VCC, GND | 3V3, GND | |
| Soil probe AOUT | GPIO 34 | An ADC1 pin, so it keeps working with Wi-Fi on |
| Relay VCC, GND | 5V (VIN), GND | |
| Relay IN1, IN2 | GPIO 27 (fan), GPIO 26 (pump) | |

The pump and the fan take their 5 V from the supply **through the relay contacts** (COM and NO), never from the ESP32's pins. Keep water away from the board, and do not switch mains with these modules.

### Run it

1. Plug the ESP32 in, choose its port in the editor and press **Upload Firmware** (once per board).
2. Press **Run**: the Output window shows the readings, and the relays click if it is hot or dry.
3. Tick **Loop** to keep it watching.

## 3. Tune it

- **Your plants' temperature.** Select the *hot* block and change **Limit**: 28 °C suits most vegetables; seedlings like it warmer.
- **Your soil probe.** Every probe reads differently. Run once with the probe in dry air and once in a glass of water, and look at the **raw** output of the soil block in the Output window (wire it to a Debug block). If dry reads 3000 and wet 1300, 35 % is a sensible "water now" level; move it up for thirsty plants.
- **Not too much water.** The pump runs for as long as the soil reads dry, and water takes a while to reach the probe. Put the probe close to where the water drips, or add a Timer block after the pump so each pass waters for a few seconds and then waits.

## 4. Leave it running

A greenhouse has to work when Visual Studio is closed. `FlowHost serve` runs the same file forever: it connects, runs a pass every `--interval` milliseconds, and connects again by itself when the board is unplugged, loses Wi-Fi or reboots.

```powershell
CodeBridge.FlowHost serve --flow C:\greenhouse\greenhouse.cbflow --port COM3 --interval 5000 --log C:\greenhouse\greenhouse.log
```

Add `--service` to install it as a Windows service that starts with the PC. Everything about it is in [A flow that runs by itself](headless.md).

## 5. See it from your phone

[`CodeBridge.HomeAssistant`](home-assistant.md) puts the board in Home Assistant through MQTT, with no YAML. From a small .NET program:

```csharp
services.AddCodeBridge(o => o.Port = "COM3");
services.AddCodeBridgeHomeAssistant(
    o => { o.Broker = "192.168.1.10"; o.Username = "codebridge"; o.Password = "..."; },
    ha => ha.Switch("pump", "Greenhouse pump", pin: 26)
            .Switch("fan", "Greenhouse fan", pin: 27)
            .AnalogSensor("soil", "Soil moisture", pin: 34, unit: "%", scale: 100.0 / 4095));
```

The pump and fan become switches you can also flip by hand, and the soil reading a sensor with history and alerts. (The editor's **Home Assistant** button publishes the GPIO blocks of a flow; publishing the sensor and relay blocks of this flow directly is a good first contribution.)

## 6. Grow it

- **More beds, no more cables.** Put an ESP32 with a soil probe and a pump in each bed and let them talk to the first board over the [ESP-NOW mesh](mesh.md), while it keeps its Wi-Fi. Each bed is a board of its own to the flow editor (`mesh://COM3/<MAC>` as the port), and `DeviceFleet` drives them all from one program.
- **More sensors.** The [.NET IoT blocks](iot-bindings.md) add CO₂ (SCD4x: open a window above 1000 ppm), light (BH1750, MAX44009: switch a grow lamp at dusk), precise temperature (SHT4x), a real-time clock for watering at fixed hours, and **I2C Scan** to find any chip you wire.
- **Your own app.** **Export C#** turns the flow into a class you can put behind a web API, a Blazor dashboard ([`CodeBridge.Blazor`](../src/CodeBridge.Blazor) has gauges and charts) or a MAUI app.
- **Ask an assistant.** With the [MCP server](mcp.md), an AI assistant can read the board's pin rules and build or change the flow for you: *"Add a light sensor on GPIO 35 and switch a grow lamp on GPIO 25 when it is dark between 6 and 22 h"*. It tests the result on the simulator before anything touches the board.

## What has been tried

- The flow is checked by the test suite on every build: it must stay valid, a hot and dry greenhouse must switch both relays on, a cool and watered one both off, and it must export to C# ([`SmartGreenhouseTests`](../tests/CodeBridge.Core.Tests/Flow/SmartGreenhouseTests.cs)).
- It has run on a real ESP32 DevKit with a BME280: 20.3 °C, 73 % and 716.6 hPa, and the right relay decisions.
- The pump, fan, LCD and soil probe have not been wired on our desk yet. If you build it, tell us how it went: an issue with a photo is the best contribution there is.
