# The flow editor in a browser (proof of concept)

`codebridge-web` is the flow editor in a web page, served from your own computer. It runs where .NET 8 runs (Windows, Linux, macOS, a Raspberry Pi), so it works without Visual Studio.

```powershell
dotnet run --project src/CodeBridge.WebEditor -- --flow my-flow.cbflow --port simulator
```

It prints an address that ends with a key; open it in the browser:

```
CodeBridge web editor: http://127.0.0.1:5280/#3f9a...
```

| Option | |
|---|---|
| `--flow <file>` | The `.cbflow` to edit. A file that does not exist yet starts as an empty flow and is created on the first save. |
| `--port <target>` | The board a run uses: `simulator` (default), a serial port (`COM3`, `/dev/ttyUSB0`), or the IP address of a paired Wi-Fi board (the pairing token is read from `CODEBRIDGE_TOKEN`). |
| `--board <id>` | The board profile for a new flow (`esp32-devkit`, `esp32-s3-devkit`, ...). It can be changed in the page. |
| `--http <port>` | The port of the page. 5280 by default. |

## What it does

- **Toolbox, canvas and properties**: add a block by clicking it, drag blocks, drag from an output dot to an input dot to wire them, edit the properties (with the choices and the help of each block), Delete removes the selection, F fits the whole flow, the mouse wheel zooms.
- **Validation as you edit**: the same validator as the Visual Studio editor; problems are listed and the blocks involved are outlined (red for errors, amber for warnings). A flow with errors is never written to the file.
- **Run**: the flow runs on the board with the same loop as `FlowHost serve` (it connects again by itself if the board goes away), and every block shows its state and outputs live. The amber `SIMULATOR` chip is on whenever the virtual board is in use.
- **Every block** of the catalog, including the ones of `CodeBridge.IotBindings`.
- The file it saves is the same `.cbflow` the Visual Studio editor opens.

## Security

A web page that can drive a board and write a file must not be reachable by other pages:

- It listens on `127.0.0.1` only, never on the network.
- Every call to `/api` needs the random key printed at start-up (kept in the address after `#`, so it is not sent to the server as part of the page request). A page open in the same browser, or another program, does not know it.
- Requests whose `Host` is not `127.0.0.1` or `localhost` are refused, so a name that merely resolves to this computer (DNS rebinding) gets nowhere.
- The only file it can write is the one it was started with, replaced atomically.

## Not there yet

This is a proof of concept. Compared with the Visual Studio editor it has no undo, no copy and paste, no board view or serial monitor, no Wi-Fi setup, no mesh window, no animated block demos, no keyboard-only way to wire blocks, and it does not flash firmware. It is a single page with no build step, so it is easy to grow, but it needs a round of design work before it is offered to beginners.
