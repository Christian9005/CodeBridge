using System.Net;
using System.Net.Sockets;
using CodeBridge.Flow;
using CodeBridge.HomeAssistant;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace CodeBridge.HomeAssistant.Tests;

/// <summary>What the Visual Studio editor relies on: explaining a broker that does not work, and turning a flow into entities.</summary>
public class EditorSupportTests
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static HomeAssistantOptions Options(int port, string? user = null, string? password = null) =>
        new() { Broker = "127.0.0.1", Port = port, Username = user, Password = password };

    private static async Task<MqttServer> StartBrokerAsync(int port, string? user = null, string? password = null)
    {
        var broker = new MqttFactory().CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(port).Build());
        if (user is not null)
        {
            broker.ValidatingConnectionAsync += e =>
            {
                if (e.UserName != user || e.Password != password)
                    e.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
                return Task.CompletedTask;
            };
        }

        await broker.StartAsync();
        return broker;
    }

    [Fact]
    public async Task A_working_broker_is_reported_as_ok()
    {
        var port = FreePort();
        using var broker = await StartBrokerAsync(port, "codebridge", "secret");

        var result = await HomeAssistantBrokerCheck.RunAsync(Options(port, "codebridge", "secret"));

        Assert.True(result.Ok);
        Assert.Equal("ok", result.Reason);
    }

    [Fact]
    public async Task A_wrong_password_is_reported_as_an_authentication_problem()
    {
        var port = FreePort();
        using var broker = await StartBrokerAsync(port, "codebridge", "secret");

        var result = await HomeAssistantBrokerCheck.RunAsync(Options(port, "codebridge", "wrong"));

        Assert.False(result.Ok);
        Assert.Equal("auth", result.Reason);
        Assert.Contains("user name or password", result.Message);
    }

    [Fact]
    public async Task A_machine_without_a_broker_tells_the_user_to_install_one()
    {
        var result = await HomeAssistantBrokerCheck.RunAsync(Options(FreePort()));

        Assert.False(result.Ok);
        Assert.Equal("refused", result.Reason);
        Assert.Contains("Mosquitto", result.Message);
    }

    [Fact]
    public void An_unknown_host_and_a_silent_host_are_told_apart()
    {
        var options = new HomeAssistantOptions { Broker = "ha.example", Port = 1883 };

        Assert.Equal("not-found", HomeAssistantBrokerCheck.Classify(new IOException("x", new SocketException((int)SocketError.HostNotFound)), options).Reason);
        Assert.Equal("timeout", HomeAssistantBrokerCheck.Classify(new IOException("x", new SocketException((int)SocketError.TimedOut)), options).Reason);
        Assert.Equal("timeout", HomeAssistantBrokerCheck.Classify(new OperationCanceledException(), options).Reason);
        Assert.Equal("error", HomeAssistantBrokerCheck.Classify(new InvalidOperationException("odd"), options).Reason);
    }

    [Fact]
    public async Task Cancelling_the_check_is_not_reported_as_a_broker_problem()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HomeAssistantBrokerCheck.RunAsync(Options(FreePort()), ct: cancel.Token));
    }

    private static FlowNode Node(string type, params (string Key, object Value)[] parameters)
    {
        var node = new FlowNode { Type = type };
        foreach (var (key, value) in parameters)
            node.Parameters[key] = value;
        return node;
    }

    [Fact]
    public void Each_input_and_output_block_becomes_the_matching_entity()
    {
        var profile = BuiltInBoardProfiles.Default;
        var document = new FlowDocument();
        document.Nodes.Add(Node(BuiltInBlockCatalog.GpioSetOutput, ("pin", 2)));
        document.Nodes.Add(Node(BuiltInBlockCatalog.GpioDigitalWrite, ("pin", 2L))); // same pin: shown once
        document.Nodes.Add(Node(BuiltInBlockCatalog.GpioDigitalWrite, ("pin", "27"), ("activeLow", "true")));
        document.Nodes.Add(Node(BuiltInBlockCatalog.PwmWrite, ("pin", 4), ("frequencyHz", 1000)));
        document.Nodes.Add(Node(BuiltInBlockCatalog.GpioAnalogRead, ("pin", 34)));
        document.Nodes.Add(Node(BuiltInBlockCatalog.GpioDigitalRead, ("pin", 12)));
        document.Nodes.Add(Node(BuiltInBlockCatalog.DebugLog));

        var entities = HomeAssistantFlow.EntitiesFor(document, profile);

        Assert.Equal(
            new[] { "switch:out-2", "switch:out-27", "light:pwm-4", "sensor:analog-34", "binary_sensor:input-12" },
            entities.Select(e => $"{e.Component}:{e.Id}").ToArray());
        Assert.Equal("Built-in LED", entities[0].Name);
        Assert.Equal("Output GPIO27", entities[1].Name);
    }

    [Fact]
    public void A_pin_used_by_PWM_and_a_digital_write_is_shown_once_as_a_light()
    {
        var document = new FlowDocument();
        document.Nodes.Add(Node(BuiltInBlockCatalog.PwmWrite, ("pin", 4)));
        document.Nodes.Add(Node(BuiltInBlockCatalog.GpioDigitalWrite, ("pin", 4)));

        var entities = HomeAssistantFlow.EntitiesFor(document, BuiltInBoardProfiles.Default);

        Assert.Single(entities);
        Assert.Equal("light", entities[0].Component);
    }

    [Fact]
    public void Without_a_flow_the_built_in_LED_is_offered()
    {
        var entities = HomeAssistantFlow.EntitiesFor(null, BuiltInBoardProfiles.Default);

        var led = Assert.Single(entities);
        Assert.Equal("switch", led.Component);
    }

    [Fact]
    public void A_flow_without_input_or_output_blocks_gives_no_entities()
    {
        var document = new FlowDocument();
        document.Nodes.Add(Node(BuiltInBlockCatalog.DebugLog));

        Assert.Empty(HomeAssistantFlow.EntitiesFor(document, BuiltInBoardProfiles.Default));
    }
}
