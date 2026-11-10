using System.Reflection;
using CodeBridge.Core.Abstractions;
using CodeBridge.Core.Enums;
using CodeBridge.ESP32;
using CodeBridge.Flow;
using CodeBridge.Flow.CodeGeneration;
using CodeBridge.Flow.Execution;
using CodeBridge.Flow.Validation;
using CodeBridge.IotBindings;
using CodeBridge.Transport.Simulation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeBridge.Matrix.Tests;

/// <summary>One block on one board, with what happened when it was validated, run on the virtual board and exported.</summary>
internal sealed class Cell
{
    public required BoardProfile Board { get; init; }
    public required FlowBlockDefinition Block { get; init; }
    public required FlowDocument Flow { get; init; }

    /// <summary>What the matrix had to add around the block to give it its inputs (trigger, constants).</summary>
    public string Setup { get; init; } = "";

    public IReadOnlyList<string> Errors { get; set; } = [];
    public IReadOnlyList<string> Warnings { get; set; } = [];
    public bool Valid => Errors.Count == 0;

    public RunResult? Run { get; set; }
    public string? ExportProblem { get; set; }
    public string? Exported { get; set; }

    public override string ToString() => $"{Board.Id} / {Block.Type}";
}

internal enum RunKind
{
    /// <summary>The flow ran to the end.</summary>
    Completed,

    /// <summary>The block asked the board for something that is not there (a chip that does not answer, no echo, a failed checksum): a clean, expected answer on a board with nothing attached.</summary>
    NothingAttached,

    /// <summary>Anything else: a crash, a block the runtime does not know, a hang.</summary>
    Failed
}

internal sealed record RunResult(RunKind Kind, string Detail, IReadOnlyList<string> Commands);

internal static class Matrix
{
    static Matrix() => IotBindingBlocks.Register();

    public static IReadOnlyList<BoardProfile> Boards => BuiltInBoardProfiles.All;

    public static FlowBlockCatalog CatalogOf(BoardProfile board) => BuiltInBlockCatalog.Create(board);

    public static IEnumerable<Cell> Cells()
    {
        foreach (var board in Boards)
        {
            foreach (var block in CatalogOf(board).Blocks.OrderBy(b => b.Type, StringComparer.Ordinal))
                yield return Build(board, block);
        }
    }

    /// <summary>The smallest flow that gives the block everything it needs: a manual trigger for a trigger input, a constant for a data input.</summary>
    public static Cell Build(BoardProfile board, FlowBlockDefinition block)
    {
        var document = new FlowDocument { Name = block.Type.Replace('.', '-') + "-on-" + board.Id, BoardId = board.Id };
        var setup = new List<string>();
        var target = new FlowNode { Id = "block", Type = block.Type };
        document.Nodes.Add(target);

        // What a person does in the editor: a property that must be chosen (a pin) and has no default is picked from the list of the board.
        foreach (var property in block.Properties.Where(p => p.Options is { Count: > 0 }))
        {
            var offered = property.Options!.Select(o => o.Value).ToList();
            // only a property with no default is chosen; a default the board does not offer is left as it is, so the block shows as invalid
            if (property.DefaultValue is not null || !property.Required)
                continue;

            target.Parameters[property.Name] = offered[0]!;
            setup.Add($"{property.Name}={offered[0]}");
        }

        var inputs = block.InputPorts.ToList();
        var triggerPort = inputs.FirstOrDefault(p => p.ValueKind == FlowValueKind.Trigger);
        if (triggerPort is not null && block.Type != BuiltInBlockCatalog.ManualTrigger)
        {
            document.Nodes.Add(new FlowNode { Id = "go", Type = BuiltInBlockCatalog.ManualTrigger });
            document.Connections.Add(new FlowConnection { Id = "c-trigger", FromNodeId = "go", FromPort = "trigger", ToNodeId = "block", ToPort = triggerPort.Name });
            setup.Add("manual trigger");
        }

        foreach (var port in inputs.Where(p => p.Required && p.ValueKind != FlowValueKind.Trigger))
        {
            var boolean = port.ValueKind == FlowValueKind.Boolean;
            var constant = new FlowNode { Id = "in-" + port.Name, Type = boolean ? BuiltInBlockCatalog.ConstantBoolean : BuiltInBlockCatalog.ConstantNumber };
            document.Nodes.Add(constant);
            document.Connections.Add(new FlowConnection { Id = "c-" + port.Name, FromNodeId = constant.Id, FromPort = "value", ToNodeId = "block", ToPort = port.Name });
            setup.Add((boolean ? "boolean" : "number") + " constant into " + port.Name);
        }

        return new Cell { Board = board, Block = block, Flow = document, Setup = string.Join(", ", setup) };
    }

