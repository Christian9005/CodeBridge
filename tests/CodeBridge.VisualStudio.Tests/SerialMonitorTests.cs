using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodeBridge.VisualStudio.Monitor;

namespace CodeBridge.VisualStudio.Tests;

public sealed class SerialMonitorTests
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

    private static Dictionary<string, double> Parse(string line) => PlotParser.Parse(line).ToDictionary(p => p.Key, p => p.Value);

    [Theory]
    [InlineData("light:2048, temp:23.5, button:0", "light=2048;temp=23.5;button=0")]
    [InlineData("temp=23.5 hum=48", "temp=23.5;hum=48")]
    [InlineData("Temperature: 23.5 C  Humidity: 48 %", "Temperature=23.5;Humidity=48")]
    [InlineData("1,2,3", "v1=1;v2=2;v3=3")]
    [InlineData("1 2.5\t-3", "v1=1;v2=2.5;v3=-3")]
    [InlineData("-0.25", "v1=-0.25")]
    [InlineData("a:1e3,b:-2.5E-2", "a=1000;b=-0.025")]
    public void Numbers_are_found_the_way_the_Arduino_plotter_finds_them(string line, string expected)
    {
        var values = Parse(line);

        var actual = string.Join(";", values.Select(v => $"{v.Key}={v.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Booting CodeBridge...")]
    [InlineData("OK:PONG")]
    [InlineData("WiFi: Connected! IP = 192.168.1.5")]
    [InlineData("1,2,x")]
    [InlineData("1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17")]
    public void Ordinary_text_plots_nothing(string line)
    {
        Assert.Empty(PlotParser.Parse(line));
    }

    [Fact]
    public void Garbage_never_throws()
    {
        var random = new Random(9);
        for (var i = 0; i < 3000; i++)
        {
            var chars = Enumerable.Range(0, random.Next(0, 80)).Select(_ => (char)random.Next(9, 127)).ToArray();
            PlotParser.Parse(new string(chars));
        }
    }

    [Fact]
    public void The_log_keeps_only_the_newest_lines()
    {
        var buffer = new LogBuffer(100);

        var dropped = 0;
        for (var i = 0; i < 250; i++)
            dropped += buffer.Add("line " + i);

        Assert.Equal(100, buffer.Count);
        Assert.Equal(150, dropped);
        Assert.Equal("line 150", buffer.Lines[0]);
        Assert.Equal("line 249", buffer.Lines[buffer.Lines.Count - 1]);
    }

    [Fact]
    public void The_plot_history_is_bounded_per_series_and_in_the_number_of_series()
    {
        var data = new PlotData(window: 50, maxSeries: 3);

        for (var i = 0; i < 500; i++)
            data.Add(new[] { new KeyValuePair<string, double>("a", i), new KeyValuePair<string, double>("b", -i) });
        data.Add(new[] { new KeyValuePair<string, double>("c", 1), new KeyValuePair<string, double>("d", 2), new KeyValuePair<string, double>("e", 3) });

        Assert.Equal(50, data.Values("a").Count);
        Assert.Equal(450, data.Values("a")[0]);
        Assert.Equal(3, data.Names.Count); // "d" and "e" were refused: a noisy log cannot create unlimited series
        Assert.Equal(501, data.Samples);
    }

    [Fact]
    public void The_scale_adapts_and_a_flat_line_is_still_visible()
    {
        var data = new PlotData();
        Assert.Equal((0.0, 1.0), data.Range());

        data.Add(new[] { new KeyValuePair<string, double>("a", 5) });
        var (min, max) = data.Range();
        Assert.True(min < 5 && max > 5);

        data.Add(new[] { new KeyValuePair<string, double>("a", 105) });
        (min, max) = data.Range();
        Assert.True(min <= 5 && max >= 105);
    }

    [Fact]
    public void A_port_held_by_the_monitor_can_be_released_for_a_flow_run_once()
    {
        var released = 0;
        SerialPortLease.Acquire("COM77", () => released++);

        Assert.False(SerialPortLease.Release("COM78"));
        Assert.True(SerialPortLease.Release("com77"));
        Assert.False(SerialPortLease.Release("COM77"));
        Assert.Equal(1, released);
    }

    [Fact]
    public void The_monitor_shows_lines_and_feeds_the_plotter()
    {
        RunSta(() =>
        {
            var monitor = new SerialMonitorControl();

            monitor.AddLine("Booting...");
            monitor.AddLine("light:100, temp:20");
            monitor.AddLine("light:200, temp:21");

            Assert.Contains("Booting...", monitor.LogText);
            Assert.Equal(new[] { "light", "temp" }, monitor.Plotter.Data.Names);
            Assert.Equal(new[] { 100.0, 200.0 }, monitor.Plotter.Data.Values("light"));
            Assert.False(monitor.IsConnected);
            monitor.Shutdown();
        });
    }

    [Fact]
    public void The_plotter_renders_one_line_per_series()
    {
        RunSta(() =>
        {
            var plotter = new PlotterControl();
            for (var i = 0; i < 120; i++)
            {
                plotter.Data.Add(new[]
                {
                    new KeyValuePair<string, double>("light", 2048 + 1800 * Math.Sin(i / 12.0)),
                    new KeyValuePair<string, double>("temp", 1500 + 300 * Math.Cos(i / 9.0)),
                    new KeyValuePair<string, double>("button", i % 40 < 8 ? 3000 : 0)
                });
            }

            var host = new System.Windows.Controls.Border { Width = 560, Height = 240, Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)), Child = plotter };
            host.Measure(new Size(560, 240));
            host.Arrange(new Rect(0, 0, 560, 240));
            host.UpdateLayout();
            plotter.Render();
            host.UpdateLayout();

            Assert.Equal(3, plotter.DrawnSeries);

            var bitmap = new RenderTargetBitmap(560, 240, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            var path = Environment.GetEnvironmentVariable("CODEBRIDGE_PLOTTER_PNG");
            if (!string.IsNullOrEmpty(path))
                File.WriteAllBytes(path, stream.ToArray());
        });
    }
}
