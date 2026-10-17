using System.ComponentModel;
using System.Drawing;
using CodeBridge.Core.Abstractions;
using CodeBridge.Designer.WinForms.Hardware;
using CodeBridge.Flow;
using CodeBridge.Flow.Execution;
using CodeBridge.Transport;

namespace CodeBridge.Designer.WinForms;

[ToolboxItem(true)]
[ToolboxBitmap(typeof(CodeBridgeBoardComponent), "CodeBridge.Designer.WinForms.Resources.CodeBridgeEsp32Component.bmp")]
[Designer(typeof(CodeBridgeBoardComponentDesigner))]
[DesignerCategory("Component")]
[DisplayName("CodeBridge Board")]
[Description("Owns CodeBridge hardware onboarding, firmware upload, and board connectivity for WinForms flows. With the default PortName \"auto\" it finds the board on USB by itself.")]
[DefaultProperty(nameof(BoardId))]
[DefaultEvent(nameof(Connected))]
public sealed class CodeBridgeBoardComponent : Component, IAsyncDisposable
{
    private IBoard? _board;
    private string _boardId = BuiltInBoardProfiles.Default.Id;
    private bool _autoDetectBoard = true;
    private string _connectedTo = string.Empty;
    private string? _detectedChip;
    private BoardProfile? _detectedBoard;
    private string? _lastAttemptedPort;
    private bool _simulated;

    /// <summary>How the ports that look like a board are found. Replaced by the tests.</summary>
    internal Func<IReadOnlyList<PortInfo>> DiscoverBoards = BoardDiscovery.FindBoardCandidates;

    /// <summary>How a connection is made. Replaced by the tests.</summary>
    internal Func<BoardConnectionRequest, CancellationToken, Task<IBoard>> Connector = BoardConnectionService.ConnectAsync;

    public event EventHandler? Connected;
    public event EventHandler? Disconnected;
    public event EventHandler<Exception>? ConnectionFailed;
    public event EventHandler<FirmwareUploadResult>? FirmwareUploaded;
    public event EventHandler<FirmwareUploadResult>? FirmwareUploadFailed;

