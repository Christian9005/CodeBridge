using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using CodeBridge.Flow;
using CodeBridge.Flow.CodeGeneration;
using CodeBridge.Flow.Execution;
using CodeBridge.Flow.Serialization;
using CodeBridge.Flow.Validation;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CodeBridge.Mcp;

/// <summary>Tools to design, check, test and export CodeBridge flows (the .cbflow JSON).</summary>
[McpServerToolType]
public sealed class FlowTools
{
    private readonly McpSession _session;

    public FlowTools(McpSession session) => _session = session;

    [McpServerTool(Name = "list_block_types", ReadOnly = true, Title = "Blocks available for a board")]
    [Description(
        "The building blocks of a flow for this board: each block's type id, ports (inputs/outputs and their kinds), and properties with defaults and allowed values " +
        "(pin properties list only the pins valid for that block on this board). Read the 'codebridge://guide/flow-format' resource for how to assemble them.")]
    public string ListBlockTypes([Description("A board id from list_boards.")] string boardId)
    {
        var board = BuiltInBoardProfiles.FindById(boardId) ?? throw new McpException($"Unknown board '{boardId}'. Call list_boards.");
        var catalog = BuiltInBlockCatalog.Create(board);

        return McpSession.ToJson(catalog.Blocks.OrderBy(b => b.Category).ThenBy(b => b.DisplayName).Select(block => new
        {
            type = block.Type,
            name = block.DisplayName,
            category = block.Category,
            description = block.Description,
            ports = block.Ports.Select(p => new { p.Name, direction = p.Direction.ToString().ToLowerInvariant(), kind = p.ValueKind.ToString().ToLowerInvariant(), p.Required }),
            properties = block.Properties.Select(p => new
            {
                p.Name,
                kind = p.ValueKind.ToString().ToLowerInvariant(),
                p.Required,
                p.IsAdvanced,
                @default = p.DefaultValue,
                allowed = Allowed(p)
            })
        }));
    }

    [McpServerTool(Name = "validate_flow", ReadOnly = true, Title = "Check a flow for mistakes")]
    [Description(
        "Checks a flow (.cbflow JSON) against the board: unknown blocks, wrong or incompatible connections, missing required inputs, loops, pins that do not exist, " +
        "reserved pins, pins without the needed capability, and pin conflicts. Fix every error before running. Returns the same messages the Visual Studio editor shows.")]
    public string ValidateFlow(
        [Description("The flow as .cbflow JSON.")] string flowJson,
        [Description("Board id; defaults to the board written in the flow.")] string? boardId = null)
    {
        var (document, board) = Parse(flowJson, boardId);
        var result = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create(board));

