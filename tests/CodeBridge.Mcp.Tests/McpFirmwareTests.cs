using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodeBridge.Mcp.Tests;

/// <summary>Code mode through the real server: an assistant writes C++, the compiler complains, it fixes the code, the build succeeds.</summary>
public sealed class McpFirmwareTests : IAsyncLifetime
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "codebridge-mcp-ws-" + Guid.NewGuid().ToString("N")[..8]);
    private McpClient _client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_workspace);
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var configuration = baseDir.Split(Path.DirectorySeparatorChar)[^2];
        var root = new DirectoryInfo(baseDir);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "CodeBridge.Mcp")))
            root = root.Parent;

        var dll = Path.Combine(root!.FullName, "src", "CodeBridge.Mcp", "bin", configuration, "net8.0", "codebridge-mcp.dll");
        _client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "codebridge",
            Command = "dotnet",
            Arguments = new[] { dll },
            EnvironmentVariables = new Dictionary<string, string?> { ["CODEBRIDGE_MCP_WORKSPACE"] = _workspace, ["CODEBRIDGE_MCP_ALLOW_HARDWARE"] = "0" }
        }));
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        try { Directory.Delete(_workspace, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<(bool IsError, string Text)> Call(string tool, Dictionary<string, object?> args)
    {
        var result = await _client.CallToolAsync(tool, args);
        return (result.IsError == true, string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static bool PlatformIoInstalled => CodeBridge.PlatformIO.PioLocator.Find() is not null;

    [Fact]
    public async Task The_code_mode_tools_are_announced_and_the_status_names_the_workspace()
    {
        var tools = (await _client.ListToolsAsync()).Select(t => t.Name).ToList();
        foreach (var expected in new[] { "firmware_status", "list_firmware_projects", "create_firmware_project", "read_firmware_file", "write_firmware_file", "build_firmware", "upload_firmware" })
            Assert.Contains(expected, tools);

        var (_, status) = await Call("firmware_status", new());
        Assert.Equal(_workspace, Json(status).GetProperty("workspace").GetString());
    }

    [Fact]
    public async Task An_assistant_can_write_code_get_the_compiler_errors_fix_them_and_build()
    {
        if (!PlatformIoInstalled)
            return; // PlatformIO is not installed on this machine

        var (created, text) = await Call("create_firmware_project", new() { ["boardId"] = "arduino-uno", ["name"] = "lamp", ["template"] = "empty" });
        Assert.False(created, text);

        // a mistake...
        await Call("write_firmware_file", new() { ["project"] = "lamp", ["path"] = "src/main.cpp", ["content"] = "#include <Arduino.h>\nvoid setup() { pinMode(LED_BUILTIN, OUTPUT); blinkTwice(); }\nvoid loop() {}\n" });
        var (_, broken) = await Call("build_firmware", new() { ["project"] = "lamp" });
        Assert.True(broken.StartsWith("{"), broken);
        var failure = Json(broken);
        Assert.False(failure.GetProperty("success").GetBoolean());
        var error = failure.GetProperty("diagnostics").EnumerateArray().First(d => d.GetProperty("severity").GetString() == "error");
        Assert.Equal(2, error.GetProperty("line").GetInt32());
        Assert.Contains("blinkTwice", error.GetProperty("message").GetString());

        // ...and the fix
        await Call("write_firmware_file", new() { ["project"] = "lamp", ["path"] = "src/main.cpp", ["content"] = "#include <Arduino.h>\nvoid blinkTwice() { for (int i = 0; i < 2; i++) { digitalWrite(LED_BUILTIN, HIGH); delay(100); digitalWrite(LED_BUILTIN, LOW); delay(100); } }\nvoid setup() { pinMode(LED_BUILTIN, OUTPUT); blinkTwice(); }\nvoid loop() {}\n" });
        var (_, fixedBuild) = await Call("build_firmware", new() { ["project"] = "lamp" });
        Assert.True(Json(fixedBuild).GetProperty("success").GetBoolean(), fixedBuild);

        var (_, read) = await Call("read_firmware_file", new() { ["project"] = "lamp", ["path"] = "src/main.cpp" });
        Assert.Contains("blinkTwice", read);

        var (_, list) = await Call("list_firmware_projects", new());
        Assert.Equal("lamp", Json(list).GetProperty("projects")[0].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("../outside", "src/main.cpp")]
    [InlineData("lamp", "../../etc/passwd")]
    [InlineData("lamp", "..\\..\\secret.txt")]
    [InlineData("lamp", "C:\\Windows\\win.ini")]
    [InlineData("lamp", "extra.py")]
    [InlineData("lamp", "tools/run.bat")]
    [InlineData("lamp", ".pio/build/firmware.cpp")]
    public async Task Files_outside_the_project_scripts_and_build_output_cannot_be_written(string project, string path)
    {
        await Call("create_firmware_project", new() { ["boardId"] = "arduino-uno", ["name"] = "lamp", ["template"] = "empty" });

        var (isError, text) = await Call("write_firmware_file", new() { ["project"] = project, ["path"] = path, ["content"] = "x" });

        Assert.True(isError, text);
        Assert.False(File.Exists(Path.Combine(_workspace, "..", "secret.txt")));
        Assert.False(File.Exists(Path.Combine(_workspace, "lamp", "extra.py")));
    }

    [Fact]
    public async Task A_platformio_ini_that_would_run_scripts_is_refused()
    {
        await Call("create_firmware_project", new() { ["boardId"] = "arduino-uno", ["name"] = "lamp", ["template"] = "empty" });
        var ini = "[env:uno]\nplatform = atmelavr\nboard = uno\nframework = arduino\nextra_scripts = pre:evil.py\n";

        var (isError, text) = await Call("write_firmware_file", new() { ["project"] = "lamp", ["path"] = "platformio.ini", ["content"] = ini });

        Assert.True(isError);
        Assert.Contains("extra_scripts", text);

        var (ok, _) = await Call("write_firmware_file", new() { ["project"] = "lamp", ["path"] = "platformio.ini", ["content"] = ini.Replace("extra_scripts = pre:evil.py\n", "lib_deps = Servo\n") });
        Assert.False(ok);
    }

    [Fact]
    public async Task Project_names_cannot_escape_the_workspace_and_uploads_need_the_users_switch()
    {
        var (badName, nameText) = await Call("create_firmware_project", new() { ["boardId"] = "arduino-uno", ["name"] = "..\\evil" });
        Assert.True(badName, nameText);

        var (unknownBoard, _) = await Call("create_firmware_project", new() { ["boardId"] = "esp8266", ["name"] = "x" });
        Assert.True(unknownBoard);

        await Call("create_firmware_project", new() { ["boardId"] = "arduino-uno", ["name"] = "lamp", ["template"] = "empty" });
        var (upload, uploadText) = await Call("upload_firmware", new() { ["project"] = "lamp", ["port"] = "COM3", ["confirmed"] = true });
        Assert.True(upload);
        Assert.Contains("CODEBRIDGE_MCP_ALLOW_HARDWARE", uploadText);
    }

    [Fact]
    public async Task A_project_cannot_be_created_twice_over_existing_files()
    {
        await Call("create_firmware_project", new() { ["boardId"] = "arduino-uno", ["name"] = "lamp" });

        var (isError, text) = await Call("create_firmware_project", new() { ["boardId"] = "arduino-uno", ["name"] = "lamp" });

        Assert.True(isError);
        Assert.Contains("already", text);
    }
}
