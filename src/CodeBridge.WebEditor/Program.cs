using System.Security.Cryptography;
using System.Text;
using CodeBridge.IotBindings;
using CodeBridge.WebEditor;

// codebridge-web --flow my.cbflow [--port simulator|COM3|/dev/ttyUSB0|192.168.1.50] [--board esp32-devkit] [--http 5280]
//
// The flow editor in a browser. It listens on this computer only (127.0.0.1) and every call needs the random key printed at start-up, so another page
// open in the same browser (or another program) cannot drive the board or rewrite the file.

var arguments = Arguments.Parse(args);
if (arguments.Has("help") || !arguments.Has("flow"))
{
    Console.WriteLine("Usage: codebridge-web --flow <file.cbflow> [--port simulator|COM3|/dev/ttyUSB0|<ip>] [--board <id>] [--http 5280]");
    return arguments.Has("help") ? 0 : 2;
}

IotBindingBlocks.Register();

var session = new EditorSession(arguments.Get("flow")!, arguments.Get("board"));
var hub = new RunHub(session, arguments.Get("port") ?? "simulator");
var httpPort = arguments.GetInt("http", 5280);
var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory, WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot") });
builder.WebHost.UseUrls($"http://127.0.0.1:{httpPort}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

app.Use(async (context, next) =>
{
    var given = context.Request.Headers["X-CodeBridge-Key"].FirstOrDefault() ?? context.Request.Query["k"].FirstOrDefault();
    if (RequestGuard.Reject(context.Request.Host.Host, context.Request.Path, given, key) is { } status)
    {
        context.Response.StatusCode = status;
        return;
    }

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/state", () => Results.Text(EditorSession.Serialize(new
{
    name = session.Document.Name,
    file = Path.GetFileName(session.Path),
    boardId = session.Document.BoardId,
    boards = EditorSession.Boards(),
    target = hub.Target,
    running = hub.Running
}), "application/json"));

app.MapGet("/api/catalog", (string? board) =>
{
    var profile = CodeBridge.Flow.BuiltInBoardProfiles.FindById(board) ?? session.Board;
    return Results.Text(EditorSession.Serialize(EditorSession.Catalog(profile)), "application/json");
});

app.MapGet("/api/flow", () => Results.Text(session.FlowJson(), "application/json"));

app.MapPut("/api/flow", async (HttpRequest request, bool? save) =>
{
    using var reader = new StreamReader(request.Body, Encoding.UTF8);
    return Results.Text(EditorSession.Serialize(session.Update(await reader.ReadToEndAsync(), save ?? false)), "application/json");
});

app.MapPost("/api/run", () =>
{
    var started = hub.Start(out var message);
    return Results.Text(EditorSession.Serialize(new { started, message }), "application/json");
});

app.MapPost("/api/stop", async () =>
{
    await hub.StopAsync();
    return Results.Text(EditorSession.Serialize(new { running = hub.Running }), "application/json");
});

app.MapGet("/api/events", async (HttpContext context, CancellationToken ct) =>
{
    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    var (id, reader) = hub.Subscribe();
    try
    {
        await foreach (var line in reader.ReadAllAsync(ct))
        {
            await context.Response.WriteAsync("data: " + line + "\n\n", ct);
            await context.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException)
    {
    }
    finally
    {
        hub.Unsubscribe(id);
    }
});

app.Lifetime.ApplicationStopping.Register(() => hub.StopAsync().GetAwaiter().GetResult());

Console.WriteLine($"CodeBridge web editor: http://127.0.0.1:{httpPort}/#{key}");
Console.WriteLine($"Flow: {session.Path}   Board link: {hub.Target}   (Ctrl+C to stop)");
await app.RunAsync();
return 0;

internal sealed class Arguments
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static Arguments Parse(string[] args)
    {
        var result = new Arguments();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                continue;

            var name = args[i][2..];
            result._values[name] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        }

        return result;
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string? Get(string name) => _values.GetValueOrDefault(name);

    public int GetInt(string name, int fallback) => int.TryParse(Get(name), out var value) ? value : fallback;
}
