#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodeBridge.VisualStudio.Editor
{
    /// <summary>A board found over ESP-NOW, as the Mesh window lists it.</summary>
    internal sealed class MeshNodeItem
    {
        public string Mac { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Board { get; set; } = string.Empty;
        public string Firmware { get; set; } = string.Empty;
        public int AgeSeconds { get; set; }
        public string Address { get; set; } = string.Empty;
        public string? SuggestedBoard { get; set; }

        public string Title => string.IsNullOrWhiteSpace(Name) ? Mac : Name;

        public string Detail => $"{Board} · firmware {Firmware} · {Mac} · heard {(AgeSeconds <= 1 ? "just now" : AgeSeconds + " s ago")}";

        public override string ToString() => Title;
    }

    /// <summary>
    /// Reach more than one board: pick a gateway (a board the PC already talks to over USB or Wi-Fi, which keeps its Wi-Fi), set the other boards up
    /// once over USB with a shared key, and then use any board in range as if it were plugged in. The key is stored encrypted for this Windows user.
    /// </summary>
    internal sealed class MeshWindow : Window
    {
        private const string KeyStore = "mesh-key";

        private readonly string _gateway;
        private readonly string? _gatewayToken;
        private readonly TextBlock _gatewayState = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly TextBox _name = new TextBox { Width = 190, Padding = new Thickness(3) };
        private readonly ComboBox _channel = new ComboBox { Width = 190 };
        private readonly Button _provision = new Button { Content = "Set up this board", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
        private readonly Button _scan = new Button { Content = "Scan", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        private readonly Button _use = new Button { Content = "Use this board", Padding = new Thickness(14, 5, 14, 5), IsEnabled = false };
        private readonly ListBox _nodes = new ListBox { MinHeight = 120, MaxHeight = 220, Margin = new Thickness(0, 0, 0, 10) };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        private bool _busy;

        /// <summary>Raised with the board's address (<c>mesh://COM3/E072A1F97B84</c>) and the board profile its chip suggests.</summary>
        public event Action<string, string?>? NodeChosen;

        public MeshWindow(string gateway, string? gatewayToken)
        {
            _gateway = gateway;
            _gatewayToken = gatewayToken;
            Title = "CodeBridge Mesh";
            Width = 620;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12;

            NativeUi.ApplyEditorTheme(this);
            SetResourceReference(BackgroundProperty, "VsToolWindowBackground");
            SetResourceReference(ForegroundProperty, "VsToolWindowText");

            _channel.ItemsSource = new[] { "Search automatically", "Channel 1", "Channel 6", "Channel 11" };
            _channel.SelectedIndex = 0;
            _nodes.ItemTemplate = BuildNodeTemplate();
            _nodes.SetResourceReference(BackgroundProperty, "VsInputBackground");
            _nodes.SetResourceReference(ForegroundProperty, "VsToolWindowText");
            _nodes.SelectionChanged += (_, __) => _use.IsEnabled = _nodes.SelectedItem is MeshNodeItem;
            _nodes.MouseDoubleClick += (_, __) => Choose();

            Content = BuildContent();
            _provision.Click += (_, __) => Guard(ProvisionAsync);
            _scan.Click += (_, __) => Guard(ScanAsync);
            _use.Click += (_, __) => Choose();
            Loaded += (_, __) => Guard(RefreshGatewayAsync);
        }

        private async void Guard(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                SetBusy(false, "Unexpected error: " + ex.Message);
            }
        }

        private static DataTemplate BuildNodeTemplate()
        {
            var template = new DataTemplate();
            var panel = new FrameworkElementFactory(typeof(StackPanel));
            panel.SetValue(StackPanel.MarginProperty, new Thickness(4, 3, 4, 3));
            var title = new FrameworkElementFactory(typeof(TextBlock));
            title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(MeshNodeItem.Title)));
            title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            var detail = new FrameworkElementFactory(typeof(TextBlock));
            detail.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(MeshNodeItem.Detail)));
            detail.SetValue(TextBlock.FontSizeProperty, 11.0);
            detail.SetResourceReference(TextBlock.ForegroundProperty, "VsGrayText");
            panel.AppendChild(title);
            panel.AppendChild(detail);
            template.VisualTree = panel;
            return template;
        }

        private FrameworkElement BuildContent()
        {
            var root = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };

            root.Children.Add(Label("Mesh (ESP-NOW)", 15, FontWeights.SemiBold, "VsToolWindowText", new Thickness(0, 0, 0, 4)));
            root.Children.Add(Label(
                "Reach boards that have no cable or Wi-Fi of their own: they talk to a gateway board over ESP-NOW, and the gateway keeps its Wi-Fi connection. " +
                "Each board is set up once over USB with the same key; after that it shows up here.",
                12, FontWeights.Normal, "VsGrayText", new Thickness(0, 0, 0, 12)));

            root.Children.Add(Label($"Gateway: {_gateway}", 12, FontWeights.SemiBold, "VsToolWindowText", new Thickness(0, 0, 0, 3)));
            _gatewayState.SetResourceReference(TextBlock.ForegroundProperty, "VsGrayText");
            _gatewayState.Margin = new Thickness(0, 0, 0, 10);
            root.Children.Add(_gatewayState);

            root.Children.Add(Label("Set up the board on the selected USB port (name it and give it the mesh key)", 12, FontWeights.Normal, "VsToolWindowText", new Thickness(0, 0, 0, 3)));
            var setup = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            var nameBox = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            nameBox.Children.Add(Label("Name (optional)", 11, FontWeights.Normal, "VsGrayText", new Thickness(0, 0, 0, 2)));
            nameBox.Children.Add(_name);
            setup.Children.Add(nameBox);
            var channelBox = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            channelBox.Children.Add(Label("Radio channel (boards without Wi-Fi)", 11, FontWeights.Normal, "VsGrayText", new Thickness(0, 0, 0, 2)));
            channelBox.Children.Add(_channel);
            setup.Children.Add(channelBox);
            var provisionBox = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            provisionBox.Children.Add(_provision);
            setup.Children.Add(provisionBox);
            root.Children.Add(setup);

            root.Children.Add(Label("Boards in range", 12, FontWeights.SemiBold, "VsToolWindowText", new Thickness(0, 0, 0, 3)));
            root.Children.Add(_nodes);

            var buttons = new WrapPanel();
            buttons.Children.Add(_scan);
            buttons.Children.Add(_use);
            var close = new Button { Content = "Close", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            close.Click += (_, __) => Close();
            buttons.Children.Add(close);
            root.Children.Add(buttons);

            _status.SetResourceReference(TextBlock.ForegroundProperty, "VsToolWindowText");
            root.Children.Add(_status);
            return root;
        }

        private static TextBlock Label(string text, double size, FontWeight weight, string brushKey, Thickness margin)
        {
            var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, Margin = margin };
            block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return block;
        }

        private void SetBusy(bool busy, string? message = null)
        {
            _busy = busy;
            _scan.IsEnabled = _provision.IsEnabled = !busy;
            _use.IsEnabled = !busy && _nodes.SelectedItem is MeshNodeItem;
            if (message != null)
                _status.Text = message;
        }

        private string GatewayArguments(string command) => $"{command} --port {HostClient.Quote(_gateway)}";

        private async Task RefreshGatewayAsync()
        {
            if (_busy)
                return;

            SetBusy(true, "Asking the gateway...");
            var messages = await HostClient.QueryAsync(GatewayArguments("mesh-status"), default, _gatewayToken);
            var status = messages.FirstOrDefault(m => m.Type == "mesh-status");
            if (status == null)
            {
                var unsupported = messages.FirstOrDefault(m => m.Type == "mesh-unsupported")?.Str("message");
                _gatewayState.Text = unsupported ?? messages.FirstOrDefault(m => m.Type == "error")?.Str("message") ?? "The gateway did not answer.";
                SetBusy(false, "Select the gateway's COM port (or its IP address) in the toolbar first.");
                return;
            }

            var enabled = status.Bool("enabled");
            var hasKey = status.Bool("hasKey");
            _gatewayState.Text = !hasKey
                ? "This board has no mesh key yet. Connect it with its USB cable and press Set up this board."
                : !enabled
                    ? "The mesh is off on this board. Press Set up this board (over USB) to switch it on."
                    : $"Mesh is on · channel {status.Str("channel")} · {(status.Bool("locked") ? "ready" : "searching for a channel")} · name '{status.Str("name")}'.";

            SetBusy(false, enabled && hasKey ? "Press Scan to look for boards in range." : null);
        }

        private async Task ScanAsync()
        {
            if (_busy)
                return;

            SetBusy(true, "Scanning for boards... (about one second)");
            var messages = await HostClient.QueryAsync(GatewayArguments("mesh-scan"), default, _gatewayToken);
            var found = messages.FirstOrDefault(m => m.Type == "mesh-nodes");
            if (found == null)
            {
                SetBusy(false, messages.FirstOrDefault(m => m.Type == "error")?.Str("message") ?? "The scan did not finish.");
                return;
            }

            var items = found.List("nodes").Select(n => new MeshNodeItem
            {
                Mac = n.Str("mac") ?? string.Empty,
                Name = n.Str("name") ?? string.Empty,
                Board = n.Str("board") ?? string.Empty,
                Firmware = n.Str("firmware") ?? string.Empty,
                AgeSeconds = int.TryParse(n.Str("ageSeconds"), out var age) ? age : 0,
                Address = n.Str("address") ?? string.Empty,
                SuggestedBoard = n.Str("suggestedBoard")
            }).ToList();

            _nodes.ItemsSource = items;
            SetBusy(false, items.Count == 0
                ? "No boards answered. Check that they are powered, set up with the same key and in range. A board without an antenna (the XIAO ESP32-S3 needs its external one) cannot hear the gateway."
                : $"{items.Count} board(s) found. Select one and press Use this board.");
        }

        private async Task ProvisionAsync()
        {
            if (_busy)
                return;

            var port = _gateway;
            // The provisioning always happens over USB on the port selected in the toolbar, which can be a different board than a Wi-Fi gateway.
            if (!BoardTokens.IsSerialPort(port) && !string.Equals(port, "simulator", StringComparison.OrdinalIgnoreCase))
            {
                SetBusy(false, "Setting a board up needs its USB cable: select its COM port in the toolbar, then open this window again.");
                return;
            }

            var name = (_name.Text ?? string.Empty).Trim();
            var channel = _channel.SelectedIndex switch { 1 => 1, 2 => 6, 3 => 11, _ => 0 };
            var existing = BoardTokens.Get(KeyStore);
            var request = new JavaScriptSerializer().Serialize(new Dictionary<string, object?>
            {
                ["key"] = existing,
                ["name"] = name.Length == 0 ? null : name,
                ["channel"] = channel
            });

            SetBusy(true, "Setting the board up... (the board restarts once)");
            var messages = await HostClient.QueryAsync(GatewayArguments("mesh-provision"), default, null, request);
            var done = messages.FirstOrDefault(m => m.Type == "mesh-provisioned");
            if (done == null)
            {
                SetBusy(false, messages.FirstOrDefault(m => m.Type == "error")?.Str("message") ?? "The board was not set up.");
                return;
            }

            var key = done.Str("key");
            if (!string.IsNullOrEmpty(key))
            {
                try
                {
                    BoardTokens.Set(KeyStore, key!);
                }
                catch (Exception ex)
                {
                    SetBusy(false, "The board is set up, but the key could not be saved for this Windows account: " + ex.Message);
                    return;
                }
            }

            SetBusy(false, $"Done: '{done.Str("name")}' is in the mesh" + (string.IsNullOrEmpty(key) ? "." : " and a new key was created and saved. Set the other boards up the same way: they will get the same key."));
            await RefreshGatewayAsync();
        }

        private void Choose()
        {
            if (_nodes.SelectedItem is not MeshNodeItem node)
                return;

            NodeChosen?.Invoke(node.Address, node.SuggestedBoard);
            Close();
        }
    }
}
