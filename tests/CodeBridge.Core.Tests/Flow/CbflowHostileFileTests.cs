using System.Globalization;
using System.Text;
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
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeBridge.Core.Tests.Flow;

/// <summary>
/// A .cbflow is a file anyone can edit, truncate, corrupt or craft: it arrives by e-mail, from a repository, from a web editor, or from a half-finished save.
/// Whatever the bytes are, the SDK must answer in one of three ways: a document, a clear refusal (a JSON or invalid-operation error), or a list of validation
/// errors. It must never crash with an internal error, hang, or turn text of the file into code that runs when the exported project is built.
/// </summary>
public class CbflowHostileFileTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);
    private static int _validSeen;

    public static IEnumerable<object[]> GoldenFiles() => GoldenFlows.All().Select(f => new object[] { f.File });

    private static FlowBlockCatalog CatalogOf(FlowDocument document) =>
        BuiltInBlockCatalog.Create((document.BoardId is null ? null : BuiltInBoardProfiles.FindById(document.BoardId)) ?? BuiltInBoardProfiles.Default);

    /// <summary>The refusals a caller is told to expect from loading. Anything else escaping is a bug in the SDK, not in the file.</summary>
    private static FlowDocument? TryLoad(string json)
    {
        try
        {
            return FlowDocumentJson.Deserialize(json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Everything a caller does with a file, in the order it does it. Returns what became of it, and throws only when the SDK broke its promise
    /// (an unexpected exception, a hang, a refusal that is not the kind it promises, or exported code that does not compile).
    /// </summary>
    private static async Task<string> ExerciseAsync(string json)
    {
        var document = TryLoad(json);
        if (document is null)
            return "refused";

        var catalog = CatalogOf(document);
        var validation = new FlowValidator().Validate(document, catalog);

        if (!validation.IsValid)
        {
            // an invalid flow is refused by the two things that would act on it, with the promised kind of error
            await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(document, catalog));
            Assert.Throws<InvalidOperationException>(() => FlowCSharpGenerator.Generate(document));
            return "invalid";
        }

        await RunAsync(document, catalog);

        // Generating is cheap and must always work for a valid flow; compiling with Roslyn is not, so one valid file in twelve is compiled (both modes).
        foreach (var mode in new[] { FlowCSharpMode.Class, FlowCSharpMode.ConsoleApp })
        {
            var generated = FlowCSharpGenerator.Generate(document, new FlowCSharpOptions { Mode = mode });
            if (Interlocked.Increment(ref _validSeen) % 12 == 0)
                CbflowGoldenTests.Compile(generated.Code, mode == FlowCSharpMode.ConsoleApp);
        }

        return "valid";
    }

    private static async Task RunAsync(FlowDocument document, FlowBlockCatalog catalog)
    {
        var profile = (document.BoardId is null ? null : BuiltInBoardProfiles.FindById(document.BoardId)) ?? BuiltInBoardProfiles.Default;
        var transport = new SimulatedTransport(new SimulatedBoardOptions { AnalogMax = profile.AnalogMaxValue, MaxPin = profile.MaxPin });
        var board = new ESP32Board(transport, profile.MaxPin);
        await board.ConnectAsync();

        // A damaged file may ask for a delay of an hour. Running it for a moment and then cancelling is the right outcome; what must not happen is a crash,
        // or a run that does not stop when asked (the outer time limit catches that).
        using var limit = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            await new FlowRuntime(catalog).ExecuteAsync(document, board, limit.Token);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
        }
    }

    /// <summary>Runs every input and reports all the broken promises at once, so one bad case does not hide the next.</summary>
    private static async Task<Dictionary<string, int>> ExerciseAllAsync(IEnumerable<(string Label, string Json)> inputs)
    {
        var outcomes = new Dictionary<string, int>();
        var broken = new List<string>();

        foreach (var (label, json) in inputs)
        {
            try
            {
                var outcome = await Task.Run(() => ExerciseAsync(json)).WaitAsync(Patience);
                outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
            }
            catch (TimeoutException)
            {
                broken.Add($"{label}: did not finish in {Patience.TotalSeconds:0} s");
            }
            catch (Exception ex)
            {
                broken.Add($"{label}: {ex.GetType().Name}: {First(ex.Message)}{Where(ex)}");
            }
        }

        Assert.True(broken.Count == 0, $"{broken.Count} input(s) broke a promise:{Environment.NewLine}{string.Join(Environment.NewLine, broken.Take(15))}");
        return outcomes;
    }

    private static string First(string text) => string.Join(" | ", text.Split((char)10).Select(l => l.Trim()).Where(l => l.Length > 0).Take(3).Select(l => l.Length > 300 ? l[..300] : l));

    /// <summary>The first line of the SDK's own code in the stack, so a failure says where, not only what.</summary>
    private static string Where(Exception ex)
    {
        var frame = (ex.StackTrace ?? string.Empty).Split((char)10).Select(l => l.Trim()).FirstOrDefault(l => l.Contains("CodeBridge.Flow.", StringComparison.Ordinal) && !l.Contains("Tests", StringComparison.Ordinal));
        return frame is null ? string.Empty : "  <- " + frame.Replace("at CodeBridge.Flow.", string.Empty).Split(" in ")[0];
    }

    private static string Short(string text) => text.Length <= 40 ? text : text[..40] + "...";

    // ---------------------------------------------------------------- not a flow at all

    [Fact]
    public async Task Text_that_is_not_a_flow_is_refused_without_an_internal_error()
    {
        var inputs = new[]
        {
            "", " ", "\r\n", "\0", "null", "true", "42", "\"text\"", "[]", "[1,2,3]", "{}", "{", "}", "{{", "[[[[", "{\"Nodes\":", "{\"Nodes\":[",
            "{\"Nodes\":null}", "{\"Nodes\":{}}", "{\"Nodes\":\"x\"}", "{\"Nodes\":[null]}", "{\"Nodes\":[1]}", "{\"Nodes\":[[]]}", "{\"Nodes\":[{}]}",
            "{\"Connections\":null}", "{\"Connections\":[null]}", "{\"Connections\":[{}]}", "{\"Connections\":\"x\"}",
            "<html><body>Not found</body></html>", "<?xml version=\"1.0\"?><flow/>", "MZ\u0090\u0000\u0003\u0000\u0000\u0000", "﻿", "﻿{}",
            "{\"Name\":null}", "{\"Name\":1}", "{\"Name\":[]}", "{\"BoardId\":null}", "{\"BoardId\":1}", "{\"BoardId\":\"\"}", "{\"BoardId\":\"no-such-board\"}",
            "{\"Version\":\"x\"}", "{\"Version\":1.5}", "{\"Version\":99999999999999999999}", "{\"Version\":-1}", "{\"Id\":null}", "{\"Id\":{}}",
            "// a comment\n{}", "{\"Name\":\"a\",}", "{'Name':'a'}", "{Name:'a'}", "{\"Name\":\"a\"}{\"Name\":\"b\"}",
        };

        var outcomes = await ExerciseAllAsync(inputs.Select(i => (Short(i), i)));

        Assert.True(outcomes.GetValueOrDefault("refused") > 20);
    }

    [Fact]
    public async Task Nodes_and_wires_with_missing_or_wrong_pieces_are_reported_not_crashed_on()
    {
        string Doc(string nodes, string wires = "[]", string rest = "") =>
            "{ \"Name\": \"x\", \"BoardId\": \"esp32-devkit\", \"Nodes\": " + nodes + ", \"Connections\": " + wires + rest + " }";

        const string Led = "{ \"Id\": \"led\", \"Type\": \"gpio.digital-write\", \"Parameters\": { \"pin\": 2, \"value\": true } }";
        const string Go = "{ \"Id\": \"go\", \"Type\": \"flow.manual-trigger\" }";

        var inputs = new List<string>
        {
            Doc("[{ \"Id\": null, \"Type\": \"flow.manual-trigger\" }]"),
            Doc("[{ \"Id\": \"\", \"Type\": \"flow.manual-trigger\" }]"),
            Doc("[{ \"Id\": \"   \", \"Type\": \"flow.manual-trigger\" }]"),
            Doc("[{ \"Id\": 7, \"Type\": \"flow.manual-trigger\" }]"),
            Doc("[{ \"Id\": \"a\", \"Type\": null }]"),
            Doc("[{ \"Id\": \"a\", \"Type\": \"\" }]"),
            Doc("[{ \"Id\": \"a\", \"Type\": 5 }]"),
            Doc("[{ \"Id\": \"a\" }]"),
            Doc("[{ \"Id\": \"a\", \"Type\": \"flow.manual-trigger\", \"Parameters\": null }]"),
            Doc("[{ \"Id\": \"a\", \"Type\": \"flow.manual-trigger\", \"Parameters\": [] }]"),
            Doc("[{ \"Id\": \"a\", \"Type\": \"flow.manual-trigger\", \"Parameters\": \"x\" }]"),
            Doc("[{ \"Id\": \"a\", \"Type\": \"flow.manual-trigger\", \"Position\": null }]"),
            Doc("[{ \"Id\": \"a\", \"Type\": \"flow.manual-trigger\", \"Position\": \"1,2\" }]"),
            Doc("[{ \"Id\": \"a\", \"Type\": \"flow.manual-trigger\", \"Position\": { \"X\": \"far\", \"Y\": 1 } }]"),
            Doc("[{ \"Id\": \"a\", \"Type\": \"flow.manual-trigger\", \"Position\": { \"X\": 1e999, \"Y\": 1 } }]"),
            Doc("[{ \"Id\": \"a\", \"Type\": \"flow.manual-trigger\", \"Position\": { \"X\": -1e308, \"Y\": 1e308 } }]"),
            Doc($"[{Go}, {Led}]", "[{ }]"),
            Doc($"[{Go}, {Led}]", "[{ \"FromNodeId\": null, \"FromPort\": null, \"ToNodeId\": null, \"ToPort\": null }]"),
            Doc($"[{Go}, {Led}]", "[{ \"Id\": \"c\", \"FromNodeId\": \"go\", \"FromPort\": \"trigger\", \"ToNodeId\": \"nowhere\", \"ToPort\": \"trigger\" }]"),
            Doc($"[{Go}, {Led}]", "[{ \"Id\": \"c\", \"FromNodeId\": \"nowhere\", \"FromPort\": \"trigger\", \"ToNodeId\": \"led\", \"ToPort\": \"trigger\" }]"),
            Doc($"[{Go}, {Led}]", "[{ \"Id\": \"c\", \"FromNodeId\": \"go\", \"FromPort\": \"nope\", \"ToNodeId\": \"led\", \"ToPort\": \"nope\" }]"),
            Doc($"[{Go}, {Led}]", "[{ \"Id\": \"c\", \"FromNodeId\": \"led\", \"FromPort\": \"done\", \"ToNodeId\": \"go\", \"ToPort\": \"trigger\" }]"),
            Doc($"[{Go}, {Led}]", "[{ \"Id\": \"c\", \"FromNodeId\": \"led\", \"FromPort\": \"done\", \"ToNodeId\": \"led\", \"ToPort\": \"trigger\" }]"),
            Doc($"[{Go}, {Led}]", "[{ \"Id\": \"c\", \"FromNodeId\": \"go\", \"FromPort\": \"trigger\", \"ToNodeId\": \"led\", \"ToPort\": \"trigger\" }, { \"Id\": \"c\", \"FromNodeId\": \"go\", \"FromPort\": \"trigger\", \"ToNodeId\": \"led\", \"ToPort\": \"trigger\" }]"),
            Doc($"[{Go}, {Led}, {Led}]", "[{ \"Id\": \"c\", \"FromNodeId\": \"go\", \"FromPort\": \"trigger\", \"ToNodeId\": \"led\", \"ToPort\": \"trigger\" }]"),
            Doc($"[{Go}, {Go}]"),
            Doc($"[{Go}, {Led.Replace("\"led\"", "\"LED\"")}, {Led}]"),
            Doc(Led.Replace("\"pin\": 2", "\"pin\": null").Insert(0, "[").Insert(Led.Length + 1, "]")),
            Doc("[" + Led.Replace("\"pin\": 2", "\"pin\": [1,2]") + "]"),
            Doc("[" + Led.Replace("\"pin\": 2", "\"pin\": { \"a\": { \"b\": 1 } }") + "]"),
            Doc("[" + Led.Replace("\"pin\": 2", "\"pin\": -1") + "]"),
            Doc("[" + Led.Replace("\"pin\": 2", "\"pin\": 99999999999") + "]"),
            Doc("[" + Led.Replace("\"pin\": 2", "\"pin\": 2147483648") + "]"),
            Doc("[" + Led.Replace("\"pin\": 2", "\"pin\": -2147483649") + "]"),
            Doc("[" + Led.Replace("\"pin\": 2", "\"pin\": 1e400") + "]"),
            Doc("[" + Led.Replace("\"pin\": 2", "\"pin\": \"\"") + "]"),
            Doc("[" + Led.Replace("\"pin\": 2", "\"pin\": \"0x10\"") + "]"),
            Doc("[" + Led.Replace("\"pin\": 2", "\"pin\": \"NaN\"") + "]"),
            Doc("[" + Led.Replace("\"pin\": 2", "\"pin\": \"Infinity\"") + "]"),
            Doc("[" + Led.Replace("\"value\": true", "\"value\": \"\"") + "]"),
            Doc("[" + Led.Replace("\"value\": true", "\"value\": {}") + "]"),
            Doc("[" + Led.Replace("\"value\": true", "\"value\": 1e999") + "]"),
        };

        var outcomes = await ExerciseAllAsync(inputs.Select((json, i) => ($"case {i + 1}", json)));

        Assert.True(outcomes.GetValueOrDefault("invalid") + outcomes.GetValueOrDefault("refused") > 20);
    }

    // ---------------------------------------------------------------- damaged real files

    [Theory]
    [MemberData(nameof(GoldenFiles))]
    public async Task A_file_cut_short_wherever_the_save_was_interrupted_never_crashes_the_loader(string file)
    {
        var text = GoldenFlows.Read(GoldenFlows.All().Single(f => f.File == file)).TrimStart('﻿');
        var step = Math.Max(1, text.Length / 120);

        var cuts = Enumerable.Range(0, (text.Length / step) + 1).Select(i => Math.Min(text.Length, i * step)).Distinct()
            .Select(length => ($"first {length} of {text.Length} characters", text[..length]));

        await ExerciseAllAsync(cuts);
    }

    [Theory]
    [MemberData(nameof(GoldenFiles))]
    public async Task A_file_with_bytes_changed_deleted_or_repeated_never_crashes_anything(string file)
    {
        var text = GoldenFlows.Read(GoldenFlows.All().Single(f => f.File == file)).TrimStart('﻿');
        var random = new Random(20261109);   // always the same damage, so a failure can be reproduced
        var replacements = new[] { "{", "}", "[", "]", ",", ":", "\"", "\\", "null", "-1", "1e999", "0", "\"\"", "true", "\u0000", "\u2028", "\ud800", " " };
        var mutations = new List<(string, string)>();

        for (var i = 0; i < 150; i++)
        {
            var at = random.Next(text.Length);
            var length = random.Next(1, Math.Min(12, text.Length - at) + 1);
            string changed;
            string what;
            switch (random.Next(4))
            {
                case 0:
                    var replacement = replacements[random.Next(replacements.Length)];
                    changed = text[..at] + replacement + text[(at + length)..];
                    what = $"replace {length} at {at} with {Short(replacement)}";
                    break;
                case 1:
                    changed = text[..at] + text[(at + length)..];
                    what = $"delete {length} at {at}";
                    break;
                case 2:
                    changed = text[..(at + length)] + text.Substring(at, length) + text[(at + length)..];
                    what = $"repeat {length} at {at}";
                    break;
                default:
                    changed = text[..at] + (char)random.Next(0x20, 0x250) + text[(at + 1)..];
                    what = $"change the character at {at}";
                    break;
            }

            mutations.Add((what, changed));
        }

        await ExerciseAllAsync(mutations);
    }

    [Fact]
    public async Task A_file_in_another_encoding_or_full_of_control_characters_is_refused_or_read()
    {
        var text = GoldenFlows.Read(GoldenFlows.All().First(f => f.File.Contains("Blink", StringComparison.Ordinal)));
        var inputs = new List<(string, string)>
        {
            ("utf-16 bytes read as utf-8", Encoding.UTF8.GetString(Encoding.Unicode.GetBytes(text))),
            ("utf-8 bytes read as latin-1", Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(text + "ñ"))),
            ("nul bytes between every character", string.Join("\0", text.Select(c => c.ToString()))),
            ("everything in one line with tabs", text.Replace("\n", "\t").Replace("\r", "")),
            ("a lone surrogate in the name", "{ \"Name\": \"a\ud800b\", \"BoardId\": \"esp32-devkit\" }"),
            ("a name with all kinds of line separators", "{ \"Name\": \"a\\u2028b\\u2029c\\u0085d\\re\\nf\", \"BoardId\": \"esp32-devkit\" }"),
        };

        await ExerciseAllAsync(inputs);
    }

    // ---------------------------------------------------------------- hostile on purpose

    [Fact]
    public async Task Nesting_deeper_than_any_real_file_is_refused_not_a_stack_overflow()
    {
        string Nested(int depth) => "{ \"Name\": \"x\", \"Nodes\": [{ \"Id\": \"a\", \"Type\": \"flow.manual-trigger\", \"Parameters\": { \"deep\": " +
                                    new string('[', depth) + new string(']', depth) + " } }] }";

        var outcomes = await ExerciseAllAsync(new (string, string)[]
        {
            ("depth 30", Nested(30)),
            ("depth 70", Nested(70)),
            ("depth 1000", Nested(1000)),
            ("depth 100000", Nested(100000)),
            ("objects 100000 deep", "{\"a\":".Repeat(100000) + "1" + "}".Repeat(100000)),
        });

        Assert.True(outcomes.GetValueOrDefault("refused") >= 3);
    }

    [Fact]
    public async Task A_huge_file_is_handled_in_reasonable_time()
    {
        string Led(int i) => $"{{ \"Id\": \"n{i}\", \"Type\": \"gpio.digital-write\", \"Parameters\": {{ \"pin\": 2, \"value\": true }} }}";
        string Wire(int i) => $"{{ \"Id\": \"w{i}\", \"FromNodeId\": \"n{i}\", \"FromPort\": \"done\", \"ToNodeId\": \"n{i + 1}\", \"ToPort\": \"trigger\" }}";

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var chain = 3000;
        var nodes = string.Join(",", Enumerable.Range(0, chain).Select(Led));
        var wires = string.Join(",", Enumerable.Range(0, chain - 1).Select(Wire));

        await ExerciseAllAsync(new (string, string)[]
        {
            ("a chain of 3000 blocks", $"{{ \"Name\": \"big\", \"BoardId\": \"esp32-devkit\", \"Nodes\": [{nodes}], \"Connections\": [{wires}] }}"),
            ("a name of 5 million characters", $"{{ \"Name\": \"{new string('x', 5_000_000)}\", \"BoardId\": \"esp32-devkit\" }}"),
            ("a parameter of 5 million characters", $"{{ \"Name\": \"x\", \"BoardId\": \"esp32-devkit\", \"Nodes\": [{{ \"Id\": \"a\", \"Type\": \"flow.manual-trigger\", \"Parameters\": {{ \"p\": \"{new string('y', 5_000_000)}\" }} }}] }}"),
            ("20000 unknown parameters on one block", $"{{ \"Name\": \"x\", \"BoardId\": \"esp32-devkit\", \"Nodes\": [{{ \"Id\": \"a\", \"Type\": \"flow.manual-trigger\", \"Parameters\": {{ {string.Join(",", Enumerable.Range(0, 20000).Select(i => $"\"p{i}\": {i}"))} }} }}] }}"),
        });

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"took {watch.Elapsed.TotalSeconds:0} s");
    }

    [Fact]
    public async Task Wires_that_loop_back_are_found_and_never_run_forever()
    {
        string Led(string id) => $"{{ \"Id\": \"{id}\", \"Type\": \"gpio.digital-write\", \"Parameters\": {{ \"pin\": 2, \"value\": true }} }}";
        string Wire(string id, string from, string to) => $"{{ \"Id\": \"{id}\", \"FromNodeId\": \"{from}\", \"FromPort\": \"done\", \"ToNodeId\": \"{to}\", \"ToPort\": \"trigger\" }}";

        var ring = 1500;
        var ringNodes = string.Join(",", Enumerable.Range(0, ring).Select(i => Led("r" + i)));
        var ringWires = string.Join(",", Enumerable.Range(0, ring).Select(i => Wire("w" + i, "r" + i, "r" + ((i + 1) % ring))));

        var outcomes = await ExerciseAllAsync(new (string, string)[]
        {
            ("two blocks feeding each other", $"{{ \"BoardId\": \"esp32-devkit\", \"Nodes\": [{Led("a")},{Led("b")}], \"Connections\": [{Wire("1", "a", "b")},{Wire("2", "b", "a")}] }}"),
            ("a block feeding itself", $"{{ \"BoardId\": \"esp32-devkit\", \"Nodes\": [{Led("a")}], \"Connections\": [{Wire("1", "a", "a")}] }}"),
            ("a ring of 1500 blocks", $"{{ \"BoardId\": \"esp32-devkit\", \"Nodes\": [{ringNodes}], \"Connections\": [{ringWires}] }}"),
        });

        Assert.Equal(3, outcomes.GetValueOrDefault("invalid"));
    }

    // ---------------------------------------------------------------- text in the file must not become code

    private static FlowDocument FlowNamed(string name, string label = "debug", string message = "")
    {
        var document = new FlowDocument { Name = name, BoardId = "esp32-devkit" };
        document.Nodes.Add(new FlowNode { Id = "go", Type = BuiltInBlockCatalog.ManualTrigger });
        document.Nodes.Add(new FlowNode { Id = "log", Type = BuiltInBlockCatalog.DebugLog, Parameters = { ["label"] = label, ["message"] = message } });
        document.Connections.Add(new FlowConnection { Id = "c", FromNodeId = "go", FromPort = "trigger", ToNodeId = "log", ToPort = "trigger" });
        return document;
    }

    private static readonly string[] Payloads =
    {
        "x\nSystem.Diagnostics.Process.Start(\"calc\");\n//",
        "x\r\nSystem.Diagnostics.Process.Start(\"calc\");\r\n//",
        "x\u2028System.Diagnostics.Process.Start(\"calc\");\u2028//",
        "x\u2029System.Diagnostics.Process.Start(\"calc\");\u2029//",
        "x\u0085System.Diagnostics.Process.Start(\"calc\");\u0085//",
        "x\"; System.Diagnostics.Process.Start(\"calc\"); //",
        "x\\\"; System.Diagnostics.Process.Start(\"calc\"); //",
        "x</summary> */ System.Diagnostics.Process.Start(\"calc\"); /*",
        "x\" + System.Diagnostics.Process.Start(\"calc\") + \"",
        "{System.Diagnostics.Process.Start(\"calc\")}",
        "{{}} {0} {",
        "\"\"\" \"\"\"",
        "\\u0022; System.Diagnostics.Process.Start(\"calc\"); //",
        "x\0y",
        "x\ud800y",
        "#if true\n#endif\n#error boom",
        "tab\there",
    };

    private static void AssertExportIsInert(FlowDocument document, string why)
    {
        foreach (var mode in new[] { FlowCSharpMode.Class, FlowCSharpMode.ConsoleApp })
        {
            var code = FlowCSharpGenerator.Generate(document, new FlowCSharpOptions { Mode = mode }).Code;
            CbflowGoldenTests.Compile(code, mode == FlowCSharpMode.ConsoleApp);   // text of the file never breaks the code out of its string or comment

            var tree = CSharpSyntaxTree.ParseText(code);
            var calls = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Select(i => i.Expression.ToString()).ToList();
            var injected = calls.Where(c => c.Contains("Process.Start", StringComparison.Ordinal) || c.StartsWith("System.", StringComparison.Ordinal)).ToList();
            Assert.True(injected.Count == 0, $"{why} ({mode}): the file's text became a call: {string.Join(", ", injected)}");
            Assert.True(tree.GetRoot().DescendantTrivia().All(t => !t.IsDirective), $"{why} ({mode}): the file's text became a preprocessor directive");
        }
    }

    [Fact]
    public void The_name_of_the_flow_cannot_break_out_of_the_comments_of_the_exported_code()
    {
        foreach (var payload in Payloads)
            AssertExportIsInert(FlowNamed(payload), $"name {Short(payload.Replace("\n", "\\n"))}");
    }

    [Fact]
    public void The_text_of_a_debug_block_cannot_break_out_of_its_string_in_the_exported_code()
    {
        foreach (var payload in Payloads)
        {
            AssertExportIsInert(FlowNamed("ok", label: payload), $"label {Short(payload.Replace("\n", "\\n"))}");
            AssertExportIsInert(FlowNamed("ok", message: payload), $"message {Short(payload.Replace("\n", "\\n"))}");
        }
    }

    [Fact]
    public void The_text_of_a_debug_block_on_a_wire_cannot_break_out_of_the_interpolated_string()
    {
        foreach (var payload in Payloads)
        {
            var document = FlowNamed("ok", label: payload);
            document.Nodes.Add(new FlowNode { Id = "n", Type = BuiltInBlockCatalog.ConstantNumber, Parameters = { ["value"] = 3 } });
            document.Connections.Add(new FlowConnection { Id = "c2", FromNodeId = "n", FromPort = "value", ToNodeId = "log", ToPort = "value" });

            AssertExportIsInert(document, $"interpolated label {Short(payload.Replace("\n", "\\n"))}");
        }
    }

    [Fact]
    public void Block_ids_that_look_alike_or_are_not_valid_names_still_export_to_code_that_compiles()
    {
        // "led 1" and "led-1" and "LED_1" would all become the same C# name; ids can also be keywords, start with digits, or be non-Latin
        var ids = new[] { "led 1", "led-1", "LED_1", "class", "int", "ct", "board", "1", "é", "日本語", "a.b", "a;b", "a\"b", "@x", "_", "led\nline" };
        var document = new FlowDocument { Name = "ids", BoardId = "esp32-devkit" };
        document.Nodes.Add(new FlowNode { Id = "go", Type = BuiltInBlockCatalog.ManualTrigger });
        var index = 0;
        foreach (var id in ids)
        {
            document.Nodes.Add(new FlowNode { Id = id, Type = BuiltInBlockCatalog.GpioSetOutput, Parameters = { ["pin"] = 2, ["value"] = true } });
            document.Connections.Add(new FlowConnection { Id = "w" + index++, FromNodeId = "go", FromPort = "trigger", ToNodeId = id, ToPort = "trigger" });
        }

        Assert.True(new FlowValidator().Validate(document, CatalogOf(document)).IsValid);
        AssertExportIsInert(document, "look-alike ids");
    }

    [Fact]
    public void A_class_name_from_a_hostile_name_is_always_a_valid_identifier()
    {
        foreach (var payload in Payloads.Concat(new[] { "", "   ", "123", "class", "global::System", "a.b.c", "Ünïcödé", "日本語", new string('x', 10000) }))
        {
            var generated = FlowCSharpGenerator.Generate(FlowNamed(payload));

            Assert.True(SyntaxFacts.IsValidIdentifier(generated.ClassName), $"'{Short(payload)}' became '{Short(generated.ClassName)}'");
        }
    }
}

internal static class StringRepeat
{
    public static string Repeat(this string text, int times) => new StringBuilder(text.Length * times).Insert(0, text, times).ToString();
}
