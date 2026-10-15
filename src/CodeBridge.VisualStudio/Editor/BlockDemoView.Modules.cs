#nullable enable
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CodeBridge.VisualStudio.Editor
{
    /// <summary>The animated explanations of the sensor, module and helper blocks.</summary>
    internal sealed partial class BlockDemoView
    {
        private static readonly Color Sky = Color.FromRgb(56, 189, 248);
        private static readonly Color Mauve = Color.FromRgb(203, 166, 247);
        private static readonly Color Fuchsia = Color.FromRgb(232, 121, 249);

        private void BuildModuleDemo(DemoKind kind)
        {
            switch (kind)
            {
                case DemoKind.Temperature: BuildTemperature(); break;
                case DemoKind.Light: BuildLight(); break;
                case DemoKind.Distance: BuildDistance(); break;
                case DemoKind.Motion: BuildMotion(); break;
                case DemoKind.Press: BuildPress(); break;
                case DemoKind.Tilt: BuildTilt(); break;
                case DemoKind.Power: BuildPower(); break;
                case DemoKind.ColorSense: BuildColorSense(); break;
                case DemoKind.Level: BuildLevel(); break;
                case DemoKind.Relay: BuildRelay(); break;
                case DemoKind.Sound: BuildSound(); break;
                case DemoKind.RgbLed: BuildRgbLed(); break;
                case DemoKind.Pixels: BuildPixels(); break;
                case DemoKind.Screen: BuildScreen(); break;
                case DemoKind.Motor: BuildMotor(stepped: false); break;
                case DemoKind.Stepper: BuildMotor(stepped: true); break;
                case DemoKind.Gate: BuildGate(); break;
                case DemoKind.Calc: BuildCalc(); break;
            }
        }

        private void BuildTemperature()
        {
            Total = 3.6;
            var tube = new Rectangle { Width = 14, Height = 52, Stroke = _text, StrokeThickness = 2, RadiusX = 7, RadiusY = 7, Fill = Brushes.Transparent };
            Place(tube, 40, 8);
            var bulb = Dot(47, 66, 10, Sky);
            var mercury = new Rectangle { Width = 8, Height = 48, Fill = new SolidColorBrush(Sky), RadiusX = 4, RadiusY = 4 };
            mercury.RenderTransformOrigin = new Point(0.5, 1);
            mercury.RenderTransform = new ScaleTransform(1, 0.2);
            Place(mercury, 43, 11);
            Wire(70, 40, 150, 40);
            var value = Chip(150, 24, 100, 32, "21.5 °C", Sky);
            Label("temperature", 150, 60, 10, _muted);
            var dot = Dot(0, 40, 4, Green);

            Anim(mercury, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)", new[] { (0.0, 0.2), (1.8, 0.85), (3.6, 0.2) }, discrete: false);
            Caption(value.Text, (0, "21.5 °C"), (1.2, "27.0 °C"), (1.8, "31.2 °C"), (2.6, "26.4 °C"));
            Move(dot, (0, 76), (1.5, 144));
            Fade(dot, (0, 1), (1.4, 1), (1.5, 0), (3.6, 0));
            Tint(bulb, (0, Sky), (1.8, Red), (3.0, Sky));
        }

        private void BuildLight()
        {
            Total = 3.2;
            var glow = new Ellipse { Width = 72, Height = 72, Fill = new SolidColorBrush(Yellow), Opacity = 0.1 };
            Place(glow, 20, 6);
            Dot(56, 42, 14, Yellow);
            for (var i = 0; i < 8; i++)
            {
                var angle = i * Math.PI / 4;
                Children.Add(new Line { X1 = 56 + Math.Cos(angle) * 20, Y1 = 42 + Math.Sin(angle) * 20, X2 = 56 + Math.Cos(angle) * 28, Y2 = 42 + Math.Sin(angle) * 28, Stroke = new SolidColorBrush(Yellow), StrokeThickness = 2, StrokeEndLineCap = PenLineCap.Round });
            }

            Wire(96, 42, 160, 42);
            var value = Chip(160, 26, 100, 32, "120 lux", Amber);
            Fade(glow, (0, 0.1), (1.4, 0.55), (2.4, 0.55), (3.2, 0.1));
            Caption(value.Text, (0, "120 lux"), (1.2, "900 lux"), (2.4, "4500 lux"), (3.0, "120 lux"));
        }

        private void BuildDistance()
        {
            Total = 3.0;
            var sensor = new Rectangle { Width = 22, Height = 34, Fill = new SolidColorBrush(Blue), RadiusX = 3, RadiusY = 3 };
            Place(sensor, 14, 25);
            for (var i = 0; i < 3; i++)
            {
                var wave = new Path { Data = Geometry.Parse("M0,-10 A14,14 0 0 1 0,10"), Stroke = new SolidColorBrush(Blue), StrokeThickness = 2, Opacity = 0 };
                wave.RenderTransformOrigin = new Point(0, 0.5);
                wave.RenderTransform = new ScaleTransform(1, 1);
                Place(wave, 40 + i * 14, 42);
                var start = i * 0.25;
                Fade(wave, (0, 0), (start, 0), (start + 0.1, 1), (start + 0.6, 0), (3.0, 0));
            }

            var wall = new Rectangle { Width = 10, Height = 50, Fill = new SolidColorBrush(Amber), RadiusX = 2, RadiusY = 2 };
            Place(wall, 190, 17);
            var value = Chip(100, 62, 74, 20, "120 cm", Blue);
            Move(wall, (0, 200), (1.5, 100), (3.0, 200));
            Caption(value.Text, (0, "120 cm"), (0.8, "80 cm"), (1.5, "20 cm"), (2.2, "80 cm"), (3.0, "120 cm"));
        }

        private void BuildMotion()
        {
            Total = 3.6;
            var lamp = new Ellipse { Width = 30, Height = 30, Fill = new SolidColorBrush(Off), Stroke = _text, StrokeThickness = 1.5 };
            Place(lamp, 218, 27);
            var sensor = new Path { Data = Geometry.Parse("M0,20 A22,22 0 0 1 44,20 Z"), Fill = new SolidColorBrush(Blue) };
            Place(sensor, 120, 28);
            var person = Dot(0, 54, 6, Amber);
            var body = Label("someone", 0, 62, 10, _muted);
            var state = Label("no motion", 160, 12, 11, _text);

            Move(person, (0, 14), (0.4, 14), (2.0, 150), (3.6, 150));
            Fade(person, (0, 1), (3.4, 1), (3.6, 0));
            Move(body, (0, 4), (0.4, 4), (2.0, 140), (3.6, 140));
            Fade(body, (0, 1), (3.4, 1), (3.6, 0));
            Tint(lamp, (0, Off), (1.4, Green), (3.0, Off));
            Caption(state, (0, "no motion"), (1.4, "MOTION"), (3.0, "no motion"));
        }

        private void BuildPress()
        {
            Total = 3.0;
            var baseRect = new Rectangle { Width = 60, Height = 12, Fill = new SolidColorBrush(Off), RadiusX = 3, RadiusY = 3 };
            Place(baseRect, 30, 58);
            var cap = new Rectangle { Width = 40, Height = 22, Fill = new SolidColorBrush(Blue), RadiusX = 5, RadiusY = 5 };
            Place(cap, 40, 28);
            Wire(100, 50, 170, 50);
            var state = Chip(170, 34, 90, 32, "released", Off);

            Anim(cap, "(Canvas.Top)", new[] { (0.0, 28.0), (1.0, 28.0), (1.15, 40.0), (2.0, 40.0), (2.15, 28.0), (3.0, 28.0) }, discrete: false);
            Caption(state.Text, (0, "released"), (1.1, "PRESSED"), (2.1, "released"));
            Tint(state.Border, (0, Off), (1.1, Green), (2.1, Off));
        }

        private void BuildTilt()
        {
            Total = 3.6;
            var board = new Rectangle { Width = 90, Height = 8, Fill = new SolidColorBrush(Blue), RadiusX = 2, RadiusY = 2 };
            board.RenderTransformOrigin = new Point(0.5, 0.5);
            board.RenderTransform = new RotateTransform(0);
            Place(board, 20, 38);
            Rotate(board, (0, 0), (0.6, 0), (1.5, -28), (2.4, 24), (3.2, 0), (3.6, 0));
            var value = Chip(140, 14, 118, 28, "ax 0.0 az 9.8", Blue);
            Label("m/s²", 140, 46, 10, _muted);
            Caption(value.Text, (0, "ax 0.0 az 9.8"), (1.5, "ax -4.6 az 8.7"), (2.4, "ax 4.0 az 9.0"), (3.2, "ax 0.0 az 9.8"));
        }

        private void BuildPower()
        {
            Total = 3.0;
            var bolt = new Path { Data = Geometry.Parse("M28,6 L12,40 L26,40 L20,72 L44,32 L30,32 Z"), Fill = new SolidColorBrush(Yellow) };
            Place(bolt, 24, 4);
            Wire(70, 42, 130, 42);
            var volts = Chip(130, 14, 120, 26, "5.02 V", Amber);
            var current = Chip(130, 46, 120, 26, "120 mA", Blue);
            Caption(current.Text, (0, "120 mA"), (1.0, "340 mA"), (2.0, "85 mA"));
            Caption(volts.Text, (0, "5.02 V"), (1.0, "4.96 V"), (2.0, "5.03 V"));
            Fade(bolt, (0, 1), (1.0, 0.55), (2.0, 1), (3.0, 1));
        }

        private void BuildColorSense()
        {
            Total = 3.6;
            var sample = new Rectangle { Width = 56, Height = 56, RadiusX = 6, RadiusY = 6, Fill = new SolidColorBrush(Red), Stroke = _text, StrokeThickness = 1.5 };
            Place(sample, 24, 14);
            Wire(88, 42, 140, 42);
            var value = Chip(140, 26, 110, 32, "255, 0, 0", Blue);
            Tint(sample, (0, Red), (1.2, Green), (2.4, Blue));
            Caption(value.Text, (0, "255, 0, 0"), (1.2, "0, 216, 143"), (2.4, "58, 130, 246"));
        }

        private void BuildLevel()
        {
            Total = 3.6;
            var track = new Rectangle { Width = 150, Height = 14, Fill = new SolidColorBrush(Off), RadiusX = 7, RadiusY = 7 };
            Place(track, 20, 36);
            var fill = new Rectangle { Width = 10, Height = 14, Fill = new SolidColorBrush(Blue), RadiusX = 7, RadiusY = 7 };
            Place(fill, 20, 36);
            Anim(fill, "Width", new[] { (0.0, 10.0), (1.2, 75.0), (2.4, 140.0), (3.6, 10.0) }, discrete: false);
            var value = Chip(190, 28, 70, 30, "7 %", Blue);
            Caption(value.Text, (0, "7 %"), (1.2, "50 %"), (2.4, "93 %"));
            Label("0 .. 100 %", 20, 56, 10, _muted);
        }

        private void BuildRelay()
        {
            Total = 3.4;
            Chip(14, 28, 62, 28, "GPIO 26", Blue);
            Wire(76, 42, 112, 42);
            Dot(112, 42, 4, _text);
            Dot(156, 42, 4, _text);
            var lever = new Line { X1 = 112, Y1 = 42, X2 = 156, Y2 = 42, Stroke = _text, StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            lever.RenderTransform = new RotateTransform(-30, 112, 42);
            Children.Add(lever);
            Wire(160, 42, 196, 42);
            var lamp = new Ellipse { Width = 34, Height = 34, Fill = new SolidColorBrush(Off), Stroke = _text, StrokeThickness = 1.5 };
            Place(lamp, 196, 25);
            Rotate(lever, (0, -30), (1.0, -30), (1.2, 0), (2.4, 0), (2.6, -30), (3.4, -30));
            Tint(lamp, (0, Off), (1.2, Yellow), (2.6, Off));
            var state = Label("OFF", 114, 60, 11, _muted);
            Caption(state, (0, "OFF"), (1.2, "ON  (click)"), (2.6, "OFF"));
        }

        private void BuildSound()
        {
            Total = 2.8;
            var cone = new Path { Data = Geometry.Parse("M0,10 L14,10 L30,0 L30,40 L14,30 L0,30 Z"), Fill = new SolidColorBrush(Blue) };
            Place(cone, 30, 22);
            for (var i = 0; i < 3; i++)
            {
                var arc = new Path { Data = Geometry.Parse("M0,-8 A10,10 0 0 1 0,8"), Stroke = new SolidColorBrush(Amber), StrokeThickness = 2.5, Opacity = 0, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                Place(arc, 72 + i * 16, 42);
                var start = 0.2 + i * 0.22;
                Fade(arc, (0, 0), (start, 0), (start + 0.1, 1), (start + 0.9, 0.2), (2.8, 0));
            }

            var value = Chip(180, 26, 80, 30, "1000 Hz", Amber);
            Caption(value.Text, (0, "1000 Hz"), (1.4, "2000 Hz"));
            Label("beep", 180, 62, 10, _muted);
        }

        private void BuildRgbLed()
        {
            Total = 3.6;
            var led = new Ellipse { Width = 52, Height = 52, Fill = new SolidColorBrush(Red) };
            Place(led, 36, 16);
            Chip(130, 8, 36, 18, "R", Red);
            Chip(130, 32, 36, 18, "G", Green);
            Chip(130, 56, 36, 18, "B", Blue);
            var value = Label("255, 0, 0", 176, 32, 12, _text);
            Tint(led, (0, Red), (1.2, Green), (2.4, Blue));
            Caption(value, (0, "255, 0, 0"), (1.2, "0, 216, 143"), (2.4, "58, 130, 246"));
        }

        private void BuildPixels()
        {
            Total = 3.2;
            var colors = new[] { Sky, Blue, Mauve, Fuchsia, Amber, Green, Sky, Blue };
            for (var i = 0; i < 8; i++)
            {
                var pixel = Dot(24 + i * 30, 42, 10, Off);
                var start = i * 0.3;
                Tint(pixel, (0, Off), (start, colors[i]), (start + 0.5, Off));
            }

            Label("8 pixels", 24, 66, 10, _muted);
        }

        private void BuildScreen()
        {
            Total = 3.6;
            var screen = new Border { Width = 180, Height = 54, BorderBrush = _text, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(60, 56, 189, 248)) };
            Place(screen, 14, 14);
            var line1 = Label("Temp: 21.5 C", 26, 22, 14, _text);
            line1.FontFamily = new FontFamily("Consolas");
            var line2 = Label("Hello", 26, 42, 14, _muted);
            line2.FontFamily = new FontFamily("Consolas");
            Caption(line1, (0, "Temp: 21.5 C"), (1.2, "Temp: 27.0 C"), (2.4, "Temp: 31.2 C"));
            Label("{value} is replaced", 14, 70, 10, _muted);
            Wire(194, 42, 236, 42);
            var dot = Dot(0, 42, 4, Green);
            Move(dot, (0, 198), (1.0, 232));
            Fade(dot, (0, 0), (0.1, 1), (1.0, 1), (1.1, 0), (3.6, 0));
        }

        private void BuildMotor(bool stepped)
        {
            Total = 3.6;
            var wheel = new Ellipse { Width = 54, Height = 54, Stroke = _text, StrokeThickness = 2.5, Fill = Brushes.Transparent };
            Place(wheel, 28, 14);
            var spoke = new Line { X1 = 55, Y1 = 41, X2 = 55, Y2 = 20, Stroke = new SolidColorBrush(Amber), StrokeThickness = 4, StrokeEndLineCap = PenLineCap.Round };
            spoke.RenderTransform = new RotateTransform(0, 55, 41);
            Children.Add(spoke);
            Dot(55, 41, 4, _text);
            var value = Chip(130, 26, 120, 30, stepped ? "512 steps" : "+60 %", Blue);

            if (stepped)
            {
                Rotate(spoke, (0, 0), (0.5, 0), (0.6, 45), (1.1, 45), (1.2, 90), (1.7, 90), (1.8, 135), (2.3, 135), (2.4, 180), (3.6, 180));
                Caption(value.Text, (0, "512 steps"), (1.8, "-512 steps"));
            }
            else
            {
                Rotate(spoke, (0, 0), (1.8, 540), (1.81, 540), (3.6, 0));
                Caption(value.Text, (0, "+60 %"), (1.8, "-60 %"));
            }

            Label(stepped ? "moves exact steps" : "speed and direction", 130, 62, 10, _muted);
        }

        private void BuildGate()
        {
            Total = 3.2;
            var a = Chip(14, 12, 54, 26, "true", Green);
            var b = Chip(14, 46, 54, 26, "false", Off);
            Label("AND", 100, 32, 16, _text);
            Wire(68, 25, 90, 36);
            Wire(68, 59, 90, 48);
            Wire(140, 42, 176, 42);
            var result = Chip(176, 26, 70, 32, "false", Off);
            Caption(a.Text, (0, "true"), (1.6, "true"));
            Caption(b.Text, (0, "false"), (0.8, "true"), (2.4, "false"));
            Tint(b.Border, (0, Off), (0.8, Green), (2.4, Off));
            Caption(result.Text, (0, "false"), (0.8, "true"), (2.4, "false"));
            Tint(result.Border, (0, Off), (0.8, Green), (2.4, Off));
        }

        private void BuildCalc()
        {
            Total = 3.6;
            var a = Chip(10, 26, 56, 30, "30", Blue);
            Label("×", 76, 28, 18, _text);
            var b = Chip(94, 26, 44, 30, "2", Amber);
            Label("=", 146, 28, 18, _text);
            var result = Chip(168, 26, 84, 30, "60", Green);
            Caption(a.Text, (0, "30"), (1.8, "100"));
            Caption(result.Text, (0, "60"), (1.8, "200"));
            Scale(result.Border, (0, 1), (0.1, 1.08), (0.35, 1), (1.8, 1), (1.9, 1.08), (2.15, 1));
            Label("add, multiply, divide...", 10, 66, 10, _muted);
        }
    }
}
