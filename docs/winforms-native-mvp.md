# WinForms Native MVP

This is the first native Visual Studio workflow for CodeBridge.

## Toolbox Components

- `CodeBridgeFlowControl` is the visible component that lives on a WinForms surface.
- `CodeBridgeEsp32Component` is the non-visual component that lives in the component tray and owns the ESP32 connection.
- Double-click `CodeBridgeFlowControl`, or call `ShowEditor(owner)`, to open the visual flow editor.
- Use the `CodeBridgeFlowControl` smart tag to load starter presets directly in the WinForms designer.
- Call `RunAsync(...)` to run once in `Trigger` mode or continuously in `Loop` mode.
- Call `Stop()` to end a running loop.

## Flows Are Files

The flow a control runs is a `.cbflow` file of the project, the same file the CodeBridge editor of Visual Studio opens (*Add New Item > CodeBridge Flow*, or the `Flows\Blink.cbflow` that the starter form comes with):

```csharp
codeBridgeFlowControl1.FlowFile = "Flows/Blink.cbflow";   // or set FlowFile in the Properties window
```

- The CodeBridge package copies every `.cbflow` of the project next to the program when it builds.
- A debug build also remembers the project folder, so the running app reads the file you edit and save in Visual Studio, not the older copy in `bin\`. Release builds read the copy next to the program.
- Save the file and the control reloads it (`FlowFileLoaded`, with `IsReload` true). A half-saved or invalid file raises `FlowFileFailed` and the flow that was loaded keeps working. Set `WatchFlowFile = false` to reload only when you call `ReloadFlowFile()`.
- `OpenFlowFile()` opens the file in Visual Studio (when the project is open there) or with the program Windows has for `.cbflow`.
- The starter form uses all of this: it runs `Flows\Blink.cbflow` when it starts and runs it again every time the file is saved.

## Built-In Presets

- Blink GPIO 2 once.
- Blink GPIO 2 once for active-low LED boards.
- Read GPIO 2 and send the value through a Debug block.

In Visual Studio, select `CodeBridgeFlowControl` and use the smart tag `Load Preset...` to choose the preset, GPIO, delay, and active-low mode.

From code:

```csharp
codeBridgeFlowControl1.LoadBlinkPreset(pin: 2, delayMs: 500);
codeBridgeFlowControl1.LoadDigitalReadDebugPreset(pin: 2);
```

## Runtime Trace

`CodeBridgeFlowControl` exposes node-level execution events:

```csharp
codeBridgeFlowControl1.TraceDelayMs = 120;
codeBridgeFlowControl1.FlowTrace += (_, trace) =>
{
    logTextBox.AppendText($"{trace.Kind}: {trace.NodeId}{Environment.NewLine}");
};
```

Use `TraceDelayMs` while recording demos or debugging hardware. Keep it at `0` for normal fast execution.

## Demo

Run:

```powershell
dotnet run --project samples\WinFormsNativeDemo\CodeBridge.Samples.WinFormsNativeDemo.csproj
```

The standalone visual designer host is separate from the component library:

```powershell
dotnet run --project tools\CodeBridge.Designer.WinForms.Host\CodeBridge.Designer.WinForms.Host.csproj
```

`src\CodeBridge.Designer.WinForms` is the reusable Toolbox/control package. `tools\CodeBridge.Designer.WinForms.Host` is only the internal test shell for the full designer surface.

Recommended first test:

1. Connect the ESP32 over USB.
2. Click `Refresh Ports`.
3. Select the COM port and keep baud `115200`.
4. Click `Connect`.
5. Click `Edit Flow` to inspect or adjust GPIO pin values.
6. Click `Run`.

The default flow blinks GPIO 2 once. To test repeated execution, switch `Mode` to `Loop`, set the loop interval, click `Run`, then click `Stop`.

## Visual Studio Usage

For a normal WinForms app, reference `CodeBridge.Designer.WinForms`, then add these components to the Toolbox:

- `CodeBridge.Designer.WinForms.CodeBridgeFlowControl`
- `CodeBridge.Designer.WinForms.CodeBridgeEsp32Component`

A basic button click can stay close to normal WinForms code:

```csharp
private async void runButton_Click(object sender, EventArgs e)
{
    codeBridgeEsp32Component1.PortName = "COM3";
    codeBridgeFlowControl1.ExecutionMode = FlowExecutionMode.Trigger;
    await codeBridgeEsp32Component1.RunAsync(codeBridgeFlowControl1);
}
```

For a continuous embedded-style loop:

```csharp
private async void startButton_Click(object sender, EventArgs e)
{
    codeBridgeFlowControl1.ExecutionMode = FlowExecutionMode.Loop;
    codeBridgeFlowControl1.LoopIntervalMs = 1000;
    await codeBridgeEsp32Component1.RunAsync(codeBridgeFlowControl1);
}

