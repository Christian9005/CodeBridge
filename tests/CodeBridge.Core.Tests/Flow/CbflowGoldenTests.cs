using System.Globalization;
using System.Reflection;
using System.Text.Json;
using CodeBridge.ESP32;
using CodeBridge.Flow;
using CodeBridge.Flow.CodeGeneration;
using CodeBridge.Flow.Execution;
using CodeBridge.Flow.Serialization;
using CodeBridge.Flow.Validation;
using CodeBridge.TestSupport;
using CodeBridge.Transport.Simulation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeBridge.Core.Tests.Flow;

/// <summary>
/// Files that real versions of CodeBridge and its editors wrote, and files people write by hand (tests/golden/cbflow). This version must read every one of them,
/// validate it, run it and export it to C#: people have files like these on their disks, and an upgrade must not strand them.
/// </summary>
[Collection(HeavyFlowTests.Name)]
public class CbflowGoldenTests
{
    public static IEnumerable<object[]> AllFlows() => GoldenFlows.All().Select(f => new object[] { f.File });

    public static IEnumerable<object[]> RunnableFlows() => GoldenFlows.All().Where(f => f.Runs).Select(f => new object[] { f.File });

    private static GoldenFlow Of(string file) => GoldenFlows.All().Single(f => f.File == file);

    private static FlowDocument Load(GoldenFlow flow) => FlowDocumentJson.Deserialize(GoldenFlows.Read(flow).TrimStart('﻿'));

    private static FlowBlockCatalog CatalogOf(FlowDocument document) =>
        BuiltInBlockCatalog.Create(BuiltInBoardProfiles.FindById(document.BoardId) ?? BuiltInBoardProfiles.Default);

    private static async Task<(ESP32Board Board, SimulatedTransport Transport)> BoardAsync(FlowDocument document)
    {
        var profile = BuiltInBoardProfiles.FindById(document.BoardId) ?? BuiltInBoardProfiles.Default;
        var transport = new SimulatedTransport(new SimulatedBoardOptions { AnalogMax = profile.AnalogMaxValue, MaxPin = profile.MaxPin });
        foreach (var pin in new[] { 32, 33, 34, 35, 36, 39 })
            transport.SetAnalogInput(pin, 1500);   // the simulator's analog pins follow a wave over time: pin them so two runs can be compared
        var board = new ESP32Board(transport, profile.MaxPin);
        await board.ConnectAsync();
        return (board, transport);
    }

    /// <summary>The commands must all be there, in this order (others may come in between). A command ending in a colon matches any value.</summary>
    private static void AssertCommandsInOrder(IReadOnlyList<string> expected, IReadOnlyList<string> actual, string what)
    {
        var at = 0;
        foreach (var command in actual)
        {
            if (at < expected.Count && command.StartsWith(expected[at], StringComparison.Ordinal))
                at++;
        }

        Assert.True(at == expected.Count, $"{what}: expected {string.Join(" ", expected)} in this order, the board got {string.Join(" ", actual)}");
    }

    // ---------------------------------------------------------------- the collection itself

    [Fact]
    public void The_manifest_and_the_files_on_disk_say_the_same()
    {
        var listed = GoldenFlows.All().Select(f => f.File).OrderBy(f => f, StringComparer.Ordinal).ToList();

        Assert.Equal(GoldenFlows.FilesOnDisk(), listed);
        Assert.Equal(listed.Count, listed.Distinct().Count());
    }

    [Fact]
    public void Every_released_example_is_still_in_the_collection()
    {
        var names = GoldenFlows.All().Select(f => Path.GetFileName(f.File)).ToList();

        foreach (var example in new[] { "Example1_Blink", "Example2_BlinkPattern", "Example3_NightLight", "Example4_ServoSweep", "Example5_ButtonLed", "BlinkFlow" })
            Assert.Contains(names, n => n.Contains(example, StringComparison.Ordinal));
    }

