#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CodeBridge.VisualStudio.Monitor
{
    /// <summary>A live line chart of the numbers the board prints (see <see cref="PlotParser"/>): one coloured line per series, auto-scaled.</summary>
    internal sealed class PlotterControl : UserControl
    {
        internal static readonly Color[] Palette =
        {
            Color.FromRgb(0, 216, 143), Color.FromRgb(58, 130, 246), Color.FromRgb(245, 170, 48), Color.FromRgb(248, 81, 99),
            Color.FromRgb(194, 86, 255), Color.FromRgb(56, 189, 248), Color.FromRgb(250, 204, 21), Color.FromRgb(248, 113, 164)
        };

        private const double LeftMargin = 52;
        private const double Padding = 8;

        private readonly Canvas _canvas = new Canvas { ClipToBounds = true };
        private readonly WrapPanel _legend = new WrapPanel { Margin = new Thickness(8, 4, 8, 2) };

        public PlotterControl()
        {
            Data = new PlotData();

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(_legend, 0);
            Grid.SetRow(_canvas, 1);
            grid.Children.Add(_legend);
            grid.Children.Add(_canvas);
            Content = grid;

            SizeChanged += (_, __) => Render();
        }

        public PlotData Data { get; }

        /// <summary>Number of lines drawn at the last render (for tests).</summary>
        internal int DrawnSeries { get; private set; }

        public void Clear()
        {
            Data.Clear();
            Render();
        }

        internal static Color ColorOf(int index) => Palette[index % Palette.Length];

        public void Render()
        {
            _canvas.Children.Clear();
            _legend.Children.Clear();
            DrawnSeries = 0;

            var width = _canvas.ActualWidth;
            var height = _canvas.ActualHeight;
            if (width < LeftMargin + 40 || height < 40)
                return;

            var (min, max) = Data.Range();
            var plotWidth = width - LeftMargin - Padding;
            var plotHeight = height - 2 * Padding;

            // grid and axis labels
            for (var i = 0; i <= 4; i++)
            {
                var y = Padding + plotHeight * i / 4.0;
                _canvas.Children.Add(new Line { X1 = LeftMargin, X2 = width - Padding, Y1 = y, Y2 = y, Stroke = Brush("VsToolWindowBorder", 0.8), StrokeThickness = 1 });
                var label = new TextBlock { Text = Format(max - (max - min) * i / 4.0), FontSize = 10, Width = LeftMargin - 6, TextAlignment = TextAlignment.Right };
                label.SetResourceReference(TextBlock.ForegroundProperty, "VsGrayText");
                Canvas.SetLeft(label, 0);
                Canvas.SetTop(label, y - 7);
                _canvas.Children.Add(label);
            }

            var index = 0;
            foreach (var name in Data.Names)
            {
                var values = Data.Values(name);
                var color = ColorOf(index++);

                if (values.Count > 1)
                {
                    var points = new PointCollection(values.Count);
                    var span = Math.Max(Data.Window - 1, 1);
                    var offset = 0; // the line grows from the left and scrolls once the window is full
                    for (var i = 0; i < values.Count; i++)
                    {
                        var x = LeftMargin + plotWidth * (offset + i) / span;
                        var y = Padding + plotHeight * (1 - (values[i] - min) / (max - min));
                        points.Add(new Point(x, y));
                    }

                    _canvas.Children.Add(new Polyline { Points = points, Stroke = new SolidColorBrush(color), StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round });
                    DrawnSeries++;
                }

                AddLegend(name, values.Count == 0 ? double.NaN : values[values.Count - 1], color);
            }
        }

        private void AddLegend(string name, double last, Color color)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
            item.Children.Add(new Rectangle { Width = 10, Height = 10, RadiusX = 2, RadiusY = 2, Fill = new SolidColorBrush(color), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            var text = new TextBlock { Text = double.IsNaN(last) ? name : name + ": " + Format(last), FontSize = 11 };
            text.SetResourceReference(TextBlock.ForegroundProperty, "VsToolWindowText");
            item.Children.Add(text);
            _legend.Children.Add(item);
        }

        private Brush Brush(string resourceKey, double opacity)
        {
            var brush = TryFindResource(resourceKey) as SolidColorBrush;
            var color = brush?.Color ?? Color.FromRgb(63, 63, 70);
            return new SolidColorBrush(Color.FromArgb((byte)(255 * opacity), color.R, color.G, color.B));
        }

        internal static string Format(double value)
        {
            var abs = Math.Abs(value);
            if (abs >= 1000) return value.ToString("0", CultureInfo.InvariantCulture);
            if (abs >= 10) return value.ToString("0.#", CultureInfo.InvariantCulture);
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
