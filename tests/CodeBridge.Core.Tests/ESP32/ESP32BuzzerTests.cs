using CodeBridge.Core.Protocol;
using CodeBridge.ESP32;
using CodeBridge.Core.Tests.Mocks;
using Xunit;

namespace CodeBridge.Core.Tests.ESP32;

public class ESP32BuzzerTests
{
    private MockTransport CreateTransport(string response = "OK")
    {
        var transport = new MockTransport();
        transport.EnqueueResponse(response);
        return transport;
    }

    // ── Tone ─────────────────────────────────────────────────

    [Fact]
    public async Task ToneAsync_Sends_TN_Command()
    {
        var transport = CreateTransport("OK");
        var buzzer = new ESP32Buzzer(transport, 25);

        await buzzer.ToneAsync(1000, TimeSpan.FromMilliseconds(500));

        Assert.Contains("TN:25:1000:500", transport.LastCommand);
    }

    [Fact]
    public async Task ToneAsync_Throws_For_Invalid_Frequency()
    {
        var transport = new MockTransport();
        var buzzer = new ESP32Buzzer(transport, 25);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => buzzer.ToneAsync(0, TimeSpan.FromMilliseconds(100)));
    }

    // ── NoTone ───────────────────────────────────────────────

    [Fact]
    public async Task NoToneAsync_Sends_NT_Command()
    {
        var transport = CreateTransport("OK");
        var buzzer = new ESP32Buzzer(transport, 25);

        await buzzer.NoToneAsync();

        Assert.StartsWith("NT:25", transport.LastCommand);
    }

    // ── Beep ────────────────────────────────────────────────

    [Fact]
    public async Task BeepAsync_Sends_1000Hz_100ms_Tone()
    {
        var transport = CreateTransport("OK");
        var buzzer = new ESP32Buzzer(transport, 25);

        await buzzer.BeepAsync();

        Assert.Contains("TN:25:1000:100", transport.LastCommand);
    }

    // ── Properties ──────────────────────────────────────────

    [Fact]
    public void Properties_Return_Correct_Values()
    {
        var transport = new MockTransport();
        var buzzer = new ESP32Buzzer(transport, 33);

        Assert.Equal(33, buzzer.Pin);
        Assert.Equal("Buzzer (pin 33)", buzzer.Name);
        Assert.True(buzzer.IsReady);
    }

    // ── Dispose ──────────────────────────────────────────────

    [Fact]
    public async Task Dispose_Prevents_Further_Operations()
    {
        var transport = new MockTransport();
        var buzzer = new ESP32Buzzer(transport, 25);
        buzzer.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => buzzer.ToneAsync(440, TimeSpan.FromSeconds(1)));
    }
}
