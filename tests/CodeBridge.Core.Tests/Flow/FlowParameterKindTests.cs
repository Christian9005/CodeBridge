using System.Globalization;
using System.Text.Json;
using CodeBridge.ESP32;
using CodeBridge.Flow;
using CodeBridge.Flow.CodeGeneration;
using CodeBridge.Flow.Execution;
using CodeBridge.Flow.Serialization;
using CodeBridge.Flow.Validation;
using CodeBridge.Transport.Simulation;

namespace CodeBridge.Core.Tests.Flow;

/// <summary>
/// The value of a parameter in a file may be a number, a number written as text, or something that is not a number at all. The validator, the runtime and
/// the exporter read it the same way: the first two kinds work, the third is reported by name instead of crashing.
/// </summary>
public class FlowParameterKindTests
{
    private static FlowDocument LedFlow(string pinJson, string valueJson = "true")
    {
        var json = $$"""
        {
          "Name": "Led", "BoardId": "esp32-devkit",
          "Nodes": [
            { "Id": "go", "Type": "flow.manual-trigger" },
            { "Id": "led", "Type": "gpio.digital-write", "Parameters": { "pin": {{pinJson}}, "value": {{valueJson}} } }
          ],
          "Connections": [ { "Id": "c", "FromNodeId": "go", "FromPort": "trigger", "ToNodeId": "led", "ToPort": "trigger" } ]
        }
        """;
        return FlowDocumentJson.Deserialize(json);
    }

    private static FlowBlockCatalog CatalogOf(FlowDocument document) =>
        BuiltInBlockCatalog.Create(BuiltInBoardProfiles.FindById(document.BoardId) ?? BuiltInBoardProfiles.Default);

    private static FlowValidationResult Validate(FlowDocument document) => new FlowValidator().Validate(document, CatalogOf(document));

    private static async Task<IReadOnlyList<string>> RunAsync(FlowDocument document)
    {
        var profile = BuiltInBoardProfiles.FindById(document.BoardId) ?? BuiltInBoardProfiles.Default;
        var transport = new SimulatedTransport(new SimulatedBoardOptions { AnalogMax = profile.AnalogMaxValue, MaxPin = profile.MaxPin });
        var board = new ESP32Board(transport, profile.MaxPin);
        await board.ConnectAsync();
        var before = transport.CommandLog.Count;
        await new FlowRuntime(CatalogOf(document)).ExecuteAsync(document, board);
        return transport.CommandLog.Skip(before).ToList();
    }

    [Theory]
    [InlineData("2")]
    [InlineData("2.0")]
    [InlineData("\"2\"")]
    [InlineData("\" 2 \"")]
    [InlineData("2e0")]
    public async Task A_pin_written_as_a_number_a_decimal_or_text_is_the_same_pin_everywhere(string pin)
    {
        var document = LedFlow(pin);

        Assert.True(Validate(document).IsValid);
        Assert.Contains("DW:2:1", await RunAsync(document));
        Assert.Contains("DigitalWriteAsync(2, PinValue.High", FlowCSharpGenerator.Generate(document).Code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("true", "True")]
    [InlineData("\"true\"", "True")]
    [InlineData("\"False\"", "False")]
    [InlineData("1", "True")]
    [InlineData("0", "False")]
    public async Task A_yes_or_no_may_be_a_boolean_the_word_or_a_one_or_zero(string value, string expected)
    {
        var level = expected == "True" ? "DW:2:1" : "DW:2:0";

        Assert.Contains(level, await RunAsync(LedFlow("2", value)));
    }

    [Theory]
    [InlineData("\"abc\"", "must be a whole number", "\"abc\"")]
    [InlineData("2.5", "must be a whole number", "2.5")]
    [InlineData("true", "must be a whole number", "true")]
    [InlineData("{}", "must be a whole number", "{}")]
    [InlineData("[1]", "must be a whole number", "[1]")]
    [InlineData("1e99", "must be a whole number", "1e99")]
    public void A_pin_that_is_not_a_number_is_reported_by_name_and_never_crashes_the_validator(string pin, string says, string showsTheValue)
    {
        var validation = Validate(LedFlow(pin));

        Assert.False(validation.IsValid);
        var issue = Assert.Single(validation.Errors, e => e.NodeId == "led");
        Assert.Contains("'pin'", issue.Message);
        Assert.Contains(says, issue.Message);
        Assert.Contains(showsTheValue, issue.Message);
    }

    [Fact]
    public void A_value_that_is_not_a_boolean_is_reported_by_name()
    {
        var validation = Validate(LedFlow("2", "\"maybe\""));

        Assert.Contains(validation.Errors, e => e.Message.Contains("'value'") && e.Message.Contains("true or false"));
    }

    [Fact]
    public void One_bad_value_does_not_hide_the_other_problems_of_the_flow()
    {
        var document = LedFlow("\"abc\"");
        document.Nodes.Add(new FlowNode { Id = "nowhere", Type = "does.not.exist" });

        var validation = Validate(document);

        Assert.Contains(validation.Errors, e => e.Message.Contains("'pin'"));
        Assert.Contains(validation.Errors, e => e.Message.Contains("does.not.exist"));
    }

    [Fact]
    public async Task Running_a_flow_with_a_bad_pin_says_which_value_and_what_it_should_be()
    {
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(LedFlow("\"abc\"")));

        Assert.Contains("'pin' must be a whole number", failure.ToString());
    }

    [Fact]
    public void Exporting_a_flow_with_a_bad_pin_is_refused_with_the_reason()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => FlowCSharpGenerator.Generate(LedFlow("\"abc\"")));

        Assert.Contains("'pin'", failure.Message);
    }

    [Theory]
    [InlineData("es-EC")]
    [InlineData("de-DE")]
    public void Numbers_in_a_file_are_read_with_dots_whatever_the_language_of_the_machine(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            var document = LedFlow("\"2.0\"");

            Assert.True(Validate(document).IsValid);
            Assert.Contains("DigitalWriteAsync(2,", FlowCSharpGenerator.Generate(document).Code, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_parameter_the_block_does_not_have_is_a_warning_and_the_flow_still_runs()
    {
        var json = """
        {
          "Name": "Extra", "BoardId": "esp32-devkit",
          "Nodes": [
            { "Id": "go", "Type": "flow.manual-trigger" },
            { "Id": "led", "Type": "gpio.digital-write", "Parameters": { "pin": 2, "value": true, "colour": "red" } }
          ],
          "Connections": [ { "Id": "c", "FromNodeId": "go", "FromPort": "trigger", "ToNodeId": "led", "ToPort": "trigger" } ]
        }
        """;
        var document = FlowDocumentJson.Deserialize(json);

        var validation = Validate(document);

        Assert.True(validation.IsValid);
        Assert.Contains(validation.Warnings, w => w.Message.Contains("'colour'"));
        Assert.IsType<JsonElement>(document.Nodes[1].Parameters["colour"]);
    }
}
