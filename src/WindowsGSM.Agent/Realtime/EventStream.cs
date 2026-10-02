using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using WindowsGSM.Agent.Api;
using WindowsGSM.Agent.Security;
using WindowsGSM.Contracts;
using WindowsGSM.Engine.Events;
using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Services;

namespace WindowsGSM.Agent.Realtime;

/// <summary>
/// The live feed: every engine event, numbered and pushed to connected browsers over one WebSocket
/// (/api/v2/events). Replaces all of the legacy dashboard's polling.
///
/// Protocol — server first says <c>hello</c> (current seq). The client sends
/// <c>{"type":"subscribe","topics":["servers","jobs","console:local/3"],"since":1234}</c> (again at any time to
/// change topics). With <c>since</c>, events it missed that are still in the recent-events buffer are
/// replayed first; if the gap is too big it gets <c>reset</c> and should refetch over REST. Each viewer only
/// ever receives events for servers it may see (console lines need the Console right).
/// </summary>
public sealed class EventStream : IDisposable
{
    public const int BufferSize = 10_000;
    private const int ClientQueue = 2_000;
    private static readonly TimeSpan SessionCheck = TimeSpan.FromSeconds(30);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>One numbered event, serialized once and shared by every client it goes to.</summary>
    private sealed record Entry(long Seq, string Topic, Func<AgentUser, bool> Visible, byte[] Payload);

