#pragma warning disable CS0618 // the component is obsolete for people; the library still has to name it
using System.ComponentModel;
using System.Drawing;
using CodeBridge.Core;
using CodeBridge.ESP32;

namespace CodeBridge.Designer.WinForms;

public enum CodeBridgeTransportMode
{
    Serial,
    WiFi,
    Simulator
}

[Obsolete("Use CodeBridgeBoardComponent: it finds the board on USB by itself, runs on the simulator when none is plugged in, and also works with Arduino boards.")]
[EditorBrowsable(EditorBrowsableState.Never)]
[ToolboxItem(false)]
[ToolboxBitmap(typeof(CodeBridgeEsp32Component), "CodeBridge.Designer.WinForms.Resources.CodeBridgeEsp32Component.bmp")]
[Designer(typeof(CodeBridgeEsp32ComponentDesigner))]
[DesignerCategory("Component")]
[DisplayName("CodeBridge ESP32")]
[Description("Owns Serial or WiFi ESP32 connectivity for CodeBridge WinForms flows.")]
[DefaultProperty(nameof(PortName))]
public sealed class CodeBridgeEsp32Component : Component, IAsyncDisposable
{
    private ESP32Board? _board;

    public event EventHandler? Connected;
    public event EventHandler? Disconnected;
    public event EventHandler<Exception>? ConnectionFailed;

    [Category("CodeBridge")]
    [DefaultValue(CodeBridgeTransportMode.Serial)]
    public CodeBridgeTransportMode TransportMode { get; set; } = CodeBridgeTransportMode.Serial;

    [Category("CodeBridge")]
    [DefaultValue("")]
    [Description("Serial port used when TransportMode is Serial, for example COM3.")]
    public string PortName { get; set; } = string.Empty;

    [Category("CodeBridge")]
    [DefaultValue(115200)]
    public int BaudRate { get; set; } = 115200;

    [Category("CodeBridge")]
    [DefaultValue("192.168.1.100")]
    public string Host { get; set; } = "192.168.1.100";

    [Category("CodeBridge")]
    [DefaultValue(8080)]
    public int TcpPort { get; set; } = 8080;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ESP32Board? Board => _board;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsConnected => _board is { IsConnected: true };

    public async Task<ESP32Board> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_board is { IsConnected: true })
            return _board;

        try
        {
            _board = TransportMode == CodeBridgeTransportMode.Serial
                ? await ConnectSerialAsync(cancellationToken)
                : await ConnectWifiAsync(cancellationToken);

            Connected?.Invoke(this, EventArgs.Empty);
            return _board;
        }
        catch (Exception ex)
        {
            ConnectionFailed?.Invoke(this, ex);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_board is null)
            return;

        await _board.DisposeAsync();
        _board = null;
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    public async Task<Flow.Execution.FlowExecutionResult> RunAsync(
        CodeBridgeFlowControl flow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var board = await ConnectAsync(cancellationToken);
        return await flow.RunAsync(board, cancellationToken);
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

    private Task<ESP32Board> ConnectSerialAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(PortName))
            throw new InvalidOperationException("Set PortName before connecting to ESP32.");

        return CodeBridgeBuilder
            .Connect()
            .Serial(PortName, BaudRate)
            .ToESP32()
            .BuildAsync(cancellationToken);
    }

    private Task<ESP32Board> ConnectWifiAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Host))
            throw new InvalidOperationException("Set Host before connecting to ESP32 over WiFi.");

        return CodeBridgeBuilder
            .Connect()
            .WiFi(Host, TcpPort)
            .ToESP32()
            .BuildAsync(cancellationToken);
    }
}
