using System.Reflection;
using System.Reflection.Emit;

namespace CodeBridge.Designer.WinForms.Tests;

public class FlowFileLocatorTests
{
    private static Assembly AppBuiltIn(string projectDirectory)
    {
        var builder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("FakeApp" + Guid.NewGuid().ToString("N")[..6]), AssemblyBuilderAccess.Run);
        var constructor = typeof(AssemblyMetadataAttribute).GetConstructor(new[] { typeof(string), typeof(string) })!;
        builder.SetCustomAttribute(new CustomAttributeBuilder(constructor, new object[] { FlowFileLocator.ProjectDirectoryMetadataKey, projectDirectory }));
        return builder;
    }

    [Fact]
    public void AFullPathIsUsedAsIs()
    {
        using var folder = new TempFolder();
        var file = folder.File("anywhere/led.cbflow", "{}");

        Assert.Equal(file, FlowFileLocator.Resolve(file, null, folder.Path));
        Assert.Null(FlowFileLocator.Resolve(Path.Combine(folder.Path, "missing.cbflow"), null, folder.Path));
    }

    [Fact]
    public void ARelativePathIsLookedUpNextToTheProgram()
    {
        using var folder = new TempFolder();
        var file = folder.File("Flows/Blink.cbflow", "{}");

        Assert.Equal(file, FlowFileLocator.Resolve("Flows/Blink.cbflow", null, folder.Path));
        Assert.Equal(file, FlowFileLocator.Resolve(@"Flows\Blink.cbflow", null, folder.Path));
        Assert.Null(FlowFileLocator.Resolve("Flows/Other.cbflow", null, folder.Path));
    }

    [Fact]
    public void WhileDebugging_TheFileOfTheProjectWinsOverTheCopyInBin()
    {
        using var project = new TempFolder();
        using var bin = new TempFolder();
        var source = project.File("Flows/Blink.cbflow", "{ \"edited\": true }");
        bin.File("Flows/Blink.cbflow", "{ \"edited\": false }");

        Assert.Equal(source, FlowFileLocator.Resolve("Flows/Blink.cbflow", project.Path, bin.Path));
    }

    [Fact]
    public void WhenTheProjectFolderIsGone_TheCopyNextToTheProgramIsUsed()
    {
        using var bin = new TempFolder();
        var copy = bin.File("Flows/Blink.cbflow", "{}");

        var resolved = FlowFileLocator.Resolve("Flows/Blink.cbflow", Path.Combine(bin.Path, "no-such-project"), bin.Path);

        Assert.Equal(copy, resolved);
    }

    [Fact]
    public void TheProjectFolderComesFromTheMetadataOfTheApp()
    {
        using var project = new TempFolder();
        var source = project.File("led.cbflow", "{}");

        Assert.Equal(source, FlowFileLocator.Resolve("led.cbflow", AppBuiltIn(project.Path)));
        Assert.Null(FlowFileLocator.Resolve("led.cbflow", (Assembly?)null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingToLookFor(string? flowFile)
    {
        Assert.Null(FlowFileLocator.Resolve(flowFile, null, AppContext.BaseDirectory));
    }

    [Fact]
    public void TheEditorIsOnlyAskedForFilesOfItsOwnSolution()
    {
        using var folder = new TempFolder();
        var inside = folder.File("App/Flows/Blink.cbflow");

        Assert.True(FlowEditorLauncher.IsInside(folder.Path, inside));
        Assert.True(FlowEditorLauncher.IsInside(folder.Path + Path.DirectorySeparatorChar, inside));
        Assert.False(FlowEditorLauncher.IsInside(Path.Combine(folder.Path, "Ap"), inside)); // a longer folder name is not the same folder
        Assert.False(FlowEditorLauncher.IsInside(Path.Combine(folder.Path, "Other"), inside));
        Assert.False(FlowEditorLauncher.IsInside(string.Empty, inside));
    }
}
