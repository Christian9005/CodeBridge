#nullable enable
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using CodeBridge.Flow;

namespace CodeBridge.VisualStudio.Editor
{
    /// <summary>
    /// Rich tooltip for a block: what it does in plain words, a looping mini animation, what each port means and a tip.
    /// Content is built when the tooltip opens and the animation stops when it closes, so idle blocks cost nothing.
    /// </summary>
    internal static class BlockTip
    {
        public static void Attach(FrameworkElement owner, FlowBlockDefinition definition)
        {
            var tip = new ToolTip
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                HasDropShadow = false,
                Placement = PlacementMode.Right
            };

            BlockDemoView? demo = null;
            owner.ToolTipOpening += (s, e) =>
            {
                tip.Content = Build(owner, definition, out demo);
                demo?.Start();
            };
            owner.ToolTipClosing += (s, e) =>
            {
                demo?.Stop();
                demo = null;
                tip.Content = null;
            };

            owner.ToolTip = tip;
            ToolTipService.SetInitialShowDelay(owner, 450);
            ToolTipService.SetShowDuration(owner, 120000);
            ToolTipService.SetPlacement(owner, PlacementMode.Right);
        }

        internal static FrameworkElement Build(FrameworkElement owner, FlowBlockDefinition definition, out BlockDemoView? demo)
        {
            Brush Brush(string key, Brush fallback) => owner.TryFindResource(key) as Brush ?? fallback;

            var text = Brush("VsToolWindowText", Brushes.White);
            var muted = Brush("VsGrayText", Brushes.Gray);
            var border = Brush("VsToolWindowBorder", Brushes.DimGray);
            var background = Brush("VsToolWindowBackground", Brushes.Black);
            var header = Brush("VsToolWindowHeader", Brushes.Black);

            var help = BlockHelp.Get(definition.Type);
            var panel = new StackPanel { Width = 300 };

            // Title
            var title = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var category = new TextBlock { Text = definition.Category, Foreground = muted, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(category, Dock.Right);
            title.Children.Add(category);
            title.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = CategoryColor(definition.Category), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            title.Children.Add(new TextBlock { Text = definition.DisplayName, Foreground = text, FontSize = 14, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(title);

            panel.Children.Add(new TextBlock
            {
                Text = BlockHelp.SummaryFor(definition),
                Foreground = text,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });

            // Animated demo
            demo = null;
            if (help != null && help.Demo != DemoKind.None)
            {
                demo = new BlockDemoView(help.Demo, text, muted, border);
                panel.Children.Add(new Border
                {
                    Background = header,
                    BorderBrush = border,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(0, 0, 0, 8),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = demo
                });
            }

            AddPorts(panel, "Inputs", definition.InputPorts, help, text, muted);
            AddPorts(panel, "Outputs", definition.OutputPorts, help, text, muted);

            if (!string.IsNullOrEmpty(help?.Tip))
            {
                var tip = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 8, 0, 0), Foreground = muted };
                tip.Inlines.Add(new System.Windows.Documents.Run("Tip  ") { FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(245, 170, 48)) });
                tip.Inlines.Add(new System.Windows.Documents.Run(help!.Tip));
                panel.Children.Add(tip);
            }

            return new Border
            {
                Background = background,
                BorderBrush = Brush("VsHighlight", border),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12),
                Child = panel,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.35 }
            };
        }

        private static void AddPorts(
            StackPanel panel,
            string heading,
            System.Collections.Generic.IEnumerable<FlowPortDefinition> ports,
            BlockHelpEntry? help,
            Brush text,
            Brush muted)
        {
            var list = ports.ToList();
            if (list.Count == 0)
                return;

            panel.Children.Add(new TextBlock
            {
                Text = heading.ToUpperInvariant(),
                Foreground = muted,
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 3)
            });

            foreach (var port in list)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
                row.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = FlowNodeControl.PortBrush(port.ValueKind), Margin = new Thickness(0, 4, 7, 0), VerticalAlignment = VerticalAlignment.Top });

                var description = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 262, FontSize = 11, Foreground = muted };
                description.Inlines.Add(new System.Windows.Documents.Run(port.Name) { FontWeight = FontWeights.SemiBold, Foreground = text });
                description.Inlines.Add(new System.Windows.Documents.Run($" ({KindName(port.ValueKind)}{(port.Direction == FlowPortDirection.Input && !port.Required ? ", optional" : string.Empty)})"));
                if (help != null && help.Ports.TryGetValue(port.Name, out var meaning))
                    description.Inlines.Add(new System.Windows.Documents.Run("  " + meaning));

                row.Children.Add(description);
                panel.Children.Add(row);
            }
        }

        private static string KindName(FlowValueKind kind)
        {
            switch (kind)
            {
                case FlowValueKind.Trigger: return "trigger";
                case FlowValueKind.Boolean: return "true / false";
                case FlowValueKind.Integer: return "whole number";
                case FlowValueKind.Number: return "number";
                case FlowValueKind.String: return "text";
                default: return "any value";
            }
        }

        private static Brush CategoryColor(string category)
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
    }
}
