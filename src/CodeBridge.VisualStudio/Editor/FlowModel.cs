#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;

namespace CodeBridge.Flow
{
    // Document model. Mirrors CodeBridge.Flow (net8) which cannot be referenced from a net472 VSIX;
    // the JSON produced here is the same .cbflow format the SDK and the FlowHost read.

    // A file can say null for anything ("Nodes": null, "Type": null). The model turns that into an empty value, so the rest of the editor never meets a
    // null reference; what is genuinely wrong with a file is reported by FlowDocumentChecks.
    public sealed class FlowDocument
    {
        private string _id = Guid.NewGuid().ToString();
        private string _name = "NewFlow";
        private string _boardId = "esp32-devkit";
        private List<FlowNode> _nodes = new List<FlowNode>();
        private List<FlowConnection> _connections = new List<FlowConnection>();

        public string Id { get => _id; init => _id = value ?? Guid.NewGuid().ToString(); }
        public string Name { get => _name; set => _name = value ?? "NewFlow"; }
        public string BoardId { get => _boardId; set => _boardId = value ?? "esp32-devkit"; }
        public int Version { get; set; } = 1;
        public List<FlowNode> Nodes { get => _nodes; init => _nodes = value ?? new List<FlowNode>(); }
        public List<FlowConnection> Connections { get => _connections; init => _connections = value ?? new List<FlowConnection>(); }
    }

