using System.ComponentModel;
using System.Text;
using CodeBridge.PlatformIO;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CodeBridge.Mcp;

/// <summary>
/// Where an assistant may create and build firmware: one workspace folder (<c>CODEBRIDGE_MCP_WORKSPACE</c>, default
/// <c>Documents\CodeBridge Projects</c>). Nothing outside it can be read, written or built through these tools.
/// </summary>
public sealed class FirmwareWorkspace
{
    private static readonly string[] AllowedExtensions = { ".cpp", ".c", ".h", ".hpp", ".ino", ".ini", ".md", ".txt", ".json" };
    private const int MaxFileBytes = 256 * 1024;

    public FirmwareWorkspace(string? root = null)
    {
        Root = Path.GetFullPath(root
            ?? Environment.GetEnvironmentVariable("CODEBRIDGE_MCP_WORKSPACE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CodeBridge Projects"));
    }

    public string Root { get; }

    /// <summary>A project is addressed by its folder name; absolute paths are accepted only when they are inside the workspace.</summary>
    public string ResolveProject(string project)
    {
        if (string.IsNullOrWhiteSpace(project))
            throw new McpException("Say which firmware project (its folder name in the workspace). Call list_firmware_projects.");

        var full = Path.GetFullPath(Path.IsPathRooted(project) ? project : Path.Combine(Root, project));
        EnsureInside(full, Root, "the firmware workspace");
        if (!PioProject.IsProject(full))
            throw new McpException($"'{project}' is not a firmware project of the workspace. Call list_firmware_projects, or create_firmware_project.");

        return full;
    }

    public string NewProjectDirectory(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.StartsWith('.'))
            throw new McpException("The project name must be a simple folder name (letters, digits, dashes), without slashes or dots at the start.");

        var full = Path.GetFullPath(Path.Combine(Root, name));
        EnsureInside(full, Root, "the firmware workspace");
        return full;
    }

    /// <summary>A file inside a project, with the checks that keep an assistant from writing outside it or planting scripts.</summary>
    public string ResolveFile(string project, string relativePath, bool forWrite)
    {
        var projectDirectory = ResolveProject(project);
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new McpException("Give the file path relative to the project, for example 'src/main.cpp'.");

        var full = Path.GetFullPath(Path.Combine(projectDirectory, relativePath));
        EnsureInside(full, projectDirectory, "the project");

        var relative = Path.GetRelativePath(projectDirectory, full).Replace('\\', '/');
        if (relative.StartsWith(".pio/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase))
            throw new McpException("Build output and git data are not editable here.");

        if (!AllowedExtensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase))
            throw new McpException($"Only source and text files can be used here ({string.Join(", ", AllowedExtensions)}). Scripts are not allowed.");

        if (forWrite && Directory.Exists(full))
            throw new McpException("That path is a folder.");

        return full;
    }

    /// <summary>platformio.ini can run Python through <c>extra_scripts</c>; an assistant may configure boards and libraries, not run scripts.</summary>
    public static void CheckIniContent(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            var key = line.TrimStart();
            if (key.StartsWith("extra_scripts", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("custom_", StringComparison.OrdinalIgnoreCase) && key.Contains("script", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("platform_packages", StringComparison.OrdinalIgnoreCase) && key.Contains("file://", StringComparison.OrdinalIgnoreCase))
                throw new McpException("platformio.ini may not use extra_scripts or local package paths: they run code on the user's machine. Ask the user to add them if they are really needed.");
        }
    }

    public static void CheckSize(string content)
    {
        if (Encoding.UTF8.GetByteCount(content) > MaxFileBytes)
            throw new McpException($"The file is larger than {MaxFileBytes / 1024} KB.");
    }

    private static void EnsureInside(string path, string root, string what)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new McpException($"That location is outside {what}.");
    }
}

/// <summary>Code mode for assistants: write firmware in C++ (PlatformIO), compile it, read the compiler's diagnostics and fix them.</summary>
[McpServerToolType]
public sealed class FirmwareTools
{
    private readonly McpSession _session;
    private readonly FirmwareWorkspace _workspace;

