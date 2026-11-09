using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using CodeBridge.Flow;
using CodeBridge.TestSupport;
using CodeBridge.VisualStudio.Editor;

namespace CodeBridge.VisualStudio.Tests;

/// <summary>
/// A .cbflow that was damaged or crafted reaches Visual Studio when someone double-clicks it. The editor must open it, or say clearly that it cannot, and in
/// both cases leave the file alone. It must not throw out of the document load: that exception would travel up into Visual Studio itself.
/// </summary>
public sealed class HostileFlowEditorTests
{
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null)
            throw new InvalidOperationException(failure.ToString());
    }

    private const string Led = "{ \"Id\": \"led\", \"Type\": \"gpio.digital-write\", \"Parameters\": { \"pin\": 2, \"value\": true } }";
    private const string Go = "{ \"Id\": \"go\", \"Type\": \"flow.manual-trigger\" }";

    private static string Doc(string nodes, string wires = "[]") => "{ \"Name\": \"x\", \"BoardId\": \"esp32-devkit\", \"Nodes\": " + nodes + ", \"Connections\": " + wires + " }";

    private static string Wire(string id, string from, string to) =>
        "{ \"Id\": \"" + id + "\", \"FromNodeId\": \"" + from + "\", \"FromPort\": \"done\", \"ToNodeId\": \"" + to + "\", \"ToPort\": \"trigger\" }";

    public static IEnumerable<object[]> Inputs() => new[]
    {
        Doc("null"),
        Doc("[null]"),
        Doc("[" + Go + ", null, " + Led + "]"),
        Doc("[{ \"Id\": null, \"Type\": \"flow.manual-trigger\" }]"),
        Doc("[{ \"Id\": \"\", \"Type\": \"flow.manual-trigger\" }]"),
        Doc("[{ \"Id\": \"a\", \"Type\": null }]"),
        Doc("[{ \"Id\": \"a\", \"Type\": \"does.not.exist\" }]"),
        Doc("[{ \"Id\": \"a\", \"Type\": \"flow.manual-trigger\", \"Parameters\": null }]"),
        Doc("[{ \"Id\": \"a\", \"Type\": \"gpio.digital-write\", \"Parameters\": { \"pin\": \"abc\", \"value\": {} } }]"),
        Doc("[{ \"Id\": \"a\", \"Type\": \"gpio.digital-write\", \"Parameters\": { \"pin\": [1, 2], \"value\": null } }]"),
        Doc("[" + Go + "," + Go + "]"),
        Doc("[" + Go + "," + Led + "]", "null"),
        Doc("[" + Go + "," + Led + "]", "[null]"),
        Doc("[" + Go + "," + Led + "]", "[{ }]"),
        Doc("[" + Go + "," + Led + "]", "[" + Wire("c", "go", "nowhere") + "]"),
        Doc("[" + Go + "," + Led + "]", "[" + Wire("c", "nowhere", "led") + "]"),
        Doc("[" + Go + "," + Led + "]", "[" + Wire("c", "led", "led") + "]"),
        Doc("[" + Go + "," + Led + "]", "[" + Wire("c", "go", "led") + "," + Wire("c", "go", "led") + "]"),
        Doc("[" + Go + "," + Led + "]", "[{ \"Id\": \"c\", \"FromNodeId\": \"go\", \"FromPort\": \"nope\", \"ToNodeId\": \"led\", \"ToPort\": \"nope\" }]"),
        Doc("[" + Go + "," + Led + "]", "[{ \"Id\": \"c\", \"FromNodeId\": null, \"FromPort\": null, \"ToNodeId\": null, \"ToPort\": null }]"),
        "{ \"Name\": null, \"BoardId\": null, \"Nodes\": [] }",
        "{ \"Name\": \"x\", \"BoardId\": \"no-such-board\", \"Nodes\": [" + Go + "] }",
        "{ \"Name\": \"x\", \"BoardId\": \"\", \"Nodes\": [" + Go + "] }",
        Doc("[{ \"Id\": \"a\", \"Type\": \"flow.manual-trigger\", \"Position\": { \"X\": 1e300, \"Y\": -1e300 } }]"),
        Doc("[{ \"Id\": \"a\", \"Type\": \"flow.manual-trigger\", \"Position\": { \"X\": 1e999, \"Y\": 0 } }]"),
    }.Select(json => new object[] { json });

    private static FlowDocument? ReadLikeThePane(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<FlowDocument>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;   // the pane tells the user the file cannot be read and does not touch it
        }
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void The_editor_opens_the_document_or_the_pane_can_say_it_cannot(string json)
    {
        RunSta(() =>
        {
            var document = ReadLikeThePane(json);
            if (document is null)
                return;

            // what the pane does first: a file the editor cannot show is refused with a sentence, and the file is left alone
            var problem = FlowDocumentChecks.Problem(document);
            if (problem != null)
            {
                Assert.False(string.IsNullOrWhiteSpace(problem));
                return;
            }

            var control = new FlowEditorControl();
            control.LoadDocument(document);   // must not throw

            // and whatever it shows can be asked for again and saved without throwing
            var saved = GoldenFlows.SaveLikeVisualStudio(control.GetDocument());
            Assert.False(string.IsNullOrEmpty(saved));
        });
    }

    [Fact]
    public void A_big_flow_opens_in_reasonable_time()
    {
        var nodes = string.Join(",", Enumerable.Range(0, 400).Select(i => Led.Replace("\"led\"", "\"n" + i + "\"")));
        var wires = string.Join(",", Enumerable.Range(0, 399).Select(i => Wire("w" + i, "n" + i, "n" + (i + 1))));

        RunSta(() =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var control = new FlowEditorControl();
            control.LoadDocument(ReadLikeThePane(Doc("[" + nodes + "]", "[" + wires + "]"))!);

            Assert.Equal(400, control.GetDocument().Nodes.Count);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "took " + watch.Elapsed.TotalSeconds.ToString("0") + " s");
        });
    }
}
