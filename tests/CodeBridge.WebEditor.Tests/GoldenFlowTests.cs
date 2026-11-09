using System.Text.Json;
using CodeBridge.Flow;
using CodeBridge.Flow.Serialization;
using CodeBridge.IotBindings;
using CodeBridge.TestSupport;

namespace CodeBridge.WebEditor.Tests;

/// <summary>Every golden file must open in the web editor, survive a save from the page, and mean the same thing when it is opened again.</summary>
public sealed class GoldenFlowTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cb-web-golden-" + Guid.NewGuid().ToString("N"));

    public GoldenFlowTests()
    {
        Directory.CreateDirectory(_folder);
        IotBindingBlocks.Register();
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    public static IEnumerable<object[]> Files() => GoldenFlows.All().Select(f => new object[] { f.File });

    private string CopyOf(GoldenFlow flow)
    {
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".cbflow");
        File.Copy(flow.Path, path);   // the golden file itself must never be written
        return path;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void The_page_opens_the_file_and_sees_the_same_flow_as_the_sdk(string file)
    {
        var flow = GoldenFlows.All().Single(f => f.File == file);
        var session = new EditorSession(CopyOf(flow), boardId: null);

        Assert.Equal(flow.Nodes, session.Document.Nodes.Count);
        Assert.Equal(flow.Connections, session.Document.Connections.Count);
        Assert.Equal(
            GoldenFlows.Canonical(FlowDocumentJson.Deserialize(GoldenFlows.Read(flow).TrimStart('﻿'))),
            GoldenFlows.Canonical(FlowDocumentJson.Deserialize(session.FlowJson())));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Saving_from_the_page_keeps_the_meaning_of_the_file(string file)
    {
        var flow = GoldenFlows.All().Single(f => f.File == file);
        var path = CopyOf(flow);
        var before = GoldenFlows.Canonical(FlowDocumentJson.Deserialize(File.ReadAllText(path)));

        var session = new EditorSession(path, boardId: null);
        var answer = JsonSerializer.SerializeToElement(session.Update(session.FlowJson(), save: true), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(flow.Valid, answer.GetProperty("valid").GetBoolean());
        Assert.Equal(flow.Valid, answer.GetProperty("saved").GetBoolean());

        var after = GoldenFlows.Canonical(FlowDocumentJson.Deserialize(File.ReadAllText(path)));
        Assert.Equal(before, after);
        Assert.Equal(before, GoldenFlows.Canonical(new EditorSession(path, boardId: null).Document));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void A_file_the_validator_rejects_is_never_written_over(string file)
    {
        var flow = GoldenFlows.All().Single(f => f.File == file);
        if (flow.Valid)
            return;

        var path = CopyOf(flow);
        var original = File.ReadAllBytes(path);
        var session = new EditorSession(path, boardId: null);

        session.Update(session.FlowJson(), save: true);

        Assert.Equal(original, File.ReadAllBytes(path));
    }
}
