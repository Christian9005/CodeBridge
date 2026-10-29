#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using CodeBridge.Flow;

namespace CodeBridge.VisualStudio.Editor
{
    /// <summary>
    /// Visual flow editor: canvas, selection, dragging, connections, undo/redo and clipboard.
    /// Toolbox, properties panel and board/hardware integration live in the other partial files.
    /// </summary>
    public partial class FlowEditorControl : UserControl
    {
        private const double Snap = 10;
        private const double PortHitRadius = 16;

        public event EventHandler? OnDirtyChanged;

        private FlowDocument _document = new FlowDocument();
        private FlowCatalog _catalog = FlowCatalog.ForBoard(null);

        private readonly Dictionary<string, FlowNodeControl> _nodeControlMap = new Dictionary<string, FlowNodeControl>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FlowConnectionPath> _connectionPaths = new Dictionary<string, FlowConnectionPath>(StringComparer.OrdinalIgnoreCase);
        private readonly List<FlowNodeControl> _selection = new List<FlowNodeControl>();
        private string? _selectedConnectionId;
        private bool _isDirty;
        private bool _fitOnLoad;
        private int _zCounter = 1;

        // node dragging
        private Dictionary<FlowNodeControl, Point>? _dragOrigins;
        private Point _dragMouseStart;
        private bool _nodeMoved;

        // wire dragging
        private FlowConnectionPath? _previewConnection;
        private string? _dragFromNodeId;
        private string? _dragFromPort;
        private FlowPortDirection _dragFromDirection;

        // rubber band
        private Rectangle? _rubberBand;
        private Point _rubberStart;
        private bool _rubberAdditive;

        // undo / redo
        private readonly List<string> _history = new List<string>();
        private int _historyIndex = -1;
        private bool _restoring;

        private int _pasteCount;
        private static string? _clipboardFallback;
        private const string ClipboardFormat = "CodeBridge.FlowNodes";

        public FlowEditorControl()
        {
            InitializeComponent();
            VsTheme.Bind(Resources);

            InitializeChrome();
            PopulateToolbox();
            UpdatePropertiesPanel();
            UpdateUndoRedoButtons();
            UpdateInfo();

            PreviewKeyDown += OnEditorKeyDown;
            Loaded += (_, __) => OnEditorLoaded();
            Unloaded += (_, __) => OnEditorUnloaded();
            SizeChanged += (_, __) => TryFitOnLoad();

            CanvasBorder.MouseLeftButtonDown += OnCanvasMouseLeftButtonDown;
            CanvasBorder.MouseMove += OnCanvasMouseMove;
            CanvasBorder.MouseLeftButtonUp += OnCanvasMouseLeftButtonUp;
            CanvasBorder.ZoomChanged += (_, __) => ZoomText.Text = Math.Round(CanvasBorder.Zoom * 100) + "%";
            CanvasBorder.ContextMenuOpening += OnCanvasContextMenuOpening;

            // The grid is drawn in the ZoomBorder background: move/scale it together with the canvas.
            CanvasBorder.AttachBackgroundTransform(GridBrush);
        }

        // ================================================================== document

        public void LoadDocument(FlowDocument doc)
        {
            _document = doc ?? new FlowDocument();
            _catalog = FlowCatalog.ForBoard(_document.BoardId);

            _selection.Clear();
            _selectedConnectionId = null;
            EditorCanvas.Children.Clear();
            _nodeControlMap.Clear();
            _connectionPaths.Clear();
            _rubberBand = null;
            _previewConnection = null;

            foreach (var node in _document.Nodes)
                AddNodeControl(node, select: false);

            RebuildAllConnections();
            SelectBoardInCombo(_document.BoardId);
            PopulateToolbox();
            UpdatePropertiesPanel();

            _isDirty = false;
            _history.Clear();
            _historyIndex = -1;
            PushHistory();
            UpdateInfo();

            _fitOnLoad = _document.Nodes.Count > 0;
            TryFitOnLoad();
            ScheduleValidation();
        }

        public FlowDocument GetDocument()
        {
            foreach (var pair in _nodeControlMap)
            {
                var left = Canvas.GetLeft(pair.Value);
                var top = Canvas.GetTop(pair.Value);
                if (!double.IsNaN(left) && !double.IsNaN(top))
                    pair.Value.NodeData.Position = new FlowPosition(left, top);
            }

            return _document;
        }

        /// <summary>Called by the editor pane after a successful save so later edits mark the document dirty again.</summary>
        public void ClearDirty() => _isDirty = false;

