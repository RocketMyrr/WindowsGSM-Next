using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WindowsGSM.Functions
{
    /// <summary>
    /// Rust-style WebSocket RCON (Facepunch "webrcon"). Connects to
    /// ws://host:port/PASSWORD and exchanges JSON messages. Unlike Source RCON,
    /// responses (and live console output: chat, joins, kills) arrive
    /// asynchronously, so callers subscribe to <see cref="MessageReceived"/>.
    /// </summary>
    public class RustRconClient
    {
        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private int _identifier = 1;

        /// <summary>Fired (off the UI thread) for every message the server sends.</summary>
        public event Action<string> MessageReceived;

        /// <summary>Fired (off the UI thread) when the connection drops; arg is the reason.</summary>
        public event Action<string> Disconnected;

        public bool IsConnected => _ws != null && _ws.State == WebSocketState.Open;

        public async Task<string> Connect(string ip, int port, string password)
        {
            try
            {
                Disconnect();

                _ws = new ClientWebSocket();
                _cts = new CancellationTokenSource();

                // Rust expects the raw password as the URI path segment.
                var uri = new Uri($"ws://{ip}:{port}/{password}");
                await _ws.ConnectAsync(uri, CancellationToken.None);

                _ = Task.Run(() => ReceiveLoop(_cts.Token));
                return string.Empty;
            }
            catch (Exception ex)
            {
                Disconnect();
                return ex.Message;
            }
        }

        public async Task<string> Send(string command)
        {
            if (!IsConnected) { return "CONNECTION FAILED"; }

            var packet = new JObject
            {
                ["Identifier"] = _identifier++,
                ["Message"] = command,
                ["Name"] = "WebRcon"
            };

            byte[] bytes = Encoding.UTF8.GetBytes(packet.ToString(Formatting.None));
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);

            // Responses come back asynchronously via MessageReceived.
            return string.Empty;
        }

        private async Task ReceiveLoop(CancellationToken token)
        {
            var buffer = new byte[8192];
            var sb = new StringBuilder();

            try
            {
                while (_ws != null && _ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    sb.Clear();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            Disconnected?.Invoke("Server closed the connection.");
                            return;
                        }

                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    while (!result.EndOfMessage);

                    string message = ExtractMessage(sb.ToString());
                    if (!string.IsNullOrEmpty(message))
                    {
                        MessageReceived?.Invoke(message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown via Disconnect().
            }
            catch (Exception ex)
            {
                Disconnected?.Invoke(ex.Message);
            }
        }

        private static string ExtractMessage(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) { return null; }

            try
            {
                // Rust frames look like {"Message":"...","Identifier":N,"Type":"Generic",...}
                return JObject.Parse(raw)["Message"]?.ToString() ?? raw;
            }
            catch
            {
                return raw; // not JSON — surface it verbatim
            }
        }

        public void Disconnect()
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }

            try
            {
                if (_ws != null && (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived))
                {
                    _ = _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                }
            }
            catch { /* ignore */ }

            try { _ws?.Dispose(); } catch { /* ignore */ }
            _ws = null;

            try { _cts?.Dispose(); } catch { /* ignore */ }
            _cts = null;
        }
    }
}
