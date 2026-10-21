# A flow that runs by itself

A flow normally runs while the editor is open. `serve` keeps it running with nobody at the keyboard: on a PC that stays on, in a Windows service or in a container.

```powershell
CodeBridge.FlowHost serve --flow C:\flows\greenhouse.cbflow --port COM3 --interval 5000 --log C:\flows\greenhouse.log
```

`serve` connects to the board, runs the flow again and again (every `--interval` milliseconds, 1000 by default) and **connects again by itself** when the board goes away: cable pulled, Wi-Fi lost, power cut, PC asleep.

| What happens | What `serve` does |
|---|---|
| A pass fails (a sensor did not answer, a block raised) | Logs it and runs the next pass. |
| Three passes in a row fail (`--failures`), or the board says it is gone | Releases the connection and connects again. |
| The board is not there | Waits 2 s, then 4 s, 8 s... up to 30 s (`--retry`, `--max-retry`), and keeps trying. A good connection starts the waits over. |
| Ctrl+C, or the service is stopped | Stops at once, even in the middle of a wait. |

`--port` takes what the editor takes: a COM port, the address of a paired Wi-Fi board, `mesh://gateway/MAC`, or `simulator`.

## What it writes

One JSON object per line on the console, and the same lines in `--log` (the file is kept under 5 MB: the previous one becomes `.old`). A pass is reported once a minute or so; add `--verbose` to see every one. Connection changes and errors are always reported:

```json
{"type":"supervisor","kind":"disconnected","message":"The board stopped answering (3 failed pass(es) in a row): ...","attempt":0}
{"type":"supervisor","kind":"waiting","message":"Trying again in 2 s.","waitSeconds":2}
{"type":"supervisor","kind":"connected","message":"Connected to ESP32.","attempt":1}
```

## As a Windows service

A service starts in `C:\Windows\System32` and has no console: use absolute paths and `--log`.

```powershell
sc.exe create CodeBridgeFlow binPath= "\"C:\Program Files\CodeBridge\CodeBridge.FlowHost.exe\" serve --service --flow C:\flows\greenhouse.cbflow --port COM3 --interval 5000 --log C:\flows\greenhouse.log" start= auto
sc.exe failure CodeBridgeFlow reset= 86400 actions= restart/5000/restart/5000/restart/60000
sc.exe start CodeBridgeFlow
```

`--name` changes the service name. The flow is validated first: a flow with errors does not start, and the reason is in the log.

## From code

`FlowSupervisor` (in `CodeBridge.Flow`) is the same loop for your own host: give it the flow, a function that connects and one that releases, and an event handler.

```csharp
var supervisor = new FlowSupervisor(document, BuiltInBlockCatalog.Create(profile),
    ct => ConnectMyBoardAsync(ct), board => board.DisposeAsync().AsTask(),
    new FlowSupervisorOptions { Interval = TimeSpan.FromSeconds(5) },
    e => logger.LogInformation("{Kind}: {Message}", e.Kind, e.Message));

await supervisor.RunAsync(stoppingToken);   // never throws because of the board
```

## Limits

- The flow still runs on the PC: the board does nothing on its own when the PC is off. Running a flow on the board without a PC is a different feature (compiling it to firmware) that CodeBridge does not have yet.
- Over USB the board restarts when the connection closes, so its outputs return to their default state: a relay switched on by a flow is off while `serve` is reconnecting.
