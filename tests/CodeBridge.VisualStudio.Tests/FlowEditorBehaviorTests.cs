using System.Linq;
using System.Threading;
using System.Windows;
using CodeBridge.Flow;
using CodeBridge.VisualStudio.Editor;

namespace CodeBridge.VisualStudio.Tests;

public sealed class FlowEditorBehaviorTests
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

    private static FlowDocument TwoNodeDocument(string boardId = "esp32-devkit")
    {
        var document = new FlowDocument { BoardId = boardId };
        document.Nodes.Add(new FlowNode { Id = "a", Type = "flow.manual-trigger", Position = new FlowPosition(0, 0) });
        document.Nodes.Add(new FlowNode { Id = "b", Type = "gpio.digital-write", Position = new FlowPosition(250, 0) });
        document.Connections.Add(new FlowConnection { Id = "c1", FromNodeId = "a", FromPort = "trigger", ToNodeId = "b", ToPort = "trigger" });
        return document;
    }

    [Fact]
    public void Catalog_is_loaded_from_the_generated_board_files()
    {
        var esp32 = FlowCatalog.ForBoard("esp32-devkit");
        var uno = FlowCatalog.ForBoard("arduino-uno");

        Assert.True(esp32.Blocks.Count > 0, FlowCatalog.LastLoadError);
        Assert.NotNull(esp32.Get("gpio.digital-write"));
        Assert.NotNull(esp32.Get("DigitalWrite")); // legacy alias

        var espPins = esp32.Get("gpio.digital-write")!.Properties.First(p => p.Name == "pin").Options!;
        var unoPins = uno.Get("gpio.digital-write")!.Properties.First(p => p.Name == "pin").Options!;
        Assert.NotEqual(espPins.Count, unoPins.Count); // the editor offers each board's own pins
    }

    [Fact]
    public void Catalog_ports_keep_their_direction_and_type()
    {
        var write = FlowCatalog.ForBoard("esp32-devkit").Get("gpio.digital-write")!;

        Assert.Equal(FlowValueKind.Trigger, write.FindPort("trigger", FlowPortDirection.Input)!.ValueKind);
        Assert.Equal(FlowValueKind.Trigger, write.FindPort("done", FlowPortDirection.Output)!.ValueKind);
    }

    [Fact]
    public void Humanize_turns_camel_case_into_labels_with_units()
    {
        Assert.Equal("Interval (ms)", FlowEditorControl.Humanize("intervalMs"));
        Assert.Equal("Sample Rate (Hz)", FlowEditorControl.Humanize("sampleRateHz"));
        Assert.Equal("Pin", FlowEditorControl.Humanize("pin"));
    }

    [Fact]
    public void HostMessage_parses_a_json_line_and_ignores_noise()
    {
        var message = HostMessage.TryParse("{\"type\":\"ports\",\"ports\":[{\"name\":\"COM3\",\"description\":\"CP210x\"}]}");

        Assert.NotNull(message);
        Assert.Equal("ports", message!.Type);
        Assert.Equal("COM3", message.List("ports").Single().Str("name"));
        Assert.Null(HostMessage.TryParse("Unhandled exception. System.Something"));
    }

    [Fact]
    public void Delete_undo_redo_round_trip_restores_the_diagram()
    {
        RunSta(() =>
        {
            var control = new FlowEditorControl();
            control.LoadDocument(TwoNodeDocument());

            control.SelectAll();
            control.DeleteSelection();
            Assert.Empty(control.GetDocument().Nodes);
            Assert.Empty(control.GetDocument().Connections);

            control.Undo();
            Assert.Equal(2, control.GetDocument().Nodes.Count);
            Assert.Single(control.GetDocument().Connections);

            control.Redo();
            Assert.Empty(control.GetDocument().Nodes);
        });
    }

    [Fact]
    public void AddBlock_creates_a_node_with_default_parameters_and_marks_the_document_dirty()
    {
        RunSta(() =>
        {
            var control = new FlowEditorControl();
            control.LoadDocument(new FlowDocument());
            var dirty = 0;
            control.OnDirtyChanged += (_, _) => dirty++;

            control.AddBlock("gpio.blink-led", new Point(100, 100));

            var node = control.GetDocument().Nodes.Single();
            Assert.Equal("gpio.blink-led", node.Type);
            Assert.True(node.Parameters.ContainsKey("pin"));
            Assert.Equal(1, dirty);
        });
    }

    [Fact]
    public void Dirty_is_reported_again_after_the_document_was_saved()
    {
        RunSta(() =>
        {
            var control = new FlowEditorControl();
            control.LoadDocument(new FlowDocument());
            var dirty = 0;
            control.OnDirtyChanged += (_, _) => dirty++;

            control.AddBlock("flow.manual-trigger", new Point(0, 0));
            control.ClearDirty(); // what the pane does after Save
            control.AddBlock("core.timer", new Point(300, 0));

            Assert.Equal(2, dirty);
        });
    }

    [Fact]
    public void Copy_paste_duplicates_blocks_and_the_wires_between_them()
    {
        RunSta(() =>
        {
            var control = new FlowEditorControl();
            control.LoadDocument(TwoNodeDocument());

            control.SelectAll();
            control.CopySelection();
            control.Paste();

            var document = control.GetDocument();
            Assert.Equal(4, document.Nodes.Count);
            Assert.Equal(2, document.Connections.Count);
            Assert.Equal(4, document.Nodes.Select(n => n.Id).Distinct().Count());
        });
    }

    [Fact]
    public void Changing_to_a_board_keeps_the_diagram_but_switches_the_catalog()
    {
        RunSta(() =>
        {
            var control = new FlowEditorControl();
            control.LoadDocument(TwoNodeDocument());

            control.LoadDocument(TwoNodeDocument("arduino-uno"));

            Assert.Equal("arduino-uno", control.GetDocument().BoardId);
            Assert.Equal(2, control.GetDocument().Nodes.Count);
        });
    }

    private static FlowEditorControl LoadedControl(params (string Id, string Type)[] nodes)
    {
        var document = new FlowDocument();
        var x = 0;
        foreach (var (id, type) in nodes)
        {
            document.Nodes.Add(new FlowNode { Id = id, Type = type, Position = new FlowPosition(x, 0) });
            x += 250;
        }

        var control = new FlowEditorControl();
        control.LoadDocument(document);
        return control;
    }

    [Fact]
    public void Connecting_compatible_ports_creates_a_wire()
    {
        RunSta(() =>
        {
            var control = LoadedControl(("t", "flow.manual-trigger"), ("w", "gpio.digital-write"));

            var error = control.TryConnect("t", "trigger", "w", "trigger");

            Assert.Null(error);
            Assert.Single(control.GetDocument().Connections);
        });
    }

    [Fact]
    public void Connecting_incompatible_types_is_rejected_with_a_reason()
    {
        RunSta(() =>
        {
            var control = LoadedControl(("c", "logic.compare"), ("t", "core.timer"));

            var error = control.TryConnect("c", "result", "t", "trigger"); // Boolean -> Trigger

            Assert.NotNull(error);
            Assert.Contains("Incompatible", error);
            Assert.Empty(control.GetDocument().Connections);
        });
    }

    [Fact]
    public void Connecting_that_would_create_a_loop_is_rejected()
    {
        RunSta(() =>
        {
            var control = LoadedControl(("a", "gpio.digital-write"), ("b", "gpio.digital-write"));
            Assert.Null(control.TryConnect("a", "done", "b", "trigger"));

            var error = control.TryConnect("b", "done", "a", "trigger");

            Assert.NotNull(error);
            Assert.Contains("loop", error);
            Assert.Single(control.GetDocument().Connections);
        });
    }

    [Fact]
    public void Connecting_to_an_occupied_input_replaces_the_old_wire()
    {
        RunSta(() =>
        {
            var control = LoadedControl(("t", "flow.manual-trigger"), ("tm", "core.timer"), ("w", "gpio.digital-write"));
            Assert.Null(control.TryConnect("t", "trigger", "w", "trigger"));

            Assert.Null(control.TryConnect("tm", "tick", "w", "trigger"));

            var wire = Assert.Single(control.GetDocument().Connections);
            Assert.Equal("tm", wire.FromNodeId);
        });
    }

    [Fact]
    public void A_block_cannot_connect_to_itself()
    {
        RunSta(() =>
        {
            var control = LoadedControl(("tm", "core.timer"));

            Assert.NotNull(control.TryConnect("tm", "tick", "tm", "trigger"));
        });
    }
    [Fact]
    public void A_block_is_as_wide_as_its_title_and_its_outputs_move_with_its_right_edge()
    {
        RunSta(() =>
        {
            var control = new FlowEditorControl();
            control.LoadDocument(TwoNodeDocument());

            var shortTitle = control.AddBlock("logic.constant-number", new Point(0, 200));
            var longTitle = control.AddBlock("iot.vl53l1x", new Point(0, 400));   // "Laser Distance 4 m (VL53L1X)"

            Assert.Equal(FlowNodeControl.NodeWidth, shortTitle.BlockWidth);
            Assert.True(longTitle.BlockWidth > FlowNodeControl.NodeWidth, $"{longTitle.TitleText.Text} is {longTitle.BlockWidth} wide.");
            Assert.True(longTitle.BlockWidth <= FlowNodeControl.MaxNodeWidth);
            Assert.Equal(longTitle.BlockWidth, longTitle.Width);

            var output = longTitle.GetPortPosition("distance", FlowPortDirection.Output);
            Assert.Equal(System.Windows.Controls.Canvas.GetLeft(longTitle) + longTitle.BlockWidth, output.X);
        });
    }

    [Fact]
    public void A_title_never_makes_a_block_wider_than_the_limit()
    {
        RunSta(() =>
        {
            var typeface = new System.Windows.Media.Typeface("Segoe UI");

            Assert.Equal(FlowNodeControl.NodeWidth, FlowNodeControl.WidthFor("Map", typeface, 11));
            Assert.Equal(FlowNodeControl.MaxNodeWidth, FlowNodeControl.WidthFor(new string('W', 200), typeface, 11));
            Assert.True(FlowNodeControl.WidthFor("Weather + Altitude (BME280)", typeface, 11) > FlowNodeControl.NodeWidth);
        });
    }
}
