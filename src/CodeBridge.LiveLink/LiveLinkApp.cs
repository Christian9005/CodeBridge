using CodeBridge.Core.Abstractions;
using CodeBridge.Flow;

namespace CodeBridge.LiveLink;

/// <summary>The web server of the live link: one WebSocket endpoint, <c>/link</c>, for one engine at a time.</summary>
internal static class LiveLinkApp
{
    /// <param name="port">The local port; 0 picks a free one (tests).</param>
    public static WebApplication Create(IBoard board, BoardProfile profile, bool simulated, string key, int port)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();
        app.UseWebSockets();

        var busy = 0;
        app.Map("/link", async context =>
        {
            if (LinkGuard.Reject(context.Request.Host.Host, context.Request.Headers.Origin.FirstOrDefault(), context.Request.Query["k"].FirstOrDefault(), key) is { } status)
            {
                context.Response.StatusCode = status;
                return;
            }

            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;   // one engine at a time: the board has one owner
                Console.WriteLine("Engine refused: another one is still connected.");
                return;
            }

            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                Console.WriteLine("Engine connected.");
                await new LinkSession(socket, board, profile, simulated).RunAsync(context.RequestAborted);
            }
            catch (Exception ex) when (ex is OperationCanceledException or System.Net.WebSockets.WebSocketException)
            {
            }
            finally
            {
                Console.WriteLine("Engine disconnected: the arm holds its position.");
                Volatile.Write(ref busy, 0);
            }
        });

        return app;
    }
}
