using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeBridge.Designer.WinForms;
using CodeBridge.Designer.WinForms.Hardware;
using CodeBridge.Flow;
using CodeBridge.Flow.CodeGeneration;
using CodeBridge.Flow.Execution;
using CodeBridge.Flow.Serialization;
using CodeBridge.Flow.Validation;
using CodeBridge.PlatformIO;
using CodeBridge.Transport.Discovery;
using CodeBridge.Transport.Ota;
using CodeBridge.Transport.Provisioning;
using CodeBridge.Transport.Serial;

namespace CodeBridge.FlowHost;

/// <summary>
/// Usage: CodeBridge.FlowHost &lt;command&gt; [--option value]...
/// Commands: boards | ports | wifi-boards | catalog | validate | export | test | upload | ota | run | wifi-scan | wifi-status | wifi-config | wifi-unpair | monitor | mesh-status | mesh-scan | mesh-provision | ha-find | ha-test | ha-bridge | pio-info | pio-new | pio-build | pio-upload | pio-clean
/// </summary>
internal static partial class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly object OutputLock = new();

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);

        if (args.Length == 0)
        {
            Emit(new { type = "error", message = "Missing command. Use: boards, ports, catalog, validate, test, upload, run." });
            return 2;
        }

        var options = Options.Parse(args.Skip(1));
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "boards" => Boards(),
                "ports" => Ports(),
                "wifi-boards" => await WifiBoardsAsync(cancellation.Token),
                "mesh-status" => await MeshStatusAsync(options, cancellation.Token),
                "mesh-scan" => await MeshScanAsync(options, cancellation.Token),
                "mesh-provision" => await MeshProvisionAsync(options, cancellation.Token),
                "ha-find" => await HomeAssistantFindAsync(cancellation.Token),
                "ha-test" => await HomeAssistantTestAsync(cancellation.Token),
                "ha-bridge" => await HomeAssistantBridgeAsync(options, cancellation),
                "ota" => await OtaAsync(options, cancellation.Token),
                "monitor" => await MonitorAsync(options, cancellation.Token),
                "pio-info" => PioInfo(),
                "pio-new" => PioNew(options),
                "pio-build" => await PioRunAsync(options, "build", cancellation.Token),
                "pio-upload" => await PioRunAsync(options, "upload", cancellation.Token),
                "pio-clean" => await PioRunAsync(options, "clean", cancellation.Token),
                "catalog" => Catalog(options),
                "validate" => Validate(options),
                "export" => Export(options),
                "test" => await TestAsync(options, cancellation.Token),
                "upload" => await UploadAsync(options, cancellation.Token),
                "run" => await RunAsync(options, cancellation),
                "wifi-scan" => await WifiScanAsync(options, cancellation.Token),
                "wifi-status" => await WifiStatusAsync(options, cancellation.Token),
                "wifi-config" => await WifiConfigAsync(options, cancellation.Token),
                "wifi-unpair" => await WifiUnpairAsync(options, cancellation.Token),
                _ => Fail($"Unknown command '{args[0]}'.")
            };
        }
        catch (OperationCanceledException)
        {
            Emit(new { type = "result", success = false, cancelled = true, message = "Cancelled." });
            return 1;
        }
        catch (Exception ex)
        {
            var profile = ResolveBoard(options, required: false);
            Emit(new
            {
                type = "error",
                message = Describe(ex, profile, options)
            });
            return 1;
        }
    }

    private static string Describe(Exception ex, BoardProfile? profile, Options options)
    {
        var port = options.Get("port") ?? options.Get("host") ?? "the selected port";
        switch (ex)
        {
            case FileNotFoundException or DirectoryNotFoundException:
                return $"{port} was not found. Check the USB cable and pick the board's COM port again (use the refresh button).";
            case UnauthorizedAccessException when ex.Message.Contains("pairing token", StringComparison.OrdinalIgnoreCase):
                return ex.Message;
            case UnauthorizedAccessException:
                return $"{port} is in use by another program (Serial Monitor, Arduino IDE, another Visual Studio window...). Close it and try again.";
            case IOException when ex.Message.Contains("semaphore", StringComparison.OrdinalIgnoreCase):
                return $"{port} stopped responding. Unplug and reconnect the board, then try again.";
        }

        return profile is null ? ex.Message : BoardConnectionService.FormatConnectionFailure(ex, profile, options.Get("port"));
    }

    // ---------------------------------------------------------------- boards / ports

    private static int Boards()
    {
        var boards = BuiltInBoardProfiles.All.Select(board => new
        {
            id = board.Id,
            displayName = board.DisplayName,
            canFlash = HardwareToolLocator.ResolveFirmwareDirectory(board.Id) is not null,
            flashTool = board.Family == CodeBridge.Core.Enums.BoardFamily.Arduino ? "Arduino CLI" : "esptool",
            family = board.Family.ToString(),
            supportsWifi = board.SupportsWifi
        });

        Emit(new { type = "boards", boards });
        return 0;
    }

    private static int Ports()
    {
        // A port that looks like a board comes first, so it is the one the editor starts on (the built-in COM1 of a PC comes last).
        var names = System.IO.Ports.SerialPort.GetPortNames()
            .OrderBy(name => name.Length)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var likely = CodeBridge.Transport.BoardDiscovery.RankCandidates(CodeBridge.Transport.BoardDiscovery.DiscoverPorts()).Select(port => port.Name).ToList();
        var real = names
            .OrderBy(name => likely.FindIndex(known => known.Equals(name, StringComparison.OrdinalIgnoreCase)) is var index && index >= 0 ? index : int.MaxValue)
            .Select(name => new
            {
                name,
                description = BoardPortDetector.DescribePort(name),
                suggestedBoard = BoardPortDetector.SuggestBoardForPort(name)?.Id
            });

        // The virtual board is always available, listed last so a real board stays the default.
        var ports = real.Append(new { name = "Simulator", description = (string?)"Virtual board - no hardware needed", suggestedBoard = (string?)null });

        Emit(new { type = "ports", ports });
        return 0;
    }

    // ---------------------------------------------------------------- catalog

    private static int Catalog(Options options)
    {
        var outDirectory = options.Get("out");
        foreach (var board in BuiltInBoardProfiles.All)
        {
            var catalog = BuiltInBlockCatalog.Create(board);
            var payload = new
            {
                boardId = board.Id,
                displayName = board.DisplayName,
                blocks = catalog.Blocks
                    .OrderBy(block => block.Category, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(block => block.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(block => new
                    {
                        type = block.Type,
                        displayName = block.DisplayName,
                        category = block.Category,
                        description = block.Description,
                        ports = block.Ports.Select(port => new
                        {
                            name = port.Name,
                            direction = port.Direction,
                            valueKind = port.ValueKind,
                            required = port.Required
                        }),
                        properties = block.Properties.Select(property => new
                        {
                            name = property.Name,
                            valueKind = property.ValueKind,
                            required = property.Required,
                            isAdvanced = property.IsAdvanced,
                            defaultValue = property.DefaultValue,
                            options = property.Options?.Select(option => new
                            {
                                label = option.Label,
                                value = option.Value,
                                description = option.Description
                            })
                        })
                    })
            };

            var text = JsonSerializer.Serialize(payload, new JsonSerializerOptions(Json) { WriteIndented = true });
            if (string.IsNullOrWhiteSpace(outDirectory))
            {
                Console.WriteLine(text);
            }
            else
            {
                Directory.CreateDirectory(outDirectory);
                File.WriteAllText(Path.Combine(outDirectory, $"catalog.{board.Id}.json"), text + "\n", new UTF8Encoding(false));
            }
        }

        return 0;
    }

    // ---------------------------------------------------------------- validate

    private static int Validate(Options options)
    {
        var document = LoadDocument(options);
        var profile = BuiltInBoardProfiles.FindById(options.Get("board") ?? document.BoardId) ?? BuiltInBoardProfiles.Default;
        var result = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create(profile));

        Emit(new
        {
            type = "validation",
            isValid = result.IsValid,
            issues = result.Issues.Select(issue => new
            {
                severity = issue.Severity,
                message = issue.Message,
                nodeId = issue.NodeId,
                connectionId = issue.ConnectionId
            })
        });
        return result.IsValid ? 0 : 1;
    }

    // ---------------------------------------------------------------- export to C#

    private static int Export(Options options)
    {
        var document = LoadDocument(options);
        if (options.Get("board") is { } board)
            document.BoardId = board;

        var mode = string.Equals(options.Get("mode"), "console", StringComparison.OrdinalIgnoreCase)
            ? FlowCSharpMode.ConsoleApp
            : FlowCSharpMode.Class;

        var result = FlowCSharpGenerator.Generate(document, new FlowCSharpOptions
        {
            ClassName = options.Get("class"),
            Namespace = options.Get("namespace") ?? "CodeBridge.Flows",
            Mode = mode,
            Loop = options.Has("loop"),
            LoopIntervalMs = Math.Max(50, options.GetInt("interval", 1000))
        });

        Emit(new { type = "export", className = result.ClassName, mode = mode.ToString(), code = result.Code, warnings = result.Warnings });
        return 0;
    }

    // ---------------------------------------------------------------- test connection

    private static async Task<int> TestAsync(Options options, CancellationToken ct)
    {
        var profile = ResolveBoard(options)!;
        Emit(new { type = "status", state = "connecting", message = $"Connecting to {profile.DisplayName}..." });

        var board = await BoardConnectionService.ConnectAsync(await CreateRequestAsync(profile, options, ct), ct);
        try
        {
            object? info = null;
            try
            {
                var boardInfo = await board.GetInfoAsync(ct);
                info = boardInfo;
            }
            catch (Exception)
            {
                // Board info is optional (the Arduino Uno sketch does not implement it).
            }

            Emit(new { type = "connected", board = profile.Id, name = board.Name, firmware = board.FirmwareVersion, info });
            return 0;
        }
        finally
        {
            await BoardConnectionService.DisposeBoardAsync(board);
        }
    }

    // ---------------------------------------------------------------- code mode (PlatformIO)

    private static int PioInfo()
    {
        var tool = PioLocator.Find();
        Emit(new { type = "pio-info", found = tool is not null, version = tool?.Version, tool = tool?.Description, hint = tool is null ? PioLocator.InstallHint : null });
        return 0;
    }

    /// <summary>Creates a firmware project. "--template bridge" starts from the CodeBridge bridge firmware itself, ready to customize.</summary>
    private static int PioNew(Options options)
    {
        var board = ResolveBoard(options)!;
        var directory = options.Get("dir") ?? throw new InvalidOperationException("Choose the folder for the new project.");
        var template = (options.Get("template") ?? "blink").ToLowerInvariant();

        IReadOnlyList<string> files;
        if (template == "bridge")
        {
            var firmware = HardwareToolLocator.ResolveFirmwareDirectory(board.Id)
                ?? throw new DirectoryNotFoundException("The bridge firmware sources were not found. Reinstall CodeBridge Visual Studio Tools.");
            var sources = new[] { "platformio.ini", Path.Combine("src", "main.cpp") }.Select(f => Path.Combine(firmware, f)).ToList();
            if (sources.Any(f => !File.Exists(f)))
                throw new FileNotFoundException("The bridge firmware sources are incomplete in this installation.");

            files = PioProject.Create(directory, board.Id, FirmwareTemplate.Empty);
            File.Copy(sources[0], Path.Combine(directory, "platformio.ini"), overwrite: true);
            File.Copy(sources[1], Path.Combine(directory, "src", "main.cpp"), overwrite: true);
        }
        else
        {
            var kind = template switch { "serial" or "plot" or "serialplot" => FirmwareTemplate.SerialPlot, "empty" => FirmwareTemplate.Empty, _ => FirmwareTemplate.Blink };
            files = PioProject.Create(directory, board.Id, kind);
        }

        Emit(new { type = "pio-created", directory, files, entry = Path.Combine(directory, "src", "main.cpp") });
        return 0;
    }

    private static async Task<int> PioRunAsync(Options options, string action, CancellationToken ct)
    {
        var tool = PioLocator.Find();
        if (tool is null)
        {
            Emit(new { type = "error", message = PioLocator.InstallHint });
            return 1;
        }

        var directory = PioProject.FindRoot(options.Get("dir")) ?? throw new InvalidOperationException("That folder is not a PlatformIO project (no platformio.ini was found).");
        var environment = options.Get("env");
        Emit(new { type = "status", state = "building", message = $"PlatformIO {action} in {directory}..." });

        var onLine = new Action<string>(line => Emit(new { type = "log", text = line }));
        PioRunResult result;
        switch (action)
        {
            case "upload":
                var port = options.Get("port") ?? throw new InvalidOperationException("Select the board's USB port first.");
                result = await PioRunner.UploadAsync(tool, directory, port, environment, onLine, ct);
                break;
            case "clean":
                result = await PioRunner.CleanAsync(tool, directory, environment, onLine, ct);
                break;
            default:
                result = await PioRunner.BuildAsync(tool, directory, environment, onLine, ct);
                break;
        }

        foreach (var d in result.Diagnostics)
            Emit(new { type = "diagnostic", severity = d.Severity, message = d.Message, file = d.File, line = d.Line, column = d.Column });

        Emit(new
        {
            type = "result",
            success = result.Success,
            message = result.Success
                ? $"PlatformIO {action} finished in {result.Duration.TotalSeconds:0.#} s ({result.Warnings} warning(s))."
                : $"PlatformIO {action} failed: {result.Errors} error(s), {result.Warnings} warning(s).",
            errors = result.Errors,
            warnings = result.Warnings
        });
        return result.Success ? 0 : 1;
    }

    // ---------------------------------------------------------------- serial monitor

    /// <summary>
    /// Streams what a board prints on its serial port ("lines" messages, batched every 40 ms) and sends what the user types.
    /// Standard input: "stop", "reset" (pulses the reset line like the flashing tools do) or "&gt;text" (sent to the board followed by
    /// the chosen line ending). "--port simulator" produces demo data so the plotter can be tried without a board.
    /// </summary>
    private static async Task<int> MonitorAsync(Options options, CancellationToken ct)
    {
        var port = options.Get("port") ?? throw new InvalidOperationException("Select a serial port first.");
        var newline = (options.Get("newline") ?? "lf").ToLowerInvariant() switch { "crlf" => "\r\n", "cr" => "\r", "none" => string.Empty, _ => "\n" };
        var baud = options.GetInt("baud", 115200);
        var simulated = port.Equals("simulator", StringComparison.OrdinalIgnoreCase);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = new List<string>();
        var gate = new object();
        System.IO.Ports.SerialPort? serial = null;
        var outbox = System.Threading.Channels.Channel.CreateUnbounded<string>();

        if (!simulated)
        {
            serial = new System.IO.Ports.SerialPort(port, baud) { DtrEnable = false, RtsEnable = false, ReadTimeout = 500, WriteTimeout = 2000 };
            serial.Open(); // opening with DTR/RTS low does not reset the board
        }

        Emit(new { type = "connected", port = simulated ? "Simulator" : port, baud });

        void Add(string line)
        {
            lock (gate)
            {
                if (pending.Count < 5000) // a runaway board cannot flood the editor
                    pending.Add(line);
            }
        }

        // reads lines from the board (or invents some)
        var reader = Task.Run(async () =>
        {
            try
            {
                if (serial is null)
                {
                    var tick = 0;
                    var random = new Random(7);
                    while (!stop.IsCancellationRequested)
                    {
                        var t = tick++ * 0.1;
                        Add($"light:{(int)(2048 + 1800 * Math.Sin(t / 2)):D}, temp:{23.5 + 2 * Math.Sin(t / 7) + random.NextDouble() * 0.2:0.0}, button:{(Math.Sin(t) > 0.8 ? 1 : 0)}");
                        while (outbox.Reader.TryRead(out var text))
                            Add(text.Trim().Equals("ping", StringComparison.OrdinalIgnoreCase) ? "pong" : "> " + text.TrimEnd());
                        await Task.Delay(100, stop.Token);
                    }

                    return;
                }

                var buffer = new byte[2048];
                var line = new System.Text.StringBuilder();
                var stream = serial.BaseStream;
                while (!stop.IsCancellationRequested)
                {
                    int read;
                    try
                    {
                        read = await stream.ReadAsync(buffer, stop.Token);
                    }
                    catch (TimeoutException)
                    {
                        continue;
                    }

                    if (read == 0)
                        continue;

                    for (var i = 0; i < read; i++)
                    {
                        var b = buffer[i];
                        if (b == '\n')
                        {
                            Add(line.ToString());
                            line.Clear();
                        }
                        else if (b != '\r') // the CR of a CR+LF line ending is not part of the text
                        {
                            if (line.Length >= 4096)
                            {
                                Add(line.ToString()); // an endless line is cut instead of growing without bound
                                line.Clear();
                            }

                            line.Append(b < 32 && b != '\t' ? '·' : (char)b);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                Emit(new { type = "error", message = "The serial port stopped responding (was the board unplugged?): " + ex.Message });
                stop.Cancel();
            }
        });

        // sends to the board
        var writer = Task.Run(async () =>
        {
            try
            {
                while (await outbox.Reader.WaitToReadAsync(stop.Token))
                {
                    if (serial is null)
                        break; // the simulator reader consumes the outbox itself

                    while (outbox.Reader.TryRead(out var text))
                        serial.Write(text + newline);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or InvalidOperationException)
            {
            }
        });

        // flushes batches to the editor
        var flusher = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(40, stop.Token);
                    string[] batch;
                    lock (gate)
                    {
                        if (pending.Count == 0)
                            continue;

                        batch = pending.ToArray();
                        pending.Clear();
                    }

                    Emit(new { type = "lines", lines = batch });
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        // commands from the editor
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var input = await Console.In.ReadLineAsync();
                if (input is null || input == "stop")
                {
                    stop.Cancel();
                    return;
                }

                if (input == "reset" && serial is not null)
                {
                    // same sequence the ESP32 flashing tools use: EN low for 100 ms
                    serial.DtrEnable = false;
                    serial.RtsEnable = true;
                    await Task.Delay(100);
                    serial.RtsEnable = false;
                }
                else if (input.StartsWith('>'))
                {
                    outbox.Writer.TryWrite(input[1..]);
                }
            }
        });

        try
        {
            await Task.WhenAny(reader, flusher);
            stop.Cancel();
            await Task.WhenAll(reader, flusher, writer).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }
        finally
        {
            try { serial?.Close(); } catch (IOException) { }
            serial?.Dispose();
        }

        Emit(new { type = "result", success = true, message = "Monitor closed." });
        return 0;
    }

    // ---------------------------------------------------------------- Wi-Fi boards found on the network, OTA update

    private static async Task<int> WifiBoardsAsync(CancellationToken ct)
    {
        var found = await WifiBoardFinder.FindAsync(TimeSpan.FromSeconds(1.5), ct);
        var boards = found.Select(board => new
        {
            id = board.Id,
            name = board.Name,
            host = board.Host,
            address = board.Address,
            target = board.Target,
            port = board.Port,
            firmware = board.Firmware,
            chip = board.Chip,
            requiresToken = board.RequiresToken,
            suggestedBoard = SuggestBoardForChip(board.Chip)
        });

        Emit(new { type = "wifi-boards", boards });
        return 0;
    }

    private static string SuggestBoardForChip(string? chip) =>
        (BoardIdentification.FromChip(chip) ?? BuiltInBoardProfiles.Esp32DevKit).Id;

    /// <summary>Updates the firmware over Wi-Fi: this PC serves the image, the board downloads it and restarts.</summary>
    private static async Task<int> OtaAsync(Options options, CancellationToken ct)
    {
        var profile = ResolveBoard(options)!;
        if (!profile.SupportsWifi)
            throw new InvalidOperationException($"{profile.DisplayName} has no Wi-Fi: update it over USB.");

        var host = options.Get("host") ?? options.Get("port")
            ?? throw new InvalidOperationException("Pick the board's Wi-Fi address in the Port box first.");
        if (host.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || host.StartsWith("/dev/", StringComparison.Ordinal) || host.Equals("simulator", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Updating over Wi-Fi needs the board's network address, not a USB port. Pair the board with Wi-Fi first (the Wi-Fi button), or use Upload Firmware over USB.");

        var directory = HardwareToolLocator.ResolveFirmwareDirectory(profile.Id)
            ?? throw new DirectoryNotFoundException("The firmware package was not found. Reinstall CodeBridge Visual Studio Tools.");
        var image = options.Get("firmware") ?? HardwareToolLocator.ResolvePrebuiltFirmwarePath(directory, profile.Id)
            ?? throw new FileNotFoundException($"No prebuilt firmware was found for {profile.DisplayName}.");
        var token = options.Get("token") ?? Environment.GetEnvironmentVariable("CODEBRIDGE_TOKEN");

        Emit(new { type = "status", state = "uploading", message = $"Updating {profile.DisplayName} at {host} over Wi-Fi..." });
        var lastPercent = -1;
        var progress = new Progress<int>(percent =>
        {
            if (percent == lastPercent) return;
            lastPercent = percent;
            Emit(new { type = "ota-progress", percent });
        });

        var result = await Esp32OtaUpdater.UpdateAsync(host, options.GetInt("tcp-port", 8080), token, await File.ReadAllBytesAsync(image, ct), progress, ct);
        Emit(new
        {
            type = "result",
            success = true,
            message = result.NewVersion is null
                ? "The board restarted with the new firmware."
                : $"The board restarted with firmware {result.NewVersion}."
        });
        return 0;
    }

    // ---------------------------------------------------------------- Wi-Fi pairing (USB only)

    private static async Task<(SerialTransport Serial, Esp32WifiProvisioner Provisioner)> OpenProvisionerAsync(Options options, CancellationToken ct)
    {
        var port = options.Get("port") ?? throw new InvalidOperationException("Select the board's USB serial port first: Wi-Fi setup always happens over USB.");
        var serial = new SerialTransport(port, options.GetInt("baud", 115200));
        try
        {
            await serial.ConnectAsync(ct);
            return (serial, new Esp32WifiProvisioner(serial));
        }
        catch
        {
            serial.Dispose();
            throw;
        }
    }

    private static async Task<int> WifiScanAsync(Options options, CancellationToken ct)
    {
        Emit(new { type = "status", state = "scanning", message = "Scanning for Wi-Fi networks..." });
        var (serial, provisioner) = await OpenProvisionerAsync(options, ct);
        using (serial)
        {
            var networks = await provisioner.ScanAsync(ct);
            Emit(new { type = "wifi-networks", networks = networks.Select(n => new { ssid = n.Ssid, rssi = n.Rssi, secured = n.Secured }) });
        }

        return 0;
    }

    private static async Task<int> WifiStatusAsync(Options options, CancellationToken ct)
    {
        var (serial, provisioner) = await OpenProvisionerAsync(options, ct);
        using (serial)
        {
            var status = await provisioner.GetStatusAsync(ct);
            Emit(new { type = "wifi-status", connected = status.Connected, ssid = status.Ssid, ip = status.IpAddress, port = status.Port, rssi = status.Rssi });
        }

        return 0;
    }

    /// <summary>Reads {"ssid","password"[,"token"]} from stdin so the password never appears on a command line.</summary>
    private static async Task<int> WifiConfigAsync(Options options, CancellationToken ct)
    {
        var payload = await Console.In.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(payload))
            throw new InvalidOperationException("Wi-Fi settings were not provided.");

        using var document = JsonDocument.Parse(payload);
        var ssid = document.RootElement.TryGetProperty("ssid", out var s) ? s.GetString() ?? string.Empty : string.Empty;
        var password = document.RootElement.TryGetProperty("password", out var p) ? p.GetString() ?? string.Empty : string.Empty;
        var token = document.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;

        var (serial, provisioner) = await OpenProvisionerAsync(options, ct);
        using (serial)
        {
            Emit(new { type = "status", state = "provisioning", message = $"Pairing the board and joining '{ssid}'..." });
            var result = await provisioner.ProvisionAsync(ssid, password, string.IsNullOrWhiteSpace(token) ? null : token, ct);
            Emit(new { type = "wifi-provisioned", ip = result.IpAddress, port = result.Port, token = result.AccessToken, deviceId = result.DeviceId });
        }

        return 0;
    }

    private static async Task<int> WifiUnpairAsync(Options options, CancellationToken ct)
    {
        var (serial, provisioner) = await OpenProvisionerAsync(options, ct);
        using (serial)
        {
            await provisioner.ClearTokenAsync(ct);
            Emit(new { type = "result", success = true, message = "The board no longer accepts network connections until it is paired again." });
        }

        return 0;
    }

    // ---------------------------------------------------------------- upload firmware

    private static async Task<int> UploadAsync(Options options, CancellationToken ct)
    {
        var profile = ResolveBoard(options)!;
        var port = options.Get("port") ?? throw new InvalidOperationException("Select a serial port before uploading firmware.");
        if (string.Equals(port, "simulator", StringComparison.OrdinalIgnoreCase))
        {
            Emit(new { type = "result", success = true, message = "The simulator needs no firmware. Pick a USB port to flash a real board." });
            return 0;
        }

        var uploader = FirmwareUploaderFactory.Create(new FirmwareUploadRequest(profile, port));
        Emit(new { type = "status", state = "uploading", message = $"Uploading {profile.DisplayName} firmware to {port} with {uploader.ToolName}..." });
        uploader.OutputReceived += line => Emit(new { type = "log", text = line });

        var result = await uploader.UploadAsync(ct);
        Emit(new
        {
            type = "result",
            success = result.Success,
            message = result.Success
                ? $"Firmware uploaded to {profile.DisplayName} on {port}."
                : $"Firmware upload failed (exit code {result.ExitCode?.ToString() ?? "n/a"}).",
            exitCode = result.ExitCode
        });
        return result.Success ? 0 : 1;
    }

    // ---------------------------------------------------------------- run flow

    private static async Task<int> RunAsync(Options options, CancellationTokenSource cancellation)
    {
        var document = LoadDocument(options);
        var profile = BuiltInBoardProfiles.FindById(options.Get("board") ?? document.BoardId) ?? BuiltInBoardProfiles.Default;
        var catalog = BuiltInBlockCatalog.Create(profile);
        var ct = cancellation.Token;

        var validation = new FlowValidator().Validate(document, catalog);
        foreach (var issue in validation.Issues)
            Emit(new { type = "issue", severity = issue.Severity, message = issue.Message, nodeId = issue.NodeId, connectionId = issue.ConnectionId });

        if (!validation.IsValid)
        {
            Emit(new { type = "result", success = false, message = "The flow has errors. Fix them and run again." });
            return 1;
        }

        // The extension asks for a graceful stop by writing "stop" to stdin (or by closing it).
        _ = Task.Run(async () =>
        {
            while (await Console.In.ReadLineAsync() is { } line)
            {
                if (line.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase))
                    break;
            }

            cancellation.Cancel();
        });

        Emit(new { type = "status", state = "connecting", message = $"Connecting to {profile.DisplayName}..." });
        var board = await BoardConnectionService.ConnectAsync((await CreateRequestAsync(profile, options, ct)) with { CommandObserver = PinObserver() }, ct);

        try
        {
            Emit(new { type = "connected", board = profile.Id, name = board.Name, firmware = board.FirmwareVersion });
            Emit(new { type = "status", state = "running", message = "Running flow..." });

            var loop = options.Has("loop");
            var interval = TimeSpan.FromMilliseconds(Math.Max(50, options.GetInt("interval", 1000)));
            var traceDelay = TimeSpan.FromMilliseconds(Math.Max(0, options.GetInt("trace-ms", 150)));
            var runtime = new FlowRuntime(catalog);
            var iteration = 0;

            do
            {
                iteration++;
                Emit(new { type = "iteration", number = iteration });

                await runtime.ExecuteAsync(
                    document,
                    new FlowExecutionContext
                    {
                        Board = board,
                        TraceDelay = traceDelay,
                        EventHandler = (executionEvent, _) =>
                        {
                            Emit(new
                            {
                                type = "node",
                                kind = executionEvent.Kind,
                                nodeId = executionEvent.NodeId,
                                blockType = executionEvent.BlockType,
                                message = executionEvent.Message,
                                outputs = executionEvent.Outputs?.ToDictionary(pair => pair.Key, pair => pair.Value?.ToString())
                            });
                            return ValueTask.CompletedTask;
                        }
                    },
                    ct);

                if (loop)
                    await Task.Delay(interval, ct);
            }
            while (loop && !ct.IsCancellationRequested);

            Emit(new
            {
                type = "result",
                success = true,
                message = loop
                    ? "Flow stopped."
                    : "Flow completed. The board restarts when the USB connection closes, so outputs return to their default state; use Loop to keep a flow running."
            });
            return 0;
        }
        catch (OperationCanceledException)
        {
            Emit(new { type = "result", success = true, cancelled = true, message = "Flow stopped." });
            return 0;
        }
        finally
        {
            await BoardConnectionService.DisposeBoardAsync(board);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static FlowDocument LoadDocument(Options options)
    {
        var path = options.Get("flow") ?? throw new InvalidOperationException("Missing --flow <file.cbflow>.");
        return FlowDocumentJson.Deserialize(File.ReadAllText(path));
    }

    private static BoardProfile? ResolveBoard(Options options, bool required = true)
    {
        var id = options.Get("board");
        var profile = BuiltInBoardProfiles.FindById(id);
        if (profile is null && required)
            throw new InvalidOperationException($"Unknown board '{id}'. Available: {string.Join(", ", BuiltInBoardProfiles.All.Select(b => b.Id))}.");

        return profile;
    }

    /// <summary>
    /// Reports what the board is told to do with each pin ("pin" messages) so the editor can draw it. Repeated identical states are
    /// dropped and analog readings are limited to 10 per second per pin, so a fast loop never floods the editor.
    /// </summary>
    private static Action<string, string> PinObserver()
    {
        var last = new Dictionary<(int Pin, string Kind), (double Value, long Ticks)>();
        return (command, response) =>
        {
            var activity = CodeBridge.Transport.PinActivity.Parse(command, response);
            if (activity is null)
                return;

            var now = Environment.TickCount64;
            lock (last)
            {
                var key = (activity.Pin, activity.Kind);
                if (last.TryGetValue(key, out var previous))
                {
                    if (previous.Value == activity.Value && activity.Kind != "digital")
                        return;
                    if (activity.Kind == "analog" && now - previous.Ticks < 100)
                        return;
                }

                last[key] = (activity.Value, now);
            }

            Emit(new { type = "pin", pin = activity.Pin, kind = activity.Kind, value = activity.Value });
        };
    }

    private static BoardConnectionRequest CreateRequest(BoardProfile profile, Options options)
    {
        var target = options.Get("port") ?? options.Get("host")
            ?? throw new InvalidOperationException("Select a serial port (or enter the board's IP address) before connecting.");

        // "COM3" (or /dev/tty*) is USB serial; anything else is treated as the board's Wi-Fi host name or IP.
        var isSimulator = string.Equals(target, "simulator", StringComparison.OrdinalIgnoreCase);
        var isSerial = target.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || target.StartsWith("/dev/", StringComparison.Ordinal);
        return new BoardConnectionRequest(
            profile,
            isSimulator ? CodeBridgeTransportMode.Simulator : isSerial ? CodeBridgeTransportMode.Serial : CodeBridgeTransportMode.WiFi,
            isSerial ? target : string.Empty,
            options.GetInt("baud", 115200),
            isSerial || isSimulator ? string.Empty : target,
            options.GetInt("tcp-port", 8080),
            options.Get("token") ?? Environment.GetEnvironmentVariable("CODEBRIDGE_TOKEN"));
    }

    private static int Fail(string message)
    {
        Emit(new { type = "error", message });
        return 2;
    }

    private static void Emit(object value)
    {
        var line = JsonSerializer.Serialize(value, Json);
        lock (OutputLock)
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
    }

    private sealed class Options
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public static Options Parse(IEnumerable<string> args)
        {
            var options = new Options();
            var list = args.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                if (!list[i].StartsWith("--", StringComparison.Ordinal))
                    continue;

                var key = list[i][2..];
                var hasValue = i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal);
                options._values[key] = hasValue ? list[++i] : "true";
            }

            return options;
        }

        public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

        public bool Has(string key) => _values.ContainsKey(key);

        public int GetInt(string key, int fallback) =>
            int.TryParse(Get(key), out var value) ? value : fallback;
    }
}
