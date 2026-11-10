using System.Text;
using CodeBridge.Core.Abstractions;
using CodeBridge.Transport;
using CodeBridge.Transport.Serial;

namespace CodeBridge.Matrix.Tests;

/// <summary>Runs only when a board is plugged in: set CODEBRIDGE_HARDWARE_PORT (for example COM3). Otherwise it is skipped, so the build does not need a board.</summary>
public sealed class HardwareFactAttribute : FactAttribute
{
    public HardwareFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEBRIDGE_HARDWARE_PORT")))
            Skip = "No board: set CODEBRIDGE_HARDWARE_PORT to the serial port of an ESP32 running the CodeBridge firmware to run this test.";
    }
}

/// <summary>
/// The matrix on the real thing: every block the board offers is run through the flow runtime on the board plugged into the serial port, with nothing wired to it.
/// A block that reads a sensor then has to stop with a clear message, not hang or crash, and after each block the board has to answer PING.
///
/// It drives pins for real (a servo on GPIO 13, a stepper, a relay, a tone): unplug anything that must not move before running it.
/// CODEBRIDGE_HARDWARE_BOARD names the profile (default esp32-devkit); CODEBRIDGE_HARDWARE_REPORT is a file the results are written to.
/// </summary>
public sealed class HardwareMatrixTests
{
    [HardwareFact]
    public async Task Every_block_ends_cleanly_on_the_real_board_and_the_board_keeps_answering()
    {
        var port = Environment.GetEnvironmentVariable("CODEBRIDGE_HARDWARE_PORT")!;
        var boardId = Environment.GetEnvironmentVariable("CODEBRIDGE_HARDWARE_BOARD") ?? "esp32-devkit";
        var profile = Matrix.Boards.Single(b => b.Id == boardId);

        var log = new List<string>();
        using var serial = new SerialTransport(port);
        using var tap = new CommandTapTransport(serial, (command, response) => { lock (log) log.Add(command + " -> " + response); });
        var board = new CodeBridge.ESP32.ESP32Board(tap, profile.MaxPin);
        await board.ConnectAsync();

        var firmware = board.FirmwareVersion;
        var cells = Matrix.Cells().Where(c => c.Board.Id == boardId).ToList();
        var report = new StringBuilder();
        report.AppendLine($"Board: {profile.DisplayName} on {port}, firmware {firmware}, {cells.Count} blocks, nothing wired to it.");
        report.AppendLine();
        report.AppendLine("| Block | Result | What the board or the block said | Commands |");
        report.AppendLine("|---|---|---|---|");

        var problems = new List<string>();
        foreach (var cell in cells)
        {
            Matrix.Validate(cell);
            if (!cell.Valid)
            {
                problems.Add($"{cell}: invalid: {string.Join(" ; ", cell.Errors)}");
                continue;
            }

            int before;
            lock (log) before = log.Count;
            await Matrix.ExecuteAsync(cell, board, () => { lock (log) return log.Skip(before).ToList(); }, TimeSpan.FromSeconds(4));

            var (alive, answer) = await AliveAsync(tap);
            var run = cell.Run!;
            var said = run.Kind == RunKind.Completed
                ? (run.Detail.Length > 0 ? run.Detail : "ran to the end")
                : run.Detail;
            report.AppendLine($"| `{cell.Block.Type}` | {Describe(run.Kind)} | {said.Replace("|", "/")} | {run.Commands.Count} |");

            if (run.Kind == RunKind.Failed)
                problems.Add($"{cell}: {run.Detail}");
            if (!alive)
            {
                string tail;
                lock (log) tail = string.Join(" | ", log.TakeLast(8));
                problems.Add($"{cell}: the board did not answer PING after this block ({answer}). Last commands: {tail}");
                break;
            }
        }

        var path = Environment.GetEnvironmentVariable("CODEBRIDGE_HARDWARE_REPORT");
        if (!string.IsNullOrWhiteSpace(path))
            File.WriteAllText(path, report.ToString());

        Assert.True(problems.Count == 0, $"{problems.Count} problem(s) on the real board:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    private static string Describe(RunKind kind) => kind switch
    {
        RunKind.Completed => "ran",
        RunKind.NothingAttached => "needs the sensor",
        _ => "FAILED"
    };

    private static async Task<(bool Alive, string Answer)> AliveAsync(ITransport transport)
    {
        var answers = new List<string>();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var answer = await transport.SendCommandAsync(CodeBridge.Core.Protocol.BridgeProtocol.BuildCommand(CodeBridge.Core.Protocol.BridgeProtocol.CMD_PING), limit.Token);
                if (answer.Contains("PONG", StringComparison.Ordinal))
                    return (true, answer);

                answers.Add($"attempt {attempt}: {answer} after {watch.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                answers.Add($"attempt {attempt}: {ex.GetType().Name} after {watch.ElapsedMilliseconds} ms");
            }

            await Task.Delay(1000);
        }

        return (false, string.Join("; ", answers));
    }
}
