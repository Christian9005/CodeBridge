using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeBridge.VisualStudio;

namespace CodeBridge.VisualStudio.Tests;

/// <summary>A new "CodeBridge WinForms App" has to be complete by itself: the package is in the project file, not left for a later step.</summary>
public sealed class ProjectTemplateTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static string TemplateDirectory() =>
        Path.Combine(RepoRoot(), "src", "CodeBridge.VisualStudio", "ProjectTemplates", "CodeBridgeWinFormsApp");

    [Fact]
    public void The_project_file_references_the_winforms_package_with_the_version_filled_in_by_the_wizard()
    {
        var project = XDocument.Load(Path.Combine(TemplateDirectory(), "ProjectTemplate.csproj"));
        var reference = project.Descendants("PackageReference").Single();

        Assert.Equal("CodeBridge.Designer.WinForms", reference.Attribute("Include")!.Value);
        Assert.Equal("$codebridgeversion$", reference.Attribute("Version")!.Value);
    }

    [Fact]
    public void The_package_the_template_references_is_the_one_the_manifest_describes()
    {
        var manifest = ToolboxManifestLoader.Load(
            Path.Combine(RepoRoot(), "src", "CodeBridge.VisualStudio", "Assets", "toolbox-components.json"));

        Assert.Equal("CodeBridge.Designer.WinForms", manifest.Package.Id);
        Assert.False(string.IsNullOrWhiteSpace(manifest.Package.Version));
    }

    [Fact]
    public void Program_starts_the_starter_form()
    {
        var program = File.ReadAllText(Path.Combine(TemplateDirectory(), "Program.cs"));

        Assert.Contains("new CodeBridgeStarterForm()", program);
    }
}
