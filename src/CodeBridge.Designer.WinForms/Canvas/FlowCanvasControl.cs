using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Text.Json;
using CodeBridge.Flow;

namespace CodeBridge.Designer.WinForms.Canvas;

[ToolboxItem(false)]
[DesignTimeVisible(false)]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class FlowCanvasControl : Control
{
    private const int NodeWidth = 206;
    private const int HeaderHeight = 34;
    private const int PortRowHeight = 20;
    private const int NodePadding = 12;
    private const int PortRadius = 5;
    private const int NodeCornerRadius = 8;
    private const float MinimumZoom = 0.35F;
    private const float MaximumZoom = 2F;

    private FlowBlockCatalog _catalog;
    private FlowNode? _draggingNode;
    private PointF _dragOffset;
    private PortHit? _connectingFrom;
    private PointF _connectionPreview;
    private PointF _viewOffset = new(0, 0);
    private float _zoom = 1F;
    private bool _isPanning;
    private Point _lastPanPoint;
    private readonly ToolTip _toolTip = new() { InitialDelay = 250, ReshowDelay = 100, AutoPopDelay = 5000 };
    private string? _hoverPortKey;
    private readonly Dictionary<string, NodeExecutionVisualState> _executionStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _executionMessages = new(StringComparer.OrdinalIgnoreCase);

    public FlowDocument Document { get; private set; } = new() { Name = "Untitled Flow" };
    public FlowNode? SelectedNode { get; private set; }

    public event EventHandler? SelectedNodeChanged;
    public event EventHandler? DocumentChanged;
    public event EventHandler<FlowNode>? NodeDoubleClicked;

    public FlowCanvasControl()
        : this(BuiltInBlockCatalog.Create())
    {
    }

    public FlowCanvasControl(FlowBlockCatalog catalog)
    {
        _catalog = catalog;
        AllowDrop = true;
        BackColor = DesignerTheme.Canvas;
        Font = DesignerTheme.UiFont;
        TabStop = true;

        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
    }

    public void SetCatalog(FlowBlockCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Invalidate();
    }

    public void SetDocument(FlowDocument document)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        ResetExecutionStates();
        SelectNode(null);
        Invalidate();
        OnDocumentChanged();
    }

    public void FitDocumentToView()
    {
        var contentBounds = GetDocumentBounds();
        if (contentBounds.IsEmpty || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            _zoom = 1F;
            _viewOffset = new PointF(0, 0);
            Invalidate();
            return;
        }

        const int margin = 44;
        var availableWidth = Math.Max(1, ClientSize.Width - margin * 2);
        var availableHeight = Math.Max(1, ClientSize.Height - margin * 2);
        var fitZoom = Math.Min(
            availableWidth / (float)Math.Max(1, contentBounds.Width),
            availableHeight / (float)Math.Max(1, contentBounds.Height));

        _zoom = Math.Clamp(fitZoom, MinimumZoom, 1F);
        _viewOffset = new PointF(
            (ClientSize.Width - contentBounds.Width * _zoom) / 2F - contentBounds.Left * _zoom,
            (ClientSize.Height - contentBounds.Height * _zoom) / 2F - contentBounds.Top * _zoom);
        Invalidate();
    }

    public void ZoomIn() => ZoomBy(1.12F, new PointF(ClientSize.Width / 2F, ClientSize.Height / 2F));

    public void ZoomOut() => ZoomBy(1F / 1.12F, new PointF(ClientSize.Width / 2F, ClientSize.Height / 2F));

    public void ResetZoom()
    {
        var centerBefore = ToDocument(new PointF(ClientSize.Width / 2F, ClientSize.Height / 2F));
        _zoom = 1F;
        _viewOffset = new PointF(
            ClientSize.Width / 2F - centerBefore.X * _zoom,
            ClientSize.Height / 2F - centerBefore.Y * _zoom);
        Invalidate();
    }

    public Point GetVisibleDocumentCenter()
    {
        var point = ToDocument(new PointF(ClientSize.Width / 2F, ClientSize.Height / 2F));
        return new Point((int)Math.Max(0, point.X), (int)Math.Max(0, point.Y));
    }

    public FlowNode AddNode(FlowBlockDefinition definition, Point location)
    {
        var node = new FlowNode
        {
            Id = CreateNodeId(definition.Type),
            Type = definition.Type,
            Position = new FlowPosition(location.X, location.Y)
        };

        foreach (var property in definition.Properties.Where(property => property.DefaultValue is not null))
            node.Parameters[property.Name] = property.DefaultValue;

        Document.Nodes.Add(node);
        SelectNode(node);
        Invalidate();
        OnDocumentChanged();
        return node;
    }

    public void AddSnippet(
        IEnumerable<FlowNode> nodes,
        IEnumerable<FlowConnection> connections)
    {
        var nodeList = nodes.ToList();
        if (nodeList.Count == 0)
            return;

        Document.Nodes.AddRange(nodeList);
        Document.Connections.AddRange(connections);
        ResetExecutionStates();
        SelectNode(nodeList[0]);
        Invalidate();
        OnDocumentChanged();
    }

    public void DeleteSelectedNode()
    {
        if (SelectedNode is null)
            return;

        var selectedId = SelectedNode.Id;
        Document.Nodes.RemoveAll(node => string.Equals(node.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        Document.Connections.RemoveAll(connection =>
            string.Equals(connection.FromNodeId, selectedId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(connection.ToNodeId, selectedId, StringComparison.OrdinalIgnoreCase));
        _executionStates.Remove(selectedId);
        _executionMessages.Remove(selectedId);

        SelectNode(null);
        Invalidate();
        OnDocumentChanged();
    }

    public void ResetExecutionStates()
    {
        _executionStates.Clear();
        _executionMessages.Clear();
        Invalidate();
    }

    public void SetNodeExecutionState(string nodeId, NodeExecutionVisualState state, string? message = null)
    {
        if (state == NodeExecutionVisualState.Idle)
        {
            _executionStates.Remove(nodeId);
            _executionMessages.Remove(nodeId);
        }
        else
        {
            _executionStates[nodeId] = state;
            if (!string.IsNullOrWhiteSpace(message))
                _executionMessages[nodeId] = message;
            else
                _executionMessages.Remove(nodeId);
        }

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        DrawGrid(e.Graphics);
        if (!Document.Nodes.Any())
            DrawEmptyState(e.Graphics);

        var state = e.Graphics.Save();
        e.Graphics.TranslateTransform(_viewOffset.X, _viewOffset.Y);
        e.Graphics.ScaleTransform(_zoom, _zoom);
        DrawConnections(e.Graphics);
        DrawConnectionPreview(e.Graphics);
        DrawNodes(e.Graphics);
        e.Graphics.Restore(state);
        DrawViewportBadge(e.Graphics);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();

        if (e.Button != MouseButtons.Left)
        {
            if (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right)
            {
                _isPanning = true;
                _lastPanPoint = e.Location;
                Cursor = Cursors.SizeAll;
                Capture = true;
                return;
            }

            base.OnMouseDown(e);
            return;
        }

        var documentLocation = ToDocument(e.Location);
        var outputPort = HitTestPort(documentLocation, FlowPortDirection.Output);
        if (outputPort is not null)
        {
            _connectingFrom = outputPort;
            _connectionPreview = documentLocation;
            SelectNode(outputPort.Value.Node);
            Invalidate();
            return;
        }

        var node = HitTestNode(documentLocation);
        if (node is not null)
        {
            _draggingNode = node;
            _dragOffset = new PointF(
                documentLocation.X - (float)node.Position.X,
                documentLocation.Y - (float)node.Position.Y);
            SelectNode(node);
            return;
        }

        SelectNode(null);
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            var node = HitTestNode(ToDocument(e.Location));
            if (node is not null)
            {
                SelectNode(node);
                NodeDoubleClicked?.Invoke(this, node);
                return;
            }
        }

        base.OnMouseDoubleClick(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_isPanning)
        {
            var dx = e.X - _lastPanPoint.X;
            var dy = e.Y - _lastPanPoint.Y;
            _viewOffset = new PointF(_viewOffset.X + dx, _viewOffset.Y + dy);
            _lastPanPoint = e.Location;
            Invalidate();
            return;
        }

        if (_draggingNode is not null)
        {
            var documentLocation = ToDocument(e.Location);
            _draggingNode.Position = new FlowPosition(
                Math.Max(0, documentLocation.X - _dragOffset.X),
                Math.Max(0, documentLocation.Y - _dragOffset.Y));
            Invalidate();
            OnDocumentChanged();
            return;
        }

        if (_connectingFrom is not null)
        {
            _connectionPreview = ToDocument(e.Location);
            Invalidate();
            return;
        }

        var hoverLocation = ToDocument(e.Location);
        var hoverPort = HitTestPort(hoverLocation, null);
        UpdatePortToolTip(hoverPort, e.Location);

        Cursor = hoverPort is not null
            ? Cursors.Cross
            : HitTestNode(hoverLocation) is not null
                ? Cursors.SizeAll
                : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            Capture = false;
            Cursor = Cursors.Default;
            return;
        }

        if (_draggingNode is not null)
        {
            _draggingNode = null;
            return;
        }

        if (_connectingFrom is not null)
        {
            var target = HitTestPort(ToDocument(e.Location), FlowPortDirection.Input);
            if (target is not null && CanConnect(_connectingFrom.Value, target.Value))
            {
                Document.Connections.Add(new FlowConnection
                {
                    Id = $"wire-{Document.Connections.Count + 1}",
                    FromNodeId = _connectingFrom.Value.Node.Id,
                    FromPort = _connectingFrom.Value.Port.Name,
                    ToNodeId = target.Value.Node.Id,
                    ToPort = target.Value.Port.Name
                });
                OnDocumentChanged();
            }

            _connectingFrom = null;
            Invalidate();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) == Keys.Control)
        {
            ZoomBy(e.Delta > 0 ? 1.12F : 1F / 1.12F, e.Location);
            return;
        }

        var horizontal = (ModifierKeys & Keys.Shift) == Keys.Shift;
        const float scrollStep = 48F;
        _viewOffset = horizontal
            ? new PointF(_viewOffset.X + Math.Sign(e.Delta) * scrollStep, _viewOffset.Y)
            : new PointF(_viewOffset.X, _viewOffset.Y + Math.Sign(e.Delta) * scrollStep);
        Invalidate();
    }

    protected override void OnDragEnter(DragEventArgs drgevent)
    {
        drgevent.Effect = drgevent.Data?.GetDataPresent(DataFormats.Text) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        var blockType = drgevent.Data?.GetData(DataFormats.Text) as string;
        if (blockType is null || !_catalog.TryGet(blockType, out var definition))
            return;

        var clientPoint = PointToClient(new Point(drgevent.X, drgevent.Y));
        var documentPoint = ToDocument(clientPoint);
        AddNode(definition, new Point((int)documentPoint.X, (int)documentPoint.Y));
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Delete)
        {
            DeleteSelectedNode();
            return true;
        }

        if (keyData is (Keys.Control | Keys.Oemplus) or (Keys.Control | Keys.Add))
        {
            ZoomIn();
            return true;
        }

        if (keyData is (Keys.Control | Keys.OemMinus) or (Keys.Control | Keys.Subtract))
        {
            ZoomOut();
            return true;
        }

        if (keyData == (Keys.Control | Keys.D0))
        {
            ResetZoom();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void DrawGrid(Graphics graphics)
    {
        using var minorBrush = new SolidBrush(DesignerTheme.GridMinor);
        using var majorPen = new Pen(DesignerTheme.GridMajor);
        var minor = Math.Max(8F, 20F * _zoom);
        var major = Math.Max(minor, 100F * _zoom);

        var startX = _viewOffset.X % minor;
        if (startX > 0)
            startX -= minor;

        var startY = _viewOffset.Y % minor;
        if (startY > 0)
            startY -= minor;

        for (var x = startX; x < Width; x += minor)
        {
            for (var y = startY; y < Height; y += minor)
                graphics.FillRectangle(minorBrush, x, y, 1F, 1F);
        }

        var majorStartX = _viewOffset.X % major;
        if (majorStartX > 0)
            majorStartX -= major;

        var majorStartY = _viewOffset.Y % major;
        if (majorStartY > 0)
            majorStartY -= major;

        for (var x = majorStartX; x < Width; x += major)
            graphics.DrawLine(majorPen, x, 0, x, Height);

        for (var y = majorStartY; y < Height; y += major)
            graphics.DrawLine(majorPen, 0, y, Width, y);
    }

    private void DrawCanvasWatermark(Graphics graphics)
    {
        if (Document.Nodes.Any())
            return;

        using var border = new Pen(DesignerTheme.BorderSubtle);
        var bounds = new Rectangle(22, 22, Width - 44, Height - 44);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        graphics.DrawRectangle(border, bounds);
    }

    private void DrawEmptyState(Graphics graphics)
    {
        DrawCanvasWatermark(graphics);
        var bounds = new Rectangle(0, 0, Width, Height);
        TextRenderer.DrawText(
            graphics,
            "No blocks on canvas",
            DesignerTheme.TitleFont,
            new Rectangle(bounds.X, bounds.Y + bounds.Height / 2 - 24, bounds.Width, 24),
            DesignerTheme.MutedText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(
            graphics,
            "Search or double-click a block in the toolbox.",
            DesignerTheme.SmallFont,
            new Rectangle(bounds.X, bounds.Y + bounds.Height / 2 + 2, bounds.Width, 22),
            DesignerTheme.MutedText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void DrawViewportBadge(Graphics graphics)
    {
        var text = $"Zoom: {_zoom * 100F:0}%";
        var badge = new Rectangle(14, Height - 34, 82, 22);
        using var path = CreateRoundedRectangle(badge, 5);
        using var brush = new SolidBrush(DesignerTheme.SurfaceRaised);
        using var border = new Pen(DesignerTheme.BorderSubtle);
        graphics.FillPath(brush, path);
        graphics.DrawPath(border, path);
        TextRenderer.DrawText(
            graphics,
            text,
            DesignerTheme.SmallFont,
            badge,
            DesignerTheme.MutedText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void DrawConnections(Graphics graphics)
    {
        foreach (var connection in Document.Connections)
        {
            var from = FindPort(connection.FromNodeId, connection.FromPort, FlowPortDirection.Output);
            var to = FindPort(connection.ToNodeId, connection.ToPort, FlowPortDirection.Input);
            if (from is null || to is null)
                continue;

            var color = GetConnectionColor(connection);
            var width = color == DesignerTheme.Copper ? 2.2F : 1.6F;
            DrawWire(graphics, Center(from.Value.Bounds), Center(to.Value.Bounds), color, width);
        }
    }

    private void DrawConnectionPreview(Graphics graphics)
    {
        if (_connectingFrom is null)
            return;

        DrawWire(
            graphics,
            Center(_connectingFrom.Value.Bounds),
            _connectionPreview,
            DesignerTheme.Copper,
            1.8F);
    }

    private static void DrawWire(Graphics graphics, PointF from, PointF to, Color color, float width)
    {
        using var pen = new Pen(color, width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        var controlOffset = Math.Max(60, Math.Abs(to.X - from.X) / 2);
        graphics.DrawBezier(
            pen,
            from,
            new PointF(from.X + controlOffset, from.Y),
            new PointF(to.X - controlOffset, to.Y),
            to);
    }

    private void DrawNodes(Graphics graphics)
    {
        foreach (var node in Document.Nodes)
        {
            if (!_catalog.TryGet(node.Type, out var definition))
                continue;

            var bounds = GetNodeBounds(node, definition);
            var selected = ReferenceEquals(node, SelectedNode);
            var state = GetExecutionState(node.Id);
            var bodyColor = selected ? DesignerTheme.SurfaceHover : DesignerTheme.Surface;
            var headerColor = selected ? DesignerTheme.SurfaceSelected : DesignerTheme.SurfaceRaised;
            var borderColor = state switch
            {
                NodeExecutionVisualState.Running => DesignerTheme.Copper,
                NodeExecutionVisualState.Completed => DesignerTheme.Signal,
                NodeExecutionVisualState.Skipped => DesignerTheme.MutedText,
                NodeExecutionVisualState.Failed => DesignerTheme.Error,
                _ => selected ? DesignerTheme.Signal : DesignerTheme.Border
            };
            var borderWidth = state == NodeExecutionVisualState.Idle
                ? selected ? 2F : 1F
                : 2F;

            using var bodyBrush = new SolidBrush(bodyColor);
            using var headerBrush = new SolidBrush(headerColor);
            using var borderPen = new Pen(borderColor, borderWidth);
            using var textBrush = new SolidBrush(DesignerTheme.Text);
            using var dividerPen = new Pen(DesignerTheme.BorderSubtle);
            using var nodePath = CreateRoundedRectangle(bounds, NodeCornerRadius);

            graphics.FillPath(bodyBrush, nodePath);
            graphics.FillRectangle(headerBrush, bounds.X + 1, bounds.Y + 1, bounds.Width - 2, HeaderHeight - 1);
            graphics.DrawPath(borderPen, nodePath);

            if (selected || state != NodeExecutionVisualState.Idle)
            {
                using var accentBrush = new SolidBrush(borderColor);
                graphics.FillRectangle(accentBrush, bounds.X, bounds.Y + NodeCornerRadius, 3, bounds.Height - NodeCornerRadius * 2);
            }

            graphics.DrawLine(dividerPen, bounds.X + 1, bounds.Y + HeaderHeight, bounds.Right - 2, bounds.Y + HeaderHeight);

            using var categoryBrush = new SolidBrush(GetCategoryColor(definition.Category));
            var iconBounds = new Rectangle(bounds.X + NodePadding, bounds.Y + 8, 18, 18);
            using (var iconPath = CreateRoundedRectangle(iconBounds, 4))
            using (var iconBack = new SolidBrush(Color.FromArgb(44, GetCategoryColor(definition.Category))))
            {
                graphics.FillPath(iconBack, iconPath);
                graphics.DrawPath(new Pen(GetCategoryColor(definition.Category), 1F), iconPath);
            }

            graphics.FillEllipse(categoryBrush, iconBounds.X + 6, iconBounds.Y + 6, 6, 6);

            DrawCanvasText(
                graphics,
                definition.DisplayName,
                DesignerTheme.TitleFont,
                new RectangleF(bounds.X + NodePadding + 28, bounds.Y + 7, bounds.Width - 86, 18),
                DesignerTheme.Text,
                StringAlignment.Near);
            var footer = GetExecutionMessage(node.Id) ?? definition.Category;
            DrawCanvasText(
                graphics,
                BuildNodeSummary(node, definition, footer),
                DesignerTheme.SmallFont,
                new RectangleF(bounds.X + NodePadding, bounds.Bottom - 22, bounds.Width - 24, 15),
                state == NodeExecutionVisualState.Failed ? DesignerTheme.Error : DesignerTheme.MutedText,
                StringAlignment.Near);

            DrawExecutionBadge(graphics, bounds, state);
            DrawPorts(graphics, node, definition);
        }
    }

    private static void DrawExecutionBadge(Graphics graphics, Rectangle bounds, NodeExecutionVisualState state)
    {
        if (state == NodeExecutionVisualState.Idle)
            return;

        var color = state switch
        {
            NodeExecutionVisualState.Running => DesignerTheme.Copper,
            NodeExecutionVisualState.Completed => DesignerTheme.Signal,
            NodeExecutionVisualState.Skipped => DesignerTheme.MutedText,
            NodeExecutionVisualState.Failed => DesignerTheme.Error,
            _ => DesignerTheme.MutedText
        };

        var label = state switch
        {
            NodeExecutionVisualState.Running => "RUN",
            NodeExecutionVisualState.Completed => "OK",
            NodeExecutionVisualState.Skipped => "SKIP",
            NodeExecutionVisualState.Failed => "ERR",
            _ => ""
        };

        var badge = new Rectangle(bounds.Right - 46, bounds.Y + 6, 34, 16);
        using var path = CreateRoundedRectangle(badge, 4);
        using var brush = new SolidBrush(color);
        graphics.FillPath(brush, path);
        DrawCanvasText(
            graphics,
            label,
            DesignerTheme.SmallFont,
            badge,
            Color.White,
            StringAlignment.Center);
    }

    private void DrawPorts(Graphics graphics, FlowNode node, FlowBlockDefinition definition)
    {
        using var inputBrush = new SolidBrush(DesignerTheme.Copper);
        using var outputBrush = new SolidBrush(DesignerTheme.Signal);
        using var portBorder = new Pen(DesignerTheme.Workbench, 1F);

        foreach (var port in definition.InputPorts)
        {
            var bounds = GetPortBounds(node, definition, port);
            graphics.FillEllipse(inputBrush, bounds);
            graphics.DrawEllipse(portBorder, bounds);
            if (ShouldShowPortLabel(node, port))
                DrawCanvasText(
                    graphics,
                    port.Name,
                    DesignerTheme.SmallFont,
                    new RectangleF(bounds.Right + 7, bounds.Y - 5, NodeWidth / 2F - 16, 18),
                    DesignerTheme.PortText,
                    StringAlignment.Near);
        }

        foreach (var port in definition.OutputPorts)
        {
            var bounds = GetPortBounds(node, definition, port);
            graphics.FillEllipse(outputBrush, bounds);
            graphics.DrawEllipse(portBorder, bounds);

            if (ShouldShowPortLabel(node, port))
                DrawCanvasText(
                    graphics,
                    port.Name,
                    DesignerTheme.SmallFont,
                    new RectangleF(bounds.Left - NodeWidth / 2F + 12, bounds.Y - 5, NodeWidth / 2F - 18, 18),
                    DesignerTheme.PortText,
                    StringAlignment.Far);
        }
    }

    private Rectangle GetNodeBounds(FlowNode node, FlowBlockDefinition? definition = null)
    {
        definition ??= _catalog.Get(node.Type);

        var rowCount = Math.Max(
            1,
            Math.Max(definition.InputPorts.Count(), definition.OutputPorts.Count()));

        var height = HeaderHeight + NodePadding + rowCount * PortRowHeight + 26;

        return new Rectangle(
            (int)node.Position.X,
            (int)node.Position.Y,
            NodeWidth,
            height);
    }

    private RectangleF GetPortBounds(FlowNode node, FlowBlockDefinition definition, FlowPortDefinition port)
    {
        var bounds = GetNodeBounds(node, definition);
        var ports = port.Direction == FlowPortDirection.Input
            ? definition.InputPorts.ToList()
            : definition.OutputPorts.ToList();
        var index = Math.Max(0, ports.FindIndex(item =>
            string.Equals(item.Name, port.Name, StringComparison.OrdinalIgnoreCase)));

        var y = bounds.Y + HeaderHeight + NodePadding + index * PortRowHeight + 6;
        var x = port.Direction == FlowPortDirection.Input
            ? bounds.X - PortRadius
            : bounds.Right - PortRadius;

        return new RectangleF(x, y, PortRadius * 2, PortRadius * 2);
    }

    private FlowNode? HitTestNode(PointF location)
    {
        foreach (var node in Document.Nodes.AsEnumerable().Reverse())
        {
            if (!_catalog.TryGet(node.Type, out var definition))
                continue;

            var bounds = GetNodeBounds(node, definition);
            if (bounds.Contains((int)location.X, (int)location.Y))
                return node;
        }

        return null;
    }

    private PortHit? HitTestPort(PointF location, FlowPortDirection? direction)
    {
        foreach (var node in Document.Nodes.AsEnumerable().Reverse())
        {
            if (!_catalog.TryGet(node.Type, out var definition))
                continue;

            foreach (var port in definition.Ports)
            {
                if (direction is not null && port.Direction != direction)
                    continue;

                var bounds = GetPortBounds(node, definition, port);
                if (bounds.Contains(location))
                    return new PortHit(node, port, bounds);
            }
        }

        return null;
    }

    private PortHit? FindPort(string nodeId, string portName, FlowPortDirection direction)
    {
        var node = Document.Nodes.FirstOrDefault(item =>
            string.Equals(item.Id, nodeId, StringComparison.OrdinalIgnoreCase));

        if (node is null || !_catalog.TryGet(node.Type, out var definition))
            return null;

        var port = direction == FlowPortDirection.Input
            ? definition.FindInput(portName)
            : definition.FindOutput(portName);

        return port is null
            ? null
            : new PortHit(node, port, GetPortBounds(node, definition, port));
    }

    private NodeExecutionVisualState GetExecutionState(string nodeId) =>
        _executionStates.TryGetValue(nodeId, out var state) ? state : NodeExecutionVisualState.Idle;

    private string? GetExecutionMessage(string nodeId) =>
        _executionMessages.TryGetValue(nodeId, out var message) ? message : null;

    private static string BuildNodeSummary(FlowNode node, FlowBlockDefinition definition, string fallback)
    {
        if (definition.Type == BuiltInBlockCatalog.GpioBlinkLed)
            return $"GPIO {GetParameter(node, "pin", 2)} | {GetParameter(node, "durationMs", 500)} ms";

        if (definition.Type is BuiltInBlockCatalog.GpioSetOutput or BuiltInBlockCatalog.GpioDigitalWrite)
            return $"GPIO {GetParameter(node, "pin", 2)} | {(GetParameter(node, "value", false) ? "HIGH" : "LOW")}";

        if (definition.Type is BuiltInBlockCatalog.GpioDigitalRead or BuiltInBlockCatalog.GpioAnalogRead)
            return $"GPIO {GetParameter(node, "pin", 2)}";

        if (definition.Type == BuiltInBlockCatalog.Timer)
            return $"{GetParameter(node, "intervalMs", 1000)} ms";

        if (definition.Type == BuiltInBlockCatalog.SampleChannel)
            return $"GPIO {GetParameter(node, "pin", 32)} | {GetParameter(node, "sampleRateHz", 1000)} Hz";

        if (definition.Type == BuiltInBlockCatalog.ServoWrite)
            return $"GPIO {GetParameter(node, "pin", 13)} | {GetParameter(node, "angle", 90)} deg";

        const int maxLength = 28;
        return fallback.Length <= maxLength ? fallback : fallback[..(maxLength - 1)] + "...";
    }

    private static T GetParameter<T>(FlowNode node, string name, T fallback)
    {
        if (!node.Parameters.TryGetValue(name, out var value) || value is null)
            return fallback;

        try
        {
            value = NormalizeJsonValue(value);

            if (value is T typed)
                return typed;

            if (value is null)
                return fallback;

            return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    private static object? NormalizeJsonValue(object? value)
    {
        if (value is not JsonElement json)
            return value;

        return json.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when json.TryGetInt32(out var integer) => integer,
            JsonValueKind.Number => json.GetDouble(),
            JsonValueKind.String => json.GetString(),
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => json.GetRawText()
        };
    }

    private Rectangle GetDocumentBounds()
    {
        Rectangle? bounds = null;
        foreach (var node in Document.Nodes)
        {
            if (!_catalog.TryGet(node.Type, out var definition))
                continue;

            var nodeBounds = GetNodeBounds(node, definition);
            nodeBounds.Inflate(72, 36);
            bounds = bounds is null
                ? nodeBounds
                : Rectangle.Union(bounds.Value, nodeBounds);
        }

        return bounds ?? Rectangle.Empty;
    }

    private PointF ToDocument(Point location) => ToDocument(new PointF(location.X, location.Y));

    private PointF ToDocument(PointF location) => new(
        (location.X - _viewOffset.X) / _zoom,
        (location.Y - _viewOffset.Y) / _zoom);

    private void UpdatePortToolTip(PortHit? hoverPort, Point location)
    {
        if (hoverPort is null)
        {
            if (_hoverPortKey is not null)
            {
                _hoverPortKey = null;
                _toolTip.Hide(this);
            }

            return;
        }

        var key = $"{hoverPort.Value.Node.Id}:{hoverPort.Value.Port.Direction}:{hoverPort.Value.Port.Name}";
        if (string.Equals(_hoverPortKey, key, StringComparison.Ordinal))
            return;

        _hoverPortKey = key;
        var direction = hoverPort.Value.Port.Direction == FlowPortDirection.Input ? "Input" : "Output";
        var required = hoverPort.Value.Port.Required ? "required" : "optional";
        _toolTip.Show(
            $"{direction}: {hoverPort.Value.Port.Name} ({hoverPort.Value.Port.ValueKind}, {required})",
            this,
            location.X + 14,
            location.Y + 18,
            4000);
        Invalidate();
    }

    private bool ShouldShowPortLabel(FlowNode node, FlowPortDefinition port)
    {
        if (ReferenceEquals(node, SelectedNode))
            return true;

        return _hoverPortKey is not null &&
               string.Equals(_hoverPortKey, $"{node.Id}:{port.Direction}:{port.Name}", StringComparison.Ordinal);
    }

    private static void DrawCanvasText(
        Graphics graphics,
        string text,
        Font font,
        RectangleF bounds,
        Color color,
        StringAlignment horizontalAlignment)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = horizontalAlignment,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        graphics.DrawString(text, font, brush, bounds, format);
    }

    private void ZoomBy(float factor, PointF anchor)
    {
        var before = ToDocument(anchor);
        var nextZoom = Math.Clamp(_zoom * factor, MinimumZoom, MaximumZoom);
        if (Math.Abs(nextZoom - _zoom) < 0.001F)
            return;

        _zoom = nextZoom;
        _viewOffset = new PointF(
            anchor.X - before.X * _zoom,
            anchor.Y - before.Y * _zoom);
        Invalidate();
    }

    private static Color GetCategoryColor(string category) =>
        category switch
        {
            "Flow" => DesignerTheme.Magenta,
            "GPIO" => DesignerTheme.AccentSoft,
            "Logic" => Color.FromArgb(56, 189, 248),
            "Math" => Color.FromArgb(236, 98, 170),
            "Actuators" => Color.FromArgb(203, 166, 247),
            "Modules" => Color.FromArgb(203, 166, 247),
            "Sensors" => Color.FromArgb(232, 121, 249),
            "Acquisition" => DesignerTheme.Copper,
            "Dashboard" => Color.FromArgb(129, 140, 248),
            "Debug" => DesignerTheme.MutedText,
            _ => DesignerTheme.Border
        };

    private Color GetConnectionColor(FlowConnection connection)
    {
        var fromState = GetExecutionState(connection.FromNodeId);
        var toState = GetExecutionState(connection.ToNodeId);

        if (toState == NodeExecutionVisualState.Running)
            return DesignerTheme.Copper;

        if (fromState == NodeExecutionVisualState.Completed && toState == NodeExecutionVisualState.Completed)
            return DesignerTheme.Signal;

        if (toState == NodeExecutionVisualState.Failed)
            return DesignerTheme.Error;

        if (toState == NodeExecutionVisualState.Skipped)
            return DesignerTheme.MutedText;

        return DesignerTheme.Signal;
    }

    private static bool CanConnect(PortHit from, PortHit to)
    {
        if (string.Equals(from.Node.Id, to.Node.Id, StringComparison.OrdinalIgnoreCase))
            return false;

        if (from.Port.ValueKind == FlowValueKind.Any || to.Port.ValueKind == FlowValueKind.Any)
            return true;

        if (from.Port.ValueKind == to.Port.ValueKind)
            return true;

        return from.Port.ValueKind == FlowValueKind.Integer && to.Port.ValueKind == FlowValueKind.Number;
    }

    private void SelectNode(FlowNode? node)
    {
        if (ReferenceEquals(SelectedNode, node))
            return;

        SelectedNode = node;
        SelectedNodeChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    private void OnDocumentChanged() => DocumentChanged?.Invoke(this, EventArgs.Empty);

    private string CreateNodeId(string blockType)
    {
        var slug = blockType
            .Split('.', '-')
            .Last()
            .Replace("_", "-", StringComparison.OrdinalIgnoreCase);

        var index = 1;
        string id;
        do
        {
            id = $"{slug}-{index++}";
        }
        while (Document.Nodes.Any(node => string.Equals(node.Id, id, StringComparison.OrdinalIgnoreCase)));

        return id;
    }

    private static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var diameter = radius * 2;
        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));

        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter - 1;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter - 1;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static PointF Center(RectangleF rectangle) =>
        new(rectangle.Left + rectangle.Width / 2F, rectangle.Top + rectangle.Height / 2F);

    private readonly record struct PortHit(FlowNode Node, FlowPortDefinition Port, RectangleF Bounds);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _toolTip.Dispose();

        base.Dispose(disposing);
    }
}
