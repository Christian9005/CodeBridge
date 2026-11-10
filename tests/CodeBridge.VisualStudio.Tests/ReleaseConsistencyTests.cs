using System.IO;
using System.Linq;
using System.Xml.Linq;
using CodeBridge.VisualStudio;

namespace CodeBridge.VisualStudio.Tests;

/// <summary>
/// Guards the things that silently break a published extension: version drift between files,
/// and the editor's block catalog drifting away from the runtime catalog.
/// </summary>
public sealed class ReleaseConsistencyTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static string ProductVersion()
    {
        var props = XDocument.Load(Path.Combine(RepoRoot(), "Directory.Build.props"));
        return props.Descendants("Version").First().Value.Trim();
    }

    [Fact]
    public void ToolboxManifest_versions_match_the_product_version()
    {
        var manifest = ToolboxManifestLoader.Load(
            Path.Combine(RepoRoot(), "src", "CodeBridge.VisualStudio", "Assets", "toolbox-components.json"));

        Assert.Equal(ProductVersion(), manifest.Package.Version);
        Assert.Equal(ProductVersion(), manifest.ManifestVersion);
    }

    [Fact]
    public void Vsix_manifest_version_matches_the_product_version()
    {
        var manifest = XDocument.Load(Path.Combine(RepoRoot(), "src", "CodeBridge.VisualStudio", "source.extension.vsixmanifest"));
        var identity = manifest.Descendants().First(element => element.Name.LocalName == "Identity");

        Assert.Equal(ProductVersion(), identity.Attribute("Version")!.Value);
    }

    [Fact]
    public void The_version_Visual_Studio_shows_in_Help_About_matches_the_product_version()
    {
        // [InstalledProductRegistration("CodeBridge", "...", "x.y.z")] is what Help > About Visual Studio lists; it said 0.3.0 for four releases
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CodeBridge.VisualStudio", "CodeBridgePackage.cs"));
        var registration = System.Text.RegularExpressions.Regex.Match(source, @"InstalledProductRegistration\([^)]*""(?<version>\d+\.\d+\.\d+)""\)");

        Assert.True(registration.Success, "InstalledProductRegistration with a version was not found in CodeBridgePackage.cs");
        Assert.Equal(ProductVersion(), registration.Groups["version"].Value);
    }

    [Fact]
    public void Every_text_the_package_asks_Visual_Studio_to_load_by_number_exists_in_its_resources()
    {
        // [ProvideEditorFactory(typeof(...), 110, ...)] names the editor with string 110 of the package; without it Visual Studio logs
        // "String load failed" and the editor has no name in Open With...
        var assembly = typeof(ToolboxManifestLoader).Assembly;   // the extension assembly, without loading the package type (it needs Visual Studio)
        using var stream = assembly.GetManifestResourceStream("VSPackage.resources");
        Assert.True(stream != null, "The assembly has no resource called VSPackage.resources: " + string.Join(", ", assembly.GetManifestResourceNames()));

        var texts = new System.Collections.Generic.Dictionary<string, string>();
        using (var reader = new System.Resources.ResourceReader(stream!))
        {
            foreach (System.Collections.DictionaryEntry entry in reader)
                texts[(string)entry.Key] = entry.Value?.ToString() ?? string.Empty;
        }

        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CodeBridge.VisualStudio", "CodeBridgePackage.cs"));
        var referenced = System.Text.RegularExpressions.Regex.Matches(source, @"ProvideEditorFactory\(typeof\([^)]*\),\s*(?<id>\d+)")
            .Cast<System.Text.RegularExpressions.Match>().Select(match => match.Groups["id"].Value).ToList();

        Assert.NotEmpty(referenced);
        foreach (var id in referenced)
            Assert.True(texts.TryGetValue(id, out var text) && text.Length > 0, "String " + id + " is not in VSPackage.resx");
    }

    [Fact]
    public void Shipped_source_does_not_contain_developer_machine_paths()
    {
        var sourceRoot = Path.Combine(RepoRoot(), "src");
        var offenders = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(@"\obj\") && !path.Contains(@"\bin\"))
            .Where(path => File.ReadAllText(path).Contains(@"C:\Projects"))
            .ToList();

        Assert.Empty(offenders);
    }
}
