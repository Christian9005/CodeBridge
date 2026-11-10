using CodeBridge.ESP32;
using CodeBridge.Core.Tests.Mocks;
using Xunit;

namespace CodeBridge.Core.Tests.ESP32;

public class ESP32Tcs34725SensorTests
{
    private MockTransport CreateTransport(string response = "OK:128,64,32,5000,4500,320.50")
    {
        var transport = new MockTransport();
        transport.EnqueueResponse(response);
        return transport;
    }

    // ── Init ─────────────────────────────────────────────────

    [Fact]
    public async Task InitAsync_Sends_TCI_Command_Default_Params()
    {
        var transport = CreateTransport("OK");
        var sensor = new ESP32Tcs34725Sensor(transport);

        await sensor.InitAsync();

        Assert.True(sensor.IsReady);
        Assert.Contains("TCI:50:4", transport.LastCommand);
    }

    [Fact]
    public async Task InitAsync_Sends_TCI_Command_Custom_Params()
    {
        var transport = CreateTransport("OK");
        var sensor = new ESP32Tcs34725Sensor(transport, integrationTimeMs: 101, gain: 16);

        await sensor.InitAsync();

        Assert.Contains("TCI:101:16", transport.LastCommand);
    }

    [Fact]
    public async Task InitAsync_Throws_On_Error()
    {
        var transport = CreateTransport("ERR:TCS_NOT_FOUND");
        var sensor = new ESP32Tcs34725Sensor(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sensor.InitAsync());
    }

    // ── ReadColor ────────────────────────────────────────────

    [Fact]
    public async Task ReadColorAsync_Parses_RgbColor()
    {
        var transport = CreateTransport("OK:200,100,50,6000,5200,450.00");
        var sensor = new ESP32Tcs34725Sensor(transport);

        var color = await sensor.ReadColorAsync();

        Assert.Equal(200, color.R);
        Assert.Equal(100, color.G);
        Assert.Equal(50, color.B);
        Assert.Equal(6000, color.Clear);
        Assert.StartsWith("TCR", transport.LastCommand);
    }

    [Fact]
    public async Task ReadColorAsync_Returns_Zero_Values()
    {
        var transport = CreateTransport("OK:0,0,0,0,0,0.00");
        var sensor = new ESP32Tcs34725Sensor(transport);

        var color = await sensor.ReadColorAsync();

        Assert.Equal(0, color.R);
        Assert.Equal(0, color.G);
        Assert.Equal(0, color.B);
    }

    [Fact]
    public async Task ReadColorAsync_Returns_Max_Values()
    {
        var transport = CreateTransport("OK:255,255,255,65535,10000,100000.00");
        var sensor = new ESP32Tcs34725Sensor(transport);

        var color = await sensor.ReadColorAsync();

        Assert.Equal(255, color.R);
        Assert.Equal(255, color.G);
        Assert.Equal(255, color.B);
        Assert.Equal(65535, color.Clear);
    }

    // ── ReadColorTemperature ─────────────────────────────────

    [Fact]
    public async Task ReadColorTemperatureAsync_Returns_Kelvin()
    {
        var transport = CreateTransport("OK:128,64,32,5000,6500,320.50");
        var sensor = new ESP32Tcs34725Sensor(transport);

        var temp = await sensor.ReadColorTemperatureAsync();

        Assert.Equal(6500, temp);
    }

    // ── ReadLux ──────────────────────────────────────────────

    [Fact]
    public async Task ReadLuxAsync_Returns_Lux()
    {
        var transport = CreateTransport("OK:128,64,32,5000,4500,850.75");
        var sensor = new ESP32Tcs34725Sensor(transport);

        var lux = await sensor.ReadLuxAsync();

        Assert.Equal(850.75, lux);
    }

    // ── ReadAsync ────────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_Returns_RgbColor()
    {
        var transport = CreateTransport("OK:100,150,200,3000,5000,200.00");
        var sensor = new ESP32Tcs34725Sensor(transport);

        var color = await sensor.ReadAsync();

        Assert.Equal(100, color.R);
        Assert.Equal(150, color.G);
        Assert.Equal(200, color.B);
    }

    // ── RgbColor Model ───────────────────────────────────────

    [Fact]
    public async Task ReadColorAsync_ToHex_Works()
    {
        var transport = CreateTransport("OK:255,128,0,5000,4500,320.00");
        var sensor = new ESP32Tcs34725Sensor(transport);

        var color = await sensor.ReadColorAsync();

        Assert.Equal("#FF8000", color.ToHex());
    }

    // ── Properties ───────────────────────────────────────────

    [Fact]
    public void Properties_Return_Correct_Values()
    {
        var transport = new MockTransport();
        var sensor = new ESP32Tcs34725Sensor(transport, 101, 16);

        Assert.Equal(101, sensor.IntegrationTimeMs);
        Assert.Equal(16, sensor.Gain);
        Assert.Contains("TCS34725", sensor.Name);
        Assert.Equal("RGB", sensor.Unit);
        Assert.False(sensor.IsReady);
    }

    [Fact]
    public void Default_Params_Are_50ms_4x()
    {
        var transport = new MockTransport();
        var sensor = new ESP32Tcs34725Sensor(transport);

        Assert.Equal(50, sensor.IntegrationTimeMs);
        Assert.Equal(4, sensor.Gain);
    }

    // ── Error Handling ───────────────────────────────────────

    [Fact]
    public async Task ReadColorAsync_Throws_On_Error()
    {
        var transport = CreateTransport("ERR:TCS_READ_FAIL");
        var sensor = new ESP32Tcs34725Sensor(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sensor.ReadColorAsync());
    }

    [Fact]
    public async Task ReadColorAsync_Throws_On_Invalid_Response()
    {
        var transport = CreateTransport("OK:128,64"); // Too few parts
        var sensor = new ESP32Tcs34725Sensor(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sensor.ReadColorAsync());
    }

    // ── Dispose ──────────────────────────────────────────────

    [Fact]
    public async Task Dispose_Prevents_Further_Use()
    {
        var transport = new MockTransport();
        var sensor = new ESP32Tcs34725Sensor(transport);
        sensor.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => sensor.ReadColorAsync());
    }
}