    public sealed class FlowNode
    {
        private string _id = "node-" + Guid.NewGuid().ToString().Substring(0, 8);
        private string _type = string.Empty;
        private Dictionary<string, object> _parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public string Id { get => _id; init => _id = value ?? string.Empty; }
        public string Type { get => _type; set => _type = value ?? string.Empty; }
        public FlowPosition Position { get; set; }
        public Dictionary<string, object> Parameters { get => _parameters; init => _parameters = value ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase); }
    }

    public sealed class FlowConnection
    {
        private string _id = "conn-" + Guid.NewGuid().ToString().Substring(0, 8);
        private string _fromNodeId = string.Empty;
        private string _fromPort = string.Empty;
        private string _toNodeId = string.Empty;
        private string _toPort = string.Empty;

        public string Id { get => _id; init => _id = value ?? string.Empty; }
        public string FromNodeId { get => _fromNodeId; init => _fromNodeId = value ?? string.Empty; }
        public string FromPort { get => _fromPort; init => _fromPort = value ?? string.Empty; }
        public string ToNodeId { get => _toNodeId; init => _toNodeId = value ?? string.Empty; }
        public string ToPort { get => _toPort; init => _toPort = value ?? string.Empty; }
    }

    public struct FlowPosition
    {
        public double X { get; set; }
        public double Y { get; set; }

        public FlowPosition(double x, double y)
        {
            X = x;
            Y = y;
        }
    }

    public enum FlowPortDirection { Input, Output }
    public enum FlowValueKind { String, Integer, Number, Boolean, Trigger, Any }

    public class FlowPortDefinition
    {
        public string Name { get; set; } = string.Empty;
        public FlowPortDirection Direction { get; set; }
        public FlowValueKind ValueKind { get; set; } = FlowValueKind.Any;
        public bool Required { get; set; } = true;
    }

    public class FlowPropertyOption
    {
        public string DisplayName { get; set; }
        public object Value { get; set; }
        public string? Description { get; set; }

        public FlowPropertyOption(string displayName, object value, string? description = null)
        {
            DisplayName = displayName;
            Value = value;
            Description = description;
        }
    }

    public class FlowPropertyDefinition
    {
        public string Name { get; set; }
        public FlowValueKind ValueKind { get; set; }
        public object? DefaultValue { get; set; }
        public bool Required { get; set; }
        public bool IsAdvanced { get; set; }
        public List<FlowPropertyOption>? Options { get; set; }

        public FlowPropertyDefinition(string name, FlowValueKind kind, object? defaultValue = null, List<FlowPropertyOption>? options = null, bool required = false, bool isAdvanced = false)
        {
            Name = name;
            ValueKind = kind;
            DefaultValue = defaultValue;
            Options = options;
            Required = required;
            IsAdvanced = isAdvanced;
        }
    }

    public class FlowBlockDefinition
    {
        public string Type { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<FlowPortDefinition> Ports { get; set; } = new List<FlowPortDefinition>();
        public List<FlowPropertyDefinition> Properties { get; set; } = new List<FlowPropertyDefinition>();

        public IEnumerable<FlowPortDefinition> InputPorts => Ports.Where(p => p.Direction == FlowPortDirection.Input);
        public IEnumerable<FlowPortDefinition> OutputPorts => Ports.Where(p => p.Direction == FlowPortDirection.Output);

        public FlowPortDefinition? FindPort(string name, FlowPortDirection direction) =>
            Ports.FirstOrDefault(p => p.Direction == direction && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Block catalog for one board. Loaded from <c>Assets\catalog.&lt;board&gt;.json</c>, which is generated from the real
    /// runtime catalog (<c>CodeBridge.FlowHost catalog</c>) so pins, options and ports can never drift from the SDK.
    /// </summary>
    public sealed class FlowCatalog
    {
        private static readonly Dictionary<string, FlowCatalog> Cache = new Dictionary<string, FlowCatalog>(StringComparer.OrdinalIgnoreCase);

        public string BoardId { get; }
        public string DisplayName { get; }
        public IReadOnlyList<FlowBlockDefinition> Blocks { get; }

        private FlowCatalog(string boardId, string displayName, List<FlowBlockDefinition> blocks)
        {
            BoardId = boardId;
            DisplayName = displayName;
            Blocks = blocks;
        }

        public static FlowCatalog ForBoard(string? boardId)
        {
            var id = string.IsNullOrWhiteSpace(boardId) ? "esp32-devkit" : boardId!.Trim();
            lock (Cache)
            {
                if (Cache.TryGetValue(id, out var cached))
                    return cached;

                var catalog = TryLoad(id) ?? TryLoad("esp32-devkit") ?? new FlowCatalog(id, id, new List<FlowBlockDefinition>());
                Cache[id] = catalog;
                return catalog;
            }
        }

        public FlowBlockDefinition? Get(string type)
        {
            if (string.IsNullOrWhiteSpace(type))
                return null;

            var normalized = BuiltInBlockCatalog.Canonicalize(type);
            var exact = Blocks.FirstOrDefault(b => string.Equals(b.Type, normalized, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;

            var squashed = normalized.Replace(" ", string.Empty);
            return Blocks.FirstOrDefault(b =>
                string.Equals(b.DisplayName.Replace(" ", string.Empty), squashed, StringComparison.OrdinalIgnoreCase) ||
                b.Type.EndsWith("." + normalized, StringComparison.OrdinalIgnoreCase));
        }

        private static FlowCatalog? TryLoad(string boardId)
        {
            try
            {
                var directory = CodeBridge.VisualStudio.Editor.ExtensionPaths.Directory;
                var path = Path.Combine(directory, "Assets", $"catalog.{boardId}.json");
                if (!File.Exists(path))
                {
                    LastLoadError = "Catalog file not found: " + path;
                    return null;
                }

                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = (Dictionary<string, object>)serializer.DeserializeObject(File.ReadAllText(path));
                var blocks = new List<FlowBlockDefinition>();
                foreach (Dictionary<string, object> block in List(root, "blocks"))
                {
                    var definition = new FlowBlockDefinition
                    {
                        Type = Str(block, "type"),
                        DisplayName = Str(block, "displayName"),
                        Category = Str(block, "category"),
                        Description = Str(block, "description")
                    };

                    foreach (Dictionary<string, object> port in List(block, "ports"))
                    {
                        definition.Ports.Add(new FlowPortDefinition
                        {
                            Name = Str(port, "name"),
                            Direction = ParseEnum(Str(port, "direction"), FlowPortDirection.Input),
                            ValueKind = ParseEnum(Str(port, "valueKind"), FlowValueKind.Any),
                            Required = port.TryGetValue("required", out var req) && req is bool b && b
                        });
                    }

                    foreach (Dictionary<string, object> property in List(block, "properties"))
                    {
                        List<FlowPropertyOption>? options = null;
                        var rawOptions = List(property, "options").ToList();
                        if (rawOptions.Count > 0)
                        {
                            options = rawOptions
                                .Cast<Dictionary<string, object>>()
                                .Select(o => new FlowPropertyOption(Str(o, "label"), Normalize(o.TryGetValue("value", out var v) ? v : null)!, o.TryGetValue("description", out var d) ? d as string : null))
                                .ToList();
                        }

                        definition.Properties.Add(new FlowPropertyDefinition(
                            Str(property, "name"),
                            ParseEnum(Str(property, "valueKind"), FlowValueKind.String),
                            Normalize(property.TryGetValue("defaultValue", out var dv) ? dv : null),
                            options,
                            property.TryGetValue("required", out var r) && r is bool rb && rb,
                            property.TryGetValue("isAdvanced", out var a) && a is bool ab && ab));
                    }

                    blocks.Add(definition);
                }

                return new FlowCatalog(Str(root, "boardId"), Str(root, "displayName"), blocks);
            }
            catch (Exception ex)
            {
                LastLoadError = ex.ToString();
                return null;
            }
        }

        /// <summary>Why the last catalog file failed to load (diagnostics and tests).</summary>
        public static string? LastLoadError { get; private set; }

        private static string Str(Dictionary<string, object> map, string key) =>
            map.TryGetValue(key, out var value) && value != null ? value.ToString() ?? string.Empty : string.Empty;

        private static IEnumerable<object> List(Dictionary<string, object> map, string key)
        {
            if (map.TryGetValue(key, out var value) && value is IEnumerable enumerable && !(value is string))
                return enumerable.Cast<object>();

            return Enumerable.Empty<object>();
        }

        private static T ParseEnum<T>(string value, T fallback) where T : struct =>
            Enum.TryParse(value, true, out T parsed) ? parsed : fallback;

        private static object? Normalize(object? value) => value is decimal d ? (object)(double)d : value;
    }

    public static class BuiltInBlockCatalog
    {
        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Start", "flow.manual-trigger" },
            { "ManualTrigger", "flow.manual-trigger" },
            { "Timer", "core.timer" },
            { "Delay", "core.timer" },
            { "Boolean", "logic.constant-boolean" },
            { "ConstantBoolean", "logic.constant-boolean" },
            { "Number", "logic.constant-number" },
            { "ConstantNumber", "logic.constant-number" },
            { "Compare", "logic.compare" },
            { "PinMode", "gpio.pin-mode" },
            { "DigitalRead", "gpio.digital-read" },
            { "AnalogRead", "gpio.analog-read" },
            { "DigitalWrite", "gpio.digital-write" },
            { "SetOutput", "gpio.set-output" },
            { "DigitalOutput", "gpio.set-output" },
            { "BlinkLed", "gpio.blink-led" },
            { "Blink", "gpio.blink-led" },
            { "SampleChannel", "acquisition.sample-channel" },
            { "InterruptInput", "acquisition.interrupt-input" },
            { "StreamDashboard", "dashboard.stream" },
            { "StreamToDashboard", "dashboard.stream" },
            { "ServoWrite", "servo.write" },
            { "Servo", "servo.write" },
            { "Map", "math.map" },
            { "MathMap", "math.map" },
            { "Pwm", "gpio.pwm-write" },
            { "PwmWrite", "gpio.pwm-write" },
            { "AnalogWrite", "gpio.pwm-write" },
            { "Debug", "debug.log" },
            { "DebugLog", "debug.log" }
        };

        /// <summary>Maps legacy and shortened block names (Start, DigitalWrite, ...) to canonical block types.</summary>
        public static string Canonicalize(string type)
        {
            var trimmed = type.Trim();
            return Aliases.TryGetValue(trimmed, out var canonical) ? canonical : trimmed;
        }

        /// <summary>Reads a node parameter, tolerating case differences and values that came back from JSON as JsonElement.</summary>
        public static object? GetParameterValue(FlowNode? node, FlowPropertyDefinition? prop)
        {
            if (node == null || prop == null || node.Parameters == null) return prop?.DefaultValue;

            object? rawVal = null;
            if (node.Parameters.TryGetValue(prop.Name, out var v1)) rawVal = v1;
            else if (!string.IsNullOrEmpty(prop.Name))
            {
                string pascal = char.ToUpperInvariant(prop.Name[0]) + prop.Name.Substring(1);
                if (node.Parameters.TryGetValue(pascal, out var v2)) rawVal = v2;
                else
                {
                    foreach (var kvp in node.Parameters)
                    {
                        if (string.Equals(kvp.Key, prop.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            rawVal = kvp.Value;
                            break;
                        }
                    }
                }
            }

            if (rawVal == null) return prop.DefaultValue;

            if (rawVal is System.Text.Json.JsonElement element)
            {
                switch (element.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.True: return true;
                    case System.Text.Json.JsonValueKind.False: return false;
                    case System.Text.Json.JsonValueKind.Number:
                        if (prop.ValueKind == FlowValueKind.Integer && element.TryGetInt32(out int iVal)) return iVal;
                        return element.GetDouble();
                    case System.Text.Json.JsonValueKind.String: return element.GetString() ?? string.Empty;
                    default: return element.ToString();
                }
            }

            return rawVal;
        }

        /// <summary>Same rule as the runtime (FlowTypeCompatibility): Any matches everything, Integer feeds Number.</summary>
        public static bool AreCompatible(FlowValueKind output, FlowValueKind input)
        {
            if (output == FlowValueKind.Any || input == FlowValueKind.Any) return true;
            if (output == input) return true;
            return output == FlowValueKind.Integer && input == FlowValueKind.Number;
        }
    }
}

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
