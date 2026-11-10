using CodeBridge.Core.Enums;
using CodeBridge.ESP32;
using CodeBridge.Core.Tests.Mocks;
using Xunit;

namespace CodeBridge.Core.Tests.ESP32;

public class ESP32MotorControllerTests
{
    private MockTransport CreateTransport(string response = "OK")
    {
        var transport = new MockTransport();
        transport.EnqueueResponse(response);
        return transport;
    }

    // ── Init ─────────────────────────────────────────────────

    [Fact]
    public async Task InitAsync_Sends_MI_Command()
    {
        var transport = CreateTransport("OK");
        var motor = new ESP32MotorController(transport, 27, 26, 14);

        await motor.InitAsync();

        Assert.Contains("MI:27:26:14", transport.LastCommand);
        Assert.True(motor.IsReady);
    }

    [Fact]
    public async Task InitAsync_Throws_On_Error()
    {
        var transport = CreateTransport("ERR:NO_PWM");
        var motor = new ESP32MotorController(transport, 27, 26, 14);

        await Assert.ThrowsAsync<InvalidOperationException>(() => motor.InitAsync());
    }

    // ── SetSpeed ─────────────────────────────────────────────

    [Fact]
    public async Task SetSpeedAsync_Forward_Sends_MS_With_Positive()
    {
        var transport = CreateTransport("OK");
        var motor = new ESP32MotorController(transport, 27, 26, 14);

        await motor.SetSpeedAsync(75);

        Assert.Contains("MS:27:75", transport.LastCommand);
        Assert.Equal(MotorDirection.Forward, motor.Direction);
    }

    [Fact]
    public async Task SetSpeedAsync_Reverse_Sends_MS_With_Negative()
    {
        var transport = CreateTransport("OK");
        var motor = new ESP32MotorController(transport, 27, 26, 14);

        await motor.SetSpeedAsync(-50);

        Assert.Contains("MS:27:-50", transport.LastCommand);
        Assert.Equal(MotorDirection.Reverse, motor.Direction);
    }

    [Fact]
    public async Task SetSpeedAsync_Zero_Sets_Direction_Stopped()
    {
        var transport = CreateTransport("OK");
        var motor = new ESP32MotorController(transport, 27, 26);

        await motor.SetSpeedAsync(0);

        Assert.Equal(MotorDirection.Stopped, motor.Direction);
    }

    [Fact]
    public async Task SetSpeedAsync_Clamps_To_Range()
    {
        var transport = CreateTransport("OK");
        var motor = new ESP32MotorController(transport, 27, 26);

        await motor.SetSpeedAsync(200); // Should clamp to 100

        Assert.Contains("MS:27:100", transport.LastCommand);
    }

    // ── Brake ────────────────────────────────────────────────

    [Fact]
    public async Task BrakeAsync_Sends_MX_Command()
    {
        var transport = CreateTransport("OK");
        var motor = new ESP32MotorController(transport, 27, 26);

        await motor.BrakeAsync();

        Assert.Contains("MX:27", transport.LastCommand);
        Assert.Equal(MotorDirection.Stopped, motor.Direction);
    }

    // ── Properties ───────────────────────────────────────────

    [Fact]
    public void Properties_Return_Correct_Values()
    {
        var transport = new MockTransport();
        var motor = new ESP32MotorController(transport, 27, 26, 14);

        Assert.Equal(27, motor.In1Pin);
        Assert.Equal(26, motor.In2Pin);
        Assert.Equal(14, motor.EnablePin);
        Assert.Equal("Motor (IN1=27, IN2=26)", motor.Name);
        Assert.Equal(MotorDirection.Stopped, motor.Direction);
    }

    // ── Dispose ──────────────────────────────────────────────

    [Fact]
    public async Task Dispose_Prevents_Further_Operations()
    {
        var transport = new MockTransport();
        var motor = new ESP32MotorController(transport, 27, 26);
        motor.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => motor.SetSpeedAsync(50));
    }
}
