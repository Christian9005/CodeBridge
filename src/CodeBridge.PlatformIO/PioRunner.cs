using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeBridge.PlatformIO;

/// <summary>One compiler or PlatformIO message, ready for an error list.</summary>
public sealed record PioDiagnostic(string Severity, string Message, string? File, int Line, int Column);

public sealed record PioRunResult(int ExitCode, IReadOnlyList<PioDiagnostic> Diagnostics, TimeSpan Duration)
{
    public bool Success => ExitCode == 0;

    public int Errors => Diagnostics.Count(d => d.Severity == "error");

    public int Warnings => Diagnostics.Count(d => d.Severity == "warning");
}

/// <summary>Runs PlatformIO commands in a project folder, streaming every output line and collecting compiler diagnostics.</summary>
public static class PioRunner
{
    // src/main.cpp:12:5: error: 'foo' was not declared in this scope        (GCC; also "fatal error")
    private static readonly Regex Gcc = new(
        @"^(?<file>(?:[A-Za-z]:)?[^:\r\n]+?):(?<line>\d+):(?:(?<col>\d+):)?\s*(?<sev>fatal error|error|warning):\s*(?<msg>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // *** [.pio\build\uno\src\main.cpp.o] Error 1   /   Error: Could not find the package ...
    private static readonly Regex PioError = new(@"^\s*Error:\s*(?<msg>.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Reads one line of build output as a diagnostic, or returns null when it is ordinary output.</summary>
    public static PioDiagnostic? ParseDiagnostic(string line, string? projectDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var gcc = Gcc.Match(line);
        if (gcc.Success)
        {
            var file = gcc.Groups["file"].Value.Trim();
            if (projectDirectory is not null && !Path.IsPathRooted(file))
                file = Path.GetFullPath(Path.Combine(projectDirectory, file));

            var severity = gcc.Groups["sev"].Value.EndsWith("error", StringComparison.Ordinal) ? "error" : "warning";
            return new PioDiagnostic(severity, gcc.Groups["msg"].Value.Trim(), file, int.Parse(gcc.Groups["line"].Value), gcc.Groups["col"].Success ? int.Parse(gcc.Groups["col"].Value) : 0);
        }

        var pio = PioError.Match(line);
        return pio.Success ? new PioDiagnostic("error", pio.Groups["msg"].Value.Trim(), null, 0, 0) : null;
    }

    public static Task<PioRunResult> BuildAsync(PioTool tool, string directory, string? environment = null, Action<string>? onLine = null, CancellationToken ct = default) =>
        RunAsync(tool, directory, With(new[] { "run" }, environment), onLine, ct);

    public static Task<PioRunResult> CleanAsync(PioTool tool, string directory, string? environment = null, Action<string>? onLine = null, CancellationToken ct = default) =>
        RunAsync(tool, directory, With(new[] { "run", "--target", "clean" }, environment), onLine, ct);

    public static Task<PioRunResult> UploadAsync(PioTool tool, string directory, string port, string? environment = null, Action<string>? onLine = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(port);
        return RunAsync(tool, directory, With(new[] { "run", "--target", "upload", "--upload-port", port }, environment), onLine, ct);
    }

    private static string[] With(string[] arguments, string? environment) =>
        string.IsNullOrWhiteSpace(environment) ? arguments : arguments.Concat(new[] { "-e", environment }).ToArray();

    public static async Task<PioRunResult> RunAsync(PioTool tool, string directory, IEnumerable<string> arguments, Action<string>? onLine = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!PioProject.IsProject(directory))
            throw new InvalidOperationException($"'{directory}' is not a PlatformIO project (it has no platformio.ini).");

        var start = new ProcessStartInfo(tool.FileName)
        {
            WorkingDirectory = directory,
            RedirectStandardInput = true, // the build must not read (and steal) the caller's standard input
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in tool.PrefixArguments.Concat(arguments))
            start.ArgumentList.Add(argument);

        start.Environment["PLATFORMIO_SETTING_ENABLE_PROMPTS"] = "false";
        start.Environment["PYTHONUNBUFFERED"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["TERM"] = "dumb";

        var diagnostics = new List<PioDiagnostic>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var gate = new object();
        var watch = Stopwatch.StartNew();

        void Handle(string? line)
        {
            if (line is null)
                return;

            var diagnostic = ParseDiagnostic(line, directory);
            lock (gate)
            {
                if (diagnostic is not null && seen.Add($"{diagnostic.Severity}|{diagnostic.File}|{diagnostic.Line}|{diagnostic.Message}"))
                    diagnostics.Add(diagnostic);
            }

            onLine?.Invoke(line);
        }

        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => Handle(e.Data);
        process.ErrorDataReceived += (_, e) => Handle(e.Data);

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"Could not start PlatformIO ({tool.Description}): {ex.Message}", ex);
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var registration = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });

        await process.WaitForExitAsync(CancellationToken.None);
        process.WaitForExit(); // flushes the asynchronous output handlers
        ct.ThrowIfCancellationRequested();

        lock (gate)
            return new PioRunResult(process.ExitCode, diagnostics.ToList(), watch.Elapsed);
    }
}
