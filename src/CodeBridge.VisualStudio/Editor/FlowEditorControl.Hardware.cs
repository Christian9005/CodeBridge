#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CodeBridge.Flow;

namespace CodeBridge.VisualStudio.Editor
{
    /// <summary>
    /// Toolbar, status bar, output panel and everything that talks to the board through CodeBridge.FlowHost:
    /// board and port selection, connection test, firmware upload, run/stop and live validation.
    /// </summary>
    public partial class FlowEditorControl
    {
        private enum RunState
        {
            Idle,
            Testing,
            Uploading,
            Connecting,
            Running,
            Publishing
        }

        private sealed class BoardItem
        {
            public string Id { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public bool CanFlash { get; set; } = true;
            public string FlashTool { get; set; } = "esptool";
            public bool SupportsWifi { get; set; }
            public override string ToString() => DisplayName;
        }

        private sealed class PortItem
        {
            public string Name { get; set; } = string.Empty;
            public string? Description { get; set; }
            public string? SuggestedBoard { get; set; }
            public string? DeviceId { get; set; }
            public override string ToString() => string.IsNullOrEmpty(Description) ? Name : $"{Name}  ({Description})";
        }

        private static readonly Brush DotIdle = new SolidColorBrush(Color.FromRgb(122, 122, 122));
        private static readonly Brush DotBusy = new SolidColorBrush(Color.FromRgb(0, 122, 204));
        private static readonly Brush DotOk = new SolidColorBrush(Color.FromRgb(0, 168, 112));
        private static readonly Brush DotWarn = new SolidColorBrush(Color.FromRgb(214, 150, 20));
        private static readonly Brush DotError = new SolidColorBrush(Color.FromRgb(214, 55, 71));

        private readonly EditorSettings _settings = EditorSettings.Load();
        private readonly List<BoardItem> _boards = new List<BoardItem>
        {
            new BoardItem { Id = "esp32-devkit", DisplayName = "ESP32 DevKit", FlashTool = "esptool", SupportsWifi = true },
            new BoardItem { Id = "arduino-uno", DisplayName = "Arduino Uno", FlashTool = "Arduino CLI" }
        };

        private RunState _state = RunState.Idle;
        private HostProcess? _host;
        private bool _suppressBoardEvent;
        private bool _loadedOnce;
        private string _baseStatus = "Ready";
        private Brush _baseDot = DotIdle;
        private DispatcherTimer? _transientTimer;
        private DispatcherTimer? _validationTimer;
        private int _validationVersion;
        private string? _tempFlowPath;
        private bool _sawResult;
        private bool _lastResultSuccess;
        private string _lastConnectionSummary = string.Empty;
        private readonly Dictionary<string, List<(string Severity, string Message)>> _issuesByNode =
            new Dictionary<string, List<(string Severity, string Message)>>(StringComparer.OrdinalIgnoreCase);
        private int _errorCount;
        private int _warningCount;

        // ================================================================== setup

        private void InitializeChrome()
        {
            SetButton(RefreshPortsButton, IconKind.Refresh, null);
            SetButton(ConnectButton, IconKind.Connect, "Connect");
            SetButton(UploadButton, IconKind.Upload, "Upload Firmware");
            SetButton(WifiButton, IconKind.Wifi, "Wi-Fi");
            SetButton(HomeAssistantButton, IconKind.Home, "Home Assistant");
            SetButton(RunButton, IconKind.Run, "Run");
            SetButton(StopButton, IconKind.Stop, "Stop");
            SetButton(UndoButton, IconKind.Undo, null);
            SetButton(RedoButton, IconKind.Redo, null);
            SetButton(ArrangeButton, IconKind.Arrange, "Arrange");
            SetButton(ExportButton, IconKind.Export, "Export C#");
            SetButton(ZoomOutButton, IconKind.ZoomOut, null);
            SetButton(ZoomInButton, IconKind.ZoomIn, null);
            SetButton(FitButton, IconKind.Fit, null);
            SetButton(OutputButton, IconKind.Output, null);
            SetButton(BoardViewButton, IconKind.Board, "Pins");
            SetButton(MonitorButton, IconKind.Monitor, "Monitor");
            SetButton(ClearOutputButton, IconKind.Clear, null);

            BoardCombo.ItemsSource = _boards;
            BoardCombo.SelectionChanged += OnBoardSelectionChanged;
            PortCombo.DropDownOpened += (_, __) => { _ = RefreshPortsAsync(keepSelection: true); _ = DiscoverWifiBoardsAsync(); };
            PortCombo.LostKeyboardFocus += (_, __) => RememberPort();

            RefreshPortsButton.Click += (_, __) => { _ = RefreshPortsAsync(keepSelection: true); _ = DiscoverWifiBoardsAsync(); };
            ConnectButton.Click += (_, __) => TestConnection();
            UploadButton.Click += (_, __) => UploadFirmware();
            WifiButton.Click += (_, __) => ShowWifiSetup();
            HomeAssistantButton.Click += (_, __) => ShowHomeAssistant();
            RunButton.Click += (_, __) => RunFlow();
            StopButton.Click += (_, __) => StopHost();
            UndoButton.Click += (_, __) => Undo();
            RedoButton.Click += (_, __) => Redo();
            ArrangeButton.Click += (_, __) => ArrangeLayout();
            ExportButton.Click += (_, __) => ShowExportMenu();
            ZoomOutButton.Click += (_, __) => CanvasBorder.ZoomBy(1 / 1.2);
            ZoomInButton.Click += (_, __) => CanvasBorder.ZoomBy(1.2);
            FitButton.Click += (_, __) => FitView();
            OutputButton.Click += (_, __) => ShowOutput(OutputPanel.Visibility != Visibility.Visible);
            MonitorButton.Click += (_, __) => Monitor.SerialMonitorLauncher.TryOpen(GetPort());
            BoardViewButton.Click += (_, __) => ShowBoardView(BoardPanel.Visibility != Visibility.Visible);
            ClearOutputButton.Click += (_, __) => OutputText.Clear();

            InitializeToolbox();
            SelectBoardInCombo(_document.BoardId);
            RefreshBoardView();
            UpdateToolbarState();
        }

