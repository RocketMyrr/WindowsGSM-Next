using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WindowsGSM.Agent.Hub;

/// <summary>
/// The hub ⇄ member link over one WebSocket. Control messages are JSON text frames with a "t" (type) field;
/// request and response bodies travel as binary frames: [1 byte kind][4 byte request id][payload]. Many
/// requests share the link at once, so every send goes through one lock.
/// </summary>
public sealed class LinkChannel : IAsyncDisposable
{
    public const byte RequestBody = 1;
    public const byte ResponseBody = 2;
    public const int ChunkSize = 64 * 1024;
    private const int MaxTextMessage = 8 * 1024 * 1024; // snapshots can be sizeable; anything bigger is a bug

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _send = new(1, 1);

    public LinkChannel(WebSocket socket) => _socket = socket;

    public bool IsOpen => _socket.State == WebSocketState.Open;

    public async Task SendAsync(object message, CancellationToken token = default)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        await _send.WaitAsync(token);
        try { await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, token); }
        finally { _send.Release(); }
    }

    public async Task SendChunkAsync(byte kind, int id, ReadOnlyMemory<byte> payload, CancellationToken token = default)
    {
        byte[] frame = new byte[5 + payload.Length];
        frame[0] = kind;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1, 4), id);
        payload.CopyTo(frame.AsMemory(5));
        await _send.WaitAsync(token);
        try { await _socket.SendAsync(frame, WebSocketMessageType.Binary, true, token); }
        finally { _send.Release(); }
    }

    /// <summary>Reads until the link closes, handing each message to the callbacks. Returns the close status.</summary>
    public async Task<(WebSocketCloseStatus? Status, string? Description)> RunAsync(
        Func<JsonObject, Task> onMessage, Func<byte, int, ReadOnlyMemory<byte>, Task> onChunk, CancellationToken token)
    {
        var buffer = new byte[ChunkSize + 1024];
        using var message = new MemoryStream();
        while (_socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close) { return (_socket.CloseStatus, _socket.CloseStatusDescription); }
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxTextMessage) { await CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too big."); return (WebSocketCloseStatus.MessageTooBig, null); }
            } while (!result.EndOfMessage);

            var bytes = message.GetBuffer().AsMemory(0, (int)message.Length);
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                if (bytes.Length < 5) { continue; }
                // Copy: the receive buffer is reused for the next message.
                await onChunk(bytes.Span[0], BinaryPrimitives.ReadInt32LittleEndian(bytes.Span.Slice(1, 4)), bytes.Slice(5).ToArray());
            }
            else
            {
                JsonObject? obj;
                try { obj = JsonNode.Parse(bytes.Span) as JsonObject; } catch (JsonException) { obj = null; }
                if (obj != null) { await onMessage(obj); }
            }
        }
        return (_socket.CloseStatus, _socket.CloseStatusDescription);
    }

    public async Task CloseAsync(WebSocketCloseStatus status, string? reason)
    {
        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) { return; }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await _socket.CloseAsync(status, reason, timeout.Token); } catch { /* gone */ }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync(WebSocketCloseStatus.NormalClosure, null);
        _socket.Dispose();
    }

    /// <summary>Close codes with a meaning for the member.</summary>
    public static class Codes
    {
        public const WebSocketCloseStatus Unpaired = (WebSocketCloseStatus)4403;   // the hub removed this machine: stop and forget the hub
        public const WebSocketCloseStatus Replaced = (WebSocketCloseStatus)4409;   // a newer connection from the same machine took over
    }
}
