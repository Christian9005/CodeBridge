using CodeBridge.ESP32;
using CodeBridge.Core.Tests.Mocks;
using Xunit;

namespace CodeBridge.Core.Tests.ESP32;

public class ESP32LcdDisplayTests
{
    private MockTransport CreateTransport(string response = "OK")
    {
        var transport = new MockTransport();
        transport.EnqueueResponse(response);
        return transport;
    }

    // ── Init ─────────────────────────────────────────────────

    [Fact]
    public async Task InitAsync_Sends_LI_Command()
    {
        var transport = CreateTransport("OK");
        var lcd = new ESP32LcdDisplay(transport, 16, 2, 0x27);

        await lcd.InitAsync();

        Assert.Contains("LI:39:16:2", transport.LastCommand); // 0x27 = 39
        Assert.True(lcd.Width > 0);
    }

    [Fact]
    public async Task InitAsync_20x4_Display()
    {
        var transport = CreateTransport("OK");
        var lcd = new ESP32LcdDisplay(transport, 20, 4, 0x27);

        await lcd.InitAsync();

        Assert.Contains("LI:39:20:4", transport.LastCommand);
    }

    // ── Clear ────────────────────────────────────────────────

    [Fact]
    public async Task ClearAsync_Sends_LC_Command()
    {
        var transport = CreateTransport("OK");
        var lcd = new ESP32LcdDisplay(transport);

        await lcd.ClearAsync();

        Assert.StartsWith("LC", transport.LastCommand);
    }

    // ── WriteText ────────────────────────────────────────────

    [Fact]
    public async Task WriteTextAsync_Sends_LT_Command()
    {
        var transport = CreateTransport("OK");
        var lcd = new ESP32LcdDisplay(transport);

        await lcd.WriteTextAsync(0, 0, "Hello World!");

        Assert.Contains("LT:0:0:Hello World!", transport.LastCommand);
    }

    [Fact]
    public async Task WriteTextAsync_Second_Row()
    {
        var transport = CreateTransport("OK");
        var lcd = new ESP32LcdDisplay(transport);

        await lcd.WriteTextAsync(1, 5, "Test");

        Assert.Contains("LT:1:5:Test", transport.LastCommand);
    }

    // ── SetCursor ────────────────────────────────────────────

    [Fact]
    public async Task SetCursorAsync_Sends_LK_Command()
    {
        var transport = CreateTransport("OK");
        var lcd = new ESP32LcdDisplay(transport);

        await lcd.SetCursorAsync(1, 10);

        Assert.Contains("LK:1:10", transport.LastCommand);
    }

    // ── Backlight ────────────────────────────────────────────

    [Fact]
    public async Task SetBacklightAsync_On_Sends_LB_1()
    {
        var transport = CreateTransport("OK");
        var lcd = new ESP32LcdDisplay(transport);

        await lcd.SetBacklightAsync(true);

        Assert.Contains("LB:1", transport.LastCommand);
    }

    [Fact]
    public async Task SetBacklightAsync_Off_Sends_LB_0()
    {
        var transport = CreateTransport("OK");
        var lcd = new ESP32LcdDisplay(transport);

        await lcd.SetBacklightAsync(false);

        Assert.Contains("LB:0", transport.LastCommand);
    }

    // ── Flush (no-op) ────────────────────────────────────────

    [Fact]
    public async Task FlushAsync_Does_Not_Send_Command()
    {
        var transport = new MockTransport();
        var lcd = new ESP32LcdDisplay(transport);

        await lcd.FlushAsync(); // should not throw

        Assert.Empty(transport.SentCommands);
    }

    // ── Properties ───────────────────────────────────────────

    [Fact]
    public void Properties_Return_Correct_Values()
    {
        var transport = new MockTransport();
        var lcd = new ESP32LcdDisplay(transport, 20, 4, 0x3F);

        Assert.Equal(20, lcd.Columns);
        Assert.Equal(4, lcd.Rows);
        Assert.Equal(20, lcd.Width);
        Assert.Equal(4, lcd.Height);
        Assert.Equal(0x3F, lcd.Address);
    }

    // ── Dispose ──────────────────────────────────────────────

    [Fact]
    public async Task Dispose_Prevents_Further_Operations()
    {
        var transport = new MockTransport();
        var lcd = new ESP32LcdDisplay(transport);
        lcd.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => lcd.ClearAsync());
    }
}
