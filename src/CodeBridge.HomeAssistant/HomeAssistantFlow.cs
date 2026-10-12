using System.Text.Json;
using CodeBridge.Flow;

namespace CodeBridge.HomeAssistant;

/// <summary>Works out which Home Assistant entities the input and output blocks of a flow stand for.</summary>
public static class HomeAssistantFlow
{
    /// <summary>
    /// Digital Output / Digital Write / Blink LED become a switch, PWM Output a dimmable light, Analog Read a sensor in percent
    /// and Digital Read a binary sensor. Each pin shows up once. With no flow, the built-in LED is offered as a switch.
    /// </summary>
    public static List<HomeAssistantEntity> EntitiesFor(FlowDocument? document, BoardProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var result = new List<HomeAssistantEntity>();
        if (document is null)
        {
            if (profile.BuiltInLedPin is { } led)
                result.Add(new SwitchEntity("led", "Built-in LED", led));

            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in document.Nodes)
        {
            if (Int(node, "pin") is not { } pin)
                continue;

            switch (node.Type)
            {
                case BuiltInBlockCatalog.PwmWrite when seen.Add("out" + pin):
                    result.Add(new LightEntity($"pwm-{pin}", $"Light GPIO{pin}", pin, Math.Max(1, Int(node, "frequencyHz") ?? 5000)));
                    break;
                case BuiltInBlockCatalog.GpioDigitalWrite or BuiltInBlockCatalog.GpioSetOutput or BuiltInBlockCatalog.GpioBlinkLed when seen.Add("out" + pin):
                    result.Add(new SwitchEntity($"out-{pin}", pin == profile.BuiltInLedPin ? "Built-in LED" : $"Output GPIO{pin}", pin, Bool(node, "activeLow")));
                    break;
                case BuiltInBlockCatalog.GpioAnalogRead when seen.Add("analog" + pin):
                    result.Add(new SensorEntity(
                        $"analog-{pin}",
                        $"Analog GPIO{pin}",
                        async (board, ct) => await board.ReadAnalogAsync(pin, ct) * (100.0 / profile.AnalogMaxValue),
                        "%",
                        null,
                        TimeSpan.FromSeconds(5),
                        1,
                        0));
                    break;
                case BuiltInBlockCatalog.GpioDigitalRead when seen.Add("in" + pin):
                    result.Add(new BinarySensorEntity($"input-{pin}", $"Input GPIO{pin}", async (board, ct) => await board.ReadDigitalAsync(pin, ct), null, TimeSpan.FromSeconds(1)));
                    break;
            }
        }

        return result;
    }

    private static int? Int(FlowNode node, string name)
    {
        if (!node.Parameters.TryGetValue(name, out var value) || value is null)
            return null;

        return value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetInt32(out var i) => i,
            JsonElement { ValueKind: JsonValueKind.String } e when int.TryParse(e.GetString(), out var i) => i,
            string s when int.TryParse(s, out var i) => i,
            _ => null
        };
    }

    private static bool Bool(FlowNode node, string name)
    {
        if (!node.Parameters.TryGetValue(name, out var value) || value is null)
            return false;

        return value switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.String } e => bool.TryParse(e.GetString(), out var b) && b,
            string s => bool.TryParse(s, out var b) && b,
            _ => false
        };
    }
}