        private void MarkDirty()
        {
            if (_restoring)
                return;

            if (!_isDirty)
            {
                _isDirty = true;
                OnDirtyChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Call after every completed user change: records undo state, marks dirty and re-validates.</summary>
        private void Commit()
        {
            if (_restoring)
                return;

            PushHistory();
            MarkDirty();
            ResetExecutionBadges();
            ScheduleValidation();
            UpdateInfo();
        }

        private void TryFitOnLoad()
        {
            if (!_fitOnLoad || CanvasBorder.ActualWidth < 50 || CanvasBorder.ActualHeight < 50)
                return;

            _fitOnLoad = false;
            FitView();
        }

        public void FitView()
        {
            if (_nodeControlMap.Count == 0)
            {
                CanvasBorder.ResetZoom();
                return;
            }

            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;
            foreach (var node in _nodeControlMap.Values)
            {
                var x = Canvas.GetLeft(node);
                var y = Canvas.GetTop(node);
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x + node.BlockWidth);
                maxY = Math.Max(maxY, y + Math.Max(node.ActualHeight, 100));
            }

            CanvasBorder.FitTo(new Rect(minX, minY, maxX - minX, maxY - minY));
        }

        // ================================================================== nodes

        private FlowNodeControl AddNodeControl(FlowNode node, bool select = true)
        {
            var control = new FlowNodeControl(node, _catalog);
            Canvas.SetLeft(control, node.Position.X);
            Canvas.SetTop(control, node.Position.Y);

            control.NodeMouseDown += OnNodeMouseDown;
            control.NodeMouseMove += OnNodeMouseMove;
            control.NodeMouseUp += OnNodeMouseUp;
            control.PortDragStarted += OnPortDragStarted;
            control.PortDragDelta += OnPortDragDelta;
            control.PortDragCompleted += OnPortDragCompleted;
            control.PreviewMouseRightButtonDown += (s, e) =>
            {
                if (!_selection.Contains(control))
                    SelectOnly(control);
            };
            control.ContextMenu = BuildNodeMenu();

            _nodeControlMap[node.Id] = control;
            EditorCanvas.Children.Add(control);

            if (select)
                SelectOnly(control);

            return control;
        }

        private FlowNode CreateNode(string blockType, Point canvasPosition)
        {
            var definition = _catalog.Get(blockType);
            var node = new FlowNode
            {
                Id = "node-" + Guid.NewGuid().ToString().Substring(0, 8),
                Type = definition?.Type ?? blockType,
                Position = new FlowPosition(SnapValue(canvasPosition.X), SnapValue(canvasPosition.Y))
            };

            if (definition != null)
            {
                foreach (var property in definition.Properties)
                {
                    if (property.DefaultValue != null && !node.Parameters.ContainsKey(property.Name))
                        node.Parameters[property.Name] = property.DefaultValue;
                }
            }

            return node;
        }

        /// <summary>Adds a block of the given type with its top-left corner at the canvas position.</summary>
        internal FlowNodeControl AddBlock(string blockType, Point canvasPosition)
        {
            var node = CreateNode(blockType, canvasPosition);
            _document.Nodes.Add(node);
            var control = AddNodeControl(node);
            Commit();
            return control;
        }

        private void AddBlockAtViewportCenter(string blockType)
        {
            var center = CanvasBorder.ViewportCenterInCanvas();
            // Fan out repeated adds so blocks never stack exactly on top of each other.
            var offset = (_document.Nodes.Count % 6) * 24;
            AddBlock(blockType, new Point(center.X - FlowNodeControl.NodeWidth / 2 + offset, center.Y - 40 + offset));
        }

        private static double SnapValue(double value) =>
            Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt) ? value : Math.Round(value / Snap) * Snap;

        private void RemoveNodes(IEnumerable<FlowNodeControl> nodes)
        {
            foreach (var node in nodes.ToList())
            {
                var id = node.NodeData.Id;
                _document.Nodes.Remove(node.NodeData);
                _document.Connections.RemoveAll(c => c.FromNodeId == id || c.ToNodeId == id);
                EditorCanvas.Children.Remove(node);
                _nodeControlMap.Remove(id);
                _selection.Remove(node);
            }

            RebuildAllConnections();
        }

        // ================================================================== selection

        private void SelectOnly(FlowNodeControl? node)
        {
            _selectedConnectionId = null;
            ApplySelection(node == null ? new List<FlowNodeControl>() : new List<FlowNodeControl> { node });
        }

