using CodeBridge.Core.Protocol;
using CodeBridge.ESP32;
using CodeBridge.Core.Tests.Mocks;
using Xunit;

namespace CodeBridge.Core.Tests.ESP32;

public class ESP32UltrasonicSensorTests
{
    private MockTransport CreateTransport(string response = "OK:25.30")
    {
        var transport = new MockTransport();
        transport.EnqueueResponse(response);
        return transport;
    }

    // ── ReadDistance ──────────────────────────────────────────

    [Fact]
    public async Task ReadDistanceCmAsync_Sends_USR_Command()
    {
        var transport = CreateTransport("OK:25.30");
        var sensor = new ESP32UltrasonicSensor(transport, 5, 18);

        var distance = await sensor.ReadDistanceCmAsync();

        Assert.Equal(25.3, distance);
        Assert.Contains("USR:5:18", transport.LastCommand);
    }

    [Fact]
    public async Task ReadAsync_Returns_Distance()
    {
        var transport = CreateTransport("OK:100.50");
        var sensor = new ESP32UltrasonicSensor(transport, 5, 18);

        var value = await sensor.ReadAsync();

        Assert.Equal(100.5, value);
    }

    // ── Error handling ───────────────────────────────────────

    [Fact]
    public async Task ReadAsync_Throws_On_Error()
    {
        var transport = CreateTransport("ERR:TIMEOUT");
        var sensor = new ESP32UltrasonicSensor(transport, 5, 18);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sensor.ReadAsync());
    }

    // ── Properties ──────────────────────────────────────────

    [Fact]
    public void Properties_Return_Correct_Values()
    {
        var transport = new MockTransport();
        var sensor = new ESP32UltrasonicSensor(transport, 12, 14);

        Assert.Equal(12, sensor.TrigPin);
        Assert.Equal(14, sensor.EchoPin);
        Assert.Equal("HC-SR04 (trig=12, echo=14)", sensor.Name);
        Assert.Equal("cm", sensor.Unit);
        Assert.Equal(2.0, sensor.MinRangeCm);
        Assert.Equal(400.0, sensor.MaxRangeCm);
        Assert.True(sensor.IsReady);
    }

    // ── Init (no-op) ─────────────────────────────────────────

    [Fact]
    public async Task InitAsync_Completes_Without_Sending_Command()
    {
        var transport = new MockTransport();
        var sensor = new ESP32UltrasonicSensor(transport, 5, 18);

        await sensor.InitAsync(); // Should not throw

        Assert.Empty(transport.SentCommands);
    }

    // ── Dispose ──────────────────────────────────────────────

    [Fact]
    public async Task Dispose_Prevents_Further_Operations()
    {
        var transport = new MockTransport();
        var sensor = new ESP32UltrasonicSensor(transport, 5, 18);
        sensor.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => sensor.ReadAsync());
    }
}
