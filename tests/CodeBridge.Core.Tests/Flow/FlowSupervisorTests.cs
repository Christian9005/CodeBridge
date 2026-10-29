using System.Collections.Concurrent;
using CodeBridge.Core.Abstractions;
using CodeBridge.ESP32;
using CodeBridge.Flow;
using CodeBridge.Flow.Execution;
using CodeBridge.Transport.Simulation;

namespace CodeBridge.Core.Tests.Flow;

/// <summary>A flow that has to keep running by itself: it rides out failed readings and builds the connection again when the board goes away.</summary>
public class FlowSupervisorTests
{
    private static readonly FlowSupervisorOptions Fast = new()
    {
        Interval = TimeSpan.FromMilliseconds(5),
        InitialBackoff = TimeSpan.FromMilliseconds(10),
        MaxBackoff = TimeSpan.FromMilliseconds(40),
        FailuresBeforeReconnect = 3
    };

    /// <summary>Manual trigger -> Digital Write on GPIO 2: every pass sends one command to the board.</summary>
    private static FlowDocument Blinker()
    {
        var document = new FlowDocument { BoardId = "esp32-devkit", Name = "Blinker" };
        document.Nodes.Add(new FlowNode { Id = "start", Type = BuiltInBlockCatalog.ManualTrigger });
        document.Nodes.Add(new FlowNode { Id = "led", Type = BuiltInBlockCatalog.GpioSetOutput, Parameters = { ["pin"] = 2, ["value"] = true } });
        document.Connections.Add(new FlowConnection { Id = "c", FromNodeId = "start", FromPort = "trigger", ToNodeId = "led", ToPort = "trigger" });
        return document;
    }

    private static FlowBlockCatalog Catalog => BuiltInBlockCatalog.Create(BuiltInBoardProfiles.Esp32DevKit);

    private sealed class Rig
    {
        public readonly List<SimulatedTransport> Transports = new();
        public readonly ConcurrentQueue<FlowSupervisorEvent> Events = new();
        public int Released;
        public int FailNextConnects;

        public async Task<IBoard> ConnectAsync(CancellationToken ct)
        {
            if (Interlocked.Decrement(ref FailNextConnects) >= 0)
                throw new IOException("No answer from the board.");

            var transport = new SimulatedTransport(new SimulatedBoardOptions());
            Transports.Add(transport);
            var board = new ESP32Board(transport, 39);
            await board.ConnectAsync(ct);
            return board;
        }

        public Task ReleaseAsync(IBoard board)
        {
            Interlocked.Increment(ref Released);
            return Task.CompletedTask;
        }

        public int Count(FlowSupervisorEventKind kind) => Events.Count(e => e.Kind == kind);

        public FlowSupervisor Supervisor(FlowSupervisorOptions? options = null) =>
            new(Blinker(), Catalog, ConnectAsync, ReleaseAsync, options ?? Fast, Events.Enqueue);
    }

    private static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            if (condition())
                return true;

