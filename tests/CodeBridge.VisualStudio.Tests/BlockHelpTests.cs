using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodeBridge.Flow;
using CodeBridge.VisualStudio.Editor;

namespace CodeBridge.VisualStudio.Tests;

public sealed class BlockHelpTests
{
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); } catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
            throw new InvalidOperationException(failure.ToString());
    }

    [Theory]
    [InlineData("esp32-devkit")]
    [InlineData("arduino-uno")]
    [InlineData("esp32-s3-devkit")]
    [InlineData("esp32-c3-devkit")]
    [InlineData("xiao-esp32-s3")]
    [InlineData("esp32-s2-mini")]
    [InlineData("arduino-nano")]
    [InlineData("arduino-mega")]
    public void Every_block_in_the_catalog_has_beginner_help_and_a_demo(string boardId)
    {
        foreach (var block in FlowCatalog.ForBoard(boardId).Blocks)
        {
            var help = BlockHelp.Get(block.Type);

            Assert.True(help != null, $"Block '{block.Type}' has no help entry.");
            Assert.False(string.IsNullOrWhiteSpace(help!.Summary));
            Assert.False(string.IsNullOrWhiteSpace(help.Tip));
            Assert.NotEqual(DemoKind.None, help.Demo);
            foreach (var port in block.Ports)
                Assert.True(help.Ports.ContainsKey(port.Name), $"Block '{block.Type}' port '{port.Name}' is not explained.");
        }
    }

    [Fact]
    public void Every_demo_builds_and_starts_and_stops()
    {
        RunSta(() =>
        {
            foreach (var kind in Enum.GetValues(typeof(DemoKind)).Cast<DemoKind>().Where(k => k != DemoKind.None))
            {
                var demo = new BlockDemoView(kind, Brushes.White, Brushes.Gray, Brushes.DimGray);
                var host = new Window { Content = demo, Width = 320, Height = 120, ShowInTaskbar = false, Left = -5000, Top = -5000 };
                host.Show();

                Assert.True(demo.HasAnimation, $"Demo {kind} has no animation.");
                demo.Start();
                demo.Stop();
                host.Close();
            }
        });
    }

    [Fact]
    public void Tooltip_content_can_be_built_for_every_block()
    {
        RunSta(() =>
        {
            var owner = new Border();
            foreach (var block in FlowCatalog.ForBoard("esp32-devkit").Blocks)
            {
                var content = BlockTip.Build(owner, block, out var demo);

                Assert.NotNull(content);
                Assert.NotNull(demo);
            }
        });
    }

    [Fact]
    public void Example_templates_open_in_the_editor()
    {
        var root = new System.IO.DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (root != null && !System.IO.Directory.Exists(System.IO.Path.Combine(root.FullName, "src", "CodeBridge.VisualStudio", "ItemTemplates")))
            root = root.Parent;

        var files = System.IO.Directory.GetFiles(System.IO.Path.Combine(root!.FullName, "src", "CodeBridge.VisualStudio", "ItemTemplates"), "Example*.cbflow", System.IO.SearchOption.AllDirectories);
        Assert.True(files.Length >= 5);

        RunSta(() =>
        {
            foreach (var file in files)
            {
                var document = System.Text.Json.JsonSerializer.Deserialize<FlowDocument>(
                    System.IO.File.ReadAllText(file),
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                var control = new FlowEditorControl();
                control.LoadDocument(document);

                Assert.Equal(document.Nodes.Count, control.GetDocument().Nodes.Count);
                Assert.Equal(document.Connections.Count, control.GetDocument().Connections.Count);
            }
        });
    }
}
