#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using CodeBridge.VisualStudio.Editor;

namespace CodeBridge.VisualStudio.Tour
{
    internal enum DiagramKind
    {
        None,
        HowItFits,
        AnyHost
    }

    internal sealed class TourCode
    {
        public TourCode(string title, string fileName)
        {
            Title = title;
            FileName = fileName;
        }

        public string Title { get; }

        /// <summary>File under Tour\Code in the extension (compiled sample code, so it never goes stale).</summary>
        public string FileName { get; }
    }

    internal sealed class TourChapter
    {
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string Level { get; set; } = string.Empty;
        public string[] Story { get; set; } = new string[0];
        public string[] Steps { get; set; } = new string[0];
        public DemoKind[] Demos { get; set; } = new DemoKind[0];
        public DiagramKind Diagram { get; set; }

        /// <summary>Item template folder + flow file of the example this chapter builds (null when there is none).</summary>
        public string? ExampleFolder { get; set; }
        public string? ExampleFile { get; set; }

        public TourCode[] Code { get; set; } = new TourCode[0];
    }

    /// <summary>The story told by the CodeBridge Tour: from the first blink to using the SDK in any .NET host.</summary>
    internal static class TourContent
    {
        public static readonly IReadOnlyList<TourChapter> Chapters = new List<TourChapter>
        {
            new TourChapter
            {
                Title = "Welcome to CodeBridge",
                Subtitle = "Visual Studio on one side, a real board on the other.",
                Level = "Start here",
                Diagram = DiagramKind.HowItFits,
                Story = new[]
                {
                    "CodeBridge lets you control real hardware from .NET without writing C or C++. You draw a flow with blocks, press Run, and an ESP32 or Arduino on your desk does what you drew.",
                    "Three pieces work together: this extension (the editor you are using), a small bridge firmware that lives on the board, and an SDK that exposes the board as a normal C# object. The designer and your own code talk to the same firmware, so what you learn here carries over.",
                    "This tour follows one small project, a desk lamp that gets smarter every chapter, and ends with the most advanced thing you can do: running the very same board from a web API or a Windows service."
                },
                Steps = new[]
                {
                    "Plug in your ESP32 with a USB data cable.",
                    "Open or create any .cbflow file (Add > New Item > CodeBridge Example 1).",
                    "In the toolbar pick the Port of your board and press Upload Firmware once.",
                    "Press Connect: the status bar shows the firmware version. You are ready."
                }
            },

            new TourChapter
            {
                Title = "First light",
                Subtitle = "Make the on-board LED blink.",
                Level = "Beginner",
                ExampleFolder = "CodeBridgeExample1Blink",
                ExampleFile = "Example1_Blink.cbflow",
                Demos = new[] { DemoKind.Trigger, DemoKind.Blink },
                Story = new[]
                {
                    "Every maker's first victory is making something blink. A flow always starts with a Manual Trigger, the start button of your program. Whatever you connect after it runs when you press Run.",
                    "Blink LED turns a pin on, waits and turns it off again. Most ESP32 DevKit boards have an LED on GPIO 2, so you do not need any wiring to see it work."
                },
                Steps = new[]
                {
                    "Press the button below to add Example 1 to your project.",
                    "Hover the two blocks to see what they do.",
                    "Press Run in the toolbar. The LED lights for half a second.",
                    "Select Blink LED and change Duration in the Properties panel, then Run again."
                },
                Code = new[] { new TourCode("Example 1 in C#", "Example1_Blink.cs") }
            },

            new TourChapter
            {
                Title = "Find your rhythm",
                Subtitle = "Chain blocks and use time.",
                Level = "Beginner",
                ExampleFolder = "CodeBridgeExample2Pattern",
                ExampleFile = "Example2_BlinkPattern.cbflow",
                Demos = new[] { DemoKind.Timer, DemoKind.Blink },
                Story = new[]
                {
                    "Blocks connect left to right: the output of one block triggers the next. Put a Timer between two Blink LED blocks and you control the pause. Three blinks with two pauses make a pattern.",
                    "Flows run once and finish. Tick Loop in the toolbar to repeat the flow until you press Stop; the board stays connected, so it is fast."
                },
                Steps = new[]
                {
                    "Add Example 2 and press Run: short, short, long.",
                    "Drag a Timer from the Toolbox onto a wire's gap and rewire it (drag from a port to another port).",
                    "Press Arrange in the toolbar to tidy the blocks.",
                    "Tick Loop and press Run to watch the lamp repeat its pattern."
                },
                Code = new[] { new TourCode("Example 2 in C#", "Example2_Pattern.cs") }
            },

            new TourChapter
            {
                Title = "Teach it to see",
                Subtitle = "Read a sensor and react.",
                Level = "Intermediate",
                ExampleFolder = "CodeBridgeExample3NightLight",
                ExampleFile = "Example3_NightLight.cbflow",
                Demos = new[] { DemoKind.AnalogRead, DemoKind.Compare },
                Story = new[]
                {
                    "Now the lamp learns to see. Analog Read measures a voltage (0 to 4095 on the ESP32), Compare turns it into true or false, and Digital Write switches the LED. Data flows through wires just like triggers do.",
                    "A Debug block prints the reading in the Output window so you can tune the threshold. Run with Loop and cover the sensor with your hand."
                },
                Steps = new[]
                {
                    "Wire a light sensor (LDR) or a potentiometer to GPIO 34: 3V3, sensor, GPIO 34, 10 kOhm resistor, GND.",
                    "Add Example 3, tick Loop and press Run.",
                    "Watch the CodeBridge pane of the Output window while you cover the sensor.",
                    "Change the Number block until the LED switches where you want."
                },
                Code = new[] { new TourCode("Example 3 in C#", "Example3_NightLight.cs") }
            },

            new TourChapter
            {
                Title = "Give it a hand",
                Subtitle = "Move things with a servo.",
                Level = "Intermediate",
                ExampleFolder = "CodeBridgeExample4Servo",
                ExampleFile = "Example4_ServoSweep.cbflow",
                Demos = new[] { DemoKind.Servo, DemoKind.Timer },
                Story = new[]
                {
                    "Hobby servos turn to an exact angle between 0 and 180 degrees: perfect for a lamp arm, a camera pan or a little door. Servo Write sets the angle; a Timer gives it time to arrive.",
                    "Servos draw more current than the board can give. Power them from an external 5 V supply and join its ground with the board's ground."
                },
                Steps = new[]
                {
                    "Connect the servo signal wire to GPIO 13 (power from 5 V, ground shared).",
                    "Add Example 4 and press Run: 0, 180, then 90 degrees.",
                    "Select a Servo Write block and edit its Angle."
                },
                Code = new[] { new TourCode("Example 4 in C#", "Example4_Servo.cs") }
            },

            new TourChapter
            {
                Title = "Press to play",
                Subtitle = "Inputs: a button controls the LED.",
                Level = "Intermediate",
                ExampleFolder = "CodeBridgeExample5Button",
                ExampleFile = "Example5_ButtonLed.cbflow",
                Demos = new[] { DemoKind.PinMode, DemoKind.DigitalRead },
                Story = new[]
                {
                    "Pins can listen as well as speak. Pin Mode declares GPIO 4 as an input with a pull-down resistor, Digital Read says whether the button is pressed, and the result is wired straight into Digital Write.",
                    "This is the shape of every interactive project: read the world, decide, act. The next step up is Interrupt Input, which reacts the instant a pin changes instead of checking over and over (find it in the Advanced group of the Toolbox)."
                },
                Steps = new[]
                {
                    "Wire a push button between 3V3 and GPIO 4.",
                    "Add Example 5, tick Loop and press Run.",
                    "Press the button: the LED follows it."
                },
                Code = new[] { new TourCode("Example 5 in C#", "Example5_Button.cs") }
            },

            new TourChapter
            {
                Title = "Beyond the designer",
                Subtitle = "The same board in a console app, a web API or a Windows service.",
                Level = "Advanced",
                Diagram = DiagramKind.AnyHost,
                Story = new[]
                {
                    "The designer is just one client of the board. The CodeBridge SDK is a set of NuGet packages, so the board becomes an object you can use from any .NET 8 program: a console tool, an ASP.NET web API, a worker that runs as a Windows service, a WinForms app with the drag-and-drop components.",
                    "Install it with: dotnet add package CodeBridge.ESP32. Then connect, and use board.Gpio, servos, sensors, displays, MQTT and more. The tabs below are real, compiled samples from the samples folder of the repository.",
                    "A good path: prototype with blocks, understand the behaviour, then press Export C# in the toolbar. CodeBridge writes the flow as a readable class (or a whole console Program.cs) that you can add to any project and keep editing as normal code."
                },
                Steps = new[]
                {
                    "Console: read a sensor every second (Integration.Console).",
                    "Web API: POST /led/on and GET /sensor/34 over HTTP (Integration.Api).",
                    "Windows service: log a reading to CSV every minute and reconnect by itself (Integration.WindowsService).",
                    "Export: in the flow editor press Export C# > Add to project as a class, then call YourFlow.RunAsync(board) from any of the hosts above.",
                    "WinForms: drop CodeBridgeFlowControl and CodeBridgeBoardComponent from the Toolbox onto a form."
                },
                Code = new[]
                {
                    new TourCode("Console", "Console_Program.cs"),
                    new TourCode("Web API", "Api_Program.cs"),
                    new TourCode("Windows service", "Service_Program.cs"),
                    new TourCode("Service worker", "Service_Worker.cs")
                }
            },

            new TourChapter
            {
                Title = "What comes next",
                Subtitle = "Wi-Fi, more boards, more drivers.",
                Level = "Explore",
                Story = new[]
                {
                    "Go wireless: give the board a Wi-Fi network once (see the WiFiSetup sample), then type its IP address in the Port box instead of a COM port.",
                    "The ESP32 package already includes drivers for DHT, BME280, DS18B20, BH1750, MPU6050, ultrasonic and PIR sensors, OLED and LCD displays, NeoPixels, stepper and DC motors, relays, MQTT and OTA updates. Explore them from code today; they are coming to the block catalog.",
                    "On the roadmap: controlling the RGB lighting of your PC through OpenRGB from the same flows."
                },
                Steps = new[]
                {
                    "Browse docs/examples.md and the samples folder for more ideas.",
                    "Open the CodeBridge Setup window (Tools menu) to add the Toolbox components to a WinForms project.",
                    "Tell us what you build and what is missing: open an issue in the repository."
                }
            }
        };

        public static string CodeDirectory => Path.Combine(ExtensionPaths.Directory, "Tour", "Code");

        public static string ReadCode(TourCode code)
        {
            try
            {
                var path = Path.Combine(CodeDirectory, code.FileName);
                return File.Exists(path) ? File.ReadAllText(path) : "// This sample is not available in this installation.";
            }
            catch (IOException ex)
            {
                return "// Could not read the sample: " + ex.Message;
            }
        }

        /// <summary>A file name that does not exist yet in the folder (Example_2, Example_3...), so examples never overwrite each other.</summary>
        public static string UniquePath(string directory, string name, string extension)
        {
            var path = Path.Combine(directory, name + extension);
            for (var i = 2; File.Exists(path); i++)
                path = Path.Combine(directory, $"{name}_{i}{extension}");

            return path;
        }

        public static string? ExamplePath(TourChapter chapter) =>
            chapter.ExampleFolder == null || chapter.ExampleFile == null
                ? null
                : Path.Combine(ExtensionPaths.Directory, "ItemTemplates", chapter.ExampleFolder, chapter.ExampleFile);
    }
}
