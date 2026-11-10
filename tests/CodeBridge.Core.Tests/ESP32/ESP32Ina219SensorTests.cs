using CodeBridge.ESP32;
using CodeBridge.Core.Tests.Mocks;
using Xunit;

namespace CodeBridge.Core.Tests.ESP32;

public class ESP32Ina219SensorTests
{
    private MockTransport CreateTransport(string response = "OK:150.30,5.05,759.02,3.75")
    {
        var transport = new MockTransport();
        transport.EnqueueResponse(response);
        return transport;
    }

    // ── Init ─────────────────────────────────────────────────

    [Fact]
    public async Task InitAsync_Sends_INI_Command_Default_Address()
    {
        var transport = CreateTransport("OK");
        var sensor = new ESP32Ina219Sensor(transport);

        await sensor.InitAsync();

        Assert.True(sensor.IsReady);
        Assert.Contains("INI:64", transport.LastCommand); // 0x40 = 64
    }

    [Fact]
    public async Task InitAsync_Sends_INI_Command_Custom_Address()
    {
        var transport = CreateTransport("OK");
        var sensor = new ESP32Ina219Sensor(transport, 0x41);

        await sensor.InitAsync();

        Assert.Contains("INI:65", transport.LastCommand); // 0x41 = 65
    }

    [Fact]
    public async Task InitAsync_Throws_On_Error()
    {
        var transport = CreateTransport("ERR:INA_NOT_FOUND");
        var sensor = new ESP32Ina219Sensor(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sensor.InitAsync());
    }

    // ── ReadAll ──────────────────────────────────────────────

    [Fact]
    public async Task ReadAllAsync_Parses_All_Values()
    {
        var transport = CreateTransport("OK:150.30,5.05,759.02,3.75");
        var sensor = new ESP32Ina219Sensor(transport);

        var (current, voltage, power, shunt) = await sensor.ReadAllAsync();

        Assert.Equal(150.30, current);
        Assert.Equal(5.05, voltage);
        Assert.Equal(759.02, power);
        Assert.Equal(3.75, shunt);
        Assert.StartsWith("INR", transport.LastCommand);
    }

    [Fact]
    public async Task ReadAllAsync_Handles_Zero_Values()
    {
        var transport = CreateTransport("OK:0.00,0.00,0.00,0.00");
        var sensor = new ESP32Ina219Sensor(transport);

        var (current, voltage, power, shunt) = await sensor.ReadAllAsync();

        Assert.Equal(0.0, current);
        Assert.Equal(0.0, voltage);
        Assert.Equal(0.0, power);
        Assert.Equal(0.0, shunt);
    }

    // ── Individual Reads ─────────────────────────────────────

    [Fact]
    public async Task ReadCurrentMaAsync_Returns_Current()
    {
        var transport = CreateTransport("OK:250.50,12.00,3006.00,6.25");
        var sensor = new ESP32Ina219Sensor(transport);

        var current = await sensor.ReadCurrentMaAsync();

        Assert.Equal(250.50, current);
    }

    [Fact]
    public async Task ReadVoltageAsync_Returns_Voltage()
    {
        var transport = CreateTransport("OK:100.00,3.30,330.00,2.50");
        var sensor = new ESP32Ina219Sensor(transport);

        var voltage = await sensor.ReadVoltageAsync();

        Assert.Equal(3.30, voltage);
    }

    [Fact]
    public async Task ReadPowerMwAsync_Returns_Power()
    {
        var transport = CreateTransport("OK:100.00,5.00,500.00,2.50");
        var sensor = new ESP32Ina219Sensor(transport);

        var power = await sensor.ReadPowerMwAsync();

        Assert.Equal(500.0, power);
    }

    [Fact]
    public async Task ReadShuntVoltageMvAsync_Returns_Shunt()
    {
        var transport = CreateTransport("OK:100.00,5.00,500.00,7.80");
        var sensor = new ESP32Ina219Sensor(transport);

        var shunt = await sensor.ReadShuntVoltageMvAsync();

        Assert.Equal(7.80, shunt);
    }

    // ── PowerReading ─────────────────────────────────────────

    [Fact]
    public async Task ReadPowerReadingAsync_Returns_PowerReading()
    {
        var transport = CreateTransport("OK:200.00,12.00,2400.00,5.00");
        var sensor = new ESP32Ina219Sensor(transport);

        var reading = await sensor.ReadPowerReadingAsync();

        Assert.Equal(200.0, reading.CurrentMa);
        Assert.Equal(12.0, reading.VoltageV);
        Assert.Equal(2400.0, reading.PowerMw);
    }

    [Fact]
    public async Task ReadAsync_Returns_PowerReading()
    {
        var transport = CreateTransport("OK:150.00,5.00,750.00,3.75");
        var sensor = new ESP32Ina219Sensor(transport);

        var reading = await sensor.ReadAsync();

        Assert.Equal(150.0, reading.CurrentMa);
        Assert.Equal(5.0, reading.VoltageV);
        Assert.Equal(750.0, reading.PowerMw);
    }

    // ── Properties ───────────────────────────────────────────

    [Fact]
    public void Properties_Return_Correct_Values()
    {
        var transport = new MockTransport();
        var sensor = new ESP32Ina219Sensor(transport, 0x41);

        Assert.Equal(0x41, sensor.Address);
        Assert.Contains("INA219", sensor.Name);
        Assert.Contains("41", sensor.Name); // Hex address in name
        Assert.Equal("mA", sensor.Unit);
        Assert.False(sensor.IsReady);
    }

    [Fact]
    public void Default_Address_Is_0x40()
    {
        var transport = new MockTransport();
        var sensor = new ESP32Ina219Sensor(transport);

        Assert.Equal(0x40, sensor.Address);
    }

    // ── Error Handling ───────────────────────────────────────

    [Fact]
    public async Task ReadAllAsync_Throws_On_Error()
    {
        var transport = CreateTransport("ERR:INA_READ_FAIL");
        var sensor = new ESP32Ina219Sensor(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sensor.ReadAllAsync());
    }

    [Fact]
    public async Task ReadAllAsync_Throws_On_Invalid_Response()
    {
        var transport = CreateTransport("OK:150.30,5.05"); // Too few parts
        var sensor = new ESP32Ina219Sensor(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sensor.ReadAllAsync());
    }

    // ── PowerReading Model ───────────────────────────────────

    [Fact]
    public async Task PowerReading_ToString_Formatted()
    {
        var transport = CreateTransport("OK:150.30,5.05,759.02,3.75");
        var sensor = new ESP32Ina219Sensor(transport);

        var reading = await sensor.ReadPowerReadingAsync();

        var str = reading.ToString();
        Assert.Contains("150.3mA", str);
        Assert.Contains("5.05V", str);
        Assert.Contains("759.0mW", str);
    }

    // ── Dispose ──────────────────────────────────────────────

    [Fact]
    public async Task Dispose_Prevents_Further_Use()
    {
        var transport = new MockTransport();
        var sensor = new ESP32Ina219Sensor(transport);
        sensor.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => sensor.ReadAllAsync());
    }
}