        private static void SetButton(Button button, IconKind icon, string? text)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var image = NativeUi.CreateIcon(icon);
            image.Width = 16;
            image.Height = 16;
            panel.Children.Add(image);
            if (!string.IsNullOrEmpty(text))
                panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(5, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center });

            button.Content = panel;

            // Icon-only buttons are named for screen readers from their tooltip.
            var name = !string.IsNullOrEmpty(text) ? text : button.ToolTip as string;
            if (!string.IsNullOrEmpty(name))
                System.Windows.Automation.AutomationProperties.SetName(button, name);
        }

        private void OnEditorLoaded()
        {
            if (_loadedOnce)
                return;

            _loadedOnce = true;
            OfferTourOnce();
            if (!HostClient.IsAvailable)
            {
                SetBaseStatus("Hardware tools unavailable (CodeBridge FlowHost not found)", DotWarn);
                AppendOutput("The CodeBridge FlowHost is missing from the extension, so board connection, firmware upload and Run are disabled. Reinstall CodeBridge Visual Studio Tools.");
                UpdateToolbarState();
                return;
            }

            _ = LoadBoardsAsync();
            _ = RefreshPortsAsync(keepSelection: false);
            _ = DiscoverWifiBoardsAsync();
            ScheduleValidation();
        }

