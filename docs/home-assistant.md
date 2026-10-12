# Show a flow in Home Assistant

CodeBridge can show the inputs and outputs of a flow in [Home Assistant](https://www.home-assistant.io) through MQTT discovery:
the board appears as one device, with no YAML. You can do it from the flow editor (this page) or from code (`CodeBridge.HomeAssistant`).

## What you need once

Home Assistant talks to devices through an **MQTT broker** (Mosquitto) and the **MQTT integration**.

- **Home Assistant OS / Supervised**: *Settings → Add-ons → Add-on store → Mosquitto broker → Install → Start*. Then *Settings → Devices & services → MQTT*.
  Create a user for CodeBridge under *Settings → People → Users*.
- **Home Assistant in Docker (Container, CasaOS, ZimaOS...)**: there are no add-ons, so run Mosquitto as another container.
  The editor's Home Assistant window has a **Copy broker setup** button with a ready compose file and the commands for the user and password;
  the steps are also in [the sample](../samples/Integration.HomeAssistant/Program.cs).
  Then add the MQTT integration in Home Assistant (broker `127.0.0.1` when both containers share the host, port `1883`, the user you created).

## From the editor

1. Build a flow with **Digital Output**, **PWM Output**, **Analog Read** or **Digital Read** blocks and choose the board's port (or the simulator).
2. Press **Home Assistant** in the toolbar.
3. **Find** looks for Home Assistant on the network; or type the broker's address. Enter the user and password and press **Test connection**:
   it tells you whether the machine is reachable, whether a broker listens, and whether the password is accepted.
4. **Publish to Home Assistant**. The board keeps being published until you press **Stop**.

| Block | Appears in Home Assistant as |
|---|---|
| Digital Output, Digital Write, Blink LED | a switch (the board's built-in LED is named *Built-in LED*) |
| PWM Output | a dimmable light |
| Analog Read | a sensor in percent |
| Digital Read | a binary sensor |

Each pin appears once. The flow is read when you press Publish: after changing the flow, stop and publish again.
Publishing uses the board's port, so Run, Connect and Upload are unavailable until you stop it (a board can have one owner at a time).
The broker password is stored encrypted for your Windows account, like the Wi-Fi pairing tokens, and is never put on a command line.

## From code

```csharp
services.AddCodeBridge(o => o.Port = "COM3");
services.AddCodeBridgeHomeAssistant(
    o => { o.Broker = "192.168.1.10"; o.Username = "codebridge"; o.Password = "..."; },
    ha => ha.Switch("led", "Built-in LED", pin: 2)
            .AnalogSensor("light", "Light", pin: 34, unit: "%", scale: 100.0 / 4095));
```

`HomeAssistantFlow.EntitiesFor(flow, board)` builds the same entities the editor uses from a `.cbflow` document, and
`HomeAssistantBrokerCheck.RunAsync(options)` runs the connection test.

## When it does not work

| Message | Meaning |
|---|---|
| nothing listens for MQTT on port 1883 | the machine is reachable but has no broker running: install Mosquitto (above) |
| the broker rejected the user name or password | the user does not exist in the broker, or the password differs |
| did not answer | wrong address, or this PC and Home Assistant are on different networks |
| the device does not appear in Home Assistant | the MQTT integration is missing, or its discovery is turned off |
