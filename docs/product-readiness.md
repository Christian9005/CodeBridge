# CodeBridge Product Readiness

## Current Native Path

CodeBridge is now usable as a Visual Studio-first WinForms extension:

- Install the signed VSIX.
- Open or create a WinForms project.
- Run `Tools > Add CodeBridge Starter Form`.
- Build and run the project.
- The starter form runs `Flows\Blink.cbflow` on the board it finds on USB (or on the simulator) and offers to upload the firmware if the board needs it. Edit the `.cbflow` and save: the running app picks the change up.

The VSIX embeds the local CodeBridge NuGet packages, installs `CodeBridge.Designer.WinForms` into the active project, adds a starter form, and only exposes the public Toolbox components.

## What Feels Product-Ready

- `CodeBridgeFlowControl` and `CodeBridgeBoardComponent` are discoverable in the WinForms Toolbox.
- The ESP32 package can run real serial flows.
- The visual flow editor supports presets, tracing, trigger mode, and loop mode.
- The VSIX command table is registered and visible under `Tools`.
- The package installation flow no longer needs a manually configured NuGet source.
- The starter form gives a first-day demo path for blink testing.
- The flow canvas now uses a restrained Visual Studio-style palette, compact nodes, subtle borders, and minimal execution badges.
- `Tools > CodeBridge Setup...` now opens a native setup dialog instead of a plain message box.

## Remaining Final-Product Gaps

- Promote the setup dialog into a dockable Visual Studio tool window when the workflow needs persistent status.
- Add a Visual Studio project template for `CodeBridge WinForms App` so new users can start from `Create a new project`, not only from `Tools`.
- Add item templates for `CodeBridge Flow Form` and `CodeBridge Flow File`.
- Add Toolbox icons and branded metadata so the components look first-party in the Toolbox.
- Add a visual connection status surface inside the Form control, including selected COM port, board state, and last runtime error.
- Persist flow documents as project files, not only inside the control serialization/runtime path.
- Add firmware/version compatibility checks between the desktop package and ESP32 bridge firmware.
- Add automated VSIX smoke tests that inspect command registration, embedded packages, and generated starter form compilation.
- Prepare public signing/publishing flow for Marketplace or private VSIX distribution.

## Recommended Next Iteration

The next native polish step should evolve the `CodeBridge Setup` dialog into a dockable tool window. It should centralize:

- Active project detection.
- Package installed or missing.
- Toolbox refresh guidance.
- ESP32 serial port detection.
- Firmware check.
- One-click starter form creation.
- Links to sample flows and docs.

That gives the extension a clear home inside Visual Studio and makes demos easier to record.

## Visual Polish Direction

Keep the designer closer to Visual Studio than to a web dashboard:

- Compact nodes with small status badges.
- Subtle borders instead of heavy cards.
- Microsoft blue for active/running state.
- Standard success/warning/error colors for runtime feedback.
- Grid and wires visible enough for precision, quiet enough for long sessions.