        return McpSession.ToJson(new
        {
            valid = result.IsValid,
            board = board.Id,
            errors = result.Errors.Select(Issue),
            warnings = result.Warnings.Select(Issue)
        });
    }

    [McpServerTool(Name = "export_csharp", ReadOnly = true, Title = "Turn a flow into C# code")]
    [Description("Generates readable C# that uses the CodeBridge SDK and does exactly what the flow does. Mode 'class' gives a static class to add to a project; 'console' gives a complete Program.cs.")]
    public string ExportCSharp(
        [Description("The flow as .cbflow JSON.")] string flowJson,
        [Description("'class' (default) or 'console'.")] string mode = "class",
        [Description("Optional class name.")] string? className = null,
        [Description("Optional namespace.")] string? @namespace = null,
        [Description("Repeat the flow until stopped.")] bool loop = false)
    {
        var (document, _) = Parse(flowJson, null);

        FlowCSharpResult result;
        try
        {
            result = FlowCSharpGenerator.Generate(document, new FlowCSharpOptions
            {
                ClassName = className,
                Namespace = string.IsNullOrWhiteSpace(@namespace) ? "CodeBridge.Flows" : @namespace!,
                Mode = mode.Equals("console", StringComparison.OrdinalIgnoreCase) ? FlowCSharpMode.ConsoleApp : FlowCSharpMode.Class,
                Loop = loop
            });
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message);
        }

        return McpSession.ToJson(new { className = result.ClassName, code = result.Code, warnings = result.Warnings });
    }

    [McpServerTool(Name = "run_flow", Destructive = true, Title = "Run a flow on the board")]
    [Description(
        "Validates and runs a flow once on the connected board (the simulator by default) and reports every block's outputs plus what happened on each pin, " +
        "so you can verify the design really drives the pins you intended. Use simulate_input first to set the sensor values you want to test. " +
        "On real hardware pass confirmed=true only after the user approved.")]
    public async Task<string> RunFlow(
        [Description("The flow as .cbflow JSON.")] string flowJson,
        [Description("Only needed on real hardware: true once the user approved running this flow.")] bool confirmed = false,
        [Description("Give up after this many seconds (1-120, default 30).")] int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        var (document, profile) = Parse(flowJson, null);

        // A simulated session follows the flow's board; real hardware must match.
        if (_session.Profile is null || _session.Profile.Id != profile.Id)
        {
            if (_session.Board is { IsSimulated: false })
                throw new McpException($"The connected board is {_session.Profile?.DisplayName}, but the flow targets {profile.DisplayName}. Connect the right board or fix the flow's boardId.");

            await _session.ConnectAsync(profile.Id, "simulator", ct);
        }

        var board = await _session.EnsureBoardAsync(ct);
        _session.RequireConsentForHardware(confirmed, "Running this flow");

        var validation = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create(profile));
        if (!validation.IsValid)
        {
            return McpSession.ToJson(new
            {
                success = false,
                reason = "The flow has errors; nothing was run.",
                errors = validation.Errors.Select(Issue),
                warnings = validation.Warnings.Select(Issue)
            });
        }

        var mark = _session.EventMark;
        var watch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 120)));

        try
        {
            var result = await board.UseAsync(
                (b, token) => new FlowRuntime(BuiltInBlockCatalog.Create(profile)).ExecuteAsync(document, b, token),
                timeout.Token);

            return McpSession.ToJson(new
            {
                success = true,
                simulated = _session.IsSimulated,
                durationMs = watch.ElapsedMilliseconds,
                nodes = document.Nodes.Select(node => new
                {
                    id = node.Id,
                    type = node.Type,
                    outputs = result.NodeOutputs.TryGetValue(node.Id, out var outputs)
                        ? outputs.ToDictionary(o => o.Key, o => o.Value?.ToString())
                        : new Dictionary<string, string?>()
                }),
                pinActivity = _session.EventsSince(mark),
                pins = _session.PinStates.Select(p => new { p.Pin, p.Kind, p.Value }),
                warnings = validation.Warnings.Select(Issue)
            });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new McpException($"The flow did not finish within {timeoutSeconds} seconds and was stopped.");
        }
        catch (Exception ex) when (ex is not McpException and not OperationCanceledException)
        {
            return McpSession.ToJson(new { success = false, reason = ex.Message, pinActivity = _session.EventsSince(mark) });
        }
    }

    // ------------------------------------------------------------------ helpers

    private static (FlowDocument Document, BoardProfile Board) Parse(string flowJson, string? boardId)
    {
        if (string.IsNullOrWhiteSpace(flowJson))
            throw new McpException("flowJson is empty. Read 'codebridge://guide/flow-format' for the format.");

        FlowDocument document;
        try
        {
            document = FlowDocumentJson.Deserialize(flowJson);
        }
        catch (JsonException ex)
        {
            throw new McpException($"The flow is not valid JSON for a .cbflow: {ex.Message} Read 'codebridge://guide/flow-format' for the format.");
        }

        var id = string.IsNullOrWhiteSpace(boardId) ? document.BoardId : boardId;
        var board = BuiltInBoardProfiles.FindById(id) ?? throw new McpException($"Unknown board '{id}'. Call list_boards, and set \"BoardId\" in the flow.");
        return (document, board);
    }

    private static object Issue(FlowValidationIssue issue) => new { node = issue.NodeId, connection = issue.ConnectionId, message = issue.Message };

    private static object? Allowed(FlowPropertyDefinition property)
    {
        if (property.Options is not { Count: > 0 } options)
            return null;

        // A pin list can have dozens of entries: numbers are enough, the manifest explains them.
        return property.Name == "pin" && options.Count > 8
            ? options.Select(o => o.Value).ToList()
            : options.Select(o => new { o.Label, o.Value }).ToList();
    }
}
