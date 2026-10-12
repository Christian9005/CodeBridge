#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CodeBridge.VisualStudio.Editor;

namespace CodeBridge.VisualStudio.Firmware
{
    /// <summary>"New firmware project": pick the board, a starting point and the folder. PlatformIO does the rest.</summary>
    internal sealed class NewFirmwareProjectWindow : Window
    {
        internal sealed class Choice
        {
            public Choice(string id, string name, string description)
            {
                Id = id;
                Name = name;
                Description = description;
            }

            public string Id { get; }
            public string Name { get; }
            public string Description { get; }
            public override string ToString() => Name;
        }

        internal static readonly Choice[] Boards =
        {
            new Choice("esp32-devkit", "ESP32 DevKit", "The original ESP32 (esp32dev)."),
            new Choice("esp32-s3-devkit", "ESP32-S3 DevKit", "ESP32-S3-DevKitC-1."),
            new Choice("esp32-c3-devkit", "ESP32-C3 DevKit", "ESP32-C3-DevKitM-1."),
            new Choice("arduino-uno", "Arduino Uno", "ATmega328P, 5 V."),
            new Choice("arduino-nano", "Arduino Nano", "New bootloader (most original boards)."),
            new Choice("arduino-nano-old", "Arduino Nano (old bootloader)", "Most clones and boards from before 2018."),
            new Choice("arduino-mega", "Arduino Mega 2560", "54 digital pins, 16 analog inputs.")
        };

        internal static readonly Choice[] Templates =
        {
            new Choice("blink", "Blink", "Blinks the built-in LED and prints what it does."),
            new Choice("serial", "Serial plot", "Prints light:<reading>,counter:<n>: open the Serial Monitor's Plotter to see it drawn."),
            new Choice("bridge", "CodeBridge firmware (customizable)", "The firmware CodeBridge flows talk to, as a project you can change and upload."),
            new Choice("empty", "Empty", "An empty setup() and loop().")
        };

        private readonly ComboBox _board = new ComboBox { MinWidth = 260, ItemsSource = Boards, SelectedIndex = 0 };
        private readonly ComboBox _template = new ComboBox { MinWidth = 260, ItemsSource = Templates, SelectedIndex = 0 };
        private readonly TextBox _name = new TextBox { MinWidth = 260, Text = "MyFirmware", Padding = new Thickness(4, 3, 4, 3) };
        private readonly TextBox _folder = new TextBox { MinWidth = 220, Padding = new Thickness(4, 3, 4, 3) };
        private readonly TextBlock _hint = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
        private readonly TextBlock _error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = System.Windows.Media.Brushes.IndianRed };

        public NewFirmwareProjectWindow()
        {
            Title = "New CodeBridge firmware project";
            Width = 480;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
            FontSize = 12;

            NativeUi.ApplyEditorTheme(this);
            SetResourceReference(BackgroundProperty, "VsToolWindowBackground");
            SetResourceReference(ForegroundProperty, "VsToolWindowText");
            _hint.SetResourceReference(TextBlock.ForegroundProperty, "VsGrayText");

            _folder.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CodeBridge Firmware");
            Content = Build();
            _template.SelectionChanged += (_, __) => UpdateHint();
            _board.SelectionChanged += (_, __) => UpdateHint();
            UpdateHint();
        }

        public string BoardId => ((Choice)_board.SelectedItem).Id;
        public string BoardName => ((Choice)_board.SelectedItem).Name;
        public string Template => ((Choice)_template.SelectedItem).Id;
        public string TemplateName => ((Choice)_template.SelectedItem).Name;

        /// <summary>The project folder: the chosen parent folder plus the project name.</summary>
        public string Directory => Path.Combine(_folder.Text.Trim(), _name.Text.Trim());

        private FrameworkElement Build()
        {
            var root = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };
            root.Children.Add(Label("New firmware project", 15, FontWeights.SemiBold, "VsToolWindowText", new Thickness(0, 0, 0, 4)));
            root.Children.Add(Label("Write code for the board in C++ with PlatformIO. Build and upload from the Tools menu; errors appear in the Error List.", 12, FontWeights.Normal, "VsGrayText", new Thickness(0, 0, 0, 12)));

            root.Children.Add(Field("Board", _board));
            root.Children.Add(Field("Starting point", _template));
            root.Children.Add(_hint);
            root.Children.Add(Field("Project name", _name));

            var browse = new Button { Content = "Browse...", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
            browse.Click += (_, __) =>
            {
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = _folder.Text, Description = "Choose the folder that will contain the project" })
                {
                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                        _folder.Text = dialog.SelectedPath;
                }
            };
            var folderRow = new DockPanel();
            DockPanel.SetDock(browse, Dock.Right);
            folderRow.Children.Add(browse);
            folderRow.Children.Add(_folder);
            root.Children.Add(Field("Create it in", folderRow));
            root.Children.Add(_error);

            var create = new Button { Content = "Create", IsDefault = true, Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 8, 0) };
            create.Click += (_, __) => Accept();
            var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 5, 16, 5) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            buttons.Children.Add(create);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            return root;
        }

        private void Accept()
        {
            var name = _name.Text.Trim();
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                _error.Text = "Give the project a name without special characters (no \\ / : * ? \" < > |).";
                return;
            }

            if (_folder.Text.Trim().Length == 0)
            {
                _error.Text = "Choose the folder where the project will be created.";
                return;
            }

            if (System.IO.Directory.Exists(Directory) && System.IO.Directory.EnumerateFileSystemEntries(Directory).Any())
            {
                _error.Text = "That folder already has files in it. Choose another name.";
                return;
            }

            DialogResult = true;
        }

        private void UpdateHint() => _hint.Text = ((Choice)_template.SelectedItem).Description;

        private static FrameworkElement Field(string label, UIElement input)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            panel.Children.Add(Label(label, 12, FontWeights.Normal, "VsToolWindowText", new Thickness(0, 0, 0, 3)));
            panel.Children.Add(input);
            return panel;
        }

        private static TextBlock Label(string text, double size, FontWeight weight, string brushKey, Thickness margin)
        {
            var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, Margin = margin };
            block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return block;
        }
    }
}
