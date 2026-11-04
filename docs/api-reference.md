# CodeBridge API Reference

CodeBridge's API is designed to be fluent, asynchronous, and intuitive for .NET developers. This document covers the core abstractions available in the SDK.

## `CodeBridgeBuilder`

The entry point for connecting to a board. It uses a fluent interface to configure the transport and target board.

```csharp
IBoard board = await CodeBridgeBuilder
    .Connect()
    .Serial("COM3", 115200) // or .WiFi("192.168.1.100", 8080, "<pairing token>")
    .ToESP32()
    .BuildAsync();
```

## Wi-Fi pairing: `Esp32WifiProvisioner` (CodeBridge.Transport)

Firmware 0.9+ keeps its network port closed until a client authenticates with a **pairing token**. The token is stored on the
board over USB, so nobody on your network can control it unless they have it. Pair a board once, over its USB serial connection:

```csharp
var serial = new SerialTransport("COM3");
await serial.ConnectAsync();

var provisioner = new Esp32WifiProvisioner(serial);
foreach (var network in await provisioner.ScanAsync())              // strongest first
    Console.WriteLine($"{network.Ssid}  {network.Rssi} dBm  {(network.Secured ? "secured" : "open")}");

var result = await provisioner.ProvisionAsync("MyNetwork", "my password"); // random 128-bit token
Console.WriteLine($"{result.IpAddress}:{result.Port}  token={result.AccessToken}");   // keep the token secret
```

Then connect without the cable: `.WiFi(result.IpAddress, result.Port, result.AccessToken)`. `GetStatusAsync()` reads the board's current
Wi-Fi state and `ClearTokenAsync()` closes network access again. In Visual Studio the **Wi-Fi** button of the flow editor does all
of this and stores the token encrypted for your Windows account.

## `IBoard`

The root object representing a connected microcontroller.

### Properties
- `bool IsConnected`: Returns true if the board is successfully communicating.
- `IGpioController Gpio`: Access GPIO functions (digital/analog pins).
- `II2cController I2C`: Access I2C bus functions.
- `ISpiController? SPI`: Access SPI bus functions (if supported by board).

### Methods
- `Task<BoardInfo> GetInfoAsync()`: Fetches hardware info like chip model, CPU frequency, and SDK version.
- `ValueTask DisposeAsync()`: Closes the connection and cleans up resources.

---

## `IGpioController`

Handles general-purpose input/output (digital and analog pins).

### Methods
- `Task SetPinModeAsync(int pin, PinMode mode)`: Configures a pin. `PinMode` can be `Input`, `Output`, `Analog`, `PullUp`, or `PullDown`.
- `Task DigitalWriteAsync(int pin, PinValue value)`: Sets a digital pin `High` or `Low`.
- `Task<PinValue> DigitalReadAsync(int pin)`: Reads a digital pin.
- `Task<int> AnalogReadAsync(int pin)`: Reads an analog value (e.g., 0-4095 on ESP32).
- `Task PwmWriteAsync(int pin, int dutyCycle, int frequency)`: Generates a PWM signal on the pin.

---

## `II2cController`

Handles the I2C bus for communicating with sensors and displays.

### Methods
- `Task<IReadOnlyList<byte>> ScanAsync()`: Scans the I2C bus and returns a list of addresses (0-127) that acknowledge.
- `Task WriteAsync(byte address, byte[] data)`: Writes an array of bytes to the specified I2C address.
- `Task<byte[]> ReadAsync(byte address, int length)`: Reads a specific number of bytes from the I2C address.
- `Task WriteReadAsync(byte address, byte[] writeData, byte[] readBuffer)`: Performs a write followed immediately by a read (common for reading registers).

---

## `CodeBridge.Designer.WinForms`

If you are building WinForms applications, you can use these UI components:

### `CodeBridgeBoardComponent`
A non-visual component that manages the board connection lifecycle (auto-detection, connecting, disconnecting) and can upload firmware.

### `CodeBridgeFlowControl`
A UI control that renders a node-based visual designer. It allows users to drag and drop logic and hardware blocks to create workflows that execute on the connected `IBoard`.

