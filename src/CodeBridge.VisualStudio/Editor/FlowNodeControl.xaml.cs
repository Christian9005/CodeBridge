#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CodeBridge.Flow;

namespace CodeBridge.VisualStudio.Editor
{
    internal enum NodeStatus
    {
        None,
        Running,
        Completed,
        Skipped,
        Failed,
        Warning,
        Error
    }

    public partial class FlowNodeControl : UserControl
    {
        public const double NodeWidth = 168;
        public const double PortTop = 41;
        public const double PortPitch = 22;

        public CodeBridge.Flow.FlowNode NodeData { get; private set; }
        public CodeBridge.Flow.FlowBlockDefinition? Definition { get; private set; }

        public Action<FlowNodeControl, MouseButtonEventArgs>? NodeMouseDown;
        public Action<FlowNodeControl, MouseEventArgs>? NodeMouseMove;
        public Action<FlowNodeControl, MouseButtonEventArgs>? NodeMouseUp;
        public Action<FlowNodeControl, MouseButtonEventArgs>? NodeDoubleClick;

        public Action<FlowNodeControl, string, FlowPortDirection, Point>? PortDragStarted;
        public Action<FlowNodeControl, string, FlowPortDirection, Point>? PortDragDelta;
        public Action<FlowNodeControl, string, FlowPortDirection, Point>? PortDragCompleted;

        private readonly FlowCatalog _catalog;
        private readonly Dictionary<string, Ellipse> _portShapes = new Dictionary<string, Ellipse>(StringComparer.OrdinalIgnoreCase);
        private bool _isDraggingNode;
        private string? _draggingPortName;
        private FlowPortDirection _draggingPortDirection;
        private bool _isSelected;
        private NodeStatus _status;

        internal FlowNodeControl(CodeBridge.Flow.FlowNode data, FlowCatalog catalog)
        {
            InitializeComponent();
            VsTheme.Bind(Resources);
            NodeData = data;
            _catalog = catalog;

            var definition = catalog.Get(data.Type);
            if (definition != null)
            {
                Definition = definition;
                data.Type = definition.Type; // Canonicalize alias
                TitleText.Text = definition.DisplayName;
                CategoryIndicator.Fill = GetCategoryColor(definition.Category);
                BlockTip.Attach(this, definition);
                PopulatePorts();
            }
            else
            {
                TitleText.Text = data.Type;
                ToolTip = $"Unknown block '{data.Type}' for {catalog.DisplayName}. It is kept in the file but cannot run.";
                SetStatus(NodeStatus.Warning, "Unknown block for the selected board");
            }

            RefreshSummary();

            MouseLeftButtonDown += (s, e) =>
            {
                if (Keyboard.IsKeyDown(Key.Space))
                    return; // Space + drag pans the canvas.

                if (e.ClickCount == 2)
                {
                    NodeDoubleClick?.Invoke(this, e);
                    e.Handled = true;
                    return;
                }

                _isDraggingNode = true;
                CaptureMouse();
                NodeMouseDown?.Invoke(this, e);
                e.Handled = true;
            };

            MouseMove += (s, e) =>
            {
                if (_isDraggingNode)
                    NodeMouseMove?.Invoke(this, e);
            };

            MouseLeftButtonUp += (s, e) =>
            {
                if (_isDraggingNode)
                {
                    _isDraggingNode = false;
                    ReleaseMouseCapture();
                    NodeMouseUp?.Invoke(this, e);
                    e.Handled = true;
                }
            };
        }

        public bool IsSelected => _isSelected;

        private static SolidColorBrush GetCategoryColor(string category)
        {
            switch (category)
            {
                case "Logic": return new SolidColorBrush(Color.FromRgb(56, 189, 248));
                case "Math": return new SolidColorBrush(Color.FromRgb(236, 98, 170));
                case "Flow": return new SolidColorBrush(Color.FromRgb(194, 86, 255));
                case "GPIO": return new SolidColorBrush(Color.FromRgb(34, 184, 207));
                case "Acquisition": return new SolidColorBrush(Color.FromRgb(58, 130, 246));
                case "Dashboard": return new SolidColorBrush(Color.FromRgb(129, 140, 248));
                case "Actuators": return new SolidColorBrush(Color.FromRgb(203, 166, 247));
                case "Debug": return new SolidColorBrush(Color.FromRgb(148, 163, 184));
                default: return new SolidColorBrush(Color.FromRgb(155, 164, 176));
            }
        }

