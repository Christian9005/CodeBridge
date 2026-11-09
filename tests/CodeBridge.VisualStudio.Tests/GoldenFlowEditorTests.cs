using System.Linq;
using System.Threading;
using CodeBridge.Flow;
using CodeBridge.TestSupport;
using CodeBridge.VisualStudio.Editor;

namespace CodeBridge.VisualStudio.Tests;

/// <summary>
/// The golden files, opened in the real flow editor the way the editor pane opens them, then saved the way it saves them and opened again. Whatever the
/// file looked like (camelCase, a BOM, numbers as text, a block this version does not know), nothing may be lost or changed on the way through.
/// </summary>
public sealed class GoldenFlowEditorTests
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

    [Fact]
    public void Every_golden_file_goes_through_the_editor_and_comes_out_the_same()
    {
        var flows = GoldenFlows.All();
        Assert.NotEmpty(flows);

        RunSta(() =>
        {
            foreach (var flow in flows)
            {
                var original = GoldenFlows.LoadLikeVisualStudio(GoldenFlows.Read(flow));
                var expected = GoldenFlows.Canonical(original);

                var control = new FlowEditorControl();
                control.LoadDocument(GoldenFlows.LoadLikeVisualStudio(GoldenFlows.Read(flow)));

                var inEditor = control.GetDocument();
                Assert.True(flow.Nodes == inEditor.Nodes.Count, $"{flow.File}: nodes");
                Assert.True(flow.Connections == inEditor.Connections.Count, $"{flow.File}: connections");

                var saved = GoldenFlows.SaveLikeVisualStudio(inEditor);
                Assert.True(expected == GoldenFlows.Canonical(GoldenFlows.LoadLikeVisualStudio(saved)), $"{flow.File}: reopened after the editor's own save");

                // And a second trip through a fresh editor changes nothing.
                var again = new FlowEditorControl();
                again.LoadDocument(GoldenFlows.LoadLikeVisualStudio(saved));
                Assert.True(expected == GoldenFlows.Canonical(again.GetDocument()), $"{flow.File}: second trip");
            }
        });
    }

    [Fact]
    public void A_block_this_version_does_not_know_stays_in_the_file_when_the_editor_saves()
    {
        var flow = GoldenFlows.All().Single(f => f.File.Contains("future"));

        RunSta(() =>
        {
            var control = new FlowEditorControl();
            control.LoadDocument(GoldenFlows.LoadLikeVisualStudio(GoldenFlows.Read(flow)));

            var saved = GoldenFlows.SaveLikeVisualStudio(control.GetDocument());

            Assert.Contains("future.quantum-gate", saved);
        });
    }
}
