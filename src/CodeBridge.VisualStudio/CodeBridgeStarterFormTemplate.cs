using System;

namespace CodeBridge.VisualStudio;

/// <summary>
/// The source of the starter form that "CodeBridge WinForms App" and "Add CodeBridge Starter Form" put in a project.
/// It is meant to work the moment it runs: it loads and runs the project's Flows\Blink.cbflow (and again whenever that file is saved),
/// the board is found on USB (or the virtual board is used), Run connects by itself,
/// and a board without the CodeBridge firmware is offered the upload instead of failing with a stack of text.
/// </summary>
internal static class CodeBridgeStarterFormTemplate
{
    /// <summary>The flow file the form runs, relative to the project. <see cref="CodeBridgeStarterFlow"/> creates it.</summary>
    public const string FlowFile = "Flows/Blink.cbflow";

    public static string Create(string namespaceName, string className) =>
        Source
            .Replace("__NAMESPACE__", namespaceName)
            .Replace("__CLASS__", className)
            .Replace("__FLOWFILE__", FlowFile)
            .Replace("\r\n", "\n")
            .Replace("\n", Environment.NewLine);

    private const string Source = """
        using System;
        using System.Drawing;
        using System.Threading.Tasks;
        using System.Windows.Forms;
        using CodeBridge.Designer.WinForms;
        using CodeBridge.Flow;
        using CodeBridge.Flow.Execution;
        using CodeBridge.Transport;

        namespace __NAMESPACE__
        {
            public sealed class __CLASS__ : Form
            {
                // The flow is a file of the project: edit it in Visual Studio and save, and this form runs the new version.
                private const string FlowFile = "__FLOWFILE__";

                // Start the flow as soon as the form is shown. Set to false to start stopped.
                private const bool RunOnStart = true;

                private const string SimulatorPort = "simulator";

                private readonly CodeBridgeBoardComponent _board = new();
                private readonly CodeBridgeFlowControl _flow = new();
                private readonly Label _status = new();
                private readonly ComboBox _portInput = new();
                private readonly CheckBox _loopInput = new();
                private readonly Button _runButton = new();
                private readonly Button _stopButton = new();
                private readonly Button _refreshButton = new();
                private readonly Button _uploadButton = new();
                private readonly Button _editButton = new();
                private readonly TextBox _log = new();
                private bool _closing;
                private bool _populating;
                private bool _stopRequested;
                private PortInfo[] _ports = Array.Empty<PortInfo>();

                public __CLASS__()
                {
                    Text = "CodeBridge Board Starter";
                    StartPosition = FormStartPosition.CenterScreen;
                    MinimumSize = new Size(860, 560);
                    Size = new Size(980, 620);
                    Font = new Font("Segoe UI", 9F);
                    BackColor = Color.FromArgb(246, 247, 248);

                    // Port "auto" finds the board on USB. With no board plugged in, the flow runs on the virtual board.
                    _board.BoardId = BuiltInBoardProfiles.Esp32DevKit.Id;
                    _board.PortName = CodeBridgeBoardComponent.AutoPort;
                    _board.FallbackToSimulator = true;

                    BuildLayout();
                    WireEvents();
                    RefreshPorts();

                    // Loads the .cbflow and keeps watching it: every save reloads the flow.
                    _flow.FlowFile = FlowFile;
                    UpdateState();

                    if (RunOnStart)
                        Shown += async (_, _) => await RunAsync();
                }

                private void BuildLayout()
                {
                    var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14) };
                    root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
                    root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

                    var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 9, Padding = new Padding(0, 0, 14, 0) };
                    left.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
                    left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

                    _status.Dock = DockStyle.Fill;
                    _status.Padding = new Padding(0, 0, 0, 6);
                    _status.TextAlign = ContentAlignment.MiddleLeft;
                    _status.Font = new Font(Font, FontStyle.Bold);
                    _portInput.DropDownStyle = ComboBoxStyle.DropDownList;
                    _loopInput.Text = "Repeat the flow";
                    _loopInput.Checked = true;

                    ConfigureButton(_runButton, "Run");
                    ConfigureButton(_stopButton, "Stop");
                    ConfigureButton(_refreshButton, "Refresh");
                    ConfigureButton(_uploadButton, "Upload FW");
                    ConfigureButton(_editButton, "Edit Flow");

                    left.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
                    left.Controls.Add(_status, 0, 0);
                    left.SetColumnSpan(_status, 2);
                    AddRow(left, 1, "Port", _portInput);
                    AddRow(left, 2, string.Empty, _runButton);
                    AddRow(left, 3, string.Empty, _stopButton);
                    AddRow(left, 4, string.Empty, _loopInput);
                    AddRow(left, 5, string.Empty, _refreshButton);
                    AddRow(left, 6, string.Empty, _uploadButton);
                    AddRow(left, 7, string.Empty, _editButton);
                    left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

                    var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
                    right.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
                    right.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
                    right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

                    _flow.Dock = DockStyle.Fill;
                    _flow.TraceDelayMs = 120;
                    _log.Dock = DockStyle.Fill;
                    _log.Multiline = true;
                    _log.ReadOnly = true;
                    _log.ScrollBars = ScrollBars.Vertical;
                    _log.Font = new Font("Consolas", 9F);
                    _log.BackColor = Color.FromArgb(30, 34, 37);
                    _log.ForeColor = Color.FromArgb(235, 238, 236);

                    right.Controls.Add(_flow, 0, 0);
                    right.Controls.Add(_log, 0, 2);
                    root.Controls.Add(left, 0, 0);
                    root.Controls.Add(right, 1, 0);
                    Controls.Add(root);
                }

                private void WireEvents()
                {
                    _runButton.Click += async (_, _) => await RunAsync();
                    _stopButton.Click += (_, _) => StopFlow();
                    _refreshButton.Click += async (_, _) => await RefreshAsync();
                    _uploadButton.Click += async (_, _) => await UploadFirmwareAsync();
                    _editButton.Click += (_, _) => EditFlow();
                    _portInput.SelectedIndexChanged += async (_, _) => await PortChangedAsync();
                    _board.Connected += (_, _) =>
                    {
                        AppendLog("Connected: " + _board.ConnectionDescription + (_board.DetectedChip is null ? string.Empty : " (" + _board.DetectedChip + ")"));
                        var mismatch = _board.DescribeMismatch(_flow.Document.BoardId);
                        if (mismatch is not null)
                            AppendLog(mismatch);
                        ShowConnection(); // a flow that repeats never returns to Run, so say it here
                    };
                    _board.Disconnected += (_, _) => AppendLog("Disconnected.");
                    _flow.FlowFileLoaded += (_, e) => OnFlowFileLoaded(e);
                    _flow.FlowFileFailed += (_, ex) =>
                    {
                        AppendLog(ex.Message);
                        SetStatus("The flow file has a problem. The flow that was loaded keeps running; the log says what to fix.", error: true);
                    };
                    _flow.FlowStarted += (_, _) => AppendLog("Flow started.");
                    _flow.FlowCompleted += (_, result) => AppendLog($"Flow completed. Nodes: {result.NodeOutputs.Count}");
                    _flow.FlowFailed += (_, ex) => AppendLog("Flow failed: " + ex.Message);
                    _flow.FlowTrace += (_, trace) =>
                    {
                        if (!(_stopRequested && trace.Kind == FlowExecutionEventKind.NodeFailed))
                            AppendLog($"{FormatTrace(trace.Kind)} {trace.NodeId}");
                    };
                    _flow.FlowStopped += (_, _) => UpdateState();
                }

                private void OnFlowFileLoaded(FlowFileEventArgs e)
                {
                    // The board the flow was made for decides the pins and the firmware.
                    _board.BoardId = _flow.Document.BoardId;
                    AppendLog((e.IsReload ? "Flow reloaded: " : "Flow loaded: ") + e.FilePath);
                    ShowDetection();

                    // Saved in the editor: run the new version.
                    if (e.IsReload && !_closing)
                        _ = RestartAsync();
                }

                private async Task RestartAsync()
                {
                    if (_flow.IsRunning)
                    {
                        StopFlow();
                        for (var i = 0; i < 60 && _flow.IsRunning; i++)
                            await Task.Delay(50);
                    }

                    await RunAsync(offerFirmware: false);
                }

                private void StopFlow()
                {
                    _stopRequested = true;
                    _flow.Stop();
                }

                private void EditFlow()
                {
                    if (!_flow.OpenFlowFile())
                        AppendLog("Open " + (_flow.ResolvedFlowFile ?? FlowFile) + " in Visual Studio to edit the flow.");
                }

                private void RefreshPorts()
                {
                    var previous = SelectedPort;
                    _populating = true;
                    try
                    {
                        _portInput.Items.Clear();
                        _portInput.Items.Add(new PortItem("Auto (recommended)", CodeBridgeBoardComponent.AutoPort));
                        _ports = BoardDiscovery.DiscoverPorts();
                        foreach (var port in _ports)
                            _portInput.Items.Add(new PortItem(port.ToString(), port.Name));
                        _portInput.Items.Add(new PortItem("Simulator (no board)", SimulatorPort));

                        _portInput.SelectedIndex = 0;
                        for (var i = 0; i < _portInput.Items.Count; i++)
                        {
                            if (_portInput.Items[i] is PortItem item && string.Equals(item.Value, previous, StringComparison.OrdinalIgnoreCase))
                                _portInput.SelectedIndex = i;
                        }
                    }
                    finally
                    {
                        _populating = false;
                    }

                    ShowDetection();
                }

                private async Task RefreshAsync()
                {
                    try
                    {
                        await _board.DisconnectAsync();
                    }
                    catch (Exception ex)
                    {
                        AppendLog("Could not release the board: " + ex.Message);
                    }

                    RefreshPorts();
                    UpdateState();
                }

                private async Task PortChangedAsync()
                {
                    if (_populating)
                        return;

                    try
                    {
                        await _board.DisconnectAsync();
                    }
                    catch (Exception ex)
                    {
                        AppendLog("Could not release the board: " + ex.Message);
                    }

                    ShowDetection();
                    UpdateState();
                }

                private async Task RunAsync(bool offerFirmware = true)
                {
                    if (_flow.IsRunning)
                        return;

                    if (_flow.ResolvedFlowFile is null)
                    {
                        SetStatus(FlowFile + " was not found. Add it to the project, or fix FlowFile in this form.", error: true);
                        return;
                    }

                    SyncConnectionSettings();
                    UpdateState(busy: true);
                    _stopRequested = false;
                    if (_board.IsConnected)
                        ShowConnection();
                    else
                        SetStatus("Connecting...");
                    try
                    {
                        await _board.RunAsync(_flow);
                        ShowConnection();
                    }
                    catch (BoardConnectionException ex)
                    {
                        if (_closing)
                            return;

                        AppendLog(ex.Message);
                        SetStatus(FirstLine(ex.Message), error: true);
                        if (ex.FirmwareMissing && offerFirmware && AskToUploadFirmware(ex.PortName))
                        {
                            if (await UploadFirmwareAsync())
                            {
                                await Task.Delay(2500); // the board restarts after the upload
                                await RunAsync(offerFirmware: false);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (_closing)
                            return;

                        // FlowFailed already wrote the details to the log.
                        SetStatus("The flow failed: " + FirstLine(ex.Message), error: true);
                    }
                    finally
                    {
                        UpdateState();
                    }
                }

                private async Task<bool> UploadFirmwareAsync()
                {
                    SyncConnectionSettings();
                    UpdateState(busy: true);
                    try
                    {
                        await _board.DisconnectAsync(); // the port must be free for the upload
                        SetStatus("Uploading the CodeBridge firmware... keep the board plugged in.");
                        AppendLog("Uploading the CodeBridge firmware...");
                        var result = await _board.UploadFirmwareAsync();
                        if (result.Success)
                        {
                            AppendLog("Firmware uploaded.");
                            SetStatus("Firmware uploaded. The board is restarting.");
                            return true;
                        }

                        AppendLog("Firmware upload failed." + Environment.NewLine + result.Log);
                        SetStatus("The firmware upload failed. The log has the details.", error: true);
                        return false;
                    }
                    catch (Exception ex)
                    {
                        if (_closing)
                            return false;

                        AppendLog("Firmware upload failed: " + ex.Message);
                        SetStatus(FirstLine(ex.Message), error: true);
                        return false;
                    }
                    finally
                    {
                        UpdateState();
                    }
                }

                private bool AskToUploadFirmware(string? port)
                {
                    var where = string.IsNullOrWhiteSpace(port) ? "The board" : "The board on " + port;
                    var answer = MessageBox.Show(
                        this,
                        where + " is not running the CodeBridge firmware yet." + Environment.NewLine + Environment.NewLine +
                        "Upload it now? This replaces the program on the board.",
                        "CodeBridge firmware",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);
                    return answer == DialogResult.Yes;
                }

                private void SyncConnectionSettings()
                {
                    _flow.ExecutionMode = _loopInput.Checked ? FlowExecutionMode.Loop : FlowExecutionMode.Trigger;

                    if (SelectedPort == SimulatorPort)
                    {
                        _board.TransportMode = CodeBridgeTransportMode.Simulator;
                        return;
                    }

                    _board.TransportMode = CodeBridgeTransportMode.Serial;
                    _board.PortName = SelectedPort;
                }

                private void ShowDetection()
                {
                    if (_board.IsConnected)
                    {
                        ShowConnection();
                        return;
                    }

                    if (SelectedPort == SimulatorPort)
                    {
                        SetStatus("Simulator: nothing is sent to a board. Press Run.");
                    }
                    else if (SelectedPort != CodeBridgeBoardComponent.AutoPort)
                    {
                        SetStatus("Port " + SelectedPort + " selected. Press Run.");
                    }
                    else
                    {
                        var boards = BoardDiscovery.RankCandidates(_ports);
                        SetStatus(boards.Length > 0
                            ? "Board found: " + boards[0] + ". Press Run."
                            : "No board found. Plug it in and press Refresh, or press Run to try the flow on the simulator.");
                    }
                }

                private void ShowConnection()
                {
                    if (_board.IsConnected)
                    {
                        var text = !_board.IsSimulated
                            ? "Connected to " + _board.ConnectionDescription + "."
                            : SelectedPort == SimulatorPort
                                ? "Running on the simulator."
                                : "No board found: running on the simulator.";

                        // The board says which chip it is: a flow made for another one would use pins that are not there.
                        var mismatch = _board.DescribeMismatch(_flow.Document.BoardId);
                        SetStatus(
                            mismatch is null ? text : text + " The flow is made for another board: the log says which.",
                            error: mismatch is not null);
                    }
                }

                private void UpdateState(bool busy = false)
                {
                    if (IsDisposed)
                        return;

                    var running = busy || _flow.IsRunning;
                    var simulator = SelectedPort == SimulatorPort;
                    _runButton.Enabled = !running;
                    _stopButton.Enabled = _flow.IsRunning;
                    _refreshButton.Enabled = !running;
                    _uploadButton.Enabled = !running && !simulator;
                    _portInput.Enabled = !running;
                    _loopInput.Enabled = !running;
                    _editButton.Enabled = !running;
                }

                private void SetStatus(string text, bool error = false)
                {
                    if (IsDisposed)
                        return;
                    _status.Text = text;
                    _status.ForeColor = error ? Color.Firebrick : Color.FromArgb(40, 44, 46);
                }

                private string SelectedPort => _portInput.SelectedItem is PortItem item ? item.Value : CodeBridgeBoardComponent.AutoPort;

                private static string FirstLine(string message)
                {
                    var end = message.IndexOfAny(new[] { '\r', '\n' });
                    return end < 0 ? message : message.Substring(0, end);
                }

                private static string FormatTrace(FlowExecutionEventKind kind)
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

                private static void AddRow(TableLayoutPanel panel, int row, string label, Control control)
                {
                    panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
                    panel.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
                    control.Dock = DockStyle.Fill;
                    control.Margin = new Padding(0, 3, 0, 3);
                    panel.Controls.Add(control, 1, row);
                }

                private static void ConfigureButton(Button button, string text)
                {
                    button.Text = text;
                    button.Height = 30;
                    button.Dock = DockStyle.Fill;
                    button.Margin = new Padding(0, 3, 0, 3);
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
                    // A flow that repeats writes a lot: keep the log from growing without end.
                    if (_log.TextLength > 100000)
                        _log.Text = _log.Text.Substring(_log.TextLength / 2);
                    _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
                }

                protected override async void OnFormClosing(FormClosingEventArgs e)
                {
                    _closing = true;
                    StopFlow();
                    try
                    {
                        await _board.DisposeAsync();
                    }
                    catch (Exception)
                    {
                        // closing anyway
                    }
                    base.OnFormClosing(e);
                }

                private sealed class PortItem
                {
                    public PortItem(string label, string value)
                    {
                        Label = label;
                        Value = value;
                    }

                    public string Label { get; }

                    public string Value { get; }

                    public override string ToString() => Label;
                }
            }
        }
        """;
}