    public FirmwareTools(McpSession session, FirmwareWorkspace workspace)
    {
        _session = session;
        _workspace = workspace;
    }

    [McpServerTool(Name = "firmware_status", ReadOnly = true, Title = "Is code mode available?")]
    [Description("Tells whether PlatformIO is installed (needed to compile firmware) and where firmware projects live. Call this first when the user wants C++ firmware instead of a flow.")]
    public string FirmwareStatus()
    {
        var tool = PioLocator.Find();
        return McpSession.ToJson(new
        {
            platformio = tool is not null,
            version = tool?.Version,
            hint = tool is null ? PioLocator.InstallHint : null,
            workspace = _workspace.Root,
            hardwareAllowed = McpSession.HardwareAllowed
        });
    }

    [McpServerTool(Name = "list_firmware_projects", ReadOnly = true, Title = "Firmware projects in the workspace")]
    [Description("The firmware projects the assistant can build: folder name and PlatformIO environments.")]
    public string ListFirmwareProjects()
    {
        var projects = Directory.Exists(_workspace.Root)
            ? Directory.EnumerateDirectories(_workspace.Root).Where(PioProject.IsProject)
                .Select(d => new { name = Path.GetFileName(d), environments = PioProject.Environments(d) }).OrderBy(p => p.name).ToList()
            : new();
        return McpSession.ToJson(new { workspace = _workspace.Root, projects });
    }

    [McpServerTool(Name = "create_firmware_project", Destructive = false, Title = "Create a firmware project")]
    [Description(
        "Creates a PlatformIO project for a board in the workspace: platformio.ini, src/main.cpp (a working starting point), README. Templates: 'blink', 'serial' " +
        "(prints light:<n>,counter:<n> for the plotter) or 'empty'. Before writing code that uses pins, read get_board_manifest for that board.")]
    public string CreateFirmwareProject(
        [Description("Board id from list_boards.")] string boardId,
        [Description("Folder name of the new project.")] string name,
        [Description("'blink' (default), 'serial' or 'empty'.")] string template = "blink")
    {
        if (PioProject.BoardFor(boardId) is null)
            throw new McpException($"Unknown board '{boardId}'. Call list_boards.");

        var kind = template.ToLowerInvariant() switch
        {
            "serial" or "plot" => FirmwareTemplate.SerialPlot,
            "empty" => FirmwareTemplate.Empty,
            "blink" => FirmwareTemplate.Blink,
            _ => throw new McpException("template must be 'blink', 'serial' or 'empty'.")
        };

        var directory = _workspace.NewProjectDirectory(name);
        try
        {
            var files = PioProject.Create(directory, boardId, kind);
            return McpSession.ToJson(new { project = name, directory, files, next = "Edit src/main.cpp with write_firmware_file, then build_firmware." });
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "read_firmware_file", ReadOnly = true, Title = "Read a file of a firmware project")]
    [Description("Reads a source or text file of a firmware project (for example 'src/main.cpp').")]
    public string ReadFirmwareFile([Description("Project folder name.")] string project, [Description("Path inside the project.")] string path)
    {
        var full = _workspace.ResolveFile(project, path, forWrite: false);
        if (!File.Exists(full))
            throw new McpException($"'{path}' does not exist in '{project}'.");

        FirmwareWorkspace.CheckSize(File.ReadAllText(full));
        return File.ReadAllText(full);
    }

