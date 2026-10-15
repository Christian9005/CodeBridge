using CodeBridge.Core.Abstractions;
using CodeBridge.Core.Enums;
using CodeBridge.ESP32;
using CodeBridge.Flow;
using CodeBridge.Transport;
using CodeBridge.Transport.Serial;
using CodeBridge.Transport.Simulation;
using CodeBridge.Transport.Wifi;

namespace CodeBridge.Designer.WinForms.Hardware;

internal static class BoardConnectionService
{
    public static async Task<IBoard> ConnectAsync(
        BoardConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // When someone wants to watch the board (the editor's board view), every command is reported on its way through.
        ITransport Observe(ITransport transport) =>
            request.CommandObserver is null ? transport : new CommandTapTransport(transport, request.CommandObserver);

        if (request.Transport is not null)
            return await ConnectAsync(request.BoardProfile, Observe(request.Transport), string.Empty, cancellationToken);

        if (request.TransportMode == CodeBridgeTransportMode.Simulator)
            return await ConnectAsync(request.BoardProfile, Observe(new SimulatedTransport(SimulatorOptions(request.BoardProfile))), " (simulator)", cancellationToken);

        if (request.TransportMode == CodeBridgeTransportMode.Serial)
        {
            var portName = request.PortName.Trim();
            if (string.IsNullOrWhiteSpace(portName))
                throw new InvalidOperationException("Select a serial port before connecting.");

            return await ConnectAsync(request.BoardProfile, Observe(new SerialTransport(portName, request.BaudRate)), string.Empty, cancellationToken);
        }

        if (!request.BoardProfile.SupportsWifi)
            throw new InvalidOperationException($"{request.BoardProfile.DisplayName} supports CodeBridge over USB only. Pick its COM port, upload the firmware, then connect.");

        if (string.IsNullOrWhiteSpace(request.Host))
            throw new InvalidOperationException("Enter the ESP32 WiFi host before connecting.");

        return await ConnectAsync(request.BoardProfile, Observe(new WifiTransport(request.Host.Trim(), request.TcpPort, request.AccessToken)), string.Empty, cancellationToken);
    }

    public static string FormatConnectionFailure(Exception ex, BoardProfile boardProfile, string? portName)
    {
        if (ex is TimeoutException || ex.Message.Contains("CODEBRIDGE_READY", StringComparison.OrdinalIgnoreCase))
        {
            var port = string.IsNullOrWhiteSpace(portName) ? "the selected port" : portName;
            return string.Join(
                Environment.NewLine,
                $"{boardProfile.DisplayName} was found on {port}, but it is not running CodeBridge firmware yet.",
                "",
                "Use Upload FW to flash the matching firmware, then press Connect again.",
                "",
                $"Details: {ex.Message}");
        }

        return ex.Message;
    }

    public static async Task DisposeBoardAsync(IBoard board)
    {
        if (board is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else
            board.Dispose();
    }

    public static bool IsEsp32Profile(BoardProfile boardProfile) => boardProfile.Family == BoardFamily.ESP32;

    /// <summary>The virtual board takes its pins and ADC range from the selected profile.</summary>
    private static SimulatedBoardOptions SimulatorOptions(BoardProfile profile) => new()
    {
        AnalogMax = profile.AnalogMaxValue,
        MaxPin = profile.MaxPin,
        ChipName = IsEsp32Profile(profile) ? profile.FlashChip?.ToUpperInvariant() + "-SIM" : "ATmega-SIM"
    };

    /// <summary>ESP32 chips and AVR Arduinos speak the same protocol; only the board class and the pin range differ.</summary>
    private static async Task<IBoard> ConnectAsync(BoardProfile profile, ITransport transport, string nameSuffix, CancellationToken cancellationToken)
    {
        IBoard board = IsEsp32Profile(profile)
            ? new ESP32Board(transport, profile.MaxPin)
            : new CodeBridgeProtocolBoard(transport, profile.DisplayName + nameSuffix, profile.Family, profile.MaxPin);

        try
        {
            await board.ConnectAsync(cancellationToken);
            return board;
        }
        catch
        {
            await DisposeBoardAsync(board);
            throw;
        }
    }
}
