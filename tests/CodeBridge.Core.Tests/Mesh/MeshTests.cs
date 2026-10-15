using CodeBridge.ESP32;
using CodeBridge.Transport.Mesh;
using CodeBridge.Transport.Simulation;

namespace CodeBridge.Core.Tests.Mesh;

public class MeshProtocolTests
{
    [Theory]
    [InlineData("e0:72:a1:f9:7b:84", "E072A1F97B84")]
    [InlineData("E0-72-A1-F9-7B-84", "E072A1F97B84")]
    [InlineData("e072a1f97b84", "E072A1F97B84")]
    public void A_mac_address_is_normalised(string input, string expected) =>
        Assert.Equal(expected, MeshProtocol.NormalizeMac(input));

    [Theory]
    [InlineData("")]
    [InlineData("e0:72:a1")]
    [InlineData("not a mac")]
    public void Something_that_is_not_a_mac_is_refused(string input) =>
        Assert.Throws<FormatException>(() => MeshProtocol.NormalizeMac(input));

    [Fact]
    public void A_generated_key_is_valid_and_different_every_time()
    {
        var a = MeshProtocol.GenerateKey();
        var b = MeshProtocol.GenerateKey();

        Assert.True(MeshProtocol.IsValidKey(a));
        Assert.NotEqual(a, b);
        Assert.False(MeshProtocol.IsValidKey("short"));
        Assert.False(MeshProtocol.IsValidKey(new string('z', 32)));
    }

    [Fact]
    public void A_scan_answer_becomes_a_list_of_nodes()
    {
        var nodes = MeshProtocol.ParseScan("OK:E072A1F97B84,ESP32-S3,0.9.1,xiao,4;FCE8C07C23CC,ESP32-D0WD-V3,0.9.1,kitchen,0");

        Assert.Equal(2, nodes.Count);
        Assert.Equal("E072A1F97B84", nodes[0].Mac);
        Assert.Equal("xiao", nodes[0].Name);
        Assert.Equal(TimeSpan.FromSeconds(4), nodes[0].Age);
        Assert.Equal("kitchen", nodes[1].DisplayName);
        Assert.Empty(MeshProtocol.ParseScan("OK"));
    }

    [Fact]
    public void The_status_answer_is_read_with_and_without_the_diagnostic_counters()
    {
        var full = MeshProtocol.ParseStatus("OK:on,FCE8C07C23CC,2,1,1,gateway,5,0,35,4");
        Assert.True(full.Enabled);
        Assert.Equal("FCE8C07C23CC", full.Mac);
        Assert.Equal(2, full.Channel);
        Assert.True(full.Locked);
        Assert.True(full.HasKey);
        Assert.Equal("gateway", full.Name);
        Assert.Equal(4, full.Lost);

        var old = MeshProtocol.ParseStatus("OK:off,E072A1F97B84,0,0,0,node-7b84");
        Assert.False(old.Enabled);
        Assert.Equal(0, old.Received);
    }

    [Theory]
    [InlineData("ERR:MESH_TIMEOUT", typeof(TimeoutException))]
    [InlineData("ERR:MESH_NOKEY", typeof(MeshException))]
    [InlineData("ERR:MESH_DENIED", typeof(MeshException))]
    [InlineData("ERR:SOMETHING_NEW", typeof(MeshException))]
    [InlineData("garbage", typeof(MeshException))]
    public void A_gateway_failure_becomes_a_typed_exception(string reply, Type expected) =>
        Assert.Throws(expected, () => MeshProtocol.UnwrapRelay(reply));

    [Fact]
    public void A_relayed_answer_is_the_nodes_own_line()
    {
        Assert.Equal("OK:PONG", MeshProtocol.UnwrapRelay("RLY:OK:PONG"));
        Assert.Equal("ERR:Invalid pin", MeshProtocol.UnwrapRelay("RLY:ERR:Invalid pin")); // the node's error is data, not an exception
    }

    [Fact]
    public void The_commands_that_set_a_board_up_are_built_in_the_right_order()
    {
        var key = MeshProtocol.GenerateKey();

        var commands = MeshProtocol.ProvisioningCommands(key, "garage", channel: 6);

        Assert.Equal(new[] { "MESHNAME:garage", $"MESHKEY:{key}", "MESHCH:6", "MESHON" }, commands);
        Assert.Throws<ArgumentException>(() => MeshProtocol.ProvisioningCommands("nope"));
        Assert.Throws<ArgumentException>(() => MeshProtocol.ProvisioningCommands(key, "bad,name"));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshProtocol.ProvisioningCommands(key, null, 20));
    }

    [Fact]
    public void A_node_address_round_trips()
    {
        var address = MeshProtocol.BuildAddress("COM3", "e0:72:a1:f9:7b:84");

        Assert.Equal("mesh://COM3/E072A1F97B84", address);
        Assert.True(MeshProtocol.TryParseAddress(address, out var gateway, out var mac));
        Assert.Equal("COM3", gateway);
        Assert.Equal("E072A1F97B84", mac);

        Assert.True(MeshProtocol.TryParseAddress("mesh://192.168.1.50/E072A1F97B84", out var ip, out _));
        Assert.Equal("192.168.1.50", ip);
        Assert.False(MeshProtocol.TryParseAddress("COM3", out _, out _));
        Assert.False(MeshProtocol.TryParseAddress("mesh://COM3/xyz", out _, out _));
    }

    [Fact]
    public void Nothing_that_could_leave_the_mesh_is_ever_relayed()
    {
        foreach (var command in new[] { "WTOK", "wcfg", "WCFGX", "OTAB", "MQC", "MESHKEY", "MRLY" })
            Assert.Contains(command, MeshProtocol.NeverRelayed);

        Assert.DoesNotContain("DW", MeshProtocol.NeverRelayed);
    }
}

