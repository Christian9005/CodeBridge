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
    /// <summary>What the user typed in the Home Assistant window; also the line the FlowHost reads from standard input.</summary>
    internal sealed class HomeAssistantSettings
    {
        public string Broker { get; set; } = string.Empty;

        public int Port { get; set; } = 1883;

        public string? User { get; set; }

        public string? Password { get; set; }

        public string? DeviceName { get; set; }

        /// <summary>The JSON line for <c>ha-test</c> and <c>ha-bridge</c> (it carries the password, so it never goes on a command line).</summary>
        public string ToHostJson()
        {
            var map = new Dictionary<string, object?>
            {
                ["broker"] = Broker.Trim(),
                ["port"] = Port,
                ["username"] = string.IsNullOrWhiteSpace(User) ? null : User!.Trim(),
                ["password"] = Password
            };
            if (!string.IsNullOrWhiteSpace(DeviceName))
                map["deviceName"] = DeviceName!.Trim();

            return new JavaScriptSerializer().Serialize(map);
        }

        /// <summary>Key under which the password is kept in <see cref="BoardTokens"/>.</summary>
        public static string PasswordKey(string broker, string? user) => "mqtt:" + (user ?? string.Empty).Trim() + "@" + broker.Trim();

        public static HomeAssistantSettings Load(EditorSettings settings)
        {
            var result = new HomeAssistantSettings
            {
                Broker = settings.HaBroker ?? string.Empty,
                Port = settings.HaPort,
                User = settings.HaUser,
                DeviceName = settings.HaDeviceName
            };
            if (result.Broker.Length > 0)
                result.Password = BoardTokens.Get(PasswordKey(result.Broker, result.User));

            return result;
        }

        public void Save(EditorSettings settings)
        {
            settings.HaBroker = Broker.Trim();
            settings.HaPort = Port;
            settings.HaUser = string.IsNullOrWhiteSpace(User) ? null : User!.Trim();
            settings.HaDeviceName = string.IsNullOrWhiteSpace(DeviceName) ? null : DeviceName!.Trim();
            settings.Save();
            if (!string.IsNullOrEmpty(Password))
                BoardTokens.Set(PasswordKey(Broker, User), Password!);
        }
    }

    /// <summary>
    /// Connects the flow to Home Assistant: finds it on the network, checks the MQTT broker and then publishes the flow's inputs
    /// and outputs there (a switch per digital output, a dimmable light per PWM output, a sensor per analog input...).
    /// The password is kept encrypted for this Windows user.
    /// </summary>
    internal sealed class HomeAssistantWindow : Window
    {
        private readonly ComboBox _broker = new ComboBox { IsEditable = true, MinWidth = 250, Margin = new Thickness(0, 0, 8, 0) };
        private readonly TextBox _port = new TextBox { Width = 70, Padding = new Thickness(3) };
        private readonly TextBox _user = new TextBox { Width = 160, Padding = new Thickness(3), Margin = new Thickness(0, 0, 8, 0) };
        private readonly PasswordBox _password = new PasswordBox { Width = 160, Padding = new Thickness(3) };
        private readonly TextBox _deviceName = new TextBox { Width = 328, Padding = new Thickness(3) };
        private readonly Button _find = new Button { Content = "Find", Padding = new Thickness(12, 3, 12, 3) };
        private readonly Button _test = new Button { Content = "Test connection", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
        private readonly Button _publish = new Button { Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        private readonly Button _guide = new Button { Content = "Copy broker setup", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        private readonly bool _publishing;
        private bool _busy;

        /// <summary>Raised with the settings once the user chose to publish.</summary>
        public event Action<HomeAssistantSettings>? PublishRequested;

        /// <summary>Raised when the user chose to stop publishing.</summary>
        public event Action? StopRequested;

        public HomeAssistantWindow(bool publishing, string flowSummary)
        {
            _publishing = publishing;
            Title = "CodeBridge Home Assistant";
            Width = 600;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12;

            NativeUi.ApplyEditorTheme(this);
            SetResourceReference(BackgroundProperty, "VsToolWindowBackground");
            SetResourceReference(ForegroundProperty, "VsToolWindowText");

            var saved = HomeAssistantSettings.Load(EditorSettings.Load());
            _broker.Text = saved.Broker;
            _port.Text = saved.Port.ToString();
            _user.Text = saved.User ?? string.Empty;
            _password.Password = saved.Password ?? string.Empty;
            _deviceName.Text = saved.DeviceName ?? string.Empty;
            _publish.Content = publishing ? "Stop publishing" : "Publish to Home Assistant";

            Content = BuildContent(flowSummary);
            _find.Click += (_, __) => Guard(FindAsync);
            _test.Click += (_, __) => Guard(TestAsync);
            _publish.Click += (_, __) => Publish();
            _guide.Click += (_, __) => CopyGuide();
            if (_broker.Text.Length == 0)
                Loaded += (_, __) => Guard(FindAsync);

            if (publishing)
                _status.Text = "This flow is being published. Stop it to change the settings; changes to the flow need a new publish.";
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

        private FrameworkElement BuildContent(string flowSummary)
        {
            var root = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };

            root.Children.Add(Label("Home Assistant", 15, FontWeights.SemiBold, "VsToolWindowText", new Thickness(0, 0, 0, 4)));
            root.Children.Add(Label(
                "Shows the inputs and outputs of this flow in Home Assistant through MQTT: it appears as a device, with no YAML. " +
                "Home Assistant needs an MQTT broker (Mosquitto) and the MQTT integration. " + flowSummary,
                12, FontWeights.Normal, "VsGrayText", new Thickness(0, 0, 0, 14)));

            root.Children.Add(Label("MQTT broker (usually the address of Home Assistant) and port", 12, FontWeights.Normal, "VsToolWindowText", new Thickness(0, 0, 0, 3)));
            var brokerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            brokerRow.Children.Add(_broker);
            brokerRow.Children.Add(Wrap(_port, new Thickness(0, 0, 8, 0)));
            brokerRow.Children.Add(_find);
            root.Children.Add(brokerRow);

            root.Children.Add(Label("User and password of the broker (create a dedicated user)", 12, FontWeights.Normal, "VsToolWindowText", new Thickness(0, 0, 0, 3)));
            var userRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            userRow.Children.Add(_user);
            userRow.Children.Add(_password);
            root.Children.Add(userRow);

            root.Children.Add(Label("Device name in Home Assistant (optional)", 12, FontWeights.Normal, "VsToolWindowText", new Thickness(0, 0, 0, 3)));
            root.Children.Add(Wrap(_deviceName, new Thickness(0, 0, 0, 14)));

            var buttons = new WrapPanel();
            buttons.Children.Add(_test);
            buttons.Children.Add(_publish);
            buttons.Children.Add(_guide);
            var close = new Button { Content = "Close", Padding = new Thickness(14, 5, 14, 5), IsCancel = true };
            close.Click += (_, __) => Close();
            buttons.Children.Add(close);
            root.Children.Add(buttons);

            _status.SetResourceReference(TextBlock.ForegroundProperty, "VsToolWindowText");
            root.Children.Add(_status);
            return root;
        }

        private static FrameworkElement Wrap(FrameworkElement element, Thickness margin)
        {
            var host = new StackPanel { Margin = margin, HorizontalAlignment = HorizontalAlignment.Left };
            host.Children.Add(element);
            return host;
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
            var editable = !busy && !_publishing;
            _publish.IsEnabled = !busy;
            _broker.IsEnabled = _port.IsEnabled = _user.IsEnabled = _password.IsEnabled = _deviceName.IsEnabled = editable;
            _find.IsEnabled = _test.IsEnabled = editable;
            if (message != null)
                _status.Text = message;
        }

        private HomeAssistantSettings Read()
        {
            int.TryParse(_port.Text.Trim(), out var port);
            return new HomeAssistantSettings
            {
                Broker = (_broker.Text ?? string.Empty).Trim(),
                Port = port > 0 && port <= 65535 ? port : 1883,
                User = _user.Text,
                Password = _password.Password,
                DeviceName = _deviceName.Text
            };
        }

        private async Task FindAsync()
        {
            if (_busy)
                return;

            SetBusy(true, "Looking for Home Assistant on the network...");
            var messages = await HostClient.QueryAsync("ha-find");
            var found = messages.FirstOrDefault(m => m.Type == "ha-instances");
            if (found == null)
            {
                SetBusy(false, messages.FirstOrDefault(m => m.Type == "error")?.Str("message") ?? "The search failed. Type the address by hand.");
                return;
            }

            var instances = found.List("instances").ToList();
            var addresses = instances.Select(i => i.Str("broker")).Where(a => !string.IsNullOrEmpty(a)).Cast<string>().ToList();
            _broker.ItemsSource = addresses;
            if (addresses.Count == 0)
            {
                SetBusy(false, "Home Assistant was not found on this network. Type the address of the MQTT broker by hand.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_broker.Text))
                _broker.Text = addresses[0];

            var first = instances[0];
            SetBusy(false, $"Found '{first.Str("name")}' at {first.Str("broker")}" +
                (string.IsNullOrEmpty(first.Str("version")) ? "." : $" (Home Assistant {first.Str("version")})."));
        }

        private async Task TestAsync()
        {
            if (_busy)
                return;

            var settings = Read();
            if (settings.Broker.Length == 0)
            {
                _status.Text = "Enter the address of the MQTT broker first, or press Find.";
                return;
            }

            _guide.Visibility = Visibility.Collapsed;
            SetBusy(true, $"Connecting to {settings.Broker}:{settings.Port}...");
            var messages = await HostClient.QueryAsync("ha-test", default, null, settings.ToHostJson());
            var result = messages.FirstOrDefault(m => m.Type == "ha-test");
            if (result == null)
            {
                SetBusy(false, messages.FirstOrDefault(m => m.Type == "error")?.Str("message") ?? "The test did not finish.");
                return;
            }

            if (result.Bool("ok"))
            {
                SetBusy(false, "Connected. The broker accepted the user and password; press Publish.");
                return;
            }

            if (result.Str("reason") == "refused")
                _guide.Visibility = Visibility.Visible;

            SetBusy(false, result.Str("message") ?? "The broker could not be reached.");
        }

        private void Publish()
        {
            if (_busy)
                return;

            if (_publishing)
            {
                StopRequested?.Invoke();
                Close();
                return;
            }

            var settings = Read();
            if (settings.Broker.Length == 0)
            {
                _status.Text = "Enter the address of the MQTT broker first, or press Find.";
                return;
            }

            try
            {
                settings.Save(EditorSettings.Load());
            }
            catch (Exception ex)
            {
                _status.Text = "The settings could not be saved for this Windows account: " + ex.Message;
                return;
            }

            PublishRequested?.Invoke(settings);
            Close();
        }

        private void CopyGuide()
        {
            try
            {
                Clipboard.SetText(BrokerGuide);
                _status.Text = "Copied. It holds a compose file for Mosquitto and the commands to create the user; the steps end with adding the MQTT integration in Home Assistant.";
            }
            catch (Exception ex)
            {
                _status.Text = "Could not use the clipboard: " + ex.Message;
            }
        }

        internal const string BrokerGuide =
            "CodeBridge: MQTT broker for Home Assistant (Docker / CasaOS)\r\n" +
            "\r\n" +
            "1. Run a Mosquitto container (compose file):\r\n" +
            "\r\n" +
            "services:\r\n" +
            "  mosquitto:\r\n" +
            "    image: eclipse-mosquitto:2\r\n" +
            "    container_name: mosquitto\r\n" +
            "    restart: unless-stopped\r\n" +
            "    ports:\r\n" +
            "      - \"1883:1883\"\r\n" +
            "    volumes:\r\n" +
            "      - /path/to/mosquitto/config:/mosquitto/config\r\n" +
            "      - /path/to/mosquitto/data:/mosquitto/data\r\n" +
            "\r\n" +
            "2. In /path/to/mosquitto/config create mosquitto.conf:\r\n" +
            "\r\n" +
            "persistence true\r\n" +
            "persistence_location /mosquitto/data/\r\n" +
            "listener 1883\r\n" +
            "allow_anonymous false\r\n" +
            "password_file /mosquitto/config/passwd\r\n" +
            "\r\n" +
            "3. Create the user (choose your own password) and give the file to the container user:\r\n" +
            "\r\n" +
            "docker run --rm -v /path/to/mosquitto/config:/mosquitto/config eclipse-mosquitto:2 mosquitto_passwd -c -b /mosquitto/config/passwd codebridge YOUR_PASSWORD\r\n" +
            "sudo chown 1883:1883 /path/to/mosquitto/config/passwd && sudo chmod 600 /path/to/mosquitto/config/passwd\r\n" +
            "docker restart mosquitto\r\n" +
            "\r\n" +
            "4. In Home Assistant: Settings > Devices & services > Add integration > MQTT. Broker 127.0.0.1 (or the machine's address), port 1883, the user above.\r\n" +
            "\r\n" +
            "With Home Assistant OS, install the 'Mosquitto broker' add-on instead and create the user under Settings > People.\r\n";
    }
}
