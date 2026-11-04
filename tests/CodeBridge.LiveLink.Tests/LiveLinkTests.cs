using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CodeBridge.Flow;
using CodeBridge.Transport.Simulation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CodeBridge.LiveLink.Tests;

public class ArmControllerTests
{
    private static readonly BoardProfile Esp32 = BuiltInBoardProfiles.Esp32DevKit;

    private static JointConfig Joint(string id = "base", int pin = 13, double min = 0, double max = 180, double home = 90, double speed = 90) =>
        new() { Id = id, Pin = pin, Min = min, Max = max, Home = home, MaxSpeed = speed };

    private static ArmController Arm(params JointConfig[] joints)
    {
        var arm = new ArmController();
        Assert.Empty(arm.Configure(joints.Length == 0 ? [Joint()] : joints, null, Esp32));
        return arm;
    }

    [Fact]
    public void A_pin_that_cannot_drive_a_servo_is_refused_with_its_name()
    {
        var problems = new ArmController().Configure([Joint(pin: 34)], null, Esp32);   // input only on the ESP32

        Assert.Contains(problems, p => p.Contains("GPIO 34"));
    }

    [Theory]
    [InlineData(-10, 180, 90)]
    [InlineData(0, 200, 90)]
    [InlineData(120, 60, 90)]
    [InlineData(0, 180, 200)]
    public void Limits_outside_the_servo_range_or_a_home_outside_them_are_refused(double min, double max, double home)
    {
        Assert.NotEmpty(new ArmController().Configure([Joint(min: min, max: max, home: home)], null, Esp32));
    }

    [Fact]
    public void Two_joints_on_one_pin_or_with_one_name_are_refused()
    {
        Assert.NotEmpty(new ArmController().Configure([Joint("a", 13), Joint("b", 13)], null, Esp32));
        Assert.NotEmpty(new ArmController().Configure([Joint("a", 13), Joint("a", 14)], null, Esp32));
    }

    [Fact]
    public void Feedback_pins_must_read_analog()
    {
        Assert.Contains(new ArmController().Configure([Joint()], [5], Esp32), p => p.Contains("GPIO 5"));   // no ADC on GPIO 5
        Assert.Empty(new ArmController().Configure([Joint()], [34], Esp32));
    }

    [Fact]
    public void A_target_outside_the_limits_stops_at_the_limit()
    {
        var arm = Arm(Joint(min: 30, max: 150, speed: 360));

        arm.SetTargets([new JointConfig { Id = "base", Angle = 500 }]);
        arm.Tick(TimeSpan.FromSeconds(5));

        Assert.Equal(150, arm.Snapshot()[0].Position);
    }

    [Fact]
    public void A_joint_turns_no_faster_than_its_speed()
    {
        var arm = Arm(Joint(speed: 90));
        arm.SetTargets([new JointConfig { Id = "base", Angle = 180 }]);

        arm.Tick(TimeSpan.FromSeconds(0.5));
        Assert.Equal(135, arm.Snapshot()[0].Position, 3);   // 90 °/s for half a second

        arm.Tick(TimeSpan.FromSeconds(0.5));
        Assert.Equal(180, arm.Snapshot()[0].Position, 3);
    }

    [Fact]
    public void A_speed_above_the_cap_is_capped()
    {
        var arm = Arm(Joint(speed: 10_000));
        arm.SetTargets([new JointConfig { Id = "base", Angle = 180 }]);

        arm.Tick(TimeSpan.FromSeconds(0.1));

        Assert.Equal(90 + ArmController.MaxAllowedSpeed * 0.1, arm.Snapshot()[0].Position, 3);
    }

    [Fact]
    public void Stop_holds_the_arm_where_it_is_and_ignores_targets_until_resume()
    {
        var arm = Arm();
        arm.SetTargets([new JointConfig { Id = "base", Angle = 180 }]);
        arm.Tick(TimeSpan.FromSeconds(0.2));
        var held = arm.Snapshot()[0].Position;

        arm.Stop();
        arm.SetTargets([new JointConfig { Id = "base", Angle = 0 }]);
        arm.Tick(TimeSpan.FromSeconds(2));
        Assert.Equal(held, arm.Snapshot()[0].Position);

        arm.Resume();
        arm.SetTargets([new JointConfig { Id = "base", Angle = 0 }]);
        arm.Tick(TimeSpan.FromSeconds(2));
        Assert.Equal(0, arm.Snapshot()[0].Position);
    }

    [Fact]
    public void A_servo_is_written_only_when_its_whole_degree_changes()
    {
        var arm = Arm();
        Assert.Single(arm.Tick(TimeSpan.Zero));   // the first tick puts every servo at home

        arm.SetTargets([new JointConfig { Id = "base", Angle = 90.3 }]);
        Assert.Empty(arm.Tick(TimeSpan.FromSeconds(1)));

        arm.SetTargets([new JointConfig { Id = "base", Angle = 91 }]);
        Assert.Equal((13, 91), Assert.Single(arm.Tick(TimeSpan.FromSeconds(1))));
    }

    [Fact]
    public void An_unknown_joint_is_reported_and_the_others_still_move()
    {
        var arm = Arm(Joint("base", 13), Joint("elbow", 14));

        var unknown = arm.SetTargets([new JointConfig { Id = "wrist", Angle = 10 }, new JointConfig { Id = "elbow", Angle = 100 }]);
        arm.Tick(TimeSpan.FromSeconds(1));

        Assert.Equal(["wrist"], unknown);
        Assert.Equal(100, arm.Snapshot().Single(j => j.Id == "elbow").Position);
    }
}

