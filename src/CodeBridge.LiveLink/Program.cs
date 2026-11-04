using System.Security.Cryptography;
using System.Text;
using CodeBridge.Flow;
using CodeBridge.LiveLink;

// codebridge-livelink [--port simulator|COM3|/dev/ttyUSB0|192.168.1.50] [--board esp32-devkit] [--http 5300] [--key <key>]
//
// A game engine (Unity, Godot...) drives the servos of a board through ws://127.0.0.1:<http>/link?k=<key>. It listens on this computer only, needs the
// key on every connection, refuses browsers (a web page must not move a robot arm), and serves one engine at a time: the board has one owner.

var arguments = Arguments.Parse(args);
if (arguments.Has("help"))
{
    Console.WriteLine("Usage: codebridge-livelink [--port simulator|COM3|<ip>] [--board esp32-devkit] [--http 5300] [--key <key>]");
    return 0;
}

var profile = BuiltInBoardProfiles.FindById(arguments.Get("board") ?? "esp32-devkit") ?? throw new InvalidOperationException($"Unknown board '{arguments.Get("board")}'.");
var target = arguments.Get("port") ?? "simulator";
var httpPort = arguments.GetInt("http", 5300);
var key = arguments.Get("key") ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

Console.WriteLine($"Connecting to {profile.DisplayName} on {target}...");
CodeBridge.Core.Abstractions.IBoard board;
bool simulated;
try
{
    (board, simulated, _) = await LinkSession.ConnectAsync(target, profile, CancellationToken.None);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
{
    Console.Error.WriteLine($"Cannot connect to {target}: {ex.Message}");
    Console.Error.WriteLine("Check that the board is plugged in and has the CodeBridge firmware, that no other program uses the port, or use --port simulator.");
    return 1;
}

Console.WriteLine($"Connected (firmware {board.FirmwareVersion}{(simulated ? ", simulator" : "")}).");

var app = LiveLinkApp.Create(board, profile, simulated, key, httpPort);

var linkAddress = $"ws://127.0.0.1:{httpPort}/link";
var keyFile = LinkKeyFile.Write(linkAddress, key);
Console.WriteLine($"Live link: {linkAddress}   key: {key}   (Ctrl+C to stop)");
if (keyFile is not null)
    Console.WriteLine($"Unity reads the address and key from {keyFile} when its key field is empty.");
await app.RunAsync();
board.Dispose();
return 0;

/// <summary>
/// The address and key of the running link, in the user's own profile (where only this user can read it), so a Unity scene on the same computer and
/// account can connect without copying the key by hand. Same trust as the key printed on the console.
/// </summary>
internal static class LinkKeyFile
{
    public static string Path => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeBridge", "livelink.txt");

    public static string? Write(string address, string key)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllLines(Path, [address, key]);
            return Path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;   // the key is still printed on the console
        }
    }
}

internal static class LinkGuard
{
    /// <summary>The status code to refuse a connection with, or null when it may go on.</summary>
    public static int? Reject(string host, string? origin, string? givenKey, string key)
    {
        // DNS rebinding: a name that merely resolves to this computer gets nowhere
        if (host is not ("127.0.0.1" or "localhost" or "[::1]" or "::1"))
            return StatusCodes.Status421MisdirectedRequest;

        // browsers always send an Origin on a WebSocket; game engines and tools do not. No web page may move the arm.
        if (!string.IsNullOrEmpty(origin))
            return StatusCodes.Status403Forbidden;

        if (givenKey is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(givenKey), Encoding.UTF8.GetBytes(key)))
            return StatusCodes.Status401Unauthorized;

        return null;
    }
}

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
