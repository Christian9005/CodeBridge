using CodeBridge.VisualStudio;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeBridge.Designer.WinForms.Tests;

/// <summary>The form that "CodeBridge WinForms App" puts in a new project has to build, and has to behave like a plug-and-play starter.</summary>
public class StarterFormTemplateTests
{
    private static string Form(string namespaceName = "MyBoardApp", string className = "CodeBridgeStarterForm") =>
        CodeBridgeStarterFormTemplate.Create(namespaceName, className);

    [Fact]
    public void TheGeneratedFormCompilesWithoutErrorsOrWarnings()
    {
        var tree = CSharpSyntaxTree.ParseText(Form());
        var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var compilation = CSharpCompilation.Create(
            "StarterFormCheck",
            new[] { tree },
            trusted.Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        Assert.NotNull(compilation.GetTypeByMetadataName("MyBoardApp.CodeBridgeStarterForm"));

        var problems = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning)
            .Select(diagnostic => diagnostic.ToString())
            .ToList();

        Assert.Empty(problems);
    }

    [Fact]
    public void TheNamesAreFilledIn()
    {
        var source = Form("Acme.Lights", "LightsForm");

        Assert.Contains("namespace Acme.Lights", source);
        Assert.Contains("public sealed class LightsForm : Form", source);
        Assert.Contains("public LightsForm()", source);
        Assert.DoesNotContain("__NAMESPACE__", source);
        Assert.DoesNotContain("__CLASS__", source);
    }

    [Fact]
    public void TheFormFindsTheBoardAndFallsBackToTheSimulatorByItself()
    {
        var source = Form();

        Assert.Contains("_board.PortName = CodeBridgeBoardComponent.AutoPort;", source);
        Assert.Contains("_board.FallbackToSimulator = true;", source);
        Assert.Contains("RunAsync(_flow)", source);
        Assert.DoesNotContain("_connectButton", source); // Run connects, there is no separate step
    }

    [Fact]
    public void TheFormRunsTheFlowFileOfTheProject_AndRunsItAgainWhenItIsSaved()
    {
        var source = Form();

        Assert.Contains("private const string FlowFile = \"Flows/Blink.cbflow\";", source);
        Assert.Contains("_flow.FlowFile = FlowFile;", source);
        Assert.Contains("_flow.FlowFileLoaded +=", source);
        Assert.Contains("e.IsReload", source);
        Assert.Contains("_flow.OpenFlowFile()", source);
        Assert.Contains("Shown += async", source);
        Assert.DoesNotContain("LoadBlinkPreset", source); // the flow is the file, not a preset in memory
        Assert.DoesNotContain("ShowEditor", source);
    }

    [Fact]
    public void ABoardWithoutTheFirmwareIsOfferedTheUpload()
    {
        var source = Form();

        Assert.Contains("FirmwareMissing", source);
        Assert.Contains("AskToUploadFirmware", source);
        Assert.DoesNotContain("MessageBox.Show(this, ex.Message", source); // no raw exception text in a dialog
    }
}
