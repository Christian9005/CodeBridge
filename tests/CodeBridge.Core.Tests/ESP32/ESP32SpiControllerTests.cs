using CodeBridge.Core.Protocol;
using CodeBridge.ESP32;
using CodeBridge.Core.Tests.Mocks;
using Xunit;

namespace CodeBridge.Core.Tests.ESP32;

/// <summary>
/// Tests for the ESP32 SPI controller talking to the firmware via protocol commands.
/// </summary>
public class ESP32SpiControllerTests
{
    private MockTransport CreateTransport(string response = "OK")
    {
        var transport = new MockTransport();
        transport.EnqueueResponse(response);
        return transport;
    }

    // ── Transfer ─────────────────────────────────────────────

    [Fact]
    public async Task TransferAsync_Sends_ST_Command_With_CsPin_And_HexData()
    {
        var transport = CreateTransport("OK:AABB");
        var spi = new ESP32SpiController(transport);

        var result = await spi.TransferAsync(5, new byte[] { 0xFF, 0x00 });

        Assert.StartsWith("ST:", transport.LastCommand);
        Assert.Contains("5", transport.LastCommand);
        Assert.Contains("FF00", transport.LastCommand);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, result);
    }

    [Fact]
    public async Task TransferAsync_Throws_On_Error()
    {
        var transport = CreateTransport("ERR:SPI failure");
        var spi = new ESP32SpiController(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => spi.TransferAsync(5, new byte[] { 0xFF }));
    }

    [Fact]
    public async Task TransferAsync_Throws_On_Empty_Data()
    {
        var transport = CreateTransport("OK");
        var spi = new ESP32SpiController(transport);

        await Assert.ThrowsAsync<ArgumentException>(
            () => spi.TransferAsync(5, Array.Empty<byte>()));
    }

    // ── Write ────────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_Sends_SW_Command()
    {
        var transport = CreateTransport("OK");
        var spi = new ESP32SpiController(transport);

        await spi.WriteAsync(10, new byte[] { 0x01, 0x02, 0x03 });

        Assert.StartsWith("SW:", transport.LastCommand);
        Assert.Contains("010203", transport.LastCommand);
    }

    // ── Read ─────────────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_Sends_SR_Command_And_Returns_Bytes()
    {
        var transport = CreateTransport("OK:DEADBEEF");
        var spi = new ESP32SpiController(transport);

        var result = await spi.ReadAsync(5, 4);

        Assert.StartsWith("SR:", transport.LastCommand);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, result);
    }

    [Fact]
    public async Task ReadAsync_Throws_On_Invalid_Length()
    {
        var transport = CreateTransport("OK");
        var spi = new ESP32SpiController(transport);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => spi.ReadAsync(5, 0));
    }

    // ── Config ───────────────────────────────────────────────

    [Fact]
    public async Task SetClockSpeedAsync_Sends_SC_Command()
    {
        var transport = CreateTransport("OK");
        var spi = new ESP32SpiController(transport);

        await spi.SetClockSpeedAsync(4000000);

        Assert.StartsWith("SC:", transport.LastCommand);
        Assert.Contains("4000000", transport.LastCommand);
    }

    [Fact]
    public async Task SetClockSpeedAsync_Does_Not_Change_The_Mode()
    {
        // the firmware sets the mode for any value from 0 to 3, so a 0 here would undo a mode chosen before: -1 means "leave it"
        var transport = CreateTransport("OK");
        var spi = new ESP32SpiController(transport);

        await spi.SetClockSpeedAsync(4000000);

        Assert.Equal("SC:4000000:-1\n", transport.LastCommand);
    }

    [Fact]
    public async Task SetModeAsync_Does_Not_Change_The_Speed()
    {
        var transport = CreateTransport("OK");
        var spi = new ESP32SpiController(transport);

        await spi.SetModeAsync(3);

        Assert.Equal("SC:0:3\n", transport.LastCommand);   // the firmware ignores a speed of 0 or less
    }

    [Fact]
    public async Task SetModeAsync_Sends_SC_Command()
    {
        var transport = CreateTransport("OK");
        var spi = new ESP32SpiController(transport);

        await spi.SetModeAsync(2);

        Assert.StartsWith("SC:", transport.LastCommand);
        Assert.Contains("2", transport.LastCommand);
    }

    [Fact]
    public async Task SetModeAsync_Throws_On_Invalid_Mode()
    {
        var transport = CreateTransport("OK");
        var spi = new ESP32SpiController(transport);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => spi.SetModeAsync(5));
    }

    // ── Dispose ──────────────────────────────────────────────

    [Fact]
    public async Task Disposed_Throws_ObjectDisposedException()
    {
        var transport = CreateTransport("OK");
        var spi = new ESP32SpiController(transport);
        spi.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => spi.TransferAsync(5, new byte[] { 0x01 }));
    }
}