public class LinkGuardTests
{
    [Fact]
    public void An_engine_on_this_computer_with_the_key_gets_in()
    {
        Assert.Null(LinkGuard.Reject("127.0.0.1", null, "k1", "k1"));
    }

    [Fact]
    public void A_browser_is_refused_even_with_the_key()
    {
        Assert.Equal(StatusCodes.Status403Forbidden, LinkGuard.Reject("127.0.0.1", "https://evil.example", "k1", "k1"));
    }

    [Fact]
    public void A_wrong_key_or_another_host_name_is_refused()
    {
        Assert.Equal(StatusCodes.Status401Unauthorized, LinkGuard.Reject("127.0.0.1", null, "nope", "k1"));
        Assert.Equal(StatusCodes.Status401Unauthorized, LinkGuard.Reject("127.0.0.1", null, null, "k1"));
        Assert.Equal(StatusCodes.Status421MisdirectedRequest, LinkGuard.Reject("rebind.example", null, "k1", "k1"));
    }
}

/// <summary>The real server, a real WebSocket and the simulator: what Unity does, without Unity.</summary>
public sealed class LiveLinkEndToEndTests : IAsyncLifetime
{
    private const string Key = "test-key";
    private WebApplication _app = null!;
    private SimulatedTransport _simulator = null!;
    private readonly List<(int Pin, double Angle)> _servo = [];
    private string _address = string.Empty;

    public async Task InitializeAsync()
    {
        var profile = BuiltInBoardProfiles.Esp32DevKit;
        var (board, simulated, simulator) = await LinkSession.ConnectAsync("simulator", profile, CancellationToken.None);
        _simulator = simulator!;
        _simulator.BoardChanged += e =>
        {
            if (e.Kind == "servo")
                lock (_servo) _servo.Add((e.Pin, e.Value));
        };

        _app = LiveLinkApp.Create(board, profile, simulated, Key, port: 0);
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _address = address.Replace("http://", "ws://") + "/link";
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task<ClientWebSocket> ConnectAsync(string? key = Key)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(_address + (key is null ? "" : "?k=" + key)), CancellationToken.None);
        return socket;
    }

    private static Task SendAsync(ClientWebSocket socket, object message) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, LinkSession.Json), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<ServerMessage> ReceiveAsync(ClientWebSocket socket, Func<ServerMessage, bool> until, int timeoutMs = 5000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[16 * 1024];
        while (true)
        {
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, timeout.Token);
                stream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var message = JsonSerializer.Deserialize<ServerMessage>(Encoding.UTF8.GetString(stream.ToArray()), LinkSession.Json)!;
            if (until(message))
                return message;
        }
    }

    private double LastAngle(int pin)
    {
        lock (_servo) return _servo.Last(s => s.Pin == pin).Angle;
    }

    [Fact]
    public async Task The_engine_moves_a_servo_and_hears_where_it_is()
    {
        using var socket = await ConnectAsync();
        var hello = await ReceiveAsync(socket, m => m.Type == "hello");
        Assert.True(hello.Simulated);
        Assert.Equal("ESP32 DevKit", hello.Board);

        await SendAsync(socket, new { type = "configure", joints = new[] { new { id = "base", pin = 13, min = 0, max = 180, home = 90, maxSpeed = 360 } }, feedbackPins = new[] { 34 } });
        await SendAsync(socket, new { type = "target", joints = new[] { new { id = "base", angle = 150 } } });

        var arrived = await ReceiveAsync(socket, m => m.Type == "state" && m.Joints is { Length: > 0 } && m.Joints[0].Position == 150);

        Assert.Equal(150, arrived.Joints![0].Target);
        Assert.Equal(150, LastAngle(13));   // the simulated servo really got there
        await ReceiveAsync(socket, m => m.Type == "state" && m.Feedback is { Length: 1 } && m.Feedback[0].Pin == 34);
    }

    [Fact]
    public async Task A_bad_arm_is_explained_and_nothing_moves()
    {
        using var socket = await ConnectAsync();

        await SendAsync(socket, new { type = "configure", joints = new[] { new { id = "base", pin = 34, min = 0, max = 180, home = 90 } } });

        var error = await ReceiveAsync(socket, m => m.Type == "error");
        Assert.Contains("GPIO 34", error.Message);
        lock (_servo) Assert.Empty(_servo);
    }

    [Fact]
    public async Task When_the_engine_goes_away_the_arm_stops_where_it_is()
    {
        var socket = await ConnectAsync();
        await SendAsync(socket, new { type = "configure", joints = new[] { new { id = "base", pin = 13, min = 0, max = 180, home = 90, maxSpeed = 30 } } });
        await SendAsync(socket, new { type = "target", joints = new[] { new { id = "base", angle = 180 } } });
        await ReceiveAsync(socket, m => m.Type == "state" && m.Joints is { Length: > 0 } && m.Joints[0].Position > 92);

        socket.Abort();   // Unity crashed
        socket.Dispose();
        await Task.Delay(400);
        var atDisconnect = LastAngle(13);
        await Task.Delay(800);

        Assert.Equal(atDisconnect, LastAngle(13));   // 30 °/s would have moved it ~24° more
        Assert.True(atDisconnect < 150);
    }

    [Fact]
    public async Task One_engine_at_a_time()
    {
        using var first = await ConnectAsync();
        await ReceiveAsync(first, m => m.Type == "hello");

        var second = await Assert.ThrowsAsync<WebSocketException>(() => ConnectAsync());

        Assert.Contains("409", second.Message);
    }

    [Fact]
    public async Task Without_the_key_nothing_connects()
    {
        var refused = await Assert.ThrowsAsync<WebSocketException>(() => ConnectAsync(key: "wrong"));

        Assert.Contains("401", refused.Message);
    }
}
