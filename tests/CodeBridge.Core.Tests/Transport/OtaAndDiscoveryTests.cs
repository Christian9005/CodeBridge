using System.Net;
using System.Net.Sockets;
using System.Text;
using CodeBridge.Transport.Discovery;
using CodeBridge.Transport.Ota;

namespace CodeBridge.Core.Tests.Transport;

public class OtaAndDiscoveryTests
{
    /// <summary>A stand-in for the board: speaks the protocol, requires the token and downloads the image like the real OTAB handler does.</summary>
    private sealed class FakeBoard : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly string _failOtaWith;

        public FakeBoard(string token, string failOtaWith = "")
        {
            Token = token;
            _failOtaWith = failOtaWith;
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        public string Token { get; }
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string Version { get; private set; } = "0.9.0";
        public byte[]? Downloaded { get; private set; }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => HandleAsync(client));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream);
                    using var writer = new StreamWriter(stream) { AutoFlush = true };
                    await writer.WriteLineAsync("OK:CODEBRIDGE_READY");
                    var authed = false;

                    while (await reader.ReadLineAsync() is { } line)
                    {
                        line = line.Trim();
                        if (line.StartsWith("AUTH:"))
                        {
                            authed = line == "AUTH:" + Token;
                            await writer.WriteLineAsync(authed ? "OK" : "ERR:AUTH_FAILED");
                        }
                        else if (!authed)
                            await writer.WriteLineAsync("ERR:AUTH_REQUIRED");
                        else if (line == "PING")
                            await writer.WriteLineAsync("OK:PONG");
                        else if (line == "VER")
                            await writer.WriteLineAsync("OK:" + Version);
                        else if (line.StartsWith("OTAB:"))
                        {
                            if (_failOtaWith.Length > 0)
                            {
                                await writer.WriteLineAsync("ERR:" + _failOtaWith);
                                continue;
                            }

                            using var http = new HttpClient();
                            Downloaded = await http.GetByteArrayAsync(line["OTAB:".Length..]);
                            Version = "0.9.1"; // the new image runs after the restart
                            await writer.WriteLineAsync("OK:OTA_COMPLETE");
                            return; // the board restarts: the connection closes
                        }
                        else
                            await writer.WriteLineAsync("ERR:Unknown command");
                    }
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or SocketException) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _loop; } catch (Exception) { }
        }
    }

    private static byte[] Image(int size = 200_000)
    {
        var bytes = new byte[size];
        new Random(5).NextBytes(bytes);
        bytes[0] = 0xE9;
        return bytes;
    }

    private sealed class Collect : IProgress<int>
    {
        public List<int> Values { get; } = new();
        public void Report(int value) { lock (Values) Values.Add(value); }
    }

    [Fact]
    public async Task The_firmware_reaches_the_board_byte_for_byte_and_the_new_version_is_reported()
    {
        await using var board = new FakeBoard("secret-token-1");
        var image = Image();
        var progress = new Collect();

        var result = await Esp32OtaUpdater.UpdateAsync("127.0.0.1", board.Port, "secret-token-1", image, progress);

        Assert.Equal("0.9.1", result.NewVersion);
        Assert.Equal(image, board.Downloaded);
        Assert.Equal(100, progress.Values.Max());
        Assert.Equal(progress.Values.OrderBy(v => v), progress.Values); // never goes backwards
    }

    [Fact]
    public async Task A_wrong_token_is_refused_before_anything_is_sent()
    {
        await using var board = new FakeBoard("right-token-123");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Esp32OtaUpdater.UpdateAsync("127.0.0.1", board.Port, "wrong-token-000", Image()));

        Assert.Null(board.Downloaded);
    }

    [Fact]
    public async Task A_board_that_cannot_reach_this_PC_gets_a_firewall_hint()
    {
        await using var board = new FakeBoard("secret-token-1", failOtaWith: "HTTP -1");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Esp32OtaUpdater.UpdateAsync("127.0.0.1", board.Port, "secret-token-1", Image()));

        Assert.Contains("Firewall", error.Message);
    }

    [Theory]
    [InlineData(10, 0xE9)]
    [InlineData(200_000, 0x00)]
    public async Task Files_that_are_not_ESP32_application_images_are_rejected(int size, byte first)
    {
        var bytes = new byte[size];
        bytes[0] = first;

        await Assert.ThrowsAsync<ArgumentException>(() => Esp32OtaUpdater.UpdateAsync("127.0.0.1", 8080, "token-12345", bytes));
    }

    // ---------------------------------------------------------------- discovery

    private static byte[] Announcement(string instance, string host, int port, string[] text, byte[] address)
    {
        var bytes = new List<byte> { 0, 0, 0x84, 0, 0, 0, 0, 4, 0, 0, 0, 0 };
        void Name(string name)
        {
            foreach (var label in name.Split('.'))
            {
                var data = Encoding.UTF8.GetBytes(label);
                bytes.Add((byte)data.Length);
                bytes.AddRange(data);
            }
            bytes.Add(0);
        }
        void Record(string name, int type, byte[] data)
        {
            Name(name);
            bytes.AddRange(new byte[] { (byte)(type >> 8), (byte)type, 0, 1, 0, 0, 0, 120, (byte)(data.Length >> 8), (byte)data.Length });
            bytes.AddRange(data);
        }
        byte[] NameBytes(string name)
        {
            var saved = bytes.ToArray();
            bytes.Clear();
            Name(name);
            var result = bytes.ToArray();
            bytes.Clear();
            bytes.AddRange(saved);
            return result;
        }

        Record("_codebridge._tcp.local", 12, NameBytes(instance + "._codebridge._tcp.local"));
        Record(instance + "._codebridge._tcp.local", 33, new byte[] { 0, 0, 0, 0, (byte)(port >> 8), (byte)port }.Concat(NameBytes(host + ".local")).ToArray());
        Record(instance + "._codebridge._tcp.local", 16, text.SelectMany(t => new[] { (byte)Encoding.UTF8.GetByteCount(t) }.Concat(Encoding.UTF8.GetBytes(t))).ToArray());
        Record(host + ".local", 1, address);
        return bytes.ToArray();
    }

    [Fact]
    public void A_board_announcement_becomes_a_found_board_with_a_stable_identity()
    {
        var packet = Announcement("codebridge-a1b2", "codebridge-a1b2", 8080,
            new[] { "id=aabbccddeeff", "fw=0.9.0", "board=ESP32-D0WD-V3", "auth=1" }, new byte[] { 192, 168, 68, 51 });

        var boards = WifiBoardFinder.FromRecords(MdnsParser.Parse(packet));

        var board = Assert.Single(boards);
        Assert.Equal("aabbccddeeff", board.Id);
        Assert.Equal("192.168.68.51", board.Target);
        Assert.Equal("codebridge-a1b2.local", board.Host);
        Assert.Equal(8080, board.Port);
        Assert.Equal("0.9.0", board.Firmware);
        Assert.True(board.RequiresToken);
    }

    [Fact]
    public void Garbage_on_the_network_never_produces_a_board_or_an_exception()
    {
        var random = new Random(3);
        for (var i = 0; i < 1000; i++)
        {
            var bytes = new byte[random.Next(0, 300)];
            random.NextBytes(bytes);
            WifiBoardFinder.FromRecords(MdnsParser.Parse(bytes));
        }
    }
}
