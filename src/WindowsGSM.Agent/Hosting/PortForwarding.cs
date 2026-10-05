using System.Text.Json;
using WindowsGSM.Agent.Api;
using WindowsGSM.Engine.Events;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Functions;

namespace WindowsGSM.Agent.Hosting;

/// <summary>
/// "Forward ports on the router" per server (UPnP): the game and query ports, UDP and TCP, are forwarded to this
/// machine when the server starts (and every 30 minutes while it runs, since routers forget after a reboot), and
/// removed when it's turned off or the server is deleted. What was forwarded is remembered
/// (configs\next\upnp.json) so old ports are cleaned up after a port change. RCON is never forwarded.
/// Setting (WindowsGSM.cfg): upnp = "1".
/// </summary>
public sealed class PortForwarding : IDisposable
{
    public const string SettingKey = "upnp";

    public sealed record Entry(int Port, string Protocol, bool Ok, string? Error);
    public sealed record Status(bool Enabled, string? Router, string? ExternalIp, string? LocalIp, IReadOnlyList<Entry> Entries, DateTimeOffset? At, string? Problem);

    private readonly AgentContext _ctx;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private readonly string _file;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly Dictionary<string, List<(int Port, string Protocol)>> _applied;
    private readonly Dictionary<string, Status> _status = new();
    private readonly IDisposable _subscription;
    private readonly Timer _refresh;
    private (IPortGateway? Gateway, string? Problem, DateTimeOffset At) _cached;

    /// <summary>Tests: a fake router instead of discovering one.</summary>
    public Func<CancellationToken, Task<(IPortGateway?, string?)>>? DiscoverOverride { get; set; }

