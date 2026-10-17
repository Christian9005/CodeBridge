using CodeBridge.Core.Abstractions;
using CodeBridge.Designer.WinForms;
using CodeBridge.Designer.WinForms.Hardware;
using CodeBridge.Flow;
using CodeBridge.Transport;

namespace CodeBridge.Designer.WinForms.Tests;

public class CodeBridgeBoardComponentTests
{
    // Port names no real machine has, so the registry lookup of the board type never matches a device of the person running the tests.
    private const string FirstPort = "COM250";
    private const string SecondPort = "COM251";

    private static PortInfo Port(string name) => new(name) { BoardHint = "Silicon Labs CP210x (ESP32 DevKit)" };

    /// <summary>Connects to the virtual board whatever the request says, and records which ports were tried.</summary>
    private static Func<BoardConnectionRequest, CancellationToken, Task<IBoard>> Simulating(List<string> tried, params string[] failing) =>
        (request, token) =>
        {
            tried.Add(request.PortName);
            if (failing.Contains(request.PortName))
                throw new TimeoutException("CodeBridge firmware did not send CODEBRIDGE_READY within 8 seconds.");

            return BoardConnectionService.ConnectAsync(request with { TransportMode = CodeBridgeTransportMode.Simulator }, token);
        };

    [Fact]
    public void ANewComponentFindsTheBoardByItself()
    {
        using var component = new CodeBridgeBoardComponent();

        Assert.Equal("auto", component.PortName);
        Assert.False(component.FallbackToSimulator);
    }

    [Fact]
    public async Task WithoutABoardAndWithoutFallback_ExplainsWhatToDo()
    {
        using var component = new CodeBridgeBoardComponent { DiscoverBoards = () => Array.Empty<PortInfo>() };
        Exception? raised = null;
        component.ConnectionFailed += (_, ex) => raised = ex;

        var failure = await Assert.ThrowsAsync<BoardConnectionException>(() => component.ConnectAsync());

        Assert.Contains("No CodeBridge board was found on USB", failure.Message);
        Assert.False(failure.FirmwareMissing);
        Assert.Same(failure, raised);
        Assert.False(component.IsConnected);
    }

    [Fact]
    public async Task WithoutABoardButWithFallback_RunsOnTheVirtualBoard()
    {
        using var component = new CodeBridgeBoardComponent { DiscoverBoards = () => Array.Empty<PortInfo>(), FallbackToSimulator = true };

        await component.ConnectAsync();

        Assert.True(component.IsConnected);
        Assert.True(component.IsSimulated);
        Assert.Equal("ESP32 DevKit (simulator)", component.ConnectionDescription);
    }

    [Fact]
    public async Task ConnectsToTheFirstPortThatAnswers()
    {
        var tried = new List<string>();
        using var component = new CodeBridgeBoardComponent
        {
            DiscoverBoards = () => new[] { Port(FirstPort), Port(SecondPort) },
            Connector = Simulating(tried, failing: FirstPort)
        };

        await component.ConnectAsync();

        Assert.Equal(new[] { FirstPort, SecondPort }, tried);
        Assert.False(component.IsSimulated);
        Assert.Equal($"ESP32 DevKit on {SecondPort}", component.ConnectionDescription);
    }

    [Fact]
    public async Task ABoardWithoutTheFirmware_IsReportedSoTheAppCanOfferToUploadIt()
    {
        var tried = new List<string>();
        using var component = new CodeBridgeBoardComponent
        {
            DiscoverBoards = () => new[] { Port(FirstPort) },
            Connector = Simulating(tried, failing: FirstPort),
            FallbackToSimulator = true // a board that is there must never be replaced by the virtual one
        };

        var failure = await Assert.ThrowsAsync<BoardConnectionException>(() => component.ConnectAsync());

        Assert.True(failure.FirmwareMissing);
        Assert.Equal(FirstPort, failure.PortName);
        Assert.Contains("not running CodeBridge firmware", failure.Message);
        Assert.Contains(FirstPort, failure.Message);
        Assert.IsType<TimeoutException>(failure.InnerException);
        Assert.False(component.IsSimulated);
    }

    [Fact]
    public async Task AChosenPortIsUsedAsIs_AndItsFailureIsExplainedToo()
    {
        var tried = new List<string>();
        using var component = new CodeBridgeBoardComponent
        {
            PortName = SecondPort,
            DiscoverBoards = () => throw new InvalidOperationException("must not be asked"),
            Connector = Simulating(tried, failing: SecondPort)
        };

        var failure = await Assert.ThrowsAsync<BoardConnectionException>(() => component.ConnectAsync());

        Assert.Equal(new[] { SecondPort }, tried);
        Assert.True(failure.FirmwareMissing);
    }