    [McpServerTool(Name = "write_firmware_file", Destructive = true, Idempotent = true, Title = "Write a file of a firmware project")]
    [Description("Creates or replaces a source or text file inside a firmware project (.cpp .c .h .hpp .ino .ini .md .txt .json). Scripts and platformio.ini 'extra_scripts' are refused. Then call build_firmware to compile.")]
    public string WriteFirmwareFile(
        [Description("Project folder name.")] string project,
        [Description("Path inside the project, e.g. 'src/main.cpp' or 'include/pins.h'.")] string path,
        [Description("The complete new content of the file.")] string content)
    {
        FirmwareWorkspace.CheckSize(content);
        var full = _workspace.ResolveFile(project, path, forWrite: true);
        if (Path.GetFileName(full).Equals("platformio.ini", StringComparison.OrdinalIgnoreCase))
            FirmwareWorkspace.CheckIniContent(content);

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content.Replace("\r\n", "\n"), new UTF8Encoding(false));
        return McpSession.ToJson(new { ok = true, path, bytes = Encoding.UTF8.GetByteCount(content) });
    }

    [McpServerTool(Name = "build_firmware", OpenWorld = true, Title = "Compile a firmware project")]
    [Description(
        "Compiles the project with PlatformIO (the first build of a board downloads its toolchain: it can take minutes). Returns success, every compiler error and warning " +
        "with file, line and column, and the end of the log. Fix the errors and build again until it succeeds. Compiling never touches hardware.")]
    public async Task<string> BuildFirmware(
        [Description("Project folder name.")] string project,
        [Description("Optional PlatformIO environment name (from list_firmware_projects).")] string? environment = null,
        CancellationToken ct = default)
    {
        var tool = PioLocator.Find() ?? throw new McpException(PioLocator.InstallHint);
        var directory = _workspace.ResolveProject(project);

        var log = new List<string>();
        var result = await PioRunner.BuildAsync(tool, directory, environment, line => { lock (log) log.Add(line); }, ct);
        return McpSession.ToJson(Describe(result, log));
    }

    [McpServerTool(Name = "upload_firmware", Destructive = true, OpenWorld = true, Title = "Compile and upload firmware to a real board")]
    [Description(
        "Compiles and flashes the project onto a REAL board over USB, replacing the program on it (including CodeBridge's own firmware: afterwards the board no longer " +
        "answers CodeBridge flows until it is flashed again from Visual Studio). Only available when the user enabled hardware, and only after the user approved: pass confirmed=true.")]
    public async Task<string> UploadFirmware(
        [Description("Project folder name.")] string project,
        [Description("The board's serial port, for example COM3.")] string port,
        [Description("true only after the user approved this exact upload.")] bool confirmed = false,
        [Description("Optional PlatformIO environment name.")] string? environment = null,
        CancellationToken ct = default)
    {
        if (!McpSession.HardwareAllowed)
            throw new McpException($"Real hardware is switched off. The user must start the server with {McpSession.HardwareSwitch}=1 before firmware can be uploaded.");
        if (!confirmed)
            throw new McpException($"Uploading replaces the program on the board at {port}. Tell the user, wait for their approval, then call again with confirmed=true.");
        if (string.IsNullOrWhiteSpace(port) || !(port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || port.StartsWith("/dev/", StringComparison.Ordinal)))
            throw new McpException("port must be a serial port such as COM3.");

        var tool = PioLocator.Find() ?? throw new McpException(PioLocator.InstallHint);
        var directory = _workspace.ResolveProject(project);

        // the board connection of this session may hold the port
        if (_session.Board is { IsSimulated: false } held && string.Equals(_session.Port, port, StringComparison.OrdinalIgnoreCase))
            await held.DisconnectAsync();

        var log = new List<string>();
        var result = await PioRunner.UploadAsync(tool, directory, port, environment, line => { lock (log) log.Add(line); }, ct);
        return McpSession.ToJson(Describe(result, log));
    }

    private static object Describe(PioRunResult result, List<string> log)
    {
        string[] tail;
        lock (log)
            tail = log.TakeLast(30).ToArray();

        return new
        {
            success = result.Success,
            seconds = Math.Round(result.Duration.TotalSeconds, 1),
            errors = result.Errors,
            warnings = result.Warnings,
            diagnostics = result.Diagnostics.Select(d => new { d.Severity, d.File, d.Line, d.Column, d.Message }),
            logTail = tail
        };
    }
}
