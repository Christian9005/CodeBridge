using System.IO;
using System.Threading;
using CodeBridge.Flow;
using CodeBridge.VisualStudio.Editor;
using CodeBridge.VisualStudio.Firmware;

namespace CodeBridge.VisualStudio.Tests;

public sealed class FirmwareModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codebridge-fw-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));

    public FirmwareModeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Project(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(directory, "src"));
        File.WriteAllText(Path.Combine(directory, "platformio.ini"), "[env:uno]\nplatform = atmelavr\n");
        File.WriteAllText(Path.Combine(directory, "src", "main.cpp"), "void setup(){}void loop(){}");
        return directory;
    }

    [Fact]
    public void The_project_is_found_from_the_open_file_the_selection_or_the_solution_folder()
    {
        var project = Project("blinker");
        var file = Path.Combine(project, "src", "main.cpp");

        Assert.Equal(project, FirmwareProjects.FindRootAbove(file));
        Assert.Equal(project, FirmwareProjects.Locate(new string?[] { null, file }, null));

        // the solution folder contains the project one level down
        Assert.Equal(project, FirmwareProjects.Locate(new string?[] { @"C:\nowhere\file.cs" }, _root));

        // an unrelated C# file does not hide the firmware project of the solution
        var other = Path.Combine(_root, "App", "Program.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllText(other, "");
        Assert.Equal(project, FirmwareProjects.Locate(new string?[] { other }, _root));
    }

    [Fact]
    public void Nothing_is_found_where_there_is_no_project_and_odd_paths_do_not_throw()
    {
        Assert.Null(FirmwareProjects.Locate(new string?[] { null, "", "   ", "not\0valid" }, null));
        Assert.Null(FirmwareProjects.FindRootBelow(Path.Combine(_root, "missing")));
        Assert.Null(FirmwareProjects.FindRootAbove(null));
    }

    [Fact]
    public void The_search_below_a_folder_is_shallow_and_skips_hidden_folders()
    {
        var hidden = Path.Combine(_root, ".git", "x");
        Directory.CreateDirectory(hidden);
        File.WriteAllText(Path.Combine(hidden, "platformio.ini"), "");
        var deep = Path.Combine(_root, "a", "b", "c", "d");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "platformio.ini"), "");

        Assert.Null(FirmwareProjects.FindRootBelow(_root));
    }

    [Fact]
    public void The_new_project_window_offers_every_board_the_editor_knows()
    {
        foreach (var board in NewFirmwareProjectWindow.Boards)
        {
            var catalog = FlowCatalog.ForBoard(board.Id);
            Assert.Equal(board.Id, catalog.BoardId);
        }

        Assert.Contains(NewFirmwareProjectWindow.Templates, t => t.Id == "bridge");
        Assert.Equal(7, NewFirmwareProjectWindow.Boards.Length);
    }

    [Fact]
    public void The_new_project_window_builds_and_computes_the_folder()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new NewFirmwareProjectWindow();

                Assert.Equal("esp32-devkit", window.BoardId);
                Assert.Equal("blink", window.Template);
                Assert.EndsWith(Path.Combine("CodeBridge Firmware", "MyFirmware"), window.Directory);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
            throw new InvalidOperationException(failure.ToString());
    }
}
