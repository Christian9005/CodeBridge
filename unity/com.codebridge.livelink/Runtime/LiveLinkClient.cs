using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace CodeBridge.LiveLink
{
    /// <summary>
    /// The connection to codebridge-livelink, without any MonoBehaviour: messages arrive on a background thread and wait in <see cref="Received"/> until the
    /// main thread takes them, so it can be used from a component, an editor tool or a test.
    /// </summary>
    public sealed class LiveLinkClient : IDisposable
    {
        private readonly ClientWebSocket _socket = new ClientWebSocket();
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        /// <summary>Messages from the link, oldest first. Take them on the main thread.</summary>
        public ConcurrentQueue<ServerMessage> Received { get; } = new ConcurrentQueue<ServerMessage>();

        public bool IsOpen => _socket.State == WebSocketState.Open;

        /// <summary>Why the connection ended, when it did not end because Unity closed it.</summary>
        public string LastError { get; private set; }

        /// <param name="address">For example <c>ws://127.0.0.1:5300/link</c>.</param>
        /// <param name="key">The key codebridge-livelink printed when it started.</param>
        public async Task ConnectAsync(string address, string key, CancellationToken ct = default)
        {
            var uri = new Uri(address + (address.Contains("?") ? "&" : "?") + "k=" + Uri.EscapeDataString(key ?? string.Empty));
            await _socket.ConnectAsync(uri, ct);
            _ = Task.Run(ReceiveLoopAsync);
        }

        public Task ConfigureAsync(JointMessage[] joints, int[] feedbackPins) =>
            SendAsync(new ClientMessage { type = "configure", joints = joints, feedbackPins = feedbackPins ?? new int[0] });

        public Task SetTargetsAsync(JointMessage[] targets) => SendAsync(new ClientMessage { type = "target", joints = targets });

        /// <summary>Every joint holds where it is; targets are ignored until <see cref="ResumeAsync"/>.</summary>
        public Task StopAsync() => SendAsync(new ClientMessage { type = "stop" });

        public Task ResumeAsync() => SendAsync(new ClientMessage { type = "resume" });

        public async Task SendAsync(ClientMessage message)
        {
            if (!IsOpen)
                return;

            var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(message));
            await _sendLock.WaitAsync(_stop.Token);
            try
            {
                await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _stop.Token);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (IsOpen && !_stop.IsCancellationRequested)
                {
                    using (var message = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _stop.Token);
                            if (result.MessageType == WebSocketMessageType.Close)
                                return;

                            message.Write(buffer, 0, result.Count);
                        }
                        while (!result.EndOfMessage);

                        var parsed = JsonUtility.FromJson<ServerMessage>(Encoding.UTF8.GetString(message.ToArray()));
                        if (parsed != null)
                            Received.Enqueue(parsed);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            try
            {
                if (IsOpen)
                    _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).Wait(500);
            }
            catch (Exception)
            {
                // closing a connection that is already broken
            }

            _socket.Dispose();
        }
    }
}