    public PortForwarding(AgentContext ctx)
    {
        _ctx = ctx;
        _file = Path.Combine(global::WindowsGSM.Hosting.WgsmEnvironment.DataRoot, "configs", "next", "upnp.json");
        _applied = (global::WindowsGSM.Hosting.SafeJson.Read<Dictionary<string, List<int[]>>>(_file) ?? new())
            .ToDictionary(kv => kv.Key, kv => (kv.Value ?? new()).Where(a => a != null && a.Length == 2).Select(a => (a[0], a[1] == 6 ? "TCP" : "UDP")).ToList());
        _subscription = ctx.Engine.Events.Subscribe(OnEvent);
        _refresh = new Timer(_ => _ = RefreshAllAsync(), null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
    }

    public static bool Enabled(ServerInstance s) => s.Config.GetCustomSetting(SettingKey, "") == "1";

    /// <summary>The ports to forward: game and query port, UDP and TCP.</summary>
    public static List<(int Port, string Protocol)> PortsFor(ServerInstance s)
    {
        var ports = new[] { s.Config.ServerPort, s.Config.ServerQueryPort }
            .Select(p => int.TryParse(p, out int n) ? n : 0).Where(p => p is > 0 and < 65536).Distinct();
        return ports.SelectMany(p => new[] { (p, "UDP"), (p, "TCP") }).ToList();
    }

    public Status Get(string id)
    {
        var s = _ctx.Engine.Servers.Get(id);
        lock (_gate)
        {
            var st = _status.GetValueOrDefault(id);
            bool on = s != null && Enabled(s);
            return st == null ? new Status(on, null, null, null, Array.Empty<Entry>(), null, null) : st with { Enabled = on };
        }
    }

    /// <summary>Turns forwarding on (and forwards now) or off (and removes what was forwarded).</summary>
    public async Task<Status> SetAsync(string id, bool on, CancellationToken token = default)
    {
        ServerConfig.SetSetting(id, SettingKey, on ? "1" : "");
        _ctx.Engine.Servers.Get(id)?.ReloadConfig();
        return on ? await ApplyAsync(id, token) : await RemoveAsync(id, token);
    }

    /// <summary>Forwards the server's ports (and removes ones it no longer uses).</summary>
    public async Task<Status> ApplyAsync(string id, CancellationToken token = default)
    {
        var s = _ctx.Engine.Servers.Get(id);
        if (s == null) { return Get(id); }
        await _one.WaitAsync(token);
        try
        {
            var (gw, problem) = await GatewayAsync(token);
            if (gw == null) { return Save(id, new Status(true, null, null, null, Array.Empty<Entry>(), DateTimeOffset.UtcNow, problem)); }
            var want = PortsFor(s);
            List<(int, string)> before;
            lock (_gate) { before = _applied.GetValueOrDefault(id)?.ToList() ?? new(); }
            foreach (var (port, proto) in before.Except(want))
            {
                try { await gw.DeleteAsync(port, proto, token); } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { /* router busy: stays listed */ }
            }

            var entries = new List<Entry>();
            foreach (var (port, proto) in want)
            {
                string? error;
                try { error = await gw.AddAsync(port, proto, $"WindowsGSM #{s.Id} {s.Name}".Trim(), token); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { error = "The router didn't answer."; }
                entries.Add(new Entry(port, proto, error == null, error));
            }
            // Keep ports forwarded earlier even if re-forwarding failed this time — they may still be on the router.
            lock (_gate) { _applied[id] = want.Where(w => before.Contains(w) || entries.Any(e => e.Ok && e.Port == w.Port && e.Protocol == w.Protocol)).ToList(); }
            Persist();
            string? external = null;
            try { external = await gw.ExternalIpAsync(token); } catch { /* optional */ }
            var failed = entries.Where(e => !e.Ok).ToList();
            if (failed.Count > 0) { _ctx.Engine.Log.Write(id, $"[NOTICE] Router port forwarding: {failed[0].Error}"); }
            else { _ctx.Engine.Log.Write(id, $"Router: forwarded {string.Join(", ", want.Select(w => w.Port).Distinct())} (UDP/TCP) to {gw.LocalIp} with UPnP."); }
            return Save(id, new Status(true, gw.Name, external, gw.LocalIp, entries, DateTimeOffset.UtcNow, failed.Count > 0 ? failed[0].Error : null));
        }
        finally { _one.Release(); }
    }

    /// <summary>Removes everything forwarded for the server.</summary>
    public async Task<Status> RemoveAsync(string id, CancellationToken token = default)
    {
        List<(int Port, string Protocol)> before;
        lock (_gate) { before = _applied.GetValueOrDefault(id)?.ToList() ?? new(); }
        if (before.Count > 0)
        {
            await _one.WaitAsync(token);
            try
            {
                var (gw, _) = await GatewayAsync(token);
                if (gw != null)
                {
                    foreach (var (port, proto) in before) { try { await gw.DeleteAsync(port, proto, token); } catch { /* router gone */ } }
                    _ctx.Engine.Log.Write(id, "Router: port forwarding removed.");
                }
            }
            finally { _one.Release(); }
        }
        lock (_gate) { _applied.Remove(id); _status.Remove(id); }
        Persist();
        return Get(id);
    }

    private async Task<(IPortGateway?, string?)> GatewayAsync(CancellationToken token)
    {
        if (DiscoverOverride != null) { return await DiscoverOverride(token); }
        if (_cached.Gateway != null && DateTimeOffset.UtcNow - _cached.At < TimeSpan.FromMinutes(10)) { return (_cached.Gateway, null); }
        var (gw, problem) = await Upnp.DiscoverAsync(_http, TimeSpan.FromSeconds(3), token);
        _cached = (gw, problem, DateTimeOffset.UtcNow);
        return (gw, problem);
    }

    private Status Save(string id, Status st)
    {
        lock (_gate) { _status[id] = st; }
        return st;
    }

    private void Persist()
    {
        try
        {
            Dictionary<string, List<int[]>> data;
            lock (_gate) { data = _applied.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value.Select(p => new[] { p.Port, p.Protocol == "TCP" ? 6 : 17 }).ToList()); }
            global::WindowsGSM.Hosting.SafeJson.Write(_file, data);
        }
        catch { /* best effort */ }
    }

    private void OnEvent(EngineEvent e)
    {
        switch (e)
        {
            case ServerStateChanged { To: ServerState.Starting } c:
                var s = _ctx.Engine.Servers.Get(c.ServerId);
                if (s != null && Enabled(s))
                {
                    _ = Task.Run(async () =>
                    {
                        try { await ApplyAsync(c.ServerId); }
                        catch (Exception ex) { _ctx.Engine.Log.Write(c.ServerId, $"[NOTICE] Router port forwarding: {ex.Message}"); }
                    });
                }
                break;
            case ServerListChanged { Removed: true } r:
                _ = Task.Run(async () => { try { await RemoveAsync(r.ServerId); } catch { /* the router keeps them until it restarts */ } });
                break;
        }
    }

    private async Task RefreshAllAsync()
    {
        foreach (var s in _ctx.Engine.Servers.All.Where(s => s.State == ServerState.Running && Enabled(s)))
        {
            try { await ApplyAsync(s.Id); } catch { /* next time */ }
        }
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _refresh.Dispose();
        _http.Dispose();
    }
}
