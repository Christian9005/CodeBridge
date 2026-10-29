using CodeBridge.Core.Abstractions;

namespace CodeBridge.Flow.Execution;

/// <summary>What <see cref="FlowSupervisor"/> is doing, for logs and status pages.</summary>
public enum FlowSupervisorEventKind
{
    /// <summary>Connecting to the board (attempt number in <see cref="FlowSupervisorEvent.Attempt"/>).</summary>
    Connecting,

    /// <summary>The board is connected and the flow is about to run.</summary>
    Connected,

    /// <summary>One pass of the flow finished.</summary>
    Iteration,

    /// <summary>One pass failed (a sensor did not answer, a block raised). The supervisor keeps going.</summary>
    IterationFailed,

    /// <summary>The board was lost (the connection failed, or too many passes in a row failed).</summary>
    Disconnected,

    /// <summary>Waiting <see cref="FlowSupervisorEvent.Delay"/> before connecting again.</summary>
    Waiting,

    /// <summary>The supervisor stopped because it was cancelled.</summary>
    Stopped
}

public sealed record FlowSupervisorEvent(
    FlowSupervisorEventKind Kind,
    string Message,
    int Attempt = 0,
    int Iteration = 0,
    TimeSpan Delay = default,
    Exception? Error = null);

public sealed class FlowSupervisorOptions
{
    /// <summary>Pause between two passes of the flow.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>First wait after the connection is lost; it doubles after each failed attempt.</summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A pass that fails is logged and the next one runs; this many failures in a row mean the board is lost and the connection is rebuilt.</summary>
    public int FailuresBeforeReconnect { get; init; } = 3;

    /// <summary>Called as each block of the flow starts and finishes (a live view of the flow, like the editor's).</summary>
    public FlowExecutionEventHandler? NodeEvents { get; init; }

    /// <summary>Pause after each block event, to follow a flow by eye. Zero for a service.</summary>
    public TimeSpan TraceDelay { get; init; }
}

/// <summary>
/// Keeps a flow running by itself, for as long as it is wanted: it connects to the board, runs the flow again and again, rides out a failed reading, and when
/// the board goes away (unplugged, Wi-Fi lost, power cut) it releases it and connects again with a growing pause, without anybody looking at it.
/// This is what makes a flow usable as a long-running service (a Windows service, a container, a headless PC) instead of a session in an editor.
/// </summary>
public sealed class FlowSupervisor
{
    private readonly FlowDocument _document;
    private readonly FlowBlockCatalog _catalog;
    private readonly Func<CancellationToken, Task<IBoard>> _connect;
    private readonly Func<IBoard, Task>? _release;
    private readonly FlowSupervisorOptions _options;
    private readonly Action<FlowSupervisorEvent>? _onEvent;

    public FlowSupervisor(
        FlowDocument document,
        FlowBlockCatalog catalog,
        Func<CancellationToken, Task<IBoard>> connect,
        Func<IBoard, Task>? release = null,
        FlowSupervisorOptions? options = null,
        Action<FlowSupervisorEvent>? onEvent = null)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        _release = release;
        _options = options ?? new FlowSupervisorOptions();
        _onEvent = onEvent;
    }

    /// <summary>Runs until <paramref name="ct"/> is cancelled. It never throws because of the board: it reports and tries again.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var runtime = new FlowRuntime(_catalog);
        var backoff = _options.InitialBackoff;
        var attempt = 0;
        var iteration = 0;

        while (!ct.IsCancellationRequested)
        {
            IBoard? board = null;
            try
            {
                attempt++;
                Raise(FlowSupervisorEventKind.Connecting, $"Connecting (attempt {attempt}).", attempt: attempt);
                board = await _connect(ct);
                Raise(FlowSupervisorEventKind.Connected, $"Connected to {board.Name}.", attempt: attempt);
                backoff = _options.InitialBackoff;
                attempt = 0;

                var failures = 0;
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await runtime.ExecuteAsync(_document, new FlowExecutionContext { Board = board, EventHandler = _options.NodeEvents, TraceDelay = _options.TraceDelay }, ct);
                        failures = 0;
                        iteration++;
                        Raise(FlowSupervisorEventKind.Iteration, $"Pass {iteration} finished.", iteration: iteration);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        failures++;
                        Raise(FlowSupervisorEventKind.IterationFailed, ex.Message, iteration: iteration, error: ex);
                        if (failures >= Math.Max(1, _options.FailuresBeforeReconnect) || !board.IsConnected)
                            throw new InvalidOperationException($"The board stopped answering ({failures} failed pass(es) in a row): {ex.Message}", ex);
                    }

                    await Task.Delay(_options.Interval, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Raise(FlowSupervisorEventKind.Disconnected, ex.Message, attempt: attempt, error: ex);
            }
            finally
            {
                if (board is not null && _release is not null)
                {
                    try
                    {
                        await _release(board);
                    }
                    catch (Exception)
                    {
                        // a board that is already gone cannot be released cleanly; the next attempt builds a new connection
                    }
                }
            }

            if (ct.IsCancellationRequested)
                break;

            Raise(FlowSupervisorEventKind.Waiting, $"Trying again in {backoff.TotalSeconds:0.##} s.", attempt: attempt, delay: backoff);
            try
            {
                await Task.Delay(backoff, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.MaxBackoff.Ticks));
        }

        Raise(FlowSupervisorEventKind.Stopped, "Stopped.");
    }

    private void Raise(FlowSupervisorEventKind kind, string message, int attempt = 0, int iteration = 0, TimeSpan delay = default, Exception? error = null)
    {
        try
        {
            _onEvent?.Invoke(new FlowSupervisorEvent(kind, message, attempt, iteration, delay, error));
        }
        catch (Exception)
        {
            // a broken logger must never stop the flow
        }
    }
}
