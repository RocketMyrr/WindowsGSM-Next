using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using WindowsGSM.Agent.Api;
using WindowsGSM.Agent.Realtime;
using WindowsGSM.Agent.Security;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Hub;

/// <summary>
/// The hub's side of multi-machine: member agents dial in to /api/v2/hub/link and stay connected. Requests for
/// /api/v2/machines/{member}/… are sent down that link, run on the member as the signed-in user, and the
/// response streams back. Members stream their events up; they're relayed to this hub's browsers.
/// </summary>
public sealed class HubLinks
{
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(120);
    private static readonly string[] CopiedResponseHeaders = { "Content-Type", "Content-Disposition", "Content-Length", "Cache-Control", "ETag", "Last-Modified" };

    private sealed class Pending
    {
        public readonly TaskCompletionSource<JsonObject> Head = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly Channel<byte[]> Body = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = true });
    }

    private sealed class Link
    {
        public required string MachineId;
        public required LinkChannel Channel;
        public readonly ConcurrentDictionary<int, Pending> Pending = new();
        public int NextId;
        public string? Version;
    }

    private sealed class LinkLostException : Exception
    {
        public LinkLostException() : base("The machine disconnected while handling this request.") { }
    }

    private readonly AgentContext _ctx;
    private readonly MachineRegistry _registry;
    private readonly EventStream _stream;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, Link> _links = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, HashSet<string>> _watches = new Dictionary<string, HashSet<string>>();

    public HubLinks(AgentContext ctx, MachineRegistry registry, EventStream stream, Action<string> log)
    {
        _ctx = ctx;
        _registry = registry;
        _stream = stream;
        _log = log;
        _stream.RemoteWatchesChanged += UpdateWatches;
    }

    public bool IsOnline(string machine) => _links.ContainsKey(machine);

    public string? VersionOf(string machine) => _links.TryGetValue(machine, out var l) ? l.Version : null;

    // ───────────────────────────── Members connecting ─────────────────────────────

    /// <summary>GET /api/v2/hub/link — a member's long-lived connection. Auth: "WGSM-Machine {id}:{credential}".</summary>
    public async Task HandleLinkAsync(HttpContext http)
    {
        if (!http.WebSockets.IsWebSocketRequest) { await ApiResults.BadRequest("This address only speaks WebSocket.").ExecuteAsync(http); return; }
        string auth = http.Request.Headers.Authorization.ToString();
        const string scheme = "WGSM-Machine ";
        string? machine = null, credential = null;
        if (auth.StartsWith(scheme, StringComparison.Ordinal))
        {
            string rest = auth[scheme.Length..];
            int colon = rest.IndexOf(':');
            if (colon > 0) { machine = rest[..colon]; credential = rest[(colon + 1)..]; }
        }
        if (machine == null || credential == null || !_registry.Verify(machine, credential))
        {
            await ApiResults.Error(401, "unknown_machine", "This machine isn't paired with this hub (or its credential is wrong). Pair it again.").ExecuteAsync(http);
            return;
        }

        using var socket = await http.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        var link = new Link { MachineId = _registry.Get(machine)!.Id, Channel = new LinkChannel(socket) };
        if (_links.TryGetValue(link.MachineId, out var previous)) { await previous.Channel.CloseAsync(LinkChannel.Codes.Replaced, "Replaced by a newer connection."); }
        _links[link.MachineId] = link;
        _log($"Machine {_registry.Get(machine)?.Name} ({link.MachineId}) connected.");

        try
        {
            await link.Channel.RunAsync(m => OnMessageAsync(link, m), (kind, id, payload) => OnChunkAsync(link, kind, id, payload), http.RequestAborted);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { /* the member went away */ }
        finally
        {
            _links.TryRemove(new KeyValuePair<string, Link>(link.MachineId, link));
            foreach (var p in link.Pending.Values)
            {
                p.Head.TrySetException(new LinkLostException());
                p.Body.Writer.TryComplete(new LinkLostException());
            }
            _registry.Flush();
            if (!_links.ContainsKey(link.MachineId))
            {
                _stream.PublishMachine(link.MachineId, new { online = false });
                _log($"Machine {_registry.Get(link.MachineId)?.Name ?? link.MachineId} disconnected.");
            }
            await link.Channel.CloseAsync(WebSocketCloseStatus.NormalClosure, null);
        }
    }

    private async Task OnMessageAsync(Link link, JsonObject m)
    {
        switch ((string?)m["t"])
        {
            case "hello":
                link.Version = (string?)m["version"];
                _registry.Seen(link.MachineId, (string?)m["name"], link.Version, null, null);
                _stream.PublishMachine(link.MachineId, new { online = true, version = link.Version });
                await SendWatchAsync(link);
                break;
            case "snapshot":
                List<ServerDto>? servers = null;
                HostMetricsDto? metrics = null;
                try
                {
                    servers = m["servers"]?.Deserialize<List<ServerDto>>(LinkChannel.Json);
                    metrics = m["metrics"]?.Deserialize<HostMetricsDto>(LinkChannel.Json);
                }
                catch (JsonException) { /* ignore a malformed snapshot */ }
                _registry.Seen(link.MachineId, null, null, servers, metrics);
                break;
            case "event":
                var vis = m["vis"]?.Deserialize<Visibility>(LinkChannel.Json) ?? Visibility.Admin;
                _stream.PublishRemote(link.MachineId, (string?)m["type"] ?? "unknown", (string?)m["topic"] ?? "servers", (string?)m["server"], vis,
                    m["data"]?.DeepClone(), m["at"] is JsonNode at ? at.GetValue<DateTimeOffset>() : DateTimeOffset.UtcNow);
                break;
            case "res":
                if (link.Pending.TryGetValue((int?)m["id"] ?? -1, out var p)) { p.Head.TrySetResult(m); }
                break;
            case "resend":
                if (link.Pending.TryGetValue((int?)m["id"] ?? -1, out var done))
                {
                    string? error = (string?)m["error"];
                    done.Head.TrySetException(new LinkLostException());
                    done.Body.Writer.TryComplete(error == null ? null : new IOException(error));
                }
                break;
        }
    }

    private async Task OnChunkAsync(Link link, byte kind, int id, ReadOnlyMemory<byte> payload)
    {
        if (kind != LinkChannel.ResponseBody || !link.Pending.TryGetValue(id, out var p)) { return; }
        try { await p.Body.Writer.WriteAsync(payload.ToArray()); }
        catch (ChannelClosedException) { /* the browser gave up on this response */ }
    }

    private void UpdateWatches(IReadOnlyDictionary<string, HashSet<string>> watches)
    {
        _watches = watches;
        foreach (var link in _links.Values) { _ = SendWatchAsync(link); }
    }

    private Task SendWatchAsync(Link link)
    {
        var servers = _watches.TryGetValue(link.MachineId, out var set) ? set.ToArray() : Array.Empty<string>();
        return SafeSend(link, new { t = "watch", servers });
    }

    private static async Task SafeSend(Link link, object message)
    {
        try { await link.Channel.SendAsync(message); } catch { /* link closing */ }
    }

    // ───────────────────────────── Forwarding ─────────────────────────────

    /// <summary>Runs one API request on a member machine and streams the answer back to the browser.</summary>
    public async Task ForwardAsync(HttpContext http, string machine, AgentUser user)
    {
        if (!_links.TryGetValue(machine, out var link))
        {
            await ApiResults.Error(503, "machine_offline", $"{_registry.Get(machine)?.Name ?? machine} is offline right now.").ExecuteAsync(http);
            return;
        }

        int id = Interlocked.Increment(ref link.NextId);
        var pending = new Pending();
        link.Pending[id] = pending;
        var abort = http.RequestAborted;
        try
        {
            var req = http.Request;
            bool hasBody = req.ContentLength > 0 || (req.ContentLength == null && (HttpMethods.IsPost(req.Method) || HttpMethods.IsPut(req.Method) || HttpMethods.IsPatch(req.Method)));
            await link.Channel.SendAsync(new
            {
                t = "req", id, method = req.Method, path = req.Path.Value + req.QueryString.Value,
                contentType = req.ContentType, hasBody,
                user = Delegation.Encode(user, machine, _ctx.Settings.MachineName),
                ip = AgentContext.Ip(http),
            }, abort);

            if (hasBody)
            {
                // Allow big uploads through the hub, as on the machine itself.
                var size = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
                if (size is { IsReadOnly: false }) { size.MaxRequestBodySize = ServerDataEndpoints.MaxUploadBytes; }
                byte[] buffer = new byte[LinkChannel.ChunkSize];
                int n;
                while ((n = await req.Body.ReadAsync(buffer, abort)) > 0)
                {
                    await link.Channel.SendChunkAsync(LinkChannel.RequestBody, id, buffer.AsMemory(0, n), abort);
                }
            }
            await link.Channel.SendAsync(new { t = "reqend", id }, abort);

            JsonObject head;
            try { head = await pending.Head.Task.WaitAsync(HeaderTimeout, abort); }
            catch (TimeoutException)
            {
                await ApiResults.Error(504, "machine_timeout", "The machine didn't answer in time.").ExecuteAsync(http);
                return;
            }

            http.Response.StatusCode = (int?)head["status"] ?? 502;
            if (head["headers"] is JsonObject headers)
            {
                foreach (string name in CopiedResponseHeaders)
                {
                    if (headers[name] is JsonNode value) { http.Response.Headers[name] = (string?)value; }
                }
            }
            await foreach (byte[] chunk in pending.Body.Reader.ReadAllAsync(abort))
            {
                await http.Response.Body.WriteAsync(chunk, abort);
            }
        }
        catch (OperationCanceledException) when (abort.IsCancellationRequested)
        {
            _ = SafeSend(link, new { t = "cancel", id });
        }
        catch (Exception ex) when (ex is LinkLostException or IOException or WebSocketException or ChannelClosedException)
        {
            if (!http.Response.HasStarted) { await ApiResults.Error(502, "machine_disconnected", "The machine disconnected while handling this request.").ExecuteAsync(http); }
            else { http.Abort(); } // a half-sent download must not look complete
        }
        finally
        {
            link.Pending.TryRemove(id, out _);
            pending.Body.Writer.TryComplete();
        }
    }

    /// <summary>Drops a member's connection; with <paramref name="unpaired"/> it's told to forget this hub.</summary>
    public async Task DisconnectAsync(string machine, bool unpaired)
    {
        if (_links.TryGetValue(machine, out var link))
        {
            await link.Channel.CloseAsync(unpaired ? LinkChannel.Codes.Unpaired : WebSocketCloseStatus.NormalClosure, unpaired ? "Removed from the hub." : null);
        }
    }
}