        private void ToggleSelect(FlowNodeControl node)
        {
            _selectedConnectionId = null;
            var next = _selection.ToList();
            if (!next.Remove(node))
                next.Add(node);

            ApplySelection(next);
        }

        private void ApplySelection(List<FlowNodeControl> next)
        {
            foreach (var node in _selection)
                node.SetSelected(false);

            _selection.Clear();
            _selection.AddRange(next);

            foreach (var node in _selection)
            {
                node.SetSelected(true);
                Panel.SetZIndex(node, ++_zCounter);
            }

            RefreshConnectionSelection();
            UpdatePropertiesPanel();
            UpdateInfo();
        }

        private void SelectConnection(string? connectionId)
        {
            foreach (var node in _selection)
                node.SetSelected(false);

            _selection.Clear();
            _selectedConnectionId = connectionId;
            RefreshConnectionSelection();
            UpdatePropertiesPanel();
            UpdateInfo();
        }

        private void RefreshConnectionSelection()
        {
            foreach (var pair in _connectionPaths)
            {
                var selected = string.Equals(pair.Key, _selectedConnectionId, StringComparison.OrdinalIgnoreCase);
                pair.Value.StrokeThickness = selected ? 3.5 : 2;
                pair.Value.Opacity = selected ? 1 : 0.85;
            }
        }

        public void SelectAll() => ApplySelection(_nodeControlMap.Values.ToList());

        /// <summary>True while a text box or combo box has keyboard focus (Edit commands then belong to that box).</summary>
        public bool IsTextInputFocused =>
            Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase || Keyboard.FocusedElement is ComboBox;

        public bool HasSelection => _selection.Count > 0 || _selectedConnectionId != null;

        // ================================================================== mouse: nodes

        private void OnNodeMouseDown(FlowNodeControl control, MouseButtonEventArgs e)
        {
            Focus();

            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
                ToggleSelect(control);
            else if (!_selection.Contains(control))
                SelectOnly(control);

            _dragOrigins = _selection.ToDictionary(n => n, n => new Point(Canvas.GetLeft(n), Canvas.GetTop(n)));
            _dragMouseStart = e.GetPosition(EditorCanvas);
            _nodeMoved = false;
        }

        private void OnNodeMouseMove(FlowNodeControl control, MouseEventArgs e)
        {
            if (_dragOrigins == null || _dragOrigins.Count == 0)
                return;

            var current = e.GetPosition(EditorCanvas);
            var dx = current.X - _dragMouseStart.X;
            var dy = current.Y - _dragMouseStart.Y;
            if (!_nodeMoved && Math.Abs(dx) < 3 && Math.Abs(dy) < 3)
                return;

            _nodeMoved = true;
            foreach (var pair in _dragOrigins)
            {
                Canvas.SetLeft(pair.Key, SnapValue(pair.Value.X + dx));
                Canvas.SetTop(pair.Key, SnapValue(pair.Value.Y + dy));
                UpdateNodeConnections(pair.Key);
            }
        }

        private void OnNodeMouseUp(FlowNodeControl control, MouseButtonEventArgs e)
        {
            var moved = _nodeMoved;
            _dragOrigins = null;
            _nodeMoved = false;
            if (moved)
            {
                GetDocument(); // sync positions into the model
                Commit();
            }
        }

        // ================================================================== mouse: canvas (selection rectangle, double click)

        private void OnCanvasMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (CanvasBorder.IsPanning || Keyboard.IsKeyDown(Key.Space))
                return;

            Focus();
            var position = e.GetPosition(EditorCanvas);

            if (e.ClickCount == 2)
            {
                ShowQuickAdd(position, e.GetPosition(CanvasBorder));
                e.Handled = true;
                return;
            }

            _rubberAdditive = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
            if (!_rubberAdditive)
                SelectOnly(null);

            _rubberStart = position;
            _rubberBand = new Rectangle
            {
                Stroke = (Brush)FindResource("VsHighlight"),
                StrokeThickness = 1,
                Fill = new SolidColorBrush(Color.FromArgb(40, 0, 122, 204)),
                Width = 0,
                Height = 0,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(_rubberBand, position.X);
            Canvas.SetTop(_rubberBand, position.Y);
            Panel.SetZIndex(_rubberBand, int.MaxValue);
            EditorCanvas.Children.Add(_rubberBand);
            CanvasBorder.CaptureMouse();
            e.Handled = true;
        }

