#nullable enable
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace CodeBridge.VisualStudio.Editor
{
    /// <summary>
    /// A tiny looping vector animation that shows what a block does (an LED lighting up, a timer counting, a servo
    /// sweeping...). Drawn with WPF shapes so it follows the Visual Studio theme, costs nothing in the VSIX and never
    /// needs to be re-recorded when a block changes. Start() when the tooltip opens, Stop() when it closes.
    /// </summary>
    internal sealed partial class BlockDemoView : Canvas
    {
        public const double DemoWidth = 280;
        public const double DemoHeight = 84;

        private static readonly Color Purple = Color.FromRgb(194, 86, 255);
        private static readonly Color Green = Color.FromRgb(0, 216, 143);
        private static readonly Color Blue = Color.FromRgb(58, 130, 246);
        private static readonly Color Amber = Color.FromRgb(245, 170, 48);
        private static readonly Color Red = Color.FromRgb(248, 81, 99);
        private static readonly Color Yellow = Color.FromRgb(255, 214, 10);
        private static readonly Color Off = Color.FromRgb(70, 72, 78);

        private readonly Brush _text;
        private readonly Brush _muted;
        private readonly Brush _line;
        private Storyboard _storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

        public BlockDemoView(DemoKind kind, Brush text, Brush muted, Brush line)
        {
            _text = text;
            _muted = muted;
            _line = line;
            Width = DemoWidth;
            Height = DemoHeight;
            ClipToBounds = true;
            Kind = kind;
            Build(kind);
        }

        public DemoKind Kind { get; }

        public bool HasAnimation => _storyboard.Children.Count > 0;

        public void Start()
        {
            Stop();
            if (HasAnimation)
                _storyboard.Begin(this, true);
        }

        /// <summary>Jumps the running animation to a point in time (used by tests and previews).</summary>
        internal void SeekTo(double seconds)
        {
            if (HasAnimation)
                _storyboard.Seek(this, TimeSpan.FromSeconds(seconds), TimeSeekOrigin.BeginTime);
        }

        public void Stop()
        {
            if (HasAnimation)
                _storyboard.Stop(this);
        }

        private void Build(DemoKind kind)
        {
            switch (kind)
            {
                case DemoKind.Trigger: BuildTrigger(); break;
                case DemoKind.Timer: BuildTimer(); break;
                case DemoKind.Boolean: BuildBoolean(); break;
                case DemoKind.Number: BuildNumber(); break;
                case DemoKind.Compare: BuildCompare(); break;
                case DemoKind.PinMode: BuildPinMode(); break;
                case DemoKind.DigitalRead: BuildDigitalRead(); break;
                case DemoKind.AnalogRead: BuildAnalogRead(); break;
                case DemoKind.DigitalWrite: BuildLed(blink: false); break;
                case DemoKind.Blink: BuildLed(blink: true); break;
                case DemoKind.Sample: BuildSample(); break;
                case DemoKind.Interrupt: BuildInterrupt(); break;
                case DemoKind.Dashboard: BuildDashboard(); break;
                case DemoKind.Servo: BuildServo(); break;
                case DemoKind.Debug: BuildDebug(); break;
                case DemoKind.Map: BuildMap(); break;
                case DemoKind.Pwm: BuildPwm(); break;
                default: BuildModuleDemo(kind); break;
            }

            if (HasAnimation)
                _storyboard.Duration = TimeSpan.FromSeconds(Total);
        }

        private double Total;

        // ------------------------------------------------------------------ demos

        private void BuildTrigger()
        {
            Total = 2.4;
            var button = Chip(14, 26, 54, 32, "Run", Purple);
            Wire(68, 42, 236, 42);
            var dot = Dot(0, 42, 6, Purple);
            var ring = Ring(244, 42, 9, Purple);

            Scale(button.Border, (0, 1), (0.1, 0.86), (0.35, 1));
            Move(dot, (0, 68), (0.25, 68), (1.5, 232));
            Fade(dot, (0, 0), (0.25, 1), (1.5, 1), (1.6, 0));
            Fade(ring, (0, 0), (1.5, 0), (1.55, 1), (2.2, 0));
            Scale(ring, (0, 0.5), (1.5, 0.5), (2.2, 1.5));
        }

        private void BuildTimer()
        {
            Total = 3.0;
            var face = new Ellipse { Width = 56, Height = 56, Stroke = _text, StrokeThickness = 2, Fill = Brushes.Transparent };
            Place(face, 20, 12);
            var hand = new Line { X1 = 48, Y1 = 40, X2 = 48, Y2 = 20, Stroke = new SolidColorBrush(Amber), StrokeThickness = 3, StrokeEndLineCap = PenLineCap.Round };
            hand.RenderTransform = new RotateTransform(0, 48, 40);
            Children.Add(hand);
            Dot(48, 40, 3, _text);
            Label("wait 1000 ms", 92, 24, 11, _text);
            Label("then continue", 92, 42, 10, _muted);
            Wire(190, 42, 236, 42);
            var tick = Dot(0, 42, 6, Purple);

            Rotate(hand, (0, 0), (0.2, 0), (2.0, 360), (3.0, 360));
            Move(tick, (0, 190), (2.0, 190), (2.7, 232));
            Fade(tick, (0, 0), (2.0, 0), (2.05, 1), (2.7, 1), (2.8, 0));
        }

        private void BuildBoolean()
        {
            Total = 3.0;
            var chip = Chip(70, 22, 80, 40, "True", Green);
            Wire(150, 42, 236, 42);
            var dot = Dot(0, 42, 5, Blue);
            Caption(chip.Text, (0, "True"), (1.5, "False"));
            Tint(chip.Border, (0, Green), (1.5, Off));
            Move(dot, (0, 150), (0.4, 150), (1.2, 230), (3.0, 230));
            Fade(dot, (0, 1), (1.3, 1), (1.4, 0), (3.0, 0));
        }

        private void BuildNumber()
        {
            Total = 2.4;
            var chip = Chip(70, 22, 80, 40, "2000", Amber);
            Wire(150, 42, 236, 42);
            var dot = Dot(0, 42, 5, Green);
            Move(dot, (0, 150), (1.6, 232));
            Fade(dot, (0, 1), (1.5, 1), (1.6, 0), (2.4, 0));
            Scale(chip.Border, (0, 1), (0.1, 1.06), (0.3, 1));
        }

        private void BuildCompare()
        {
            Total = 3.2;
            var a = Chip(10, 26, 54, 30, "1500", Blue);
            Label(">", 72, 29, 16, _text);
            Chip(88, 26, 54, 30, "2000", Amber);
            Wire(142, 41, 176, 41);
            var lamp = new Ellipse { Width = 30, Height = 30, Fill = new SolidColorBrush(Red) };
            Place(lamp, 176, 26);
            var result = Label("false", 216, 32, 12, _text);

            Caption(a.Text, (0, "1500"), (1.6, "2500"));
            Tint(lamp, (0, Red), (1.6, Green));
            Caption(result, (0, "false"), (1.6, "true"));
        }

        private void BuildPinMode()
        {
            Total = 4.0;
            Chip(14, 26, 54, 32, "GPIO 4", Blue);
            var arrow = new Path { Data = Geometry.Parse("M0,0 L26,0 M18,-6 L26,0 L18,6"), Stroke = new SolidColorBrush(Green), StrokeThickness = 3, StrokeLineJoin = PenLineJoin.Round };
            Canvas.SetLeft(arrow, 88);
            Canvas.SetTop(arrow, 42);
            arrow.RenderTransformOrigin = new Point(0.5, 0.5);
            arrow.RenderTransform = new RotateTransform(0);
            Children.Add(arrow);
            var mode = Label("OUTPUT", 140, 26, 13, _text);
            var detail = Label("sends signals to LEDs, relays...", 140, 46, 10, _muted);

            Rotate(arrow, (0, 0), (1.9, 0), (2.0, 180), (4.0, 180));
            Caption(mode, (0, "OUTPUT"), (2.0, "INPUT"));
            Caption(detail, (0, "sends signals to LEDs, relays..."), (2.0, "listens to buttons, switches..."));
        }

        private void BuildDigitalRead()
        {
            Total = 3.0;
            Seg(26, 52, 70, 52, _line);
            Dot(70, 52, 4, _text);
            Dot(128, 52, 4, _text);
            Seg(128, 52, 150, 52, _line);
            var lever = new Line { X1 = 70, Y1 = 52, X2 = 128, Y2 = 52, Stroke = _text, StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            lever.RenderTransform = new RotateTransform(-32, 70, 52);
            Children.Add(lever);
            Wire(150, 52, 168, 42);
            var chip = Chip(168, 28, 86, 28, "False", Off);

            Rotate(lever, (0, -32), (1.3, -32), (1.5, 0), (2.7, 0), (2.9, -32));
            Caption(chip.Text, (0, "False"), (1.5, "True"), (2.9, "False"));
            Tint(chip.Border, (0, Off), (1.5, Green), (2.9, Off));
        }

        private void BuildAnalogRead()
        {
            Total = 3.0;
            Seg(14, 60, 200, 60, _line);
            var points = new PointCollection();
            for (var x = 0; x <= 186; x += 6)
                points.Add(new Point(14 + x, 38 - Math.Sin(x / 186.0 * Math.PI * 2) * 22));
            Children.Add(new Polyline { Points = points, Stroke = new SolidColorBrush(Green), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round });
            var cursor = new Line { X1 = 0, Y1 = 8, X2 = 0, Y2 = 70, Stroke = _muted, StrokeThickness = 1 };
            Children.Add(cursor);
            var dot = Dot(0, 38, 4.5, Amber);
            var value = Chip(212, 26, 56, 30, "2048", Blue);

            Move(cursor, (0, 14), (3.0, 200));
            Move(dot, (0, 14), (3.0, 200));
            var sine = new List<(double, double)>();
            for (var step = 0; step <= 12; step++)
                sine.Add((step * 0.25, 38 - Math.Sin(step / 12.0 * Math.PI * 2) * 22));
            Anim(dot, "(Canvas.Top)", sine.ToArray(), discrete: false, offsetByRadius: 4.5);
            Caption(value.Text, (0, "2048"), (0.5, "3500"), (1.0, "2048"), (1.5, "600"), (2.25, "100"), (2.75, "1800"));
        }

        private void BuildLed(bool blink)
        {
            Total = blink ? 2.6 : 3.2;
            Chip(14, 28, 62, 28, "GPIO 2", Blue);
            Wire(76, 42, 150, 42);
            var glow = new Ellipse { Width = 60, Height = 60, Fill = new SolidColorBrush(Yellow), Opacity = 0 };
            Place(glow, 140, 12);
            var led = new Ellipse { Width = 34, Height = 34, Fill = new SolidColorBrush(Off), Stroke = _text, StrokeThickness = 1.5 };
            Place(led, 153, 25);
            var state = Label(blink ? "off" : "LOW", 206, 34, 13, _text);

            if (blink)
            {
                Tint(led, (0, Off), (0.4, Yellow), (1.6, Off));
                Fade(glow, (0, 0), (0.4, 0.4), (1.6, 0), (2.6, 0));
                Caption(state, (0, "off"), (0.4, "ON 500 ms"), (1.6, "off"));
            }
            else
            {
                Tint(led, (0, Off), (1.0, Yellow), (2.2, Off));
                Fade(glow, (0, 0), (1.0, 0.4), (2.2, 0), (3.2, 0));
                Caption(state, (0, "LOW"), (1.0, "HIGH"), (2.2, "LOW"));
            }
        }

        private void BuildSample()
        {
            Total = 3.0;
            Seg(14, 42, 150, 42, _line);
            var bufferFrame = new Rectangle { Width = 96, Height = 40, Stroke = _text, StrokeThickness = 1.5 };
            Place(bufferFrame, 160, 22);
            var fill = new Rectangle { Width = 0, Height = 36, Fill = new SolidColorBrush(Blue), Opacity = 0.8 };
            Place(fill, 162, 24);
            Label("buffer", 184, 64, 10, _muted);

            for (var i = 0; i < 5; i++)
            {
                var dot = Dot(0, 42, 4, Green);
                var start = i * 0.5;
                Move(dot, (0, 14), (start, 14), (start + 0.7, 156));
                Fade(dot, (0, 0), (start, 0), (start + 0.01, 1), (start + 0.65, 1), (start + 0.7, 0));
            }

            Anim(fill, "Width", new[] { (0.0, 0.0), (0.7, 6.0), (2.4, 90.0), (2.9, 90.0), (3.0, 0.0) }, discrete: false);
        }

        private void BuildInterrupt()
        {
            Total = 3.0;
            var wave = new PointCollection
            {
                new Point(14, 58), new Point(80, 58), new Point(80, 24), new Point(140, 24),
                new Point(140, 58), new Point(200, 58), new Point(200, 24), new Point(250, 24)
            };
            Children.Add(new Polyline { Points = wave, Stroke = new SolidColorBrush(Blue), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Miter });
            var cursor = new Line { X1 = 0, Y1 = 8, X2 = 0, Y2 = 72, Stroke = _muted, StrokeThickness = 1 };
            Children.Add(cursor);
            Move(cursor, (0, 14), (3.0, 250));

            foreach (var edge in new[] { 80.0, 140.0, 200.0 })
            {
                var t = (edge - 14) / (250 - 14) * 3.0;
                var spark = Ring(edge, edge == 140.0 ? 58 : 24, 8, Amber);
                Fade(spark, (0, 0), (t, 0), (t + 0.02, 1), (t + 0.35, 0), (3.0, 0));
                Scale(spark, (0, 0.5), (t, 0.5), (t + 0.35, 1.6), (3.0, 1.6));
            }

            var changed = Label("changed!", 200, 66, 11, new SolidColorBrush(Amber));
            Fade(changed, (0, 0), (0.78, 0), (0.8, 1), (1.1, 0), (1.57, 0), (1.59, 1), (1.9, 0), (2.35, 0), (2.37, 1), (2.7, 0), (3.0, 0));
        }

        private void BuildDashboard()
        {
            Total = 3.0;
            Seg(30, 66, 250, 66, _line);
            var heights = new[]
            {
                new[] { 0.3, 0.7, 0.5, 0.9, 0.4 },
                new[] { 0.8, 0.4, 0.9, 0.5, 0.7 },
                new[] { 0.5, 0.9, 0.3, 0.7, 0.8 }
            };

            for (var i = 0; i < 5; i++)
            {
                var bar = new Rectangle { Width = 26, Height = 50, Fill = new SolidColorBrush(i % 2 == 0 ? Blue : Green) };
                Place(bar, 42 + i * 42, 16);
                bar.RenderTransformOrigin = new Point(0.5, 1);
                bar.RenderTransform = new ScaleTransform(1, heights[0][i]);
                Anim(bar, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)",
                    new[] { (0.0, heights[0][i]), (1.0, heights[1][i]), (2.0, heights[2][i]), (3.0, heights[0][i]) }, discrete: false);
            }
        }

        private void BuildServo()
        {
            Total = 3.6;
            var body = new Rectangle { Width = 40, Height = 18, Fill = new SolidColorBrush(Blue), RadiusX = 3, RadiusY = 3 };
            Place(body, 60, 62);
            var arm = new Line { X1 = 80, Y1 = 60, X2 = 30, Y2 = 60, Stroke = new SolidColorBrush(Amber), StrokeThickness = 5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            arm.RenderTransform = new RotateTransform(0, 80, 60);
            Children.Add(arm);
            Dot(80, 60, 6, _text);
            var angle = Chip(176, 26, 70, 30, "0°", Amber);
            Label("angle", 176, 60, 10, _muted);

            Rotate(arm, (0, 0), (0.6, 0), (1.5, 180), (2.2, 180), (3.0, 90), (3.6, 90));
            Caption(angle.Text, (0, "0°"), (0.6, "0° → 180°"), (1.5, "180°"), (2.2, "90°"));
        }

        private void BuildMap()
        {
            Total = 3.6;
            // Two bars: the input range (0-4095) on the left, the output range (0-255) on the right.
            var inTrack = new Rectangle { Width = 16, Height = 64, Fill = new SolidColorBrush(Off), RadiusX = 3, RadiusY = 3 };
            Place(inTrack, 40, 8);
            var outTrack = new Rectangle { Width = 16, Height = 64, Fill = new SolidColorBrush(Off), RadiusX = 3, RadiusY = 3 };
            Place(outTrack, 210, 8);
            var inFill = new Rectangle { Width = 16, Height = 64, Fill = new SolidColorBrush(Blue), RadiusX = 3, RadiusY = 3 };
            inFill.RenderTransformOrigin = new Point(0.5, 1);
            inFill.RenderTransform = new ScaleTransform(1, 0.05);
            Place(inFill, 40, 8);
            var outFill = new Rectangle { Width = 16, Height = 64, Fill = new SolidColorBrush(Green), RadiusX = 3, RadiusY = 3 };
            outFill.RenderTransformOrigin = new Point(0.5, 1);
            outFill.RenderTransform = new ScaleTransform(1, 0.05);
            Place(outFill, 210, 8);
            Wire(62, 40, 204, 40);

            var inValue = Chip(66, 12, 56, 24, "0", Blue);
            var outValue = Chip(140, 44, 56, 24, "0", Green);
            Label("in", 40, 74, 10, _muted);
            Label("out", 210, 74, 10, _muted);

            Anim(inFill, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)", new[] { (0.0, 0.05), (1.2, 0.5), (2.4, 1.0), (3.6, 0.05) }, discrete: false);
            Anim(outFill, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)", new[] { (0.0, 0.05), (1.2, 0.5), (2.4, 1.0), (3.6, 0.05) }, discrete: false);
            Caption(inValue.Text, (0, "0"), (1.2, "2048"), (2.4, "4095"));
            Caption(outValue.Text, (0, "0"), (1.2, "128"), (2.4, "255"));
        }

        private void BuildPwm()
        {
            Total = 3.6;
            // A square wave whose pulses get wider while an LED glows brighter.
            var wave = new Polyline { Stroke = new SolidColorBrush(Green), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
            var points = new PointCollection();
            double x = 14;
            foreach (var duty in new[] { 0.2, 0.5, 0.8 })
            {
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    const double period = 24;
                    points.Add(new Point(x, 60));
                    points.Add(new Point(x, 30));
                    points.Add(new Point(x + period * duty, 30));
                    points.Add(new Point(x + period * duty, 60));
                    points.Add(new Point(x + period, 60));
                    x += period;
                }
            }

            wave.Points = points;
            Children.Add(wave);

            var led = Dot(238, 40, 14, Amber);
            Fade(led, (0, 0.2), (1.2, 0.5), (2.4, 1.0), (3.6, 0.2));
            var duty255 = Chip(190, 66, 60, 20, "51", Amber);
            Caption(duty255.Text, (0, "51"), (1.2, "128"), (2.4, "204"));
        }

        private void BuildDebug()
        {
            Total = 4.0;
            var console = new Border { Width = 252, Height = 64, CornerRadius = new CornerRadius(3), BorderBrush = _line, BorderThickness = new Thickness(1), Background = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)) };
            Place(console, 14, 10);
            var lines = new[] { "[12:00:01] LED turned on", "[12:00:02] light = 2048", "[12:00:03] flow completed" };
            for (var i = 0; i < lines.Length; i++)
            {
                var line = Label(lines[i], 22, 16 + i * 17, 11, i == 2 ? new SolidColorBrush(Green) : _text);
                line.FontFamily = new FontFamily("Consolas");
                Fade(line, (0, 0), (0.4 + i * 0.9, 0), (0.45 + i * 0.9, 1), (3.6, 1), (3.9, 0), (4.0, 0));
            }
        }

        // ------------------------------------------------------------------ drawing helpers

        private sealed class ChipParts
        {
            public Border Border = null!;
            public TextBlock Text = null!;
        }

        private ChipParts Chip(double x, double y, double w, double h, string text, Color accent)
        {
            var label = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var border = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(accent), Child = label };
            border.RenderTransformOrigin = new Point(0.5, 0.5);
            border.RenderTransform = new ScaleTransform(1, 1);
            Place(border, x, y);
            return new ChipParts { Border = border, Text = label };
        }

        private TextBlock Label(string text, double x, double y, double size, Brush brush)
        {
            var label = new TextBlock { Text = text, FontSize = size, Foreground = brush };
            Place(label, x, y);
            return label;
        }

        private Ellipse Dot(double cx, double cy, double r, Color color) => Dot(cx, cy, r, new SolidColorBrush(color));

        private Ellipse Dot(double cx, double cy, double r, Brush brush)
        {
            var dot = new Ellipse { Width = r * 2, Height = r * 2, Fill = brush };
            Place(dot, cx - r, cy - r);
            return dot;
        }

        private Ellipse Ring(double cx, double cy, double r, Color color)
        {
            var ring = new Ellipse { Width = r * 2, Height = r * 2, Stroke = new SolidColorBrush(color), StrokeThickness = 2.5, Opacity = 0 };
            ring.RenderTransformOrigin = new Point(0.5, 0.5);
            ring.RenderTransform = new ScaleTransform(1, 1);
            Place(ring, cx - r, cy - r);
            return ring;
        }

        private void Wire(double x1, double y1, double x2, double y2) => Seg(x1, y1, x2, y2, _line);

        private void Seg(double x1, double y1, double x2, double y2, Brush brush) =>
            Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = 2 });

        private void Place(UIElement element, double x, double y)
        {
            SetLeft(element, x);
            SetTop(element, y);
            Children.Add(element);
        }

        // ------------------------------------------------------------------ animation helpers

        private void Anim(DependencyObject target, string path, (double Time, double Value)[] frames, bool discrete, double offsetByRadius = 0)
        {
            var animation = new DoubleAnimationUsingKeyFrames();
            foreach (var frame in frames)
            {
                var time = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(frame.Time));
                var value = frame.Value - offsetByRadius;
                animation.KeyFrames.Add(discrete ? (DoubleKeyFrame)new DiscreteDoubleKeyFrame(value, time) : new LinearDoubleKeyFrame(value, time));
            }

            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, new PropertyPath(path));
            _storyboard.Children.Add(animation);
        }

        private void Move(UIElement element, params (double Time, double Value)[] frames)
        {
            if (element is Ellipse dot)
            {
                // keep the dot's centre on the requested coordinate
                for (var i = 0; i < frames.Length; i++)
                    frames[i].Value -= dot.Width / 2;
            }

            Anim(element, "(Canvas.Left)", frames, discrete: false);
        }

        private void Fade(UIElement element, params (double Time, double Value)[] frames) => Anim(element, "Opacity", frames, discrete: false);

        private void Scale(UIElement element, params (double Time, double Value)[] frames)
        {
            Anim(element, "(UIElement.RenderTransform).(ScaleTransform.ScaleX)", frames, discrete: false);
            Anim(element, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)", frames, discrete: false);
        }

        private void Rotate(UIElement element, params (double Time, double Value)[] frames) =>
            Anim(element, "(UIElement.RenderTransform).(RotateTransform.Angle)", frames, discrete: false);

        private void Caption(TextBlock block, params (double Time, string Value)[] frames)
        {
            var animation = new ObjectAnimationUsingKeyFrames();
            foreach (var frame in frames)
                animation.KeyFrames.Add(new DiscreteObjectKeyFrame(frame.Value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(frame.Time))));

            Storyboard.SetTarget(animation, block);
            Storyboard.SetTargetProperty(animation, new PropertyPath("(TextBlock.Text)"));
            _storyboard.Children.Add(animation);
        }

        private void Tint(Shape shape, params (double Time, Color Value)[] frames) =>
            ColorOf(shape, "(Shape.Fill).(SolidColorBrush.Color)", frames);

        private void Tint(Border border, params (double Time, Color Value)[] frames) =>
            ColorOf(border, "(Border.Background).(SolidColorBrush.Color)", frames);

        private void ColorOf(DependencyObject target, string path, (double Time, Color Value)[] frames)
        {
            var animation = new ColorAnimationUsingKeyFrames();
            foreach (var frame in frames)
                animation.KeyFrames.Add(new DiscreteColorKeyFrame(frame.Value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(frame.Time))));

            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, new PropertyPath(path));
            _storyboard.Children.Add(animation);
        }
    }
}