        /// <summary>The first time a flow opens, show the CodeBridge Tour once (only marked as offered if it really opened).</summary>
        private void OfferTourOnce()
        {
            if (_settings.TourOffered)
                return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Tour.TourLauncher.TryOpen())
                {
                    _settings.TourOffered = true;
                    _settings.Save();
                }
            }), DispatcherPriority.ApplicationIdle);
        }

        private void OnEditorUnloaded()
        {
            _host?.Kill();
            _host = null;
            TryDelete(_tempFlowPath);
            _tempFlowPath = null;
        }

        // ================================================================== boards and ports

        private async System.Threading.Tasks.Task LoadBoardsAsync()
        {
            var messages = await HostClient.QueryAsync("boards");
            var message = messages.FirstOrDefault(m => m.Type == "boards");
            if (message == null)
                return;

            await Dispatcher.InvokeAsync(() =>
            {
                var loaded = message.List("boards")
                    .Select(b => new BoardItem
                    {
                        Id = b.Str("id") ?? string.Empty,
                        DisplayName = b.Str("displayName") ?? b.Str("id") ?? string.Empty,
                        CanFlash = b.Bool("canFlash"),
                        FlashTool = b.Str("flashTool") ?? "esptool",
                        SupportsWifi = b.Bool("supportsWifi")
                    })
                    .Where(b => b.Id.Length > 0)
                    .ToList();

                if (loaded.Count == 0)
                    return;

                _boards.Clear();
                _boards.AddRange(loaded);
                _suppressBoardEvent = true;
                BoardCombo.ItemsSource = null;
                BoardCombo.ItemsSource = _boards;
                _suppressBoardEvent = false;
                SelectBoardInCombo(_document.BoardId);
                UpdateToolbarState();
            });
        }

        private void SelectBoardInCombo(string? boardId)
        {
            _suppressBoardEvent = true;
            try
            {
                var item = _boards.FirstOrDefault(b => string.Equals(b.Id, boardId, StringComparison.OrdinalIgnoreCase)) ?? _boards.FirstOrDefault();
                BoardCombo.SelectedItem = item;
            }
            finally
            {
                _suppressBoardEvent = false;
            }
        }

        private BoardItem? CurrentBoardItem => BoardCombo.SelectedItem as BoardItem;

        private void OnBoardSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressBoardEvent || !(BoardCombo.SelectedItem is BoardItem board))
                return;

            if (string.Equals(board.Id, _document.BoardId, StringComparison.OrdinalIgnoreCase))
                return;

            SetBoard(board.Id);
        }

        /// <summary>Switches the flow to another board: the catalog (pins, blocks) changes, the diagram is kept.</summary>
        private void SetBoard(string boardId)
        {
            _document.BoardId = boardId;
            _catalog = FlowCatalog.ForBoard(boardId);
            RefreshBoardView();
            RebuildCanvasFromDocument();
            PopulateToolbox();
            UpdatePropertiesPanel();
            Commit();
            AppendOutput($"Board changed to {_catalog.DisplayName}. Pin options now follow this board.");
            UpdateToolbarState();
        }

        private void RebuildCanvasFromDocument()
        {
            GetDocument();
            var selectedIds = _selection.Select(n => n.NodeData.Id).ToList();

            _selection.Clear();
            EditorCanvas.Children.Clear();
            _nodeControlMap.Clear();
            _connectionPaths.Clear();
            foreach (var node in _document.Nodes)
                AddNodeControl(node, select: false);

            RebuildAllConnections();
            ApplySelection(selectedIds.Where(_nodeControlMap.ContainsKey).Select(id => _nodeControlMap[id]).ToList());
        }

        private async System.Threading.Tasks.Task RefreshPortsAsync(bool keepSelection)
        {
            if (!HostClient.IsAvailable)
                return;

            var remembered = keepSelection ? GetPort() : null;
            var messages = await HostClient.QueryAsync("ports");
            var message = messages.FirstOrDefault(m => m.Type == "ports");
            if (message == null)
                return;

            await Dispatcher.InvokeAsync(() =>
            {
                var ports = message.List("ports")
                    .Select(p => new PortItem { Name = p.Str("name") ?? string.Empty, Description = p.Str("description"), SuggestedBoard = p.Str("suggestedBoard") })
                    .Where(p => p.Name.Length > 0)
                    .ToList();

                _serialPorts = ports;
                ports = ports.Concat(_wifiPorts).ToList();

                var typed = PortCombo.Text;
                PortCombo.ItemsSource = ports;

                var pick = ports.FirstOrDefault(p => string.Equals(p.Name, remembered, StringComparison.OrdinalIgnoreCase))
                           ?? ports.FirstOrDefault(p => string.Equals(p.Name, _settings.LastPort, StringComparison.OrdinalIgnoreCase))
                           ?? ports.FirstOrDefault(p => string.Equals(p.SuggestedBoard, _document.BoardId, StringComparison.OrdinalIgnoreCase))
                           ?? ports.FirstOrDefault();

                if (pick != null && (string.IsNullOrWhiteSpace(typed) || !keepSelection))
                    PortCombo.SelectedItem = pick;
                else if (!string.IsNullOrWhiteSpace(typed))
                    PortCombo.Text = typed;

                UpdateToolbarState();
            });
        }

        private List<PortItem> _serialPorts = new List<PortItem>();
        private List<PortItem> _wifiPorts = new List<PortItem>();

        /// <summary>Looks for CodeBridge boards announcing themselves on the network and adds them to the Port list.</summary>
        private async System.Threading.Tasks.Task DiscoverWifiBoardsAsync()
        {
            if (!HostClient.IsAvailable)
                return;

            var messages = await HostClient.QueryAsync("wifi-boards");
            var message = messages.FirstOrDefault(m => m.Type == "wifi-boards");
            if (message == null)
                return;

            await Dispatcher.InvokeAsync(() =>
            {
                _wifiPorts = message.List("boards")
                    .Select(b => new PortItem
                    {
                        Name = b.Str("target") ?? string.Empty,
                        Description = $"Wi-Fi · {b.Str("name")} · firmware {b.Str("firmware") ?? "?"}",
                        SuggestedBoard = b.Str("suggestedBoard"),
                        DeviceId = b.Str("id")
                    })
                    .Where(p => p.Name.Length > 0)
                    .ToList();

                if (_wifiPorts.Count == 0)
                    return;

                var typed = PortCombo.Text;
                var selected = GetPort();
                PortCombo.ItemsSource = _serialPorts.Concat(_wifiPorts).ToList();
                PortCombo.Text = typed;
                if (!string.IsNullOrEmpty(selected))
                {
                    var match = _wifiPorts.FirstOrDefault(p => string.Equals(p.Name, selected, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                        PortCombo.SelectedItem = match;
                }

                AppendOutput($"Found {_wifiPorts.Count} board(s) on the network: " + string.Join(", ", _wifiPorts.Select(p => p.Name)));
            });
        }

        /// <summary>The port name (COM3) or host/IP typed or selected in the toolbar.</summary>
        private string GetPort()
        {
            if (PortCombo.SelectedItem is PortItem item && PortCombo.Text == item.ToString())
                return item.Name;

            var text = (PortCombo.Text ?? string.Empty).Trim();
            var space = text.IndexOf(' ');
            return space > 0 ? text.Substring(0, space) : text;
        }

        private void RememberPort()
        {
            var port = GetPort();
            if (port.Length > 0 && !string.Equals(port, _settings.LastPort, StringComparison.OrdinalIgnoreCase))
            {
                _settings.LastPort = port;
                _settings.Save();
            }
        }

        // ================================================================== host commands

        private bool RequireHost()
        {
            if (HostClient.IsAvailable)
                return true;

            ShowOutput(true);
            AppendOutput("The CodeBridge FlowHost is missing from the extension. Reinstall CodeBridge Visual Studio Tools.");
            return false;
        }

        private bool RequirePort(out string port)
        {
            port = GetPort();
            if (port.Length > 0)
                return true;

            ShowOutput(true);
            AppendOutput("Select a COM port (or type the board's IP address) in the toolbar first.");
            SetTransientStatus("Select a port first");
            return false;
        }

        private void TestConnection()
        {
            if (_state != RunState.Idle || !RequireHost() || !RequirePort(out var port))
                return;

            RememberPort();
            var board = CurrentBoardItem?.Id ?? _document.BoardId;
            StartHost($"test --board {board} --port {HostClient.Quote(port)}", RunState.Testing, $"Connecting to {port}...", TokenFor(port));
        }

        /// <summary>The saved pairing token when the port box holds a Wi-Fi address; USB ports need none.</summary>
        private string? TokenFor(string port)
        {
            if (BoardTokens.IsSerialPort(port))
                return null;

            // By address first; a board that got a new IP address is recognized by its identity (its MAC address).
            var token = BoardTokens.Get(port);
            if (token != null)
                return token;

            var found = _wifiPorts.FirstOrDefault(p => string.Equals(p.Name, port, StringComparison.OrdinalIgnoreCase));
            return found?.DeviceId == null ? null : BoardTokens.Get(found.DeviceId);
        }

        private void ShowWifiSetup()
        {
            if (_state != RunState.Idle || !RequireHost())
                return;

            var port = GetPort();
            if (port.Length == 0 || !BoardTokens.IsSerialPort(port))
            {
                ShowOutput(true);
                AppendOutput("Wi-Fi setup needs the board on USB: select its COM port in the toolbar first (it is only needed once).");
                SetTransientStatus("Select the USB port first");
                return;
            }

            if (CurrentBoardItem?.SupportsWifi != true)
            {
                ShowOutput(true);
                AppendOutput("Wi-Fi is available on the ESP32 boards only. The Arduino boards work over USB.");
                return;
            }

            var window = new WifiSetupWindow(port) { Owner = Window.GetWindow(this) };
            window.Paired += ip =>
            {
                PortCombo.Text = ip;
                RememberPort();
                AppendOutput($"Board paired over Wi-Fi: {ip}");
            };
            window.ShowDialog();
        }

        private void ShowHomeAssistant()
        {
            if (!RequireHost())
                return;

            var publishing = _state == RunState.Publishing;
            if (!publishing && _state != RunState.Idle)
                return;

            var inputs = _document.Nodes.Count(n => n.Type.StartsWith("gpio.", StringComparison.OrdinalIgnoreCase));
            var summary = _document.Nodes.Count == 0
                ? "The flow is empty: add Digital Output, PWM Output, Analog Read or Digital Read blocks first."
                : $"This flow has {inputs} GPIO block(s) to share.";
            var window = new HomeAssistantWindow(publishing, summary) { Owner = Window.GetWindow(this) };
            window.StopRequested += StopHost;
            window.PublishRequested += StartHomeAssistant;
            window.ShowDialog();
        }

        private void StartHomeAssistant(HomeAssistantSettings settings)
        {
            if (_state != RunState.Idle || !RequirePort(out var port))
                return;

            if (_document.Nodes.Count == 0)
            {
                ShowOutput(true);
                AppendOutput("The flow is empty. Add Digital Output, PWM Output, Analog Read or Digital Read blocks first.");
                return;
            }

            var path = WriteTempFlow();
            if (path == null)
                return;

            RememberPort();
            var board = CurrentBoardItem?.Id ?? _document.BoardId;
            StartHost(
                $"ha-bridge --flow {HostClient.Quote(path)} --board {board} --port {HostClient.Quote(port)}",
                RunState.Publishing,
                $"Publishing to Home Assistant ({settings.Broker})...",
                TokenFor(port),
                settings.ToHostJson());
        }

        private void UploadFirmware()
        {
            if (_state != RunState.Idle || !RequireHost() || !RequirePort(out var port))
                return;

            var board = CurrentBoardItem ?? _boards[0];

            // A Wi-Fi address instead of a COM port: update over the air.
            if (!BoardTokens.IsSerialPort(port) && !port.Equals("simulator", StringComparison.OrdinalIgnoreCase))
            {
                UpdateOverWifi(board, port);
                return;
            }

            if (!board.CanFlash)
            {
                ShowOutput(true);
                AppendOutput($"No firmware package was found for {board.DisplayName}. Reinstall CodeBridge Visual Studio Tools.");
                return;
            }

            var answer = MessageBox.Show(
                $"Install the CodeBridge firmware on {board.DisplayName} ({port})?\n\n" +
                "This replaces the program currently stored on the board. " +
                (board.FlashTool == "esptool"
                    ? "The ESP32 flashing tool (esptool) is downloaded automatically the first time."
                    : "Arduino CLI must be installed (it ships with Arduino IDE 2)."),
                "Upload CodeBridge firmware",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK)
                return;

            RememberPort();
            StartHost($"upload --board {board.Id} --port {HostClient.Quote(port)}", RunState.Uploading, $"Uploading firmware to {port}...");
        }

        private void UpdateOverWifi(BoardItem board, string host)
        {
            if (!board.SupportsWifi)
            {
                ShowOutput(true);
                AppendOutput($"{board.DisplayName} has no Wi-Fi. Pick its USB port to upload the firmware.");
                return;
            }

            var answer = MessageBox.Show(
                $"Update the firmware of {board.DisplayName} at {host} over Wi-Fi?\n\n" +
                "The board downloads the new firmware from this PC and restarts (about 30 seconds). " +
                "Windows may ask to allow this program through the firewall: allow it on private networks.",
                "Update firmware over Wi-Fi",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK)
                return;

            RememberPort();
            StartHost($"ota --board {board.Id} --host {HostClient.Quote(host)}", RunState.Uploading, $"Updating firmware over Wi-Fi ({host})...", TokenFor(host));
        }

        private void RunFlow()
        {
            if (_state != RunState.Idle || !RequireHost() || !RequirePort(out var port))
                return;

            if (_document.Nodes.Count == 0)
            {
                ShowOutput(true);
                AppendOutput("The flow is empty. Add a Manual Trigger and some blocks first.");
                return;
            }

            var path = WriteTempFlow();
            if (path == null)
                return;

            RememberPort();
            ResetExecutionBadges();
            BoardView.Reset();
            BoardView.SetSource(port.Equals("simulator", StringComparison.OrdinalIgnoreCase) ? "Simulator (virtual board)" : port);
            ShowBoardView(true);
            var board = CurrentBoardItem?.Id ?? _document.BoardId;
            var loop = LoopCheck.IsChecked == true ? " --loop --interval 1000" : string.Empty;
            StartHost($"run --flow {HostClient.Quote(path)} --board {board} --port {HostClient.Quote(port)} --trace-ms 60{loop}", RunState.Connecting, $"Connecting to {port}...", TokenFor(port));
        }

        private void StopHost()
        {
            if (_host == null)
                return;

            _stopRequested = true;
            AppendOutput("Stopping...");
            _host.Stop();
        }

        private bool _stopRequested;

        private void StartHost(string arguments, RunState state, string message, string? accessToken = null, string? stdinLine = null)
        {
            // The serial monitor keeps its port open; a flow, a connection test or a firmware upload needs the port to itself.
            var port = GetPort();
            if (port.Length > 0 && Monitor.SerialPortLease.Release(port))
            {
                AppendOutput($"The serial monitor was closed so {port} can be used here.");
                System.Threading.Thread.Sleep(400); // let the operating system release the port
            }

            _stopRequested = false;
            _sawResult = false;
            _lastResultSuccess = false;
            ShowOutput(true);
            AppendOutput(message);
            SetState(state, message);

            try
            {
                _host = HostClient.Start(
                    arguments,
                    m => Dispatcher.BeginInvoke(new Action(() => HandleHostMessage(m))),
                    (code, error) => Dispatcher.BeginInvoke(new Action(() => OnHostExit(code, error))),
                    accessToken,
                    stdinLine);
            }
            catch (Exception ex)
            {
                AppendOutput("ERROR: " + ex.Message);
                _host = null;
                SetState(RunState.Idle, "Ready");
            }
        }

        private void HandleHostMessage(HostMessage message)
        {
            switch (message.Type)
            {
                case "status":
                    SetBaseStatus(message.Str("message") ?? string.Empty, DotBusy);
                    break;

                case "log":
                    AppendOutput(message.Str("text") ?? string.Empty);
                    break;

                case "ha-entities":
                    AppendOutput("Shown in Home Assistant as device '" + message.Str("device") + "':");
                    foreach (var entity in message.List("entities"))
                        AppendOutput($"  {entity.Str("kind")}: {entity.Str("name")}");
                    break;

                case "connected":
                    _lastConnectionSummary = $"{message.Str("name")} · firmware {message.Str("firmware")}";
                    AppendOutput($"Connected to {message.Str("name")} (firmware {message.Str("firmware")}).");
                    if (_state == RunState.Testing)
                        SetBaseStatus($"Connected · {_lastConnectionSummary}", DotOk);
                    else
                        SetBaseStatus($"Running · {_lastConnectionSummary}", DotBusy);
                    break;

                case "ota-progress":
                    SetBaseStatus($"Updating over Wi-Fi... {message.Str("percent")}%", DotBusy);
                    break;

                case "pin":
                    if (int.TryParse(message.Str("pin"), out var pin) && double.TryParse(message.Str("value"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pinValue))
                        BoardView.Apply(pin, message.Str("kind") ?? string.Empty, pinValue);
                    break;

                case "iteration":
                    if (int.TryParse(message.Str("number"), out var iteration) && iteration > 1)
                        ResetExecutionBadges();
                    if (_state == RunState.Connecting)
                        _state = RunState.Running;
                    break;

                case "issue":
                    AppendOutput($"[{message.Str("severity")}] {message.Str("message")}");
                    break;

                case "node":
                    HandleNodeEvent(message);
                    break;

                case "result":
                    _sawResult = true;
                    _lastResultSuccess = message.Bool("success");
                    AppendOutput(message.Str("message") ?? string.Empty);
                    break;

                case "error":
                    _sawResult = true;
                    _lastResultSuccess = false;
                    AppendOutput("ERROR: " + message.Str("message"));
                    break;
            }
        }

        private void HandleNodeEvent(HostMessage message)
        {
            var nodeId = message.Str("nodeId") ?? string.Empty;
            if (!_nodeControlMap.TryGetValue(nodeId, out var control))
                return;

            var kind = message.Str("kind");
            var text = message.Str("message");
            var title = control.Definition?.DisplayName ?? message.Str("blockType") ?? nodeId;

            switch (kind)
            {
                case "nodeStarted":
                    control.SetStatus(NodeStatus.Running);
                    break;
                case "nodeCompleted":
                    control.SetStatus(NodeStatus.Completed);
                    var outputs = message.Child("outputs");
                    var debug = outputs?.Str("message");
                    if (!string.IsNullOrEmpty(debug))
                        AppendOutput($"{title}: {debug}");
                    else if (!string.IsNullOrEmpty(text))
                        AppendOutput($"{title}: {text}");
                    break;
                case "nodeSkipped":
                    control.SetStatus(NodeStatus.Skipped);
                    break;
                case "nodeFailed":
                    if (_stopRequested)
                    {
                        // Stop cancels the running block ("A task was canceled"): that is not a failure.
                        control.SetStatus(NodeStatus.Skipped);
                        break;
                    }

                    control.SetStatus(NodeStatus.Failed, text);
                    AppendOutput($"{title} failed: {text}");
                    break;
            }
        }

        private void OnHostExit(int code, string error)
        {
            var finishedState = _state;
            _host = null;

            if (!_sawResult && !string.IsNullOrWhiteSpace(error))
                AppendOutput("ERROR: " + HostClient.Friendly(error));
            else if (!_sawResult && code != 0)
                AppendOutput($"The CodeBridge FlowHost exited unexpectedly (code {code}).");

            var ok = _sawResult ? _lastResultSuccess || finishedState == RunState.Testing && code == 0 : code == 0;
            if (finishedState == RunState.Testing)
                ok = code == 0;

            _state = RunState.Idle;
            switch (finishedState)
            {
                case RunState.Testing:
                    SetBaseStatus(ok ? $"Connected · {_lastConnectionSummary}" : "Connection failed", ok ? DotOk : DotError);
                    break;
                case RunState.Uploading:
                    SetBaseStatus(ok ? "Firmware uploaded. Press Connect or Run." : "Firmware upload failed", ok ? DotOk : DotError);
                    break;
                case RunState.Publishing:
                    SetBaseStatus(ok ? "Stopped publishing to Home Assistant" : "Publishing to Home Assistant stopped with errors", ok ? DotIdle : DotError);
                    break;
                default:
                    SetBaseStatus(ok ? "Flow finished" : "Flow stopped with errors", ok ? DotOk : DotError);
                    break;
            }

            UpdateToolbarState();
            ScheduleValidation();
        }

        private void SetState(RunState state, string status)
        {
            _state = state;
            SetBaseStatus(status, state == RunState.Idle ? DotIdle : DotBusy);
            UpdateToolbarState();
        }

        // ================================================================== board view

        /// <summary>Redraws the board picture for the selected board (its pins come from the block catalog).</summary>
        private void RefreshBoardView()
        {
            var pinProperty = _catalog.Get("gpio.pin-mode")?.Properties.FirstOrDefault(p => p.Name == "pin");
            var pins = new List<(int, string)>();
            if (pinProperty?.Options != null)
            {
                foreach (var option in pinProperty.Options)
                {
                    try
                    {
                        pins.Add((Convert.ToInt32(option.Value, System.Globalization.CultureInfo.InvariantCulture), option.DisplayName));
                    }
                    catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
                    {
                        // an option that is not a pin number is not drawn
                    }
                }
            }

            var id = _document.BoardId ?? string.Empty;
            var arduino = id.StartsWith("arduino", StringComparison.OrdinalIgnoreCase);
            var led = arduino ? 13 : id == "esp32-devkit" ? 2 : -1;
            var analogMax = arduino ? 1023 : 4095;
            var chip = id.StartsWith("arduino-mega", StringComparison.OrdinalIgnoreCase) ? "ATmega2560"
                : arduino ? "ATmega328P"
                : id.Contains("s3") ? "ESP32-S3" : id.Contains("c3") ? "ESP32-C3" : "ESP32";
            BoardView.SetBoard(_catalog.DisplayName, chip, pins, led, analogMax);
        }

        private void ShowBoardView(bool show)
        {
            var visibility = show ? Visibility.Visible : Visibility.Collapsed;
            BoardPanel.Visibility = visibility;
            BoardSplitter.Visibility = visibility;
        }

        private void UpdateToolbarState()
        {
            var idle = _state == RunState.Idle;
            var host = HostClient.IsAvailable;

            BoardCombo.IsEnabled = idle;
            PortCombo.IsEnabled = idle && host;
            RefreshPortsButton.IsEnabled = idle && host;
            ConnectButton.IsEnabled = idle && host;
            UploadButton.IsEnabled = idle && host && (CurrentBoardItem?.CanFlash ?? true);
            WifiButton.IsEnabled = idle && host && CurrentBoardItem?.SupportsWifi == true;
            HomeAssistantButton.IsEnabled = host && (idle || _state == RunState.Publishing);
            RunButton.IsEnabled = idle && host;
            ArrangeButton.IsEnabled = idle;
            ExportButton.IsEnabled = idle && host;
            StopButton.IsEnabled = !idle;
            LoopCheck.IsEnabled = idle;

            var tip = host ? null : "Unavailable: the CodeBridge FlowHost is missing from the extension.";
            ConnectButton.ToolTip = tip ?? "Test the connection with the board and read its firmware version";
            UploadButton.ToolTip = tip ?? "Install the CodeBridge firmware on the board: over USB, or over Wi-Fi (no cable) when a network address is selected";
            RunButton.ToolTip = tip ?? "Run this flow on the board";
        }

        // ================================================================== validation & badges

        private void ScheduleValidation()
        {
            if (_restoring || !HostClient.IsAvailable)
                return;

            if (_validationTimer == null)
            {
                _validationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                _validationTimer.Tick += (s, e) =>
                {
                    _validationTimer!.Stop();
                    _ = ValidateAsync();
                };
            }

            _validationTimer.Stop();
            _validationTimer.Start();
        }

        private async System.Threading.Tasks.Task ValidateAsync()
        {
            if (_state != RunState.Idle)
                return;

            var path = WriteTempFlow();
            if (path == null)
                return;

            var version = Interlocked.Increment(ref _validationVersion);
            var messages = await HostClient.QueryAsync($"validate --flow {HostClient.Quote(path)} --board {_document.BoardId}");
            if (version != _validationVersion || _state != RunState.Idle)
                return;

            var validation = messages.FirstOrDefault(m => m.Type == "validation");
            if (validation == null)
                return;

            _issuesByNode.Clear();
            _errorCount = 0;
            _warningCount = 0;
            foreach (var issue in validation.List("issues"))
            {
                var severity = issue.Str("severity") ?? "info";
                var text = issue.Str("message") ?? string.Empty;
                if (severity == "error") _errorCount++;
                else if (severity == "warning") _warningCount++;

                var nodeId = issue.Str("nodeId");
                if (string.IsNullOrEmpty(nodeId))
                    continue;

                if (!_issuesByNode.TryGetValue(nodeId!, out var list))
                    _issuesByNode[nodeId!] = list = new List<(string, string)>();

                list.Add((severity, text));
            }

            ApplyStoredIssues();
            UpdateInfo();
        }

        private void ApplyStoredIssues()
        {
            foreach (var pair in _nodeControlMap)
            {
                var control = pair.Value;
                if (control.Definition == null)
                    continue; // unknown blocks keep their own warning

                if (_issuesByNode.TryGetValue(pair.Key, out var issues))
                {
                    var worst = issues.Any(i => i.Severity == "error") ? NodeStatus.Error : NodeStatus.Warning;
                    control.SetStatus(worst, string.Join("\n", issues.Select(i => i.Message)));
                }
                else if (control.Status == NodeStatus.Error || control.Status == NodeStatus.Warning)
                {
                    control.SetStatus(NodeStatus.None);
                }
            }
        }

        private void ResetExecutionBadges()
        {
            foreach (var control in _nodeControlMap.Values)
            {
                if (control.Definition == null)
                    continue;

                control.SetStatus(NodeStatus.None);
            }

            ApplyStoredIssues();
        }

        private string? WriteTempFlow()
        {
            try
            {
                if (_tempFlowPath == null)
                {
                    var directory = Path.Combine(Path.GetTempPath(), "CodeBridge");
                    Directory.CreateDirectory(directory);
                    _tempFlowPath = Path.Combine(directory, "flow-" + Guid.NewGuid().ToString("N") + ".cbflow");
                }

                File.WriteAllText(_tempFlowPath, JsonSerializer.Serialize(GetDocument(), new JsonSerializerOptions { WriteIndented = true }));
                return _tempFlowPath;
            }
            catch (Exception ex)
            {
                AppendOutput("ERROR: could not prepare the flow for the board: " + ex.Message);
                return null;
            }
        }

        private static void TryDelete(string? path)
        {
            try
            {
                if (path != null && File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // ================================================================== status bar & output

        private void SetBaseStatus(string text, Brush dot)
        {
            _baseStatus = text;
            _baseDot = dot;
            StatusText.Text = text;
            StatusDot.Fill = dot;
        }

        /// <summary>Shows a short message in the status bar that reverts after a few seconds.</summary>
        private void SetTransientStatus(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                StatusText.Text = _baseStatus;
                StatusDot.Fill = _baseDot;
                return;
            }

            StatusText.Text = text;
            if (_transientTimer == null)
            {
                _transientTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                _transientTimer.Tick += (s, e) =>
                {
                    _transientTimer!.Stop();
                    StatusText.Text = _baseStatus;
                    StatusDot.Fill = _baseDot;
                };
            }

            _transientTimer.Stop();
            _transientTimer.Start();
        }

        private void UpdateInfo()
        {
            if (InfoText == null)
                return;

            var parts = new List<string> { $"{_document.Nodes.Count} blocks", $"{_document.Connections.Count} wires" };
            if (_selection.Count > 0)
                parts.Add($"{_selection.Count} selected");

            if (_errorCount > 0)
                parts.Add($"{_errorCount} error{(_errorCount == 1 ? string.Empty : "s")}");

            if (_warningCount > 0)
                parts.Add($"{_warningCount} warning{(_warningCount == 1 ? string.Empty : "s")}");

            InfoText.Text = string.Join("  ·  ", parts);
            InfoText.Foreground = _errorCount > 0 ? DotError : (Brush)FindResource("VsGrayText");
            InfoText.ToolTip = _issuesByNode.Count == 0
                ? null
                : string.Join("\n", _issuesByNode.SelectMany(p => p.Value.Select(i => $"{NodeTitle(p.Key)}: {i.Message}")).Take(12));
        }

        private bool _usingVsOutput;

        /// <summary>
        /// Inside Visual Studio the messages go to the "CodeBridge" pane of the Output window; the embedded panel is only
        /// the fallback for hosts without one.
        /// </summary>
        private void ShowOutput(bool show)
        {
            if (show && VsOutput.TryShow())
            {
                _usingVsOutput = true;
                return;
            }

            if (_usingVsOutput)
            {
                // The Output window is shown by Visual Studio itself; the toolbar button only brings it forward.
                VsOutput.TryShow();
                return;
            }

            OutputPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            OutputSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            OutputRow.Height = show ? new GridLength(Math.Max(OutputRow.ActualHeight, 150)) : new GridLength(0);
        }

        private void AppendOutput(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            if (OutputText.Text.Length > 200_000)
                OutputText.Text = OutputText.Text.Substring(OutputText.Text.Length - 100_000);

            var line = $"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}";
            if (VsOutput.TryWrite(line))
            {
                _usingVsOutput = true;
                return;
            }

            OutputText.AppendText(line);
            OutputText.ScrollToEnd();
        }
    }
}
