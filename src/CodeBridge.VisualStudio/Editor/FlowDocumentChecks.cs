#nullable enable
using System;
using System.Collections.Generic;
using CodeBridge.Flow;

namespace CodeBridge.VisualStudio.Editor
{
    /// <summary>
    /// What the editor needs from a file before it can show it. A .cbflow can be damaged or written by hand (a block that is null, two blocks with the same id,
    /// a wire with no ends): the editor refuses those with a sentence that says what is wrong, and leaves the file untouched, instead of failing half way
    /// through drawing it. Kept free of WPF so it can be tested without Visual Studio.
    /// </summary>
    internal static class FlowDocumentChecks
    {
        /// <summary>Null when the document can be opened; otherwise what is wrong with the file, in a sentence.</summary>
        public static string? Problem(FlowDocument document)
        {
            var blockIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var position = 0;
            foreach (var node in document.Nodes)
            {
                position++;
                if (node == null)
                    return "Block " + position + " of the file is empty.";

                if (string.IsNullOrWhiteSpace(node.Id))
                    return "Block " + position + " of the file has no id.";

                if (!blockIds.Add(node.Id))
                    return "Two blocks of the file have the id '" + node.Id + "'. Every block needs its own id.";
            }

            var wireIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            position = 0;
            foreach (var wire in document.Connections)
            {
                position++;
                if (wire == null)
                    return "Wire " + position + " of the file is empty.";

                if (string.IsNullOrWhiteSpace(wire.Id))
                    return "Wire " + position + " of the file has no id.";

                if (!wireIds.Add(wire.Id))
                    return "Two wires of the file have the id '" + wire.Id + "'. Every wire needs its own id.";

                if (string.IsNullOrWhiteSpace(wire.FromNodeId) || string.IsNullOrWhiteSpace(wire.ToNodeId))
                    return "Wire '" + wire.Id + "' of the file does not say which blocks it connects.";
            }

            return null;
        }
    }
}
