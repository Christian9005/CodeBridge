using System.Net.Sockets;
using System.Text;
using CodeBridge.Core.Abstractions;
using CodeBridge.Core.Protocol;

namespace CodeBridge.Transport.Wifi;

/// <summary>
/// TCP socket transport for communicating with ESP32 over WiFi.
/// The ESP32 firmware runs a TCP server on port 8080 (default).
/// Same protocol as serial — just a different pipe. Firmware 0.9+ requires a pairing token
/// (set over USB, see <see cref="CodeBridge.Transport.Provisioning.Esp32WifiProvisioner"/>); the transport sends it as the first command.
/// </summary>
public class WifiTransport : ITransport
{
    /// <summary>The longest protocol line accepted from the board. Anything longer is discarded instead of buffered.</summary>
    public const int MaxLineLength = 4096;

    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private readonly string _ipAddress;
    private readonly int _port;
    private readonly string? _token;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private TaskCompletionSource<string>? _responseWaiter;
    private TaskCompletionSource<bool>? _readyWaiter;
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoopTask;
    private volatile bool _connectionLost;
    private bool _needsResync;
    private bool _disposed;

    public bool IsConnected => !_connectionLost && (_tcpClient?.Connected ?? false);

    public event EventHandler<DataReceivedEventArgs>? DataReceived;

    /// <summary>
    /// Creates a new WiFi transport.
    /// </summary>
    /// <param name="ipAddress">IP address of the ESP32 (e.g. "192.168.1.100")</param>
    /// <param name="port">TCP port (default: 8080)</param>
    /// <param name="accessToken">Pairing token stored on the board (firmware 0.9+). Null for boards that do not use one.</param>
    public WifiTransport(string ipAddress, int port = 8080, string? accessToken = null)
    {
        _ipAddress = ipAddress;
        _port = port;
        _token = string.IsNullOrEmpty(accessToken) ? null : accessToken;
    }

    /// <summary>How long a command may take before it counts as lost. Raise it for slow operations such as an OTA update.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum connection attempts when the ESP32 is not yet reachable (e.g., booting).
    /// </summary>
    public int MaxConnectRetries { get; set; } = 5;

    /// <summary>
    /// Delay between connection retry attempts.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Exception? lastException = null;

        for (int attempt = 1; attempt <= MaxConnectRetries; attempt++)
        {
            try
            {
                await OpenSessionAsync(ct);
                return; // Success!
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or TimeoutException)
            {
                if (ct.IsCancellationRequested)
                    throw;

                lastException = ex;
                await CloseSessionAsync();

                if (attempt < MaxConnectRetries)
                {
                    await Task.Delay(RetryDelay, ct);
                }
            }
            catch
            {
                await CloseSessionAsync();
                throw; // authentication and protocol errors are not transient: do not retry
            }
        }

