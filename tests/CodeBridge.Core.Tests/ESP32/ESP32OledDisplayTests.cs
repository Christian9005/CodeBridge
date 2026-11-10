using CodeBridge.ESP32;
using CodeBridge.Core.Tests.Mocks;
using Xunit;

namespace CodeBridge.Core.Tests.ESP32;

public class ESP32OledDisplayTests
{
    private MockTransport CreateTransport(string response = "OK")
    {
        var transport = new MockTransport();
        transport.EnqueueResponse(response);
        return transport;
    }

    // ── Init ─────────────────────────────────────────────────

    [Fact]
    public async Task InitAsync_Sends_OI_Command()
    {
        var transport = CreateTransport("OK");
        var oled = new ESP32OledDisplay(transport, 128, 64, 0x3C);

        await oled.InitAsync();

        Assert.Contains("OI:128:64:60", transport.LastCommand); // 0x3C = 60
    }

    [Fact]
    public async Task InitAsync_Throws_On_Error()
    {
        var transport = CreateTransport("ERR:OLED init failed");
        var oled = new ESP32OledDisplay(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() => oled.InitAsync());
    }

    // ── Clear ────────────────────────────────────────────────

    [Fact]
    public async Task ClearAsync_Sends_OC_Command()
    {
        var transport = CreateTransport("OK");
        var oled = new ESP32OledDisplay(transport);

        await oled.ClearAsync();

        Assert.StartsWith("OC", transport.LastCommand);
    }

    // ── Flush ────────────────────────────────────────────────

    [Fact]
    public async Task FlushAsync_Sends_OF_Command()
    {
        var transport = CreateTransport("OK");
        var oled = new ESP32OledDisplay(transport);

        await oled.FlushAsync();

        Assert.StartsWith("OF", transport.LastCommand);
    }

    // ── DrawText ─────────────────────────────────────────────

    [Fact]
    public async Task DrawTextAsync_Sends_OT_Command()
    {
        var transport = CreateTransport("OK");
        var oled = new ESP32OledDisplay(transport);

        await oled.DrawTextAsync(10, 20, "Hello", 1, 2);

        Assert.Contains("OT:10:20:2:Hello", transport.LastCommand);
    }

    // ── DrawPixel ────────────────────────────────────────────

    [Fact]
    public async Task DrawPixelAsync_Sends_OP_Command()
    {
        var transport = CreateTransport("OK");
        var oled = new ESP32OledDisplay(transport);

        await oled.DrawPixelAsync(5, 10, 1);

        Assert.Contains("OP:5:10:1", transport.LastCommand);
    }

    // ── DrawLine ─────────────────────────────────────────────

    [Fact]
    public async Task DrawLineAsync_Sends_OL_Command()
    {
        var transport = CreateTransport("OK");
        var oled = new ESP32OledDisplay(transport);

        await oled.DrawLineAsync(0, 0, 127, 63, 1);

        Assert.Contains("OL:0:0:127:63:1", transport.LastCommand);
    }

    // ── DrawRect ─────────────────────────────────────────────

    [Fact]
    public async Task DrawRectAsync_Sends_OR_With_NoFill()
    {
        var transport = CreateTransport("OK");
        var oled = new ESP32OledDisplay(transport);

        await oled.DrawRectAsync(10, 10, 50, 30, 1);

        Assert.Contains("OR:10:10:50:30:1:0", transport.LastCommand);
    }

    [Fact]
    public async Task FillRectAsync_Sends_OR_With_Fill()
    {
        var transport = CreateTransport("OK");
        var oled = new ESP32OledDisplay(transport);

        await oled.FillRectAsync(10, 10, 50, 30, 1);

        Assert.Contains("OR:10:10:50:30:1:1", transport.LastCommand);
    }

    // ── DrawCircle ───────────────────────────────────────────

    [Fact]
    public async Task DrawCircleAsync_Sends_OE_Command()
    {
        var transport = CreateTransport("OK");
        var oled = new ESP32OledDisplay(transport);

        await oled.DrawCircleAsync(64, 32, 20, 1);

        Assert.Contains("OE:64:32:20:1", transport.LastCommand);
    }

    // ── Brightness ───────────────────────────────────────────

    [Fact]
    public async Task SetBrightnessAsync_Sends_OB_Command()
    {
        var transport = CreateTransport("OK");
        var oled = new ESP32OledDisplay(transport);

        await oled.SetBrightnessAsync(128);

        Assert.Contains("OB:128", transport.LastCommand);
    }

    // ── Properties ───────────────────────────────────────────

    [Fact]
    public void Properties_Return_Correct_Values()
    {
        var transport = new MockTransport();
        var oled = new ESP32OledDisplay(transport, 128, 32, 0x3D);

        Assert.Equal(128, oled.Width);
        Assert.Equal(32, oled.Height);
        Assert.Equal(0x3D, oled.Address);
        Assert.Equal(1, oled.ColorDepth);
    }

    // ── Dispose ──────────────────────────────────────────────

    [Fact]
    public async Task Dispose_Prevents_Further_Operations()
    {
        var transport = new MockTransport();
        var oled = new ESP32OledDisplay(transport);
        oled.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => oled.ClearAsync());
    }
}
