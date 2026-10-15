using CodeBridge.PlatformIO;

namespace CodeBridge.Core.Tests.PlatformIO;

public class PlatformIoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codebridge-pio-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public PlatformIoTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------- projects

    [Theory]
    [InlineData("esp32-devkit", "esp32dev", "espressif32")]
    [InlineData("esp32-s3-devkit", "esp32-s3-devkitc-1", "espressif32")]
    [InlineData("esp32-c3-devkit", "esp32-c3-devkitm-1", "espressif32")]
    [InlineData("xiao-esp32-s3", "seeed_xiao_esp32s3", "espressif32")]
    [InlineData("esp32-s2-mini", "lolin_s2_mini", "espressif32")]
    [InlineData("arduino-uno", "uno", "atmelavr")]
    [InlineData("arduino-nano", "nanoatmega328new", "atmelavr")]
    [InlineData("arduino-nano-old", "nanoatmega328", "atmelavr")]
    [InlineData("arduino-mega", "megaatmega2560", "atmelavr")]
    public void A_project_is_created_for_every_board(string boardId, string pioBoard, string platform)
    {
        var directory = Path.Combine(_root, boardId);

        var files = PlatformIoProject(directory, boardId);

        Assert.Equal(new[] { ".gitignore", "README.md", "platformio.ini", "src/main.cpp" }, files.OrderBy(f => f, StringComparer.Ordinal).ToArray());
        Assert.True(PioProject.IsProject(directory));
        Assert.Equal(new[] { pioBoard }, PioProject.Environments(directory));
        var ini = File.ReadAllText(Path.Combine(directory, "platformio.ini"));
        Assert.Contains($"platform = {platform}", ini);
        Assert.Contains($"board = {pioBoard}", ini);
        Assert.Contains("framework = arduino", ini);
    }

    private static IReadOnlyList<string> PlatformIoProject(string directory, string boardId, FirmwareTemplate template = FirmwareTemplate.Blink) =>
        PioProject.Create(directory, boardId, template);

    [Fact]
    public void The_templates_differ_and_the_plot_template_uses_the_boards_analog_pin()
    {
        PlatformIoProject(Path.Combine(_root, "a"), "arduino-uno", FirmwareTemplate.SerialPlot);
        PlatformIoProject(Path.Combine(_root, "b"), "esp32-devkit", FirmwareTemplate.SerialPlot);
        PlatformIoProject(Path.Combine(_root, "c"), "esp32-devkit", FirmwareTemplate.Empty);

        Assert.Contains("ANALOG_PIN = A0", File.ReadAllText(Path.Combine(_root, "a", "src", "main.cpp")));
        Assert.Contains("ANALOG_PIN = 34", File.ReadAllText(Path.Combine(_root, "b", "src", "main.cpp")));
        Assert.DoesNotContain("analogRead", File.ReadAllText(Path.Combine(_root, "c", "src", "main.cpp")));
    }

    [Fact]
    public void An_existing_project_or_a_folder_with_files_is_never_overwritten()
    {
        var directory = Path.Combine(_root, "p");
        PlatformIoProject(directory, "arduino-uno");
        Assert.Throws<InvalidOperationException>(() => PlatformIoProject(directory, "arduino-uno"));

        var busy = Path.Combine(_root, "busy");
        Directory.CreateDirectory(busy);
        File.WriteAllText(Path.Combine(busy, "mine.txt"), "important");
        var error = Assert.Throws<InvalidOperationException>(() => PlatformIoProject(busy, "arduino-uno"));
        Assert.Contains("not empty", error.Message);
        Assert.Equal("important", File.ReadAllText(Path.Combine(busy, "mine.txt")));

        Assert.Throws<ArgumentException>(() => PlatformIoProject(Path.Combine(_root, "x"), "esp8266"));
    }

    [Fact]
    public void The_project_root_is_found_from_a_file_deep_inside()
    {
        var directory = Path.Combine(_root, "deep");
        PlatformIoProject(directory, "arduino-uno");
        Directory.CreateDirectory(Path.Combine(directory, "lib", "util"));
        var file = Path.Combine(directory, "lib", "util", "x.h");
        File.WriteAllText(file, "//");

        Assert.Equal(directory, PioProject.FindRoot(file));
        Assert.Equal(directory, PioProject.FindRoot(Path.Combine(directory, "src")));
        Assert.Null(PioProject.FindRoot(_root));
        Assert.Null(PioProject.FindRoot(null));
    }

    // ---------------------------------------------------------------- diagnostics

    [Fact]
    public void Compiler_output_becomes_diagnostics_with_file_line_and_column()
    {
        var error = PioRunner.ParseDiagnostic("src/main.cpp:12:5: error: 'foo' was not declared in this scope", @"C:\work\proj");
        Assert.NotNull(error);
        Assert.Equal("error", error!.Severity);
        Assert.Equal(12, error.Line);
        Assert.Equal(5, error.Column);
        Assert.Equal("'foo' was not declared in this scope", error.Message);
        Assert.EndsWith(Path.Combine("proj", "src", "main.cpp"), error.File);

        var windows = PioRunner.ParseDiagnostic(@"C:\work\proj\src\main.cpp:7:1: warning: unused variable 'x' [-Wunused-variable]");
        Assert.Equal("warning", windows!.Severity);
        Assert.Equal(@"C:\work\proj\src\main.cpp", windows.File);

        Assert.Equal("error", PioRunner.ParseDiagnostic("src/a.cpp:3:10: fatal error: Missing.h: No such file or directory")!.Severity);
        Assert.Equal("error", PioRunner.ParseDiagnostic("Error: Could not find the package with 'x' requirements")!.Severity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Compiling .pio\\build\\uno\\src\\main.cpp.o")]
    [InlineData("RAM:   [===       ]  30.2% (used 618 bytes from 2048 bytes)")]
    [InlineData("========================= [SUCCESS] Took 1.97 seconds =========================")]
    public void Ordinary_output_is_not_a_diagnostic(string line) => Assert.Null(PioRunner.ParseDiagnostic(line));

    // ---------------------------------------------------------------- real builds (need PlatformIO; skipped when it is not installed)

    private static PioTool? Tool => PioLocator.Find();

    [Fact]
    public async Task A_blink_project_builds_for_the_ESP32_and_for_the_Uno()
    {
        if (Tool is not { } tool)
            return; // PlatformIO is not installed on this machine

        foreach (var board in new[] { "arduino-uno", "esp32-devkit" })
        {
            var directory = Path.Combine(_root, "build-" + board);
            PlatformIoProject(directory, board);

            var lines = new List<string>();
            var result = await PioRunner.BuildAsync(tool, directory, onLine: lines.Add);

            Assert.True(result.Success, $"{board} did not build:{Environment.NewLine}{string.Join(Environment.NewLine, lines.TakeLast(25))}");
            Assert.Equal(0, result.Errors);
            Assert.Contains(lines, l => l.Contains("SUCCESS"));
        }
    }

    [Fact]
    public async Task A_mistake_in_the_code_is_reported_with_its_file_and_line()
    {
        if (Tool is not { } tool)
            return;

        var directory = Path.Combine(_root, "broken");
        PlatformIoProject(directory, "arduino-uno", FirmwareTemplate.Empty);
        File.WriteAllText(Path.Combine(directory, "src", "main.cpp"), "#include <Arduino.h>\nvoid setup() { undefinedFunction(); }\nvoid loop() {}\n");

        var result = await PioRunner.BuildAsync(tool, directory);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Severity == "error" && d.Message.Contains("undefinedFunction"));
        Assert.Equal(2, diagnostic.Line);
        Assert.Equal(Path.Combine(directory, "src", "main.cpp"), diagnostic.File, ignoreCase: true);
    }

    [Fact]
    public async Task Running_in_a_folder_that_is_not_a_project_fails_clearly_and_a_build_can_be_cancelled()
    {
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);
        var fake = new PioTool("pio", Array.Empty<string>(), "0");
        await Assert.ThrowsAsync<InvalidOperationException>(() => PioRunner.BuildAsync(fake, plain));

        if (Tool is not { } tool)
            return;

        var directory = Path.Combine(_root, "cancel");
        PlatformIoProject(directory, "esp32-devkit");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PioRunner.BuildAsync(tool, directory, ct: cts.Token));
    }
}
