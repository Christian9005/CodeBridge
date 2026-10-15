using CodeBridge.Hosting;
using CodeBridge.Transport.Mesh;
using CodeBridge.Transport.Simulation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodeBridge.Core.Tests.Mesh;

public class FleetTests
{
    private static DeviceFleet NewFleet(FleetOptions? options = null) => new(Options.Create(options ?? new FleetOptions()));

    private static FleetOptions WithGateway(SimulatedMeshNetwork network, TimeSpan? reconnect = null) => new()
    {
        ConfigureDevice = (definition, options) =>
        {
            if (reconnect is { } delay)
                options.ReconnectDelay = delay;

            // the gateway is the simulated mesh gateway instead of a COM port
            if (definition.Id == "hub")
                options.TransportFactory = _ => Task.FromResult(network.Gateway);
        }
    };

    [Fact]
    public async Task Several_simulated_devices_are_independent_boards()
    {
        await using var fleet = NewFleet();
        fleet.Add(new DeviceDefinition { Id = "bench", Kind = DeviceKind.Simulator });
        fleet.Add(new DeviceDefinition { Id = "shelf", Name = "Shelf light", Kind = DeviceKind.Simulator });

        await fleet.ConnectAllAsync();
        await fleet["bench"].Board.WriteDigitalAsync(2, true);

        Assert.True(fleet["bench"].Board.Simulator!.GetOutput(2));
        Assert.False(fleet["shelf"].Board.Simulator!.GetOutput(2));
        Assert.All(fleet.Devices, d => Assert.True(d.IsConnected));
        Assert.Equal("Shelf light", fleet["shelf"].Name);
    }

    [Fact]
    public void Ids_are_cleaned_and_must_be_unique_and_a_mesh_node_needs_its_gateway_and_mac()
    {
        var fleet = NewFleet();

        var device = fleet.Add(new DeviceDefinition { Id = " Garage Door! ", Kind = DeviceKind.Simulator });
        Assert.Equal("garage-door", device.Id);
        Assert.Same(device, fleet["GARAGE-DOOR"]);

        Assert.Throws<InvalidOperationException>(() => fleet.Add(new DeviceDefinition { Id = "garage-door", Kind = DeviceKind.Simulator }));
        Assert.Throws<ArgumentException>(() => fleet.Add(new DeviceDefinition { Id = "", Kind = DeviceKind.Simulator }));
        Assert.Throws<ArgumentException>(() => fleet.Add(new DeviceDefinition { Id = "n1", Kind = DeviceKind.MeshNode, GatewayId = "hub" }));
        Assert.Throws<ArgumentException>(() => fleet.Add(new DeviceDefinition { Id = "n2", Kind = DeviceKind.MeshNode, Mac = "E072A1F97B84" }));
        Assert.Throws<KeyNotFoundException>(() => fleet["nothing"]);
    }

    [Fact]
    public async Task A_mesh_node_is_reached_through_its_gateway_like_any_other_board()
    {
        var network = new SimulatedMeshNetwork("FCE8C07C23CC");
        var nodeBoard = network.AddNode("E072A1F97B84", "xiao", "ESP32-S3");
        await using var fleet = NewFleet(WithGateway(network));
        fleet.Add(new DeviceDefinition { Id = "hub", Kind = DeviceKind.Usb, Target = "COM3" });
        fleet.Add(new DeviceDefinition { Id = "xiao", Kind = DeviceKind.MeshNode, Board = BoardKind.Esp32S3, GatewayId = "hub", Mac = "e0:72:a1:f9:7b:84" });

        await fleet.ConnectAllAsync();
        await fleet["xiao"].Board.WriteDigitalAsync(5, true);
        nodeBoard.SetAnalogInput(4, 1000);

        Assert.True(fleet["xiao"].IsConnected);
        Assert.Equal("mesh://hub/E072A1F97B84", fleet["xiao"].Board.Target);
        Assert.True(nodeBoard.GetOutput(5));
        Assert.False(network.GatewayBoard.GetOutput(5));
        Assert.Equal(1000, await fleet["xiao"].Board.ReadAnalogAsync(4));
    }

    [Fact]
    public async Task One_device_that_is_down_never_hides_the_others()
    {
        await using var fleet = NewFleet(new FleetOptions { ConfigureDevice = (_, o) => o.AutoReconnect = false });
        fleet.Add(new DeviceDefinition { Id = "ok", Kind = DeviceKind.Simulator });
        fleet.Add(new DeviceDefinition { Id = "gone", Kind = DeviceKind.Usb, Target = "COM250" });

        await fleet.ConnectAllAsync(); // does not throw

        Assert.True(fleet["ok"].IsConnected);
        Assert.False(fleet["gone"].IsConnected);

        var results = await fleet.ForEachAsync((device, ct) => device.Board.ReadAnalogAsync(34, ct), connectedOnly: false);

        Assert.Equal(2, results.Count);
        Assert.True(results.Single(r => r.Device.Id == "ok").Succeeded);
        Assert.False(results.Single(r => r.Device.Id == "gone").Succeeded);
    }

