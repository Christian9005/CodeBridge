using CodeBridge.ESP32;
using CodeBridge.Core.Enums;
using CodeBridge.Core.Tests.Mocks;
using Xunit;

namespace CodeBridge.Core.Tests.ESP32;

public class ESP32InterruptControllerTests
{
    // ── Attach ───────────────────────────────────────────────

    [Fact]
    public async Task AttachInterruptAsync_Sends_GINT_Command()
    {
        var transport = new MockTransport();
        transport.EnqueueResponse("OK");
        var ctrl = new ESP32InterruptController(transport);

        await ctrl.AttachInterruptAsync(4, InterruptEdge.Rising);

        Assert.StartsWith("GINT:4:1", transport.LastCommand);
    }

    [Fact]
    public async Task AttachInterruptAsync_Falling_Edge()
    {
        var transport = new MockTransport();
        transport.EnqueueResponse("OK");
        var ctrl = new ESP32InterruptController(transport);

        await ctrl.AttachInterruptAsync(15, InterruptEdge.Falling);

        Assert.StartsWith("GINT:15:2", transport.LastCommand);
    }

    [Fact]
    public async Task AttachInterruptAsync_Change_Edge()
    {
        var transport = new MockTransport();
        transport.EnqueueResponse("OK");
        var ctrl = new ESP32InterruptController(transport);

        await ctrl.AttachInterruptAsync(2, InterruptEdge.Change);

        Assert.StartsWith("GINT:2:3", transport.LastCommand);
    }

    [Fact]
    public async Task AttachInterruptAsync_Throws_On_Error()
    {
        var transport = new MockTransport();
        transport.EnqueueResponse("ERR:Max interrupts reached");
        var ctrl = new ESP32InterruptController(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ctrl.AttachInterruptAsync(4, InterruptEdge.Rising));
    }

    // ── Detach ───────────────────────────────────────────────

    [Fact]
    public async Task DetachInterruptAsync_Sends_GINTD_Command()
    {
        var transport = new MockTransport();
        transport.EnqueueResponse("OK");
        var ctrl = new ESP32InterruptController(transport);

        await ctrl.DetachInterruptAsync(4);

        Assert.StartsWith("GINTD:4", transport.LastCommand);
    }

    [Fact]
    public async Task DetachInterruptAsync_Throws_On_Error()
    {
        var transport = new MockTransport();
        transport.EnqueueResponse("ERR:Pin not attached");
        var ctrl = new ESP32InterruptController(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ctrl.DetachInterruptAsync(4));
    }

    // ── Poll ─────────────────────────────────────────────────

    [Fact]
    public async Task PollAsync_Returns_Empty_When_None()
    {
        var transport = new MockTransport();
        transport.EnqueueResponse("OK:NONE");
        var ctrl = new ESP32InterruptController(transport);

        var events = await ctrl.PollAsync();

        Assert.Empty(events);
        Assert.StartsWith("GINTP", transport.LastCommand);
    }

    [Fact]
    public async Task PollAsync_Parses_Single_Event()
    {
        var transport = new MockTransport();
        transport.EnqueueResponse("OK:4:5:1");
        var ctrl = new ESP32InterruptController(transport);

        var events = await ctrl.PollAsync();

        Assert.Single(events);
        Assert.Equal(4, events[0].Pin);
        Assert.Equal(5u, events[0].Count);
        Assert.Equal(InterruptEdge.Rising, events[0].Edge);
    }

    [Fact]
    public async Task PollAsync_Parses_Multiple_Events()
    {
        var transport = new MockTransport();
        transport.EnqueueResponse("OK:4:3:1,15:7:2");
        var ctrl = new ESP32InterruptController(transport);

        var events = await ctrl.PollAsync();

        Assert.Equal(2, events.Count);
        Assert.Equal(4, events[0].Pin);
        Assert.Equal(3u, events[0].Count);
        Assert.Equal(15, events[1].Pin);
        Assert.Equal(7u, events[1].Count);
        Assert.Equal(InterruptEdge.Falling, events[1].Edge);
    }

    // ── Dispose ──────────────────────────────────────────────

    [Fact]
    public async Task Dispose_Prevents_Further_Use()
    {
        var transport = new MockTransport();
        var ctrl = new ESP32InterruptController(transport);
        ctrl.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => ctrl.AttachInterruptAsync(4, InterruptEdge.Rising));
    }
}
