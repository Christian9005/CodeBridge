using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CodeBridge.Flow;

// Shared by the tests of several projects (Core, Visual Studio, web editor): it is linked into each of them, so keep it plain C# that net472 compiles.
namespace CodeBridge.TestSupport
{
    /// <summary>One golden file and what must stay true about it. The facts come from <c>manifest.json</c>, next to the files.</summary>
    public sealed class GoldenFlow
    {
        public string File { get; set; } = string.Empty;
        public string Origin { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Board { get; set; } = string.Empty;
        public int Nodes { get; set; }
        public int Connections { get; set; }

        /// <summary>The validator accepts it. False for a file that is kept but cannot run (a block of a newer version).</summary>
        public bool Valid { get; set; }

        /// <summary>It can be run on the simulator and exported to C#.</summary>
        public bool Runs { get; set; }

        /// <summary>Commands the simulated board must have received, in this order (other commands may come between).</summary>
        public string[] Commands { get; set; } = Array.Empty<string>();

        public string[] ValidationContains { get; set; } = Array.Empty<string>();

        public string Path => System.IO.Path.Combine(GoldenFlows.Folder, File.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public override string ToString() => File;
    }

    public static class GoldenFlows
    {
        private static readonly Lazy<string> FolderValue = new Lazy<string>(FindFolder);

        public static string Folder => FolderValue.Value;

        private static string FindFolder()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var candidate = System.IO.Path.Combine(directory.FullName, "tests", "golden", "cbflow");
                if (System.IO.File.Exists(System.IO.Path.Combine(candidate, "manifest.json")))
                    return candidate;

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("tests/golden/cbflow was not found above " + AppContext.BaseDirectory);
        }

        public static IReadOnlyList<GoldenFlow> All()
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<List<GoldenFlow>>(System.IO.File.ReadAllText(System.IO.Path.Combine(Folder, "manifest.json")), options)
                   ?? throw new InvalidDataException("manifest.json is empty");
        }

        /// <summary>Every .cbflow under the golden folder, as paths relative to it with forward slashes.</summary>
        public static IReadOnlyList<string> FilesOnDisk() =>
            Directory.GetFiles(Folder, "*.cbflow", SearchOption.AllDirectories)
                .Select(p => p.Substring(Folder.Length).TrimStart('\\', '/').Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

        public static string Read(GoldenFlow flow) => System.IO.File.ReadAllText(flow.Path);

        /// <summary>The way Visual Studio's editor reads a file (FlowEditorPane): case-insensitive names, the framework's own JSON.</summary>
        public static FlowDocument LoadLikeVisualStudio(string json) =>
            JsonSerializer.Deserialize<FlowDocument>(json.TrimStart('﻿'), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("empty document");

        /// <summary>The way Visual Studio's editor writes a file.</summary>
        public static string SaveLikeVisualStudio(FlowDocument document) => JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });

        /// <summary>
        /// The meaning of a document, independent of how it was spelled: ids, types, positions, wires and every parameter with numbers compared as numbers
        /// (a file may say 34, 34.0 or "34"), so two readers or a save and a load can be compared with one string.
        /// </summary>
        public static string Canonical(FlowDocument document)
        {
            var text = new StringBuilder();
            text.Append("doc ").Append(document.Name).Append(" board=").Append(document.BoardId).AppendLine();
            foreach (var node in document.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
            {
                text.Append("node ").Append(node.Id).Append(' ').Append(node.Type).Append(" @")
                    .Append(Number(node.Position.X)).Append(',').Append(Number(node.Position.Y)).AppendLine();
                foreach (var parameter in node.Parameters.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                    text.Append("  ").Append(parameter.Key.ToLowerInvariant()).Append('=').Append(Value(parameter.Value)).AppendLine();
            }

            foreach (var wire in document.Connections.OrderBy(c => c.Id, StringComparer.Ordinal))
                text.Append("wire ").Append(wire.Id).Append(' ').Append(wire.FromNodeId).Append('.').Append(wire.FromPort).Append(" -> ")
                    .Append(wire.ToNodeId).Append('.').Append(wire.ToPort).AppendLine();

            return text.ToString();
        }

        private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

        private static string Value(object? value)
        {
            switch (value)
            {
                case null: return "null";
                case JsonElement element: return Element(element);
                case bool flag: return flag ? "true" : "false";
                case string s: return "\"" + s + "\"";
                case IFormattable number: return Number(Convert.ToDouble(number, CultureInfo.InvariantCulture));
                default: return value.ToString() ?? "";
            }
        }

        private static string Element(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.True: return "true";
                case JsonValueKind.False: return "false";
                case JsonValueKind.Null: return "null";
                case JsonValueKind.Number: return Number(element.GetDouble());
                case JsonValueKind.String: return "\"" + element.GetString() + "\"";
                default: return element.GetRawText();
            }
        }
    }
}
