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

    /// <summary>What the board is connected to, ready to show in a status bar: "ESP32 DevKit on COM5", "ESP32 DevKit (simulator)". Empty when not connected.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string ConnectionDescription
    {
        get
        {
            if (!IsConnected)
                return string.Empty;
            if (_simulated)
                return BoardName + " (simulator)";
            return TransportMode == CodeBridgeTransportMode.WiFi ? $"{BoardName} at {Host}" : $"{BoardName} on {_connectedTo}";
        }
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
        return board;
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