        internal static Brush PortBrush(FlowValueKind kind)
        {
            switch (kind)
            {
                case FlowValueKind.Trigger: return new SolidColorBrush(Color.FromRgb(194, 86, 255));
                case FlowValueKind.Boolean: return new SolidColorBrush(Color.FromRgb(58, 130, 246));
                case FlowValueKind.Integer:
                case FlowValueKind.Number: return new SolidColorBrush(Color.FromRgb(0, 216, 143));
                case FlowValueKind.String: return new SolidColorBrush(Color.FromRgb(245, 170, 48));
                default: return new SolidColorBrush(Color.FromRgb(155, 164, 176));
            }
        }

        private void PopulatePorts()
        {
            InputPortsPanel.Children.Clear();
            OutputPortsPanel.Children.Clear();
            _portShapes.Clear();

            if (Definition == null) return;

            foreach (var port in Definition.InputPorts)
                InputPortsPanel.Children.Add(CreatePortElement(port, FlowPortDirection.Input));

            foreach (var port in Definition.OutputPorts)
                OutputPortsPanel.Children.Add(CreatePortElement(port, FlowPortDirection.Output));
        }

        private UIElement CreatePortElement(FlowPortDefinition port, FlowPortDirection direction)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Height = 20,
                Margin = new Thickness(0, 1, 0, 1),
                HorizontalAlignment = direction == FlowPortDirection.Input ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                Tag = port.Name,
                Background = Brushes.Transparent,
                ToolTip = $"{port.Name}: {port.ValueKind}{(direction == FlowPortDirection.Input && !port.Required ? " (optional)" : string.Empty)}"
            };

