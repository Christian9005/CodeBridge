using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodeBridge.Mcp.Tests;

/// <summary>The server is started as a real process and spoken to over stdio with the official MCP client, exactly like Claude would.</summary>
public sealed class McpServerTests : IAsyncLifetime
{
    private McpClient _client = null!;

    public async Task InitializeAsync() => _client = await StartAsync(hardware: false);

    public async Task DisposeAsync() => await _client.DisposeAsync();

    private static string ServerDll()
    {
        // tests/<project>/bin/<Configuration>/net8.0/ -> src/CodeBridge.Mcp/bin/<Configuration>/net8.0/
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var parts = baseDir.Split(Path.DirectorySeparatorChar);
        var configuration = parts[^2];
        var root = new DirectoryInfo(baseDir);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "CodeBridge.Mcp")))
            root = root.Parent;

        var dll = Path.Combine(root!.FullName, "src", "CodeBridge.Mcp", "bin", configuration, "net8.0", "codebridge-mcp.dll");
        Assert.True(File.Exists(dll), $"Server not built: {dll}");
        return dll;
    }

    private static async Task<McpClient> StartAsync(bool hardware)
    {
        var options = new StdioClientTransportOptions
        {
            Name = "codebridge",
            Command = "dotnet",
            Arguments = new[] { ServerDll() },
            EnvironmentVariables = new Dictionary<string, string?> { ["CODEBRIDGE_MCP_ALLOW_HARDWARE"] = hardware ? "1" : "0" }
        };
        return await McpClient.CreateAsync(new StdioClientTransport(options));
    }

    private async Task<(bool IsError, string Text)> Call(string tool, Dictionary<string, object?>? args = null)
    {
        var result = await _client.CallToolAsync(tool, args ?? new Dictionary<string, object?>());
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        return (result.IsError == true, text);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private const string NightLight = """
        {
          "Name": "Night light", "BoardId": "esp32-devkit", "Version": 1,
          "Nodes": [
            { "Id": "start", "Type": "flow.manual-trigger", "Position": { "X": 60,  "Y": 120 }, "Parameters": {} },
            { "Id": "read",  "Type": "gpio.analog-read",    "Position": { "X": 300, "Y": 120 }, "Parameters": { "pin": 34 } },
            { "Id": "map",   "Type": "math.map",            "Position": { "X": 540, "Y": 120 }, "Parameters": { "inMin": 0, "inMax": 4095, "outMin": 255, "outMax": 0 } },
            { "Id": "lamp",  "Type": "gpio.pwm-write",      "Position": { "X": 780, "Y": 120 }, "Parameters": { "pin": 4 } }
          ],
          "Connections": [
            { "Id": "c1", "FromNodeId": "start", "FromPort": "trigger", "ToNodeId": "read", "ToPort": "trigger" },
            { "Id": "c2", "FromNodeId": "read",  "FromPort": "value",   "ToNodeId": "map",  "ToPort": "value" },
            { "Id": "c3", "FromNodeId": "map",   "FromPort": "result",  "ToNodeId": "lamp", "ToPort": "duty" }
          ]
        }
        """;

    [Fact]
    public async Task The_server_announces_its_tools_resources_and_instructions()
    {
        var tools = (await _client.ListToolsAsync()).Select(t => t.Name).ToList();
        foreach (var expected in new[] { "list_boards", "get_board_manifest", "connect_board", "read_pin", "set_pin", "simulate_input", "get_pin_states", "list_block_types", "validate_flow", "export_csharp", "run_flow" })
            Assert.Contains(expected, tools);

        var resources = await _client.ListResourcesAsync();
        Assert.Contains(resources, r => r.Uri == "codebridge://guide/flow-format");
        Assert.Contains(resources, r => r.Uri == "codebridge://guide/safety");

        Assert.Contains("get_board_manifest", _client.ServerInstructions);
    }

    [Fact]
    public async Task The_manifest_tells_the_assistant_which_pins_are_safe_and_what_they_can_do()
    {
        var (isError, text) = await Call("get_board_manifest", new() { ["boardId"] = "esp32-devkit" });
        Assert.False(isError);

        var manifest = Json(text);
        Assert.Equal(4095, manifest.GetProperty("analogMax").GetInt32());
        Assert.Equal(3.3, manifest.GetProperty("logicLevelVolts").GetDouble());
        Assert.Equal(2, manifest.GetProperty("builtInLedPin").GetInt32());

        var pins = manifest.GetProperty("pins").EnumerateArray().ToDictionary(p => p.GetProperty("number").GetInt32());
        Assert.Equal("input-only", pins[34].GetProperty("safety").GetString());
        Assert.Contains("analog-read", pins[34].GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal("reserved", pins[1].GetProperty("safety").GetString());
        Assert.Equal("boot-strap", pins[0].GetProperty("safety").GetString());
        Assert.Contains(21, manifest.GetProperty("buses").GetProperty("i2c-sda").EnumerateArray().Select(n => n.GetInt32()));
        Assert.Contains(manifest.GetProperty("rules").EnumerateArray(), r => r.GetString()!.Contains("Map"));
    }

    [Fact]
    public async Task The_manifest_is_also_a_resource()
    {
        var content = await _client.ReadResourceAsync("codebridge://boards/arduino-mega");

        var text = ((TextResourceContents)content.Contents[0]).Text;
        Assert.Equal(69, Json(text).GetProperty("maxPin").GetInt32());
    }

    [Fact]
    public async Task An_unknown_board_gets_a_helpful_error_not_a_crash()
    {
        var (isError, text) = await Call("get_board_manifest", new() { ["boardId"] = "esp8266" });

        Assert.True(isError);
        Assert.Contains("list_boards", text);
    }

    [Fact]
    public async Task A_flow_is_validated_and_mistakes_are_explained()
    {
        var (_, ok) = await Call("validate_flow", new() { ["flowJson"] = NightLight });
        Assert.True(Json(ok).GetProperty("valid").GetBoolean());

        // wiring an analog reading into a boolean input is the classic mistake
        var broken = NightLight.Replace("\"ToPort\": \"duty\"", "\"ToPort\": \"trigger\"");
        var (_, bad) = await Call("validate_flow", new() { ["flowJson"] = broken });
        var result = Json(bad);
        Assert.False(result.GetProperty("valid").GetBoolean());
        Assert.NotEmpty(result.GetProperty("errors").EnumerateArray());

        var (isError, garbage) = await Call("validate_flow", new() { ["flowJson"] = "{ not json" });
        Assert.True(isError);
        Assert.Contains("flow-format", garbage);
    }

    [Fact]
    public async Task A_flow_runs_on_the_simulator_and_the_pin_activity_proves_it()
    {
        await Call("simulate_input", new() { ["pin"] = 34, ["kind"] = "analog", ["value"] = 0 }); // pitch dark

        var (isError, text) = await Call("run_flow", new() { ["flowJson"] = NightLight });
        Assert.False(isError, text);

        var run = Json(text);
        Assert.True(run.GetProperty("success").GetBoolean());
        Assert.True(run.GetProperty("simulated").GetBoolean());

        // dark room -> map 0..4095 to 255..0 -> full brightness on pin 4
        var pins = run.GetProperty("pins").EnumerateArray().ToDictionary(p => p.GetProperty("pin").GetInt32());
        Assert.Equal("pwm", pins[4].GetProperty("kind").GetString());
        Assert.Equal(255, pins[4].GetProperty("value").GetDouble());
        Assert.Contains(run.GetProperty("pinActivity").EnumerateArray(), e => e.GetString()!.StartsWith("pin 4: pwm 255"));

        // and a bright room dims it
        await Call("simulate_input", new() { ["pin"] = 34, ["kind"] = "analog", ["value"] = 4095 });
        await Call("run_flow", new() { ["flowJson"] = NightLight });
        var (_, states) = await Call("get_pin_states");
        Assert.Equal(0, Json(states).EnumerateArray().First(p => p.GetProperty("pin").GetInt32() == 4).GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task A_flow_with_errors_is_not_run()
    {
        var (failed, text) = await Call("run_flow", new() { ["flowJson"] = NightLight.Replace("\"pin\": 4", "\"pin\": 99") });
        Assert.False(failed, text);

        var run = Json(text);
        Assert.False(run.GetProperty("success").GetBoolean());
        Assert.NotEmpty(run.GetProperty("errors").EnumerateArray());
    }

    [Fact]
    public async Task The_flow_can_be_exported_as_csharp()
    {
        var (isError, text) = await Call("export_csharp", new() { ["flowJson"] = NightLight, ["className"] = "NightLamp" });

        Assert.False(isError);
        var code = Json(text).GetProperty("code").GetString()!;
        Assert.Contains("class NightLamp", code);
        Assert.Contains("PwmWriteAsync(4", code);
    }

    [Fact]
    public async Task Dangerous_pins_are_refused_even_on_the_simulator()
    {
        var (reserved, text1) = await Call("set_pin", new() { ["pin"] = 1, ["kind"] = "digital", ["value"] = 1 });
        Assert.True(reserved);
        Assert.Contains("reserved", text1);

        var (inputOnly, text2) = await Call("set_pin", new() { ["pin"] = 34, ["kind"] = "digital", ["value"] = 1 });
        Assert.True(inputOnly);
        Assert.Contains("input only", text2);

        var (noPwm, text3) = await Call("set_pin", new() { ["pin"] = 4, ["kind"] = "pwm", ["value"] = 300 });
        Assert.True(noPwm);
        Assert.Contains("0 to 255", text3);

        var (ok, text4) = await Call("set_pin", new() { ["pin"] = 4, ["kind"] = "pwm", ["value"] = 128 });
        Assert.False(ok, text4);
        var (_, read) = await Call("get_pin_states");
        Assert.Contains(Json(read).EnumerateArray(), p => p.GetProperty("pin").GetInt32() == 4 && p.GetProperty("value").GetDouble() == 128);
    }

    [Fact]
    public async Task Real_hardware_is_refused_unless_the_human_enabled_it()
    {
        var (isError, text) = await Call("connect_board", new() { ["boardId"] = "esp32-devkit", ["port"] = "COM3" });

        Assert.True(isError);
        Assert.Contains("CODEBRIDGE_MCP_ALLOW_HARDWARE", text);
        // and the simulator keeps working
        var (_, status) = await Call("board_status");
        Assert.True(Json(status).GetProperty("simulated").GetBoolean());
    }

    [Fact]
    public async Task With_hardware_enabled_a_board_that_is_not_there_is_reported_not_faked()
    {
        await using var client = await StartAsync(hardware: true);

        var result = await client.CallToolAsync("connect_board", new Dictionary<string, object?> { ["boardId"] = "esp32-devkit", ["port"] = "COM250" });

        Assert.True(result.IsError);
        Assert.Contains("Could not connect", string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
    }

    [Fact]
    public async Task Block_types_follow_the_board()
    {
        var (_, esp32) = await Call("list_block_types", new() { ["boardId"] = "esp32-devkit" });
        var (_, uno) = await Call("list_block_types", new() { ["boardId"] = "arduino-uno" });

        string MapMax(string json) => Json(json).EnumerateArray()
            .First(b => b.GetProperty("type").GetString() == "math.map")
            .GetProperty("properties").EnumerateArray()
            .First(p => p.GetProperty("name").GetString() == "inMax")
            .GetProperty("default").ToString();

        Assert.Equal("4095", MapMax(esp32));
        Assert.Equal("1023", MapMax(uno));
    }
}
