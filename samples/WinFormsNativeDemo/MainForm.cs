using CodeBridge.Core;
using CodeBridge.Designer.WinForms;
using CodeBridge.Flow;
using CodeBridge.Flow.Execution;
using CodeBridge.Transport;

namespace CodeBridge.Samples.WinFormsNativeDemo;

public sealed class MainForm : Form
{
    private readonly CodeBridgeBoardComponent _board = new();
    private readonly CodeBridgeFlowControl _flow = new();
    private readonly ComboBox _portInput = new();
    private readonly NumericUpDown _baudRateInput = new();
    private readonly ComboBox _pinInput = new();
    private readonly ComboBox _modeInput = new();
    private readonly NumericUpDown _loopIntervalInput = new();
    private readonly NumericUpDown _traceDelayInput = new();
    private readonly Button _refreshButton = new();
    private readonly Button _connectButton = new();
    private readonly Button _disconnectButton = new();
    private readonly Button _editButton = new();
    private readonly Button _presetButton = new();
    private readonly Button _blinkTemplateButton = new();
    private readonly Button _runButton = new();
    private readonly Button _stopButton = new();
    private readonly TextBox _log = new();
    private readonly StatusBadge _status = new();
    private bool _closing;

    public MainForm()
    {
        Text = "CodeBridge WinForms Native Demo";
        MinimumSize = new Size(920, 560);
        Size = new Size(1080, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        BuildLayout();
        PopulatePins();
        WireEvents();
        LoadDefaultFlow();
        RefreshPorts();
        UpdateState();
    }

    // The colours are the CodeBridge light scheme shared with the Blazor cards and the MAUI sample.
    private static readonly Color PageColor = Color.FromArgb(238, 241, 245);
    private static readonly Color CardColor = Color.White;
    private static readonly Color LineColor = Color.FromArgb(214, 220, 228);
    private static readonly Color InkColor = Color.FromArgb(21, 26, 33);
    private static readonly Color MutedColor = Color.FromArgb(86, 97, 112);
    private static readonly Color AccentColor = Color.FromArgb(10, 95, 194);
    private static readonly Color SunkenColor = Color.FromArgb(243, 245, 248);

    private void BuildLayout()
    {
        BackColor = PageColor;
        ForeColor = InkColor;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(16),
            BackColor = PageColor
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _portInput.DropDownStyle = ComboBoxStyle.DropDown;
        _baudRateInput.Minimum = 9600;
        _baudRateInput.Maximum = 921600;
        _baudRateInput.Increment = 9600;
        _baudRateInput.Value = 115200;

        _pinInput.DropDownStyle = ComboBoxStyle.DropDownList;

        _modeInput.DropDownStyle = ComboBoxStyle.DropDownList;
        _modeInput.Items.AddRange(Enum.GetNames<FlowExecutionMode>());
        _modeInput.SelectedItem = FlowExecutionMode.Trigger.ToString();

        _loopIntervalInput.Minimum = 100;
        _loopIntervalInput.Maximum = 60000;
        _loopIntervalInput.Increment = 100;
        _loopIntervalInput.Value = 1000;

        _traceDelayInput.Minimum = 0;
        _traceDelayInput.Maximum = 5000;
        _traceDelayInput.Increment = 50;
        _traceDelayInput.Value = 120;

        ConfigureButton(_refreshButton, "Refresh");
        ConfigureButton(_connectButton, "Connect", primary: true);
        ConfigureButton(_disconnectButton, "Disconnect");
        ConfigureButton(_editButton, "Edit flow");
        ConfigureButton(_presetButton, "Preset...");
        ConfigureButton(_blinkTemplateButton, "Load blink");
        ConfigureButton(_runButton, "Run", primary: true);
        ConfigureButton(_stopButton, "Stop");

        var connection = new Card("Board connection");
        connection.AddRow("State", _status);
        connection.AddRow("Port", _portInput);
        connection.AddRow("Baud", _baudRateInput);
        connection.AddButtons(_refreshButton, _connectButton, _disconnectButton);

        var flow = new Card("Flow");
        flow.AddRow("GPIO", _pinInput);
        flow.AddRow("Mode", _modeInput);
        flow.AddRow("Loop (ms)", _loopIntervalInput);
        flow.AddRow("Trace (ms)", _traceDelayInput);
        flow.AddButtons(_editButton, _presetButton, _blinkTemplateButton);

        var run = new Card("Run");
        run.AddButtons(_runButton, _stopButton);

        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = PageColor,
            Padding = new Padding(0, 0, 14, 0)
        };
        foreach (var card in new[] { connection, flow, run })
        {
            card.Width = 314;
            card.FitHeight();
            card.Margin = new Padding(0, 0, 0, 12);
            left.Controls.Add(card);
        }

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = PageColor
        };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _flow.Dock = DockStyle.Fill;
        _flow.Margin = new Padding(0);

        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Font = new Font("Consolas", 9F);
        _log.BackColor = Color.FromArgb(11, 15, 20);
        _log.ForeColor = Color.FromArgb(231, 237, 244);
        _log.BorderStyle = BorderStyle.None;

