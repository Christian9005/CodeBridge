using System.Text.Json;
using CodeBridge.Flow;
using CodeBridge.Flow.Serialization;
using CodeBridge.IotBindings;
using Microsoft.AspNetCore.Http;

namespace CodeBridge.WebEditor.Tests;

public sealed class WebEditorTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cb-web-" + Guid.NewGuid().ToString("N"));

    public WebEditorTests()
    {
        Directory.CreateDirectory(_folder);
        IotBindingBlocks.Register();
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string FlowPath(string name = "flow.cbflow") => Path.Combine(_folder, name);

    /// <summary>Manual trigger -> Digital Write on GPIO 2.</summary>
    private static FlowDocument Blinker()
    {
        var document = new FlowDocument { Name = "Blinker", BoardId = "esp32-devkit" };
        document.Nodes.Add(new FlowNode { Id = "start", Type = BuiltInBlockCatalog.ManualTrigger });
        document.Nodes.Add(new FlowNode { Id = "led", Type = BuiltInBlockCatalog.GpioSetOutput, Parameters = { ["pin"] = 2, ["value"] = true } });
        document.Connections.Add(new FlowConnection { Id = "c", FromNodeId = "start", FromPort = "trigger", ToNodeId = "led", ToPort = "trigger" });
        return document;
    }

    private EditorSession Session(FlowDocument? document = null, string? board = null)
    {
        if (document is not null)
            File.WriteAllText(FlowPath(), FlowDocumentJson.Serialize(document));

        return new EditorSession(FlowPath(), board);
    }

    private static JsonElement Result(object update) => JsonDocument.Parse(EditorSession.Serialize(update)).RootElement;

    // ---------------------------------------------------------------- the session

    [Fact]
    public void A_file_that_does_not_exist_yet_is_an_empty_flow_named_after_it()
    {
        var session = new EditorSession(FlowPath("garden.cbflow"), null);

        Assert.Equal("garden", session.Document.Name);
        Assert.Empty(session.Document.Nodes);
    }

    [Fact]
    public void An_unknown_board_is_refused_at_start()
    {
        Assert.Throws<InvalidOperationException>(() => new EditorSession(FlowPath(), "esp99"));
    }

    [Fact]
    public void The_board_can_be_chosen_at_start()
    {
        Assert.Equal("esp32-s3-devkit", new EditorSession(FlowPath(), "esp32-s3-devkit").Document.BoardId);
    }

    [Fact]
    public void A_valid_flow_is_validated_and_written_only_when_saving()
    {
        var session = Session();
        var json = FlowDocumentJson.Serialize(Blinker());

        var validated = Result(session.Update(json, save: false));
        Assert.True(validated.GetProperty("valid").GetBoolean());
        Assert.False(validated.GetProperty("saved").GetBoolean());
        Assert.False(File.Exists(FlowPath()));

        var saved = Result(session.Update(json, save: true));
        Assert.True(saved.GetProperty("saved").GetBoolean());
        Assert.Equal(2, FlowDocumentJson.Deserialize(File.ReadAllText(FlowPath())).Nodes.Count);
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public void A_flow_with_errors_is_reported_and_never_overwrites_a_good_file()
    {
        var session = Session(Blinker());
        var before = File.ReadAllText(FlowPath());
        var broken = Blinker();
        broken.Nodes.Single(n => n.Id == "led").Parameters["pin"] = 99;   // no such pin on the board

        var result = Result(session.Update(FlowDocumentJson.Serialize(broken), save: true));

        Assert.False(result.GetProperty("valid").GetBoolean());
        Assert.False(result.GetProperty("saved").GetBoolean());
        Assert.NotEmpty(result.GetProperty("issues").EnumerateArray());
        Assert.Equal(before, File.ReadAllText(FlowPath()));
    }

    [Fact]
    public void Text_that_is_not_a_flow_is_an_answer_not_a_crash()
    {
        var result = Result(Session().Update("{ this is not json", save: true));

        Assert.False(result.GetProperty("valid").GetBoolean());
        Assert.Contains("could not be read", result.GetProperty("issues")[0].GetProperty("message").GetString());
    }

    [Fact]
    public void The_catalog_offers_the_built_in_blocks_and_the_ones_of_the_bindings_package()
    {
        var catalog = Result(Session().Catalog());
        var types = catalog.GetProperty("blocks").EnumerateArray().Select(b => b.GetProperty("type").GetString()).ToList();

        Assert.Contains(BuiltInBlockCatalog.GpioSetOutput, types);
        Assert.Contains(IotBindingBlocks.Sht3x, types);
        Assert.Contains(ModuleBlocks.Dht, types);
    }

    [Fact]
    public void Properties_travel_with_their_options_so_the_page_can_draw_a_choice()
    {
        var catalog = Result(Session().Catalog());
        var sht = catalog.GetProperty("blocks").EnumerateArray().Single(b => b.GetProperty("type").GetString() == IotBindingBlocks.Sht3x);
        var address = sht.GetProperty("properties").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "address");

        Assert.Equal(2, address.GetProperty("options").GetArrayLength());
        Assert.Equal("integer", address.GetProperty("valueKind").GetString());
    }

    // ---------------------------------------------------------------- who may talk to it

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public void This_computer_with_the_key_gets_in(string host)
    {
        Assert.Null(RequestGuard.Reject(host, "/api/flow", "secret", "secret"));
    }

    [Fact]
    public void The_page_itself_needs_no_key_but_the_api_does()
    {
        Assert.Null(RequestGuard.Reject("127.0.0.1", "/", null, "secret"));
        Assert.Null(RequestGuard.Reject("127.0.0.1", "/index.html", null, "secret"));
        Assert.Equal(StatusCodes.Status401Unauthorized, RequestGuard.Reject("127.0.0.1", "/api/flow", null, "secret"));
        Assert.Equal(StatusCodes.Status401Unauthorized, RequestGuard.Reject("127.0.0.1", "/api/run", "guess", "secret"));
        Assert.Equal(StatusCodes.Status401Unauthorized, RequestGuard.Reject("127.0.0.1", "/api/run", "", "secret"));
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("192.168.1.20")]
    [InlineData("127.0.0.1.evil.example")]
    public void A_name_that_only_points_at_this_computer_is_refused_even_with_the_key(string host)
    {
        Assert.Equal(StatusCodes.Status421MisdirectedRequest, RequestGuard.Reject(host, "/api/flow", "secret", "secret"));
        Assert.Equal(StatusCodes.Status421MisdirectedRequest, RequestGuard.Reject(host, "/", null, "secret"));
    }

    // ---------------------------------------------------------------- running

    private static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            if (condition())
                return true;

            await Task.Delay(10);
        }

        return condition();
    }

    [Fact]
    public async Task A_flow_runs_on_the_virtual_board_and_the_page_hears_about_every_block()
    {
        var hub = new RunHub(Session(Blinker()), "simulator");
        var (id, reader) = hub.Subscribe();
        var lines = new List<string>();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pump = Task.Run(async () =>
        {
            await foreach (var line in reader.ReadAllAsync(cancel.Token))
                lock (lines) lines.Add(line);
        });

        Assert.True(hub.Start(out _));
        Assert.True(await UntilAsync(() => { lock (lines) return lines.Any(l => l.Contains("\"nodeId\":\"led\"") && l.Contains("nodecompleted")); }));
        await hub.StopAsync();
        Assert.True(await UntilAsync(() => !hub.Running));
        hub.Unsubscribe(id);
        await pump;

        string[] seen;
        lock (lines) seen = lines.ToArray();
        Assert.Contains(seen, l => l.Contains("\"type\":\"state\"") && l.Contains("\"running\":true"));
        Assert.Contains(seen, l => l.Contains("supervisor") && l.Contains("\"kind\":\"connected\""));
        Assert.Contains(seen, l => l.Contains("\"type\":\"state\"") && l.Contains("\"running\":false"));
    }

    [Fact]
    public async Task A_second_start_is_refused_while_it_runs()
    {
        var hub = new RunHub(Session(Blinker()), "simulator");

        Assert.True(hub.Start(out _));
        Assert.False(hub.Start(out var message));
        Assert.Contains("already running", message);
        await hub.StopAsync();
    }

    [Fact]
    public void A_flow_with_errors_is_not_started_and_says_why()
    {
        var broken = Blinker();
        broken.Nodes.Single(n => n.Id == "led").Parameters["pin"] = 99;
        var hub = new RunHub(Session(broken), "simulator");

        Assert.False(hub.Start(out var message));
        Assert.StartsWith("The flow has errors", message);
        Assert.False(hub.Running);
    }

    [Fact]
    public async Task A_board_that_is_not_there_keeps_being_retried_and_can_be_stopped()
    {
        var hub = new RunHub(Session(Blinker()), "COM-DOES-NOT-EXIST");
        var (id, reader) = hub.Subscribe();
        var lines = new List<string>();
        var pump = Task.Run(async () =>
        {
            await foreach (var line in reader.ReadAllAsync())
                lock (lines) lines.Add(line);
        });

        Assert.True(hub.Start(out _));
        Assert.True(await UntilAsync(() => { lock (lines) return lines.Any(l => l.Contains("\"kind\":\"waiting\"")); }));
        await hub.StopAsync();
        hub.Unsubscribe(id);
        await pump;

        Assert.False(hub.Running);
    }
}
