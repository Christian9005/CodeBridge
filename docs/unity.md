# Drive a real robot arm from Unity (proof of concept)

Build a robot arm in a Unity scene, move it with sliders, an animation or inverse kinematics, and the real servos follow. The scene also shows where the real servos are, so you see the arm in Unity and on the desk side by side.

```
Unity scene ──WebSocket (localhost)──> codebridge-livelink ──USB / Wi-Fi──> ESP32 ──> servos
   ServoJoint per joint                 limits, speed, stop                   (or the simulator)
   feedback copy  <──── state 20×/s ────  joint positions, analog sensors
```

Two pieces:

- **`codebridge-livelink`** (`src/CodeBridge.LiveLink`): a small local server that owns the board. It is the safety layer: every joint stays inside its limits and turns no faster than its speed whatever Unity sends, the arm stops on request, and it **stops where it is when Unity goes away** (closed, crashed, paused in the debugger).
- **The Unity package** `com.codebridge.livelink` (`unity/com.codebridge.livelink`): `LiveLinkConnection` and `ServoJoint` components, no other dependency. Unity 2021.3 or later, desktop (Windows, macOS, Linux); not WebGL.

## Try it with no hardware

1. Start the link on the simulator:
   ```bash
   dotnet run --project src/CodeBridge.LiveLink -- --port simulator
   ```
   It prints `ws://127.0.0.1:5300/link` and a key.
2. Open the ready project `unity/LiveLinkDemo` in Unity Hub (*Add > Add project from disk*, Unity 2022.3). It opens `Assets/Scenes/RobotArm.unity`; press **Play**.

   Or in your own project: *Window > Package Manager > + > Add package from disk...*, pick `unity/com.codebridge.livelink/package.json`, import the **Robot arm** sample, add the `RobotArmDemo` component to an empty GameObject and press **Play**.

There is no key to copy on the same computer: the link writes its address and key to `%LOCALAPPDATA%\CodeBridge\livelink.txt` (your own profile), and a `LiveLinkConnection` whose key field is empty reads them from there. On another computer, paste the key the link prints.

Move the sliders: the solid arm is what Unity asks for; the orange see-through arm is where the servos are, as the link reports. Move a slider fast and the copy follows at the joint's speed (90 °/s in the sample); push the shoulder to the end and it stops at its limit. **Escape** stops the arm.

## With a real arm

A 3-joint arm with hobby servos (SG90 or MG996R): base, shoulder and elbow on GPIO 13, 14 and 27 of an ESP32 DevKit.

- **Power the servos from their own 5 V supply** (2 A or more for MG996R), with its ground joined to the board's ground. Never from the board's 3V3 or 5 V pins.
- Start with the arm free to move and the `maxSpeed` low (30 °/s), and keep a hand on the servo supply's switch: the stop key is a software stop.
- Start the link with the board's port: `dotnet run --project src/CodeBridge.LiveLink -- --port COM3`.

Set each `ServoJoint`: `pin`, the axis it turns around, `home` (the servo angle when the model's joint has no rotation), `min`/`max` (keep them inside what the mechanics allow), `maxSpeed`, and `invert` when the servo turns the other way from the model.

## About the feedback

A hobby servo **does not report its position**: the "real" arm in the scene is the angle the link has sent, moving at the joint's speed. That shows the limits, the speed and a stop, but not a servo that is blocked or slipping. For real feedback:

- **Potentiometers on the joints** (or a servo with its potentiometer wire brought out): list their analog pins in `LiveLinkConnection.feedbackPins`; the readings arrive in `Feedback` 10 times a second.
- **A current sensor** (INA219) on the servo supply: a jump in current means the arm hit something.
- **Smart servos** that report position, load and temperature (Feetech STS / SCS, Dynamixel): the natural next step, not supported yet.

## The protocol

JSON messages on `ws://127.0.0.1:<port>/link?k=<key>`, simple enough for any engine (Godot, Unreal, a Python script):

| From | Message |
|---|---|
| engine | `{"type":"configure","joints":[{"id":"base","pin":13,"min":0,"max":180,"home":90,"maxSpeed":90}],"feedbackPins":[34]}` |
| engine | `{"type":"target","joints":[{"id":"base","angle":120}]}` |
| engine | `{"type":"stop"}`, `{"type":"resume"}` |
| link | `{"type":"hello","board":"ESP32 DevKit","firmware":"0.9.1","simulated":false}` |
| link | `{"type":"state","stopped":false,"joints":[{"id":"base","target":120,"position":104.5}],"feedback":[{"pin":34,"value":2048}]}` (about 20 per second) |
| link | `{"type":"error","message":"Joint 'base': GPIO 34 cannot drive a servo on ESP32 DevKit."}` |

The link checks every pin against the board (a servo needs an output pin with PWM, feedback an analog pin) and refuses an arm that does not fit, with the reason.

## Security

A program that moves a robot arm must not be reachable by anything else:

- It listens on `127.0.0.1` only.
- Every connection needs the key printed at start-up (`--key` sets your own).
- It refuses any connection that carries an `Origin` header, which every browser sends on a WebSocket: no web page can move the arm.
- One engine at a time: a second connection is refused while the first is open.

## What has been tried

- The link: 21 automated tests, including a real WebSocket against the simulator (the servo moves and reports back, a bad pin is refused, the arm stops when the engine disconnects, one engine at a time, no key no connection).
- The Unity package in Unity 2022.3: its Play Mode tests (`Tests/Runtime`) pass against the link on the simulator **and on a real ESP32 DevKit**: a joint turned in the scene moves the servo and the copy follows, a rotation past the limit stops at the limit, and stop holds the servo while the scene keeps moving. Pressing Play right after Stop finds the link still closing the previous session; the component tries again for a few seconds (`connectTimeout`).
- On the real board the firmware accepted every servo command (attach and angle on GPIO 13); no servo or arm was attached yet.

## Limits and next steps

- Servo angles are whole degrees (the firmware's servo command); 50 updates per second, so a 6-joint arm on USB takes about 24 ms per update.
- Next: smart servos with real position feedback, the PCA9685 (16 servos on two wires) as a joint driver, and the SDK inside Unity itself once Unity's CoreCLR runtime lands (planned for Unity 6.8 and 7).
