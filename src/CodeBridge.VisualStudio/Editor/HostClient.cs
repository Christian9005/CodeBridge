#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodeBridge.VisualStudio.Editor
{
    /// <summary>One JSON line written by CodeBridge.FlowHost.</summary>
    internal sealed class HostMessage
    {
        private readonly Dictionary<string, object> _values;

        public HostMessage(Dictionary<string, object> values)
        {
            _values = values;
        }

        public string Type => Str("type") ?? string.Empty;

        public string? Str(string key) =>
            _values.TryGetValue(key, out var value) && value != null ? Convert.ToString(value) : null;

        public bool Bool(string key) =>
            _values.TryGetValue(key, out var value) && value is bool flag && flag;

        public IEnumerable<HostMessage> List(string key)
        {
            if (_values.TryGetValue(key, out var value) && value is IEnumerable items && !(value is string))
            {
                foreach (var item in items)
                {
                    if (item is Dictionary<string, object> map)
                        yield return new HostMessage(map);
                }
            }
        }

        /// <summary>A JSON array of strings (for example the export warnings).</summary>
        public List<string> StringList(string key)
        {
            var result = new List<string>();
            if (_values.TryGetValue(key, out var value) && value is IEnumerable items && !(value is string))
            {
                foreach (var item in items)
                {
                    if (item != null)
                        result.Add(Convert.ToString(item) ?? string.Empty);
                }
            }

            return result;
        }

        public HostMessage? Child(string key) =>
            _values.TryGetValue(key, out var value) && value is Dictionary<string, object> map ? new HostMessage(map) : null;

        public static HostMessage? TryParse(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
                return null;

            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                return serializer.DeserializeObject(line) is Dictionary<string, object> map ? new HostMessage(map) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>A running CodeBridge.FlowHost command.</summary>
    internal sealed class HostProcess : IDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _stderr = new StringBuilder();
        private int _stopRequested;

        public HostProcess(Process process)
        {
            _process = process;
        }

        public bool IsRunning
        {
            get
            {
                try { return !_process.HasExited; }
                catch (InvalidOperationException) { return false; }
            }
        }

        public string StandardError => _stderr.ToString();

        internal void AppendError(string? line)
        {
            if (line != null)
                _stderr.AppendLine(line);
        }

        /// <summary>Sends one line to the host's standard input (commands of a running host, such as the serial monitor).</summary>
        public void SendLine(string line)
        {
            try
            {
                _process.StandardInput.WriteLine(line);
                _process.StandardInput.Flush();
            }
            catch (Exception)
            {
                // the host already ended
            }
        }

        /// <summary>Asks the host to stop gracefully; kills the whole process tree if it does not exit quickly.</summary>
        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopRequested, 1) == 1 || !IsRunning)
                return;

            try
            {
                _process.StandardInput.WriteLine("stop");
                _process.StandardInput.Flush();
            }
            catch (Exception)
            {
                // stdin already closed
            }

            Task.Run(async () =>
            {
                await Task.Delay(3000).ConfigureAwait(false);
                Kill();
            });
        }

        public void Kill()
        {
            if (!IsRunning)
                return;

            try
            {
                // Process.Kill() on .NET Framework does not stop child processes (esptool); taskkill /T does.
                using (var kill = Process.Start(new ProcessStartInfo("taskkill", $"/PID {_process.Id} /T /F")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                }))
                {
                    kill?.WaitForExit(5000);
                }
            }
            catch (Exception)
            {
                try { _process.Kill(); } catch (Exception) { }
            }
        }

        public void Dispose()
        {
            Kill();
            _process.Dispose();
        }
    }

    internal static class HostClient
    {
        private static readonly object ExtractLock = new object();
        private static string? _hostPath;

        /// <summary>
        /// Full path of CodeBridge.FlowHost.exe. The VSIX carries the host as FlowHost.zip (keeping net8 assemblies
        /// out of the extension folder); it is unpacked once per version to %LOCALAPPDATA%\CodeBridge\host.
        /// </summary>
        public static string? HostPath
        {
            get
            {
                lock (ExtractLock)
                {
                    if (_hostPath != null && File.Exists(_hostPath))
                        return _hostPath;

                    _hostPath = Locate();
                    return _hostPath;
                }
            }
        }

        private static string? Locate()
        {
            var directory = ExtensionPaths.Directory;

            // Development / tests: an unpacked host next to the assembly.
            var loose = Path.Combine(directory, "FlowHost", "CodeBridge.FlowHost.exe");
            if (File.Exists(loose))
                return loose;

            var archive = Path.Combine(directory, "FlowHost.zip");
            if (!File.Exists(archive))
                return null;

            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeBridge", "host");
                var info = new FileInfo(archive);
                var target = Path.Combine(root, info.Length.ToString() + "-" + info.LastWriteTimeUtc.Ticks.ToString());
                var exe = Path.Combine(target, "CodeBridge.FlowHost.exe");
                if (File.Exists(exe))
                    return exe;

                Directory.CreateDirectory(root);
                var staging = target + ".tmp-" + Guid.NewGuid().ToString("N");
                ZipFile.ExtractToDirectory(archive, staging);
                try
                {
                    Directory.Move(staging, target);
                }
                catch (IOException)
                {
                    // Another Visual Studio instance extracted it first.
                    Directory.Delete(staging, true);
                }

                CleanOldVersions(root, target);
                return File.Exists(exe) ? exe : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void CleanOldVersions(string root, string keep)
        {
            try
            {
                foreach (var directory in Directory.GetDirectories(root))
                {
                    if (!string.Equals(directory, keep, StringComparison.OrdinalIgnoreCase))
                    {
                        try { Directory.Delete(directory, true); }
                        catch (IOException) { /* in use by another instance */ }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }
            catch (IOException)
            {
            }
        }

        public static bool IsAvailable => HostPath != null;

        /// <summary>
        /// Starts a host command. <paramref name="onMessage"/> and <paramref name="onExit"/> run on background threads.
        /// </summary>
        /// <param name="accessToken">Pairing token for Wi-Fi boards, passed through the environment so it never shows up in a command line.</param>
        /// <param name="stdinLine">One line written to the host's standard input right after it starts (secrets such as the Wi-Fi password).</param>
        public static HostProcess Start(string arguments, Action<HostMessage> onMessage, Action<int, string> onExit,
            string? accessToken = null, string? stdinLine = null)
        {
            var path = HostPath ?? throw new FileNotFoundException(
                "The CodeBridge FlowHost is missing from the extension. Reinstall CodeBridge Visual Studio Tools.");

            var startInfo = new ProcessStartInfo(path, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(path)!
            };
            startInfo.EnvironmentVariables["DOTNET_NOLOGO"] = "1";
            if (!string.IsNullOrEmpty(accessToken))
                startInfo.EnvironmentVariables["CODEBRIDGE_TOKEN"] = accessToken;

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var host = new HostProcess(process);
            var outputDone = new TaskCompletionSource<bool>();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null)
                {
                    outputDone.TrySetResult(true);
                    return;
                }

                var message = HostMessage.TryParse(e.Data);
                if (message != null)
                    onMessage(message);
            };
            process.ErrorDataReceived += (_, e) => host.AppendError(e.Data);
            process.Exited += (_, __) =>
            {
                // Make sure every stdout line was delivered before reporting the exit.
                Task.Run(async () =>
                {
                    await Task.WhenAny(outputDone.Task, Task.Delay(2000)).ConfigureAwait(false);
                    int code;
                    try { code = process.ExitCode; }
                    catch (InvalidOperationException) { code = -1; }
                    onExit(code, host.StandardError);
                });
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (stdinLine != null)
            {
                process.StandardInput.WriteLine(stdinLine);
                process.StandardInput.Flush();
            }

            return host;
        }

        /// <summary>Runs a one-shot command and returns every message it printed.</summary>
        public static Task<IReadOnlyList<HostMessage>> QueryAsync(string arguments, CancellationToken cancellationToken = default,
            string? accessToken = null, string? stdinLine = null)
        {
            var messages = new List<HostMessage>();
            var completion = new TaskCompletionSource<IReadOnlyList<HostMessage>>();
            HostProcess? host = null;

            try
            {
                host = Start(
                    arguments,
                    message => { lock (messages) messages.Add(message); },
                    (code, error) =>
                    {
                        lock (messages)
                        {
                            if (messages.Count == 0 && !string.IsNullOrWhiteSpace(error))
                            {
                                messages.Add(new HostMessage(new Dictionary<string, object>
                                {
                                    ["type"] = "error",
                                    ["message"] = Friendly(error)
                                }));
                            }

                            completion.TrySetResult(messages.ToList());
                        }
                    },
                    accessToken,
                    stdinLine);
            }
            catch (Exception ex)
            {
                completion.TrySetResult(new List<HostMessage>
                {
                    new HostMessage(new Dictionary<string, object> { ["type"] = "error", ["message"] = ex.Message })
                });
            }

            if (host != null)
            {
                cancellationToken.Register(() => host.Kill());
                completion.Task.ContinueWith(_ => host.Dispose(), TaskScheduler.Default);
            }

            return completion.Task;
        }

        public static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

        /// <summary>Turns the apphost's "runtime not found" output into something actionable.</summary>
        public static string Friendly(string stderr)
        {
            if (stderr.IndexOf("framework", StringComparison.OrdinalIgnoreCase) >= 0 ||
                stderr.IndexOf("You must install", StringComparison.OrdinalIgnoreCase) >= 0 ||
                stderr.IndexOf(".NET", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "CodeBridge needs the .NET 8 (or newer) Desktop Runtime to talk to the board. Install it from https://dotnet.microsoft.com/download and try again.";
            }

            return stderr.Trim();
        }
    }
}
