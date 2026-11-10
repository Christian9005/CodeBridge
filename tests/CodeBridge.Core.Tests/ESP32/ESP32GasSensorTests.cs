using CodeBridge.ESP32;
using CodeBridge.Core.Tests.Mocks;
using Xunit;

namespace CodeBridge.Core.Tests.ESP32;

public class ESP32GasSensorTests
{
    private MockTransport CreateTransport(string response = "OK:1500")
    {
        var transport = new MockTransport();
        transport.EnqueueResponse(response);
        return transport;
    }

    // ── Init ─────────────────────────────────────────────────

    [Fact]
    public async Task InitAsync_Sets_Pin_As_Input()
    {
        var transport = CreateTransport("OK");
        var gas = new ESP32GasSensor(transport, 34, "CO2");

        await gas.InitAsync();

        Assert.True(gas.IsReady);
        Assert.StartsWith("PM:34:", transport.LastCommand);
    }

    [Fact]
    public async Task InitAsync_Throws_On_Error()
    {
        var transport = CreateTransport("ERR:PIN_FAIL");
        var gas = new ESP32GasSensor(transport, 34);

        await Assert.ThrowsAsync<InvalidOperationException>(() => gas.InitAsync());
    }

    // ── ReadRaw ──────────────────────────────────────────────

    [Fact]
    public async Task ReadRawAsync_Returns_ADC_Value()
    {
        var transport = CreateTransport("OK:2048");
        var gas = new ESP32GasSensor(transport, 34);

        var raw = await gas.ReadRawAsync();

        Assert.Equal(2048, raw);
        Assert.StartsWith("AR:34", transport.LastCommand);
    }

    [Fact]
    public async Task ReadRawAsync_Returns_Zero()
    {
        var transport = CreateTransport("OK:0");
        var gas = new ESP32GasSensor(transport, 34);

        var raw = await gas.ReadRawAsync();

        Assert.Equal(0, raw);
    }

    [Fact]
    public async Task ReadRawAsync_Returns_Max_ADC()
    {
        var transport = CreateTransport("OK:4095");
        var gas = new ESP32GasSensor(transport, 34);

        var raw = await gas.ReadRawAsync();

        Assert.Equal(4095, raw);
    }

    // ── ReadPpm ──────────────────────────────────────────────

    [Fact]
    public async Task ReadPpmAsync_Calculates_PPM_Above_Baseline()
    {
        // cleanAirValue=400, raw=800 → ratio=2.0, ppm=(2.0-1.0)*5.0*100=500
        var transport = CreateTransport("OK:800");
        var gas = new ESP32GasSensor(transport, 34, cleanAirValue: 400, sensitivityFactor: 5.0);

        var ppm = await gas.ReadPpmAsync();

        Assert.Equal(500.0, ppm);
    }

    [Fact]
    public async Task ReadPpmAsync_Returns_Zero_When_Below_Baseline()
    {
        // raw=200 < cleanAirValue=400 → ratio=0.5, ppm=(0.5-1.0)*5.0*100=-250 → clamped to 0
        var transport = CreateTransport("OK:200");
        var gas = new ESP32GasSensor(transport, 34, cleanAirValue: 400, sensitivityFactor: 5.0);

        var ppm = await gas.ReadPpmAsync();

        Assert.Equal(0.0, ppm);
    }

    [Fact]
    public async Task ReadPpmAsync_At_Baseline_Returns_Zero()
    {
        var transport = CreateTransport("OK:400");
        var gas = new ESP32GasSensor(transport, 34, cleanAirValue: 400);

        var ppm = await gas.ReadPpmAsync();

        Assert.Equal(0.0, ppm);
    }

    // ── ReadAsync ────────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_Delegates_To_ReadPpmAsync()
    {
        var transport = CreateTransport("OK:1200");
        var gas = new ESP32GasSensor(transport, 34, cleanAirValue: 400, sensitivityFactor: 5.0);

        var value = await gas.ReadAsync();

        Assert.True(value > 0);
    }

    // ── Properties ───────────────────────────────────────────

    [Fact]
    public void Properties_Return_Correct_Values()
    {
        var transport = new MockTransport();
        var gas = new ESP32GasSensor(transport, 34, "CO2");

        Assert.Equal(34, gas.Pin);
        Assert.Equal("CO2", gas.GasType);
        Assert.Contains("Gas Sensor", gas.Name);
        Assert.Equal("ppm", gas.Unit);
        Assert.False(gas.IsReady);
    }

    [Fact]
    public void Default_GasType_Is_Smoke()
    {
        var transport = new MockTransport();
        var gas = new ESP32GasSensor(transport, 34);

        Assert.Equal("Smoke", gas.GasType);
    }

    [Fact]
    public void CleanAirValue_And_SensitivityFactor_Configurable()
    {
        var transport = new MockTransport();
        var gas = new ESP32GasSensor(transport, 34, cleanAirValue: 500, sensitivityFactor: 3.0);

        Assert.Equal(500.0, gas.CleanAirValue);
        Assert.Equal(3.0, gas.SensitivityFactor);
    }

    // ── Error Handling ───────────────────────────────────────

    [Fact]
    public async Task ReadRawAsync_Throws_On_Error()
    {
        var transport = CreateTransport("ERR:ADC_FAIL");
        var gas = new ESP32GasSensor(transport, 34);

        await Assert.ThrowsAsync<InvalidOperationException>(() => gas.ReadRawAsync());
    }

    // ── Dispose ──────────────────────────────────────────────

    [Fact]
    public async Task Dispose_Prevents_Further_Use()
    {
        var transport = new MockTransport();
        var gas = new ESP32GasSensor(transport, 34);
        gas.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => gas.ReadRawAsync());
    }
}
