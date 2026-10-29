# CodeBridge examples

Five ready-made flows ship with the Visual Studio extension. Add one with **Add > New Item** and search for *CodeBridge Example*.
Each one is a small circuit you can build with an ESP32 DevKit (the on-board LED on GPIO 2 is enough for the first two).

Hover any block in the Toolbox or on the canvas for a short explanation, a looping animation and the meaning of every port.

| # | Example | What it teaches | Wiring |
|---|---|---|---|
| 1 | **Blink an LED** | Manual Trigger and Blink LED: the "hello world" of hardware | On-board LED (GPIO 2) |
| 2 | **Blink pattern** | Chaining blocks with Timer pauses; try **Loop** | On-board LED (GPIO 2) |
| 3 | **Night light (sensor)** | Analog Read, Number, Compare and Digital Write; Debug prints the reading; use **Loop** | LDR or potentiometer on GPIO 34 (3V3 - sensor - GPIO 34 - 10 kΩ - GND), LED on GPIO 2 |
| 4 | **Servo sweep** | Servo Write and timing | Servo signal on GPIO 13, servo power from an external 5 V supply sharing GND with the board |
| 5 | **Button controls LED** | Pin Mode (input with pull-down), Digital Read, Digital Write; use **Loop** | Push button between 3V3 and GPIO 4, LED on GPIO 2 |

## A complete project: the smart greenhouse

[`samples/SmartGreenhouse`](../samples/SmartGreenhouse) puts the sensor and module blocks together: a BME280 and a soil probe, two relays for a fan
and a pump, and an LCD. It runs on the simulator as it is. The [step-by-step guide](greenhouse.md) covers the parts, the wiring, tuning,
leaving it running day and night, Home Assistant and growing it to several beds.

## How to run an example

1. Pick the board and port in the toolbar, press **Upload Firmware** once, then **Connect** to check.
2. Press **Run**. Blocks show RUN / OK while they execute and Debug output appears in the **CodeBridge** pane of the Output window.
3. For examples that react to the world (3 and 5) tick **Loop** so the flow keeps reading until you press **Stop**.

## Tips

- A block's *Properties* panel (right side) explains the selected block and lists its settings; *Advanced* settings are folded away.
- **Arrange** tidies the layout left to right; `Ctrl+Z` undoes it.
- The *Advanced* group of the Toolbox holds Pin Mode, Digital Output, Sample Channel, Interrupt Input and Stream To Dashboard.

## Using the SDK outside Visual Studio

The same board works from any .NET 8 program. Each of these is a small, compiled sample in [`samples/`](../samples):

| Sample | What it shows |
|---|---|
| `TourSnippets` | The five examples above written in C# |
| `Integration.Console` | A console tool that reads a sensor every second |
| `Integration.Api` | An ASP.NET minimal API: `POST /led/on`, `GET /sensor/34` |
| `Integration.WindowsService` | A Windows service that logs a reading to CSV every minute and reconnects by itself |
| `SmartGreenhouse` | The greenhouse flow of the [guide](greenhouse.md), to open in the editor or run with `FlowHost serve` |

Add the SDK to your own project with `dotnet add package CodeBridge.ESP32`.
