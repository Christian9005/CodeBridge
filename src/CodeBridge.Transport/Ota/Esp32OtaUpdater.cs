using System.Net;
using System.Net.Sockets;
using System.Text;
using CodeBridge.Core.Protocol;
using CodeBridge.Transport.Wifi;

namespace CodeBridge.Transport.Ota;

/// <summary>What the board reported after the update.</summary>
public sealed record OtaResult(string? NewVersion);

/// <summary>
/// Updates an ESP32's firmware over Wi-Fi (no cable). The board downloads the image itself, so this class serves it for a
/// moment from a tiny web server on this PC, tells the board where to fetch it, and waits for the board to restart.
/// The board must already be paired (see <see cref="Provisioning.Esp32WifiProvisioner"/>); only an authenticated client can start an update.
/// </summary>
public static class Esp32OtaUpdater
{
    /// <param name="host">The board's IP address or mDNS name.</param>
    /// <param name="firmware">The application image (<c>firmware.bin</c>), not the merged flash image.</param>
    /// <param name="progress">Percent of the image the board has downloaded (0-100).</param>
    public static async Task<OtaResult> UpdateAsync(
        string host,
        int port,
        string? accessToken,
        byte[] firmware,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(firmware);
        if (firmware.Length < 64 * 1024)
            throw new ArgumentException("That file is too small to be an ESP32 firmware image.", nameof(firmware));
        if (firmware[0] != 0xE9)
            throw new ArgumentException("That file is not an ESP32 application image (it must start with 0xE9). Use firmware.bin, not a merged flash dump.", nameof(firmware));

        var boardAddress = await ResolveAsync(host, ct);
        var localAddress = LocalAddressTowards(boardAddress, port);

        using var server = new FirmwareServer(localAddress, firmware, progress);
        server.Start();

        using var transport = new WifiTransport(host, port, accessToken) { CommandTimeout = TimeSpan.FromMinutes(4), MaxConnectRetries = 2 };
        await transport.ConnectAsync(ct);

        var url = $"http://{localAddress}:{server.Port}/firmware.bin";
        string response;
        try
        {
            response = await transport.SendCommandAsync(BridgeProtocol.BuildCommand(BridgeProtocol.CMD_OTA_BEGIN, url), ct);
        }
        catch (IOException)
        {
            // the board restarts right after it answers; a reset connection at this point still means it finished the download
            response = server.Completed ? "OK:OTA_COMPLETE" : throw new InvalidOperationException("The connection to the board dropped during the update.");
        }

        var (success, data) = BridgeProtocol.ParseResponse(response);
        if (!success)
            throw new InvalidOperationException(Explain(data, localAddress));

        progress?.Report(100);
        return new OtaResult(await WaitForRestartAsync(host, port, accessToken, ct));
    }

    private static string Explain(string firmwareMessage, IPAddress localAddress) =>
        firmwareMessage.StartsWith("HTTP", StringComparison.Ordinal)
            ? $"The board could not download the firmware from this PC ({localAddress}): {firmwareMessage}. Allow the program through Windows Firewall for private networks and try again."
            : firmwareMessage.Contains("AUTH", StringComparison.Ordinal) || firmwareMessage.Contains("TOKEN", StringComparison.Ordinal)
                ? "The board rejected the pairing token. Pair it again over USB."
                : "The update failed: " + firmwareMessage;

    /// <summary>The board restarts after flashing: wait until it answers again and report its version.</summary>
    private static async Task<string?> WaitForRestartAsync(string host, int port, string? token, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), ct); // let it go down first
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var transport = new WifiTransport(host, port, token) { MaxConnectRetries = 1, RetryDelay = TimeSpan.FromSeconds(1) };
                await transport.ConnectAsync(ct);
                var version = BridgeProtocol.ParseResponse(await transport.SendCommandAsync(BridgeProtocol.BuildCommand(BridgeProtocol.CMD_VERSION), ct));
                return version.Success ? version.Data : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }

        throw new TimeoutException("The board accepted the update but did not come back within a minute. Check its power and try connecting again.");
    }

    private static async Task<IPAddress> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
            return literal;

        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? throw new InvalidOperationException($"Could not find '{host}' on the network.");
    }

    /// <summary>The address of this PC that the board can reach: the one the operating system would use to talk to it.</summary>
    internal static IPAddress LocalAddressTowards(IPAddress board, int port)
    {
        if (IPAddress.IsLoopback(board))
            return IPAddress.Loopback;

        using var probe = new UdpClient(AddressFamily.InterNetwork);
        probe.Connect(board, port);
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Address;
    }

    /// <summary>A one-purpose web server that hands the firmware to the board and counts the bytes it downloaded.</summary>
    private sealed class FirmwareServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[] _firmware;
        private readonly IProgress<int>? _progress;
        private readonly CancellationTokenSource _stop = new();
        private int _completed;

        public FirmwareServer(IPAddress address, byte[] firmware, IProgress<int>? progress)
        {
            _listener = new TcpListener(address, 0);
            _firmware = firmware;
            _progress = progress;
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public bool Completed => Volatile.Read(ref _completed) == 1;

        public void Start()
        {
            _listener.Start(2);
            _ = Task.Run(() => AcceptLoopAsync(_stop.Token));
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(ct);
                    _ = Task.Run(() => ServeAsync(client, ct), ct);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // stopped
            }
        }

        private async Task ServeAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                try
                {
                    client.SendTimeout = 15000;
                    client.ReceiveTimeout = 15000;
                    using var stream = client.GetStream();
                    var requestLine = await ReadRequestLineAsync(stream, ct);
                    if (!requestLine.StartsWith("GET /firmware.bin", StringComparison.Ordinal))
                    {
                        var notFound = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(notFound, ct);
                        return;
                    }

                    var header = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {_firmware.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, ct);

                    const int chunk = 4096;
                    var sent = 0;
                    var lastPercent = -1;
                    while (sent < _firmware.Length)
                    {
                        var size = Math.Min(chunk, _firmware.Length - sent);
                        await stream.WriteAsync(_firmware.AsMemory(sent, size), ct);
                        sent += size;
                        var percent = (int)(sent * 100L / _firmware.Length);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            _progress?.Report(Math.Min(percent, 99));
                        }
                    }

                    await stream.FlushAsync(ct);
                    Interlocked.Exchange(ref _completed, 1);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                {
                    // the board will report the failure itself
                }
            }
        }

        private static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
        {
            // Read up to the end of the headers, but never more than 8 KB: this server only ever talks to one trusted request.
            var buffer = new byte[8192];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
                if (read == 0)
                    break;

                length += read;
                if (length >= 4 && Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal))
                    break;
            }

            var text = Encoding.ASCII.GetString(buffer, 0, length);
            var end = text.IndexOf("\r\n", StringComparison.Ordinal);
            return end < 0 ? text : text[..end];
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch (Exception) { }
            _stop.Dispose();
        }
    }
}