    [Fact]
    public void The_shipped_item_templates_are_golden_files_too()
    {
        // the files the extension installs on a user's disk are the released ones: if a template changes, a new golden file has to be added for it
        var templates = Directory.GetFiles(Path.Combine(GoldenFlows.Folder, "..", "..", "..", "src", "CodeBridge.VisualStudio", "ItemTemplates"), "*.cbflow", SearchOption.AllDirectories);
        var golden = Directory.GetFiles(Path.Combine(GoldenFlows.Folder, "released"), "*.cbflow").ToDictionary(f => Path.GetFileName(f).Split("--")[1]);

        foreach (var template in templates)
        {
            Assert.True(golden.TryGetValue(Path.GetFileName(template), out var copy), $"{Path.GetFileName(template)} has no golden copy in released/.");
            Assert.Equal(File.ReadAllText(copy!).Replace("\r\n", "\n"), File.ReadAllText(template).Replace("\r\n", "\n"));
        }
    }

    // ---------------------------------------------------------------- reading

    [Theory]
    [MemberData(nameof(AllFlows))]
    public void The_file_opens_with_the_name_the_board_and_the_blocks_it_has_always_had(string file)
    {
        var flow = Of(file);
        var document = Load(flow);

        Assert.Equal(flow.Name, document.Name);
        Assert.Equal(flow.Board, document.BoardId);
        Assert.Equal(flow.Nodes, document.Nodes.Count);
        Assert.Equal(flow.Connections, document.Connections.Count);
    }

    [Theory]
    [MemberData(nameof(AllFlows))]
    public void The_hosts_and_the_Visual_Studio_editor_read_the_file_the_same_way(string file)
    {
        var flow = Of(file);
        var json = GoldenFlows.Read(flow);

        Assert.Equal(GoldenFlows.Canonical(FlowDocumentJson.Deserialize(json.TrimStart('﻿'))), GoldenFlows.Canonical(GoldenFlows.LoadLikeVisualStudio(json)));
    }

