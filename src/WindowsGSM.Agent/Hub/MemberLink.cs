using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using WindowsGSM.Agent.Api;
using WindowsGSM.Agent.Realtime;
using WindowsGSM.Agent.Security;
using WindowsGSM.Contracts;
using WindowsGSM.Engine.Events;
using WindowsGSM.Hosting;

namespace WindowsGSM.Agent.Hub;

public enum LinkState { NotJoined, Connecting, Connected, Offline, Removed }

/// <summary>
/// The member's side of multi-machine: this agent keeps one outbound connection to its hub (so no ports need
/// opening here), answers the requests the hub relays by replaying them against its own API over loopback, and
/// streams its events up. Reconnects with back-off; stops for good if the hub removes this machine.
/// </summary>
public sealed class MemberLink : IAsyncDisposable
{
    private static readonly TimeSpan SnapshotEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SnapshotDebounce = TimeSpan.FromSeconds(1);
    private static readonly Regex MachineSegment = new(@"^/api/v2/machines/[^/?]+", RegexOptions.Compiled);

    /// <summary>Opens the WebSocket to the hub (replaceable in tests).</summary>
    public delegate Task<WebSocket> Connector(Uri uri, string authorization, string? pinnedThumbprint, CancellationToken token);

    private readonly AgentContext _ctx;
    private readonly Func<HttpClient> _local;
    private readonly Connector _connect;
    private readonly Action<string> _log;
    private readonly string _machineId;
    private readonly AgentSettings _settings;
    private CancellationTokenSource? _run;
    private Task? _loop;
    private HashSet<string> _watched = new();
    private readonly ConcurrentDictionary<int, (Pipe? Body, CancellationTokenSource Cancel)> _requests = new();

    public MemberLink(AgentContext ctx, Func<HttpClient> localApi, Connector? connect, Action<string> log, string? machineIdOverride = null, AgentSettings? settings = null)
    {
        _settings = settings ?? ctx.Settings;
        _ctx = ctx;
        _local = localApi;
        _connect = connect ?? ConnectAsync;
        _log = log;
        _machineId = machineIdOverride ?? ctx.MachineId;
    }

    public LinkState State { get; private set; } = LinkState.NotJoined;
    public string? LastError { get; private set; }
    public DateTimeOffset? ConnectedSince { get; private set; }

    public bool Joined => !string.IsNullOrWhiteSpace(_settings.HubUrl) && !string.IsNullOrWhiteSpace(_settings.HubCredential);

