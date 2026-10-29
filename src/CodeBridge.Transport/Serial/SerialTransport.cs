using System.IO.Ports;
using CodeBridge.Core.Abstractions;
using CodeBridge.Core.Protocol;

namespace CodeBridge.Transport.Serial;

/// <summary>
/// Serial port transport for communicating with microcontrollers via USB.
/// This is the primary transport for the MVP.
/// </summary>
public class SerialTransport : ITransport
{
    private SerialPort? _serialPort;
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private TaskCompletionSource<string>? _responseWaiter;
    private TaskCompletionSource<bool>? _readyWaiter;
    private bool _disposed;

    /// <summary>
    /// Command response timeout in seconds. Default is 5s. 
    /// Increase for long-running commands like WiFi configuration.
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 5;

    public bool IsConnected => _serialPort?.IsOpen ?? false;

    public event EventHandler<DataReceivedEventArgs>? DataReceived;

    /// <summary>
    /// Creates a new serial transport.
    /// </summary>
    /// <param name="portName">COM port name (e.g., "COM3" on Windows, "/dev/ttyUSB0" on Linux)</param>
    /// <param name="baudRate">Baud rate (default: 115200)</param>
    public SerialTransport(string portName, int baudRate = 115200)
    {
        _portName = portName;
        _baudRate = baudRate;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // DTR/RTS stay low while opening: asserting them resets an ESP32 (auto-reset circuit), which made every
        // connection wait for a full reboot and switched off whatever the board was driving.
        _serialPort = new SerialPort(_portName, _baudRate)
        {
            ReadTimeout = 5000,
            WriteTimeout = 5000,
            DtrEnable = false,
            RtsEnable = false,
            Handshake = Handshake.None,
            NewLine = "\n"
        };

        _serialPort.DataReceived += OnSerialDataReceived;
        try
        {
            _serialPort.Open();
            await WaitForBoardAsync(ct);
        }
        catch
        {
            // Never leave the COM port locked after a failed connect (a retry would get "access denied").
            ReleasePort();
            throw;
        }
    }

    private async Task WaitForBoardAsync(CancellationToken ct)
    {
        // 1) The usual case: the firmware is already running, so a PING answers immediately (no reboot).
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (await TryPingAsync(TimeSpan.FromMilliseconds(1200), ct))
                return;
        }

