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
    /// <summary>
    /// Wi-Fi setup for the ESP32: pick a network, enter the password, and CodeBridge stores both on the board over USB
    /// and pairs it with a private token (saved encrypted for this Windows user). Afterwards the board can be reached
    /// by IP from the Port box — no cable, and nobody else on the network can control it.
    /// </summary>
    internal sealed class WifiSetupWindow : Window
    {
        private readonly string _usbPort;
        private readonly ComboBox _network = new ComboBox { IsEditable = true, MinWidth = 220, Margin = new Thickness(0, 0, 8, 0) };
        private readonly PasswordBox _password = new PasswordBox { MinWidth = 220, Margin = new Thickness(0, 0, 8, 0) };
        private readonly Button _scan = new Button { Content = "Scan", Padding = new Thickness(12, 3, 12, 3) };
        private readonly Button _pair = new Button { Content = "Pair and connect", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
        private readonly Button _forget = new Button { Content = "Disable network access", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        private bool _busy;

        /// <summary>Raised with the board's IP address once pairing succeeded.</summary>
        public event Action<string>? Paired;

        public WifiSetupWindow(string usbPort)
        {
            _usbPort = usbPort;
            Title = "CodeBridge Wi-Fi setup";
            Width = 560;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12;

            NativeUi.ApplyEditorTheme(this);
            SetResourceReference(BackgroundProperty, "VsToolWindowBackground");
            SetResourceReference(ForegroundProperty, "VsToolWindowText");

            Content = BuildContent();
            _scan.Click += (_, __) => Guard(ScanAsync);
            _pair.Click += (_, __) => Guard(PairAsync);
            _forget.Click += (_, __) => Guard(ForgetAsync);
            Loaded += (_, __) => Guard(ScanAsync);
        }

        // An exception escaping an async event handler would take Visual Studio down with it: report it in the window instead.
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

        private FrameworkElement BuildContent()
        {
            var root = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };

            root.Children.Add(Label("Wi-Fi setup for the ESP32", 15, FontWeights.SemiBold, "VsToolWindowText", new Thickness(0, 0, 0, 4)));
            root.Children.Add(Label(
                $"Uses the USB cable on {_usbPort} once. CodeBridge saves the network on the board and pairs it with a private token, " +
                "so only this PC can control it over Wi-Fi. The ESP32 supports 2.4 GHz networks only.",
                12, FontWeights.Normal, "VsGrayText", new Thickness(0, 0, 0, 14)));

            root.Children.Add(Label("Network", 12, FontWeights.Normal, "VsToolWindowText", new Thickness(0, 0, 0, 3)));
            var networkRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            networkRow.Children.Add(_network);
            networkRow.Children.Add(_scan);
            root.Children.Add(networkRow);

            root.Children.Add(Label("Password (leave empty for an open network)", 12, FontWeights.Normal, "VsToolWindowText", new Thickness(0, 0, 0, 3)));
            var passwordRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            passwordRow.Children.Add(_password);
            root.Children.Add(passwordRow);

            var buttons = new WrapPanel();
            buttons.Children.Add(_pair);
            buttons.Children.Add(_forget);
            var close = new Button { Content = "Close", Padding = new Thickness(14, 5, 14, 5), IsCancel = true };
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
            _scan.IsEnabled = _pair.IsEnabled = _forget.IsEnabled = !busy;
            _network.IsEnabled = _password.IsEnabled = !busy;
            if (message != null)
                _status.Text = message;
        }

        private async Task ScanAsync()
        {
            if (_busy)
                return;

            SetBusy(true, "Scanning for networks...");
            var messages = await HostClient.QueryAsync($"wifi-scan --port {HostClient.Quote(_usbPort)}");
            var error = messages.FirstOrDefault(m => m.Type == "error");
            var networks = messages.FirstOrDefault(m => m.Type == "wifi-networks");

            if (networks == null)
            {
                SetBusy(false, error?.Str("message") ?? "The board did not answer. Check the USB port and that the CodeBridge firmware is installed.");
                return;
            }

            var current = _network.Text;
            var items = networks.List("networks")
                .Select(n => n.Str("ssid"))
                .Where(ssid => !string.IsNullOrEmpty(ssid))
                .Cast<string>()
                .ToList();
            _network.ItemsSource = items;
            _network.Text = !string.IsNullOrWhiteSpace(current) ? current : items.FirstOrDefault() ?? string.Empty;
            SetBusy(false, items.Count == 0 ? "No networks found. Type the name by hand." : $"{items.Count} network(s) found.");
        }

        private async Task PairAsync()
        {
            if (_busy)
                return;

            var ssid = (_network.Text ?? string.Empty).Trim();
            if (ssid.Length == 0)
            {
                _status.Text = "Choose or type the network name first.";
                return;
            }

            var payload = new JavaScriptSerializer().Serialize(new Dictionary<string, string> { ["ssid"] = ssid, ["password"] = _password.Password });
            SetBusy(true, $"Pairing the board and joining '{ssid}'... this takes up to 30 seconds.");

            var messages = await HostClient.QueryAsync($"wifi-config --port {HostClient.Quote(_usbPort)}", default, null, payload);
            _password.Clear();

            var provisioned = messages.FirstOrDefault(m => m.Type == "wifi-provisioned");
            if (provisioned == null)
            {
                var error = messages.FirstOrDefault(m => m.Type == "error")?.Str("message");
                SetBusy(false, error ?? "The board could not join the network.");
                return;
            }

            var ip = provisioned.Str("ip") ?? string.Empty;
            var token = provisioned.Str("token");
            var deviceId = provisioned.Str("deviceId");
            if (ip.Length == 0 || string.IsNullOrEmpty(token))
            {
                SetBusy(false, "The board answered without an address. Try again.");
                return;
            }

            try
            {
                BoardTokens.Set(ip, token!);
                if (!string.IsNullOrEmpty(deviceId))
                    BoardTokens.Set(deviceId!, token!); // the board keeps this identity when the router gives it another IP address
            }
            catch (Exception ex)
            {
                SetBusy(false, "The board is paired, but the token could not be saved for this Windows account: " + ex.Message);
                return;
            }

            SetBusy(false, $"Done. The board is at {ip}. The Port box now shows it: press Connect to use it over Wi-Fi.");
            Paired?.Invoke(ip);
        }

        private async Task ForgetAsync()
        {
            if (_busy)
                return;

            var answer = MessageBox.Show(
                this,
                "The board will stop accepting Wi-Fi connections until it is paired again. USB keeps working.",
                "Disable network access",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK)
                return;

            SetBusy(true, "Removing the pairing...");
            var messages = await HostClient.QueryAsync($"wifi-unpair --port {HostClient.Quote(_usbPort)}");
            var error = messages.FirstOrDefault(m => m.Type == "error");
            SetBusy(false, error != null ? error.Str("message") : "Network access is disabled on the board.");
        }
    }
}
