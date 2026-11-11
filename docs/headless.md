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

## Checked for a long time

`tests/serve_soak.ps1` starts the `FlowHost` the extension ships in `serve` mode and measures it every few seconds: private memory, handles, threads, CPU, the size of the log, the passes, failed passes and disconnections. At the end it fits a line to the memory after a warm-up and fails if the process died, the passes stopped, the memory or the handles grew, the log went past its limit or Ctrl+C did not stop it.

```powershell
pwsh tests/serve_soak.ps1 -Flow tests/golden/cbflow/released/0.8--SmartGreenhouse.cbflow -Port simulator -Minutes 30 -IntervalMs 50
pwsh tests/serve_soak.ps1 -Flow tests/golden/cbflow/released/0.5.4--Example3_NightLight.cbflow -Port COM3 -Minutes 30 -IntervalMs 1000
```

What it found on 10 November 2026 (FlowHost of 0.8.0):

| Run | Passes | Memory | Handles and threads | Ctrl+C |
|---|---|---|---|---|
| Virtual board, greenhouse flow, every 50 ms, 30 min | 28,981, none failed | 21-25 MB, trend 0 MB/hour | 262-268 and 8-12, steady | stopped in 1.5 s |
| ESP32 DevKit, night light flow, every 1 s, 30 min | 1,741, none failed | settles at 22 MB after the first 15 minutes | 284-314 (a slow rise in this run) | stopped in 1.6 s |
| ESP32 DevKit, night light flow, every 100 ms, 15 min | 8,221, none failed | 21-23 MB, flat | 286-299, goes up and down | stopped in 1.7 s |

The slow rise of the handles in the second run looked like a leak, so the third run asks the board ten times more: with ten times the commands the handles did not grow in proportion (they moved up and down within 13), so it was start-up noise. What these runs do not cover is a board that goes away: the loop that reconnects is tested with a board that fails, but pulling the cable of a real one during a run has not been done yet.

## Limits

- The flow still runs on the PC: the board does nothing on its own when the PC is off. Running a flow on the board without a PC is a different feature (compiling it to firmware) that CodeBridge does not have yet.
- Over USB the board restarts when the connection closes, so its outputs return to their default state: a relay switched on by a flow is off while `serve` is reconnecting.