    public void Start()
    {
        if (!Joined || _loop != null) { if (!Joined) { State = LinkState.NotJoined; } return; }
        _run = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_run.Token));
    }

    public async Task StopAsync()
    {
        _run?.Cancel();
        if (_loop != null) { try { await _loop; } catch { /* stopping */ } }
        _loop = null;
        _run = null;
    }

    // ───────────────────────────── Joining ─────────────────────────────

    /// <summary>
    /// Pairs this machine with a hub using a code shown on the hub. For an https hub with a certificate this
    /// machine doesn't trust (self-signed), the certificate seen now is pinned and required from then on.
    /// </summary>
    public async Task<string?> JoinAsync(string hubUrl, string code, HttpMessageHandler? handler = null)
    {
        if (!Uri.TryCreate(hubUrl.Trim().TrimEnd('/'), UriKind.Absolute, out var hub) || (hub.Scheme != "http" && hub.Scheme != "https"))
        {
            return "Enter the hub's address, like https://game-box-a:8971.";
        }
        string? thumbprint = null;
        using var http = new HttpClient(handler ?? new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            {
                if (errors != SslPolicyErrors.None && cert != null) { thumbprint = cert.GetCertHashString(); }
                return true; // trust on first use: pinned below
            },
        }) { BaseAddress = hub, Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Add("X-WGSM-CSRF", "1");
        HttpResponseMessage res;
        try
        {
            res = await http.PostAsJsonAsync("/api/v2/hub/pair", new { code, machineId = _machineId, machineName = _settings.MachineName, version = WgsmEnvironment.Version });
        }
        catch (Exception ex) { return $"Couldn't reach the hub at {hub}: {ex.Message}"; }
        var body = await res.Content.ReadFromJsonAsync<JsonObject>();
        if (!res.IsSuccessStatusCode) { return (string?)body?["error"] ?? $"The hub refused ({(int)res.StatusCode})."; }

        await StopAsync();
        var s = _settings;
        s.HubUrl = hub.ToString().TrimEnd('/');
        s.HubCredential = (string?)body?["credential"];
        s.HubName = (string?)body?["hubName"];
        s.HubCertThumbprint = hub.Scheme == "https" ? thumbprint : null;
        s.Save();
        _log($"Joined the hub {s.HubName} at {s.HubUrl}.");
        Start();
        return null;
    }

    public async Task LeaveAsync()
    {
        await StopAsync();
        var s = _settings;
        s.HubUrl = s.HubCredential = s.HubName = s.HubCertThumbprint = null;
        s.Save();
        State = LinkState.NotJoined;
        LastError = null;
    }

    // ───────────────────────────── Connection loop ─────────────────────────────

    private async Task RunAsync(CancellationToken token)
    {
        int attempt = 0;
        while (!token.IsCancellationRequested && Joined)
        {
            State = LinkState.Connecting;
            WebSocketCloseStatus? closed = null;
            try
            {
                var uri = new Uri(Regex.Replace(_settings.HubUrl!, "^http", "ws") + "/api/v2/hub/link");
                using var socket = await _connect(uri, $"WGSM-Machine {_machineId}:{_settings.HubCredential}", _settings.HubCertThumbprint, token);
                await using var channel = new LinkChannel(socket);
                attempt = 0;
                State = LinkState.Connected;
                ConnectedSince = DateTimeOffset.UtcNow;
                LastError = null;
                _log($"Connected to the hub {_settings.HubName}.");
                closed = (await ServeAsync(channel, token)).Status;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { LastError = ex.Message; }
            finally
            {
                ConnectedSince = null;
                foreach (var r in _requests.Values) { r.Cancel.Cancel(); r.Body?.Writer.Complete(); }
                _requests.Clear();
            }

            if (closed == LinkChannel.Codes.Unpaired)
            {
                _log("The hub removed this machine; no longer linked.");
                var s = _settings;
                s.HubUrl = s.HubCredential = s.HubCertThumbprint = null;
                s.Save();
                State = LinkState.Removed;
                return;
            }
            State = LinkState.Offline;
            LastError ??= "Disconnected from the hub.";
            var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(attempt++, 6))));
            try { await Task.Delay(delay, token); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task<(WebSocketCloseStatus? Status, string? Description)> ServeAsync(LinkChannel channel, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var outbox = Channel.CreateBounded<object>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        // A server changed: send a fresh snapshot soon, so the hub's copy (what it shows if this machine drops
        // off) is never much older than the last change.
        var nudge = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

        // Engine events → the hub. Console lines only for consoles someone is watching on the hub.
        using var subscription = _ctx.Engine.Events.Subscribe(e =>
        {
            if (e is Engine.Events.ServerStateChanged or Engine.Events.ServerListChanged or Engine.Events.ServerConfigChanged) { nudge.Writer.TryWrite(true); }
            var m = EventMapper.Map(e, _ctx);
            if (m == null || (m.IsConsole && !_watched.Contains(m.Server ?? ""))) { return; }
            outbox.Writer.TryWrite(new { t = "event", type = m.Type, topic = m.Topic, server = m.Server, at = e.At, data = m.Data, vis = m.Visibility });
        });

        var sender = Task.Run(async () =>
        {
            await foreach (object msg in outbox.Reader.ReadAllAsync(stop.Token)) { await channel.SendAsync(msg, stop.Token); }
        });
        var snapshots = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                outbox.Writer.TryWrite(Snapshot());
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                await Task.WhenAny(Task.Delay(SnapshotEvery, wait.Token), nudge.Reader.WaitToReadAsync(wait.Token).AsTask());
                wait.Cancel();
                if (nudge.Reader.TryRead(out _)) { await Task.Delay(SnapshotDebounce, stop.Token); } // one snapshot per burst of changes
            }
        });

        await channel.SendAsync(new { t = "hello", machine = _machineId, name = _settings.MachineName, version = WgsmEnvironment.Version }, token);
        try
        {
            return await channel.RunAsync(m => OnMessageAsync(channel, m, stop.Token), OnChunkAsync, stop.Token);
        }
        finally
        {
            stop.Cancel();
            outbox.Writer.TryComplete();
            await Task.WhenAll(sender.ContinueWith(_ => { }), snapshots.ContinueWith(_ => { }));
        }
    }

    private object Snapshot()
    {
        var system = new AgentUser { Username = "hub", Role = Contracts.Role.Owner, Enabled = true };
        var servers = _ctx.Engine.Servers.All.Select(s => _ctx.ToDto(s, system)).ToList();
        return new { t = "snapshot", servers, metrics = _ctx.Metrics.Sample() };
    }

    private Task OnMessageAsync(LinkChannel channel, JsonObject m, CancellationToken token)
    {
        switch ((string?)m["t"])
        {
            case "req":
                int id = (int?)m["id"] ?? 0;
                bool hasBody = (bool?)m["hasBody"] ?? false;
                var entry = (hasBody ? new Pipe() : null, CancellationTokenSource.CreateLinkedTokenSource(token));
                _requests[id] = entry;
                _ = Task.Run(() => ExecuteAsync(channel, id, m, entry.Item1, entry.Item2.Token));
                break;
            case "reqend":
                if (_requests.TryGetValue((int?)m["id"] ?? 0, out var r) && r.Body != null) { r.Body.Writer.Complete(); }
                break;
            case "cancel":
                if (_requests.TryRemove((int?)m["id"] ?? 0, out var c)) { c.Cancel.Cancel(); c.Body?.Writer.Complete(); }
                break;
            case "watch":
                _watched = new HashSet<string>(m["servers"]?.AsArray().Select(x => (string?)x ?? "") ?? Enumerable.Empty<string>());
                break;
        }
        return Task.CompletedTask;
    }

    private async Task OnChunkAsync(byte kind, int id, ReadOnlyMemory<byte> payload)
    {
        if (kind == LinkChannel.RequestBody && _requests.TryGetValue(id, out var r) && r.Body != null)
        {
            await r.Body.Writer.WriteAsync(payload);
        }
    }

    /// <summary>Replays one relayed request against this agent's own API and streams the response back.</summary>
    private async Task ExecuteAsync(LinkChannel channel, int id, JsonObject m, Pipe? body, CancellationToken token)
    {
        bool headSent = false;
        try
        {
            // This machine only serves itself: whatever id the hub used, it's "local" here.
            string path = MachineSegment.Replace((string?)m["path"] ?? "/", "/api/v2/machines/local");
            using var request = new HttpRequestMessage(new HttpMethod((string?)m["method"] ?? "GET"), path);
            request.Headers.Add(AgentContext.InternalHeader, _ctx.InternalSecret);
            request.Headers.Add(AgentContext.DelegateHeader, (string?)m["user"] ?? "");
            request.Headers.Add("X-WGSM-CSRF", "1");
            if ((string?)m["ip"] is string ip && ip.Length > 0) { request.Headers.Add(AgentContext.ClientIpHeader, ip); }
            if (body != null)
            {
                request.Content = new StreamContent(body.Reader.AsStream());
                if ((string?)m["contentType"] is string ct && ct.Length > 0) { request.Content.Headers.TryAddWithoutValidation("Content-Type", ct); }
            }

            using var client = _local();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            var headers = new Dictionary<string, string>();
            foreach (var h in response.Headers.Concat(response.Content.Headers)) { headers[h.Key] = string.Join(", ", h.Value); }
            await channel.SendAsync(new { t = "res", id, status = (int)response.StatusCode, headers }, token);
            headSent = true;

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            byte[] buffer = new byte[LinkChannel.ChunkSize];
            int n;
            while ((n = await stream.ReadAsync(buffer, token)) > 0)
            {
                await channel.SendChunkAsync(LinkChannel.ResponseBody, id, buffer.AsMemory(0, n), token);
            }
            await channel.SendAsync(new { t = "resend", id }, token);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            try
            {
                if (!headSent)
                {
                    var err = new ApiError($"The machine couldn't handle this request: {ex.Message}", "machine_error");
                    await channel.SendAsync(new { t = "res", id, status = 502, headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" } });
                    await channel.SendChunkAsync(LinkChannel.ResponseBody, id, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(err, LinkChannel.Json));
                    await channel.SendAsync(new { t = "resend", id });
                }
                else { await channel.SendAsync(new { t = "resend", id, error = ex.Message }); }
            }
            catch { /* the link is gone */ }
        }
        catch (OperationCanceledException) { /* cancelled by the hub or the link closed */ }
        finally
        {
            _requests.TryRemove(id, out _);
        }
    }

    // ───────────────────────────── Real connection ─────────────────────────────

    private static async Task<WebSocket> ConnectAsync(Uri uri, string authorization, string? pinnedThumbprint, CancellationToken token)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", authorization);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        ws.Options.RemoteCertificateValidationCallback = (_, cert, _, errors) =>
            errors == SslPolicyErrors.None
            || (pinnedThumbprint != null && cert != null && string.Equals(new X509Certificate2(cert).Thumbprint, pinnedThumbprint, StringComparison.OrdinalIgnoreCase));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await ws.ConnectAsync(uri, timeout.Token);
            return ws;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
