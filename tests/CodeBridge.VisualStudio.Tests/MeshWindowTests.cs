using System.Threading;
using CodeBridge.VisualStudio.Editor;

namespace CodeBridge.VisualStudio.Tests;

public sealed class MeshWindowTests
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
    [InlineData("COM3", "COM3")]
    [InlineData("192.168.1.50", "192.168.1.50")]
    [InlineData("mesh://COM3/E072A1F97B84", "COM3")]
    [InlineData("mesh://192.168.1.50/E072A1F97B84", "192.168.1.50")]
    [InlineData("MESH://simulator/AABBCC000002", "simulator")]
    public void The_gateway_is_found_inside_a_node_address(string port, string expected) =>
        Assert.Equal(expected, FlowEditorControl.GatewayOf(port));

    [Fact]
    public void The_mesh_window_builds_with_the_editor_theme()
    {
        RunSta(() =>
        {
            var window = new MeshWindow("simulator", null);

            Assert.NotNull(window.Content);
            Assert.True(window.Resources.Contains("VsToolWindowText"));
            Assert.Contains("Mesh", window.Title);
        });
    }

    [Fact]
    public void A_node_is_listed_by_its_name_and_falls_back_to_its_address()
    {
        var named = new MeshNodeItem { Mac = "E072A1F97B84", Name = "xiao", Board = "ESP32-S3", Firmware = "0.9.1", AgeSeconds = 4 };
        var unnamed = new MeshNodeItem { Mac = "E072A1F97B84", Board = "ESP32-S3", Firmware = "0.9.1", AgeSeconds = 0 };

        Assert.Equal("xiao", named.Title);
        Assert.Equal("E072A1F97B84", unnamed.Title);
        Assert.Contains("heard 4 s ago", named.Detail);
        Assert.Contains("just now", unnamed.Detail);
    }
}