    private sealed class Client
    {
        public required AgentUser User;
        public required string? SessionId;
        public HashSet<string> Topics = new(StringComparer.OrdinalIgnoreCase);
        public bool Subscribed;
        public volatile bool Overflowed;
        public readonly Channel<byte[]> Queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(ClientQueue)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait, // TryWrite fails when full → Overflowed
        });
        public bool Wants(Entry e) => Subscribed && Topics.Contains(e.Topic) && e.Visible(User);
    }

    private readonly AgentContext _ctx;
    private readonly object _gate = new();
    private readonly Entry?[] _ring = new Entry?[BufferSize];
    private readonly List<Client> _clients = new();
    private readonly IDisposable _subscription;
    private long _seq;

    public EventStream(AgentContext ctx)
    {
        _ctx = ctx;
        _subscription = ctx.Engine.Events.Subscribe(OnEngineEvent);
    }

    public long CurrentSeq { get { lock (_gate) { return _seq; } } }

    public int ClientCount { get { lock (_gate) { return _clients.Count; } } }

    // ───────────────────────────── Engine → entries ─────────────────────────────

    private void OnEngineEvent(EngineEvent e)
    {
        var m = EventMapper.Map(e, _ctx);
        if (m == null) { return; }
        string machine = _ctx.MachineId;
        Add(machine, m.Type, EventMapper.TopicFor(m, machine), m.Server, m.Visibility, m.Visibility.For(machine, m.Server, _ctx.CanSeeJob), m.Data, e.At);
    }

    /// <summary>
    /// An event from a member machine (relayed by the hub link): numbered into this stream like a local one,
    /// and shown only to users allowed to see it on that machine.
    /// </summary>
    public void PublishRemote(string machine, string type, string topic, string? server, Visibility visibility, object? data, DateTimeOffset at)
    {
        if (type == "console") { topic = $"console:{machine}/{server}"; }
        Add(machine, type, topic, server, visibility, visibility.For(machine, server), data, at);
    }

    /// <summary>A machine-level notice (a member connected or went away) for everyone who can see that machine.</summary>
    public void PublishMachine(string machine, object data) =>
        Add(machine, "machine", "servers", null, Visibility.All, _ => true, data, DateTimeOffset.UtcNow);

    /// <summary>An event the agent itself raises (not from an engine), e.g. a new notification.</summary>
    public void Publish(string machine, string type, string topic, string? server, Visibility visibility, object? data) =>
        Add(machine, type, topic, server, visibility, visibility.For(machine, server), data, DateTimeOffset.UtcNow);

    /// <summary>Every event, local or relayed, after it's been numbered and sent (outside the stream's lock).</summary>
    public event Action<StreamEvent>? Published;

    /// <summary>Raised when the set of remote console topics anyone is watching changes (machine → server ids).</summary>
    public event Action<IReadOnlyDictionary<string, HashSet<string>>>? RemoteWatchesChanged;

    private void Add(string machine, string type, string topic, string? server, Visibility visibility, Func<AgentUser, bool> visible, object? data, DateTimeOffset at)
    {
        lock (_gate)
        {
            long seq = ++_seq;
            var envelope = new EventEnvelope(seq, type, machine, server, at, data);
            var entry = new Entry(seq, topic, visible, JsonSerializer.SerializeToUtf8Bytes(envelope, Json));
            _ring[seq % BufferSize] = entry;
            foreach (var c in _clients) { Offer(c, entry); }
        }
        var published = Published;
        if (published != null && type != "console" && type != "metrics")
        {
            try { published(new StreamEvent(machine, type, server, visibility, data, at)); }
            catch { /* a listener's problem, never the stream's */ }
        }
    }

    private static void Offer(Client c, Entry e)
    {
        if (!c.Wants(e)) { return; }
        bool ok;
        try { ok = c.Queue.Writer.TryWrite(e.Payload); } catch { ok = false; } // user filters run engine lookups
        if (!ok) { c.Overflowed = true; }
    }

    // ───────────────────────────── Connections ─────────────────────────────

    public async Task HandleAsync(HttpContext http)
    {
        if (!http.WebSockets.IsWebSocketRequest) { await ApiResults.BadRequest("This address only speaks WebSocket.").ExecuteAsync(http); return; }
        var user = _ctx.CurrentUser(http);
        if (user == null) { await ApiResults.Unauthorized().ExecuteAsync(http); return; }

        // Browsers send cookies on cross-site WebSocket handshakes and CORS doesn't apply, so check the
        // origin ourselves: only pages served by this agent may open the stream.
        string origin = http.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var o)
            || !string.Equals(o.Authority, http.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
        {
            await ApiResults.Forbidden("Cross-site connections aren't allowed.").ExecuteAsync(http);
            return;
        }

        using var socket = await http.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        var client = new Client { User = user, SessionId = AgentContext.SessionId(http) };
        lock (_gate) { _clients.Add(client); }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        try
        {
            await SendAsync(socket, Envelope("hello", new { seq = CurrentSeq, machine = _ctx.MachineId, user = user.Username }), stop.Token);
            var sending = SendLoopAsync(socket, client, stop.Token);
            var watching = WatchSessionAsync(socket, client, stop.Token);
            await ReceiveLoopAsync(socket, client, stop.Token);
            stop.Cancel();
            await Task.WhenAll(sending.ContinueWith(_ => { }), watching.ContinueWith(_ => { }));
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { /* client went away */ }
        finally
        {
            lock (_gate) { _clients.Remove(client); }
            NotifyWatches();
            client.Queue.Writer.TryComplete();
            // Finish the close handshake — including when the client started it (CloseReceived), or the
            // client's CloseAsync waits forever for our reply.
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token); } catch { /* gone */ }
            }
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket, Client client, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close) { return; }
                message.Write(buffer, 0, result.Count);
                if (message.Length > 64 * 1024) { await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too big.", token); return; }
            } while (!result.EndOfMessage);

            SubscribeMessage? msg;
            try { msg = JsonSerializer.Deserialize<SubscribeMessage>(message.ToArray(), Json); } catch { msg = null; }
            if (msg == null || !string.Equals(msg.Type, "subscribe", StringComparison.OrdinalIgnoreCase))
            {
                // Queued, not sent: only the send loop may write to the socket.
                client.Queue.Writer.TryWrite(Envelope("error", new { message = "Send {\"type\":\"subscribe\",\"topics\":[…]}." }));
                continue;
            }
            Subscribe(client, msg);
            NotifyWatches();
        }
    }

    private string _lastWatches = string.Empty;

    /// <summary>Tells the hub link which remote consoles anyone is watching (only those are streamed up).</summary>
    private void NotifyWatches()
    {
        var watches = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (string t in _clients.SelectMany(c => c.Topics))
            {
                if (!t.StartsWith("console:", StringComparison.OrdinalIgnoreCase)) { continue; }
                string rest = t.Substring(8);
                int slash = rest.IndexOf('/');
                if (slash <= 0) { continue; }
                string machine = rest.Substring(0, slash);
                if (string.Equals(machine, _ctx.MachineId, StringComparison.OrdinalIgnoreCase)) { continue; }
                if (!watches.TryGetValue(machine, out var set)) { watches[machine] = set = new HashSet<string>(); }
                set.Add(rest.Substring(slash + 1));
            }
        }
        string key = string.Join(";", watches.OrderBy(w => w.Key).Select(w => w.Key + ":" + string.Join(",", w.Value.OrderBy(x => x))));
        if (key == _lastWatches) { return; }
        _lastWatches = key;
        RemoteWatchesChanged?.Invoke(watches);
    }

    private void Subscribe(Client client, SubscribeMessage msg)
    {
        // "local" is this machine: console:local/3 == console:{machineId}/3.
        var topics = (msg.Topics ?? Array.Empty<string>())
            .Select(t => t.Replace(":local/", $":{_ctx.MachineId}/", StringComparison.OrdinalIgnoreCase))
            .Take(100)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            bool first = !client.Subscribed;
            client.Topics = topics;
            client.Subscribed = true;
            if (!first || msg.Since is not long since) { return; }

            // Replay what was missed — under the lock, so live events queue strictly after it.
            long oldest = Math.Max(1, _seq - BufferSize + 1);
            if (since + 1 < oldest)
            {
                client.Queue.Writer.TryWrite(Envelope("reset", new { seq = _seq, reason = "Too many events were missed — refetch, then carry on from this seq." }));
                return;
            }
            for (long s = since + 1; s <= _seq; s++)
            {
                if (_ring[s % BufferSize] is { } e && e.Seq == s) { Offer(client, e); }
            }
        }
    }

    private async Task SendLoopAsync(WebSocket socket, Client client, CancellationToken token)
    {
        var reader = client.Queue.Reader;
        while (await reader.WaitToReadAsync(token))
        {
            if (client.Overflowed)
            {
                // This viewer fell too far behind (slow connection, huge console burst): drop the backlog and
                // tell it to refetch rather than let memory grow.
                while (reader.TryRead(out _)) { }
                client.Overflowed = false;
                await SendAsync(socket, Envelope("reset", new { seq = CurrentSeq, reason = "Fell behind — refetch, then carry on from this seq." }), token);
                continue;
            }
            while (reader.TryRead(out var payload)) { await SendAsync(socket, payload, token); }
        }
    }

    /// <summary>Closes the stream when the session is revoked or the account disabled; picks up permission changes.</summary>
    private async Task WatchSessionAsync(WebSocket socket, Client client, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(SessionCheck, token);
            var fresh = _ctx.Users.Get(client.User.Username);
            if (fresh is not { Enabled: true } || !_ctx.Sessions.IsValid(client.SessionId, client.User.Username))
            {
                await socket.CloseAsync((WebSocketCloseStatus)4401, "Signed out.", CancellationToken.None);
                return;
            }
            lock (_gate) { client.User = fresh; }
        }
    }

    private static byte[] Envelope(string type, object data) =>
        JsonSerializer.SerializeToUtf8Bytes(new EventEnvelope(0, type, null, null, DateTimeOffset.UtcNow, data), Json);

    private static Task SendAsync(WebSocket socket, byte[] payload, CancellationToken token) =>
        socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, token);

    public void Dispose()
    {
        _subscription.Dispose();
        lock (_gate) { foreach (var c in _clients) { c.Queue.Writer.TryComplete(); } }
    }
}