private void stopButton_Click(object sender, EventArgs e)
{
    codeBridgeFlowControl1.Stop();
}
```

## Local Packaging

Create local NuGet packages:

```powershell
dotnet pack C:\Projects\CodeBridge\src\CodeBridge.Core\CodeBridge.Core.csproj -c Release -o C:\Projects\CodeBridge\artifacts\packages
dotnet pack C:\Projects\CodeBridge\src\CodeBridge.Transport\CodeBridge.Transport.csproj -c Release -o C:\Projects\CodeBridge\artifacts\packages
dotnet pack C:\Projects\CodeBridge\src\CodeBridge.ESP32\CodeBridge.ESP32.csproj -c Release -o C:\Projects\CodeBridge\artifacts\packages
dotnet pack C:\Projects\CodeBridge\src\CodeBridge.Flow\CodeBridge.Flow.csproj -c Release -o C:\Projects\CodeBridge\artifacts\packages
dotnet pack C:\Projects\CodeBridge\src\CodeBridge.Designer.WinForms\CodeBridge.Designer.WinForms.csproj -c Release -o C:\Projects\CodeBridge\artifacts\packages
```

Then add `C:\Projects\CodeBridge\artifacts\packages` as a local NuGet source in Visual Studio and install `CodeBridge.Designer.WinForms` in a clean WinForms app.

Smoke test from the CLI:

```powershell
dotnet new winforms -f net8.0 -n CodeBridge.PackageSmokeSplit -o C:\Projects\CodeBridge\artifacts\package-smoke-split
dotnet add C:\Projects\CodeBridge\artifacts\package-smoke-split\CodeBridge.PackageSmokeSplit.csproj package CodeBridge.Designer.WinForms --version 0.1.0-preview.1 --source C:\Projects\CodeBridge\artifacts\packages --no-restore
dotnet restore C:\Projects\CodeBridge\artifacts\package-smoke-split\CodeBridge.PackageSmokeSplit.csproj -p:RestorePackagesPath=C:\Projects\CodeBridge\artifacts\package-smoke-split\.packages -p:RestoreAdditionalProjectSources=C:\Projects\CodeBridge\artifacts\packages
dotnet build C:\Projects\CodeBridge\artifacts\package-smoke-split\CodeBridge.PackageSmokeSplit.csproj --no-restore -p:RestorePackagesPath=C:\Projects\CodeBridge\artifacts\package-smoke-split\.packages
```

## Visual Studio VSIX Base

The VSIX shell lives in `src\CodeBridge.VisualStudio`. It currently provides:

- A Visual Studio package loaded asynchronously.
- A `Tools > CodeBridge Setup...` command.
- A `Tools > Install CodeBridge WinForms Package` command that adds `CodeBridge.Designer.WinForms` to the active C# WinForms project from the package feed embedded in the VSIX.
- A `Tools > Add CodeBridge Starter Form` command that installs the package, adds a ready-to-run ESP32 blink form, and updates the default `Program.cs` startup form when the project is still using the standard `Application.Run(new Form1());` template.
- A VSIX asset manifest at `Assets\toolbox-components.json` that describes package installation, Toolbox category, component types, design-time metadata, recommended properties, events, runtime APIs, and samples.
- A schema at `Assets\toolbox-components.schema.json` for validating the manifest shape.
- Embedded local NuGet packages under `Packages\*.nupkg` so a clean WinForms project can install the designer components without separately configuring a local source.

The setup command loads and validates `toolbox-components.json` at runtime, then opens a native setup dialog with the active project, package feed, public components, and quick actions. The quickest VS 2026 workflow is now: select the WinForms project, run `Tools > CodeBridge Setup...`, click `Add Starter Form`, rebuild the project, then run it and select the ESP32 COM port.

Build the VSIX:

```powershell
dotnet pack C:\Projects\CodeBridge\src\CodeBridge.Designer.WinForms\CodeBridge.Designer.WinForms.csproj -c Release -o C:\Projects\CodeBridge\artifacts\packages
dotnet build C:\Projects\CodeBridge\src\CodeBridge.VisualStudio\CodeBridge.VisualStudio.csproj -c Release
```

Output:

```text
C:\Projects\CodeBridge\src\CodeBridge.VisualStudio\bin\Release\net472\CodeBridge.VisualStudio.vsix
```

Install manually into the local Visual Studio instance:

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\VSIXInstaller.exe" C:\Projects\CodeBridge\src\CodeBridge.VisualStudio\bin\Release\net472\CodeBridge.VisualStudio.vsix
```

After installation:

1. Open or create a WinForms project.
2. Select the project in Solution Explorer or open `Form1.cs [Design]`.
3. Run `Tools > Add CodeBridge Starter Form`.
4. Rebuild the WinForms project.
5. Run the project. The starter form runs `Flows\Blink.cbflow` on the ESP32 it finds on USB (or on the simulator when no board is plugged in) and offers to upload the CodeBridge firmware if the board needs it. Open the `.cbflow`, change it and save: the running app picks the change up.

If Visual Studio still shows an empty CodeBridge tab, right-click the Toolbox and choose `Reset Toolbox`, then reopen the designer. The project reference is the important source of truth.

If the installer says prerequisites cannot be resolved, inspect the latest log:

```powershell
Get-ChildItem $env:TEMP -Filter "dd_VSIXInstaller_*.log" |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First 1 |
  Get-Content -Tail 80
```

For Visual Studio 2026, keep only `Microsoft.VisualStudio.Component.CoreEditor` as a VSIX prerequisite. `Microsoft.VisualStudio.Component.ManagedDesktop` is not a valid VSIX prerequisite in this environment.

Sign with a P12/PFX certificate:

```powershell
powershell -ExecutionPolicy Bypass -File C:\Projects\CodeBridge\tools\CodeBridge.VisualStudio.Signing\Sign-CodeBridgeVsix.ps1 -CertificatePath C:\path\to\certificate.p12 -VsixPath C:\Projects\CodeBridge\src\CodeBridge.VisualStudio\bin\Release-signed\net472\CodeBridge.VisualStudio.vsix
```

The signing script prompts for the P12 password securely, validates that the certificate has a private key and Code Signing EKU when present, converts `.p12` to a temporary `.pfx` for `VsixSignTool`, signs a copy named `CodeBridge.VisualStudio.signed.vsix`, and verifies the signature with `Microsoft.VSSDK.VsixSignTool`.

Install the signed output:

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\VSIXInstaller.exe" C:\Projects\CodeBridge\src\CodeBridge.VisualStudio\bin\Release-signed\net472\CodeBridge.VisualStudio.signed.vsix
```
