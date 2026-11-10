using CodeBridge.Core.Enums;
using CodeBridge.Flow;

namespace CodeBridge.Matrix.Tests;

/// <summary>
/// Every block on every board. The editor offers 65 blocks on an ESP32 and 24 on an Arduino, on nine boards: this checks that each block it offers is valid with
/// what the editor fills in, runs on the virtual board of that board, and exports to C# that compiles. The result is the table of docs/block-board-matrix.md.
/// </summary>
public sealed class MatrixTests : IClassFixture<MatrixFixture>
{
    private readonly MatrixFixture _matrix;

    public MatrixTests(MatrixFixture matrix) => _matrix = matrix;

    private static void NoneOf(IEnumerable<string> problems, string what)
    {
        var list = problems.ToList();
        Assert.True(list.Count == 0, $"{list.Count} {what}:{Environment.NewLine}{string.Join(Environment.NewLine, list.Take(25))}");
    }

    [Fact]
    public void All_nine_boards_are_in_the_matrix_and_the_ESP32_boards_offer_the_same_blocks()
    {
        Assert.Equal(9, Matrix.Boards.Count);

        var offered = Matrix.Boards.ToDictionary(b => b.Id, b => Matrix.CatalogOf(b).Blocks.Select(x => x.Type).ToHashSet());
        var esp32 = Matrix.Boards.Where(b => b.Family == BoardFamily.ESP32).Select(b => offered[b.Id]).ToList();
        Assert.All(esp32, set => Assert.True(set.SetEquals(esp32[0]), "an ESP32 board offers other blocks than the rest"));

        // an Arduino offers fewer blocks, never different ones
        foreach (var board in Matrix.Boards.Where(b => b.Family == BoardFamily.Arduino))
            Assert.True(offered[board.Id].IsSubsetOf(esp32[0]), board.Id + " offers a block the ESP32 boards do not");
    }

    [Fact]
    public void A_default_is_always_one_the_board_offers_in_the_list_of_that_property()
    {
        // a default outside the list is not shown by the editor's drop-down, and the block is invalid the moment it is dropped on the canvas
        var problems = new List<string>();
        foreach (var board in Matrix.Boards)
        {
            foreach (var block in Matrix.CatalogOf(board).Blocks)
            {
                foreach (var property in block.Properties.Where(p => p.Options is { Count: > 0 } && p.DefaultValue is not null))
                {
                    if (!property.Options!.Any(o => Matrix.SameValue(o.Value, property.DefaultValue)))
                        problems.Add($"{board.Id} / {block.Type}.{property.Name}: default {property.DefaultValue} is not one of {string.Join(", ", property.Options!.Select(o => o.Value))}");
                }
            }
        }

        NoneOf(problems, "default(s) the board does not offer");
    }

    [Fact]
    public void Every_block_is_valid_with_its_defaults_once_the_pin_that_has_no_default_is_chosen()
    {
        NoneOf(_matrix.Cells.Where(c => !c.Valid).Select(c => $"{c} ({c.Setup}): {string.Join(" ; ", c.Errors)}"), "block(s) invalid on a board that offers them");
    }

    [Fact]
    public void Every_block_runs_on_the_virtual_board_or_says_that_the_chip_it_needs_is_missing()
    {
        NoneOf(_matrix.Cells.Where(c => c.Run is { Kind: RunKind.Failed }).Select(c => $"{c}: {c.Run!.Detail}"), "block(s) that crashed on the virtual board");
    }

    [Fact]
    public void The_blocks_that_need_a_chip_are_the_I2C_chips_and_nothing_else()
    {
        // a block that says "nothing attached" must be one that really depends on something being wired: the I2C chips of the .NET IoT drivers
        var notChips = _matrix.Cells.Where(c => c.Run is { Kind: RunKind.NothingAttached } && !c.Block.Type.StartsWith("iot.", StringComparison.Ordinal))
            .Select(c => $"{c}: {c.Run!.Detail}");

        NoneOf(notChips, "block(s) that are not an I2C chip but report a missing one");
    }

    [Fact]
    public void A_block_that_talks_to_the_hardware_sends_it_commands()
    {
        // a GPIO, actuator, sensor or module block that ran without sending a single command would look fine and do nothing
        var hardware = new[] { "GPIO", "Actuators", "Sensors", "Modules" };
        var silent = _matrix.Cells.Where(c => hardware.Contains(c.Block.Category) && c.Run is { Commands.Count: 0 })
            .Select(c => $"{c} ran and sent nothing");

        NoneOf(silent, "hardware block(s) that did nothing");
    }

    [Fact]
    public void The_pin_a_block_is_given_is_the_pin_it_uses()
    {
        // the first pin of the list of the board reaches the board in the command: a block that ignored its property would use the wrong pin
        var gpio = new[] { "gpio.digital-write", "gpio.digital-read", "gpio.pwm-write", "gpio.analog-read" };
        var problems = new List<string>();
        foreach (var cell in _matrix.Cells.Where(c => c.Run is { Kind: RunKind.Completed } && gpio.Contains(c.Block.Type)))
        {
            var pin = Convert.ToInt32(cell.Flow.Nodes.Single(n => n.Id == "block").Parameters["pin"], System.Globalization.CultureInfo.InvariantCulture);
            var prefix = cell.Block.Type switch { "gpio.digital-write" => "DW:", "gpio.digital-read" => "DR:", "gpio.pwm-write" => "PW:", _ => "AR:" };
            if (!cell.Run!.Commands.Any(command => command.StartsWith(prefix + pin + ":", StringComparison.Ordinal) || command == prefix + pin))
                problems.Add($"{cell}: pin {pin} chosen, the board got {string.Join(" ", cell.Run.Commands)}");
        }

        NoneOf(problems, "block(s) that did not use the pin they were given");
    }

    [Fact]
    public void The_exported_C_sharp_of_every_block_compiles()
    {
        NoneOf(_matrix.Cells.Where(c => c.Valid && c.ExportProblem is not null).Select(c => $"{c}: {c.ExportProblem}"), "block(s) whose C# does not export or compile");
    }

    [Fact]
    public void The_matrix_in_the_documentation_is_the_one_the_code_gives()
    {
        var path = Path.Combine(RepoRoot(), "docs", "block-board-matrix.md");
        var document = File.ReadAllText(path).Replace("\r\n", "\n");
        var start = document.IndexOf(MatrixDocument.Start, StringComparison.Ordinal);
        var end = document.IndexOf(MatrixDocument.End, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "docs/block-board-matrix.md has no matrix section between the two markers");

        var current = document[start..(end + MatrixDocument.End.Length)];
        var expected = MatrixDocument.Render(_matrix.Cells);

        if (Environment.GetEnvironmentVariable("CODEBRIDGE_UPDATE_MATRIX") == "1")
        {
            var crlf = File.ReadAllText(path).Contains("\r\n");
            var updated = document.Replace(current, expected);
            File.WriteAllText(path, crlf ? updated.Replace("\n", "\r\n") : updated);
            return;
        }

        Assert.True(current == expected, "docs/block-board-matrix.md is out of date: run these tests again with CODEBRIDGE_UPDATE_MATRIX=1 and commit the file.");
    }

    internal static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