        // 2) No answer: reboot the board (RTS pulse on the auto-reset circuit) and wait for its ready banner.
        var readyWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _readyWaiter = readyWaiter;
        try
        {
            PulseReset();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));
            using var reg = timeoutCts.Token.Register(() =>
            {
                if (ct.IsCancellationRequested)
                    readyWaiter.TrySetCanceled(ct);
                else
                    readyWaiter.TrySetException(
                        new TimeoutException("CodeBridge firmware did not send CODEBRIDGE_READY within 8 seconds."));
            });

            await readyWaiter.Task;
        }
        finally
        {
            _readyWaiter = null;
        }
    }

    /// <summary>Resets boards with the standard DTR/RTS auto-reset circuit without entering the bootloader.</summary>
    private void PulseReset()
    {
        var port = _serialPort;
        if (port is null || !port.IsOpen)
            return;

        port.DtrEnable = false; // IO0 high: boot normally
        port.RtsEnable = true;  // EN low: hold in reset
        Thread.Sleep(120);
        port.RtsEnable = false; // EN high: start
    }

    private async Task<bool> TryPingAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var response = await SendCommandAsync(BridgeProtocol.BuildCommand(BridgeProtocol.CMD_PING), timeout, ct);
            var (success, data) = BridgeProtocol.ParseResponse(response);
            return success && data == "PONG";
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        ReleasePort();
        return Task.CompletedTask;
    }

    private void ReleasePort()
    {
        var port = _serialPort;
        _serialPort = null;
        if (port is null)
            return;

        port.DataReceived -= OnSerialDataReceived;
        try
        {
            if (port.IsOpen)
                port.Close();
        }
        catch (IOException)
        {
            // The device was unplugged; nothing left to close.
        }

        port.Dispose();
    }

    public Task<string> SendCommandAsync(string command, CancellationToken ct = default) =>
        SendCommandAsync(command, TimeSpan.FromSeconds(CommandTimeoutSeconds), ct);

    private async Task<string> SendCommandAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_serialPort is null || !_serialPort.IsOpen)
            throw new InvalidOperationException("Serial port is not connected. Call ConnectAsync() first.");

        await _commandLock.WaitAsync(ct);
        try
        {
            // Clear any pending data safely
            try
            {
                if (_serialPort.BytesToRead > 0)
                    _serialPort.DiscardInBuffer();
            }
            catch
            {
                // DiscardInBuffer can throw if concurrent read/write occurs on underlying Win32 port
            }

            // Set up response waiter
            _responseWaiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Register cancellation
            using var registration = ct.Register(() =>
                _responseWaiter.TrySetCanceled(ct));

            // Send command
            _serialPort.Write(command);

            // Wait for response with timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            using var timeoutRegistration = timeoutCts.Token.Register(() =>
                _responseWaiter.TrySetException(
                    new TimeoutException($"No response received for command: {command.Trim()}")));

            return await _responseWaiter.Task;
        }
        finally
        {
            _responseWaiter = null;
            _commandLock.Release();
        }
    }

    public async Task SendRawAsync(byte[] data, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_serialPort is null || !_serialPort.IsOpen)
            throw new InvalidOperationException("Serial port is not connected.");

        await _commandLock.WaitAsync(ct);
        try
        {
            _serialPort.Write(data, 0, data.Length);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public Task<byte[]> ReceiveRawAsync(int length, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_serialPort is null || !_serialPort.IsOpen)
            throw new InvalidOperationException("Serial port is not connected.");

        var buffer = new byte[length];
        var bytesRead = _serialPort.Read(buffer, 0, length);

        if (bytesRead < length)
            Array.Resize(ref buffer, bytesRead);

        return Task.FromResult(buffer);
    }

    /// <summary>
    /// Lists all available serial ports on the system.
    /// </summary>
    public static string[] GetAvailablePorts() => SerialPort.GetPortNames();

    private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        // One DataReceived event can cover several buffered lines: drain them all, otherwise the
        // remaining lines (often the command response) would sit unread until more data arrives.
        while (true)
        {
            var port = _serialPort;
            string line;
            try
            {
                if (port is null || !port.IsOpen || port.BytesToRead == 0)
                    return;

                line = port.ReadLine().Trim();
            }
            catch (TimeoutException)
            {
                return; // Partial line; the rest arrives with the next event.
            }
            catch (InvalidOperationException)
            {
                return; // Port closed while reading.
            }
            catch (IOException)
            {
                return;
            }

            if (line.Length > 0)
                HandleLine(line);
        }
    }

    private void HandleLine(string line)
    {
        var readyWaiter = _readyWaiter;
        // Check for CODEBRIDGE_READY signal during boot
        if (readyWaiter != null && line.Contains("CODEBRIDGE_READY"))
        {
            readyWaiter.TrySetResult(true);
            return;
        }

        // During boot phase, discard all other messages
        if (readyWaiter != null)
            return;

        // If we're waiting for a command response, only accept protocol responses
        var responseWaiter = _responseWaiter;
        if (responseWaiter != null)
        {
            if (line.StartsWith("OK") || line.StartsWith("ERR"))
                responseWaiter.TrySetResult(line);

            // Discard non-protocol lines (debug messages, WiFi status, etc.)
            return;
        }

        // Otherwise, raise as async data event
        DataReceived?.Invoke(this, new DataReceivedEventArgs(line));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ReleasePort();
        _commandLock.Dispose();

        GC.SuppressFinalize(this);
    }
}
