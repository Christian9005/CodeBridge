using System.ComponentModel;
using System.Globalization;
using System.Drawing.Drawing2D;
using System.Text.Json;
using CodeBridge.Core.Abstractions;
using CodeBridge.Core.Abstractions.Acquisition;
using CodeBridge.Core.Enums;
using CodeBridge.Designer.WinForms.Canvas;
using CodeBridge.Designer.WinForms.Hardware;
using CodeBridge.Flow;
using CodeBridge.Flow.Execution;
using CodeBridge.Flow.Serialization;
using CodeBridge.Flow.Validation;
using CodeBridge.Transport;

namespace CodeBridge.Designer.WinForms;

public enum FlowExecutionMode
{
    Trigger,
    Loop
}

[ToolboxItem(false)]
[DesignTimeVisible(false)]
[EditorBrowsable(EditorBrowsableState.Never)]
[DesignerCategory("Code")]
[DefaultProperty(nameof(ExecutionMode))]
public sealed class CodeBridgeFlowDesignerControl : UserControl
{
    private FlowBlockCatalog _catalog = BuiltInBlockCatalog.Create();
    private readonly FlowValidator _validator = new();
    private readonly FlowCanvasControl _canvas;
    private readonly TreeView _toolbox = new();
    private readonly TextBox _toolboxSearch = new();
    private readonly DataGridView _parameterGrid = new();
    private readonly TextBox _messages = new();
    private readonly Label _selectedTitle = new();
    private readonly Label _selectedSubtitle = new();
    private readonly ComboBox _transportSelector = new();
    private readonly ComboBox _portSelector = new();
    private readonly ToolStripComboBox _boardSelector = new();
    private readonly ComboBox _executionModeSelector = new();
    private readonly NumericUpDown _baudRateInput = new();
    private readonly TextBox _wifiHostInput = new();
    private readonly NumericUpDown _wifiPortInput = new();
    private readonly NumericUpDown _traceDelayInput = new();
    private readonly NumericUpDown _loopIntervalInput = new();
    private readonly ComboBox _probePinSelector = new();
    private readonly Button _refreshPortsButton = new();
    private readonly Button _connectButton = new();
    private readonly Button _disconnectButton = new();
    private readonly Button _uploadFirmwareButton = new();
    private readonly Button _probeHighButton = new();
    private readonly Button _probeLowButton = new();
    private readonly Button _probeReadButton = new();
    private readonly Label _connectionState = new();
    private readonly ToolStripLabel _hardwareStatus = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ToolTip _toolTip = new();
    private readonly ToolStripButton _runButton;
    private readonly ToolStripButton _stopButton;
    private SplitContainer? _outerSplit;
    private SplitContainer? _innerSplit;
    private bool _layoutRestored;

    private IBoard? _connectedBoard;
    private bool _usingExternalBoard;
    private string? _externalBoardLabel;
    private CancellationTokenSource? _runCancellation;
    private string? _currentFile;
    private FlowExecutionMode _executionMode = FlowExecutionMode.Trigger;
    private bool _isDirty;
    private bool _isConnecting;
    private bool _isUploadingFirmware;
    private bool _isRunning;
    private bool _updatingInspector;
    private bool _syncingBoardSelector;
    private bool _syncingExecutionMode;
    private bool _suppressDocumentChanged;

    public CodeBridgeFlowDesignerControl()
    {
        _canvas = new FlowCanvasControl(_catalog);
        _runButton = CreateButton("Run", ToolbarGlyph.Run, async (_, _) => await RunFlowAsync());
        _stopButton = CreateButton("Stop", ToolbarGlyph.Stop, (_, _) => StopRun());

        Text = "Untitled - CodeBridge Designer";
        MinimumSize = new Size(900, 560);
        Size = new Size(1100, 720);
        BackColor = DesignerTheme.Workbench;
        Font = DesignerTheme.UiFont;

        BuildLayout();
        PopulateToolbox();
        PopulateProbePins();

        if (!IsInDesignMode)
            RefreshPorts();

        UpdateConnectionControls();
        WireEvents();
        UpdateInspector();
        UpdateRunControls();
        UpdateTitle();
    }

    public void RestoreDesignerLayout()
    {
        ApplyPreferredSplitterLayout();
        _layoutRestored = true;
    }

    public void FitDocumentToView() => _canvas.FitDocumentToView();

    public void ZoomIn() => _canvas.ZoomIn();

    public void ZoomOut() => _canvas.ZoomOut();

    public void ResetZoom() => _canvas.ResetZoom();

    public void UseExternalBoard(IBoard board, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(board);

        _connectedBoard = board;
        _usingExternalBoard = true;
        _externalBoardLabel = string.IsNullOrWhiteSpace(label) ? board.Name : label;
        _messages.Text = $"Using {_externalBoardLabel}. Connection is owned by the host form.";
        SetStatus("Using host board connection", DesignerTheme.Signal);
        UpdateConnectionControls();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        if (_layoutRestored)
            return;

        BeginInvoke((MethodInvoker)(() =>
        {
            RestoreDesignerLayout();
            FitDocumentToView();
        }));
    }

    [Category("CodeBridge")]
    [DefaultValue(FlowExecutionMode.Trigger)]
    [Description("Runs once from triggers, or repeats the flow continuously until stopped.")]
    public FlowExecutionMode ExecutionMode
    {
        get => _executionMode;
        set
        {
            if (_executionMode == value)
                return;

            _executionMode = value;
            SyncExecutionModeSelector();
        }
    }

    [Category("CodeBridge")]
    [DefaultValue(1000)]
    [Description("Delay between loop iterations when ExecutionMode is Loop.")]
    public int LoopIntervalMs
    {
        get => (int)_loopIntervalInput.Value;
        set => _loopIntervalInput.Value = Math.Clamp(value, (int)_loopIntervalInput.Minimum, (int)_loopIntervalInput.Maximum);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public FlowDocument Document
    {
        get => _canvas.Document;
        set
        {
            _suppressDocumentChanged = true;
            try
            {
                _canvas.SetDocument(value);
                RebuildCatalogForDocumentBoard(updateSelector: true);
                _canvas.FitDocumentToView();
                _isDirty = false;
                UpdateTitle();
            }
            finally
            {
                _suppressDocumentChanged = false;
            }
        }
    }

    private bool IsInDesignMode =>
        LicenseManager.UsageMode == LicenseUsageMode.Designtime || DesignMode;

    private void BuildLayout()
    {
        SuspendLayout();

        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            BackColor = DesignerTheme.Workbench
        };
        // One fixed-width column: without it the column grows to the toolbar's full width and pushes the inspector off the window.
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

        shell.Controls.Add(BuildToolStrip(), 0, 0);
        shell.Controls.Add(BuildMainSplit(), 0, 1);
        shell.Controls.Add(BuildStatusStrip(), 0, 2);

        Controls.Add(shell);
        ResumeLayout(false);
    }

    private ToolStrip BuildToolStrip()
    {
        var strip = new ToolStrip
        {
            Dock = DockStyle.Fill,
            CanOverflow = true,
            GripStyle = ToolStripGripStyle.Hidden,
            BackColor = DesignerTheme.Surface,
            ForeColor = DesignerTheme.Text,
            Padding = new Padding(8, 4, 8, 4),
            Renderer = new CodeBridgeToolStripRenderer()
        };

        strip.Items.Add(CreateButton("New", ToolbarGlyph.New, (_, _) => NewFlow()));
        strip.Items.Add(CreateButton("Open", ToolbarGlyph.Open, (_, _) => OpenFlow()));
        strip.Items.Add(CreateButton("Save", ToolbarGlyph.Save, (_, _) => SaveFlow()));
        strip.Items.Add(new ToolStripSeparator());
        strip.Items.Add(CreateButton("Blink", ToolbarGlyph.Blink, (_, _) => LoadBlinkTestFlow(activeLow: false)));
        strip.Items.Add(CreateButton("Blink Low", ToolbarGlyph.BlinkLow, (_, _) => LoadBlinkTestFlow(activeLow: true)));
        strip.Items.Add(CreateButton("Read", ToolbarGlyph.Read, (_, _) => LoadReadDebugFlow()));
        strip.Items.Add(CreateButton("Stream", ToolbarGlyph.Stream, (_, _) => LoadStreamDashboardFlow()));
        strip.Items.Add(new ToolStripSeparator());
        strip.Items.Add(new ToolStripLabel("Board"));
        ConfigureBoardSelector();
        strip.Items.Add(_boardSelector);
        ConfigureHardwareStatus();
        strip.Items.Add(_hardwareStatus);
        strip.Items.Add(new ToolStripSeparator());
        strip.Items.Add(CreateButton("Zoom -", ToolbarGlyph.ZoomOut, (_, _) => ZoomOut()));
        strip.Items.Add(CreateButton("100%", ToolbarGlyph.ZoomReset, (_, _) => ResetZoom()));
        strip.Items.Add(CreateButton("Zoom +", ToolbarGlyph.ZoomIn, (_, _) => ZoomIn()));
        strip.Items.Add(CreateButton("Fit", ToolbarGlyph.Fit, (_, _) => FitDocumentToView()));
        strip.Items.Add(CreateButton("Arrange", ToolbarGlyph.Arrange, (_, _) => ArrangeFlow()));
        strip.Items.Add(new ToolStripSeparator());
        strip.Items.Add(CreateButton("Validate", ToolbarGlyph.Validate, (_, _) => ValidateFlow()));
        strip.Items.Add(_runButton);
        strip.Items.Add(_stopButton);
        strip.Items.Add(new ToolStripSeparator());
        strip.Items.Add(CreateButton("Configure", ToolbarGlyph.Configure, (_, _) => ConfigureSelectedNode()));
        strip.Items.Add(CreateButton("Delete", ToolbarGlyph.Delete, (_, _) => _canvas.DeleteSelectedNode()));

        return strip;
    }

    private SplitContainer BuildMainSplit()
    {
        var outer = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            SplitterWidth = 5,
            BackColor = DesignerTheme.Workbench
        };
        _outerSplit = outer;

