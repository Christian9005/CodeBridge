# Use CodeBridge from an AI assistant (MCP)

`CodeBridge.Mcp` is a [Model Context Protocol](https://modelcontextprotocol.io) server. It lets an assistant such as Claude learn a board,
design a flow, check it, test it on a virtual board and export it as C#, without guessing pin numbers.

The important part is the **board manifest**: for every board it states which pins exist, what each can do (digital, analog, PWM,
interrupt, I2C/SPI/UART roles), which ones are unsafe (reserved, input-only, boot-strap), the electrical limits and the rules that keep a design from
damaging the board or silently not working. The assistant reads it first, so it stops inventing pins.

## Install

```bash
dotnet tool install -g CodeBridge.Mcp        # once the package is published
```

From a checkout of this repository you can run it without installing: `dotnet run --project src/CodeBridge.Mcp`.

## Connect it to your assistant

**Claude Code**

```bash
claude mcp add codebridge -- codebridge-mcp
```

**Claude Desktop** (`claude_desktop_config.json`)

```json
{
  "mcpServers": {
    "codebridge": { "command": "codebridge-mcp" }
  }
}
```

Then ask, for example: *"Using CodeBridge, make the built-in LED of an ESP32 fade with the light level on pin 34, test it in the simulator and give me the C#."*

## What the assistant can do

| Tool | What for |
|---|---|
| `list_boards`, `get_board_manifest` | Learn the boards and their pins, safety and rules |
| `list_block_types` | The blocks available for a board, with ports, properties, defaults and valid pins |
| `validate_flow` | The same checks as the Visual Studio editor: wrong connections, bad pins, conflicts |
| `simulate_input` | Make a sensor or button read a value (a dark room, a pressed button) |
| `run_flow` | Run a flow and get every block's output plus the **pin activity** (proof of what was driven) |
| `get_pin_states`, `read_pin`, `set_pin` | Look at or drive single pins (reserved and input-only pins are refused) |
| `export_csharp` | Turn the flow into SDK code (class or console app) |
| `connect_board`, `list_ports`, `board_status` | Choose the board; find real ones (gated, see below) |

**Code mode** (C++ firmware with PlatformIO instead of flows):

| Tool | What for |
|---|---|
| `firmware_status` | Is PlatformIO installed, where the workspace is |
| `create_firmware_project`, `list_firmware_projects` | A project for any board from a template |
| `read_firmware_file`, `write_firmware_file` | Edit sources inside the project (no scripts, no `extra_scripts`) |
| `build_firmware` | Compile; returns every error and warning with file, line and column |
| `upload_firmware` | Flash a real board: needs `CODEBRIDGE_MCP_ALLOW_HARDWARE=1` and `confirmed=true` |

Projects live in one workspace folder (`CODEBRIDGE_MCP_WORKSPACE`, default `Documents\CodeBridge Projects`); nothing outside it can be read, written or built.
Building runs PlatformIO, which downloads toolchains and libraries the first time, like any PlatformIO build.

Resources: `codebridge://guide/flow-format` (how to write a `.cbflow`, with examples and patterns), `codebridge://guide/safety`,
and `codebridge://boards/{id}` (the manifest).

## Safety model

- **The simulator is the default.** Everything works without hardware and cannot damage anything. It reports exactly which pin was driven how.
- **Real hardware is off.** Connecting to a COM port or an IP address fails unless *you* start the server with `CODEBRIDGE_MCP_ALLOW_HARDWARE=1`:

  ```json
  { "mcpServers": { "codebridge": { "command": "codebridge-mcp", "env": { "CODEBRIDGE_MCP_ALLOW_HARDWARE": "1" } } } }
  ```

- **Every physical action needs your approval.** With hardware enabled, `set_pin` and `run_flow` still require the assistant to pass `confirmed=true`,
  which the instructions tell it to do only after asking you.
- Reserved and input-only pins are refused even on the simulator, so a design that works there will not break a real board for that reason.
- The Wi-Fi pairing token never passes through the assistant: pair boards in Visual Studio. For a Wi-Fi board the server reads
  `CODEBRIDGE_TOKEN` from its own environment.
- The server writes logs to standard error only; standard output carries the protocol.
