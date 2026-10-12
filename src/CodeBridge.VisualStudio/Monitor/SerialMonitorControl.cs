#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using CodeBridge.VisualStudio.Editor;

namespace CodeBridge.VisualStudio.Monitor
{
    /// <summary>
    /// The serial monitor and plotter: connect to a board's USB port, read what it prints, send text, and watch the numbers as a chart.
    /// It talks to the board through CodeBridge.FlowHost ("monitor"), like the flow editor does.
    /// </summary>
    internal sealed class SerialMonitorControl : UserControl
    {
        private static readonly int[] BaudRates = { 300, 1200, 2400, 4800, 9600, 19200, 38400, 57600, 74880, 115200, 230400, 460800, 921600 };

        private readonly ComboBox _port = new ComboBox { IsEditable = true, MinWidth = 150, Margin = new Thickness(0, 0, 4, 0), ToolTip = "USB serial port (COM3) or Simulator" };
        private readonly ComboBox _baud = new ComboBox { Width = 84, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Baud rate: it must match Serial.begin() in the board's program" };
        private readonly ComboBox _newline = new ComboBox { Width = 84, ToolTip = "What is appended to the text you send" };
        private readonly Button _connect = new Button { Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        private readonly Button _refresh = new Button { Content = "↻", Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 0, 8, 0), ToolTip = "Refresh the list of ports" };
        private readonly ToggleButton _monitorTab = new ToggleButton { Content = "Monitor", Padding = new Thickness(10, 3, 10, 3), IsChecked = true };
        private readonly ToggleButton _plotterTab = new ToggleButton { Content = "Plotter", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
        private readonly ToggleButton _pause = new ToggleButton { Content = "Pause", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 4, 0), ToolTip = "Freeze the display (data keeps arriving)" };
        private readonly Button _clear = new Button { Content = "Clear", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 4, 0) };
        private readonly Button _save = new Button { Content = "Save...", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 4, 0) };
        private readonly Button _reset = new Button { Content = "Reset board", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 8, 0), ToolTip = "Restart the board (pulses the reset line) to see its start-up messages" };
        private readonly CheckBox _autoScroll = new CheckBox { Content = "Auto-scroll", IsChecked = true, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly CheckBox _timestamps = new CheckBox { Content = "Timestamps", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBox _log = new TextBox
        {
            IsReadOnly = true,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0)
        };
        private readonly PlotterControl _plotter = new PlotterControl { Visibility = Visibility.Collapsed };
        private readonly TextBox _input = new TextBox { Padding = new Thickness(4, 3, 4, 3), ToolTip = "Type text for the board and press Enter (Up/Down recall earlier lines)" };
        private readonly Button _send = new Button { Content = "Send", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(6, 0, 0, 0) };
        private readonly TextBlock _status = new TextBlock { Margin = new Thickness(8, 3, 8, 3), FontSize = 11 };
        private readonly LogBuffer _buffer = new LogBuffer();
        private readonly List<string> _history = new List<string>();
        private readonly DispatcherTimer _redraw;
        private HostProcess? _host;
        private string? _connectedPort;
        private int _historyIndex;
        private bool _dirty;
        private bool _stopping;

        public SerialMonitorControl()
        {
            NativeUi.ApplyEditorTheme(this);
            SetResourceReference(BackgroundProperty, "VsToolWindowBackground");
            SetResourceReference(ForegroundProperty, "VsToolWindowText");
            _status.SetResourceReference(TextBlock.ForegroundProperty, "VsGrayText");
            _log.SetResourceReference(BackgroundProperty, "VsWindowBackground");
            _log.SetResourceReference(ForegroundProperty, "VsToolWindowText");

            foreach (var rate in BaudRates)
                _baud.Items.Add(rate);
            _baud.SelectedItem = 115200;
            foreach (var text in new[] { "No line ending", "Newline", "Carriage return", "Both CR+LF" })
                _newline.Items.Add(text);
            _newline.SelectedIndex = 1;

            Content = Build();
            WireEvents();
            UpdateButtons();

            _redraw = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
            _redraw.Tick += (_, __) => Flush();
            _redraw.Start();
        }

        public bool IsConnected => _host != null;

        public string Port
        {
            get => (_port.Text ?? string.Empty).Trim();
            set => _port.Text = value;
        }

        public string LogText => _buffer.Text;

        internal PlotterControl Plotter => _plotter;

        internal bool PlotterVisible => _plotter.Visibility == Visibility.Visible;

        private FrameworkElement Build()
        {
            var bar = new WrapPanel { Margin = new Thickness(8, 6, 8, 2) };
            bar.Children.Add(_port);
            bar.Children.Add(_refresh);
            bar.Children.Add(_baud);
            bar.Children.Add(_connect);
            bar.Children.Add(_monitorTab);
            bar.Children.Add(_plotterTab);
            bar.Children.Add(_pause);
            bar.Children.Add(_clear);
            bar.Children.Add(_save);
            bar.Children.Add(_reset);
            bar.Children.Add(_autoScroll);
            bar.Children.Add(_timestamps);

            var send = new DockPanel { Margin = new Thickness(8, 4, 8, 6) };
            DockPanel.SetDock(_send, Dock.Right);
            DockPanel.SetDock(_newline, Dock.Right);
            _newline.Margin = new Thickness(6, 0, 0, 0);
            send.Children.Add(_send);
            send.Children.Add(_newline);
            send.Children.Add(_input);

            var center = new Grid();
            center.Children.Add(_log);
            center.Children.Add(_plotter);

            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            DockPanel.SetDock(send, Dock.Bottom);
            DockPanel.SetDock(_status, Dock.Bottom);
            root.Children.Add(bar);
            root.Children.Add(send);
            root.Children.Add(_status);
            root.Children.Add(center);
            return root;
        }

        private void WireEvents()
        {
            _connect.Click += (_, __) => { if (IsConnected) Disconnect(); else Connect(); };
            _refresh.Click += (_, __) => _ = RefreshPortsAsync();
            _port.DropDownOpened += (_, __) => _ = RefreshPortsAsync();
            _clear.Click += (_, __) => ClearAll();
            _save.Click += (_, __) => Save();
            _reset.Click += (_, __) => _host?.SendLine("reset");
            _send.Click += (_, __) => SendInput();
            _pause.Click += (_, __) => { if (_pause.IsChecked != true) { _dirty = true; RebuildLog(); } };
            _monitorTab.Click += (_, __) => ShowPlotter(false);
            _plotterTab.Click += (_, __) => ShowPlotter(true);
            _timestamps.Click += (_, __) => { };
            _input.KeyDown += OnInputKeyDown;
            Loaded += (_, __) => OnLoaded();
        }

        private void OnLoaded()
        {
            if (!string.IsNullOrEmpty(SerialMonitorHandoff.PreferredPort))
            {
                Port = SerialMonitorHandoff.PreferredPort!;
                SerialMonitorHandoff.PreferredPort = null;
            }

            _ = RefreshPortsAsync();
        }

        // ------------------------------------------------------------------ connection

        public void Connect()
        {
            if (IsConnected)
                return;

            var port = Port;
            if (port.Length == 0)
            {
                SetStatus("Pick a port first.");
                return;
            }

            if (!HostClient.IsAvailable)
            {
                SetStatus("The CodeBridge helper is missing from the extension. Reinstall CodeBridge Visual Studio Tools.");
                return;
            }

            _stopping = false;
            var baud = _baud.SelectedItem is int rate ? rate : 115200;
            var newline = new[] { "none", "lf", "cr", "crlf" }[Math.Max(0, _newline.SelectedIndex)];
            SetStatus($"Connecting to {port}...");

            try
            {
                _host = HostClient.Start(
                    $"monitor --port {HostClient.Quote(port)} --baud {baud} --newline {newline}",
                    message => Dispatcher.BeginInvoke(new Action(() => OnHostMessage(message))),
                    (code, error) => Dispatcher.BeginInvoke(new Action(() => OnHostExit(code, error))));
            }
            catch (Exception ex)
            {
                _host = null;
                SetStatus(ex.Message);
                return;
            }

            _connectedPort = port;
            SerialPortLease.Acquire(port, () => Dispatcher.Invoke(new Action(Disconnect)));
            UpdateButtons();
        }

        public void Disconnect()
        {
            _stopping = true;
            _host?.Stop();
        }

        private void OnHostMessage(HostMessage message)
        {
            switch (message.Type)
            {
                case "connected":
                    SetStatus($"Connected to {message.Str("port")} at {message.Str("baud")} baud.");
                    break;

                case "lines":
                    foreach (var line in message.StringList("lines"))
                        AddLine(line);
                    break;

                case "error":
                    SetStatus(message.Str("message") ?? "Error");
                    AddLine("[CodeBridge] " + message.Str("message"));
                    break;
            }
        }

        private void OnHostExit(int code, string error)
        {
            if (_connectedPort != null)
                SerialPortLease.Forget(_connectedPort);

            var friendly = code != 0 && !_stopping && !string.IsNullOrWhiteSpace(error);
            _host = null;
            _connectedPort = null;
            UpdateButtons();

            if (friendly)
                SetStatus(HostClient.Friendly(error));
            else if (_status.Text.StartsWith("Connect", StringComparison.Ordinal) || _stopping)
                SetStatus("Disconnected.");
        }

        // ------------------------------------------------------------------ data

        /// <summary>Takes one line from the board (also used by tests).</summary>
        internal void AddLine(string line)
        {
            var stamped = _timestamps.IsChecked == true ? DateTime.Now.ToString("[HH:mm:ss.fff] ") + line : line;
            _buffer.Add(stamped);
            _plotterData.Add(PlotParser.Parse(line));
            _dirty = true;
        }

        private PlotData _plotterData => _plotter.Data;

        private void Flush()
        {
            if (!_dirty || _pause.IsChecked == true)
                return;

            _dirty = false;
            RebuildLog();
            if (PlotterVisible)
                _plotter.Render();
        }

        private void RebuildLog()
        {
            if (PlotterVisible)
                return;

            _log.Text = _buffer.Text;
            if (_autoScroll.IsChecked == true)
                _log.ScrollToEnd();
        }

        private void ShowPlotter(bool plotter)
        {
            _monitorTab.IsChecked = !plotter;
            _plotterTab.IsChecked = plotter;
            _plotter.Visibility = plotter ? Visibility.Visible : Visibility.Collapsed;
            _log.Visibility = plotter ? Visibility.Collapsed : Visibility.Visible;
            if (plotter)
                _plotter.Render();
            else
                RebuildLog();
        }

        internal void ShowPlotterForTests(bool plotter) => ShowPlotter(plotter);

        private void ClearAll()
        {
            _buffer.Clear();
            _plotter.Clear();
            _log.Clear();
            _dirty = false;
        }

        private void Save()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Text file (*.txt)|*.txt|CSV (*.csv)|*.csv", FileName = $"serial-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
            if (dialog.ShowDialog() != true)
                return;

            try
            {
                File.WriteAllText(dialog.FileName, _buffer.Text);
                SetStatus("Saved " + dialog.FileName);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                SetStatus("Could not save: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ sending

        private void SendInput()
        {
            var text = _input.Text;
            if (text.Length == 0 && _newline.SelectedIndex == 0)
                return;

            if (_host == null)
            {
                SetStatus("Connect first.");
                return;
            }

            _host.SendLine(">" + text);
            if (text.Length > 0 && (_history.Count == 0 || _history[_history.Count - 1] != text))
                _history.Add(text);

            _historyIndex = _history.Count;
            _input.Clear();
        }

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SendInput();
                e.Handled = true;
            }
            else if (e.Key == Key.Up && _history.Count > 0)
            {
                _historyIndex = Math.Max(0, _historyIndex - 1);
                _input.Text = _history[_historyIndex];
                _input.CaretIndex = _input.Text.Length;
                e.Handled = true;
            }
            else if (e.Key == Key.Down && _history.Count > 0)
            {
                _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
                _input.Text = _historyIndex >= _history.Count ? string.Empty : _history[_historyIndex];
                _input.CaretIndex = _input.Text.Length;
                e.Handled = true;
            }
        }

        // ------------------------------------------------------------------ ports

        private async System.Threading.Tasks.Task RefreshPortsAsync()
        {
            if (!HostClient.IsAvailable)
                return;

            var messages = await HostClient.QueryAsync("ports");
            var ports = messages.FirstOrDefault(m => m.Type == "ports")?.List("ports").Select(p => p.Str("name") ?? string.Empty).Where(n => n.Length > 0).ToList();
            if (ports == null)
                return;

            await Dispatcher.InvokeAsync(() =>
            {
                var current = Port;
                _port.ItemsSource = ports;
                if (current.Length > 0)
                    _port.Text = current;
                else if (ports.Count > 0)
                    _port.Text = ports.FirstOrDefault(p => p.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && p != "COM1") ?? ports[0];
            });
        }

        // ------------------------------------------------------------------ small things

        private void SetStatus(string text) => _status.Text = text;

        private void UpdateButtons()
        {
            _connect.Content = IsConnected ? "Disconnect" : "Connect";
            _port.IsEnabled = _baud.IsEnabled = !IsConnected;
            _reset.IsEnabled = IsConnected && !string.Equals(_connectedPort, "Simulator", StringComparison.OrdinalIgnoreCase);
            _send.IsEnabled = IsConnected;
        }

        /// <summary>Stops the host process (the tool window is closing).</summary>
        public void Shutdown()
        {
            _redraw.Stop();
            _stopping = true;
            _host?.Kill();
            if (_connectedPort != null)
                SerialPortLease.Forget(_connectedPort);
        }
    }

    /// <summary>Lets the flow editor or a command suggest the port the monitor should open.</summary>
    internal static class SerialMonitorHandoff
    {
        public static string? PreferredPort { get; set; }
    }
}
