#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CodeBridge.Flow;

namespace CodeBridge.VisualStudio.Editor
{
    /// <summary>Block toolbox: search, drag to the canvas (no OLE drag-and-drop), double click and the quick-add popup.</summary>
    public partial class FlowEditorControl
    {
        private const double DragThreshold = 5;

        private string _toolboxFilter = string.Empty;
        private bool _toolboxPopulated;

        // Blocks for fine control or data streaming live in a collapsed "Advanced" group so the basics stay short.
        private static readonly HashSet<string> AdvancedBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "gpio.pin-mode",
            "gpio.set-output",
            "acquisition.sample-channel",
            "acquisition.interrupt-input",
            "dashboard.stream"
        };
        private Point _toolboxPressPoint;
        private string? _toolboxPressType;
        private string? _draggingBlockType;
        private FrameworkElement? _dragGhost;
        private Popup? _quickAdd;

        private void InitializeToolbox()
        {
            Toolbox.PreviewMouseLeftButtonDown += OnToolboxPreviewMouseDown;
            Toolbox.PreviewMouseMove += OnToolboxPreviewMouseMove;
            Toolbox.PreviewMouseLeftButtonUp += OnToolboxPreviewMouseUp;
            Toolbox.PreviewKeyDown += OnToolboxPreviewKeyDown;
            Toolbox.MouseDoubleClick += OnToolboxDoubleClick;
            Toolbox.LostMouseCapture += (_, __) => CancelToolboxDrag();
        }

        private void OnToolboxSearchChanged(object sender, TextChangedEventArgs e)
        {
            if (ToolboxSearch == null || Toolbox == null)
                return;

            _toolboxFilter = (ToolboxSearch.Text ?? string.Empty).Trim();
            if (SearchPlaceholder != null)
                SearchPlaceholder.Visibility = string.IsNullOrEmpty(_toolboxFilter) ? Visibility.Visible : Visibility.Collapsed;

            PopulateToolbox();
        }

        private static bool Matches(FlowBlockDefinition block, string filter) =>
            string.IsNullOrEmpty(filter) ||
            block.DisplayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
            block.Category.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
            block.Type.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
            block.Description.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

        private void PopulateToolbox()
        {
            if (Toolbox == null)
                return;

            // Keep categories the user collapsed.
            var collapsed = new HashSet<string>(
                Toolbox.Items.OfType<TreeViewItem>().Where(i => !i.IsExpanded).Select(i => i.Tag as string ?? string.Empty));

            Toolbox.Items.Clear();
            var textBrush = (Brush)FindResource("VsToolWindowText");
            var filtered = _catalog.Blocks.Where(b => Matches(b, _toolboxFilter));

            var firstPopulate = !_toolboxPopulated;
            _toolboxPopulated = true;

            var groups = filtered
                .GroupBy(b => AdvancedBlocks.Contains(b.Type) ? "Advanced" : b.Category)
                .OrderBy(g => g.Key == "Advanced" ? 1 : 0)
                .ThenBy(g => g.Key);

            foreach (var category in groups)
            {
                var categoryItem = new TreeViewItem
                {
                    Header = category.Key,
                    Tag = "category:" + category.Key,
                    IsExpanded = !string.IsNullOrEmpty(_toolboxFilter)
                                 || (firstPopulate ? category.Key != "Advanced" : !collapsed.Contains("category:" + category.Key)),
                    Foreground = textBrush,
                    FontWeight = FontWeights.SemiBold
                };

                foreach (var block in category.OrderBy(b => b.DisplayName))
                {
                    var header = new StackPanel { Orientation = Orientation.Horizontal };
                    header.Children.Add(new Ellipse
                    {
                        Width = 8,
                        Height = 8,
                        Margin = new Thickness(0, 0, 8, 0),
                        Fill = CategoryBrush(block.Category),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    header.Children.Add(new TextBlock { Text = block.DisplayName, FontWeight = FontWeights.Normal });

                    var blockItem = new TreeViewItem
                    {
                        Header = header,
                        Tag = block.Type,
                        Foreground = textBrush
                    };
                    BlockTip.Attach(blockItem, block);
                    categoryItem.Items.Add(blockItem);
                }

                Toolbox.Items.Add(categoryItem);
            }

            if (Toolbox.Items.Count == 0)
            {
                Toolbox.Items.Add(new TreeViewItem
                {
                    Header = "No blocks match your search",
                    Foreground = (Brush)FindResource("VsGrayText"),
                    IsEnabled = false
                });
            }
        }

        private static Brush CategoryBrush(string category)
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
                case "Modules": return new SolidColorBrush(Color.FromRgb(203, 166, 247));
                case "Sensors": return new SolidColorBrush(Color.FromRgb(232, 121, 249));
                case "Debug": return new SolidColorBrush(Color.FromRgb(148, 163, 184));
                default: return new SolidColorBrush(Color.FromRgb(155, 164, 176));
            }
        }