    public static bool SameValue(object? a, object? b) =>
        string.Equals(Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture), Convert.ToString(b, System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

    public static void Validate(Cell cell)
    {
        var result = new FlowValidator().Validate(cell.Flow, CatalogOf(cell.Board));
        cell.Errors = result.Errors.Select(e => e.Message).ToList();
        cell.Warnings = result.Issues.Where(i => i.Severity == FlowValidationSeverity.Warning).Select(i => i.Message).ToList();
    }

    public static SimulatedTransport NewTransport(BoardProfile board)
    {
        var transport = new SimulatedTransport(new SimulatedBoardOptions
        {
            AnalogMax = board.AnalogMaxValue,
            MaxPin = board.MaxPin,
            ChipName = board.Family == BoardFamily.ESP32 ? (board.FlashChip ?? "esp32").ToUpperInvariant() + "-SIM" : "ATmega-SIM"
        });

        // With no virtual chip the virtual bus answers every I2C address (a demo mode: the sensors give plausible readings). With one chip on it the bus behaves like a
        // real one and nobody answers at an empty address. This chip sits at 0x7F, which no block uses: the board is then a real one with nothing wired to it.
        transport.AddI2cDevice(0x7F, new EmptyChip());
        return transport;
    }

    private sealed class EmptyChip : ISimulatedI2cDevice
    {
        public void Write(byte[] data)
        {
        }

        public byte[] Read(int length) => new byte[length];
    }

    /// <summary>The same board class the editor and FlowHost use: ESP32 chips and AVR Arduinos speak the same protocol; only the class and the pin range differ.</summary>
    public static async Task<IBoard> ConnectAsync(BoardProfile board, SimulatedTransport transport)
    {
        IBoard connected = board.Family == BoardFamily.ESP32
            ? new ESP32Board(transport, board.MaxPin)
            : new CodeBridgeProtocolBoard(transport, board.DisplayName + " (simulator)", board.Family, board.MaxPin);

        switch (connected)
        {
            case ESP32Board esp:
                await esp.ConnectAsync();
                break;
            case CodeBridgeProtocolBoard protocol:
                await protocol.ConnectAsync();
                break;
        }

        return connected;
    }

    public static async Task RunAsync(Cell cell, TimeSpan limit)
    {
        var transport = NewTransport(cell.Board);
        var board = await ConnectAsync(cell.Board, transport);
        try
        {
            var before = transport.CommandLog.Count;
            await ExecuteAsync(cell, board, () => transport.CommandLog.Skip(before).ToList(), limit);
        }
        finally
        {
            if (board is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
        }
    }

    /// <summary>Runs the flow of the cell on a board that is already connected (the virtual one, or a real one) and records how it ended.</summary>
    public static async Task ExecuteAsync(Cell cell, IBoard board, Func<IReadOnlyList<string>> commandsSent, TimeSpan limit)
    {
        using var cancellation = new CancellationTokenSource(limit);
        try
        {
            await new FlowRuntime(CatalogOf(cell.Board)).ExecuteAsync(cell.Flow, new FlowExecutionContext { Board = board }, cancellation.Token);
            cell.Run = new RunResult(RunKind.Completed, "", commandsSent());
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // a block that waits (a timer, a pulse) was still running when the limit came: it started and did not crash
            cell.Run = new RunResult(RunKind.Completed, "still running after " + limit.TotalSeconds.ToString("0.#") + " s", commandsSent());
        }
        catch (Exception ex)
        {
            var kind = IsNothingAttached(ex) ? RunKind.NothingAttached : RunKind.Failed;
            cell.Run = new RunResult(kind, ex.GetType().Name + ": " + First(ex.Message), commandsSent());
        }
    }

    private static string First(string text) => text.Split('\n')[0].Trim();

    /// <summary>The answers a board with nothing attached gives: no chip at the address, no echo, no sensor, a checksum that does not match.</summary>
    private static bool IsNothingAttached(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (e is I2cChipException)
                return true;

            var text = e.Message;
            if (text.Contains("Check the wiring", StringComparison.OrdinalIgnoreCase) || text.Contains("No echo", StringComparison.OrdinalIgnoreCase) || text.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("checksum", StringComparison.OrdinalIgnoreCase) || text.Contains("I2C write failed", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("I2C register write failed", StringComparison.OrdinalIgnoreCase) || text.Contains("did not answer", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("NACK", StringComparison.OrdinalIgnoreCase) || text.Contains("no device", StringComparison.OrdinalIgnoreCase))
                return true;

            if (e.InnerException is null)
                break;
        }

        return false;
    }

    public static void Export(Cell cell)
    {
        try
        {
            cell.Exported = FlowCSharpGenerator.Generate(cell.Flow, new FlowCSharpOptions { ClassName = ClassName(cell) }).Code;
        }
        catch (Exception ex)
        {
            cell.ExportProblem = ex.GetType().Name + ": " + First(ex.Message);
        }
    }

    public static string ClassName(Cell cell) => "Cell_" + new string((cell.Board.Id + "_" + cell.Block.Type).Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    /// <summary>Compiles the exported code of many cells together (one tree each) and attaches what the compiler said to the cell it belongs to.</summary>
    public static void Compile(IReadOnlyList<Cell> cells)
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).ToList();
        foreach (var assembly in new[] { typeof(IBoard).Assembly, typeof(ESP32Board).Assembly, typeof(CodeBridge.Transport.BoardDiscovery).Assembly, typeof(FlowDocument).Assembly, typeof(IotBindingBlocks).Assembly })
            paths.Add(assembly.Location);

        var trees = new Dictionary<SyntaxTree, Cell>();
        foreach (var cell in cells.Where(c => c.Exported is not null))
            trees[CSharpSyntaxTree.ParseText(cell.Exported!, new CSharpParseOptions(LanguageVersion.Latest), path: cell + ".cs")] = cell;

        if (trees.Count == 0)
            return;

        var compilation = CSharpCompilation.Create(
            "Matrix_" + Guid.NewGuid().ToString("N"),
            trees.Keys,
            paths.Distinct().Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToList(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        // errors only: the matrix flows do not use the outputs of the block, so "assigned but never used" warnings are expected
        foreach (var diagnostic in compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            if (diagnostic.Location.SourceTree is { } tree && trees.TryGetValue(tree, out var cell))
                cell.ExportProblem = (cell.ExportProblem is null ? "" : cell.ExportProblem + " | ") + diagnostic.Id + ": " + diagnostic.GetMessage();
        }
    }
}
