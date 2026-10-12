using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeBridge.PlatformIO;

/// <summary>How to start PlatformIO on this machine: <c>pio</c> itself, or <c>python -m platformio</c>.</summary>
public sealed record PioTool(string FileName, IReadOnlyList<string> PrefixArguments, string Version)
{
    public string Description => PrefixArguments.Count == 0 ? FileName : FileName + " " + string.Join(' ', PrefixArguments);
}

/// <summary>Finds PlatformIO Core. It ships with the VS Code extension, can be installed with <c>pip install platformio</c>, and needs no IDE.</summary>
public static class PioLocator
{
    public const string InstallHint =
        "PlatformIO is not installed. Install it once with:  pip install platformio   (needs Python 3.8+),  " +
        "or set CODEBRIDGE_PIO to the path of pio.exe. https://docs.platformio.org/en/latest/core/installation/";

    private static readonly Regex VersionPattern = new(@"version\s+(?<v>\d+\.\d+(\.\d+)?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static PioTool? _cached;

    public static PioTool? Find(bool useCache = true)
    {
        if (useCache && _cached is not null)
            return _cached;

        foreach (var (file, prefix) in Candidates())
        {
            var version = Probe(file, prefix);
            if (version is null)
                continue;

            _cached = new PioTool(file, prefix, version);
            return _cached;
        }

        return null;
    }

    private static IEnumerable<(string File, string[] Prefix)> Candidates()
    {
        var configured = Environment.GetEnvironmentVariable("CODEBRIDGE_PIO");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            yield return (configured, Array.Empty<string>());

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var relative in new[] { Path.Combine(".platformio", "penv", "Scripts", "pio.exe"), Path.Combine(".platformio", "penv", "bin", "pio") })
        {
            var path = Path.Combine(home, relative);
            if (File.Exists(path))
                yield return (path, Array.Empty<string>());
        }

        yield return ("pio", Array.Empty<string>());
        yield return ("platformio", Array.Empty<string>());
        yield return ("python", new[] { "-m", "platformio" });
        yield return ("py", new[] { "-3", "-m", "platformio" });
        yield return ("python3", new[] { "-m", "platformio" });
    }

    private static string? Probe(string file, IReadOnlyList<string> prefix)
    {
        try
        {
            var start = new ProcessStartInfo(file)
            {
                RedirectStandardInput = true, // never inherit our standard input: for an MCP server it is the protocol pipe
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            foreach (var argument in prefix)
                start.ArgumentList.Add(argument);
            start.ArgumentList.Add("--version");

            using var process = Process.Start(start);
            if (process is null)
                return null;

            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(20000))
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                return null;
            }

            var match = VersionPattern.Match(output.GetAwaiter().GetResult());
            return process.ExitCode == 0 && match.Success ? match.Groups["v"].Value : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null; // not installed under this name
        }
    }
}