        private static string? BlockTypeOf(object? source)
        {
            var item = source is DependencyObject dependency ? FindParent<TreeViewItem>(dependency) : null;
            var tag = item?.Tag as string;
            return tag != null && !tag.StartsWith("category:", StringComparison.Ordinal) ? tag : null;
        }

        // ---------------------------------------------------------------- drag from the toolbox

        private void OnToolboxPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _toolboxPressType = BlockTypeOf(e.OriginalSource);
            _toolboxPressPoint = e.GetPosition(this);
        }

        private void OnToolboxPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                if (_draggingBlockType != null)
                    CancelToolboxDrag();

                return;
            }

            var position = e.GetPosition(this);
            if (_draggingBlockType == null)
            {
                if (_toolboxPressType == null ||
                    (Math.Abs(position.X - _toolboxPressPoint.X) < DragThreshold && Math.Abs(position.Y - _toolboxPressPoint.Y) < DragThreshold))
                    return;

                BeginToolboxDrag(_toolboxPressType);
            }

            if (_dragGhost != null)
            {
                Canvas.SetLeft(_dragGhost, position.X + 12);
                Canvas.SetTop(_dragGhost, position.Y + 8);
            }

            var overCanvas = IsOverCanvas(e);
            Mouse.OverrideCursor = overCanvas ? Cursors.Cross : Cursors.No;
            e.Handled = true;
        }

        private void BeginToolboxDrag(string blockType)
        {
            var definition = _catalog.Get(blockType);
            _draggingBlockType = blockType;

            var ghost = new Border
            {
                Padding = new Thickness(10, 5, 10, 5),
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                Opacity = 0.9,
                Child = new TextBlock { Text = definition?.DisplayName ?? blockType, FontSize = 11, FontWeight = FontWeights.SemiBold }
            };
            ghost.SetResourceReference(Border.BackgroundProperty, "VsToolWindowHeader");
            ghost.SetResourceReference(Border.BorderBrushProperty, "VsHighlight");
            ((TextBlock)ghost.Child).SetResourceReference(TextBlock.ForegroundProperty, "VsToolWindowText");

            _dragGhost = ghost;
            DragLayer.Children.Add(ghost);
            Mouse.Capture(Toolbox, CaptureMode.SubTree);
        }

        private bool IsOverCanvas(MouseEventArgs e)
        {
            var point = e.GetPosition(CanvasBorder);
            return point.X >= 0 && point.Y >= 0 && point.X <= CanvasBorder.ActualWidth && point.Y <= CanvasBorder.ActualHeight;
        }

        private void OnToolboxPreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            var blockType = _draggingBlockType;
            if (blockType == null)
            {
                _toolboxPressType = null;
                return;
            }

            var overCanvas = IsOverCanvas(e);
            var viewportPoint = e.GetPosition(CanvasBorder);
            CancelToolboxDrag();

            if (overCanvas)
            {
                var canvasPoint = CanvasBorder.ViewportToCanvas(viewportPoint);
                AddBlock(blockType, new Point(canvasPoint.X - FlowNodeControl.NodeWidth / 2, canvasPoint.Y - 20));
                Focus();
            }

            e.Handled = true;
        }

        private void CancelToolboxDrag()
        {
            _toolboxPressType = null;
            if (_draggingBlockType == null)
                return;

            _draggingBlockType = null;
            if (_dragGhost != null)
            {
                DragLayer.Children.Remove(_dragGhost);
                _dragGhost = null;
            }

            Mouse.OverrideCursor = null;
            if (Mouse.Captured == Toolbox)
                Mouse.Capture(null);
        }

        private void OnToolboxDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var blockType = BlockTypeOf(e.OriginalSource);
            if (blockType == null)
                return;

            AddBlockAtViewportCenter(blockType);
            e.Handled = true;
        }

        private void OnToolboxPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _draggingBlockType != null)
            {
                CancelToolboxDrag();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter && Toolbox.SelectedItem is TreeViewItem item && item.Tag is string tag && !tag.StartsWith("category:", StringComparison.Ordinal))
            {
                AddBlockAtViewportCenter(tag);
                e.Handled = true;
            }
        }

        // ---------------------------------------------------------------- quick add popup (double click on the canvas, Ctrl+Space)

        private void ShowQuickAdd(Point canvasPoint, Point viewportPoint)
        {
            if (_quickAdd == null)
                _quickAdd = BuildQuickAdd();

            var search = (TextBox)((StackPanel)((Border)_quickAdd.Child).Child).Children[0];
            var list = (ListBox)((StackPanel)((Border)_quickAdd.Child).Child).Children[1];
            _quickAdd.Tag = canvasPoint;
            _quickAdd.PlacementTarget = CanvasBorder;
            _quickAdd.HorizontalOffset = Math.Max(0, Math.Min(viewportPoint.X, CanvasBorder.ActualWidth - 260));
            _quickAdd.VerticalOffset = Math.Max(0, Math.Min(viewportPoint.Y, CanvasBorder.ActualHeight - 260));
            search.Text = string.Empty;
            FillQuickAdd(list, string.Empty);
            _quickAdd.IsOpen = true;
            Dispatcher.BeginInvoke(new Action(() => search.Focus()), System.Windows.Threading.DispatcherPriority.Input);
        }

        private Popup BuildQuickAdd()
        {
            var search = new TextBox { Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(6, 4, 6, 4) };
            var list = new ListBox { MaxHeight = 220, BorderThickness = new Thickness(0) };
            search.SetResourceReference(TextBox.BackgroundProperty, "VsInputBackground");
            search.SetResourceReference(TextBox.ForegroundProperty, "VsToolWindowText");
            search.SetResourceReference(TextBox.BorderBrushProperty, "VsInputBorder");
            list.SetResourceReference(ListBox.BackgroundProperty, "VsToolWindowBackground");
            list.SetResourceReference(ListBox.ForegroundProperty, "VsToolWindowText");

            var hint = new TextBlock { Text = "Type to search, Enter to add, Esc to close", FontSize = 10, Margin = new Thickness(2, 4, 0, 0) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "VsGrayText");

            var panel = new StackPanel { Width = 260 };
            panel.Children.Add(search);
            panel.Children.Add(list);
            panel.Children.Add(hint);

            var border = new Border { Padding = new Thickness(6), BorderThickness = new Thickness(1), Child = panel };
            border.SetResourceReference(Border.BackgroundProperty, "VsToolWindowBackground");
            border.SetResourceReference(Border.BorderBrushProperty, "VsHighlight");

            var popup = new Popup
            {
                Child = border,
                StaysOpen = false,
                Placement = PlacementMode.Relative,
                AllowsTransparency = false
            };

            search.TextChanged += (s, e) => FillQuickAdd(list, search.Text.Trim());
            search.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Down || e.Key == Key.Up)
                {
                    var next = list.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
                    if (next >= 0 && next < list.Items.Count)
                        list.SelectedIndex = next;

                    e.Handled = true;
                }
                else if (e.Key == Key.Enter)
                {
                    CommitQuickAdd(popup, list);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    popup.IsOpen = false;
                    e.Handled = true;
                }
            };
            list.MouseDoubleClick += (s, e) => CommitQuickAdd(popup, list);

            return popup;
        }

        private void FillQuickAdd(ListBox list, string filter)
        {
            list.Items.Clear();
            foreach (var block in _catalog.Blocks.Where(b => Matches(b, filter)).OrderBy(b => b.Category).ThenBy(b => b.DisplayName))
            {
                list.Items.Add(new ListBoxItem
                {
                    Content = $"{block.DisplayName}   ·   {block.Category}",
                    Tag = block.Type,
                    ToolTip = block.Description
                });
            }

            if (list.Items.Count > 0)
                list.SelectedIndex = 0;
        }

        private void CommitQuickAdd(Popup popup, ListBox list)
        {
            if (!(list.SelectedItem is ListBoxItem item) || !(item.Tag is string blockType))
                return;

            var point = popup.Tag is Point p ? p : CanvasBorder.ViewportCenterInCanvas();
            popup.IsOpen = false;
            AddBlock(blockType, new Point(point.X - FlowNodeControl.NodeWidth / 2, point.Y - 20));
            Focus();
        }
    }
}