            await Task.Delay(5);
        }

        return condition();
    }

    [Fact]
    public async Task It_runs_the_flow_again_and_again_until_it_is_stopped()
    {
        var rig = new Rig();
        using var stop = new CancellationTokenSource();
        var running = rig.Supervisor().RunAsync(stop.Token);

        Assert.True(await UntilAsync(() => rig.Count(FlowSupervisorEventKind.Iteration) >= 5));
        stop.Cancel();
        await running;

        Assert.Single(rig.Transports);
        Assert.Contains(rig.Transports[0].CommandLog, c => c.StartsWith("DW:2"));
        Assert.Equal(1, rig.Released);
        Assert.Equal(FlowSupervisorEventKind.Stopped, rig.Events.Last().Kind);
    }

    [Fact]
    public async Task When_the_board_goes_away_it_connects_again_by_itself()
    {
        var rig = new Rig();
        using var stop = new CancellationTokenSource();
        var running = rig.Supervisor().RunAsync(stop.Token);
        Assert.True(await UntilAsync(() => rig.Count(FlowSupervisorEventKind.Iteration) >= 2));

        await rig.Transports[0].DisconnectAsync();   // the cable is pulled

        Assert.True(await UntilAsync(() => rig.Transports.Count == 2 && rig.Events.Count(e => e.Kind == FlowSupervisorEventKind.Iteration && e.Iteration > 2) > 2));
        stop.Cancel();
        await running;

        Assert.Equal(1, rig.Count(FlowSupervisorEventKind.Disconnected));
        Assert.Equal(2, rig.Count(FlowSupervisorEventKind.Connected));
        Assert.Equal(2, rig.Released);
        Assert.Contains(rig.Transports[1].CommandLog, c => c.StartsWith("DW:2"));
    }

    [Fact]
    public async Task A_board_that_is_not_there_yet_is_retried_with_a_growing_pause()
    {
        var rig = new Rig { FailNextConnects = 3 };
        using var stop = new CancellationTokenSource();
        var running = rig.Supervisor().RunAsync(stop.Token);

        Assert.True(await UntilAsync(() => rig.Count(FlowSupervisorEventKind.Iteration) >= 1));
        stop.Cancel();
        await running;

        var pauses = rig.Events.Where(e => e.Kind == FlowSupervisorEventKind.Waiting).Select(e => e.Delay.TotalMilliseconds).ToList();
        Assert.Equal(new[] { 10.0, 20.0, 40.0 }, pauses);
        Assert.Equal(3, rig.Count(FlowSupervisorEventKind.Disconnected));
    }

    [Fact]
    public async Task The_pause_never_grows_past_its_limit_and_starts_over_after_a_good_connection()
    {
        var rig = new Rig { FailNextConnects = 6 };
        using var stop = new CancellationTokenSource();
        var running = rig.Supervisor().RunAsync(stop.Token);

        Assert.True(await UntilAsync(() => rig.Count(FlowSupervisorEventKind.Iteration) >= 1));
        Assert.Equal(40, rig.Events.Where(e => e.Kind == FlowSupervisorEventKind.Waiting).Max(e => e.Delay.TotalMilliseconds));

        await rig.Transports[0].DisconnectAsync();
        Assert.True(await UntilAsync(() => rig.Transports.Count == 2));
        stop.Cancel();
        await running;

        Assert.Equal(10, rig.Events.Where(e => e.Kind == FlowSupervisorEventKind.Waiting).Last().Delay.TotalMilliseconds);
    }

    [Fact]
    public async Task One_failed_pass_does_not_drop_the_connection()
    {
        var rig = new Rig();
        var document = Blinker();
        document.Nodes.Add(new FlowNode { Id = "six", Type = BuiltInBlockCatalog.ConstantNumber, Parameters = { ["value"] = 6 } });
        var calc = new FlowNode { Id = "calc", Type = "math.calc", Parameters = { ["operation"] = "divide", ["b"] = 2 } };
        document.Nodes.Add(calc);
        document.Connections.Add(new FlowConnection { Id = "a", FromNodeId = "six", FromPort = "value", ToNodeId = "calc", ToPort = "a" });
        using var stop = new CancellationTokenSource();
        var supervisor = new FlowSupervisor(document, Catalog, rig.ConnectAsync, rig.ReleaseAsync, Fast, e =>
        {
            rig.Events.Enqueue(e);
            // the third pass divides by zero (a reading that came out wrong); the next one is fine again
            if (e.Kind == FlowSupervisorEventKind.Iteration && e.Iteration == 2)
                calc.Parameters["b"] = 0;
            else if (e.Kind == FlowSupervisorEventKind.IterationFailed)
                calc.Parameters["b"] = 2;
        });
        var running = supervisor.RunAsync(stop.Token);

        Assert.True(await UntilAsync(() => rig.Count(FlowSupervisorEventKind.Iteration) >= 6));
        stop.Cancel();
        await running;

        Assert.Equal(1, rig.Count(FlowSupervisorEventKind.IterationFailed));
        Assert.Equal(0, rig.Count(FlowSupervisorEventKind.Disconnected));
        Assert.Single(rig.Transports);
    }

    [Fact]
    public async Task A_flow_that_keeps_failing_makes_it_connect_again()
    {
        var rig = new Rig();
        var document = Blinker();
        document.Nodes.Add(new FlowNode { Id = "six", Type = BuiltInBlockCatalog.ConstantNumber, Parameters = { ["value"] = 6 } });
        document.Nodes.Add(new FlowNode { Id = "calc", Type = "math.calc", Parameters = { ["operation"] = "divide", ["b"] = 0 } });
        document.Connections.Add(new FlowConnection { Id = "a", FromNodeId = "six", FromPort = "value", ToNodeId = "calc", ToPort = "a" });
        using var stop = new CancellationTokenSource();
        var running = new FlowSupervisor(document, Catalog, rig.ConnectAsync, rig.ReleaseAsync, Fast, rig.Events.Enqueue).RunAsync(stop.Token);

        Assert.True(await UntilAsync(() => rig.Transports.Count >= 2));
        stop.Cancel();
        await running;

        Assert.True(rig.Count(FlowSupervisorEventKind.Disconnected) >= 1);
        Assert.True(rig.Count(FlowSupervisorEventKind.IterationFailed) >= 3);
    }

    [Fact]
    public async Task Stopping_while_it_waits_to_reconnect_is_immediate_and_releases_everything()
    {
        var rig = new Rig { FailNextConnects = 100 };
        using var stop = new CancellationTokenSource();
        var slow = new FlowSupervisorOptions { InitialBackoff = TimeSpan.FromSeconds(30), MaxBackoff = TimeSpan.FromSeconds(30), Interval = TimeSpan.FromMilliseconds(5) };
        var running = rig.Supervisor(slow).RunAsync(stop.Token);
        Assert.True(await UntilAsync(() => rig.Count(FlowSupervisorEventKind.Waiting) >= 1));

        stop.Cancel();
        var finished = await Task.WhenAny(running, Task.Delay(2000));

        Assert.Same(running, finished);
        Assert.Equal(FlowSupervisorEventKind.Stopped, rig.Events.Last().Kind);
    }

    [Fact]
    public async Task A_broken_logger_never_stops_the_flow()
    {
        var rig = new Rig();
        using var stop = new CancellationTokenSource();
        var count = 0;
        var supervisor = new FlowSupervisor(Blinker(), Catalog, rig.ConnectAsync, rig.ReleaseAsync, Fast, _ =>
        {
            Interlocked.Increment(ref count);
            throw new InvalidOperationException("the log disk is full");
        });
        var running = supervisor.RunAsync(stop.Token);

        Assert.True(await UntilAsync(() => rig.Transports.Count == 1 && rig.Transports[0].CommandLog.Count(c => c.StartsWith("DW:2")) >= 3));
        stop.Cancel();
        await running;

        Assert.True(count > 3);
    }
}