public class SimulatedMeshTests
{
    private static (SimulatedMeshNetwork Network, MeshGateway Gateway) Setup()
    {
        var network = new SimulatedMeshNetwork("FCE8C07C23CC");
        network.AddNode("E072A1F97B84", "xiao", "ESP32-S3");
        network.AddNode("AABBCC000002", "garage", "ESP32-C3");
        network.Gateway.ConnectAsync().GetAwaiter().GetResult();
        return (network, new MeshGateway(network.Gateway));
    }

    [Fact]
    public async Task The_gateway_reports_its_status_and_lists_the_nodes_in_range()
    {
        var (_, gateway) = Setup();

        var status = await gateway.GetStatusAsync();
        var nodes = await gateway.ScanAsync();

        Assert.True(status.Enabled);
        Assert.True(status.HasKey);
        Assert.Equal(new[] { "garage", "xiao" }, nodes.Select(n => n.Name).OrderBy(n => n).ToArray());
        Assert.Contains(nodes, n => n.Mac == "E072A1F97B84" && n.Board == "ESP32-S3");
    }

    [Fact]
    public async Task A_command_reaches_only_the_node_it_is_addressed_to()
    {
        var (network, gateway) = Setup();

        var answer = await gateway.RelayAsync("E072A1F97B84", "PM:5:1");
        await gateway.RelayAsync("E072A1F97B84", "DW:5:1");

        Assert.StartsWith("OK", answer);
        Assert.True(network.Node("E072A1F97B84")!.GetOutput(5));
        Assert.False(network.Node("AABBCC000002")!.GetOutput(5));
        Assert.False(network.GatewayBoard.GetOutput(5));
    }

    [Fact]
    public async Task A_node_that_is_off_times_out_instead_of_hanging()
    {
        var (network, gateway) = Setup();
        network.SetOnline("E072A1F97B84", false);

        await Assert.ThrowsAsync<TimeoutException>(() => gateway.RelayAsync("E072A1F97B84", "PING"));

        var nodes = await gateway.ScanAsync();
        Assert.DoesNotContain(nodes, n => n.Mac == "E072A1F97B84");
    }

    [Theory]
    [InlineData("WTOK:abcdefgh")]
    [InlineData("WCFG:ssid:pass")]
    [InlineData("OTAB:http://example.com/x.bin")]
    [InlineData("MESHKEY:00000000000000000000000000000000")]
    public async Task Commands_that_would_reach_beyond_the_mesh_are_refused(string command)
    {
        var (_, gateway) = Setup();

        var ex = await Assert.ThrowsAsync<MeshException>(() => gateway.RelayAsync("E072A1F97B84", command));

        Assert.Equal("MESH_DENIED", ex.Code);
    }

    [Fact]
    public async Task A_gateway_without_a_key_refuses_to_scan_and_to_relay()
    {
        var network = new SimulatedMeshNetwork(provisioned: false);
        await network.Gateway.ConnectAsync();
        var gateway = new MeshGateway(network.Gateway);

        var scan = await Assert.ThrowsAsync<MeshException>(() => gateway.ScanAsync());
        Assert.Equal("MESH_NOKEY", scan.Code);

        // provisioning over "USB" switches it on
        foreach (var command in MeshProtocol.ProvisioningCommands(MeshProtocol.GenerateKey(), "gw"))
            Assert.StartsWith("OK", await network.Gateway.SendCommandAsync(command));

        Assert.Empty(await gateway.ScanAsync());
    }

    [Fact]
    public async Task A_whole_board_runs_on_a_node_exactly_like_on_a_cable()
    {
        var (network, gateway) = Setup();
        var node = network.Node("E072A1F97B84")!;
        node.SetAnalogInput(34, 2048);

        using var board = new ESP32Board(gateway.CreateNode("E072A1F97B84"), maxGpio: 48);
        await board.ConnectAsync();
        await board.Gpio.SetPinModeAsync(5, CodeBridge.Core.Enums.PinMode.Output);
        await board.Gpio.DigitalWriteAsync(5, CodeBridge.Core.Enums.PinValue.High);
        var analog = await board.Gpio.AnalogReadAsync(34);

        Assert.True(node.GetOutput(5));
        Assert.Equal(2048, analog);
        Assert.NotNull(board.FirmwareVersion);
    }

    [Fact]
    public async Task Connecting_to_a_node_that_is_off_fails_with_a_timeout_the_board_service_can_retry()
    {
        var (network, gateway) = Setup();
        network.SetOnline("AABBCC000002", false);
        using var transport = gateway.CreateNode("AABBCC000002");

        await Assert.ThrowsAsync<TimeoutException>(() => transport.ConnectAsync());
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task A_node_that_drops_after_connecting_marks_the_link_lost()
    {
        var (network, gateway) = Setup();
        using var transport = gateway.CreateNode("E072A1F97B84");
        await transport.ConnectAsync();
        Assert.True(transport.IsConnected);

        network.SetOnline("E072A1F97B84", false);
        await Assert.ThrowsAsync<TimeoutException>(() => transport.SendCommandAsync("PING"));

        Assert.False(transport.IsConnected);
    }
}
