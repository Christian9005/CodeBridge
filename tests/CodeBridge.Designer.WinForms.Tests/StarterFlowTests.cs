using CodeBridge.Flow.Serialization;
using CodeBridge.VisualStudio;

namespace CodeBridge.Designer.WinForms.Tests;

public class StarterFlowTests
{
    [Fact]
    public void TheFlowIsWrittenWhereTheFormLooksForIt()
    {
        using var folder = new TempFolder();

        var path = CodeBridgeStarterFlow.EnsureIn(folder.Path);

        Assert.True(File.Exists(path));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(folder.Path, CodeBridgeStarterFormTemplate.FlowFile)),
            Path.GetFullPath(path));
    }

    [Fact]
    public void TheFlowIsAValidDocument()
    {
        var document = FlowDocumentJson.Deserialize(CodeBridgeStarterFlow.Blink);

        Assert.Equal("Blink", document.Name);
        Assert.Equal("esp32-devkit", document.BoardId);
        Assert.Equal(5, document.Nodes.Count);
        Assert.Equal(4, document.Connections.Count);
    }

    [Fact]
    public void AFlowThePersonAlreadyHasIsNotTouched()
    {
        using var folder = new TempFolder();
        var existing = folder.File("Flows/Blink.cbflow", "my own flow");

        CodeBridgeStarterFlow.EnsureIn(folder.Path);

        Assert.Equal("my own flow", File.ReadAllText(existing));
    }
}
