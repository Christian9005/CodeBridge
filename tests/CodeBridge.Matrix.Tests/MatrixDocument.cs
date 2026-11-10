using System.Text;
using CodeBridge.Core.Enums;

namespace CodeBridge.Matrix.Tests;

/// <summary>The table of docs/block-board-matrix.md, written from what the cells did, so the document cannot drift away from the code.</summary>
internal static class MatrixDocument
{
    public const string Start = "<!-- matrix:start -->";
    public const string End = "<!-- matrix:end -->";

    public const string Works = "works";
    public const string NeedsChip = "needs the chip";
    public const string PlanOnly = "plan only";
    public const string NotOffered = "-";
    public const string Broken = "BROKEN";

    public static string Symbol(Cell? cell)
    {
        if (cell is null)
            return NotOffered;

        if (!cell.Valid || cell.ExportProblem is not null || cell.Run is null)
            return Broken;

        return cell.Run.Kind switch
        {
            RunKind.NothingAttached => NeedsChip,
            RunKind.Failed => Broken,
            // the buffered acquisition lives in the ESP32 firmware; on an Arduino the block hands its sampling plan to the next block
            _ when cell.Block.Type == "acquisition.sample-channel" && cell.Board.Family == BoardFamily.Arduino => PlanOnly,
            _ => Works
        };
    }

    public static string Render(IReadOnlyList<Cell> cells)
    {
        var boards = Matrix.Boards;
        var blocks = cells.Select(c => c.Block).GroupBy(b => b.Type).Select(g => g.First())
            .OrderBy(b => b.Category, StringComparer.Ordinal).ThenBy(b => b.Type, StringComparer.Ordinal).ToList();

        var text = new StringBuilder();
        text.AppendLine(Start);
        text.AppendLine("<!-- Written by the tests of tests/CodeBridge.Matrix.Tests. Do not edit by hand: run them with CODEBRIDGE_UPDATE_MATRIX=1 to write it again. -->");
        text.AppendLine();
        text.AppendLine("| Block | Category | " + string.Join(" | ", boards.Select(b => b.DisplayName)) + " |");
        text.AppendLine("|---|---|" + string.Join("|", boards.Select(_ => "---")) + "|");
        foreach (var block in blocks)
        {
            var row = boards.Select(board => Symbol(cells.FirstOrDefault(c => c.Board.Id == board.Id && c.Block.Type == block.Type)));
            text.AppendLine($"| `{block.Type}` | {block.Category} | {string.Join(" | ", row)} |");
        }

        text.AppendLine();
        text.AppendLine("| Board | Blocks | " + string.Join(" | ", new[] { Works, NeedsChip, PlanOnly, Broken }) + " |");
        text.AppendLine("|---|---|---|---|---|---|");
        foreach (var board in boards)
        {
            var mine = cells.Where(c => c.Board.Id == board.Id).Select(Symbol).ToList();
            text.AppendLine($"| {board.DisplayName} | {mine.Count} | {mine.Count(s => s == Works)} | {mine.Count(s => s == NeedsChip)} | {mine.Count(s => s == PlanOnly)} | {mine.Count(s => s == Broken)} |");
        }

        text.AppendLine();
        text.Append(End);
        return text.ToString().Replace("\r\n", "\n");
    }
}