            var portCircle = new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = PortBrush(port.ValueKind),
                Stroke = new SolidColorBrush(Color.FromRgb(24, 25, 27)),
                StrokeThickness = 1.5,
                Tag = port,
                Cursor = Cursors.Cross,
                VerticalAlignment = VerticalAlignment.Center
            };
            _portShapes[(direction == FlowPortDirection.Input ? "in:" : "out:") + port.Name] = portCircle;

            var label = new TextBlock
            {
                Text = port.Name,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 5, 0)
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "VsNodeText");

            if (direction == FlowPortDirection.Input)
            {
                panel.Children.Add(portCircle);
                panel.Children.Add(label);
            }
            else
            {
                panel.Children.Add(label);
                panel.Children.Add(portCircle);
            }

            portCircle.MouseEnter += (s, e) => portCircle.StrokeThickness = 2.5;
            portCircle.MouseLeave += (s, e) =>
            {
                if (!portCircle.IsMouseCaptured)
                    portCircle.StrokeThickness = 1.5;
            };

            portCircle.MouseLeftButtonDown += (s, e) =>
            {
                if (Keyboard.IsKeyDown(Key.Space))
                    return;

                _draggingPortName = port.Name;
                _draggingPortDirection = direction;
                portCircle.CaptureMouse();
                PortDragStarted?.Invoke(this, port.Name, direction, GetPortPosition(port.Name, direction));
                e.Handled = true;
            };

            portCircle.MouseMove += (s, e) =>
            {
                if (portCircle.IsMouseCaptured && _draggingPortName != null)
                {
                    var parent = (Parent as UIElement) ?? this;
                    PortDragDelta?.Invoke(this, _draggingPortName, _draggingPortDirection, e.GetPosition(parent));
                    e.Handled = true;
                }
            };

            portCircle.MouseLeftButtonUp += (s, e) =>
            {
                if (portCircle.IsMouseCaptured && _draggingPortName != null)
                {
                    portCircle.ReleaseMouseCapture();
                    portCircle.StrokeThickness = 1.5;
                    var parent = (Parent as UIElement) ?? this;
                    var name = _draggingPortName;
                    _draggingPortName = null;
                    PortDragCompleted?.Invoke(this, name, _draggingPortDirection, e.GetPosition(parent));
                    e.Handled = true;
                }
            };

            return panel;
        }

        /// <summary>Canvas position of a port's centre.</summary>
        public Point GetPortPosition(string portName, FlowPortDirection direction)
        {
            double nodeX = Canvas.GetLeft(this);
            double nodeY = Canvas.GetTop(this);
            if (double.IsNaN(nodeX)) nodeX = NodeData.Position.X;
            if (double.IsNaN(nodeY)) nodeY = NodeData.Position.Y;

            var portIndex = 0;
            if (Definition != null)
            {
                var ports = direction == FlowPortDirection.Input ? Definition.InputPorts : Definition.OutputPorts;
                var index = 0;
                foreach (var port in ports)
                {
                    if (string.Equals(port.Name, portName, StringComparison.OrdinalIgnoreCase))
                    {
                        portIndex = index;
                        break;
                    }

                    index++;
                }
            }

            var portX = direction == FlowPortDirection.Input ? nodeX : nodeX + NodeWidth;
            var portY = nodeY + PortTop + (portIndex * PortPitch);
            return new Point(portX, portY);
        }

        /// <summary>True when the block definition has a port with that name and direction.</summary>
        public bool HasPort(string portName, FlowPortDirection direction) =>
            Definition?.FindPort(portName, direction) != null;

        public void SetSelected(bool selected)
        {
            _isSelected = selected;
            if (selected)
            {
                NodeBorder.SetResourceReference(Border.BorderBrushProperty, "VsNodeSelectedBorder");
                NodeBorder.BorderThickness = new Thickness(2);
            }
            else
            {
                NodeBorder.SetResourceReference(Border.BorderBrushProperty, "VsNodeBorder");
                NodeBorder.BorderThickness = new Thickness(1);
            }
        }

        internal void SetStatus(NodeStatus status, string? tooltip = null)
        {
            _status = status;
            if (status == NodeStatus.None)
            {
                BadgeBorder.Visibility = Visibility.Collapsed;
                return;
            }

            string text;
            Color color;
            switch (status)
            {
                case NodeStatus.Running: text = "RUN"; color = Color.FromRgb(0, 122, 204); break;
                case NodeStatus.Completed: text = "OK"; color = Color.FromRgb(0, 168, 112); break;
                case NodeStatus.Skipped: text = "SKIP"; color = Color.FromRgb(120, 120, 120); break;
                case NodeStatus.Failed: text = "FAIL"; color = Color.FromRgb(214, 55, 71); break;
                case NodeStatus.Warning: text = "WARN"; color = Color.FromRgb(200, 130, 10); break;
                default: text = "ERR"; color = Color.FromRgb(214, 55, 71); break;
            }

            BadgeText.Text = text;
            BadgeBorder.Background = new SolidColorBrush(color);
            BadgeBorder.ToolTip = tooltip;
            BadgeBorder.Visibility = Visibility.Visible;
        }

        internal NodeStatus Status => _status;

        private static string UnitSuffix(string propertyName)
        {
            if (propertyName.EndsWith("Ms", StringComparison.Ordinal)) return " ms";
            if (propertyName.EndsWith("Us", StringComparison.Ordinal)) return " µs";
            if (propertyName.EndsWith("Hz", StringComparison.Ordinal)) return " Hz";
            return string.Empty;
        }

        /// <summary>Shows the two most relevant parameters under the ports ("GPIO 2 (LED) · 500").</summary>
        public void RefreshSummary()
        {
            if (Definition == null)
            {
                SummaryBorder.Visibility = Visibility.Collapsed;
                return;
            }

            var parts = new List<string>();
            foreach (var property in Definition.Properties.Where(p => !p.IsAdvanced).Take(2))
            {
                var value = BuiltInBlockCatalog.GetParameterValue(NodeData, property);
                if (value == null)
                    continue;

                var text = property.Options?.FirstOrDefault(o => string.Equals(o.Value?.ToString(), value.ToString(), StringComparison.Ordinal))?.DisplayName
                           ?? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
                           ?? string.Empty;
                if (property.Options == null)
                    text += UnitSuffix(property.Name);

                if (text.Length > 0)
                    parts.Add(text);
            }

            SummaryText.Text = string.Join("  ·  ", parts);
            SummaryBorder.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