    [Category("CodeBridge")]
    [DefaultValue("esp32-devkit")]
    [TypeConverter(typeof(BoardProfileIdTypeConverter))]
    [Description("Target board profile used for pin filtering, validation, firmware upload, and connection behavior.")]
    public string BoardId
    {
        get => _boardId;
        set => _boardId = BuiltInBoardProfiles.FindById(value)?.Id ?? BuiltInBoardProfiles.Default.Id;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string BoardName => CurrentBoard.DisplayName;

    [Category("CodeBridge")]
    [DefaultValue(true)]
    [Description("Automatically switches the board profile when the selected serial port can be recognized as ESP32 or Arduino.")]
    public bool AutoDetectBoard
    {
        get => _autoDetectBoard;
        set => _autoDetectBoard = value;
    }

    [Category("CodeBridge")]
    [DefaultValue(CodeBridgeTransportMode.Serial)]
    public CodeBridgeTransportMode TransportMode { get; set; } = CodeBridgeTransportMode.Serial;

    [Category("CodeBridge")]
    [DefaultValue(AutoPort)]
    [Description("Serial port used when TransportMode is Serial, for example COM3. \"auto\" uses the board found on USB.")]
    public string PortName { get; set; } = AutoPort;

    [Category("CodeBridge")]
    [DefaultValue(false)]
    [Description("When PortName is \"auto\" and no board is found on USB, run on the virtual board instead of failing. Handy for a first run without hardware.")]
    public bool FallbackToSimulator { get; set; }

    [Category("CodeBridge")]
    [DefaultValue(115200)]
    public int BaudRate { get; set; } = 115200;

    [Category("CodeBridge")]
    [DefaultValue("192.168.1.100")]
    [Description("ESP32 host used when TransportMode is WiFi.")]
    public string Host { get; set; } = "192.168.1.100";

    [Category("CodeBridge")]
    [DefaultValue(8080)]
    [Description("ESP32 TCP port used when TransportMode is WiFi.")]
    public int TcpPort { get; set; } = 8080;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IBoard? Board => _board;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public BoardProfile CurrentBoard => BuiltInBoardProfiles.FindById(BoardId) ?? BuiltInBoardProfiles.Default;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsConnected => _board is { IsConnected: true };

    /// <summary>True when the connected board is the virtual one.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsSimulated => IsConnected && _simulated;

    /// <summary>The chip the connected board reports about itself ("ESP32-S3"), or null when it does not say (the Arduino Uno sketch has no INFO).</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? DetectedChip => IsConnected ? _detectedChip : null;

    /// <summary>The board profile that matches the chip the connected board reports, or null when there is none to tell. <see cref="BoardId"/> is what the app was made for; this is what is plugged in.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public BoardProfile? DetectedBoard => IsConnected ? _detectedBoard : null;

    /// <summary>What the board is connected to, ready to show in a status bar: "ESP32-S3 DevKit on COM5", "ESP32 DevKit (simulator)". Empty when not connected.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string ConnectionDescription
    {
        get
        {
            if (!IsConnected)
                return string.Empty;

            var name = _detectedBoard?.DisplayName ?? BoardName;
            if (_simulated)
                return name + " (simulator)";
            return TransportMode == CodeBridgeTransportMode.WiFi ? $"{name} at {Host}" : $"{name} on {_connectedTo}";
        }
    }

    /// <summary>
    /// A sentence for the person when the connected board cannot take the flow that was made for <paramref name="flowBoardId"/> (an ESP32-S3 where the flow says ESP32 DevKit,
    /// an Arduino for an ESP32 flow), or null when it can, or when the board does not say what it is.
    /// </summary>
    public string? DescribeMismatch(string? flowBoardId)
    {
        if (!IsConnected || _detectedBoard is null || BuiltInBoardProfiles.FindById(flowBoardId) is not { } flowBoard)
            return null;

        if (BoardIdentification.IsCompatible(flowBoard, _detectedBoard))
            return null;

        return $"The flow is made for {flowBoard.DisplayName} but the connected board is {_detectedBoard.DisplayName} ({_detectedChip}). Open the flow and pick the board you have before you trust its pins.";
    }

    /// <summary>The value of <see cref="PortName"/> that picks the board found on USB.</summary>
    public const string AutoPort = "auto";

    public void DetectFirstSerialPort()
    {
        var port = DiscoverBoardsSafely().FirstOrDefault();
        if (port is null)
            return;

        TransportMode = CodeBridgeTransportMode.Serial;
        PortName = port.Name;

        var suggestedBoard = BoardPortDetector.SuggestBoardForPort(port.Name);
        if (suggestedBoard is not null)
            BoardId = suggestedBoard.Id;
    }

    public async Task<IBoard> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_board is { IsConnected: true })
            return _board;