        private void OnCanvasMouseMove(object sender, MouseEventArgs e)
        {
            if (_rubberBand == null)
                return;

            var position = e.GetPosition(EditorCanvas);
            Canvas.SetLeft(_rubberBand, Math.Min(position.X, _rubberStart.X));
            Canvas.SetTop(_rubberBand, Math.Min(position.Y, _rubberStart.Y));
            _rubberBand.Width = Math.Abs(position.X - _rubberStart.X);
            _rubberBand.Height = Math.Abs(position.Y - _rubberStart.Y);
        }

        private void OnCanvasMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_rubberBand == null)
                return;

            var rect = new Rect(Canvas.GetLeft(_rubberBand), Canvas.GetTop(_rubberBand), _rubberBand.Width, _rubberBand.Height);
            EditorCanvas.Children.Remove(_rubberBand);
            _rubberBand = null;
            CanvasBorder.ReleaseMouseCapture();

            if (rect.Width < 3 && rect.Height < 3)
                return;

            var hit = _nodeControlMap.Values
                .Where(n => rect.IntersectsWith(new Rect(Canvas.GetLeft(n), Canvas.GetTop(n), n.BlockWidth, Math.Max(n.ActualHeight, 60))))
                .ToList();

            var next = _rubberAdditive ? _selection.Union(hit).ToList() : hit;
            ApplySelection(next);
        }

        // ================================================================== connections

        private void OnPortDragStarted(FlowNodeControl nodeControl, string portName, FlowPortDirection direction, Point canvasPos)
        {
            _dragFromNodeId = nodeControl.NodeData.Id;
            _dragFromPort = portName;
            _dragFromDirection = direction;

            _previewConnection = new FlowConnectionPath
            {
                Stroke = (Brush)FindResource("VsHighlight"),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                StartPoint = direction == FlowPortDirection.Output ? canvasPos : canvasPos,
                EndPoint = canvasPos,
                IsHitTestVisible = false
            };

            EditorCanvas.Children.Add(_previewConnection);
            SetTransientStatus("Drop on a compatible port to connect");
        }

        private void OnPortDragDelta(FlowNodeControl nodeControl, string portName, FlowPortDirection direction, Point canvasPos)
        {
            if (_previewConnection == null)
                return;

            // Dragging from an input draws the wire backwards.
            if (_dragFromDirection == FlowPortDirection.Input)
            {
                _previewConnection.StartPoint = canvasPos;
                _previewConnection.EndPoint = nodeControl.GetPortPosition(portName, direction);
            }
            else
            {
                _previewConnection.EndPoint = canvasPos;
            }
        }

        private void OnPortDragCompleted(FlowNodeControl nodeControl, string portName, FlowPortDirection direction, Point canvasPos)
        {
            if (_previewConnection == null)
                return;

            EditorCanvas.Children.Remove(_previewConnection);
            _previewConnection = null;

            var wanted = _dragFromDirection == FlowPortDirection.Output ? FlowPortDirection.Input : FlowPortDirection.Output;
            var target = FindPortNear(canvasPos, wanted, nodeControl);
            if (target == null || _dragFromNodeId == null || _dragFromPort == null)
            {
                SetTransientStatus(string.Empty);
                return;
            }

            var fromIsOutput = _dragFromDirection == FlowPortDirection.Output;
            var error = TryConnect(
                fromIsOutput ? _dragFromNodeId : target.Value.Node.NodeData.Id,
                fromIsOutput ? _dragFromPort : target.Value.Port,
                fromIsOutput ? target.Value.Node.NodeData.Id : _dragFromNodeId,
                fromIsOutput ? target.Value.Port : _dragFromPort);

            SetTransientStatus(error ?? "Connected");
        }

        private (FlowNodeControl Node, string Port)? FindPortNear(Point canvasPoint, FlowPortDirection wanted, FlowNodeControl exclude)
        {
            (FlowNodeControl Node, string Port)? best = null;
            var bestDistance = PortHitRadius;
            foreach (var node in _nodeControlMap.Values)
            {
                if (node == exclude || node.Definition == null)
                    continue;

                var ports = wanted == FlowPortDirection.Input ? node.Definition.InputPorts : node.Definition.OutputPorts;
                foreach (var port in ports)
                {
                    var position = node.GetPortPosition(port.Name, wanted);
                    var distance = (position - canvasPoint).Length;
                    if (distance <= bestDistance)
                    {
                        bestDistance = distance;
                        best = (node, port.Name);
                    }
                }
            }

            return best;
        }

        /// <summary>Connects an output to an input. Returns an error message, or null when the wire was created.</summary>
        internal string? TryConnect(string fromNodeId, string fromPort, string toNodeId, string toPort)
        {
            if (fromNodeId == toNodeId)
                return "A block cannot connect to itself.";

            if (!_nodeControlMap.TryGetValue(fromNodeId, out var fromNode) || !_nodeControlMap.TryGetValue(toNodeId, out var toNode))
                return "Unknown block.";

            var output = fromNode.Definition?.FindPort(fromPort, FlowPortDirection.Output);
            var input = toNode.Definition?.FindPort(toPort, FlowPortDirection.Input);
            if (output == null || input == null)
                return "Connect an output port to an input port.";

            if (!BuiltInBlockCatalog.AreCompatible(output.ValueKind, input.ValueKind))
                return $"Incompatible ports: {output.ValueKind} cannot feed {input.ValueKind}.";

            if (_document.Connections.Any(c => c.FromNodeId == fromNodeId && c.FromPort == fromPort && c.ToNodeId == toNodeId && c.ToPort == toPort))
                return "Those ports are already connected.";

            if (WouldCreateCycle(fromNodeId, toNodeId))
                return "That connection would create a loop. Flows must run forward.";

            // An input accepts one wire: connecting again replaces the previous one.
            _document.Connections.RemoveAll(c => c.ToNodeId == toNodeId && string.Equals(c.ToPort, toPort, StringComparison.OrdinalIgnoreCase));

            _document.Connections.Add(new FlowConnection
            {
                FromNodeId = fromNodeId,
                FromPort = fromPort,
                ToNodeId = toNodeId,
                ToPort = toPort
            });

            RebuildAllConnections();
            Commit();
            return null;
        }

        private bool WouldCreateCycle(string fromNodeId, string toNodeId)
        {
            // Adding from -> to creates a cycle if `from` is already reachable from `to`.
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>();
            stack.Push(toNodeId);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (string.Equals(current, fromNodeId, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (!visited.Add(current))
                    continue;

                foreach (var connection in _document.Connections.Where(c => string.Equals(c.FromNodeId, current, StringComparison.OrdinalIgnoreCase)))
                    stack.Push(connection.ToNodeId);
            }

            return false;
        }

        private void UpdateNodeConnections(FlowNodeControl nodeControl)
        {
            var nodeId = nodeControl.NodeData.Id;
            foreach (var connection in _document.Connections)
            {
                if (connection.FromNodeId != nodeId && connection.ToNodeId != nodeId)
                    continue;

                if (!_connectionPaths.TryGetValue(connection.Id, out var path))
                    continue;

                if (_nodeControlMap.TryGetValue(connection.FromNodeId, out var from))
                    path.StartPoint = from.GetPortPosition(connection.FromPort, FlowPortDirection.Output);

                if (_nodeControlMap.TryGetValue(connection.ToNodeId, out var to))
                    path.EndPoint = to.GetPortPosition(connection.ToPort, FlowPortDirection.Input);
            }
        }

        /// <summary>Redraws every wire. Called on load and when the topology changes.</summary>
        private void RebuildAllConnections()
        {
            foreach (var path in _connectionPaths.Values)
                EditorCanvas.Children.Remove(path);

            _connectionPaths.Clear();

            // Drop wires whose blocks no longer exist; wires to unknown ports are kept in the file but not drawn.
            _document.Connections.RemoveAll(c => !_nodeControlMap.ContainsKey(c.FromNodeId) || !_nodeControlMap.ContainsKey(c.ToNodeId));

            foreach (var connection in _document.Connections)
            {
                var from = _nodeControlMap[connection.FromNodeId];
                var to = _nodeControlMap[connection.ToNodeId];
                if (!from.HasPort(connection.FromPort, FlowPortDirection.Output) || !to.HasPort(connection.ToPort, FlowPortDirection.Input))
                    continue;

                var kind = from.Definition!.FindPort(connection.FromPort, FlowPortDirection.Output)!.ValueKind;
                var path = new FlowConnectionPath
                {
                    ConnectionId = connection.Id,
                    StartPoint = from.GetPortPosition(connection.FromPort, FlowPortDirection.Output),
                    EndPoint = to.GetPortPosition(connection.ToPort, FlowPortDirection.Input),
                    Stroke = FlowNodeControl.PortBrush(kind),
                    StrokeThickness = 2,
                    Opacity = 0.85,
                    Cursor = Cursors.Hand,
                    ContextMenu = BuildConnectionMenu()
                };

                var id = connection.Id;
                path.MouseLeftButtonDown += (s, e) =>
                {
                    Focus();
                    SelectConnection(id);
                    e.Handled = true;
                };
                path.PreviewMouseRightButtonDown += (s, e) => SelectConnection(id);

                _connectionPaths[connection.Id] = path;
                EditorCanvas.Children.Insert(0, path);
            }

            RefreshConnectionSelection();
        }

        // ================================================================== auto layout

        /// <summary>Lays the blocks out left to right by execution depth, centred on their inputs, with even spacing.</summary>
        public void ArrangeLayout()
        {
            if (_nodeControlMap.Count == 0)
                return;

            GetDocument();
            var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int DepthOf(string id)
            {
                if (depth.TryGetValue(id, out var known))
                    return known;

                if (!visiting.Add(id))
                    return 0; // cycles are rejected when connecting; this only guards hand-edited files

                var parents = _document.Connections.Where(c => string.Equals(c.ToNodeId, id, StringComparison.OrdinalIgnoreCase)).Select(c => c.FromNodeId).ToList();
                var value = parents.Count == 0 ? 0 : parents.Max(DepthOf) + 1;
                visiting.Remove(id);
                depth[id] = value;
                return value;
            }

            foreach (var node in _document.Nodes)
                DepthOf(node.Id);

            const double left = 60, top = 60, columnGap = 70, rowGap = 28;
            // every column is as wide as the widest block, so a long title never runs into the next column
            var columnWidth = _nodeControlMap.Values.Select(n => n.BlockWidth).DefaultIfEmpty(FlowNodeControl.NodeWidth).Max();
            var placedY = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var layer in _document.Nodes.GroupBy(n => depth[n.Id]).OrderBy(g => g.Key))
            {
                // Order each column by the average position of its inputs (fewer crossings), then by current Y.
                var ordered = layer
                    .OrderBy(n =>
                    {
                        var parents = _document.Connections.Where(c => string.Equals(c.ToNodeId, n.Id, StringComparison.OrdinalIgnoreCase)).Select(c => c.FromNodeId).Where(placedY.ContainsKey).ToList();
                        return parents.Count == 0 ? n.Position.Y : parents.Average(p => placedY[p]);
                    })
                    .ThenBy(n => n.Position.Y)
                    .ToList();

                var cursor = top;
                foreach (var node in ordered)
                {
                    var control = _nodeControlMap[node.Id];
                    var wanted = _document.Connections
                        .Where(c => string.Equals(c.ToNodeId, node.Id, StringComparison.OrdinalIgnoreCase) && placedY.ContainsKey(c.FromNodeId))
                        .Select(c => placedY[c.FromNodeId]).DefaultIfEmpty(cursor).Average();
                    var y = Math.Max(cursor, wanted);
                    var x = left + layer.Key * (columnWidth + columnGap);

                    Canvas.SetLeft(control, x);
                    Canvas.SetTop(control, y);
                    placedY[node.Id] = y;
                    cursor = y + Math.Max(control.ActualHeight, 70) + rowGap;
                }
            }

            RebuildAllConnections();
            GetDocument();
            Commit();
            FitView();
        }

        // ================================================================== delete / clipboard

        public void DeleteSelection()
        {
            if (_selectedConnectionId != null)
            {
                var id = _selectedConnectionId;
                _selectedConnectionId = null;
                _document.Connections.RemoveAll(c => c.Id == id);
                RebuildAllConnections();
                UpdatePropertiesPanel();
                Commit();
                return;
            }

            if (_selection.Count == 0)
                return;

            RemoveNodes(_selection);
            UpdatePropertiesPanel();
            Commit();
        }

        private sealed class ClipboardPayload
        {
            public List<FlowNode> Nodes { get; set; } = new List<FlowNode>();
            public List<FlowConnection> Connections { get; set; } = new List<FlowConnection>();
        }

        public bool CanPaste
        {
            get
            {
                try
                {
                    return Clipboard.ContainsData(ClipboardFormat) || !string.IsNullOrEmpty(_clipboardFallback);
                }
                catch (Exception)
                {
                    return !string.IsNullOrEmpty(_clipboardFallback);
                }
            }
        }

        public void CopySelection()
        {
            if (_selection.Count == 0)
                return;

            GetDocument();
            var ids = new HashSet<string>(_selection.Select(n => n.NodeData.Id), StringComparer.OrdinalIgnoreCase);
            var payload = new ClipboardPayload
            {
                Nodes = _selection.Select(n => n.NodeData).ToList(),
                Connections = _document.Connections.Where(c => ids.Contains(c.FromNodeId) && ids.Contains(c.ToNodeId)).ToList()
            };

            var json = JsonSerializer.Serialize(payload);
            _clipboardFallback = json;
            _pasteCount = 0;
            try
            {
                var data = new DataObject();
                data.SetData(ClipboardFormat, json);
                data.SetText(json);
                Clipboard.SetDataObject(data, true);
            }
            catch (Exception)
            {
                // Clipboard locked by another process: the in-process copy still works.
            }
        }

        public void Cut()
        {
            CopySelection();
            DeleteSelection();
        }

        public void Paste()
        {
            string? json = null;
            try
            {
                if (Clipboard.ContainsData(ClipboardFormat))
                    json = Clipboard.GetData(ClipboardFormat) as string;
            }
            catch (Exception)
            {
                // fall back below
            }

            json ??= _clipboardFallback;
            if (string.IsNullOrEmpty(json))
                return;

            ClipboardPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<ClipboardPayload>(json!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException)
            {
                return;
            }

            if (payload == null || payload.Nodes.Count == 0)
                return;

            _pasteCount++;
            var offset = 30 * _pasteCount;
            var idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var created = new List<FlowNodeControl>();

            foreach (var source in payload.Nodes)
            {
                var copy = new FlowNode
                {
                    Id = "node-" + Guid.NewGuid().ToString().Substring(0, 8),
                    Type = source.Type,
                    Position = new FlowPosition(source.Position.X + offset, source.Position.Y + offset)
                };
                foreach (var parameter in source.Parameters)
                    copy.Parameters[parameter.Key] = parameter.Value;

                idMap[source.Id] = copy.Id;
                _document.Nodes.Add(copy);
                created.Add(AddNodeControl(copy, select: false));
            }

            foreach (var connection in payload.Connections)
            {
                if (idMap.TryGetValue(connection.FromNodeId, out var from) && idMap.TryGetValue(connection.ToNodeId, out var to))
                {
                    _document.Connections.Add(new FlowConnection
                    {
                        FromNodeId = from,
                        FromPort = connection.FromPort,
                        ToNodeId = to,
                        ToPort = connection.ToPort
                    });
                }
            }

            RebuildAllConnections();
            ApplySelection(created);
            Commit();
        }

        public void DuplicateSelection()
        {
            if (_selection.Count == 0)
                return;

            var previous = _clipboardFallback;
            CopySelection();
            _pasteCount = 0;
            Paste();
            _clipboardFallback = previous ?? _clipboardFallback;
        }

        // ================================================================== undo / redo

        public bool CanUndo => _historyIndex > 0;
        public bool CanRedo => _historyIndex >= 0 && _historyIndex < _history.Count - 1;

        private string Snapshot() => JsonSerializer.Serialize(GetDocument());

        private void PushHistory()
        {
            if (_restoring)
                return;

            var snapshot = Snapshot();
            if (_historyIndex >= 0 && _historyIndex < _history.Count && _history[_historyIndex] == snapshot)
                return;

            if (_historyIndex < _history.Count - 1)
                _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);

            _history.Add(snapshot);
            if (_history.Count > 100)
                _history.RemoveAt(0);

            _historyIndex = _history.Count - 1;
            UpdateUndoRedoButtons();
        }

        public void Undo()
        {
            if (CanUndo)
                Restore(--_historyIndex);
        }

        public void Redo()
        {
            if (CanRedo)
                Restore(++_historyIndex);
        }

        private void Restore(int index)
        {
            var document = JsonSerializer.Deserialize<FlowDocument>(_history[index], new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (document == null)
                return;

            var selectedIds = _selection.Select(n => n.NodeData.Id).ToList();
            var boardChanged = !string.Equals(document.BoardId, _document.BoardId, StringComparison.OrdinalIgnoreCase);

            _restoring = true;
            try
            {
                var zoomState = CanvasBorder.Zoom;
                _document = document;
                _catalog = FlowCatalog.ForBoard(_document.BoardId);
                _selection.Clear();
                _selectedConnectionId = null;
                EditorCanvas.Children.Clear();
                _nodeControlMap.Clear();
                _connectionPaths.Clear();
                foreach (var node in _document.Nodes)
                    AddNodeControl(node, select: false);

                RebuildAllConnections();
                ApplySelection(selectedIds.Where(_nodeControlMap.ContainsKey).Select(id => _nodeControlMap[id]).ToList());
                SelectBoardInCombo(_document.BoardId);
                if (boardChanged)
                    PopulateToolbox();
            }
            finally
            {
                _restoring = false;
            }

            _isDirty = false;
            MarkDirty();
            UpdateUndoRedoButtons();
            ScheduleValidation();
            UpdateInfo();
        }

        private void UpdateUndoRedoButtons()
        {
            UndoButton.IsEnabled = CanUndo;
            RedoButton.IsEnabled = CanRedo;
        }

        // ================================================================== keyboard

        private void OnEditorKeyDown(object sender, KeyEventArgs e)
        {
            // Let text boxes (properties, search, port) keep their own editing shortcuts.
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase || Keyboard.FocusedElement is ComboBox)
                return;

            var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            switch (e.Key)
            {
                case Key.Delete:
                case Key.Back:
                    DeleteSelection();
                    break;
                case Key.Z when control && !shift:
                    Undo();
                    break;
                case Key.Y when control:
                case Key.Z when control && shift:
                    Redo();
                    break;
                case Key.C when control:
                    CopySelection();
                    break;
                case Key.X when control:
                    Cut();
                    break;
                case Key.V when control:
                    Paste();
                    break;
                case Key.D when control:
                    DuplicateSelection();
                    break;
                case Key.A when control:
                    SelectAll();
                    break;
                case Key.D0 when control:
                case Key.NumPad0 when control:
                    CanvasBorder.ResetZoom();
                    break;
                case Key.Space when control:
                    ShowQuickAdd(CanvasBorder.ViewportCenterInCanvas(), new Point(CanvasBorder.ActualWidth / 2, CanvasBorder.ActualHeight / 2));
                    break;
                case Key.Escape:
                    SelectOnly(null);
                    break;
                case Key.Left:
                case Key.Right:
                case Key.Up:
                case Key.Down:
                    NudgeSelection(e.Key, shift ? 50 : Snap);
                    break;
                default:
                    return;
            }

            e.Handled = true;
        }

        private void NudgeSelection(Key key, double step)
        {
            if (_selection.Count == 0)
                return;

            double dx = key == Key.Left ? -step : key == Key.Right ? step : 0;
            double dy = key == Key.Up ? -step : key == Key.Down ? step : 0;
            foreach (var node in _selection)
            {
                Canvas.SetLeft(node, Canvas.GetLeft(node) + dx);
                Canvas.SetTop(node, Canvas.GetTop(node) + dy);
                UpdateNodeConnections(node);
            }

            Commit();
        }

        // ================================================================== context menus

        private ContextMenu BuildNodeMenu()
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("Duplicate", "Ctrl+D", DuplicateSelection));
            menu.Items.Add(Item("Copy", "Ctrl+C", CopySelection));
            menu.Items.Add(Item("Cut", "Ctrl+X", Cut));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Delete", "Del", DeleteSelection));
            return menu;
        }

        private ContextMenu BuildConnectionMenu()
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("Delete connection", "Del", DeleteSelection));
            return menu;
        }

        private void OnCanvasContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && FindParent<FlowNodeControl>(source) != null)
                return;

            var position = Mouse.GetPosition(EditorCanvas);
            var viewportPosition = Mouse.GetPosition(CanvasBorder);
            var menu = new ContextMenu();
            menu.Items.Add(Item("Add block...", "Ctrl+Space", () => ShowQuickAdd(position, viewportPosition)));
            var paste = Item("Paste", "Ctrl+V", Paste);
            paste.IsEnabled = CanPaste;
            menu.Items.Add(paste);
            menu.Items.Add(Item("Select all", "Ctrl+A", SelectAll));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Fit to view", null, FitView));
            menu.Items.Add(Item("Reset zoom", "Ctrl+0", CanvasBorder.ResetZoom));
            CanvasBorder.ContextMenu = menu;
        }

        private static MenuItem Item(string header, string? shortcut, Action action)
        {
            var item = new MenuItem { Header = header, InputGestureText = shortcut };
            item.Click += (s, e) => action();
            return item;
        }

        // ================================================================== helpers

        private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T match)
                    return match;

                child = child is Visual || child is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(child)
                    : LogicalTreeHelper.GetParent(child);
            }

            return null;
        }
    }
}
