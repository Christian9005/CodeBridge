using CodeBridge.ESP32;
using CodeBridge.Core.Tests.Mocks;
using Xunit;

namespace CodeBridge.Core.Tests.ESP32;

public class ESP32RgbLedTests
{
    // ── Init ─────────────────────────────────────────────────

    [Fact]
    public async Task InitAsync_Sets_PinModes_And_Off()
    {
        var transport = new MockTransport();
        // 3x PM + 3x PW (SetColor 0,0,0)
        for (int i = 0; i < 6; i++) transport.EnqueueResponse("OK");

        var led = new ESP32RgbLed(transport, 25, 26, 27);
        await led.InitAsync();

        Assert.True(led.IsReady);
        // First 3 commands should be PM (PinMode)
        Assert.StartsWith("PM:25:", transport.SentCommands[0]);
        Assert.StartsWith("PM:26:", transport.SentCommands[1]);
        Assert.StartsWith("PM:27:", transport.SentCommands[2]);
    }

    // ── SetColor ─────────────────────────────────────────────

    [Fact]
    public async Task SetColorAsync_Sends_PWM_Commands()
    {
        var transport = new MockTransport();
        transport.EnqueueResponse("OK"); // R
        transport.EnqueueResponse("OK"); // G
        transport.EnqueueResponse("OK"); // B

        var led = new ESP32RgbLed(transport, 25, 26, 27);
        await led.SetColorAsync(255, 128, 0);

        Assert.Contains("PW:25:255:5000", transport.SentCommands[0]);
        Assert.Contains("PW:26:128:5000", transport.SentCommands[1]);
        Assert.Contains("PW:27:0:5000", transport.SentCommands[2]);
    }

    [Fact]
    public async Task SetColorAsync_CommonAnode_Inverts_Values()
    {
        var transport = new MockTransport();
        transport.EnqueueResponse("OK");
        transport.EnqueueResponse("OK");
        transport.EnqueueResponse("OK");

        var led = new ESP32RgbLed(transport, 25, 26, 27, commonAnode: true);
        await led.SetColorAsync(255, 0, 100);

        // Inverted: 255-255=0, 255-0=255, 255-100=155
        Assert.Contains("PW:25:0:5000", transport.SentCommands[0]);
        Assert.Contains("PW:26:255:5000", transport.SentCommands[1]);
        Assert.Contains("PW:27:155:5000", transport.SentCommands[2]);
    }

    // ── Off ──────────────────────────────────────────────────

    [Fact]
    public async Task OffAsync_Sets_All_Channels_Zero()
    {
        var transport = new MockTransport();
        transport.EnqueueResponse("OK");
        transport.EnqueueResponse("OK");
        transport.EnqueueResponse("OK");

        var led = new ESP32RgbLed(transport, 25, 26, 27);
        await led.OffAsync();

        Assert.Contains("PW:25:0:5000", transport.SentCommands[0]);
        Assert.Contains("PW:26:0:5000", transport.SentCommands[1]);
        Assert.Contains("PW:27:0:5000", transport.SentCommands[2]);
    }

    // ── Properties ───────────────────────────────────────────

    [Fact]
    public void Properties_Return_Correct_Values()
    {
        var transport = new MockTransport();
        var led = new ESP32RgbLed(transport, 25, 26, 27, true);

        Assert.Equal(25, led.RedPin);
        Assert.Equal(26, led.GreenPin);
        Assert.Equal(27, led.BluePin);
        Assert.True(led.CommonAnode);
        Assert.Contains("RGB LED", led.Name);
        Assert.False(led.IsReady);
    }

    [Fact]
    public void Properties_CommonCathode_Default()
    {
        var transport = new MockTransport();
        var led = new ESP32RgbLed(transport, 1, 2, 3);

        Assert.False(led.CommonAnode);
    }

    // ── Error Handling ───────────────────────────────────────

    [Fact]
    public async Task SetColorAsync_Throws_On_Error()
    {
        var transport = new MockTransport();
        transport.EnqueueResponse("ERR:PWM_FAIL");

        var led = new ESP32RgbLed(transport, 25, 26, 27);

        await Assert.ThrowsAsync<InvalidOperationException>(() => led.SetColorAsync(255, 0, 0));
    }

    [Fact]
    public async Task InitAsync_Throws_On_Error()
    {
        var transport = new MockTransport();
        transport.EnqueueResponse("ERR:PIN_MODE_FAIL");

        var led = new ESP32RgbLed(transport, 25, 26, 27);

        await Assert.ThrowsAsync<InvalidOperationException>(() => led.InitAsync());
    }

    // ── Dispose ──────────────────────────────────────────────

    [Fact]
    public async Task Dispose_Prevents_Further_Use()
    {
        var transport = new MockTransport();
        var led = new ESP32RgbLed(transport, 25, 26, 27);
        led.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => led.SetColorAsync(255, 0, 0));
    }
}