        try
        {
            _board = TransportMode == CodeBridgeTransportMode.Serial && IsAuto(PortName)
                ? await ConnectAutomaticallyAsync(cancellationToken)
                : await ConnectToAsync(TransportMode, PortName, cancellationToken);

            Connected?.Invoke(this, EventArgs.Empty);
            return _board;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failure = ex as BoardConnectionException ?? BoardConnectionException.From(ex, ResolveBoard(PortName), _lastAttemptedPort ?? PortName);
            ConnectionFailed?.Invoke(this, failure);
            throw failure;
        }
    }

    /// <summary>Tries the ports that look like a board, best first. With none, runs on the virtual board when <see cref="FallbackToSimulator"/> is on.</summary>
    private async Task<IBoard> ConnectAutomaticallyAsync(CancellationToken cancellationToken)
    {
        var candidates = DiscoverBoardsSafely();
        if (candidates.Count == 0)
        {
            if (!FallbackToSimulator)
            {
                throw new BoardConnectionException(
                    "No CodeBridge board was found on USB. Connect the board, choose its port, or turn on FallbackToSimulator to try the flow without hardware.");
            }

            return await ConnectToAsync(CodeBridgeTransportMode.Simulator, string.Empty, cancellationToken);
        }

        BoardConnectionException? lastFailure = null;
        foreach (var candidate in candidates)
        {
            try
            {
                return await ConnectToAsync(CodeBridgeTransportMode.Serial, candidate.Name, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastFailure = BoardConnectionException.From(ex, ResolveBoard(candidate.Name), candidate.Name);
            }
        }

        throw lastFailure!;
    }

    private async Task<IBoard> ConnectToAsync(CodeBridgeTransportMode mode, string portName, CancellationToken cancellationToken)
    {
        _lastAttemptedPort = portName;
        var board = await Connector(
            new BoardConnectionRequest(ResolveBoard(portName), mode, portName, BaudRate, Host, TcpPort),
            cancellationToken);

        _simulated = mode == CodeBridgeTransportMode.Simulator;
        _connectedTo = portName;
        await IdentifyAsync(board, cancellationToken);
        return board;
    }

    /// <summary>Asks the board what chip it is. An answer is exact; no answer (a firmware without INFO) just leaves the profile the app was made for.</summary>
    private async Task IdentifyAsync(IBoard board, CancellationToken cancellationToken)
    {
        _detectedChip = null;
        _detectedBoard = null;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(2));
            var info = await board.GetInfoAsync(limit.Token);
            _detectedChip = info.ChipModel;
            _detectedBoard = BoardIdentification.FromChip(info.ChipModel);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The Arduino Uno sketch has no INFO, and an old firmware may not answer: the board is what the app says it is.
        }
    }

    private IReadOnlyList<PortInfo> DiscoverBoardsSafely()
    {
        try
        {
            return DiscoverBoards();
        }
        catch (Exception)
        {
            return Array.Empty<PortInfo>(); // the ports cannot be listed on this system
        }
    }

    private static bool IsAuto(string? portName) =>
        string.Equals(portName?.Trim(), AutoPort, StringComparison.OrdinalIgnoreCase);

    public async Task DisconnectAsync()
    {
        if (_board is null)
            return;

        await BoardConnectionService.DisposeBoardAsync(_board);
        _board = null;
        _connectedTo = string.Empty;
        _simulated = false;
        _detectedChip = null;
        _detectedBoard = null;
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    public async Task<FirmwareUploadResult> UploadFirmwareAsync(CancellationToken cancellationToken = default)
    {
        var portName = ResolveUploadPort();
        var uploader = FirmwareUploaderFactory.Create(new FirmwareUploadRequest(ResolveBoard(portName), portName));
        var result = await uploader.UploadAsync(cancellationToken);

        if (result.Success)
            FirmwareUploaded?.Invoke(this, result);
        else
            FirmwareUploadFailed?.Invoke(this, result);

        return result;
    }

    public async Task<FlowExecutionResult> RunAsync(
        CodeBridgeFlowControl flow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var board = await ConnectAsync(cancellationToken);
        return await flow.RunAsync(board, cancellationToken);
    }

    private BoardProfile ResolveBoard(string portName)
    {
        if (!AutoDetectBoard || TransportMode != CodeBridgeTransportMode.Serial || IsAuto(portName))
            return CurrentBoard;

        var suggestedBoard = BoardPortDetector.SuggestBoardForPort(portName);
        if (suggestedBoard is null)
            return CurrentBoard;

        BoardId = suggestedBoard.Id;
        return suggestedBoard;
    }

    /// <summary>The port a firmware upload goes to: the chosen one, or with "auto" the one the last connection tried, or else the best candidate.</summary>
    private string ResolveUploadPort()
    {
        if (!IsAuto(PortName))
            return PortName;

        var port = _lastAttemptedPort is { Length: > 0 } last && !IsAuto(last)
            ? last
            : DiscoverBoardsSafely().FirstOrDefault()?.Name;

        return port ?? throw new BoardConnectionException("No CodeBridge board was found on USB. Connect the board or choose its port before uploading the firmware.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _board?.Dispose();
            _board = null;
        }

        base.Dispose(disposing);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        GC.SuppressFinalize(this);
    }
}

public sealed class BoardProfileIdTypeConverter : StringConverter
{
    public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;

    public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => true;

    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context) =>
        new(BuiltInBoardProfiles.All.Select(board => board.Id).ToArray());
}