        outer.Panel1.Controls.Add(BuildToolboxPanel());

        var inner = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel2,
            SplitterWidth = 5,
            BackColor = DesignerTheme.Workbench
        };
        _innerSplit = inner;

        _canvas.Dock = DockStyle.Fill;
        inner.Panel1.Controls.Add(_canvas);
        inner.Panel2.Controls.Add(BuildInspectorPanel());
        outer.Panel2.Controls.Add(inner);
        return outer;
    }

    private void ApplyPreferredSplitterLayout()
    {
        const int toolboxMin = 250;
        const int workbenchMin = 680;
        const int canvasMin = 460;
        const int inspectorMin = 360;

        if (_outerSplit is { IsDisposed: false })
        {
            var available = _outerSplit.Width - _outerSplit.SplitterWidth;
            if (available > toolboxMin + workbenchMin)
                SetSplitterDistance(_outerSplit, Math.Min(300, Math.Max(268, available / 4)), toolboxMin, workbenchMin);
        }

        if (_innerSplit is { IsDisposed: false })
        {
            var available = _innerSplit.Width - _innerSplit.SplitterWidth;
            if (available > canvasMin + inspectorMin)
            {
                var inspectorWidth = Math.Min(400, Math.Max(360, available / 3));
                SetSplitterDistance(_innerSplit, available - inspectorWidth, canvasMin, inspectorMin);
            }
        }
    }

    private static void SetSplitterDistance(SplitContainer split, int distance, int panel1Min, int panel2Min)
    {
        var maxDistance = split.Width - split.SplitterWidth - panel2Min;
        if (maxDistance < panel1Min)
            return;

        split.SplitterDistance = Math.Clamp(distance, panel1Min, maxDistance);
    }

    private Control BuildToolboxPanel()
    {
        var panel = CreatePanel();
        panel.Padding = new Padding(10, 8, 10, 10);

        var title = CreateSectionTitle("Toolbox");
        ConfigureToolboxSearch();
        var hint = CreateHint("Drag blocks to the canvas or double-click to add.");
        var presetTitle = CreateSectionTitle("Quick Add");
        var presets = BuildPresetPanel();

        _toolbox.Dock = DockStyle.Fill;
        _toolbox.BackColor = DesignerTheme.Surface;
        _toolbox.ForeColor = DesignerTheme.Text;
        _toolbox.BorderStyle = BorderStyle.None;
        _toolbox.HideSelection = false;
        _toolbox.ShowNodeToolTips = true;
        _toolbox.ItemHeight = 24;

        panel.Controls.Add(_toolbox);
        panel.Controls.Add(hint);
        panel.Controls.Add(presets);
        panel.Controls.Add(presetTitle);
        panel.Controls.Add(_toolboxSearch);
        panel.Controls.Add(title);
        return panel;
    }

    private Control BuildPresetPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 136,
            ColumnCount = 2,
            RowCount = 4,
            BackColor = DesignerTheme.Surface,
            Padding = new Padding(0, 0, 0, 10)
        };

        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        panel.Controls.Add(CreatePresetButton("Trigger", "Add a manual trigger block.", AddManualTriggerPreset), 0, 0);
        panel.Controls.Add(CreatePresetButton("Delay", "Add a timer block.", AddDelayPreset), 1, 0);
        panel.Controls.Add(CreatePresetButton("GPIO Out", "Add a single digital output action.", AddGpioOutputPreset), 0, 1);
        panel.Controls.Add(CreatePresetButton("Read DBG", "Add pin mode, digital read, and debug output.", AddReadDebugPreset), 1, 1);
        panel.Controls.Add(CreatePresetButton("Compare", "Add two number blocks and a compare block.", AddComparePreset), 0, 2);
        panel.Controls.Add(CreatePresetButton("Debug", "Add a debug output block.", AddDebugPreset), 1, 2);
        panel.Controls.Add(CreatePresetButton("Sample", "Add analog sample channel, dashboard stream, and debug output.", AddSampleDashboardPreset), 0, 3);
        return panel;
    }

    private Control BuildInspectorPanel()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 5,
            BackColor = DesignerTheme.Workbench,
            Panel1MinSize = 340,
            Panel2MinSize = 120
        };

        var panel = CreatePanel();
        panel.Padding = new Padding(10);
        panel.AutoScroll = true;

        var outputPanel = CreatePanel();
        outputPanel.Padding = new Padding(10);

        _selectedTitle.Dock = DockStyle.Top;
        _selectedTitle.Height = 24;
        _selectedTitle.ForeColor = DesignerTheme.Text;
        _selectedTitle.Font = DesignerTheme.TitleFont;
        _selectedTitle.Text = "No block selected";

        _selectedSubtitle.Dock = DockStyle.Top;
        _selectedSubtitle.Height = 38;
        _selectedSubtitle.ForeColor = DesignerTheme.MutedText;
        _selectedSubtitle.Font = DesignerTheme.SmallFont;

        var propertyTitle = CreateSectionTitle("Parameters");
        var connectionTitle = CreateSectionTitle("Connection");
        var logTitle = CreateSectionTitle("Output");

        _parameterGrid.Dock = DockStyle.Top;
        _parameterGrid.Height = 150;
        _parameterGrid.BackgroundColor = DesignerTheme.SurfaceSunken;
        _parameterGrid.BorderStyle = BorderStyle.None;
        _parameterGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _parameterGrid.ColumnHeadersHeight = 28;
        _parameterGrid.RowHeadersVisible = false;
        _parameterGrid.AllowUserToAddRows = false;
        _parameterGrid.AllowUserToDeleteRows = false;
        _parameterGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _parameterGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _parameterGrid.EnableHeadersVisualStyles = false;
        _parameterGrid.GridColor = DesignerTheme.BorderSubtle;
        _parameterGrid.DefaultCellStyle.BackColor = DesignerTheme.InputBackground;
        _parameterGrid.DefaultCellStyle.ForeColor = DesignerTheme.Text;
        _parameterGrid.DefaultCellStyle.SelectionBackColor = DesignerTheme.SurfaceSelected;
        _parameterGrid.DefaultCellStyle.SelectionForeColor = DesignerTheme.Text;
        _parameterGrid.ColumnHeadersDefaultCellStyle.BackColor = DesignerTheme.ButtonBackground;
        _parameterGrid.ColumnHeadersDefaultCellStyle.ForeColor = DesignerTheme.Text;
        _parameterGrid.Columns.Add("name", "Name");
        _parameterGrid.Columns.Add("value", "Value");
        _parameterGrid.Columns[0].ReadOnly = true;

        _messages.Dock = DockStyle.Fill;
        _messages.BackColor = DesignerTheme.OutputBackground;
        _messages.ForeColor = DesignerTheme.Text;
        _messages.BorderStyle = BorderStyle.None;
        _messages.Multiline = true;
        _messages.ReadOnly = true;
        _messages.ScrollBars = ScrollBars.Vertical;
        _messages.Font = DesignerTheme.MonoFont;
        _messages.Text = "Ready.";

        outputPanel.Controls.Add(_messages);
        outputPanel.Controls.Add(logTitle);
        panel.Controls.Add(_parameterGrid);
        panel.Controls.Add(propertyTitle);
        panel.Controls.Add(BuildConnectionPanel());
        panel.Controls.Add(connectionTitle);
        panel.Controls.Add(_selectedSubtitle);
        panel.Controls.Add(_selectedTitle);

        split.Panel1.Controls.Add(panel);
        split.Panel2.Controls.Add(outputPanel);
        split.SizeChanged += (_, _) =>
        {
            if (split.Height <= split.Panel1MinSize + split.Panel2MinSize + split.SplitterWidth)
                return;

            var desired = Math.Max(split.Panel1MinSize, split.Height - 190);
            var max = split.Height - split.Panel2MinSize - split.SplitterWidth;
            split.SplitterDistance = Math.Min(desired, max);
        };

        return split;
    }

    private Control BuildConnectionPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 322,
            ColumnCount = 2,
            RowCount = 10,
            BackColor = DesignerTheme.Surface,
            Padding = new Padding(0, 0, 0, 8)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 9; i++)
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));

        ConfigureComboBox(_transportSelector);
        _transportSelector.Items.AddRange(["Serial", "WiFi"]);
        _transportSelector.SelectedIndex = 0;

        ConfigureComboBox(_portSelector);
        ConfigureComboBox(_executionModeSelector);
        _executionModeSelector.Items.AddRange(Enum.GetNames<FlowExecutionMode>());
        SyncExecutionModeSelector();

        ConfigureNumeric(_baudRateInput, minimum: 9600, maximum: 921600, value: 115200);
        ConfigureTextBox(_wifiHostInput, "192.168.1.100");
        ConfigureNumeric(_wifiPortInput, minimum: 1, maximum: 65535, value: 8080);
        ConfigureNumeric(_traceDelayInput, minimum: 0, maximum: 2000, value: 180);
        ConfigureNumeric(_loopIntervalInput, minimum: 100, maximum: 60000, value: 1000);
        ConfigureComboBox(_probePinSelector);

        ConfigureButton(_refreshPortsButton, "Refresh");
        ConfigureButton(_connectButton, "Connect");
        ConfigureButton(_disconnectButton, "Disconnect");
        ConfigureButton(_uploadFirmwareButton, "Upload FW");
        ConfigureButton(_probeHighButton, "HIGH");
        ConfigureButton(_probeLowButton, "LOW");
        ConfigureButton(_probeReadButton, "READ");
        _toolTip.SetToolTip(_connectButton, "Open a CodeBridge connection to the selected board.");
        _toolTip.SetToolTip(_uploadFirmwareButton, "Flash the matching CodeBridge firmware to the selected board and serial port.");
        _toolTip.SetToolTip(_portSelector, "USB serial port. Use Refresh after plugging in a board.");
        _toolTip.SetToolTip(_transportSelector, "Arduino Uno uses Serial. ESP32 can use Serial or WiFi.");

        _connectionState.AutoSize = false;
        _connectionState.Dock = DockStyle.Fill;
        _connectionState.AutoEllipsis = true;
        _connectionState.Margin = new Padding(4, 0, 0, 0);
        _connectionState.TextAlign = ContentAlignment.MiddleLeft;
        _connectionState.ForeColor = DesignerTheme.MutedText;
        _connectionState.Text = "Disconnected";

        panel.Controls.Add(CreateFieldLabel("Mode"), 0, 0);
        panel.Controls.Add(_transportSelector, 1, 0);
        panel.Controls.Add(CreateFieldLabel("Port"), 0, 1);
        panel.Controls.Add(BuildPortRow(), 1, 1);
        panel.Controls.Add(CreateFieldLabel("Baud"), 0, 2);
        panel.Controls.Add(_baudRateInput, 1, 2);
        panel.Controls.Add(CreateFieldLabel("Host"), 0, 3);
        panel.Controls.Add(_wifiHostInput, 1, 3);
        panel.Controls.Add(CreateFieldLabel("TCP"), 0, 4);
        panel.Controls.Add(_wifiPortInput, 1, 4);
        panel.Controls.Add(CreateFieldLabel("Trace"), 0, 5);
        panel.Controls.Add(_traceDelayInput, 1, 5);
        panel.Controls.Add(CreateFieldLabel("Flow"), 0, 6);
        panel.Controls.Add(_executionModeSelector, 1, 6);
        panel.Controls.Add(CreateFieldLabel("Loop"), 0, 7);
        panel.Controls.Add(_loopIntervalInput, 1, 7);
        panel.Controls.Add(CreateFieldLabel("GPIO"), 0, 8);
        panel.Controls.Add(BuildProbeRow(), 1, 8);
        var actions = BuildConnectionActions();
        panel.Controls.Add(actions, 0, 9);
        panel.SetColumnSpan(actions, 2);
        return panel;
    }

    private Control BuildPortRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = DesignerTheme.Surface,
            Margin = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
        row.Controls.Add(_portSelector, 0, 0);
        row.Controls.Add(_refreshPortsButton, 1, 0);
        return row;
    }

    private Control BuildConnectionActions()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = DesignerTheme.Surface,
            Margin = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(_connectButton, 0, 0);
        row.Controls.Add(_disconnectButton, 1, 0);
        row.Controls.Add(_uploadFirmwareButton, 2, 0);
        row.Controls.Add(_connectionState, 3, 0);
        return row;
    }

    private Control BuildProbeRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = DesignerTheme.Surface,
            Margin = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        row.Controls.Add(_probePinSelector, 0, 0);
        row.Controls.Add(_probeHighButton, 1, 0);
        row.Controls.Add(_probeLowButton, 2, 0);
        row.Controls.Add(_probeReadButton, 3, 0);
        return row;
    }

    private StatusStrip BuildStatusStrip()
    {
        var strip = new StatusStrip
        {
            BackColor = DesignerTheme.SurfaceSunken,
            ForeColor = DesignerTheme.MutedText,
            SizingGrip = false
        };

        _statusLabel.Text = "Ready";
        _statusLabel.ForeColor = DesignerTheme.MutedText;
        strip.Items.Add(_statusLabel);
        return strip;
    }

    private static ToolStripButton CreateButton(string text, ToolbarGlyph glyph, EventHandler click)
    {
        var button = new ToolStripButton(text)
        {
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            Image = CreateToolbarIcon(glyph),
            ImageTransparentColor = Color.Transparent,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            AutoToolTip = true,
            ForeColor = DesignerTheme.Text,
            Margin = new Padding(1, 0, 1, 0),
            Padding = new Padding(6, 0, 6, 0)
        };
        button.Click += click;
        return button;
    }

    private static Bitmap CreateToolbarIcon(ToolbarGlyph glyph)
    {
        var color = glyph switch
        {
            ToolbarGlyph.Run or ToolbarGlyph.Validate => DesignerTheme.Signal,
            ToolbarGlyph.Stop or ToolbarGlyph.Delete => DesignerTheme.Error,
            ToolbarGlyph.Blink or ToolbarGlyph.BlinkLow or ToolbarGlyph.Stream => DesignerTheme.Warning,
            ToolbarGlyph.Configure => DesignerTheme.AccentSoft,
            _ => DesignerTheme.MutedText
        };

        var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 1.7F)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var brush = new SolidBrush(color);

        switch (glyph)
        {
            case ToolbarGlyph.New:
                graphics.DrawRectangle(pen, 4, 2, 8, 12);
                graphics.DrawLine(pen, 8, 5, 12, 5);
                graphics.DrawLine(pen, 8, 2, 8, 5);
                break;
            case ToolbarGlyph.Open:
                graphics.DrawLine(pen, 2, 6, 6, 6);
                graphics.DrawLine(pen, 6, 6, 7, 4);
                graphics.DrawLine(pen, 7, 4, 13, 4);
                graphics.DrawRectangle(pen, 2, 7, 12, 8);
                break;
            case ToolbarGlyph.Save:
                graphics.DrawRectangle(pen, 3, 2, 11, 12);
                graphics.DrawLine(pen, 5, 3, 11, 3);
                graphics.DrawRectangle(pen, 6, 9, 7, 5);
                break;
            case ToolbarGlyph.Blink:
            case ToolbarGlyph.BlinkLow:
                graphics.DrawLines(pen, [new PointF(9, 1), new PointF(5, 8), new PointF(9, 8), new PointF(6, 15), new PointF(13, 6), new PointF(9, 6)]);
                if (glyph == ToolbarGlyph.BlinkLow)
                    graphics.DrawLine(pen, 2, 13, 6, 13);
                break;
            case ToolbarGlyph.Read:
                graphics.DrawEllipse(pen, 3, 3, 7, 7);
                graphics.DrawLine(pen, 9, 9, 13, 13);
                break;
            case ToolbarGlyph.Stream:
                graphics.DrawLines(pen, [new PointF(1, 9), new PointF(4, 9), new PointF(6, 4), new PointF(9, 13), new PointF(11, 7), new PointF(15, 7)]);
                break;
            case ToolbarGlyph.ZoomOut:
                DrawMagnifier(graphics, pen);
                graphics.DrawLine(pen, 5, 7, 9, 7);
                break;
            case ToolbarGlyph.ZoomReset:
                graphics.DrawRectangle(pen, 3, 3, 10, 10);
                graphics.DrawLine(pen, 6, 8, 10, 8);
                break;
            case ToolbarGlyph.ZoomIn:
                DrawMagnifier(graphics, pen);
                graphics.DrawLine(pen, 5, 7, 9, 7);
                graphics.DrawLine(pen, 7, 5, 7, 9);
                break;
            case ToolbarGlyph.Fit:
                graphics.DrawLine(pen, 2, 6, 2, 2);
                graphics.DrawLine(pen, 2, 2, 6, 2);
                graphics.DrawLine(pen, 14, 6, 14, 2);
                graphics.DrawLine(pen, 14, 2, 10, 2);
                graphics.DrawLine(pen, 2, 10, 2, 14);
                graphics.DrawLine(pen, 2, 14, 6, 14);
                graphics.DrawLine(pen, 14, 10, 14, 14);
                graphics.DrawLine(pen, 14, 14, 10, 14);
                break;
            case ToolbarGlyph.Arrange:
                graphics.FillEllipse(brush, 2, 3, 4, 4);
                graphics.FillEllipse(brush, 10, 3, 4, 4);
                graphics.FillEllipse(brush, 6, 11, 4, 4);
                graphics.DrawLine(pen, 5, 5, 10, 5);
                graphics.DrawLine(pen, 5, 7, 7, 11);
                graphics.DrawLine(pen, 12, 7, 9, 11);
                break;
            case ToolbarGlyph.Validate:
                graphics.DrawLine(pen, 3, 8, 7, 12);
                graphics.DrawLine(pen, 7, 12, 13, 4);
                break;
            case ToolbarGlyph.Run:
                graphics.FillPolygon(brush, new[] { new Point(5, 3), new Point(13, 8), new Point(5, 13) });
                break;
            case ToolbarGlyph.Stop:
                graphics.FillRectangle(brush, 4, 4, 8, 8);
                break;
            case ToolbarGlyph.Configure:
                graphics.DrawEllipse(pen, 5, 5, 6, 6);
                graphics.DrawLine(pen, 8, 1, 8, 4);
                graphics.DrawLine(pen, 8, 12, 8, 15);
                graphics.DrawLine(pen, 1, 8, 4, 8);
                graphics.DrawLine(pen, 12, 8, 15, 8);
                break;
            case ToolbarGlyph.Delete:
                graphics.DrawLine(pen, 4, 5, 12, 5);
                graphics.DrawRectangle(pen, 5, 6, 7, 9);
                graphics.DrawLine(pen, 6, 3, 10, 3);
                break;
        }

        return bitmap;
    }

    private static void DrawMagnifier(Graphics graphics, Pen pen)
    {
        graphics.DrawEllipse(pen, 3, 3, 8, 8);
        graphics.DrawLine(pen, 10, 10, 14, 14);
    }

    private Button CreatePresetButton(string text, string tooltip, Action click)
    {
        var button = new Button();
        ConfigureButton(button, text);
        button.Height = 28;
        button.Click += (_, _) => click();
        _toolTip.SetToolTip(button, tooltip);
        return button;
    }

    private static Panel CreatePanel() => new()
    {
        Dock = DockStyle.Fill,
        BackColor = DesignerTheme.Surface
    };

    private static Label CreateSectionTitle(string text) => new()
    {
        Dock = DockStyle.Top,
        Height = 30,
        Text = text.ToUpperInvariant(),
        ForeColor = DesignerTheme.MutedText,
        Font = DesignerTheme.SectionFont,
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static Label CreateHint(string text) => new()
    {
        Dock = DockStyle.Top,
        Height = 38,
        Text = text,
        ForeColor = DesignerTheme.MutedText,
        Font = DesignerTheme.SmallFont,
        TextAlign = ContentAlignment.TopLeft
    };

    private void WireEvents()
    {
        _toolbox.ItemDrag += (_, e) =>
        {
            if (e.Item is TreeNode { Tag: string blockType })
                DoDragDrop(blockType, DragDropEffects.Copy);
        };

        _toolbox.NodeMouseDoubleClick += (_, e) =>
        {
            if (e.Node.Tag is string blockType && _catalog.TryGet(blockType, out var definition))
                _canvas.AddNode(definition, _canvas.GetVisibleDocumentCenter());
        };
        _toolboxSearch.TextChanged += (_, _) => PopulateToolbox();

        _canvas.SelectedNodeChanged += (_, _) => UpdateInspector();
        _canvas.NodeDoubleClicked += (_, _) => ConfigureSelectedNode();
        _canvas.DocumentChanged += (_, _) =>
        {
            if (_suppressDocumentChanged)
                return;

            _isDirty = true;
            UpdateTitle();
            SetStatus("Flow changed", DesignerTheme.MutedText);
        };

        _parameterGrid.CellEndEdit += (_, e) => UpdateNodeParameter(e.RowIndex);
        _parameterGrid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == 1 &&
                _parameterGrid.Rows[e.RowIndex].Cells[e.ColumnIndex] is DataGridViewComboBoxCell)
            {
                UpdateNodeParameter(e.RowIndex);
            }
        };
        _parameterGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_parameterGrid.IsCurrentCellDirty)
                _parameterGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _parameterGrid.DataError += (_, e) => e.ThrowException = false;

        _transportSelector.SelectedIndexChanged += (_, _) =>
        {
            if (!EnsureTransportCompatibleWithCurrentBoard(showStatus: true))
                return;

            UpdateConnectionControls();
        };
        _portSelector.SelectedIndexChanged += (_, _) => ApplyBoardSuggestionForSelectedPort();
        _boardSelector.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingBoardSelector)
                return;

            if (_boardSelector.SelectedItem is not BoardProfileItem item)
                return;

            _canvas.Document.BoardId = item.Profile.Id;
            RebuildCatalogForDocumentBoard(updateSelector: false);
            _isDirty = true;
            UpdateTitle();
            SetStatus($"Board: {item.Profile.DisplayName}", DesignerTheme.Signal);
        };
        _executionModeSelector.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingExecutionMode)
                return;

            if (Enum.TryParse<FlowExecutionMode>(_executionModeSelector.Text, out var mode))
                ExecutionMode = mode;
        };
        _loopIntervalInput.ValueChanged += (_, _) => UpdateRunControls();
        _refreshPortsButton.Click += (_, _) => RefreshPorts();
        _connectButton.Click += async (_, _) => await ConnectBoardAsync();
        _disconnectButton.Click += async (_, _) => await DisconnectBoardAsync();
        _uploadFirmwareButton.Click += async (_, _) => await UploadFirmwareAsync();
        _probeHighButton.Click += async (_, _) => await WriteProbePinAsync(true);
        _probeLowButton.Click += async (_, _) => await WriteProbePinAsync(false);
        _probeReadButton.Click += async (_, _) => await ReadProbePinAsync();
    }

    private void PopulateToolbox()
    {
        _toolbox.Nodes.Clear();
        var filter = _toolboxSearch.Text.Trim();

        foreach (var group in _catalog.Blocks
                     .Where(block => MatchesToolboxFilter(block, filter))
                     .OrderBy(block => block.Category)
                     .ThenBy(block => block.DisplayName)
                     .GroupBy(block => block.Category))
        {
            var blocks = group.ToArray();
            var categoryNode = _toolbox.Nodes.Add($"{group.Key}   {blocks.Length}");
            categoryNode.ForeColor = DesignerTheme.Text;

            foreach (var block in blocks)
            {
                var blockNode = categoryNode.Nodes.Add(block.DisplayName);
                blockNode.Tag = block.Type;
                blockNode.ToolTipText = block.Description ?? block.Type;
                blockNode.ForeColor = DesignerTheme.Text;
            }

            categoryNode.Expand();
        }
    }

    private static bool MatchesToolboxFilter(FlowBlockDefinition block, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        return Contains(block.DisplayName, filter) ||
            Contains(block.Category, filter) ||
            Contains(block.Type, filter) ||
            Contains(block.Description, filter);
    }

    private static bool Contains(string? value, string filter) =>
        value?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

    private void ConfigureBoardSelector()
    {
        _boardSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        DesignerTheme.StyleCombo(_boardSelector.ComboBox);
        _boardSelector.AutoSize = false;
        _boardSelector.Width = 156;
        _boardSelector.ToolTipText = "Board profile used for pin options and validation.";
        _boardSelector.Items.Clear();

        foreach (var board in BuiltInBoardProfiles.All)
            _boardSelector.Items.Add(new BoardProfileItem(board));

        SelectBoardInSelector(CurrentBoard);
    }

    private void ConfigureHardwareStatus()
    {
        _hardwareStatus.ForeColor = DesignerTheme.MutedText;
        _hardwareStatus.Margin = new Padding(8, 0, 4, 0);
        _hardwareStatus.Text = "Disconnected";
        _hardwareStatus.ToolTipText = "Current board connection state.";
    }

    private void ConfigureToolboxSearch()
    {
        _toolboxSearch.Dock = DockStyle.Top;
        _toolboxSearch.Height = 28;
        _toolboxSearch.BorderStyle = BorderStyle.FixedSingle;
        _toolboxSearch.BackColor = DesignerTheme.InputBackground;
        _toolboxSearch.ForeColor = DesignerTheme.Text;
        _toolboxSearch.Margin = new Padding(0, 0, 0, 8);
        _toolboxSearch.Text = "";
        _toolboxSearch.PlaceholderText = "Search nodes...";
    }

    private BoardProfile CurrentBoard =>
        BuiltInBoardProfiles.FindById(_canvas.Document.BoardId) ?? BuiltInBoardProfiles.Default;

    private void RebuildCatalogForDocumentBoard(bool updateSelector)
    {
        var board = CurrentBoard;
        _canvas.Document.BoardId = board.Id;
        EnsureTransportCompatibleWithCurrentBoard(showStatus: false);
        _catalog = BuiltInBlockCatalog.Create(board);
        _canvas.SetCatalog(_catalog);

        if (updateSelector)
            SelectBoardInSelector(board);

        PopulateToolbox();
        PopulateProbePins();
        UpdateInspector();
    }

    private bool EnsureTransportCompatibleWithCurrentBoard(bool showStatus)
    {
        if (IsSerialMode() || BoardConnectionService.IsEsp32Profile(CurrentBoard))
            return true;

        _transportSelector.SelectedItem = "Serial";
        if (showStatus)
            SetStatus($"{CurrentBoard.DisplayName} uses USB Serial for CodeBridge", DesignerTheme.Warning);

        UpdateConnectionControls();
        return false;
    }

    private void SelectBoardInSelector(BoardProfile board)
    {
        _syncingBoardSelector = true;
        try
        {
            for (var index = 0; index < _boardSelector.Items.Count; index++)
            {
                if (_boardSelector.Items[index] is BoardProfileItem item &&
                    string.Equals(item.Profile.Id, board.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _boardSelector.SelectedIndex = index;
                    return;
                }
            }

            if (_boardSelector.Items.Count > 0)
                _boardSelector.SelectedIndex = 0;
        }
        finally
        {
            _syncingBoardSelector = false;
        }
    }

    private void UpdateInspector()
    {
        _updatingInspector = true;
        _parameterGrid.Rows.Clear();

        var node = _canvas.SelectedNode;
        if (node is null || !_catalog.TryGet(node.Type, out var definition))
        {
            _selectedTitle.Text = "No block selected";
            _selectedSubtitle.Text = "Select a block to edit its parameters.";
            _updatingInspector = false;
            return;
        }

        _selectedTitle.Text = definition.DisplayName;
        _selectedSubtitle.Text = $"{node.Id}  |  {definition.Type}";

        foreach (var property in definition.Properties)
        {
            var value = node.Parameters.TryGetValue(property.Name, out var currentValue)
                ? currentValue
                : property.DefaultValue;

            var rowIndex = _parameterGrid.Rows.Add(property.Name);
            var row = _parameterGrid.Rows[rowIndex];
            row.Tag = property;
            ConfigurePropertyValueCell(row, property, value);
        }

        _updatingInspector = false;
    }

    private static void ConfigurePropertyValueCell(
        DataGridViewRow row,
        FlowPropertyDefinition property,
        object? value)
    {
        if (property.Options is { Count: > 0 } options)
        {
            var comboCell = new DataGridViewComboBoxCell
            {
                DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox,
                FlatStyle = FlatStyle.Flat,
                ValueType = typeof(string)
            };

            foreach (var option in options)
                comboCell.Items.Add(option.Label);

            var selectedOption = FindOptionForValue(property, value);
            var displayValue = selectedOption?.Label ?? FormatValue(value);
            if (!comboCell.Items.Contains(displayValue))
                comboCell.Items.Add(displayValue);

            comboCell.Value = displayValue;
            row.Cells[1] = comboCell;
            row.Cells[1].ToolTipText = selectedOption?.Description ?? property.Name;
            return;
        }

        row.Cells[1].Value = FormatValue(value);
        row.Cells[1].ToolTipText = property.Name;
    }

    private void UpdateNodeParameter(int rowIndex)
    {
        if (_updatingInspector || rowIndex < 0 || _canvas.SelectedNode is null)
            return;

        var row = _parameterGrid.Rows[rowIndex];
        if (row.Tag is not FlowPropertyDefinition property)
            return;

        try
        {
            _canvas.SelectedNode.Parameters[property.Name] = ResolvePropertyValue(
                property,
                row.Cells[1].Value?.ToString());
            _canvas.Invalidate();
            _isDirty = true;
            UpdateTitle();
            SetStatus($"Updated {property.Name}", DesignerTheme.Signal);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, DesignerTheme.Error);
            UpdateInspector();
        }
    }

    private static object? ResolvePropertyValue(FlowPropertyDefinition property, string? rawValue)
    {
        if (property.Options is { Count: > 0 })
        {
            var option = property.Options.FirstOrDefault(item =>
                string.Equals(item.Label, rawValue, StringComparison.OrdinalIgnoreCase));

            if (option is not null)
                return option.Value;
        }

        return ParseValue(rawValue, property.ValueKind);
    }

    private static FlowPropertyOption? FindOptionForValue(
        FlowPropertyDefinition property,
        object? value)
    {
        if (property.Options is not { Count: > 0 })
            return null;

        var formattedValue = FormatValue(value);
        return property.Options.FirstOrDefault(option =>
            string.Equals(FormatValue(option.Value), formattedValue, StringComparison.OrdinalIgnoreCase));
    }

    private void NewFlow()
    {
        _canvas.SetDocument(new FlowDocument { Name = "Untitled Flow", BoardId = CurrentBoard.Id });
        RebuildCatalogForDocumentBoard(updateSelector: true);
        _canvas.FitDocumentToView();
        _currentFile = null;
        _isDirty = false;
        _messages.Text = "New flow.";
        UpdateTitle();
    }

    private void AddManualTriggerPreset()
    {
        var origin = GetPresetOrigin();
        var run = CreatePresetNode("run", BuiltInBlockCatalog.ManualTrigger, origin);
        AddPresetSnippet("Manual trigger", [run], []);
    }

    private void AddDelayPreset()
    {
        var origin = GetPresetOrigin();
        var timer = CreatePresetNode(
            "delay",
            BuiltInBlockCatalog.Timer,
            origin,
            new Dictionary<string, object?> { ["intervalMs"] = 1000 });
        AddPresetSnippet("Delay", [timer], []);
    }

    private void AddDebugPreset()
    {
        var origin = GetPresetOrigin();
        var debug = CreatePresetNode(
            "debug",
            BuiltInBlockCatalog.DebugLog,
            origin,
            new Dictionary<string, object?> { ["label"] = "debug", ["message"] = "debug" });
        AddPresetSnippet("Debug", [debug], []);
    }

    private void AddGpioOutputPreset()
    {
        var origin = GetPresetOrigin();
        var write = CreatePresetNode(
            "output",
            BuiltInBlockCatalog.GpioSetOutput,
            origin,
            new Dictionary<string, object?> { ["pin"] = 2, ["value"] = true, ["activeLow"] = false });

        AddPresetSnippet(
            "GPIO output",
            [write],
            []);
    }

    private void AddReadDebugPreset()
    {
        var origin = GetPresetOrigin();
        var pinMode = CreatePresetNode(
            "pin-mode",
            BuiltInBlockCatalog.GpioPinMode,
            origin,
            new Dictionary<string, object?> { ["pin"] = 2, ["mode"] = "Input" });
        var read = CreatePresetNode(
            "read",
            BuiltInBlockCatalog.GpioDigitalRead,
            Offset(origin, 300, 0),
            new Dictionary<string, object?> { ["pin"] = 2 });
        var debug = CreatePresetNode(
            "debug",
            BuiltInBlockCatalog.DebugLog,
            Offset(origin, 600, 0),
            new Dictionary<string, object?> { ["label"] = "GPIO 2" });

        AddPresetSnippet(
            "Read debug",
            [pinMode, read, debug],
            [
                Connect(pinMode, "done", read, "trigger"),
                Connect(pinMode, "done", debug, "trigger"),
                Connect(read, "value", debug, "value")
            ]);
    }

    private void AddComparePreset()
    {
        var origin = GetPresetOrigin();
        var left = CreatePresetNode(
            "left",
            BuiltInBlockCatalog.ConstantNumber,
            origin,
            new Dictionary<string, object?> { ["value"] = 1 });
        var right = CreatePresetNode(
            "right",
            BuiltInBlockCatalog.ConstantNumber,
            Offset(origin, 0, 150),
            new Dictionary<string, object?> { ["value"] = 0 });
        var compare = CreatePresetNode(
            "compare",
            BuiltInBlockCatalog.Compare,
            Offset(origin, 300, 70),
            new Dictionary<string, object?> { ["operator"] = ">" });

        AddPresetSnippet(
            "Compare",
            [left, right, compare],
            [
                Connect(left, "value", compare, "left"),
                Connect(right, "value", compare, "right")
            ]);
    }

    private void AddSampleDashboardPreset()
    {
        var origin = GetPresetOrigin();
        var sample = CreatePresetNode(
            "sample",
            BuiltInBlockCatalog.SampleChannel,
            origin,
            new Dictionary<string, object?>
            {
                ["pin"] = 32,
                ["analog"] = true,
                ["mode"] = "HardwareTimer",
                ["sampleRateHz"] = 1000,
                ["bufferCapacity"] = 4096,
                ["backpressure"] = "DropOldest",
                ["batchSize"] = 64
            });
        var dashboard = CreatePresetNode(
            "dashboard",
            BuiltInBlockCatalog.StreamDashboard,
            Offset(origin, 300, 0),
            new Dictionary<string, object?>
            {
                ["channelName"] = "GPIO 32",
                ["refreshHz"] = 20,
                ["retentionSeconds"] = 30,
                ["maxPoints"] = 64
            });
        var debug = CreatePresetNode(
            "debug",
            BuiltInBlockCatalog.DebugLog,
            Offset(origin, 300, 150),
            new Dictionary<string, object?> { ["label"] = "GPIO 32 stream" });

        AddPresetSnippet(
            "Sample dashboard",
            [sample, dashboard, debug],
            [
                Connect(sample, "samples", dashboard, "samples"),
                Connect(sample, "status", debug, "value")
            ]);
    }

    private void AddPresetSnippet(
        string presetName,
        IReadOnlyList<FlowNode> nodes,
        IReadOnlyList<FlowConnection> connections)
    {
        _canvas.AddSnippet(nodes, connections);
        _canvas.FitDocumentToView();
        _messages.Text = $"{presetName} preset added.";
        SetStatus($"{presetName} preset added", DesignerTheme.Signal);
    }

    private void ArrangeFlow()
    {
        if (!_canvas.Document.Nodes.Any())
            return;

        var levels = CalculateNodeLevels();
        const double left = 64;
        const double top = 72;
        const double columnWidth = 292;
        const double rowHeight = 148;

        foreach (var group in _canvas.Document.Nodes
                     .OrderBy(node => levels.GetValueOrDefault(node.Id))
                     .ThenBy(node => node.Position.Y)
                     .ThenBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
                     .GroupBy(node => levels.GetValueOrDefault(node.Id)))
        {
            var row = 0;
            foreach (var node in group)
                node.Position = new FlowPosition(left + group.Key * columnWidth, top + row++ * rowHeight);
        }

        _canvas.ResetExecutionStates();
        _canvas.FitDocumentToView();
        _isDirty = true;
        UpdateTitle();
        SetStatus("Flow arranged", DesignerTheme.Signal);
    }

    private void ConfigureSelectedNode()
    {
        var node = _canvas.SelectedNode;
        if (node is null || !_catalog.TryGet(node.Type, out var definition))
            return;

        using var dialog = new CodeBridgeNodeEditorDialog(definition, node);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
            return;

        foreach (var parameter in dialog.ReadParameters())
            node.Parameters[parameter.Key] = parameter.Value;

        _canvas.Invalidate();
        UpdateInspector();
        _isDirty = true;
        UpdateTitle();
        SetStatus($"{definition.DisplayName} configured", DesignerTheme.Signal);
    }

    private Dictionary<string, int> CalculateNodeLevels()
    {
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in _canvas.Document.Nodes)
            levels.TryAdd(node.Id, 0);

        var nodeIds = levels.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var pass = 0; pass < levels.Count; pass++)
        {
            var changed = false;
            foreach (var connection in _canvas.Document.Connections)
            {
                if (!nodeIds.Contains(connection.FromNodeId) || !nodeIds.Contains(connection.ToNodeId))
                    continue;

                var nextLevel = levels[connection.FromNodeId] + 1;
                if (nextLevel <= levels[connection.ToNodeId])
                    continue;

                levels[connection.ToNodeId] = nextLevel;
                changed = true;
            }

            if (!changed)
                break;
        }

        return levels;
    }

    private FlowNode CreatePresetNode(
        string idBase,
        string blockType,
        FlowPosition position,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var node = new FlowNode
        {
            Id = CreateUniqueDocumentId(idBase),
            Type = blockType,
            Position = position
        };

        var definition = _catalog.Get(blockType);
        foreach (var property in definition.Properties.Where(property => property.DefaultValue is not null))
            node.Parameters[property.Name] = property.DefaultValue;

        if (parameters is not null)
        {
            foreach (var parameter in parameters)
                node.Parameters[parameter.Key] = parameter.Value;
        }

        return node;
    }

    private string CreateUniqueDocumentId(string idBase)
    {
        var used = _canvas.Document.Nodes
            .Select(node => node.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var id = idBase;
        var index = 2;
        while (used.Contains(id))
            id = $"{idBase}-{index++}";

        return id;
    }

    private FlowPosition GetPresetOrigin()
    {
        const double margin = 60;
        const double horizontalStep = 300;
        const double verticalStep = 190;

        if (!_canvas.Document.Nodes.Any())
            return new FlowPosition(margin, 80);

        var right = _canvas.Document.Nodes.Max(node => node.Position.X) + horizontalStep;
        var top = Math.Max(80, _canvas.Document.Nodes.Min(node => node.Position.Y));

        if (_canvas.ClientSize.Width <= 0 || right + 240 <= _canvas.ClientSize.Width)
            return new FlowPosition(right, top);

        var bottom = _canvas.Document.Nodes.Max(node => node.Position.Y) + verticalStep;
        return new FlowPosition(margin, bottom);
    }

    private static FlowPosition Offset(FlowPosition origin, double x, double y) =>
        new(origin.X + x, origin.Y + y);

    private static FlowConnection Connect(
        FlowNode from,
        string fromPort,
        FlowNode to,
        string toPort) => new()
    {
        Id = $"{from.Id}-{to.Id}-{toPort}",
        FromNodeId = from.Id,
        FromPort = fromPort,
        ToNodeId = to.Id,
        ToPort = toPort
    };

    private void LoadBlinkTestFlow(bool activeLow)
    {
        SetDocumentFromTemplate(
            BuiltInFlowPresets.CreateBlinkOnce(activeLow: activeLow),
            activeLow
                ? $"Active-low blink test loaded. Connect the {CurrentBoard.DisplayName}, then press Run."
                : $"Blink test loaded. Connect the {CurrentBoard.DisplayName}, then press Run.");
    }

    private void LoadReadDebugFlow()
    {
        SetDocumentFromTemplate(
            BuiltInFlowPresets.CreateDigitalReadDebug(),
            $"GPIO read debug test loaded. Connect the {CurrentBoard.DisplayName}, then press Run.");
    }

    private void LoadStreamDashboardFlow()
    {
        SetDocumentFromTemplate(
            BuiltInFlowPresets.CreateAnalogStreamDashboard(),
            $"Analog stream test loaded. Upload CodeBridge firmware to the {CurrentBoard.DisplayName}, then press Run.");
    }

    private void SetDocumentFromTemplate(FlowDocument document, string message)
    {
        document.BoardId = CurrentBoard.Id;
        _canvas.SetDocument(document);
        RebuildCatalogForDocumentBoard(updateSelector: true);
        _canvas.FitDocumentToView();
        _currentFile = null;
        _isDirty = true;
        _messages.Text = message;
        UpdateTitle();
    }

    private void OpenFlow()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "CodeBridge Flow (*.cbflow)|*.cbflow|JSON (*.json)|*.json|All files (*.*)|*.*",
            Title = "Open CodeBridge Flow"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            var document = FlowDocumentJson.Deserialize(File.ReadAllText(dialog.FileName));
            _canvas.SetDocument(document);
            RebuildCatalogForDocumentBoard(updateSelector: true);
            _canvas.FitDocumentToView();
            _currentFile = dialog.FileName;
            _isDirty = false;
            _messages.Text = $"Opened {Path.GetFileName(dialog.FileName)}.";
            UpdateTitle();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveFlow()
    {
        if (_currentFile is null)
        {
            using var dialog = new SaveFileDialog
            {
                Filter = "CodeBridge Flow (*.cbflow)|*.cbflow|JSON (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = "cbflow",
                Title = "Save CodeBridge Flow"
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            _currentFile = dialog.FileName;
        }

        try
        {
            File.WriteAllText(_currentFile, FlowDocumentJson.Serialize(_canvas.Document));
            _isDirty = false;
            _messages.Text = $"Saved {Path.GetFileName(_currentFile)}.";
            SetStatus("Saved", DesignerTheme.Signal);
            UpdateTitle();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ValidateFlow()
    {
        _canvas.ResetExecutionStates();
        var result = _validator.Validate(_canvas.Document, _catalog);
        ShowValidation(result);
        ApplyValidationStates(result);
        SetStatus(
            result.IsValid ? "Flow valid" : "Flow has errors",
            result.IsValid ? DesignerTheme.Signal : DesignerTheme.Error);
    }

    private async Task RunFlowAsync()
    {
        if (_isRunning)
            return;

        _runCancellation = new CancellationTokenSource();
        _isRunning = true;
        UpdateRunControls();

        try
        {
            if (ExecutionMode == FlowExecutionMode.Loop)
                await RunLoopAsync(_runCancellation.Token);
            else
                await RunOnceAsync(_runCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            _messages.AppendText(Environment.NewLine + "Run stopped.");
            SetStatus("Run stopped", DesignerTheme.Warning);
        }
        finally
        {
            _isRunning = false;
            _runCancellation.Dispose();
            _runCancellation = null;
            UpdateRunControls();
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var iteration = 1;
        while (!ct.IsCancellationRequested)
        {
            _messages.Text = $"Loop iteration {iteration++}..." + Environment.NewLine;
            if (!await ExecuteValidatedFlowAsync(ct))
                break;

            await Task.Delay(LoopIntervalMs, ct);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        _messages.Text = "Running flow..." + Environment.NewLine;
        await ExecuteValidatedFlowAsync(ct);
    }

    private async Task<bool> ExecuteValidatedFlowAsync(CancellationToken ct)
    {
        _canvas.ResetExecutionStates();

        var validation = _validator.Validate(_canvas.Document, _catalog);
        if (!validation.IsValid)
        {
            ShowValidation(validation);
            ApplyValidationStates(validation);
            SetStatus("Flow has errors", DesignerTheme.Error);
            return false;
        }

        if (RequiresConnectedBoard(_canvas.Document) && _connectedBoard is not { IsConnected: true })
        {
            _messages.Text = "This flow uses ESP32/GPIO blocks. Connect the board first, then run again.";
            SetStatus("Board required", DesignerTheme.Error);
            return false;
        }

        try
        {
            var runtime = new FlowRuntime(_catalog);
            var context = new FlowExecutionContext
            {
                Board = _connectedBoard is { IsConnected: true } ? _connectedBoard : null,
                EventHandler = HandleFlowExecutionEventAsync,
                TraceDelay = TimeSpan.FromMilliseconds((int)_traceDelayInput.Value)
            };
            var result = await runtime.ExecuteAsync(_canvas.Document, context, ct);
            _messages.AppendText(Environment.NewLine + FormatRunResult(result));
            SetStatus(ExecutionMode == FlowExecutionMode.Loop ? "Loop iteration completed" : "Run completed", DesignerTheme.Signal);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _messages.AppendText(Environment.NewLine + ex.Message);
            SetStatus("Run failed", DesignerTheme.Error);
            return false;
        }
    }

    private void StopRun() => _runCancellation?.Cancel();

    private ValueTask HandleFlowExecutionEventAsync(
        FlowExecutionEvent executionEvent,
        CancellationToken cancellationToken)
    {
        if (IsDisposed || !IsHandleCreated)
            return ValueTask.CompletedTask;

        if (InvokeRequired)
            BeginInvoke((MethodInvoker)(() => ApplyFlowExecutionEvent(executionEvent)));
        else
            ApplyFlowExecutionEvent(executionEvent);

        return ValueTask.CompletedTask;
    }

    private void ApplyFlowExecutionEvent(FlowExecutionEvent executionEvent)
    {
        var visualState = executionEvent.Kind switch
        {
            FlowExecutionEventKind.NodeStarted => NodeExecutionVisualState.Running,
            FlowExecutionEventKind.NodeCompleted => NodeExecutionVisualState.Completed,
            FlowExecutionEventKind.NodeSkipped => NodeExecutionVisualState.Skipped,
            FlowExecutionEventKind.NodeFailed => NodeExecutionVisualState.Failed,
            _ => NodeExecutionVisualState.Idle
        };

        var visualMessage = executionEvent.Kind switch
        {
            FlowExecutionEventKind.NodeFailed => executionEvent.Message,
            FlowExecutionEventKind.NodeSkipped => "Skipped",
            FlowExecutionEventKind.NodeCompleted
                when string.Equals(executionEvent.BlockType, BuiltInBlockCatalog.DebugLog, StringComparison.OrdinalIgnoreCase)
                => FormatDebugMessage(executionEvent.Outputs),
            _ => null
        };

        _canvas.SetNodeExecutionState(executionEvent.NodeId, visualState, visualMessage);
        _canvas.Refresh();

        var line = executionEvent.Kind switch
        {
            FlowExecutionEventKind.NodeStarted => $"RUN  {executionEvent.NodeId}",
            FlowExecutionEventKind.NodeCompleted
                when string.Equals(executionEvent.BlockType, BuiltInBlockCatalog.DebugLog, StringComparison.OrdinalIgnoreCase)
                => $"DBG  {executionEvent.NodeId}: {FormatDebugMessage(executionEvent.Outputs)}",
            FlowExecutionEventKind.NodeCompleted => $"OK   {executionEvent.NodeId}: {FormatOutputs(executionEvent.Outputs)}",
            FlowExecutionEventKind.NodeSkipped => $"SKIP {executionEvent.NodeId}",
            FlowExecutionEventKind.NodeFailed => $"ERR  {executionEvent.NodeId}: {executionEvent.Message}",
            _ => $"{executionEvent.Kind} {executionEvent.NodeId}"
        };

        _messages.AppendText(line + Environment.NewLine);
    }

    private void ApplyValidationStates(FlowValidationResult result)
    {
        foreach (var issue in result.Errors.Where(issue => issue.NodeId is not null))
            _canvas.SetNodeExecutionState(issue.NodeId!, NodeExecutionVisualState.Failed, issue.Message);

        _canvas.Refresh();
    }

    private static bool RequiresConnectedBoard(FlowDocument document) =>
        document.Nodes.Any(node =>
            string.Equals(node.Type, BuiltInBlockCatalog.GpioPinMode, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(node.Type, BuiltInBlockCatalog.GpioDigitalRead, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(node.Type, BuiltInBlockCatalog.GpioAnalogRead, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(node.Type, BuiltInBlockCatalog.GpioDigitalWrite, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(node.Type, BuiltInBlockCatalog.GpioSetOutput, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(node.Type, BuiltInBlockCatalog.GpioBlinkLed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(node.Type, BuiltInBlockCatalog.SampleChannel, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(node.Type, BuiltInBlockCatalog.InterruptInput, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(node.Type, BuiltInBlockCatalog.ServoWrite, StringComparison.OrdinalIgnoreCase));

    private async Task ConnectBoardAsync()
    {
        if (_usingExternalBoard)
        {
            SetStatus("Board connection is owned by the host form", DesignerTheme.Signal);
            return;
        }

        if (_connectedBoard is { IsConnected: true } || _isConnecting)
            return;

        _isConnecting = true;
        UpdateConnectionControls();

        IBoard? board = null;
        var boardProfile = CurrentBoard;
        try
        {
            var request = CreateConnectionRequest(boardProfile);
            if (IsSerialMode())
            {
                _messages.Text = $"Connecting to {boardProfile.DisplayName} over Serial {request.PortName} @ {request.BaudRate}...";
            }
            else
            {
                _messages.Text = $"Connecting to {boardProfile.DisplayName} over WiFi {request.Host}:{request.TcpPort}...";
            }

            board = await BoardConnectionService.ConnectAsync(request);
            _connectedBoard = board;
            _messages.Text = await BuildConnectionSummaryAsync(board);
            SetStatus("Board connected", DesignerTheme.Signal);
        }
        catch (Exception ex)
        {
            if (board is not null)
                await BoardConnectionService.DisposeBoardAsync(board);

            _connectedBoard = null;
            _messages.Text = BoardConnectionService.FormatConnectionFailure(ex, boardProfile, IsSerialMode() ? _portSelector.Text.Trim() : null);
            SetStatus("Connection failed", DesignerTheme.Error);
        }
        finally
        {
            _isConnecting = false;
            UpdateConnectionControls();
        }
    }

    private BoardConnectionRequest CreateConnectionRequest(BoardProfile boardProfile) =>
        new(
            boardProfile,
            IsSerialMode() ? CodeBridgeTransportMode.Serial : CodeBridgeTransportMode.WiFi,
            _portSelector.Text.Trim(),
            (int)_baudRateInput.Value,
            _wifiHostInput.Text.Trim(),
            (int)_wifiPortInput.Value);

    private async Task DisconnectBoardAsync()
    {
        if (_connectedBoard is null)
            return;

        if (_usingExternalBoard)
        {
            SetStatus("Host-owned board stays connected", DesignerTheme.Signal);
            _messages.Text = "This board connection belongs to the WinForms component. Disconnect it from the host form.";
            return;
        }

        try
        {
            StopRun();
            await BoardConnectionService.DisposeBoardAsync(_connectedBoard);

            _messages.Text = "Board disconnected.";
            SetStatus("Board disconnected", DesignerTheme.MutedText);
        }
        catch (Exception ex)
        {
            _messages.Text = ex.Message;
            SetStatus("Disconnect failed", DesignerTheme.Error);
        }
        finally
        {
            _connectedBoard = null;
            UpdateConnectionControls();
        }
    }

    private static async Task<string> BuildConnectionSummaryAsync(IBoard board)
    {
        try
        {
            var info = await board.GetInfoAsync();
            return string.Join(
                Environment.NewLine,
                $"Connected to {board.Name}.",
                $"Firmware: {board.FirmwareVersion}",
                $"Chip: {info.ChipModel}",
                $"CPU: {info.CpuFrequencyMHz} MHz",
                $"Free heap: {info.FreeHeapBytes} bytes",
                $"Flash: {info.FlashSizeBytes} bytes",
                $"SDK: {info.SdkVersion}");
        }
        catch
        {
            return $"Connected to {board.Name}. Firmware: {board.FirmwareVersion}";
        }
    }

    private async Task UploadFirmwareAsync()
    {
        if (_isUploadingFirmware || _isConnecting)
            return;

        if (!IsSerialMode())
        {
            _messages.Text = "Firmware upload is available over USB Serial only.";
            SetStatus("Switch to Serial mode before upload", DesignerTheme.Warning);
            return;
        }

        var portName = _portSelector.Text.Trim();
        if (string.IsNullOrWhiteSpace(portName))
        {
            _messages.Text = "Select a serial port before uploading firmware.";
            SetStatus("Select a serial port", DesignerTheme.Warning);
            return;
        }

        var boardProfile = CurrentBoard;
        IBoardFirmwareUploader uploader;
        try
        {
            uploader = FirmwareUploaderFactory.Create(new FirmwareUploadRequest(boardProfile, portName));
        }
        catch (Exception ex)
        {
            _messages.Text = ex.Message;
            SetStatus("Firmware project missing", DesignerTheme.Error);
            return;
        }

        _isUploadingFirmware = true;
        UpdateConnectionControls();

        _messages.Text = string.Join(
            Environment.NewLine,
            $"Uploading CodeBridge firmware to {boardProfile.DisplayName} on {portName}...",
            uploader.WorkingDirectory,
            uploader.CommandText,
            "");

        try
        {
            var result = await uploader.UploadAsync();
            if (result.Success)
            {
                _messages.Text = string.Join(
                    Environment.NewLine,
                    $"Firmware uploaded to {boardProfile.DisplayName} on {portName}.",
                    "Press Connect after the board restarts.",
                    "",
                    result.Log);
                SetStatus("Firmware uploaded", DesignerTheme.Signal);
            }
            else
            {
                _messages.Text = string.Join(
                    Environment.NewLine,
                    result.ExitCode is null
                        ? "Firmware upload failed."
                        : $"Firmware upload failed with exit code {result.ExitCode}.",
                    "",
                    result.Log);
                SetStatus("Firmware upload failed", DesignerTheme.Error);
            }
        }
        catch (Exception ex)
        {
            _messages.Text = ex.Message;
            SetStatus("Firmware upload failed", DesignerTheme.Error);
        }
        finally
        {
            _isUploadingFirmware = false;
            UpdateConnectionControls();
        }
    }

    private void RefreshPorts()
    {
        var selectedPort = _portSelector.Text;
        _portSelector.Items.Clear();

        foreach (var port in BoardDiscovery.DiscoverPorts())
            _portSelector.Items.Add(port.Name);

        if (!string.IsNullOrWhiteSpace(selectedPort) && _portSelector.Items.Contains(selectedPort))
            _portSelector.SelectedItem = selectedPort;
        else if (_portSelector.Items.Count > 0)
            _portSelector.SelectedIndex = 0;

        ApplyBoardSuggestionForSelectedPort();

        SetStatus(
            _portSelector.Items.Count == 0 ? "No serial ports found" : "Serial ports refreshed",
            _portSelector.Items.Count == 0 ? DesignerTheme.Warning : DesignerTheme.MutedText);
    }

    private void ApplyBoardSuggestionForSelectedPort()
    {
        if (_connectedBoard is not null || _isConnecting || _isUploadingFirmware)
            return;

        var suggestion = BoardPortDetector.SuggestBoardForPort(_portSelector.Text.Trim());
        if (suggestion is null || string.Equals(CurrentBoard.Id, suggestion.Id, StringComparison.OrdinalIgnoreCase))
            return;

        _canvas.Document.BoardId = suggestion.Id;
        RebuildCatalogForDocumentBoard(updateSelector: true);
        _isDirty = true;
        UpdateTitle();
        SetStatus($"Detected {suggestion.DisplayName} on {_portSelector.Text}", DesignerTheme.Signal);
    }

    private void PopulateProbePins()
    {
        _probePinSelector.DisplayMember = nameof(FlowPropertyOption.Label);
        _probePinSelector.ValueMember = nameof(FlowPropertyOption.Value);
        _probePinSelector.Items.Clear();

        foreach (var option in CurrentBoard.DigitalReadPinOptions)
            _probePinSelector.Items.Add(option);

        var defaultPin = _probePinSelector.Items
            .OfType<FlowPropertyOption>()
            .FirstOrDefault(option => Convert.ToInt32(option.Value, CultureInfo.InvariantCulture) == 2);

        if (defaultPin is not null)
            _probePinSelector.SelectedItem = defaultPin;
        else if (_probePinSelector.Items.Count > 0)
            _probePinSelector.SelectedIndex = 0;
    }

    private void UpdateConnectionControls()
    {
        var connected = _connectedBoard is { IsConnected: true };
        var serial = IsSerialMode();
        var editable = !_isConnecting && !_isUploadingFirmware && !connected && !_usingExternalBoard;

        _transportSelector.Enabled = editable;
        _portSelector.Enabled = editable && serial;
        _refreshPortsButton.Enabled = editable && serial;
        _baudRateInput.Enabled = editable && serial;
        _wifiHostInput.Enabled = editable && !serial;
        _wifiPortInput.Enabled = editable && !serial;
        _connectButton.Enabled = !_isConnecting && !_isUploadingFirmware && !connected && !IsInDesignMode && !_usingExternalBoard;
        _disconnectButton.Enabled = !_isConnecting && !_isUploadingFirmware && connected && !_usingExternalBoard;
        _uploadFirmwareButton.Enabled = editable && serial && !IsInDesignMode;
        _probePinSelector.Enabled = !_isConnecting && !_isUploadingFirmware && connected;
        _probeHighButton.Enabled = !_isConnecting && !_isUploadingFirmware && connected;
        _probeLowButton.Enabled = !_isConnecting && !_isUploadingFirmware && connected;
        _probeReadButton.Enabled = !_isConnecting && !_isUploadingFirmware && connected;

        if (_isUploadingFirmware)
        {
            _connectionState.Text = "Uploading firmware...";
            _connectionState.ForeColor = DesignerTheme.Warning;
            _hardwareStatus.Text = $"Uploading {CurrentBoard.DisplayName} firmware...";
            _hardwareStatus.ForeColor = DesignerTheme.Warning;
        }
        else if (_isConnecting)
        {
            _connectionState.Text = "Connecting...";
            _connectionState.ForeColor = DesignerTheme.Warning;
            _hardwareStatus.Text = $"Connecting {CurrentBoard.DisplayName}...";
            _hardwareStatus.ForeColor = DesignerTheme.Warning;
        }
        else if (connected && _usingExternalBoard)
        {
            _connectionState.Text = $"Host board ({_connectedBoard!.FirmwareVersion})";
            _connectionState.ForeColor = DesignerTheme.Signal;
            _hardwareStatus.Text = $"{CurrentBoard.DisplayName} host board";
            _hardwareStatus.ForeColor = DesignerTheme.Signal;
        }
        else if (connected)
        {
            _connectionState.Text = $"Connected ({_connectedBoard!.FirmwareVersion})";
            _connectionState.ForeColor = DesignerTheme.Signal;
            _hardwareStatus.Text = $"{CurrentBoard.DisplayName} @ {_portSelector.Text}";
            _hardwareStatus.ForeColor = DesignerTheme.Signal;
        }
        else
        {
            _connectionState.Text = IsInDesignMode ? "Design mode" : "Disconnected";
            _connectionState.ForeColor = DesignerTheme.MutedText;
            _hardwareStatus.Text = $"{CurrentBoard.DisplayName} disconnected";
            _hardwareStatus.ForeColor = DesignerTheme.MutedText;
        }

        UpdateRunControls();
    }

    private void UpdateRunControls()
    {
        _runButton.Enabled = !_isRunning && !IsInDesignMode;
        _stopButton.Enabled = _isRunning;
        _loopIntervalInput.Enabled = !_isRunning && ExecutionMode == FlowExecutionMode.Loop;
    }

    private void SyncExecutionModeSelector()
    {
        _syncingExecutionMode = true;
        _executionModeSelector.SelectedItem = _executionMode.ToString();
        _syncingExecutionMode = false;
        UpdateRunControls();
    }

    private bool IsSerialMode() =>
        string.Equals(_transportSelector.SelectedItem?.ToString(), "Serial", StringComparison.OrdinalIgnoreCase);

    private async Task WriteProbePinAsync(bool high)
    {
        if (_connectedBoard is not { IsConnected: true })
        {
            SetStatus("Connect a board before probing GPIO", DesignerTheme.Warning);
            return;
        }

        var pin = GetSelectedProbePin();
        var pinDefinition = CurrentBoard.FindPin(pin);
        if (pinDefinition is { SupportsDigitalWrite: false })
        {
            _messages.Text = $"{DescribePin(pin)} is input-only on {CurrentBoard.DisplayName}.";
            SetStatus($"GPIO {pin} is input-only", DesignerTheme.Warning);
            return;
        }

        try
        {
            await _connectedBoard.Gpio.SetPinModeAsync(pin, PinMode.Output);
            await _connectedBoard.Gpio.DigitalWriteAsync(pin, high ? PinValue.High : PinValue.Low);
            _messages.Text = $"{DescribePin(pin)} <- {(high ? "HIGH" : "LOW")}";
            SetStatus($"GPIO {pin} {(high ? "HIGH" : "LOW")}", DesignerTheme.Signal);
        }
        catch (Exception ex)
        {
            _messages.Text = ex.Message;
            SetStatus("GPIO probe failed", DesignerTheme.Error);
        }
    }

    private async Task ReadProbePinAsync()
    {
        if (_connectedBoard is not { IsConnected: true })
        {
            SetStatus("Connect a board before reading GPIO", DesignerTheme.Warning);
            return;
        }

        var pin = GetSelectedProbePin();
        try
        {
            await _connectedBoard.Gpio.SetPinModeAsync(pin, PinMode.Input);
            var value = await _connectedBoard.Gpio.DigitalReadAsync(pin);
            var state = value == PinValue.High ? "HIGH / true" : "LOW / false";
            _messages.Text = $"{DescribePin(pin)} -> {state}";
            SetStatus($"GPIO {pin} {state}", DesignerTheme.Signal);
        }
        catch (Exception ex)
        {
            _messages.Text = ex.Message;
            SetStatus("GPIO read failed", DesignerTheme.Error);
        }
    }

    private int GetSelectedProbePin()
    {
        if (_probePinSelector.SelectedItem is FlowPropertyOption option && option.Value is not null)
            return Convert.ToInt32(option.Value, CultureInfo.InvariantCulture);

        if (int.TryParse(_probePinSelector.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pin))
            return pin;

        throw new InvalidOperationException("Select a GPIO pin before probing.");
    }

    private string DescribePin(int pin)
    {
        var definition = CurrentBoard.FindPin(pin);
        return definition is null ? $"GPIO {pin}" : definition.Label;
    }

    private void ShowValidation(FlowValidationResult result)
    {
        if (!result.Issues.Any())
        {
            _messages.Text = "No validation issues.";
            return;
        }

        _messages.Text = string.Join(
            Environment.NewLine,
            result.Issues.Select(issue =>
                $"{issue.Severity}: {issue.Message}" +
                (issue.NodeId is null ? "" : $" [node: {issue.NodeId}]") +
                (issue.ConnectionId is null ? "" : $" [wire: {issue.ConnectionId}]")));
    }

    private static string FormatRunResult(FlowExecutionResult result)
    {
        if (!result.NodeOutputs.Any())
            return "Run completed. No outputs.";

        return string.Join(
            Environment.NewLine,
            result.NodeOutputs.Select(node =>
                $"{node.Key}: " +
                string.Join(", ", node.Value.Select(output => $"{output.Key}={FormatValue(output.Value)}"))));
    }

    private static string FormatOutputs(IReadOnlyDictionary<string, object?>? outputs)
    {
        if (outputs is null || outputs.Count == 0)
            return "(no outputs)";

        return string.Join(", ", outputs.Select(output => $"{output.Key}={FormatValue(output.Value)}"));
    }

    private static string FormatDebugMessage(IReadOnlyDictionary<string, object?>? outputs)
    {
        if (outputs is not null &&
            outputs.TryGetValue("message", out var message) &&
            !string.IsNullOrWhiteSpace(FormatValue(message)))
        {
            return FormatValue(message);
        }

        if (outputs is not null && outputs.TryGetValue("value", out var value))
            return FormatValue(value);

        return "(empty)";
    }

    private static object? ParseValue(string? text, FlowValueKind kind)
    {
        text ??= string.Empty;

        return kind switch
        {
            FlowValueKind.Boolean => ParseBoolean(text),
            FlowValueKind.Integer => int.Parse(text, CultureInfo.InvariantCulture),
            FlowValueKind.Number => double.Parse(text, CultureInfo.InvariantCulture),
            FlowValueKind.String => text,
            FlowValueKind.Trigger => ParseBoolean(text),
            FlowValueKind.Any => text,
            _ => text
        };
    }

    private static bool ParseBoolean(string text)
    {
        if (bool.TryParse(text, out var value))
            return value;

        if (text == "1")
            return true;

        if (text == "0")
            return false;

        throw new FormatException("Boolean values must be true/false or 1/0.");
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => "",
            IReadOnlyList<BoardSampleFrame> frames => FormatSampleFrames(frames),
            BoardSampleChannel channel => $"{channel.Id} GPIO {channel.Request.Pin} {channel.Request.SampleRateHz} Hz",
            JsonElement json => json.ValueKind == JsonValueKind.String ? json.GetString() ?? "" : json.GetRawText(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };
    }

    private static string FormatSampleFrames(IReadOnlyList<BoardSampleFrame> frames)
    {
        if (frames.Count == 0)
            return "0 samples";

        var last = frames[^1];
        return $"{frames.Count} samples, last={last.Value.ToString(CultureInfo.InvariantCulture)} at {last.Elapsed.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms";
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

    private void UpdateTitle()
    {
        var file = _currentFile is null ? "Untitled" : Path.GetFileName(_currentFile);
        Text = $"{(_isDirty ? "*" : "")}{file} - CodeBridge Designer";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _runCancellation?.Cancel();
            _runCancellation?.Dispose();
            if (!_usingExternalBoard)
                _connectedBoard?.Dispose();
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    private static Label CreateFieldLabel(string text) => new()
    {
        Dock = DockStyle.Fill,
        Text = text,
        ForeColor = DesignerTheme.MutedText,
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static void ConfigureComboBox(ComboBox comboBox)
    {
        comboBox.Dock = DockStyle.Fill;
        comboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        DesignerTheme.StyleCombo(comboBox);
        comboBox.Margin = new Padding(0, 2, 6, 2);
    }

    private static void ConfigureTextBox(TextBox textBox, string value)
    {
        textBox.Dock = DockStyle.Fill;
        textBox.BorderStyle = BorderStyle.FixedSingle;
        textBox.BackColor = DesignerTheme.InputBackground;
        textBox.ForeColor = DesignerTheme.Text;
        textBox.Margin = new Padding(0, 2, 6, 2);
        textBox.Text = value;
    }

    private static void ConfigureNumeric(
        NumericUpDown input,
        decimal minimum,
        decimal maximum,
        decimal value)
    {
        input.Dock = DockStyle.Fill;
        input.Minimum = minimum;
        input.Maximum = maximum;
        input.Value = value;
        input.BorderStyle = BorderStyle.FixedSingle;
        input.BackColor = DesignerTheme.InputBackground;
        input.ForeColor = DesignerTheme.Text;
        input.Margin = new Padding(0, 2, 6, 2);
    }

    private static void ConfigureButton(Button button, string text)
    {
        button.Dock = DockStyle.Fill;
        DesignerTheme.StyleButton(button);
        button.Text = text;
        button.Margin = new Padding(0, 2, 6, 2);
    }

    private sealed record BoardProfileItem(BoardProfile Profile)
    {
        public override string ToString() => Profile.DisplayName;
    }
}

internal enum ToolbarGlyph
{
    New,
    Open,
    Save,
    Blink,
    BlinkLow,
    Read,
    Stream,
    ZoomOut,
    ZoomReset,
    ZoomIn,
    Fit,
    Arrange,
    Validate,
    Run,
    Stop,
    Configure,
    Delete
}

internal sealed class CodeBridgeToolStripRenderer : ToolStripProfessionalRenderer
{
    public CodeBridgeToolStripRenderer()
        : base(new CodeBridgeToolStripColorTable())
    {
        RoundedEdges = false;
    }
}

internal sealed class CodeBridgeToolStripColorTable : ProfessionalColorTable
{
    public override Color ToolStripGradientBegin => DesignerTheme.Surface;
    public override Color ToolStripGradientMiddle => DesignerTheme.Surface;
    public override Color ToolStripGradientEnd => DesignerTheme.Surface;
    public override Color ButtonSelectedGradientBegin => DesignerTheme.SurfaceHover;
    public override Color ButtonSelectedGradientMiddle => DesignerTheme.SurfaceHover;
    public override Color ButtonSelectedGradientEnd => DesignerTheme.SurfaceHover;
    public override Color ButtonPressedGradientBegin => DesignerTheme.SurfaceSelected;
    public override Color ButtonPressedGradientMiddle => DesignerTheme.SurfaceSelected;
    public override Color ButtonPressedGradientEnd => DesignerTheme.SurfaceSelected;
    public override Color SeparatorDark => DesignerTheme.Border;
    public override Color SeparatorLight => DesignerTheme.BorderSubtle;
}