    [Fact]
    public async Task AnyOtherFailureKeepsItsOwnMessage()
    {
        using var component = new CodeBridgeBoardComponent
        {
            PortName = SecondPort,
            Connector = (_, _) => throw new UnauthorizedAccessException("Access to the port 'COM251' is denied.")
        };

        var failure = await Assert.ThrowsAsync<BoardConnectionException>(() => component.ConnectAsync());

        Assert.Equal("Access to the port 'COM251' is denied.", failure.Message);
        Assert.False(failure.FirmwareMissing);
    }

    [Fact]
    public async Task ThePortsCannotBeListed_IsTheSameAsNoBoard()
    {
        using var component = new CodeBridgeBoardComponent
        {
            DiscoverBoards = () => throw new PlatformNotSupportedException(),
            FallbackToSimulator = true
        };

        await component.ConnectAsync();

        Assert.True(component.IsSimulated);
    }

    /// <summary>Answers as the given board whatever the app was made for, as the hardware would.</summary>
    private static Func<BoardConnectionRequest, CancellationToken, Task<IBoard>> PluggedIn(BoardProfile hardware) =>
        (request, token) => BoardConnectionService.ConnectAsync(
            request with { BoardProfile = hardware, TransportMode = CodeBridgeTransportMode.Simulator },
            token);

    [Fact]
    public async Task AfterConnecting_TheBoardIsAskedWhatItIs()
    {
        using var component = new CodeBridgeBoardComponent
        {
            DiscoverBoards = () => new[] { Port(FirstPort) },
            Connector = PluggedIn(BuiltInBoardProfiles.Esp32S3DevKit)
        };

        await component.ConnectAsync();

        Assert.Equal(BuiltInBoardProfiles.Esp32S3DevKit.Id, component.DetectedBoard?.Id);
        Assert.Contains("S3", component.DetectedChip);
        Assert.Equal("esp32-devkit", component.BoardId); // what the app was made for does not change by itself
        Assert.StartsWith(BuiltInBoardProfiles.Esp32S3DevKit.DisplayName, component.ConnectionDescription);
        Assert.Equal($"{BuiltInBoardProfiles.Esp32S3DevKit.DisplayName} on {FirstPort}", component.ConnectionDescription);
    }

    [Fact]
    public async Task AFlowMadeForAnotherChip_IsToldAboutTheDifference()
    {
        using var component = new CodeBridgeBoardComponent
        {
            DiscoverBoards = () => new[] { Port(FirstPort) },
            Connector = PluggedIn(BuiltInBoardProfiles.Esp32S3DevKit)
        };
        await component.ConnectAsync();

        var message = component.DescribeMismatch("esp32-devkit");

        Assert.NotNull(message);
        Assert.Contains(BuiltInBoardProfiles.Esp32DevKit.DisplayName, message);
        Assert.Contains(BuiltInBoardProfiles.Esp32S3DevKit.DisplayName, message);
        Assert.Null(component.DescribeMismatch("esp32-s3-devkit"));
        Assert.Null(component.DescribeMismatch("xiao-esp32-s3")); // another shape of the same chip
        Assert.NotNull(component.DescribeMismatch("arduino-uno"));
        Assert.Null(component.DescribeMismatch("no-such-board"));
        Assert.Null(component.DescribeMismatch(null));
    }

    [Fact]
    public async Task NothingIsKnownAboutTheBoardBeforeAndAfterTheConnection()
    {
        using var component = new CodeBridgeBoardComponent { DiscoverBoards = () => Array.Empty<PortInfo>(), FallbackToSimulator = true };

        Assert.Null(component.DetectedBoard);
        Assert.Null(component.DetectedChip);
        Assert.Null(component.DescribeMismatch("esp32-devkit"));

        await component.ConnectAsync();
        Assert.Equal("esp32-devkit", component.DetectedBoard?.Id);

        await component.DisconnectAsync();
        Assert.Null(component.DetectedBoard);
        Assert.Null(component.DetectedChip);
    }

    [Fact]
    public async Task Disconnecting_ClearsTheDescription()
    {
        using var component = new CodeBridgeBoardComponent { DiscoverBoards = () => Array.Empty<PortInfo>(), FallbackToSimulator = true };
        await component.ConnectAsync();

        await component.DisconnectAsync();

        Assert.False(component.IsConnected);
        Assert.False(component.IsSimulated);
        Assert.Equal(string.Empty, component.ConnectionDescription);
    }
}
