using CodeBridge.ESP32;
using CodeBridge.Core.Tests.Mocks;
using Xunit;

namespace CodeBridge.Core.Tests.ESP32;

public class ESP32StepperMotorTests
{
    private MockTransport CreateTransport(string response = "OK")
    {
        var transport = new MockTransport();
        transport.EnqueueResponse(response);
        return transport;
    }

    // ── Init ─────────────────────────────────────────────────

    [Fact]
    public async Task InitAsync_Sends_STI_Command()
    {
        var transport = CreateTransport("OK");
        var stepper = new ESP32StepperMotor(transport, 19, 18, 5, 17, 2048);

        await stepper.InitAsync();

        Assert.Contains("STI:19:18:5:17:2048", transport.LastCommand);
        Assert.True(stepper.IsReady);
    }

    // ── Step ─────────────────────────────────────────────────

    [Fact]
    public async Task StepAsync_Sends_STS_Command()
    {
        var transport = CreateTransport("OK");
        var stepper = new ESP32StepperMotor(transport, 19, 18, 5, 17);

        await stepper.StepAsync(512);

        Assert.Contains("STS:19:512:10", transport.LastCommand); // Default 10 RPM
    }

    [Fact]
    public async Task StepAsync_Negative_Steps_For_Reverse()
    {
        var transport = CreateTransport("OK");
        var stepper = new ESP32StepperMotor(transport, 19, 18, 5, 17);

        await stepper.StepAsync(-256);

        Assert.Contains("STS:19:-256:10", transport.LastCommand);
    }

    // ── RotateDegrees ────────────────────────────────────────

    [Fact]
    public async Task RotateDegreesAsync_Converts_To_Steps()
    {
        var transport = CreateTransport("OK");
        var stepper = new ESP32StepperMotor(transport, 19, 18, 5, 17, 2048);

        await stepper.RotateDegreesAsync(180); // 2048/2 = 1024 steps

        Assert.Contains("STS:19:1024", transport.LastCommand);
    }

    // ── SetSpeedRpm ──────────────────────────────────────────

    [Fact]
    public async Task SetSpeedRpmAsync_Changes_Speed()
    {
        var transport = CreateTransport("OK");
        transport.EnqueueResponse("OK"); // for step
        var stepper = new ESP32StepperMotor(transport, 19, 18, 5, 17);

        await stepper.SetSpeedRpmAsync(15);
        await stepper.StepAsync(100);

        Assert.Contains("STS:19:100:15", transport.LastCommand);
    }

    [Fact]
    public async Task SetSpeedRpmAsync_Throws_For_Zero()
    {
        var transport = new MockTransport();
        var stepper = new ESP32StepperMotor(transport, 19, 18, 5, 17);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => stepper.SetSpeedRpmAsync(0));
    }

    // ── Properties ───────────────────────────────────────────

    [Fact]
    public void Properties_Return_Correct_Values()
    {
        var transport = new MockTransport();
        var stepper = new ESP32StepperMotor(transport, 19, 18, 5, 17, 4096);

        Assert.Equal(19, stepper.Pin1);
        Assert.Equal(18, stepper.Pin2);
        Assert.Equal(5, stepper.Pin3);
        Assert.Equal(17, stepper.Pin4);
        Assert.Equal(4096, stepper.StepsPerRevolution);
        Assert.Equal("Stepper (pin1=19)", stepper.Name);
    }

    // ── Dispose ──────────────────────────────────────────────

    [Fact]
    public async Task Dispose_Prevents_Further_Operations()
    {
        var transport = new MockTransport();
        var stepper = new ESP32StepperMotor(transport, 19, 18, 5, 17);
        stepper.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => stepper.StepAsync(100));
    }
}
