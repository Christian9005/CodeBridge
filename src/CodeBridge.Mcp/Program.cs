using CodeBridge.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// MCP over standard input/output: stdout carries the protocol, so every log line goes to stderr.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<McpSession>();
builder.Services.AddSingleton(new FirmwareWorkspace());
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "codebridge", Version = typeof(McpSession).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" };
        options.ServerInstructions =
            "CodeBridge connects microcontroller boards (ESP32, Arduino) to .NET. Before using any pin, call get_board_manifest: it lists what each pin can do, which are unsafe, and the rules of the board. " +
            "Design flows with list_block_types and the 'codebridge://guide/flow-format' resource, check them with validate_flow, test them on the simulator with simulate_input and run_flow " +
            "(the default board is a safe virtual one) and read get_pin_states to confirm the right pins moved. For C++ firmware instead of flows (code mode) use firmware_status, create_firmware_project, write_firmware_file and build_firmware: the compiler reports every error with file and line. Real hardware is off unless the user enabled it, and every physical action then needs the user's approval (confirmed=true).";
    })
    .WithStdioServerTransport()
    .WithTools<BoardTools>()
    .WithTools<FlowTools>()
    .WithTools<FirmwareTools>()
    .WithResources<GuideResources>();

await builder.Build().RunAsync();
