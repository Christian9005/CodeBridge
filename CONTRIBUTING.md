# Contributing to CodeBridge

Thank you for your interest in contributing to **CodeBridge**! We welcome contributions from the community to help make microcontroller and IoT programming seamless in .NET and Visual Studio.

---

## Development Environment Requirements

- **Operating System**: Windows 10/11 (x64)
- **IDE**: Visual Studio 2022 (version 17.0 or newer)
  - Workload: **.NET desktop development**
  - Workload: **Visual Studio extension development**
- **SDK**: .NET 8.0 SDK or newer

---

## Getting Started

1. **Fork and Clone** the repository:
   ```bash
   git clone https://github.com/Christian9005/CodeBridge.git
   cd CodeBridge
   ```

2. **Restore & Build** the solution:
   ```bash
   dotnet build CodeBridge.slnx -c Release
   ```

3. **Run Tests**:
   ```bash
   dotnet test tests/CodeBridge.Core.Tests/CodeBridge.Core.Tests.csproj -c Release
   ```

---

## Project Structure

- `src/CodeBridge.Core/`: Hardware abstractions (`IBoard`, `ITransport`), protocol definitions, and basic models.
- `src/CodeBridge.Flow/`: Visual programming engine, document models (`FlowDocument`), validation, and execution runtime.
- `src/CodeBridge.Transport/`: Serial and WiFi transport implementations, board discovery with USB VID/PID recognition.
- `src/CodeBridge.ESP32/`: Concrete drivers and peripheral implementations for ESP32 boards.
- `src/CodeBridge.Designer.WinForms/`: WinForms design-time controls, canvas, firmware uploader, and property designers.
- `src/CodeBridge.VisualStudio/`: Visual Studio 2022 extension package, WPF visual flow editor, and project/item templates.
- `src/CodeBridge.FlowHost/`: .NET 8 helper bundled in the VSIX (as `FlowHost.zip`) that gives the net472 extension access to serial ports, firmware upload, connection test, validation and flow execution. `build/pack.ps1 -Vsix` publishes it and regenerates `Assets/catalog.*.json` (the editor block catalog) from the runtime catalog.
- `firmware/`: C++/Arduino firmware sketches for supported boards.
- `tests/`: Automated unit and integration tests.

---

## Code Guidelines

- Follow standard C# naming conventions and clean architecture principles.
- Avoid `async void` methods; use `async Task` or safe wrappers with `try/catch`.
- Ensure public APIs contain XML documentation where appropriate.
- Verify that changes compile cleanly with **0 errors and 0 warnings**.
- Keep Visual Studio extension code thread-safe by respecting Visual Studio Threading analyzers (`VSTHRD`).

---

## Submitting Pull Requests

1. Create a feature branch from `main`:
   ```bash
   git checkout -b feature/your-feature-name
   ```
2. Commit your changes with clear, descriptive commit messages.
3. Push to your fork and submit a Pull Request against `main`.
4. Ensure all CI checks pass.
