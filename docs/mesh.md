# Many boards, one PC: fleets and the ESP-NOW mesh

CodeBridge can drive any number of boards from one program, and reach boards that have no cable and no Wi-Fi of their own.

```
PC / .NET app ──USB or Wi-Fi──> gateway ESP32 ══ ESP-NOW ══> node (battery, no Wi-Fi)
                                   │                    └──> node
                                   └── keeps its normal Wi-Fi link
```

A **gateway** is any board the PC already talks to. A **node** is any board with the mesh on. The firmware is the same: every board can play both roles.
A node looks like a board of its own to the rest of CodeBridge: flows, the board view, sensors, the Blazor panels, Home Assistant and the MCP server work on it unchanged.

## ESP-NOW and Wi-Fi at the same time

ESP-NOW does not stop Wi-Fi. What matters is the radio channel: a station has one radio, so it can only use one channel. The rules CodeBridge follows:

- A gateway that is connected to your router uses the **router's channel**; ESP-NOW rides on the same channel, so Wi-Fi and the mesh both work.
- Modem sleep is switched off while the mesh is on (with it on, the radio sleeps between router beacons and misses ESP-NOW frames). That costs a little power on the gateway.
- A node **without Wi-Fi** hops across channels 1 to 13, sending a short beacon on each, until a gateway answers; then it stays on that channel. It searches again if it hears nothing for 40 seconds.
  You can also pin its channel with `MESHCH:<n>` (the editor offers 1, 6 or 11), which makes it instant and predictable.
- A node that also has Wi-Fi saved should be on the same router; a board whose saved network is unreachable keeps retrying the router, which moves its radio. Use nodes without a saved network.
- The antenna matters: the **XIAO ESP32-S3 needs its external antenna** (the small adhesive one that comes with it). Without it the board sees no Wi-Fi networks at all and cannot hear the gateway.

## Setting a mesh up (Visual Studio)

1. Connect the first board with USB, select its COM port in the editor toolbar and press **Mesh**.
2. **Set up this board**: give it a name; a random 128-bit key is created and kept encrypted for your Windows account.
3. Plug in each of the other boards, select its port, press **Mesh** and **Set up this board**: they get the same key.
4. Select the gateway's port (USB or the board's Wi-Fi address), press **Mesh** and **Scan**. Pick a board and **Use this board**: the Port box shows `mesh://COM3/E072A1F97B84`.
5. Run, Connect, the board view and Home Assistant work on it. The gateway does not need to be that same board: any board with the mesh on and a link to the PC works.

`mesh://simulator/...` is a virtual gateway with three virtual nodes, to build and test flows for a mesh without boards.

## From code

```csharp
services.AddCodeBridgeFleet(o =>
{
    o.StorePath = "fleet.json";                       // remembered between runs, no secrets in it
});

// later
var fleet = provider.GetRequiredService<DeviceFleet>();
fleet.Add(new DeviceDefinition { Id = "hub", Kind = DeviceKind.Wifi, Target = "192.168.1.50", Board = BoardKind.Esp32 });
fleet.Add(new DeviceDefinition { Id = "garage", Kind = DeviceKind.MeshNode, GatewayId = "hub", Mac = "E072A1F97B84", Board = BoardKind.Esp32S3 });
await fleet.ConnectAllAsync();

await fleet["garage"].Board.WriteDigitalAsync(2, true);
var readings = await fleet.ForEachAsync((device, ct) => device.Board.ReadAnalogAsync(34, ct));   // one broken device never hides the others

foreach (var found in await fleet.DiscoverAsync())       // Wi-Fi boards (mDNS) and the nodes every connected gateway hears
    fleet.Add(found.ToDefinition());
```

`MeshGateway` and `MeshNodeTransport` (in `CodeBridge.Transport.Mesh`) are the lower layer; `SimulatedMeshNetwork` is a virtual mesh for tests.
Each device is a `BoardService`, with its own state, reconnection and `Changed` event. A node that goes offline becomes available again by itself when it returns.

## Security

- Every frame carries an 8-byte HMAC-SHA256 tag made with the shared key and bound to the sender and receiver addresses. Frames made without the key are dropped.
- A command must echo a **single-use challenge** that the node handed out in its previous answer, so a captured command cannot be replayed.
- The key is written **over USB only** (`MESHKEY`), like the Wi-Fi token. It is never accepted over the air, Wi-Fi or the mesh.
- These commands are **never relayed**: `AUTH`, `WTOK`, `WCFG`, `WCFGX`, `WSCAN`, `OTAB`, `OTAS`, `MQC`/`MQP`/`MQS`/`MQU`/`MQR`/`MQD` and every `MESH*` / `MSCAN` / `MRLY`. Someone who has the key can drive a node's pins, but cannot change its Wi-Fi, token or firmware through the mesh.
- Frames are authenticated, not encrypted: a neighbour can see which pins you switch. Do not send secrets through a flow over the mesh.

## Limits

- One radio frame carries up to 250 bytes: commands and answers longer than about 200 characters are refused.
- A gateway remembers the 8 most recently heard boards; scans list the ones heard in the last 90 seconds.
- One hop only: a node talks to a gateway directly, not through another node.
- A relayed command takes a few milliseconds to a few tens; a node that is off answers with a timeout after 1.5 seconds.
- Firmware 0.9.1 or later on every board of the mesh.

## Commands (firmware 0.9.1)

| Command | What it does |
|---|---|
| `MESH` | `OK:on,MAC,channel,locked,hasKey,name,received,badSignatures,delivered,lost` |
| `MESHKEY:<32 hex>` | Sets the shared key (USB only). `MESHKEY:` with nothing clears it and switches the mesh off. |
| `MESHNAME:<name>` | Names the board (USB only). |
| `MESHCH:<0-13>` | Pins the channel of a board without Wi-Fi; 0 goes back to searching (USB only). |
| `MESHON` / `MESHOFF` | Switches the mesh on or off (USB only; the setting is remembered). |
| `MSCAN` | Asks everything in range to introduce itself: `OK:MAC,board,fw,name,ageSeconds;...` |
| `MRLY:<MAC>:<command>` | Runs `<command>` on that node: `RLY:<its answer>`, or `ERR:MESH_TIMEOUT`, `MESH_DENIED`, `MESH_NOKEY`, `MESH_OFF`. |

## When it does not work

| Symptom | Likely cause |
|---|---|
| Scan finds nothing | The board is not set up (same key), is off, or is out of range. Check `MESH` on it: `hasKey` must be 1. |
| The gateway lists the board but commands time out | The board cannot hear the gateway: no antenna (XIAO ESP32-S3), too far, or on another channel. Pin both to the same channel with `MESHCH`. |
| `badSignatures` grows | Another mesh nearby with a different key (harmless) or one board has an old key. |
| `MESH` says channel changes all the time | The board has no Wi-Fi and has not found a gateway yet. |
