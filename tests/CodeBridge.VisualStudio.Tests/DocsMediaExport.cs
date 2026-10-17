using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodeBridge.Flow;
using CodeBridge.VisualStudio.Editor;
using CodeBridge.VisualStudio.Monitor;
using CodeBridge.VisualStudio.Tour;

namespace CodeBridge.VisualStudio.Tests;

/// <summary>
/// Not a test of behaviour: it draws the extension's own animations and windows to PNG files so the documentation
/// site can show exactly what Visual Studio shows. It does nothing unless CODEBRIDGE_DOCS_MEDIA names an output folder:
///   $env:CODEBRIDGE_DOCS_MEDIA = "C:\somewhere"; dotnet test tests/CodeBridge.VisualStudio.Tests --filter DocsMediaExport
/// The site's scripts/build-docs-media.mjs turns the frames into animated WebP.
/// </summary>
public sealed class DocsMediaExport
{
    private const double Scale = 3;
    private const int Fps = 25;

    private static readonly string[] Boards =
    {
        "esp32-devkit", "esp32-s3-devkit", "esp32-c3-devkit", "xiao-esp32-s3", "esp32-s2-mini",
        "arduino-uno", "arduino-nano", "arduino-mega"
    };

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                action();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
            throw new InvalidOperationException(failure.ToString());
    }

    [Fact]
    public void Export_documentation_media()
    {
        var root = Environment.GetEnvironmentVariable("CODEBRIDGE_DOCS_MEDIA");
        if (string.IsNullOrWhiteSpace(root))
            return;

        Directory.CreateDirectory(root);
        RunSta(() =>
        {
            var parts0 = (Environment.GetEnvironmentVariable("CODEBRIDGE_DOCS_PARTS") ?? "help,demos,shots,runs,code").Split(',');
            if (parts0.Contains("help")) ExportBlockHelp(root!);
            if (parts0.Contains("demos")) ExportDemos(root!);
            var parts = (Environment.GetEnvironmentVariable("CODEBRIDGE_DOCS_PARTS") ?? "help,demos,shots,runs,code").Split(',');
            if (parts.Contains("shots")) ExportShots(root!);
            if (parts.Contains("runs")) ExportRuns(root!);
            if (parts.Contains("code")) ExportCode(root!);
            File.WriteAllLines(Path.Combine(root!, "shots.log"), Log);
        });
    }

    // -------------------------------------------------------------- a real run on the simulator

    /// <summary>
    /// Presses Run in the real editor with the Simulator selected and photographs the window while the real FlowHost executes the flow:
    /// blocks turning RUN then OK, the output panel, the board view with the pins. Needs artifacts/flowhost (build/pack.ps1 -Vsix).
    /// </summary>
    private static void ExportRuns(string root)
    {
        foreach (var run in new[] { ("blink", "Example1_Blink.cbflow"), ("nightlight", "Example3_NightLight.cbflow") })
            Try("run-" + run.Item1, () => RunAndRecord(root, run.Item1, run.Item2));
    }

    private static T Member<T>(object target, string name)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var field = target.GetType().GetField(name, flags);
        if (field == null)
            throw new MissingFieldException(target.GetType().Name, name);
        return (T)field.GetValue(target)!;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.GetDirectories(from))
            CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)));
    }

    private static void RunAndRecord(string root, string name, string example)
    {
        // The extension looks for its helper process in a FlowHost folder next to its assembly (development and tests use this).
        var host = Path.Combine(ExtensionPaths.Directory, "FlowHost");
        if (!File.Exists(Path.Combine(host, "CodeBridge.FlowHost.exe")))
        {
            var built = Path.Combine(RepositoryRoot(), "artifacts", "flowhost");
            if (!Directory.Exists(built))
                throw new InvalidOperationException("artifacts/flowhost is missing: run build/pack.ps1 -Vsix first.");
            CopyDirectory(built, host);
        }

        var templates = Path.Combine(RepositoryRoot(), "src", "CodeBridge.VisualStudio", "ItemTemplates");
        var file = Directory.GetFiles(templates, example, SearchOption.AllDirectories).First();
        var document = JsonSerializer.Deserialize<FlowDocument>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var control = new FlowEditorControl();
        control.LoadDocument(document);
        var window = new Window { Content = control, Width = 1240, Height = 700, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -5000, Top = -5000 };
        window.Show();

        var stateField = typeof(FlowEditorControl).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!;
        string State() => stateField.GetValue(control)!.ToString()!;
        try
        {
            Settle(window.Dispatcher);
            control.FitView();

            // The port list is filled by asking the FlowHost, so wait for it and pick the Simulator exactly as a person would.
            var ports = Member<System.Windows.Controls.ComboBox>(control, "PortCombo");
            var waiting = Stopwatch.StartNew();
            object? simulator = null;
            while (simulator == null && waiting.Elapsed < TimeSpan.FromSeconds(30))
            {
                Pump(window.Dispatcher);
                Thread.Sleep(50);
                foreach (var item in ports.Items)
                    if (item.ToString()!.StartsWith("Simulator", StringComparison.OrdinalIgnoreCase))
                        simulator = item;
            }

            if (simulator == null)
                throw new InvalidOperationException("The Simulator never appeared in the port list.");
            ports.SelectedItem = simulator;
            Settle(window.Dispatcher);

            var frames = new List<(int Ms, byte[] Png)>();
            var clock = Stopwatch.StartNew();
            frames.Add((0, Png(window, window.ActualWidth, window.ActualHeight, 1)));

            typeof(FlowEditorControl).GetMethod("RunFlow", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, null);
            if (State() == "Idle")
                throw new InvalidOperationException("Run did not start (the editor stayed idle).");

            var started = clock.ElapsedMilliseconds;
            long next = started;
            while (clock.Elapsed < TimeSpan.FromSeconds(45))
            {
                Pump(window.Dispatcher);
                var now = clock.ElapsedMilliseconds;
                if (now >= next)
                {
                    frames.Add(((int)now, Png(window, window.ActualWidth, window.ActualHeight, 1)));
                    next = now + 80;
                }

                if (State() == "Idle" && now - started > 300)
                    break;
                Thread.Sleep(10);
            }

            // Let the last messages land, then one more picture of the finished state.
            for (var i = 0; i < 6; i++) { Pump(window.Dispatcher); Thread.Sleep(40); }
            frames.Add(((int)clock.ElapsedMilliseconds, Png(window, window.ActualWidth, window.ActualHeight, 1)));

            var folder = Path.Combine(root, "runs", name);
            Directory.CreateDirectory(folder);
            var timeline = new List<int>();
            byte[]? previous = null;
            var kept = 0;
            foreach (var frame in frames)
            {
                if (previous != null && frame.Png.SequenceEqual(previous))
                    continue;
                File.WriteAllBytes(Path.Combine(folder, $"f{kept:D3}.png"), frame.Png);
                timeline.Add(frame.Ms);
                previous = frame.Png;
                kept++;
            }

            File.WriteAllText(Path.Combine(root, "runs", name + ".json"), JsonSerializer.Serialize(new { name, example, width = (int)window.ActualWidth, height = (int)window.ActualHeight, frames = kept, ms = timeline, state = State() }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            if (State() != "Idle")
                typeof(FlowEditorControl).GetMethod("StopHost", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(control, null);
            window.Close();
        }
    }

    // -------------------------------------------------------------- the C# that "Export C#" writes

    private static void ExportCode(string root)
    {
        var folder = Path.Combine(root, "export");
        Directory.CreateDirectory(folder);
        var templates = Path.Combine(RepositoryRoot(), "src", "CodeBridge.VisualStudio", "ItemTemplates");
        foreach (var example in new[] { ("example1", "Example1_Blink"), ("example4", "Example4_ServoSweep") })
        {
            Try("export-" + example.Item1, () =>
            {
                var file = Directory.GetFiles(templates, example.Item2 + ".cbflow", SearchOption.AllDirectories).First();
                var document = JsonSerializer.Deserialize<FlowDocument>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                foreach (var action in new[] { (ExportAction.AddClassToProject, "class"), (ExportAction.AddConsoleProgram, "console") })
                {
                    // What the Export C# button does: the editor asks the FlowHost and raises the generated code.
                    var control = new FlowEditorControl { FlowFilePath = @"C:\work\" + example.Item2 + ".cbflow", NamespaceProvider = () => "MyApp" };
                    control.LoadDocument(document);
                    ExportedCode? exported = null;
                    control.CodeExported += code => exported = code;
                    AwaitTask(control.ExportAsync(action.Item1));
                    if (exported == null)
                        throw new InvalidOperationException("The editor raised no code for " + example.Item2 + " (" + action.Item2 + ").");
                    File.WriteAllText(Path.Combine(folder, example.Item1 + "-" + action.Item2 + ".cs"), exported.Code);
                }
            });
        }
    }

    /// <summary>Lets continuations of an async UI method run on this thread while waiting for it.</summary>
    private static void AwaitTask(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(10);
        }

        task.GetAwaiter().GetResult();
    }

    // -------------------------------------------------------------- the extension's windows

    private static readonly List<string> Log = new List<string>();

    private static void ExportShots(string root)
    {
        var folder = Path.Combine(root, "shots");
        Directory.CreateDirectory(folder);

        // The editor with each of the five example flows the project templates ship
        var templates = Path.Combine(RepositoryRoot(), "src", "CodeBridge.VisualStudio", "ItemTemplates");
        var examples = new Dictionary<string, string>
        {
            ["editor-blink"] = "Example1_Blink.cbflow",
            ["editor-pattern"] = "Example2_BlinkPattern.cbflow",
            ["editor-night-light"] = "Example3_NightLight.cbflow",
            ["editor-servo"] = "Example4_ServoSweep.cbflow",
            ["editor-button"] = "Example5_ButtonLed.cbflow"
        };
        foreach (var example in examples)
        {
            Try(example.Key, () =>
            {
                var file = Directory.GetFiles(templates, example.Value, SearchOption.AllDirectories).First();
                var document = JsonSerializer.Deserialize<FlowDocument>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                var control = new FlowEditorControl();
                control.LoadDocument(document);
                Capture(folder, example.Key, control, 1240, 660, window => control.FitView());
            });
        }

        // A block selected: the Properties panel explains it in plain words
        foreach (var selected in new[] { ("editor-properties", "Example3_NightLight.cbflow", "logic.compare"), ("editor-properties-blink", "Example1_Blink.cbflow", "gpio.blink-led") })
        {
            Try(selected.Item1, () =>
            {
                var file = Directory.GetFiles(templates, selected.Item2, SearchOption.AllDirectories).First();
                var document = JsonSerializer.Deserialize<FlowDocument>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                var control = new FlowEditorControl();
                control.LoadDocument(document);
                var map = (System.Collections.IDictionary)typeof(FlowEditorControl).GetField("_nodeControlMap", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(control)!;
                var node = map[document.Nodes.First(n => n.Type == selected.Item3).Id];
                Capture(folder, selected.Item1, control, 1240, 660, window =>
                {
                    control.FitView();
                    typeof(FlowEditorControl).GetMethod("SelectOnly", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, new[] { node });
                });
            });
        }

        Try("board-view", () =>
        {
            var view = new BoardViewControl();
            var pins = new[] { 0, 2, 4, 13, 16, 21, 25, 26, 34, 35 }.Select(n => (n, $"GPIO {n}")).ToList();
            view.SetBoard("ESP32 DevKit", "ESP32", pins, ledPin: 2, analogMax: 4095);
            view.SetSource("Simulator (virtual board)");
            view.Apply(2, "pwm", 180);
            view.Apply(13, "servo", 120);
            view.Apply(34, "analog", 3000);
            view.Apply(4, "input", 1);
            view.Apply(21, "digital", 1);
            var ground = new Border { Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)), Child = view };
            Capture(folder, "board-view", ground, 280, 280, null);
        });

        Try("plotter", () =>
        {
            var plotter = new PlotterControl();
            for (var i = 0; i < 320; i++)
            {
                plotter.Data.Add(new[]
                {
                    new KeyValuePair<string, double>("light", 2048 + 1800 * Math.Sin(i / 12.0)),
                    new KeyValuePair<string, double>("temp", 1500 + 300 * Math.Cos(i / 9.0)),
                    new KeyValuePair<string, double>("button", i % 40 < 8 ? 3000 : 0)
                });
            }

            var ground = new Border { Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)), Child = plotter };
            Capture(folder, "plotter", ground, 640, 260, window => plotter.Render());
        });

        Try("mesh-window", () =>
        {
            var mesh = new MeshWindow("simulator", null);
            Capture(folder, "mesh-window", mesh, 0, 0, window =>
            {
                var nodes = (ListBox)typeof(MeshWindow).GetField("_nodes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mesh)!;
                nodes.ItemsSource = new[]
                {
                    new MeshNodeItem { Mac = "E072A1F97B84", Name = "greenhouse", Board = "ESP32-S3", Firmware = "0.9.1", AgeSeconds = 2 },
                    new MeshNodeItem { Mac = "E072A1F97C10", Name = "door", Board = "XIAO ESP32-S3", Firmware = "0.9.1", AgeSeconds = 4 }
                };
                typeof(MeshWindow).GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(mesh, new object[] { false, "2 board(s) found. Select one and press Use this board." });
            });
        });
        Try("home-assistant-window", () => Capture(folder, "home-assistant-window", new HomeAssistantWindow(false, "This flow has 3 GPIO block(s) to share."), 0, 0, null));

        for (var chapter = 0; chapter < TourContent.Chapters.Count; chapter++)
        {
            var index = chapter;
            // Chapter 7 draws the SDK as a NuGet package; the site must not suggest it is published on nuget.org.
            if (TourContent.Chapters[index].Title.StartsWith("Beyond", StringComparison.Ordinal))
                continue;

            Try($"tour-{index + 1}", () =>
            {
                var control = new TourControl(new EditorSettings());
                Capture(folder, $"tour-{index + 1}", control, 1100, 760, window => control.ShowChapter(index));
            });
        }

        Try("tooltips", () =>
        {
            var owner = new FlowEditorControl();
            var total = typeof(BlockDemoView).GetField("Total", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var catalog = FlowCatalog.ForBoard("esp32-devkit");
            foreach (var type in new[] { "flow.manual-trigger", "gpio.blink-led", "acquisition.sample-channel", "servo.write", "sensor.dht", "module.oled" })
            {
                var definition = catalog.Get(type);
                if (definition == null)
                    continue;

                var tip = BlockTip.Build(owner, definition, out var demo);
                var host = new Border { Padding = new Thickness(18), Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)), Child = tip, Width = 368 };
                Capture(folder, "tip-" + type.Replace('.', '-'), host, 368, 0, window =>
                {
                    if (demo == null)
                        return;
                    demo.Start();
                    demo.SeekTo((double)total.GetValue(demo)! * 0.5);
                });
            }
        });

    }

    private static void Try(string name, Action action)
    {
        try
        {
            action();
            Log.Add("ok     " + name);
        }
        catch (Exception ex)
        {
            Log.Add("FAILED " + name + ": " + ex.GetType().Name + " " + ex.Message);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "src", "CodeBridge.VisualStudio", "ItemTemplates")))
            directory = directory.Parent;
        return directory!.FullName;
    }

    /// <summary>Shows the element in an off-screen window, lets it lay out and draws it at 2x. Width/height 0 keeps the content's own size.</summary>
    private static void Capture(string folder, string name, object content, double width, double height, Action<Window>? prepare)
    {
        var window = content as Window ?? new Window { Content = content, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize };
        if (!(content is Window))
        {
            window.Width = width;
            if (height > 0)
                window.Height = height;
            else
                window.SizeToContent = SizeToContent.Height;
        }
        else
        {
            if (width > 0) window.Width = width;
            if (height > 0) window.Height = height;
        }

        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -5000;
        window.Top = -5000;
        window.Show();
        try
        {
            Settle(window.Dispatcher);
            prepare?.Invoke(window);
            Settle(window.Dispatcher);
            window.UpdateLayout();

            const double shotScale = 2;
            var w = window.ActualWidth;
            var h = window.ActualHeight;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(w * shotScale), (int)Math.Ceiling(h * shotScale), 96 * shotScale, 96 * shotScale, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            File.WriteAllBytes(Path.Combine(folder, name + ".png"), stream.ToArray());
        }
        finally
        {
            window.Close();
        }
    }

    private static void Settle(Dispatcher dispatcher)
    {
        for (var i = 0; i < 3; i++)
        {
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    // -------------------------------------------------------------- block help (text the tooltips show)

    private static void ExportBlockHelp(string root)
    {
        var blocks = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var board in Boards)
        {
            foreach (var block in FlowCatalog.ForBoard(board).Blocks)
            {
                if (blocks.ContainsKey(block.Type))
                    continue;

                var help = BlockHelp.Get(block.Type);
                blocks[block.Type] = new
                {
                    name = block.DisplayName,
                    category = block.Category,
                    summary = BlockHelp.SummaryFor(block),
                    tip = help?.Tip ?? string.Empty,
                    demo = help == null ? string.Empty : help.Demo.ToString(),
                    ports = help == null ? new Dictionary<string, string>() : help.Ports.ToDictionary(p => p.Key, p => p.Value)
                };
            }
        }

        File.WriteAllText(Path.Combine(root, "block-help.json"), JsonSerializer.Serialize(blocks, new JsonSerializerOptions { WriteIndented = true }));
    }

    // -------------------------------------------------------------- the looping block demos

    private static void ExportDemos(string root)
    {
        var owner = new FlowEditorControl();
        Brush Resource(string key, Brush fallback) => owner.TryFindResource(key) as Brush ?? fallback;
        var text = Resource("VsToolWindowText", Brushes.White);
        var muted = Resource("VsGrayText", Brushes.Gray);
        var border = Resource("VsToolWindowBorder", Brushes.DimGray);
        var header = Resource("VsToolWindowHeader", Brushes.Black);

        var total = typeof(BlockDemoView).GetField("Total", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var index = new List<object>();

        foreach (var kind in Enum.GetValues(typeof(DemoKind)).Cast<DemoKind>().Where(k => k != DemoKind.None))
        {
            var demo = new BlockDemoView(kind, text, muted, border);
            var host = new Border { Width = BlockDemoView.DemoWidth, Height = BlockDemoView.DemoHeight, Background = header, Child = demo };
            var window = new Window { Content = host, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None, ShowInTaskbar = false, Left = -5000, Top = -5000 };
            window.Show();

            var seconds = (double)total.GetValue(demo)!;
            var frames = (int)Math.Round(seconds * Fps);
            var folder = Path.Combine(root, "demos", kind.ToString());
            Directory.CreateDirectory(folder);

            demo.Start();
            var distinct = new HashSet<int>();
            for (var i = 0; i < frames; i++)
            {
                demo.SeekTo(i / (double)Fps);
                Pump(window.Dispatcher);
                host.UpdateLayout();

                var bytes = Png(host, BlockDemoView.DemoWidth, BlockDemoView.DemoHeight);
                distinct.Add(bytes.Length * 31 + bytes[bytes.Length / 2]);
                File.WriteAllBytes(Path.Combine(folder, $"f{i:D3}.png"), bytes);
            }

            demo.Stop();
            window.Close();
            index.Add(new { kind = kind.ToString(), seconds, fps = Fps, frames, width = (int)BlockDemoView.DemoWidth, height = (int)BlockDemoView.DemoHeight, distinct = distinct.Count });
        }

        File.WriteAllText(Path.Combine(root, "demos.json"), JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));
    }

    // -------------------------------------------------------------- helpers

    private static void Pump(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static byte[] Png(FrameworkElement element, double width, double height) => Png(element, width, height, Scale);

    private static byte[] Png(FrameworkElement element, double width, double height, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
