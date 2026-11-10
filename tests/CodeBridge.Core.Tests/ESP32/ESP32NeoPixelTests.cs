using CodeBridge.Core.Protocol;
using CodeBridge.ESP32;
using CodeBridge.Core.Tests.Mocks;
using Xunit;

namespace CodeBridge.Core.Tests.ESP32;

public class ESP32NeoPixelTests
{
    private MockTransport CreateTransport(string response = "OK")
    {
        var transport = new MockTransport();
        transport.EnqueueResponse(response);
        return transport;
    }

    // ── Init ─────────────────────────────────────────────────

    [Fact]
    public async Task InitAsync_Sends_NI_Command()
    {
        var transport = CreateTransport("OK");
        var neo = new ESP32NeoPixel(transport, 16, 30);

        await neo.InitAsync();

        // NI:pin:count, the order the firmware reads. It used to be sent the other way round, so a strip of 8 on pin 5 reached the board as pin 8 (a flash pin).
        Assert.Contains("NI:16:30", transport.LastCommand);
        Assert.True(neo.IsReady);
    }

    [Fact]
    public void Constructor_Throws_For_Zero_LedCount()
    {
        var transport = new MockTransport();
        Assert.Throws<ArgumentOutOfRangeException>(() => new ESP32NeoPixel(transport, 16, 0));
    }

    // ── SetPixel ─────────────────────────────────────────────

    [Fact]
    public async Task SetPixelAsync_Sends_NS_Command()
    {
        var transport = CreateTransport("OK");
        var neo = new ESP32NeoPixel(transport, 16, 8);

        await neo.SetPixelAsync(3, 255, 0, 128);

        Assert.Contains("NS:3:255:0:128", transport.LastCommand);
    }

    [Fact]
    public async Task SetPixelAsync_Throws_For_Invalid_Index()
    {
        var transport = new MockTransport();
        var neo = new ESP32NeoPixel(transport, 16, 8);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => neo.SetPixelAsync(8, 0, 0, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => neo.SetPixelAsync(-1, 0, 0, 0));
    }

    // ── SetAll ───────────────────────────────────────────────

    [Fact]
    public async Task SetAllAsync_Sends_NA_Command()
    {
        var transport = CreateTransport("OK");
        var neo = new ESP32NeoPixel(transport, 16, 8);

        await neo.SetAllAsync(0, 255, 0);

        Assert.Contains("NA:0:255:0", transport.LastCommand);
    }

    // ── Clear ────────────────────────────────────────────────

    [Fact]
    public async Task ClearAsync_Sends_NC_Command()
    {
        var transport = CreateTransport("OK");
        var neo = new ESP32NeoPixel(transport, 16, 8);

        await neo.ClearAsync();

        Assert.StartsWith("NC", transport.LastCommand);
    }

    // ── Show ─────────────────────────────────────────────────

    [Fact]
    public async Task ShowAsync_Sends_NH_Command()
    {
        var transport = CreateTransport("OK");
        var neo = new ESP32NeoPixel(transport, 16, 8);

        await neo.ShowAsync();

        Assert.StartsWith("NH", transport.LastCommand);
    }

    // ── Brightness ───────────────────────────────────────────

    [Fact]
    public async Task SetBrightnessAsync_Sends_NB_Command()
    {
        var transport = CreateTransport("OK");
        var neo = new ESP32NeoPixel(transport, 16, 8);

        await neo.SetBrightnessAsync(128);

        Assert.Contains("NB:128", transport.LastCommand);
    }

    // ── SetRange ─────────────────────────────────────────────

    [Fact]
    public async Task SetRangeAsync_Sends_NR_With_HexColors()
    {
        var transport = CreateTransport("OK");
        var neo = new ESP32NeoPixel(transport, 16, 8);

        var colors = new (byte, byte, byte)[] { (255, 0, 0), (0, 255, 0) };
        await neo.SetRangeAsync(2, colors);

        // NR:start:r:g:b:r:g:b, the format the firmware reads
        Assert.Contains("NR:2:255:0:0:0:255:0", transport.LastCommand);
    }

    [Fact]
    public async Task SetRangeAsync_Splits_A_Long_Range_Into_Lines_The_Firmware_Can_Hold()
    {
        var transport = CreateTransport("OK");
        for (var i = 0; i < 3; i++)
            transport.EnqueueResponse("OK");   // one answer for each of the four commands
        var neo = new ESP32NeoPixel(transport, 16, 100);

        await neo.SetRangeAsync(0, Enumerable.Repeat(((byte)255, (byte)255, (byte)255), 100).ToArray());

        var commands = transport.SentCommands.Where(c => c.StartsWith("NR:", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, commands.Count);                          // 32 + 32 + 32 + 4 pixels
        Assert.StartsWith("NR:0:", commands[0]);
        Assert.StartsWith("NR:32:", commands[1]);
        Assert.StartsWith("NR:96:", commands[3]);
        Assert.All(commands, c => Assert.True(c.Length < 400, "a line longer than the firmware's buffer of 512 would be refused"));
    }

    [Fact]
    public async Task SetRangeAsync_Throws_For_Overflow()
    {
        var transport = new MockTransport();
        var neo = new ESP32NeoPixel(transport, 16, 4);

        var colors = new (byte, byte, byte)[] { (255, 0, 0), (0, 255, 0), (0, 0, 255) };
        // Start at index 2 with 3 colors = 5, but only 4 LEDs
        await Assert.ThrowsAsync<ArgumentException>(() => neo.SetRangeAsync(2, colors));
    }

    // ── Properties ───────────────────────────────────────────

    [Fact]
    public void Properties_Return_Correct_Values()
    {
        var transport = new MockTransport();
        var neo = new ESP32NeoPixel(transport, 5, 60);

        Assert.Equal(5, neo.Pin);
        Assert.Equal(60, neo.LedCount);
        Assert.Equal("NeoPixel (60 LEDs, pin 5)", neo.Name);
    }

    // ── Dispose ──────────────────────────────────────────────

    [Fact]
    public async Task Dispose_Prevents_Further_Operations()
    {
        var transport = new MockTransport();
        var neo = new ESP32NeoPixel(transport, 16, 8);
        neo.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => neo.SetPixelAsync(0, 0, 0, 0));
    }
}
