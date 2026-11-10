using CodeBridge.ESP32;
using CodeBridge.Core.Tests.Mocks;
using Xunit;

namespace CodeBridge.Core.Tests.ESP32;

public class ESP32RelayTests
{
    private MockTransport CreateTransport(params string[] responses)
    {
        var transport = new MockTransport();
        transport.EnqueueResponses(responses);
        return transport;
    }

    // ── Init ─────────────────────────────────────────────────

    [Fact]
    public async Task InitAsync_Sets_PinMode_And_Off()
    {
        var transport = CreateTransport("OK", "OK"); // pinMode + digitalWrite
        var relay = new ESP32Relay(transport, 4);

        await relay.InitAsync();

        Assert.True(relay.IsReady);
        Assert.False(relay.IsOn);
        Assert.Equal(2, transport.SentCommands.Count);
        Assert.Contains("PM:4:1", transport.SentCommands[0]); // OUTPUT
        Assert.Contains("DW:4:1", transport.SentCommands[1]); // HIGH = OFF for active-low
    }

    [Fact]
    public async Task InitAsync_ActiveHigh_Starts_Low()
    {
        var transport = CreateTransport("OK", "OK");
        var relay = new ESP32Relay(transport, 4, activeLow: false);

        await relay.InitAsync();

        Assert.Contains("DW:4:0", transport.SentCommands[1]); // LOW = OFF for active-high
    }

    // ── On / Off ─────────────────────────────────────────────

    [Fact]
    public async Task OnAsync_ActiveLow_Sends_Low()
    {
        var transport = CreateTransport("OK");
        var relay = new ESP32Relay(transport, 4);

        await relay.OnAsync();

        Assert.Contains("DW:4:0", transport.LastCommand); // LOW = ON for active-low
        Assert.True(relay.IsOn);
    }

    [Fact]
    public async Task OffAsync_ActiveLow_Sends_High()
    {
        var transport = CreateTransport("OK");
        var relay = new ESP32Relay(transport, 4);

        await relay.OffAsync();

        Assert.Contains("DW:4:1", transport.LastCommand); // HIGH = OFF for active-low
        Assert.False(relay.IsOn);
    }

    [Fact]
    public async Task OnAsync_ActiveHigh_Sends_High()
    {
        var transport = CreateTransport("OK");
        var relay = new ESP32Relay(transport, 4, activeLow: false);

        await relay.OnAsync();

        Assert.Contains("DW:4:1", transport.LastCommand);
        Assert.True(relay.IsOn);
    }

    // ── Toggle ───────────────────────────────────────────────

    [Fact]
    public async Task ToggleAsync_Turns_On_When_Off()
    {
        var transport = CreateTransport("OK");
        var relay = new ESP32Relay(transport, 4);

        await relay.ToggleAsync(); // OFF → ON

        Assert.True(relay.IsOn);
    }

    [Fact]
    public async Task ToggleAsync_Turns_Off_When_On()
    {
        var transport = CreateTransport("OK", "OK");
        var relay = new ESP32Relay(transport, 4);

        await relay.OnAsync();
        await relay.ToggleAsync(); // ON → OFF

        Assert.False(relay.IsOn);
    }

    // ── Properties ───────────────────────────────────────────

    [Fact]
    public void Properties_Return_Correct_Values()
    {
        var transport = new MockTransport();
        var relay = new ESP32Relay(transport, 12, activeLow: false);

        Assert.Equal(12, relay.Pin);
        Assert.Equal("Relay (pin 12)", relay.Name);
        Assert.False(relay.ActiveLow);
    }

    // ── Dispose ──────────────────────────────────────────────

    [Fact]
    public async Task Dispose_Prevents_Further_Operations()
    {
        var transport = new MockTransport();
        var relay = new ESP32Relay(transport, 4);
        relay.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => relay.OnAsync());
    }
}
