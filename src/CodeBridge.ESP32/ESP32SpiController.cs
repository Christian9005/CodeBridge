using CodeBridge.Core.Abstractions;
using CodeBridge.Core.Protocol;

namespace CodeBridge.ESP32;

/// <summary>
/// ESP32 SPI controller implementation.
/// Sends SPI commands to the firmware which handles the actual bus communication.
/// Default ESP32 SPI pins: MOSI=23, MISO=19, SCK=18.
/// </summary>
public class ESP32SpiController : ISpiController
{
    private readonly ITransport _transport;
    private bool _disposed;

    public ESP32SpiController(ITransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public async Task<byte[]> TransferAsync(int csPin, byte[] data, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (data == null || data.Length == 0)
            throw new ArgumentException("Data cannot be empty.", nameof(data));

        var hexData = Convert.ToHexString(data);
        var response = await _transport.SendCommandAsync(
            BridgeProtocol.BuildCommand(BridgeProtocol.CMD_SPI_TRANSFER, csPin, hexData), ct);

        var (success, result) = BridgeProtocol.ParseResponse(response);
        if (!success)
            throw new InvalidOperationException($"SPI transfer failed: {result}");

        return Convert.FromHexString(result);
    }

    public async Task WriteAsync(int csPin, byte[] data, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (data == null || data.Length == 0)
            throw new ArgumentException("Data cannot be empty.", nameof(data));

        var hexData = Convert.ToHexString(data);
        var response = await _transport.SendCommandAsync(
            BridgeProtocol.BuildCommand(BridgeProtocol.CMD_SPI_WRITE, csPin, hexData), ct);

        var (success, error) = BridgeProtocol.ParseResponse(response);
        if (!success)
            throw new InvalidOperationException($"SPI write failed: {error}");
    }

    public async Task<byte[]> ReadAsync(int csPin, int length, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length), "Length must be positive.");

        var response = await _transport.SendCommandAsync(
            BridgeProtocol.BuildCommand(BridgeProtocol.CMD_SPI_READ, csPin, length), ct);

        var (success, result) = BridgeProtocol.ParseResponse(response);
        if (!success)
            throw new InvalidOperationException($"SPI read failed: {result}");

        return Convert.FromHexString(result);
    }

    public async Task SetClockSpeedAsync(int speedHz, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var response = await _transport.SendCommandAsync(
            BridgeProtocol.BuildCommand(BridgeProtocol.CMD_SPI_CONFIG, speedHz, -1), ct);   // -1: leave the mode as it is (the firmware sets the mode for 0 to 3, and 0 is a mode)

        var (success, error) = BridgeProtocol.ParseResponse(response);
        if (!success)
            throw new InvalidOperationException($"SPI config failed: {error}");
    }

    public async Task SetModeAsync(int mode, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (mode < 0 || mode > 3)
            throw new ArgumentOutOfRangeException(nameof(mode), "SPI mode must be 0-3.");

        var response = await _transport.SendCommandAsync(
            BridgeProtocol.BuildCommand(BridgeProtocol.CMD_SPI_CONFIG, 0, mode), ct);

        var (success, error) = BridgeProtocol.ParseResponse(response);
        if (!success)
            throw new InvalidOperationException($"SPI config failed: {error}");
    }

    public void Dispose()
    {
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
