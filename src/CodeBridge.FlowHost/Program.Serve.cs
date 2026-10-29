using System.Text.Json;
using CodeBridge.Designer.WinForms.Hardware;
using CodeBridge.Flow;
using CodeBridge.Flow.Execution;
using CodeBridge.Flow.Serialization;
using CodeBridge.Flow.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CodeBridge.FlowHost;

// A flow that keeps running with nobody at the editor: `serve` connects, runs the flow again and again, and connects again by itself when the board goes
// away. With --service it is a Windows service (sc.exe create), with a log file because a service has no console.
internal static partial class Program
{
    private const long MaxLogBytes = 5 * 1024 * 1024;

    private static async Task<int> ServeAsync(Options options, CancellationTokenSource cancellation)
    {
        // A service starts in System32: everything is resolved against absolute paths.
        var flowPath = Path.GetFullPath(options.Get("flow") ?? throw new InvalidOperationException("Missing --flow <file.cbflow>."));
        var document = FlowDocumentJson.Deserialize(File.ReadAllText(flowPath));
        var profile = BuiltInBoardProfiles.FindById(options.Get("board") ?? document.BoardId) ?? BuiltInBoardProfiles.Default;
        var catalog = BuiltInBlockCatalog.Create(profile);
        var logPath = options.Get("log") is { Length: > 0 } log ? Path.GetFullPath(log) : null;
        var verbose = options.Has("verbose");

        var validation = new FlowValidator().Validate(document, catalog);
        foreach (var issue in validation.Issues)
            ServeLog(logPath, new { type = "issue", severity = issue.Severity, message = issue.Message, nodeId = issue.NodeId });

        if (!validation.IsValid)
        {
            ServeLog(logPath, new { type = "result", success = false, message = "The flow has errors. Fix them and start it again." });
            return 1;
        }

        var supervisor = new FlowSupervisor(
            document,
            catalog,
            async ct => await BoardConnectionService.ConnectAsync(await CreateRequestAsync(profile, options, ct), ct),
            BoardConnectionService.DisposeBoardAsync,
            new FlowSupervisorOptions
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(50, options.GetInt("interval", 1000))),
                InitialBackoff = TimeSpan.FromSeconds(Math.Max(1, options.GetInt("retry", 2))),
                MaxBackoff = TimeSpan.FromSeconds(Math.Max(1, options.GetInt("max-retry", 30))),
                FailuresBeforeReconnect = Math.Max(1, options.GetInt("failures", 3))
            },
            // one line per pass would fill a log in a day: a pass is reported every minute or so unless --verbose; everything else always is
            e =>
            {
                if (e.Kind == FlowSupervisorEventKind.Iteration && !verbose && e.Iteration % 60 != 1)
                    return;

                ServeLog(logPath, new
                {
                    type = "supervisor",
                    kind = e.Kind,
                    message = e.Message,
                    attempt = e.Attempt == 0 ? (int?)null : e.Attempt,
                    pass = e.Iteration == 0 ? (int?)null : e.Iteration,
                    waitSeconds = e.Delay == default ? (double?)null : Math.Round(e.Delay.TotalSeconds, 1)
                });
            });

        ServeLog(logPath, new { type = "status", state = "starting", message = $"Serving {Path.GetFileName(flowPath)} on {profile.DisplayName}.", service = options.Has("service") });

        if (options.Has("service"))
        {
            // sc.exe create CodeBridgeFlow binPath= "<this exe> serve --service --flow <abs path> --port COM3 --log <abs path>"
            using var host = Host.CreateDefaultBuilder()
                .UseWindowsService(service => service.ServiceName = options.Get("name") ?? "CodeBridgeFlow")
                .ConfigureServices(services => services.AddHostedService(_ => new SupervisorService(supervisor)))
                .Build();
            await host.RunAsync(cancellation.Token);
            return 0;
        }

        await supervisor.RunAsync(cancellation.Token);
        return 0;
    }

    private static void ServeLog(string? path, object value)
    {
        Emit(value);
        if (path is null)
            return;

        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxLogBytes)
                File.Move(path, path + ".old", overwrite: true);

            File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + JsonSerializer.Serialize(value, Json) + Environment.NewLine);
        }
        catch (Exception)
        {
            // a log that cannot be written must never stop the flow
        }
    }

    private sealed class SupervisorService : BackgroundService
    {
        private readonly FlowSupervisor _supervisor;

        public SupervisorService(FlowSupervisor supervisor) => _supervisor = supervisor;

        protected override Task ExecuteAsync(CancellationToken stoppingToken) => _supervisor.RunAsync(stoppingToken);
    }
}
