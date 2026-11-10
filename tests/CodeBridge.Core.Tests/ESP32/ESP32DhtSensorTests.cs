using CodeBridge.Core.Protocol;
using CodeBridge.ESP32;
using CodeBridge.Core.Tests.Mocks;
using Xunit;

namespace CodeBridge.Core.Tests.ESP32;

public class ESP32DhtSensorTests
{
    private MockTransport CreateTransport(string response = "OK:23.50,65.00")
    {
        var transport = new MockTransport();
        transport.EnqueueResponse(response);
        return transport;
    }

    // ── Init ─────────────────────────────────────────────────

    [Fact]
    public async Task InitAsync_Sends_DHTI_Command()
    {
        var transport = CreateTransport("OK");
        var dht = new ESP32DhtSensor(transport, 4, 22);

        await dht.InitAsync();

        Assert.Contains("DHTI:4:22", transport.LastCommand);
        Assert.True(dht.IsReady);
    }

    [Fact]
    public void Constructor_Throws_For_Invalid_DhtType()
    {
        var transport = new MockTransport();
        Assert.Throws<ArgumentException>(() => new ESP32DhtSensor(transport, 4, 33));
    }

    // ── ReadBoth ─────────────────────────────────────────────

    [Fact]
    public async Task ReadBothAsync_Parses_Temperature_And_Humidity()
    {
        var transport = CreateTransport("OK:23.50,65.00");
        var dht = new ESP32DhtSensor(transport, 4);

        var (temp, humidity) = await dht.ReadBothAsync();

        Assert.Equal(23.50, temp);
        Assert.Equal(65.00, humidity);
        Assert.Contains("DHTR:4:22", transport.LastCommand);
    }

    // ── ReadTemperature ──────────────────────────────────────

    [Fact]
    public async Task ReadTemperatureCelsiusAsync_Returns_TempC()
    {
        var transport = CreateTransport("OK:25.00,50.00");
        var dht = new ESP32DhtSensor(transport, 4);

        var temp = await dht.ReadTemperatureCelsiusAsync();

        Assert.Equal(25.0, temp);
    }

    [Fact]
    public async Task ReadTemperatureFahrenheitAsync_Converts_Correctly()
    {
        var transport = CreateTransport("OK:100.00,50.00");
        var dht = new ESP32DhtSensor(transport, 4);

        var tempF = await dht.ReadTemperatureFahrenheitAsync();

        Assert.Equal(212.0, tempF); // 100°C = 212°F
    }

    // ── ReadHumidity ─────────────────────────────────────────

    [Fact]
    public async Task ReadHumidityAsync_Returns_Humidity()
    {
        var transport = CreateTransport("OK:23.00,72.50");
        var dht = new ESP32DhtSensor(transport, 4);

        var humidity = await dht.ReadHumidityAsync();

        Assert.Equal(72.5, humidity);
    }

    // ── ReadAsync (generic) ──────────────────────────────────

    [Fact]
    public async Task ReadAsync_Returns_Temperature()
    {
        var transport = CreateTransport("OK:30.20,55.00");
        var dht = new ESP32DhtSensor(transport, 4);

        var value = await dht.ReadAsync();

        Assert.Equal(30.2, value);
    }

    // ── DHT11 support ────────────────────────────────────────

    [Fact]
    public async Task DHT11_Type_Is_Sent_In_Command()
    {
        var transport = CreateTransport("OK:24.00,60.00");
        var dht = new ESP32DhtSensor(transport, 15, 11);

        await dht.ReadBothAsync();

        Assert.Contains("DHTR:15:11", transport.LastCommand);
        Assert.Equal("DHT11 (pin 15)", dht.Name);
    }

    // ── Error handling ───────────────────────────────────────

    [Fact]
    public async Task ReadAsync_Throws_On_Error()
    {
        var transport = CreateTransport("ERR:CHECKSUM");
        var dht = new ESP32DhtSensor(transport, 4);

        await Assert.ThrowsAsync<InvalidOperationException>(() => dht.ReadAsync());
    }

    // ── Properties ──────────────────────────────────────────

    [Fact]
    public void Properties_Return_Correct_Values()
    {
        var transport = new MockTransport();
        var dht = new ESP32DhtSensor(transport, 4, 22);

        Assert.Equal(4, dht.Pin);
        Assert.Equal(22, dht.DhtType);
        Assert.Equal("DHT22 (pin 4)", dht.Name);
        Assert.Equal("°C", dht.Unit);
    }

    // ── Dispose ──────────────────────────────────────────────

    [Fact]
    public async Task Dispose_Prevents_Further_Operations()
    {
        var transport = new MockTransport();
        var dht = new ESP32DhtSensor(transport, 4);
        dht.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => dht.ReadAsync());
    }
}