## Error Handling

CodeBridge uses specialized exceptions to help you diagnose hardware issues:

- `CodeBridgeException`: Base class for all CodeBridge errors.
- `DeviceNotRespondingException`: Thrown when the board fails to respond to a command (e.g., disconnected or crashed).
- `ProtocolMismatchException`: Thrown if the C# SDK version does not match the C++ firmware version flashed on the board.
- `PinConfigurationException`: Thrown if you attempt an invalid operation on a pin (e.g., analog read on a digital-only pin).

## `FlowCSharpGenerator` (CodeBridge.Flow)

Turns a visual flow into C# that uses the SDK, the same code the *Export C#* button of the Visual Studio editor produces.

```csharp
using CodeBridge.Flow.CodeGeneration;
using CodeBridge.Flow.Serialization;

var document = FlowDocumentJson.Deserialize(File.ReadAllText("Blink.cbflow"));
FlowCSharpResult result = FlowCSharpGenerator.Generate(document, new FlowCSharpOptions
{
    ClassName = "BlinkFlow",
    Namespace = "MyApp",
    Mode = FlowCSharpMode.Class,   // or ConsoleApp for a complete Program.cs
    Loop = false
});

File.WriteAllText("BlinkFlow.cs", result.Code);
foreach (var warning in result.Warnings) Console.WriteLine(warning);   // blocks that could not be exported
```

The generated class exposes `Task RunAsync(IBoard board, CancellationToken ct)`. A flow with validation errors throws `InvalidOperationException` listing them.

## `FlowSupervisor` (CodeBridge.Flow)

Keeps a flow running by itself: it connects, runs the flow again and again, rides out a failed pass, and when the board goes away it releases it and
connects again with a growing pause. `FlowHost serve` is this class behind a command line. Details in [A flow that runs by itself](headless.md).

```csharp
var supervisor = new FlowSupervisor(document, BuiltInBlockCatalog.Create(profile),
    connect: async ct => { var board = new ESP32Board(new SerialTransport("COM3"), profile.MaxPin); await board.ConnectAsync(ct); return board; },
    release: board => { board.Dispose(); return Task.CompletedTask; },
    options: new FlowSupervisorOptions { Interval = TimeSpan.FromSeconds(5) },
    onEvent: e => Console.WriteLine($"{e.Kind}: {e.Message}"));

await supervisor.RunAsync(stoppingToken);   // returns when cancelled; never throws because of the board
```

`FlowSupervisorOptions`: `Interval`, `InitialBackoff`, `MaxBackoff`, `FailuresBeforeReconnect`, `NodeEvents` (each block as it runs) and `TraceDelay`.

## `DeviceFleet` (CodeBridge.Hosting)

Any number of named boards behind one object: USB, Wi-Fi, or nodes of an ESP-NOW mesh reached through a gateway. `services.AddCodeBridgeFleet(...)`
registers it; `fleet["garage"].Board` is that device's `BoardService` (state, reconnection, every board call); `ForEachAsync` runs the same work on every device, and `DiscoverAsync` finds Wi-Fi boards and the
nodes every gateway hears. See [Many boards: fleets and the ESP-NOW mesh](mesh.md).

## `BridgeI2cDevice` and `IotBindingBlocks` (CodeBridge.IotBindings)

`BridgeI2cDevice` is a `System.Device.I2c.I2cDevice` whose bus is a CodeBridge board, so the drivers of the .NET IoT bindings run on it:

```csharp
using var sensor = new Iot.Device.Sht3x.Sht3x(new BridgeI2cDevice(board.I2C, 0x44), Iot.Device.Sht3x.Resolution.High);
Console.WriteLine(sensor.Temperature.DegreesCelsius);
```

`IotBindingBlocks.Register()` adds their flow blocks (call it once at start-up). To add blocks from your own package, describe each one as a
`ModuleBlock` and call `ModuleBlocks.Register(block)`. To test a driver without hardware, give the simulator a chip:
`simulatedTransport.AddI2cDevice(0x44, myChip)` with any `ISimulatedI2cDevice`. See [.NET IoT drivers on a board](iot-bindings.md).