        right.Controls.Add(_flow, 0, 0);
        right.Controls.Add(_log, 0, 2);

        root.Controls.Add(left, 0, 0);
        root.Controls.Add(right, 1, 0);
        Controls.Add(root);
    }

    /// <summary>A white panel with a hairline border, a title and label/field rows.</summary>
    private sealed class Card : Panel
    {
        private readonly TableLayoutPanel _rows = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            BackColor = CardColor
        };

        private int _next = 1;

        public Card(string title)
        {
            BackColor = CardColor;
            Padding = new Padding(14, 10, 14, 12);
            _rows.RowCount = 1;
            _rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _rows.Controls.Add(
                new Label
                {
                    Text = title,
                    AutoSize = true,
                    Font = new Font("Segoe UI Semibold", 9.5F),
                    ForeColor = InkColor,
                    Margin = new Padding(0, 0, 0, 8)
                },
                0,
                0);
            _rows.SetColumnSpan(_rows.GetControlFromPosition(0, 0)!, 2);
            Controls.Add(_rows);
        }

        public void AddRow(string label, Control control)
        {
            var row = _next++;
            _rows.RowCount = _next;
            _rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 3, 0, 3);
            _rows.Controls.Add(
                new Label
                {
                    Text = label,
                    AutoSize = false,
                    Dock = DockStyle.Fill,
                    Height = 26,
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = MutedColor,
                    Margin = new Padding(0, 3, 0, 3)
                },
                0,
                row);
            _rows.Controls.Add(control, 1, row);
        }

        public void AddButtons(params Button[] buttons)
        {
            var strip = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = buttons.Length,
                RowCount = 1,
                BackColor = CardColor,
                Margin = new Padding(0, 8, 0, 0)
            };
            for (var i = 0; i < buttons.Length; i++)
            {
                strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / buttons.Length));
                buttons[i].Margin = new Padding(i == 0 ? 0 : 3, 0, i == buttons.Length - 1 ? 0 : 3, 0);
                strip.Controls.Add(buttons[i], i, 0);
            }

            var row = _next++;
            _rows.RowCount = _next;
            _rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _rows.Controls.Add(strip, 0, row);
            _rows.SetColumnSpan(strip, 2);
        }

        /// <summary>Sizes the card to its rows; call once the width is set.</summary>
        public void FitHeight()
        {
            var inner = Width - Padding.Horizontal;
            Height = _rows.GetPreferredSize(new Size(inner, 0)).Height + Padding.Vertical;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(LineColor);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    private void WireEvents()
    {
        _refreshButton.Click += (_, _) => RefreshPorts();
        _connectButton.Click += async (_, _) => await ConnectAsync();
        _disconnectButton.Click += async (_, _) => await DisconnectAsync();
        _editButton.Click += (_, _) => _flow.ShowEditor(this);
        _presetButton.Click += (_, _) => LoadPresetFromDialog();
        _blinkTemplateButton.Click += (_, _) => LoadDefaultFlow();
        _runButton.Click += async (_, _) => await RunFlowAsync();
        _stopButton.Click += (_, _) => _flow.Stop();
        _modeInput.SelectedIndexChanged += (_, _) => SyncFlowSettings();
        _loopIntervalInput.ValueChanged += (_, _) => SyncFlowSettings();
        _traceDelayInput.ValueChanged += (_, _) => SyncFlowSettings();

        _board.Connected += (_, _) => AppendLog("ESP32 connected.");
        _board.Disconnected += (_, _) => AppendLog("ESP32 disconnected.");
        _board.ConnectionFailed += (_, ex) =>
        {
            AppendLog("Connection failed: " + ex.Message);
            _status.Set(BadgeState.Faulted, "Connection failed");
        };

        _flow.FlowStarted += (_, _) => AppendLog("Flow started.");
        _flow.FlowCompleted += (_, result) => AppendLog($"Flow completed. Nodes: {result.NodeOutputs.Count}");
        _flow.FlowFailed += (_, ex) => AppendLog("Flow failed: " + ex.Message);
        _flow.FlowTrace += (_, trace) => AppendLog($"{FormatTraceKind(trace.Kind)} {trace.NodeId}");
        _flow.FlowStopped += (_, _) =>
        {
            AppendLog("Flow stopped.");
            UpdateState();
        };
    }

    private void PopulatePins()
    {
        _pinInput.Items.Clear();

        foreach (var option in BuiltInBoardProfiles.Esp32DevKit.DigitalWritePinOptions)
            _pinInput.Items.Add(new PinItem(option));

        SelectPin(2);
    }

    private void RefreshPorts()
    {
        var selected = _portInput.Text;
        _portInput.Items.Clear();

        foreach (var port in BoardDiscovery.DiscoverPorts())
            _portInput.Items.Add(port.Name);

        if (!string.IsNullOrWhiteSpace(selected))
            _portInput.Text = selected;
        else if (_portInput.Items.Count > 0)
            _portInput.SelectedIndex = 0;

        AppendLog(_portInput.Items.Count == 0
            ? "No serial ports detected."
            : $"Detected {_portInput.Items.Count} serial port(s).");
    }

    private async Task ConnectAsync()
    {
        SyncConnectionSettings();
        UpdateState(connecting: true);

        try
        {
            await _board.ConnectAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "CodeBridge connection", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UpdateState();
        }
    }

    private async Task DisconnectAsync()
    {
        _flow.Stop();
        await _board.DisconnectAsync();
        UpdateState();
    }

    private async Task RunFlowAsync()
    {
        SyncConnectionSettings();
        SyncFlowSettings();
        UpdateState(running: true);

        try
        {
            await _board.RunAsync(_flow);
        }
        catch (Exception ex) when (!_closing)
        {
            MessageBox.Show(this, ex.Message, "CodeBridge flow", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UpdateState();
        }
    }

    private void SyncConnectionSettings()
    {
        _board.TransportMode = CodeBridgeTransportMode.Serial;
        _board.PortName = _portInput.Text.Trim();
        _board.BaudRate = (int)_baudRateInput.Value;
    }

    private void SyncFlowSettings()
    {
        if (Enum.TryParse<FlowExecutionMode>(_modeInput.Text, out var mode))
            _flow.ExecutionMode = mode;

        _flow.LoopIntervalMs = (int)_loopIntervalInput.Value;
        _flow.TraceDelayMs = (int)_traceDelayInput.Value;
    }

    private void LoadPresetFromDialog()
    {
        if (_flow.ShowPresetDialog(this))
            AppendLog($"Loaded preset: {_flow.FlowName}.");
    }

    private void LoadDefaultFlow()
    {
        var pin = SelectedPin;
        _flow.FlowName = "ESP32 Blink Flow";
        _flow.LoadBlinkPreset(pin, delayMs: 500);
        AppendLog($"Loaded blink flow for GPIO {pin}.");
    }

    private void UpdateState(bool connecting = false, bool running = false)
    {
        var isRunning = running || _flow.IsRunning;
        var isConnected = _board.IsConnected;

        if (connecting)
            _status.Set(BadgeState.Connecting, "Connecting");
        else if (isRunning)
            _status.Set(BadgeState.Connected, "Running a flow");
        else if (isConnected)
            _status.Set(BadgeState.Connected, "Connected");
        else if (_status.State != BadgeState.Faulted)
            _status.Set(BadgeState.Idle, "Not connected");

        // One primary action at a time: Connect until a board is connected, then Run.
        StylePrimary(_connectButton, !isConnected);
        StylePrimary(_runButton, isConnected);

        _connectButton.Enabled = !connecting && !isConnected && !isRunning;
        _disconnectButton.Enabled = isConnected && !isRunning;
        _runButton.Enabled = !connecting && !isRunning;
        _stopButton.Enabled = isRunning;
        _refreshButton.Enabled = !connecting && !isRunning;
        _portInput.Enabled = !isConnected && !isRunning;
        _baudRateInput.Enabled = !isConnected && !isRunning;
        _modeInput.Enabled = !isRunning;
        _loopIntervalInput.Enabled = !isRunning;
        _traceDelayInput.Enabled = !isRunning;
        _pinInput.Enabled = !isRunning;
        _editButton.Enabled = !isRunning;
        _presetButton.Enabled = !isRunning;
        _blinkTemplateButton.Enabled = !isRunning;
    }

    private void SelectPin(int pin)
    {
        for (var index = 0; index < _pinInput.Items.Count; index++)
        {
            if (_pinInput.Items[index] is PinItem item && item.Number == pin)
            {
                _pinInput.SelectedIndex = index;
                return;
            }
        }

        if (_pinInput.Items.Count > 0)
            _pinInput.SelectedIndex = 0;
    }

    private int SelectedPin => _pinInput.SelectedItem is PinItem item ? item.Number : 2;

    private static string FormatTraceKind(FlowExecutionEventKind kind)
    {
        return kind switch
        {
            FlowExecutionEventKind.NodeStarted => "started",
            FlowExecutionEventKind.NodeCompleted => "completed",
            FlowExecutionEventKind.NodeSkipped => "skipped",
            FlowExecutionEventKind.NodeFailed => "failed",
            _ => kind.ToString()
        };
    }

    private static void StylePrimary(Button button, bool primary)
    {
        button.FlatAppearance.BorderColor = primary ? AccentColor : LineColor;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(8, 78, 160) : Color.FromArgb(230, 235, 241);
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(6, 62, 130) : Color.FromArgb(214, 221, 230);
        button.BackColor = primary ? AccentColor : SunkenColor;
        button.ForeColor = primary ? Color.White : InkColor;
    }

    private enum BadgeState
    {
        Idle,
        Connecting,
        Connected,
        Faulted
    }

    /// <summary>A coloured dot and the state in words: colour is never the only signal.</summary>
    private sealed class StatusBadge : Control
    {
        private string _text = "Not connected";

        public StatusBadge()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 26;
            BackColor = CardColor;
            TabStop = false;
        }

        public BadgeState State { get; private set; }

        public void Set(BadgeState state, string text)
        {
            State = state;
            _text = text;
            AccessibleName = "Connection state";
            AccessibleDescription = text;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var color = State switch
            {
                BadgeState.Connected => Color.FromArgb(15, 122, 85),
                BadgeState.Connecting => AccentColor,
                BadgeState.Faulted => Color.FromArgb(196, 36, 59),
                _ => Color.FromArgb(138, 150, 166)
            };
            using var brush = new SolidBrush(color);
            var dot = new Rectangle(2, (Height - 10) / 2, 10, 10);
            e.Graphics.FillEllipse(brush, dot);
            var textColor = State == BadgeState.Idle || State == BadgeState.Connecting ? MutedColor : color;
            TextRenderer.DrawText(e.Graphics, _text, Font, new Rectangle(20, 0, Width - 20, Height), textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    private static void ConfigureButton(Button button, string text, bool primary = false)
    {
        button.Text = text;
        button.Height = 32;
        button.Dock = DockStyle.Fill;
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = primary ? AccentColor : LineColor;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(8, 78, 160) : Color.FromArgb(230, 235, 241);
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(6, 62, 130) : Color.FromArgb(214, 221, 230);
        button.BackColor = primary ? AccentColor : SunkenColor;
        button.ForeColor = primary ? Color.White : InkColor;
        button.Cursor = Cursors.Hand;
    }

    private void AppendLog(string message)
    {
        if (IsDisposed)
            return;

        if (InvokeRequired)
        {
            BeginInvoke((MethodInvoker)(() => AppendLog(message)));
            return;
        }

        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        _flow.Stop();
        await _board.DisposeAsync();
        base.OnFormClosing(e);
    }

    private sealed class PinItem
    {
        public PinItem(FlowPropertyOption option)
        {
            Label = option.Label;
            Number = Convert.ToInt32(option.Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        public string Label { get; }
        public int Number { get; }

        public override string ToString() => Label;
    }
}
