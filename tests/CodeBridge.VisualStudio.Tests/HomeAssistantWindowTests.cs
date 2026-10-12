using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Controls;
using CodeBridge.VisualStudio.Editor;

namespace CodeBridge.VisualStudio.Tests;

public sealed class HomeAssistantWindowTests
{
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); } catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
            throw new InvalidOperationException(failure.ToString());
    }

    private static Dictionary<string, object> Parse(string json) =>
        (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);

    [Fact]
    public void The_host_receives_the_broker_settings_as_one_json_line()
    {
        var settings = new HomeAssistantSettings
        {
            Broker = " 192.168.1.10 ",
            Port = 1884,
            User = " codebridge ",
            Password = "p\"ass\\word",
            DeviceName = "Garage board"
        };

        var json = settings.ToHostJson();
        var map = Parse(json);

        Assert.DoesNotContain("\n", json);
        Assert.Equal("192.168.1.10", map["broker"]);
        Assert.Equal(1884, map["port"]);
        Assert.Equal("codebridge", map["username"]);
        Assert.Equal("p\"ass\\word", map["password"]);
        Assert.Equal("Garage board", map["deviceName"]);
    }

    [Fact]
    public void An_anonymous_broker_sends_no_user_and_no_device_name()
    {
        var map = Parse(new HomeAssistantSettings { Broker = "ha.local", User = "  ", DeviceName = "" }.ToHostJson());

        Assert.Null(map["username"]);
        Assert.False(map.ContainsKey("deviceName"));
    }

    [Fact]
    public void The_password_is_kept_per_broker_and_user()
    {
        Assert.NotEqual(HomeAssistantSettings.PasswordKey("ha.local", "a"), HomeAssistantSettings.PasswordKey("ha.local", "b"));
        Assert.NotEqual(HomeAssistantSettings.PasswordKey("ha.local", "a"), HomeAssistantSettings.PasswordKey("other.local", "a"));
        Assert.Equal(HomeAssistantSettings.PasswordKey("ha.local", "a"), HomeAssistantSettings.PasswordKey(" ha.local ", " a "));
    }

    [Fact]
    public void The_password_is_stored_encrypted_and_found_again()
    {
        var key = HomeAssistantSettings.PasswordKey("broker.codebridge-tests.invalid", "tester");
        const string password = "correct horse battery staple";
        try
        {
            BoardTokens.Set(key, password);

            Assert.Equal(password, BoardTokens.Get(key));
            var file = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeBridge", "board-tokens.json");
            Assert.DoesNotContain(password, System.IO.File.ReadAllText(file));
        }
        finally
        {
            BoardTokens.Remove(key);
        }
    }

    [Fact]
    public void The_window_offers_to_publish_or_to_stop()
    {
        RunSta(() =>
        {
            var idle = new HomeAssistantWindow(false, "This flow has 3 GPIO block(s) to share.");
            var publishing = new HomeAssistantWindow(true, "This flow has 3 GPIO block(s) to share.");

            Assert.Contains("Home Assistant", idle.Title);
            Assert.True(idle.Resources.Contains("VsToolWindowText"));
            Assert.Contains("Publish to Home Assistant", ButtonTexts(idle));
            Assert.Contains("Stop publishing", ButtonTexts(publishing));
        });
    }

    private static List<string> ButtonTexts(System.Windows.Window window)
    {
        var texts = new List<string>();
        void Walk(System.Windows.DependencyObject node)
        {
            if (node is Button { Content: string text })
                texts.Add(text);
            foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(node))
            {
                if (child is System.Windows.DependencyObject next)
                    Walk(next);
            }
        }

        Walk(window);
        return texts;
    }
}