    [Theory]
    [MemberData(nameof(AllFlows))]
    public void The_validator_says_what_it_always_said(string file)
    {
        var flow = Of(file);
        var document = Load(flow);

        var validation = new FlowValidator().Validate(document, CatalogOf(document));

        Assert.True(validation.IsValid == flow.Valid, string.Join("; ", validation.Issues.Select(i => i.Severity + ": " + i.Message)));
        foreach (var expected in flow.ValidationContains)
            Assert.Contains(validation.Issues, issue => issue.Message.Contains(expected, StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- running and exporting

    [Theory]
    [MemberData(nameof(RunnableFlows))]
    public async Task The_flow_runs_on_the_simulator_and_the_board_gets_what_it_always_got(string file)
    {
        var flow = Of(file);
        var document = Load(flow);
        var (board, transport) = await BoardAsync(document);
        var before = transport.CommandLog.Count;

        await new FlowRuntime(CatalogOf(document)).ExecuteAsync(document, board);

        AssertCommandsInOrder(flow.Commands, transport.CommandLog.Skip(before).ToList(), flow.File);
    }

    [Theory]
    [MemberData(nameof(RunnableFlows))]
    public async Task The_exported_C_sharp_compiles_and_talks_to_the_board_like_the_flow(string file)
    {
        var flow = Of(file);
        var document = Load(flow);
        var generated = FlowCSharpGenerator.Generate(document);
        Assert.Empty(generated.Warnings);
        var assembly = Compile(generated.Code);

        var (flowBoard, flowTransport) = await BoardAsync(document);
        var flowBefore = flowTransport.CommandLog.Count;
        await new FlowRuntime(CatalogOf(document)).ExecuteAsync(document, flowBoard);
        var fromFlow = flowTransport.CommandLog.Skip(flowBefore).ToList();

        var (board, transport) = await BoardAsync(document);
        var before = transport.CommandLog.Count;
        var run = assembly.GetTypes().Single(t => t.Name == generated.ClassName).GetMethod("RunAsync")!;
        await (Task)run.Invoke(null, new object[] { board, CancellationToken.None })!;

        Assert.Equal(fromFlow, transport.CommandLog.Skip(before).ToList());
    }

    [Fact]
    public async Task A_machine_that_writes_decimals_with_a_comma_reads_and_runs_the_files_the_same_way()
    {
        // an Ecuadorian, Spanish or German Windows: "2000.0" must still be two thousand, not twenty thousand
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (var name in new[] { "es-EC", "de-DE", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(name);
                foreach (var flow in GoldenFlows.All().Where(f => f.Runs && f.File.Contains("Night", StringComparison.Ordinal) || f.File.Contains("numbers-as-text", StringComparison.Ordinal)))
                {
                    var document = Load(flow);
                    var (board, transport) = await BoardAsync(document);
                    var before = transport.CommandLog.Count;

                    await new FlowRuntime(CatalogOf(document)).ExecuteAsync(document, board);

                    AssertCommandsInOrder(flow.Commands, transport.CommandLog.Skip(before).ToList(), $"{flow.File} in {name}");
                    Assert.True(new FlowValidator().Validate(document, CatalogOf(document)).IsValid, $"{flow.File} in {name}");
                    Assert.Contains("2000", FlowCSharpGenerator.Generate(document).Code);
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = original;
        }
    }

    // ---------------------------------------------------------------- saving

    [Theory]
    [MemberData(nameof(AllFlows))]
    public void Saving_and_opening_again_changes_nothing_that_matters(string file)
    {
        var flow = Of(file);
        var original = Load(flow);

        var byTheSdk = FlowDocumentJson.Deserialize(FlowDocumentJson.Serialize(original));
        var byVisualStudio = FlowDocumentJson.Deserialize(GoldenFlows.SaveLikeVisualStudio(GoldenFlows.LoadLikeVisualStudio(GoldenFlows.Read(flow))));

        Assert.Equal(GoldenFlows.Canonical(original), GoldenFlows.Canonical(byTheSdk));
        Assert.Equal(GoldenFlows.Canonical(original), GoldenFlows.Canonical(byVisualStudio));
    }

    [Fact]
    public void A_block_from_a_newer_version_is_kept_when_the_file_is_saved_again()
    {
        var flow = GoldenFlows.All().Single(f => f.File.Contains("future", StringComparison.Ordinal));

        var saved = FlowDocumentJson.Deserialize(FlowDocumentJson.Serialize(Load(flow)));

        var unknown = saved.Nodes.Single(n => n.Type == "future.quantum-gate");
        Assert.Equal(3, Assert.IsType<JsonElement>(unknown.Parameters["qubits"]).GetInt32());
        Assert.Equal(flow.Nodes, saved.Nodes.Count);
    }

    // ---------------------------------------------------------------- the blocks of older releases

    private sealed record Port(string Name, string Direction, string Kind, bool Required);

    private sealed record Property(string Name, string Kind, bool Required, string Default, IReadOnlyList<string> Choices);

    private static string Norm(object? value)
    {
        switch (value)
        {
            case null: return "null";
            case JsonElement element:
                return element.ValueKind switch
                {
                    JsonValueKind.Null or JsonValueKind.Undefined => "null",
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Number => element.GetDouble().ToString("R", CultureInfo.InvariantCulture),
                    JsonValueKind.String => element.GetString() ?? "",
                    _ => element.GetRawText()
                };
            case bool flag: return flag ? "true" : "false";
            case string text: return text;
            case IConvertible number when value is not char: return Convert.ToDouble(number, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
            default: return value.ToString() ?? "";
        }
    }

    public static IEnumerable<object[]> ReleasedBoards()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(GoldenFlows.Folder, "catalogs.json")));
        foreach (var release in document.RootElement.GetProperty("releases").EnumerateArray())
        {
            foreach (var board in release.GetProperty("boards").EnumerateObject())
                yield return new object[] { release.GetProperty("version").GetString()!, board.Name };
        }
    }

    [Theory]
    [MemberData(nameof(ReleasedBoards))]
    public void Nothing_a_flow_of_that_release_relies_on_was_removed_or_changed(string version, string boardId)
    {
        using var snapshot = JsonDocument.Parse(File.ReadAllText(Path.Combine(GoldenFlows.Folder, "catalogs.json")));
        var release = snapshot.RootElement.GetProperty("releases").EnumerateArray().Single(r => r.GetProperty("version").GetString() == version);
        var oldBlocks = release.GetProperty("boards").GetProperty(boardId);
        var profile = BuiltInBoardProfiles.FindById(boardId);
        Assert.True(profile is not null, $"The board '{boardId}' of {version} no longer exists.");
        var catalog = BuiltInBlockCatalog.Create(profile);

        var problems = new List<string>();
        foreach (var oldBlock in oldBlocks.EnumerateObject())
        {
            if (!catalog.TryGet(oldBlock.Name, out var block))
            {
                problems.Add($"block {oldBlock.Name} no longer exists");
                continue;
            }

            foreach (var port in oldBlock.Value.GetProperty("ports").EnumerateArray())
            {
                var old = new Port(port.GetProperty("name").GetString()!, port.GetProperty("direction").GetString()!, port.GetProperty("kind").GetString()!, port.GetProperty("required").GetBoolean());
                var now = block.Ports.FirstOrDefault(p => p.Name == old.Name && p.Direction.ToString().Equals(old.Direction, StringComparison.OrdinalIgnoreCase));
                if (now is null)
                    problems.Add($"{oldBlock.Name}: port {old.Name} ({old.Direction}) was removed");
                else if (!now.ValueKind.ToString().Equals(old.Kind, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{oldBlock.Name}: port {old.Name} was {old.Kind}, now {now.ValueKind}");
                else if (now.Required && !old.Required)
                    problems.Add($"{oldBlock.Name}: port {old.Name} was optional and is now required");
            }

            var oldPorts = oldBlock.Value.GetProperty("ports").EnumerateArray().Select(p => (p.GetProperty("name").GetString(), p.GetProperty("direction").GetString()!)).ToList();
            foreach (var added in block.Ports.Where(p => p.Required && p.Direction == FlowPortDirection.Input && !oldPorts.Any(o => o.Item1 == p.Name && o.Item2.Equals(p.Direction.ToString(), StringComparison.OrdinalIgnoreCase))))
                problems.Add($"{oldBlock.Name}: new required input {added.Name}: files from {version} do not wire it");

            foreach (var property in oldBlock.Value.GetProperty("properties").EnumerateArray())
            {
                var old = new Property(
                    property.GetProperty("name").GetString()!,
                    property.GetProperty("kind").GetString()!,
                    property.GetProperty("required").GetBoolean(),
                    Norm(property.GetProperty("default")),
                    property.GetProperty("choices").EnumerateArray().Select(c => Norm(c)).ToList());
                var now = block.FindProperty(old.Name);
                if (now is null)
                {
                    problems.Add($"{oldBlock.Name}: property {old.Name} was removed");
                    continue;
                }

                if (!now.ValueKind.ToString().Equals(old.Kind, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{oldBlock.Name}: property {old.Name} was {old.Kind}, now {now.ValueKind}");
                if (Norm(now.DefaultValue) != old.Default)
                    problems.Add($"{oldBlock.Name}: property {old.Name} default was {old.Default}, now {Norm(now.DefaultValue)}: a file that does not set it changes meaning");

                var nowChoices = (now.Options ?? []).Select(o => Norm(o.Value)).ToHashSet();
                foreach (var lost in old.Choices.Where(c => !nowChoices.Contains(c)))
                    problems.Add($"{oldBlock.Name}: property {old.Name} no longer offers {lost}");
            }

            var oldProperties = oldBlock.Value.GetProperty("properties").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToHashSet();
            foreach (var added in block.Properties.Where(p => !oldProperties.Contains(p.Name) && p.Required && p.DefaultValue is null))
                problems.Add($"{oldBlock.Name}: new required property {added.Name} has no default: files from {version} do not set it");
        }

        Assert.True(problems.Count == 0, $"A flow saved with {version} on {boardId} would break:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    // ---------------------------------------------------------------- helpers

    internal static Assembly Compile(string code, bool executable = false)
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).ToList();
        foreach (var assembly in new[] { typeof(CodeBridge.Core.Abstractions.IBoard).Assembly, typeof(ESP32Board).Assembly, typeof(CodeBridge.Transport.BoardDiscovery).Assembly, typeof(FlowDocument).Assembly })
            paths.Add(assembly.Location);

        var compilation = CSharpCompilation.Create(
            "Exported_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.Latest)) },
            paths.Distinct().Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToList(),
            new CSharpCompilationOptions(executable ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        var problems = result.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Select(d => d.ToString()).ToList();
        Assert.True(result.Success && problems.Count == 0, "Generated code does not compile cleanly:" + Environment.NewLine + string.Join(Environment.NewLine, problems) + Environment.NewLine + code);
        return Assembly.Load(stream.ToArray());
    }
}