        throw new InvalidOperationException(
            $"Could not connect to ESP32 at {_ipAddress}:{_port} after {MaxConnectRetries} attempts. " +
            $"Make sure the ESP32 is powered on and connected to WiFi.",
            lastException);
    }

    private async Task OpenSessionAsync(CancellationToken ct)
    {
        _connectionLost = false;
        _needsResync = false;
        _tcpClient = new TcpClient
        {
            ReceiveTimeout = 5000,
            SendTimeout = 5000,
            NoDelay = true  // Disable Nagle for low-latency commands
        };

        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            await _tcpClient.ConnectAsync(_ipAddress, _port, timeoutCts.Token);
        }

        _stream = _tcpClient.GetStream();

        // Start background read loop
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _readyWaiter = ready;
        _readLoopCts = new CancellationTokenSource();
        var loopToken = _readLoopCts.Token;
        var stream = _stream;
        _readLoopTask = Task.Run(() => ReadLoopAsync(stream, loopToken));

        try
        {
            using var readyTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readyTimeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            using var reg = readyTimeoutCts.Token.Register(() =>
                ready.TrySetException(
                    new TimeoutException("CodeBridge firmware did not send CODEBRIDGE_READY within 15 seconds.")));

            await ready.Task;
        }
        catch (TimeoutException)
        {
            _readyWaiter = null;
            if (!await TryPingExistingSessionAsync(ct))
                throw;
        }
        finally
        {
            _readyWaiter = null;
        }

        await AuthenticateAsync(ct);
    }

    private async Task AuthenticateAsync(CancellationToken ct)
    {
        if (_token is not null)
        {
            var response = await ExchangeAsync(BridgeProtocol.BuildCommand(BridgeProtocol.CMD_AUTH, _token), TimeSpan.FromSeconds(5), ct);
            var (ok, data) = BridgeProtocol.ParseResponse(response);
            if (!ok)
                throw new UnauthorizedAccessException(
                    $"The board at {_ipAddress}:{_port} rejected the pairing token ({data}). " +
                    "Pair the board again over USB (Wi-Fi setup in the flow editor, or Esp32WifiProvisioner).");
            return;
        }
    }

    private async Task CloseSessionAsync()
    {
        var loopCts = _readLoopCts;
        _readLoopCts = null;
        if (loopCts is not null)
        {
            loopCts.Cancel();
            try { await (_readLoopTask ?? Task.CompletedTask); } catch { }
            loopCts.Dispose();
        }

        _readLoopTask = null;
        _stream?.Dispose();
        _tcpClient?.Dispose();
        _stream = null;
        _tcpClient = null;
    }

    public async Task DisconnectAsync(CancellationToken ct = default) => await CloseSessionAsync();

    public async Task<string> SendCommandAsync(string command, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_stream is null || _tcpClient is null || !IsConnected)
            throw new InvalidOperationException("Not connected. Call ConnectAsync() first.");

        await _commandLock.WaitAsync(ct);
        try
        {
            if (_needsResync)
                await ResyncAsync(ct);

            var response = await ExchangeAsync(command, CommandTimeout, ct);
            ThrowIfAccessDenied(response);
            return response;
        }
        finally
        {
            _commandLock.Release();
        }
    }

    // Firmware 0.9+ answers every command with an authentication error until the session presents the pairing token.
    private void ThrowIfAccessDenied(string response)
    {
        if (!response.StartsWith("ERR", StringComparison.Ordinal))
            return;

        if (response.Contains("NO_TOKEN", StringComparison.Ordinal) || response.Contains("AUTH_REQUIRED", StringComparison.Ordinal))
            throw new UnauthorizedAccessException(
                $"The board at {_ipAddress}:{_port} requires a pairing token ({response}). " +
                "Pair it over USB first (Wi-Fi setup in the flow editor, or Esp32WifiProvisioner), then pass the token to WiFi(ip, port, token).");
    }

    /// <summary>Writes one command and waits for its OK/ERR line. Callers hold <c>_commandLock</c> (or are connecting).</summary>
    private async Task<string> ExchangeAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException("Not connected.");
        var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _responseWaiter = waiter;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var registration = cts.Token.Register(() =>
        {
            if (ct.IsCancellationRequested) waiter.TrySetCanceled(ct);
            else waiter.TrySetException(new TimeoutException($"No response received for command: {command.Trim()}"));
        });

        try
        {
            var bytes = Encoding.UTF8.GetBytes(command);
            await stream.WriteAsync(bytes, cts.Token);
            await stream.FlushAsync(cts.Token);
            return await waiter.Task;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // The board may still answer later; that late line must not be taken for the next command's response.
            _needsResync = true;
            throw;
        }
        finally
        {
            if (ReferenceEquals(_responseWaiter, waiter))
                _responseWaiter = null;
        }
    }

    /// <summary>After a timeout, PING until the board answers PONG so stale responses are flushed before the next command.</summary>
    private async Task ResyncAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var response = await ExchangeAsync(BridgeProtocol.BuildCommand(BridgeProtocol.CMD_PING), TimeSpan.FromSeconds(2), ct);
                if (BridgeProtocol.ParseResponse(response) is (true, "PONG"))
                {
                    // Answers to earlier commands (and to our own extra PINGs) may still be in flight: let them arrive with
                    // nobody waiting for a response, so they are dropped instead of being taken for the next command's answer.
                    await Task.Delay(100, ct);
                    _needsResync = false;
                    return;
                }
            }
            catch (TimeoutException)
            {
                // try again
            }
        }

        throw new TimeoutException("The board stopped answering. Reconnect to resynchronize the connection.");
    }

    private async Task<bool> TryPingExistingSessionAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(250, ct);
            var response = await ExchangeAsync(BridgeProtocol.BuildCommand(BridgeProtocol.CMD_PING), TimeSpan.FromSeconds(5), ct);
            var (success, data) = BridgeProtocol.ParseResponse(response);
            // An authentication error still proves the firmware is alive and talking.
            return success ? data == "PONG" : data.Contains("AUTH_REQUIRED", StringComparison.Ordinal) || data.Contains("NO_TOKEN", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public async Task SendRawAsync(byte[] data, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_stream is null || _tcpClient is null || !IsConnected)
            throw new InvalidOperationException("Not connected.");

        await _commandLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(data, ct);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task<byte[]> ReceiveRawAsync(int length, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (_stream is null || _tcpClient is null || !IsConnected)
            throw new InvalidOperationException("Not connected.");

        var buffer = new byte[length];
        var totalRead = 0;

        while (totalRead < length)
        {
            var bytesRead = await _stream.ReadAsync(
                buffer.AsMemory(totalRead, length - totalRead), ct);

            if (bytesRead == 0)
                break; // Connection closed

            totalRead += bytesRead;
        }

        if (totalRead < length)
            Array.Resize(ref buffer, totalRead);

        return buffer;
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var chunk = new byte[1024];
        var line = new StringBuilder();
        var discarding = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(chunk, ct);
                if (read == 0)
                    break; // the board closed the connection

                for (var i = 0; i < read; i++)
                {
                    var b = chunk[i];
                    if (b == (byte)'\n' || b == (byte)'\r')
                    {
                        if (!discarding && line.Length > 0)
                            HandleLine(line.ToString().Trim());

                        line.Clear();
                        discarding = false;
                    }
                    else if (!discarding)
                    {
                        if (line.Length >= MaxLineLength)
                        {
                            // A line this long is not protocol traffic: drop it instead of growing without bound.
                            line.Clear();
                            discarding = true;
                        }
                        else
                        {
                            line.Append((char)b);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* a dropped connection is reported below */ }

        if (!ct.IsCancellationRequested)
        {
            _connectionLost = true;
            var error = new IOException("The connection to the board was closed.");
            _readyWaiter?.TrySetException(error);
            _responseWaiter?.TrySetException(error);
        }
    }

    private void HandleLine(string line)
    {
        if (line.Length == 0)
            return;

        var readyWaiter = _readyWaiter;
        if (readyWaiter is not null)
        {
            if (line.Contains("CODEBRIDGE_READY", StringComparison.Ordinal))
                readyWaiter.TrySetResult(true);
            return;
        }

        var responseWaiter = _responseWaiter;
        if (responseWaiter is not null)
        {
            if (line.StartsWith("OK", StringComparison.Ordinal) || line.StartsWith("ERR", StringComparison.Ordinal))
                responseWaiter.TrySetResult(line);
            return;
        }

        DataReceived?.Invoke(this, new DataReceivedEventArgs(line));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var loopCts = _readLoopCts;
        _readLoopCts = null;
        loopCts?.Cancel();
        loopCts?.Dispose();

        _stream?.Dispose();
        _tcpClient?.Dispose();
        _commandLock.Dispose();

        GC.SuppressFinalize(this);
    }
}
