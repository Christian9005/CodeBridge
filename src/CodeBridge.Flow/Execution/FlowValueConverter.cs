using System.Globalization;
using System.Text.Json;

namespace CodeBridge.Flow.Execution;

/// <summary>
/// Reads the values of a flow file: the parameters of a block and the values that travel on wires. The same rules serve the validator, the runtime and the
/// C# exporter, so a flow that validates also runs and exports. A file may say 34, 34.0 or "34" for a whole number, and a machine whose language uses a comma
/// for decimals must read the same file the same way: numbers are always read with the invariant culture.
/// </summary>
internal static class FlowValueConverter
{
    public static T ConvertTo<T>(object? value, string name)
    {
        if (value is null)
            throw new InvalidOperationException($"Value '{name}' is required.");

        if (value is T typed)
            return typed;

        if (value is JsonElement json)
            return ConvertJsonElement<T>(json, name);

        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        if (targetType == typeof(string))
            return (T)(object)Convert.ToString(value, CultureInfo.InvariantCulture)!;

        if (targetType == typeof(bool) && value is int intBool)
            return (T)(object)(intBool != 0);

        try
        {
            return (T)Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw Wrong(name, targetType, Describe(value), ex);
        }
    }

    private static T ConvertJsonElement<T>(JsonElement json, string name)
    {
        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        if (targetType == typeof(string))
        {
            return (T)(object)(json.ValueKind switch
            {
                JsonValueKind.String => json.GetString() ?? string.Empty,
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                _ => json.GetRawText()
            });
        }

        if (targetType == typeof(bool))
        {
            switch (json.ValueKind)
            {
                case JsonValueKind.True:
                    return (T)(object)true;
                case JsonValueKind.False:
                    return (T)(object)false;
                case JsonValueKind.Number when json.TryGetDouble(out var number):
                    return (T)(object)(number != 0);
                case JsonValueKind.String when bool.TryParse(json.GetString(), out var flag):
                    return (T)(object)flag;
            }
        }
        else if (targetType == typeof(int))
        {
            if (TryDouble(json, out var number) && number == Math.Floor(number) && number >= int.MinValue && number <= int.MaxValue)
                return (T)(object)(int)number;
        }
        else if (targetType == typeof(double))
        {
            if (TryDouble(json, out var number))
                return (T)(object)number;
        }
        else
        {
            throw new InvalidOperationException($"Value '{name}' cannot be converted to {targetType.Name}.");
        }

        throw Wrong(name, targetType, Describe(json), null);
    }

    private static bool TryDouble(JsonElement json, out double number)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.Number:
                return json.TryGetDouble(out number);
            case JsonValueKind.String:
                return double.TryParse(json.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
            default:
                number = 0;
                return false;
        }
    }

    private static InvalidOperationException Wrong(string name, Type target, string found, Exception? inner)
    {
        var expected = target == typeof(int) ? "a whole number" : target == typeof(double) ? "a number" : target == typeof(bool) ? "true or false" : target.Name;
        return new InvalidOperationException($"'{name}' must be {expected}, but the file has {found}.", inner);
    }

    private static string Describe(object value) =>
        value is JsonElement json
            ? json.ValueKind == JsonValueKind.String ? $"\"{json.GetString()}\"" : json.GetRawText()
            : value is string text ? $"\"{text}\"" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "nothing";
}
