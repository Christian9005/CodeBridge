# Product

<!-- impeccable:product-schema 1 -->

## Platform

adaptive

## Users

.NET developers and makers who wire an ESP32 or Arduino to an app. They design a flow or write a few lines of C#, test it with a board on the desk (or the built-in simulator), and sometimes hand a control panel to someone else (home automation, workshop, classroom). They work inside Visual Studio, in a WinForms or Blazor app, or on a phone.

## Product Purpose

CodeBridge makes a microcontroller board a first-class .NET citizen: an SDK, a firmware protocol (USB and token-paired Wi-Fi), a visual flow editor (.cbflow) inside Visual Studio, ready-made UI components, an MCP server for AI assistants and integrations such as Home Assistant. Success: a user goes from an unboxed board to a LED reacting to a sensor in minutes, and trusts what the screen says about the board.

## Positioning

A visual, board-aware editor and component set that lives where .NET developers already work, and that shows what the board is really doing (live pin view, simulator, pin-safety rules) instead of leaving them to guess.

## Operating Context

- Flow editor (.cbflow): WPF custom editor in Visual Studio 2022/2026, themed from the VS theme (Light, Dark, Blue, High Contrast). Toolbar, canvas of blocks, properties, output, board view, serial monitor and plotter, Wi-Fi setup, Home Assistant window.
- WinForms: `CodeBridge.Designer.WinForms` (designer control and dialogs, own dark theme) and the toolbox components.
- Blazor: `CodeBridge.Blazor` components (`BoardStatus`, `PinToggle`, `PwmSlider`, `AnalogGauge`, `SensorChart`) themed through CSS variables, plus the `Integration.Blazor` sample dashboard.
- MAUI: `Integration.Maui` sample (Android and Windows).
- Boards: ESP32 / S3 / C3, Arduino Uno / Nano / Mega; simulator as a virtual board.

## Capabilities and Constraints

- The editor runs on the Visual Studio UI thread: it must follow the VS theme, stay testable outside VS and never throw from async void handlers.
- Blazor components are a library: consumers theme them with `--cb-*` CSS variables; the defaults must look good without any setup and in light and dark.
- Components must stay accessible (keyboard, contrast, screen readers) and respect reduced motion.
- State names are shared across surfaces: connected, connecting, faulted, simulated, disconnected.
- UI copy is English.

## Brand Commitments

Name "CodeBridge". The current accent is a blue (#0b6bcb light / #4c9dff dark in Blazor, #3a82f6 in the designer) with green for connected/on, amber for simulator/warning, red for fault; the pin-safety colours of the board view are product meaning, not decoration.

## Product Principles

1. Show the real state of the board: a status colour or picture must always be backed by a real signal, never decorative.
2. One language, native in each place: shared state colours, icons and vocabulary; platform conventions for everything else.
3. A beginner can succeed without reading docs; an expert is never slowed down.
4. Safe by default: unsafe pins, destructive actions and network access are visible and explicit.
5. Works with no hardware: the simulator is a first-class state, clearly marked, never mistaken for a real board.

## Accessibility & Inclusion

WCAG AA contrast in every theme (including High Contrast in Visual Studio), keyboard operation for every control, state never conveyed by colour alone, reduced motion respected.
