// ═══════════════════════════════════════════════════════════════
//  CodeBridge sample: Home Assistant
//  ─────────────────────────────────
//  Shows your board in Home Assistant without writing any YAML: the switch, the dimmable light, the light sensor and the
//  button below appear by themselves (MQTT discovery) under one device.
//
//  Before the first run, in Home Assistant:
//    1. Install and start the "Mosquitto broker" add-on and the MQTT integration.
//       (Home Assistant in Docker has no add-ons: run an eclipse-mosquitto container instead, see docs/home-assistant.md.)
//    2. Create a user for CodeBridge (Settings -> People -> Users) and note its name and password.
//
//  Then:
//    dotnet run -- --user codebridge --password "<password>"              (finds Home Assistant on your network)
//    dotnet run -- --broker 192.168.1.10 --user codebridge --password "<password>"
//
//  The board comes from the CodeBridge section of appsettings.json: the first USB board, or the built-in simulator
//  when none is plugged in, so you can try everything without hardware.
// ═══════════════════════════════════════════════════════════════

using CodeBridge.HomeAssistant;
using CodeBridge.Hosting;
using Microsoft.Extensions.Hosting;

string? Arg(string name) => args.SkipWhile(a => a != "--" + name).Skip(1).FirstOrDefault();

var broker = Arg("broker");
if (broker is null)
{
    Console.WriteLine("Looking for Home Assistant on the network...");
    var found = await HomeAssistantFinder.FindAsync(TimeSpan.FromSeconds(4));
    if (found.Count == 0)
    {
        Console.WriteLine("Home Assistant was not found. Pass its address with --broker <ip>.");
        return 1;
    }

    foreach (var instance in found)
        Console.WriteLine($"  found '{instance.Name}' at {instance.BrokerHost}:{instance.Port} (version {instance.Version ?? "?"})");

    broker = found[0].BrokerHost;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddCodeBridge(builder.Configuration);
builder.Services.AddCodeBridgeHomeAssistant(
    options =>
    {
        options.Broker = broker;
        options.Username = Arg("user");
        options.Password = Arg("password");
        options.DeviceId = "codebridge-demo";
        options.DeviceName = "CodeBridge demo board";
    },
    ha => ha
        .Switch("led", "Built-in LED", pin: 2)
        .Light("lamp", "Lamp", pin: 4)
        .AnalogSensor("light", "Light level", pin: 34, unit: "%", scale: 100.0 / 4095, intervalSeconds: 5, deadband: 1, decimals: 0)
        .BinarySensor("button", "Button", pin: 12, deviceClass: "power", invert: true));

Console.WriteLine($"Publishing to the MQTT broker at {broker}. Press Ctrl+C to stop.");
await builder.Build().RunAsync();
return 0;
