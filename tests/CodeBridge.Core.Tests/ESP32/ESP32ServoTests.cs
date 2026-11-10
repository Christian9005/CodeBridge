using CodeBridge.Core.Protocol;
using CodeBridge.ESP32;
using CodeBridge.Core.Tests.Mocks;
using Xunit;

namespace CodeBridge.Core.Tests.ESP32;

public class ESP32ServoTests
{
    private MockTransport CreateTransport(string response = "OK")
    {
        var transport = new MockTransport();
        transport.EnqueueResponse(response);
        return transport;
    }

    // ── Init (Attach) ────────────────────────────────────────

    [Fact]
    public async Task InitAsync_Sends_SA_Command_With_Pin_And_PulseRange()
    {
        var transport = CreateTransport("OK");
        var servo = new ESP32Servo(transport, 13, 500, 2500);

        await servo.InitAsync();

        Assert.Contains("SA:13:500:2500", transport.LastCommand);
        Assert.True(servo.IsReady);
    }

    [Fact]
    public async Task InitAsync_Throws_On_Error()
    {
        var transport = CreateTransport("ERR:NO_SLOT");
        var servo = new ESP32Servo(transport, 13);

        await Assert.ThrowsAsync<InvalidOperationException>(() => servo.InitAsync());
    }

    // ── SetAngle ─────────────────────────────────────────────

    [Fact]
    public async Task SetAngleAsync_Sends_SV_Command()
    {
        var transport = CreateTransport("OK");
        var servo = new ESP32Servo(transport, 13);

        await servo.SetAngleAsync(90);

        Assert.StartsWith("SV:13:90", transport.LastCommand);
    }

    [Fact]
    public async Task SetAngleAsync_Throws_For_Invalid_Angle()
    {
        var transport = new MockTransport();
        var servo = new ESP32Servo(transport, 13);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => servo.SetAngleAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => servo.SetAngleAsync(181));
    }

    // ── GetAngle ─────────────────────────────────────────────

    [Fact]
    public async Task GetAngleAsync_Returns_Parsed_Angle()
    {
        var transport = CreateTransport("OK:90");
        var servo = new ESP32Servo(transport, 13);

        var angle = await servo.GetAngleAsync();

        Assert.Equal(90, angle);
        Assert.StartsWith("SVR:13", transport.LastCommand);
    }

    // ── SetPulseWidth ────────────────────────────────────────

    [Fact]
    public async Task SetPulseWidthAsync_Sends_SU_Command()
    {
        var transport = CreateTransport("OK");
        var servo = new ESP32Servo(transport, 13);

        await servo.SetPulseWidthAsync(1500);

        Assert.StartsWith("SU:13:1500", transport.LastCommand);
    }

    // ── Detach ───────────────────────────────────────────────

    [Fact]
    public async Task DetachAsync_Sends_SD_Command()
    {
        var transport = CreateTransport("OK");
        transport.EnqueueResponse("OK"); // for detach
        var servo = new ESP32Servo(transport, 13);
        await servo.InitAsync();

        await servo.DetachAsync();

        Assert.StartsWith("SD:13", transport.LastCommand);
        Assert.False(servo.IsReady);
    }

    // ── Properties ───────────────────────────────────────────

    [Fact]
    public void Properties_Return_Correct_Values()
    {
        var transport = new MockTransport();
        var servo = new ESP32Servo(transport, 18, 600, 2400);

        Assert.Equal(18, servo.Pin);
        Assert.Equal("Servo (pin 18)", servo.Name);
        Assert.Equal(0, servo.MinAngle);
        Assert.Equal(180, servo.MaxAngle);
        Assert.Equal(600, servo.MinPulseUs);
        Assert.Equal(2400, servo.MaxPulseUs);
    }

    // ── Dispose ──────────────────────────────────────────────

    [Fact]
    public async Task Dispose_Prevents_Further_Operations()
    {
        var transport = new MockTransport();
        var servo = new ESP32Servo(transport, 13);
        servo.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => servo.SetAngleAsync(90));
    }
}
