using System.Text.Json;
using CodeBridge.Flow;
using CodeBridge.Flow.Serialization;
using CodeBridge.IotBindings;

namespace CodeBridge.WebEditor.Tests;

/// <summary>What the page sends is whatever a browser, a script or another machine on the network sends. The server answers, never throws, and never writes a flow that is not valid.</summary>
public sealed class HostileFlowTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cb-web-hostile-" + Guid.NewGuid().ToString("N"));

    public HostileFlowTests()
    {
        Directory.CreateDirectory(_folder);
        IotBindingBlocks.Register();
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private const string Led = "{ \"Id\": \"led\", \"Type\": \"gpio.digital-write\", \"Parameters\": { \"pin\": 2, \"value\": true } }";
    private const string Go = "{ \"Id\": \"go\", \"Type\": \"flow.manual-trigger\" }";

    private static string Doc(string nodes, string wires = "[]") => "{ \"Name\": \"x\", \"BoardId\": \"esp32-devkit\", \"Nodes\": " + nodes + ", \"Connections\": " + wires + " }";

    public static IEnumerable<object[]> Bodies() => new[]
    {
        "", " ", "null", "[]", "42", "{", "not json at all", "{}", Doc("null"), Doc("[null]"), Doc("[{ }]"),
        Doc("[{ \"Id\": null, \"Type\": null }]"), Doc("[" + Go + "," + Go + "]"),
        Doc("[" + Go + "," + Led + "]", "[null]"), Doc("[" + Go + "," + Led + "]", "[{ }]"),
        Doc("[" + Go + "," + Led + "]", "[{ \"Id\": \"c\", \"FromNodeId\": \"go\", \"FromPort\": \"trigger\", \"ToNodeId\": \"led\", \"ToPort\": \"trigger\" },{ \"Id\": \"c\", \"FromNodeId\": \"go\", \"FromPort\": \"trigger\", \"ToNodeId\": \"led\", \"ToPort\": \"trigger\" }]"),
        Doc("[{ \"Id\": \"a\", \"Type\": \"gpio.digital-write\", \"Parameters\": { \"pin\": \"abc\" } }]"),
        "{ \"Name\": \"" + new string('x', 2_000_000) + "\", \"BoardId\": \"esp32-devkit\" }",
        "{ \"Name\": \"x\", \"BoardId\": \"no-such-board\", \"Nodes\": [" + Go + "] }",
    }.Select(body => new object[] { body });

    [Theory]
    [MemberData(nameof(Bodies))]
    public void A_body_that_is_not_a_valid_flow_gets_an_answer_and_leaves_the_file_alone(string body)
    {
        var path = Path.Combine(_folder, "flow.cbflow");
        var document = new FlowDocument { Name = "Original", BoardId = "esp32-devkit" };
        document.Nodes.Add(new FlowNode { Id = "go", Type = "flow.manual-trigger" });
        File.WriteAllText(path, FlowDocumentJson.Serialize(document));
        var before = File.ReadAllBytes(path);
        var session = new EditorSession(path, boardId: null);

        var answer = JsonSerializer.SerializeToElement(session.Update(body, save: true), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        // either it says "this is not valid", or it was a flow after all and was saved; it never throws and never writes something invalid
        var valid = answer.GetProperty("valid").GetBoolean();
        var saved = answer.GetProperty("saved").GetBoolean();
        Assert.Equal(valid, saved);
        if (!valid)
            Assert.Equal(before, File.ReadAllBytes(path));
        else
            Assert.True(new CodeBridge.Flow.Validation.FlowValidator().Validate(FlowDocumentJson.Deserialize(File.ReadAllText(path)), BuiltInBlockCatalog.Create(BuiltInBoardProfiles.Default)).IsValid);
    }

    [Fact]
    public void Opening_a_damaged_file_from_the_command_line_fails_with_a_clear_error_not_an_internal_one()
    {
        var path = Path.Combine(_folder, "broken.cbflow");
        foreach (var content in new[] { "{", "not json", "[]", "\0\0\0", "{\"Nodes\":[null]}" })
        {
            File.WriteAllText(path, content);

            try
            {
                var session = new EditorSession(path, boardId: null);
                var answer = JsonSerializer.SerializeToElement(session.Update(session.FlowJson(), save: false), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                Assert.False(answer.GetProperty("saved").GetBoolean());
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
            {
                // a refusal to open is fine; anything else (NullReferenceException, ...) fails the test
            }
        }
    }
}
