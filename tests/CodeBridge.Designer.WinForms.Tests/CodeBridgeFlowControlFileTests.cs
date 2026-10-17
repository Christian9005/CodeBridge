using CodeBridge.Transport;
using CodeBridge.VisualStudio;

namespace CodeBridge.Designer.WinForms.Tests;

public class CodeBridgeFlowControlFileTests
{
    private static string FlowJson(string name) => CodeBridgeStarterFlow.Blink.Replace("\"Name\": \"Blink\"", $"\"Name\": \"{name}\"");

    [Fact]
    public void ThereIsNoFlowFileUnlessOneIsGiven()
    {
        using var control = new CodeBridgeFlowControl();

        Assert.Equal(string.Empty, control.FlowFile);
        Assert.Null(control.ResolvedFlowFile);
        Assert.True(control.WatchFlowFile);
    }

    [Fact]
    public void TheFlowIsReadFromTheFile()
    {
        using var folder = new TempFolder();
        var path = folder.File("Flows/Blink.cbflow", FlowJson("Blink"));
        using var control = new CodeBridgeFlowControl();
        FlowFileEventArgs? loaded = null;
        control.FlowFileLoaded += (_, e) => loaded = e;

        control.FlowFile = path;

        Assert.Equal("Blink", control.FlowName);
        Assert.Equal(5, control.Document.Nodes.Count);
        Assert.Equal(path, control.ResolvedFlowFile);
        Assert.NotNull(loaded);
        Assert.Equal(path, loaded!.FilePath);
        Assert.False(loaded.IsReload);
    }

    [Fact]
    public void AMissingFileIsReportedAndTheControlStaysUsable()
    {
        using var folder = new TempFolder();
        using var control = new CodeBridgeFlowControl();
        Exception? failure = null;
        control.FlowFileFailed += (_, ex) => failure = ex;

        control.FlowFile = Path.Combine(folder.Path, "nothing.cbflow");

        Assert.IsType<FileNotFoundException>(failure);
        Assert.Null(control.ResolvedFlowFile);
        Assert.Equal("Embedded Flow", control.FlowName);
    }

    [Fact]
    public void AFileThatIsHalfSaved_KeepsTheFlowThatWorks()
    {
        using var folder = new TempFolder();
        var path = folder.File("led.cbflow", FlowJson("Working"));
        using var control = new CodeBridgeFlowControl { FlowFile = path };
        Exception? failure = null;
        control.FlowFileFailed += (_, ex) => failure = ex;
        File.WriteAllText(path, "{ \"Name\": \"Broken\", \"Nodes\": [");

        var reloaded = control.ReloadFlowFile();

        Assert.False(reloaded);
        Assert.IsType<InvalidDataException>(failure);
        Assert.Contains(path, failure!.Message);
        Assert.Equal("Working", control.FlowName);
        Assert.Equal(5, control.Document.Nodes.Count);
    }

    [Fact]
    public void SavingTheFile_ReloadsTheFlow()
    {
        using var folder = new TempFolder();
        var path = folder.File("led.cbflow", FlowJson("Before"));
        using var control = new CodeBridgeFlowControl { PostToUi = action => action() };
        control.FlowFile = path;
        using var reloaded = new ManualResetEventSlim();
        FlowFileEventArgs? last = null;
        control.FlowFileLoaded += (_, e) =>
        {
            last = e;
            reloaded.Set();
        };

        File.WriteAllText(path, FlowJson("After"));

        Assert.True(reloaded.Wait(TimeSpan.FromSeconds(10)), "the control did not notice that the file was saved");
        Assert.True(last!.IsReload);
        Assert.Equal("After", control.FlowName);
    }

    [Fact]
    public void WithoutWatching_ASavedFileIsNotReloadedByItself()
    {
        using var folder = new TempFolder();
        var path = folder.File("led.cbflow", FlowJson("Before"));
        using var control = new CodeBridgeFlowControl { PostToUi = action => action(), WatchFlowFile = false };
        control.FlowFile = path;

        File.WriteAllText(path, FlowJson("After"));
        Thread.Sleep(700);

        Assert.Equal("Before", control.FlowName);
        Assert.True(control.ReloadFlowFile());
        Assert.Equal("After", control.FlowName);
    }

    [Fact]
    public async Task TheStarterFlowRunsOnTheSimulator()
    {
        using var folder = new TempFolder();
        var path = CodeBridgeStarterFlow.EnsureIn(folder.Path);
        using var control = new CodeBridgeFlowControl { FlowFile = path };
        using var board = new CodeBridgeBoardComponent { DiscoverBoards = () => Array.Empty<PortInfo>(), FallbackToSimulator = true };
        var traced = new List<string>();
        control.FlowTrace += (_, trace) => traced.Add(trace.NodeId);

        var result = await board.RunAsync(control);

        Assert.True(board.IsSimulated);
        Assert.Contains("led-on", result.NodeOutputs.Keys);
        Assert.Contains("led-off", result.NodeOutputs.Keys);
        Assert.Contains("led-on", traced);
    }
}