    [Fact]
    public async Task A_node_that_was_off_becomes_available_when_it_returns()
    {
        var network = new SimulatedMeshNetwork();
        network.AddNode("AABBCC000002", "garage", "ESP32-C3");
        await using var fleet = NewFleet(WithGateway(network, TimeSpan.FromMilliseconds(50)));
        fleet.Add(new DeviceDefinition { Id = "hub", Kind = DeviceKind.Usb, Target = "COM3" });
        fleet.Add(new DeviceDefinition { Id = "garage", Kind = DeviceKind.MeshNode, Board = BoardKind.Esp32C3, GatewayId = "hub", Mac = "AABBCC000002" });
        network.SetOnline("AABBCC000002", false);

        await fleet.ConnectAllAsync();
        Assert.False(fleet["garage"].IsConnected);

        network.SetOnline("AABBCC000002", true);
        for (var i = 0; i < 100 && !fleet["garage"].IsConnected; i++)
            await Task.Delay(50);

        Assert.True(fleet["garage"].IsConnected);
    }

    [Fact]
    public async Task Discovery_offers_the_nodes_a_gateway_hears_and_leaves_out_the_ones_already_added()
    {
        var network = new SimulatedMeshNetwork("FCE8C07C23CC");
        network.AddNode("E072A1F97B84", "xiao", "ESP32-S3");
        network.AddNode("AABBCC000002", "garage", "ESP32-C3");
        await using var fleet = NewFleet(WithGateway(network));
        fleet.Add(new DeviceDefinition { Id = "hub", Kind = DeviceKind.Usb, Target = "COM3" });
        fleet.Add(new DeviceDefinition { Id = "xiao", Kind = DeviceKind.MeshNode, GatewayId = "hub", Mac = "E072A1F97B84" });
        await fleet.ConnectAllAsync();

        var found = await fleet.DiscoverAsync(TimeSpan.FromMilliseconds(100));

        var garage = Assert.Single(found.Where(f => f.Kind == DeviceKind.MeshNode));
        Assert.Equal("garage", garage.Name);
        var definition = garage.ToDefinition();
        Assert.Equal("garage", definition.Id);
        Assert.Equal(BoardKind.Esp32C3, definition.Board);
        Assert.Equal("hub", definition.GatewayId);
        Assert.Null(definition.Target);

        fleet.Add(definition);
        Assert.Equal(3, fleet.Devices.Count);
    }

    [Fact]
    public void The_fleet_is_remembered_between_runs_without_any_secret()
    {
        var path = Path.Combine(Path.GetTempPath(), "codebridge-fleet-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var first = NewFleet(new FleetOptions { StorePath = path });
            first.Add(new DeviceDefinition { Id = "kitchen", Name = "Kitchen", Kind = DeviceKind.Wifi, Target = "192.168.1.50", Board = BoardKind.Esp32S3 });
            first.Add(new DeviceDefinition { Id = "shed", Kind = DeviceKind.MeshNode, GatewayId = "kitchen", Mac = "E072A1F97B84" });

            var text = File.ReadAllText(path);
            Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"kind\": \"wifi\"", text);

            var second = NewFleet(new FleetOptions { StorePath = path });
            Assert.Equal(new[] { "kitchen", "shed" }, second.Devices.Select(d => d.Id).OrderBy(i => i).ToArray());
            Assert.Equal(DeviceKind.MeshNode, second["shed"].Definition.Kind);
            Assert.Equal("Kitchen", second["kitchen"].Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_damaged_store_file_does_not_stop_the_application()
    {
        var path = Path.Combine(Path.GetTempPath(), "codebridge-fleet-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, "{ this is not json");

            await using var fleet = NewFleet(new FleetOptions { StorePath = path });

            Assert.Empty(fleet.Devices);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Removing_a_device_forgets_it()
    {
        await using var fleet = NewFleet();
        fleet.Add(new DeviceDefinition { Id = "a", Kind = DeviceKind.Simulator });

        Assert.True(await fleet.RemoveAsync("a"));
        Assert.False(await fleet.RemoveAsync("a"));
        Assert.Empty(fleet.Devices);
    }

    [Fact]
    public async Task The_fleet_registers_with_dependency_injection_and_reads_its_devices_from_options()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodeBridgeFleet(o =>
        {
            o.ConnectOnStart = false;
            o.Devices.Add(new DeviceDefinition { Id = "bench", Kind = DeviceKind.Simulator });
        });

        await using var provider = services.BuildServiceProvider();
        var fleet = provider.GetRequiredService<DeviceFleet>();

        Assert.Equal("bench", Assert.Single(fleet.Devices).Id);
    }
}
