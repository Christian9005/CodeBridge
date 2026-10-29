using CodeBridge.ESP32;
using CodeBridge.Flow;
using CodeBridge.Flow.CodeGeneration;
using CodeBridge.Flow.Execution;
using CodeBridge.Flow.Serialization;
using CodeBridge.Flow.Validation;
using CodeBridge.Transport.Simulation;

namespace CodeBridge.Core.Tests.Flow;

/// <summary>The smart greenhouse of the docs (samples/SmartGreenhouse): it must stay valid and make the right decisions, since people copy it.</summary>
public class SmartGreenhouseTests
{
    private static FlowDocument Load()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "CodeBridge.slnx")))
            folder = folder.Parent;

        Assert.NotNull(folder);
        return FlowDocumentJson.Deserialize(File.ReadAllText(Path.Combine(folder!.FullName, "samples", "SmartGreenhouse", "greenhouse.cbflow")));
    }

    /// <summary>Runs one pass with the air at <paramref name="celsius"/> and the soil probe at <paramref name="soilRaw"/>, and returns the last level written to each relay pin.</summary>
    private static async Task<(int Fan, int Pump, FlowExecutionResult Result)> PassAsync(double celsius, int soilRaw)
    {
        var document = Load();
        var profile = BuiltInBoardProfiles.FindById(document.BoardId)!;
        var transport = new SimulatedTransport(new SimulatedBoardOptions { AnalogMax = profile.AnalogMaxValue, MaxPin = profile.MaxPin }) { Temperature = celsius };
        transport.SetAnalogInput(34, soilRaw);
        var board = new ESP32Board(transport, profile.MaxPin);
        await board.ConnectAsync();

        var result = await new FlowRuntime(BuiltInBlockCatalog.Create(profile)).ExecuteAsync(document, board);

        int Last(int pin) => int.Parse(transport.CommandLog.Last(c => c.StartsWith($"DW:{pin}:", StringComparison.Ordinal)).Split(':')[2]);
        return (Last(27), Last(26), result);
    }

    [Fact]
    public void The_greenhouse_flow_is_valid_on_the_ESP32_DevKit()
    {
        var document = Load();

        var validation = new FlowValidator().Validate(document, BuiltInBlockCatalog.Create(BuiltInBoardProfiles.FindById(document.BoardId)!));

        Assert.True(validation.IsValid, string.Join("; ", validation.Errors.Select(e => e.Message)));
        Assert.Equal("Smart Greenhouse", document.Name);
    }

    [Fact]
    public async Task A_hot_and_dry_greenhouse_runs_the_fan_and_the_pump()
    {
        var (fan, pump, result) = await PassAsync(celsius: 31, soilRaw: 3900);   // a dry probe gives a high voltage

        Assert.Equal(0, fan);    // the relays are active low: 0 switches them on
        Assert.Equal(0, pump);
        Assert.Equal(true, result.NodeOutputs["hot"]["result"]);
        Assert.Equal(true, result.NodeOutputs["dry"]["result"]);
    }

    [Fact]
    public async Task A_cool_and_watered_greenhouse_keeps_both_off()
    {
        var (fan, pump, _) = await PassAsync(celsius: 22, soilRaw: 1200);

        Assert.Equal(1, fan);
        Assert.Equal(1, pump);
    }

    [Fact]
    public async Task The_display_shows_the_air_temperature_and_the_soil_moisture()
    {
        var (_, _, result) = await PassAsync(celsius: 24, soilRaw: 1200);

        Assert.InRange(Assert.IsType<double>(result.NodeOutputs["soil"]["percent"]), 65, 75);
        Assert.Equal(true, result.NodeOutputs["lcdAir"]["done"]);
        Assert.Equal(true, result.NodeOutputs["lcdSoil"]["done"]);
    }

    [Fact]
    public void The_greenhouse_exports_to_CSharp_without_warnings()
    {
        var generated = FlowCSharpGenerator.Generate(Load());

        Assert.Empty(generated.Warnings);
        Assert.Contains("CreateBme280", generated.Code);
    }
}
