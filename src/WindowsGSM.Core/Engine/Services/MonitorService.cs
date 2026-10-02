#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowsGSM.Engine.Events;
using WindowsGSM.Engine.Servers;
using WindowsGSM.GameServer.Query;

namespace WindowsGSM.Engine.Services
{
    /// <summary>Monitor tuning. Health defaults are the legacy values.</summary>
    public sealed class MonitorOptions
    {
        /// <summary>How often players, CPU and RAM are sampled (legacy query loop: 5 s).</summary>
        public TimeSpan Tick { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Treat a server that stops answering its game query as hung and restart it. Off by default, like
        /// legacy ("A2S query check"). Uses each game's own query protocol — legacy always used A2S, so games
        /// that don't speak A2S would have been killed as "unresponsive".
        /// </summary>
        public bool HealthChecks { get; set; } = Functions.AppSettings.GetBool("A2SQueryCheck", false);

        public TimeSpan HealthInterval { get; set; } = TimeSpan.FromSeconds(45);
        public TimeSpan HealthGrace { get; set; } = TimeSpan.FromSeconds(300);   // boot time before probing
        public int HealthFailuresBeforeRestart { get; set; } = 4;

        /// <summary>How much per-server CPU/RAM history is kept in memory for charts.</summary>
        public TimeSpan HistoryLength { get; set; } = TimeSpan.FromHours(1);
    }

    /// <summary>A live sample for one running server.</summary>
    public sealed record ServerSample(DateTimeOffset At, double CpuPercent, double MemoryMb, int? Players, int? MaxPlayers);

    /// <summary>Published every monitor tick for each running server.</summary>
    public sealed record ServerMetricsSampled(string ServerId, ServerSample Sample) : EngineEvent;

    /// <summary>Published when the connected-player list changes.</summary>
    public sealed record PlayersChanged(string ServerId, IReadOnlyList<PlayerData> Players) : EngineEvent;

    /// <summary>
    /// How the player query is doing, for the panel ("why don't I see players?").
    /// </summary>
    /// <param name="Protocol">A2S, EOS, UT3, FiveM… or null when the game has no query.</param>
    /// <param name="Endpoint">The address:port being asked (null until known).</param>
    /// <param name="Answering">The last tries got an answer.</param>
    /// <param name="Note">Plain words: a fallback port in use, nothing answering and what to check, etc.</param>
    public sealed record QueryStatus(string? Protocol, string? Endpoint, bool Answering, DateTimeOffset? LastAnswer, string? Note);

    /// <summary>
    /// Watches running servers on a timer: player counts and lists (each game's own query protocol), CPU
    /// and RAM, the hung-server health check, and the memory guard. Port of the legacy StartQuery loop,
    /// RunServerHealthChecks, RunMemoryGuardChecks and CaptureServerResourceSamples — as one loop owned by
    /// the engine instead of one loop per server hanging off the window.
    /// </summary>
    public sealed class MonitorService : IDisposable
    {
        private sealed class Watch
        {
            public int ProcessId;
            public dynamic? Query;
            public bool QueryUsesGamePort;
            public int? QueryPort;          // the port that answered (may differ from the configured one)
            public string? QueryHost;
            public int QueryFailures;       // in a row
            public DateTimeOffset? LastAnswer;
            public DateTimeOffset NextDiscovery;
            public string? QueryNote;
            public int? LastPlayers, LastMaxPlayers;
            public volatile bool Discovering;
            public TimeSpan LastCpu;
            public DateTimeOffset LastCpuAt;
            public DateTimeOffset LastHealthProbe;
            public int HealthFailures;
            public string PlayersSignature = string.Empty;
            public IReadOnlyList<PlayerData> Players = Array.Empty<PlayerData>();
            public readonly List<ServerSample> History = new List<ServerSample>();
        }

        private readonly ServerRegistry _servers;
        private readonly PluginCatalog _plugins;
        private readonly LifecycleService _lifecycle;
        private readonly ServerLog _log;
        private readonly EventBus _events;
        private readonly ConcurrentDictionary<string, Watch> _watches = new ConcurrentDictionary<string, Watch>(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _loop;

        public MonitorService(ServerRegistry servers, PluginCatalog plugins, LifecycleService lifecycle, ServerLog log, EventBus events, MonitorOptions? options = null)
        {
            _servers = servers;
            _plugins = plugins;
            _lifecycle = lifecycle;
            _log = log;
            _events = events;
            Options = options ?? new MonitorOptions();
        }

        public MonitorOptions Options { get; }

        /// <summary>Latest sample for a server, or null if it isn't running / hasn't been sampled yet.</summary>
        public ServerSample? Latest(string id)
        {
            // Live numbers only mean something while the server runs — never show a stopped server's last values.
            var s = _servers.Get(id);
            if (s == null || s.State != ServerState.Running) { return null; }
            return _watches.TryGetValue(id, out var w) && w.ProcessId == s.Process?.Id ? LockedLast(w) : null;
        }

        /// <summary>The last player list the game reported (empty when stopped or not yet known).</summary>
        public IReadOnlyList<PlayerData> Players(string id)
        {
            var s = _servers.Get(id);
            if (s == null || s.State != ServerState.Running) { return Array.Empty<PlayerData>(); }
            return _watches.TryGetValue(id, out var w) && w.ProcessId == s.Process?.Id ? w.Players : Array.Empty<PlayerData>();
        }

        /// <summary>Recent samples (oldest first) for charts.</summary>
        public IReadOnlyList<ServerSample> History(string id)
        {
            if (!_watches.TryGetValue(id, out var w)) { return Array.Empty<ServerSample>(); }
            lock (w.History) { return w.History.ToList(); }
        }

        public void Start()
        {
            if (_loop != null) { return; }
            _loop = new CancellationTokenSource();
            var token = _loop.Token;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try { await TickAsync().ConfigureAwait(false); }
                    catch (Exception ex) { Debug.WriteLine($"[Monitor] tick failed: {ex}"); }
                    try { await Task.Delay(Options.Tick, token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                }
            });
        }

        public void Dispose()
        {
            _loop?.Cancel();
            _loop = null;
        }

        /// <summary>One pass over every server. Public so hosts/tests can drive it without the timer.</summary>
        public async Task TickAsync()
        {
            var running = new List<(ServerInstance server, Process process)>();
            foreach (var s in _servers.All)
            {
                var p = s.Process;
                bool alive;
                try { alive = p != null && !p.HasExited && s.State == ServerState.Running; } catch { alive = false; }
                if (!alive)
                {
                    s.MemoryOverThresholdSince = null;
                    if (_watches.TryGetValue(s.Id, out var stale)) { stale.HealthFailures = 0; }
                    continue;
                }
                running.Add((s, p!));
            }

            // Servers are sampled in parallel so one slow query (up to its timeout) doesn't delay the rest.
            await Task.WhenAll(running.Select(r => SampleAsync(r.server, r.process))).ConfigureAwait(false);
        }

        private async Task SampleAsync(ServerInstance s, Process p)
        {
            var now = DateTimeOffset.Now;
            var w = _watches.AddOrUpdate(s.Id, _ => NewWatch(s, p), (_, existing) => existing.ProcessId == p.Id ? existing : NewWatch(s, p));

            // ── CPU and RAM ──
            double cpu = 0, memMb = 0;
            try
            {
                p.Refresh();
                memMb = p.WorkingSet64 / 1024.0 / 1024.0;
                var total = p.TotalProcessorTime;
                if (w.LastCpuAt != default)
                {
                    double wall = (now - w.LastCpuAt).TotalMilliseconds * Environment.ProcessorCount;
                    if (wall > 0) { cpu = Math.Clamp((total - w.LastCpu).TotalMilliseconds / wall * 100.0, 0, 100); }
                }
                w.LastCpu = total;
                w.LastCpuAt = now;
            }
            catch { return; } // process went away mid-sample

            // ── Players (and health) via the game's own query ──
            int? players = null, maxPlayers = null;
            bool? answered = null;
            if (w.Query != null)
            {
                try { (answered, players, maxPlayers) = await QueryAsync(s, w, now).ConfigureAwait(false); }
                catch { answered = false; }
            }

            var sample = new ServerSample(now, Math.Round(cpu, 1), Math.Round(memMb, 0), players, maxPlayers);
            lock (w.History)
            {
                w.History.Add(sample);
                w.History.RemoveAll(x => now - x.At > Options.HistoryLength);
            }
            _events.Publish(new ServerMetricsSampled(s.Id, sample));

            CheckHealth(s, p, w, answered, now);
            CheckMemoryGuard(s, memMb, now);
        }

        /// <summary>The player query's state, for the panel.</summary>
        public QueryStatus QueryState(string id)
        {
            var s = _servers.Get(id);
            if (s == null || s.State != ServerState.Running || !_watches.TryGetValue(id, out var w) || w.ProcessId != s.Process?.Id)
            {
                return new QueryStatus(null, null, false, null, null);
            }
            if (w.Query == null) { return new QueryStatus(null, null, false, null, "This game doesn't offer a player query, so player counts aren't available."); }
            string endpoint = w.QueryHost != null && w.QueryPort != null ? $"{w.QueryHost}:{w.QueryPort}" : null!;
            return new QueryStatus(((object)w.Query).GetType().Name, endpoint, w.QueryFailures == 0 && w.LastAnswer != null, w.LastAnswer, w.QueryNote);
        }

        /// <summary>A missed answer or two keep the last numbers (UDP drops packets); after this many, they're cleared.</summary>
        private const int KeepLastFor = 3;

        /// <summary>
        /// One query. The legacy app asked ServerIP:QueryPort and nothing else, so player counts silently vanished
        /// when either was off — and both often are: ServerIP is commonly the PUBLIC address (for the join
        /// address), which most routers won't answer from inside; and games pick their own query port. Here:
        /// this machine's own address is asked (loopback unless the server is bound to one specific local IP),
        /// and when the configured port doesn't answer, the usual alternatives are tried and the one that
        /// answers is remembered — with a note saying so.
        /// </summary>
        private async Task<(bool? Answered, int? Players, int? Max)> QueryAsync(ServerInstance s, Watch w, DateTimeOffset now)
        {
            string host = QueryHost(s.Config.ServerIP);
            int? configured = int.TryParse(w.QueryUsesGamePort ? s.Config.ServerPort : s.Config.ServerQueryPort, out int cp) && cp is > 0 and < 65536 ? cp : null;
            if (w.QueryHost != host) { w.QueryHost = host; w.QueryPort = null; }

            int? port = w.QueryPort ?? configured;
            (int, int)? result = port == null ? null : await AskAsync((object)w.Query!, host, port.Value).ConfigureAwait(false);

            // Nothing on the port we used: look for the one that answers — in the background (each try waits for a
            // timeout), at most once a minute, with its own query object so it never trips over the regular one.
            if (result == null && !w.Discovering && now >= w.NextDiscovery && (w.QueryFailures + 1 >= 2 || port == null))
            {
                w.NextDiscovery = now.AddMinutes(1);
                StartDiscovery(s, w, host, configured, port);
            }

            if (result is (int cur, int max))
            {
                if (port == configured) { w.QueryNote = null; } // a fallback port keeps its note
                w.QueryPort = port;
                w.QueryFailures = 0;
                w.LastAnswer = now;
                w.LastPlayers = cur;
                w.LastMaxPlayers = max;
                await RefreshPlayerListAsync(s, w).ConfigureAwait(false);
                return (true, cur, max);
            }

            w.QueryFailures++;
            if (w.QueryPort == null) { w.QueryPort = configured; }
            if (w.QueryFailures >= KeepLastFor)
            {
                w.LastPlayers = w.LastMaxPlayers = null;
                if (w.LastAnswer == null || now - w.LastAnswer > TimeSpan.FromMinutes(2))
                {
                    w.QueryNote = $"Nothing answers the player query on {host}:{w.QueryPort?.ToString() ?? "(no port set)"}. The server may still be starting; " +
                                  "otherwise check the query port in Settings (and that the game's own config enables queries).";
                }
            }
            // A missed packet keeps the last numbers for a few samples instead of flickering to "—".
            return (false, w.LastPlayers, w.LastMaxPlayers);
        }

        private void StartDiscovery(ServerInstance s, Watch w, string host, int? configured, int? current)
        {
            dynamic? probe;
            try { probe = Activator.CreateInstance(((object)w.Query!).GetType()); } catch { return; }
            if (probe == null) { return; }
            w.Discovering = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (int candidate in Candidates(s, configured).Where(c => c != current))
                    {
                        if (await AskAsync((object)probe, host, candidate).ConfigureAwait(false) == null) { continue; }
                        if (candidate != configured)
                        {
                            string where = configured == null ? "no query port is set" : $"the query port in Settings is {configured}";
                            w.QueryNote = $"The game answers queries on port {candidate}, but {where} — using {candidate}. Set the query port to {candidate} in Settings to make it stick.";
                            _log.Write(s.Id, $"[NOTICE] Player query: {w.QueryNote}");
                        }
                        w.QueryPort = candidate;
                        return;
                    }
                }
                catch { /* try again in a minute */ }
                finally { w.Discovering = false; }
            });
        }

        private static async Task<(int, int)?> AskAsync(object target, string host, int port)
        {
            dynamic query = target;
            try
            {
                query.SetAddressPort(host, port, 2);
                string? text = await query.GetPlayersAndMaxPlayers();
                if (text == null) { return null; }
                var parts = text.Split('/');
                return parts.Length == 2 && int.TryParse(parts[0], out int cur) && int.TryParse(parts[1], out int max) ? (cur, max) : null;
            }
            catch { return null; }
        }

        /// <summary>Where else games usually answer: the game port, the port after it, the Source default.</summary>
        private static IEnumerable<int> Candidates(ServerInstance s, int? configured)
        {
            var list = new List<int>();
            void Add(int p) { if (p is > 0 and < 65536 && !list.Contains(p)) { list.Add(p); } }
            if (configured != null) { Add(configured.Value); }
            if (int.TryParse(s.Config.ServerQueryPort, out int q)) { Add(q); }
            if (int.TryParse(s.Config.ServerPort, out int g)) { Add(g); Add(g + 1); Add(g + 2); Add(g + 15); }
            Add(27015);
            return list;
        }

        /// <summary>
        /// The address to ask: loopback, unless the server is bound to one specific address of THIS machine (then
        /// that one). A public or otherwise foreign address in ServerIP is for players, not for us.
        /// </summary>
        public static string QueryHost(string? serverIp)
        {
            if (string.IsNullOrWhiteSpace(serverIp) || !System.Net.IPAddress.TryParse(serverIp.Trim(), out var ip)) { return "127.0.0.1"; }
            if (System.Net.IPAddress.IsLoopback(ip) || ip.Equals(System.Net.IPAddress.Any)) { return "127.0.0.1"; }
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) { continue; }
                    if (nic.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(ip))) { return ip.ToString(); }
                }
            }
            catch { /* can't tell */ }
            return "127.0.0.1";
        }

        private async Task RefreshPlayerListAsync(ServerInstance s, Watch w)
        {
            if (w.Query is not QueryTemplate template) { return; }
            List<PlayerData>? list;
            try { list = await template.GetPlayersData().ConfigureAwait(false); } catch { return; }
            list ??= new List<PlayerData>();
            string signature = string.Join("|", list.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal));
            if (signature == w.PlayersSignature) { return; }
            w.PlayersSignature = signature;
            w.Players = list;
            _events.Publish(new PlayersChanged(s.Id, list));
        }

        /// <summary>
        /// Legacy health check, using the game's own query: after the boot grace period, probe at most every
        /// HealthInterval; after N consecutive silent probes the process is alive but hung — kill it and let
        /// the crash handler (and auto-restart) take over.
        /// </summary>
        private void CheckHealth(ServerInstance s, Process p, Watch w, bool? answered, DateTimeOffset now)
        {
            if (!Options.HealthChecks || answered == null) { return; }
            DateTimeOffset started;
            try { started = p.StartTime; } catch { return; }
            if (now - started < Options.HealthGrace || now - w.LastHealthProbe < Options.HealthInterval) { return; }
            w.LastHealthProbe = now;

            if (answered == true) { w.HealthFailures = 0; return; }
            w.HealthFailures++;
            _log.Write(s.Id, $"[Health] Server unresponsive to query ({w.HealthFailures}/{Options.HealthFailuresBeforeRestart}).");
            if (w.HealthFailures < Options.HealthFailuresBeforeRestart) { return; }

            w.HealthFailures = 0;
            _log.Write(s.Id, "[Health] Server process alive but unresponsive — treating as crashed.");
            try { if (!p.HasExited) { p.Kill(entireProcessTree: true); } } catch { /* already gone */ }
        }

        /// <summary>Legacy memory guard: restart a server whose memory stays over its threshold for its sustain period.</summary>
        private void CheckMemoryGuard(ServerInstance s, double memMb, DateTimeOffset now)
        {
            var cfg = s.Config;
            if (!cfg.MemoryGuard || cfg.MemoryGuardThresholdMb <= 0 || cfg.MemoryGuardSustainMinutes <= 0 || memMb < cfg.MemoryGuardThresholdMb)
            {
                s.MemoryOverThresholdSince = null;
                return;
            }
            if (s.MemoryOverThresholdSince == null)
            {
                s.MemoryOverThresholdSince = now;
                _log.Write(s.Id, $"[Memory Guard] Working set {memMb:0} MB exceeded threshold {cfg.MemoryGuardThresholdMb} MB — monitoring.");
                return;
            }
            if ((now - s.MemoryOverThresholdSince.Value).TotalMinutes < cfg.MemoryGuardSustainMinutes) { return; }

            s.MemoryOverThresholdSince = null;
            _log.Write(s.Id, $"[Memory Guard] Memory above {cfg.MemoryGuardThresholdMb} MB for {cfg.MemoryGuardSustainMinutes}+ min ({memMb:0} MB) — restarting server.");
            _events.Publish(new ServerAlert(s.Id, AlertKind.MemoryGuard, $"{s.Name} — memory guard restart",
                $"Memory stayed above {cfg.MemoryGuardThresholdMb} MB for {cfg.MemoryGuardSustainMinutes}+ minutes."));
            var request = _lifecycle.Restart(s.Id);
            if (!request.Accepted) { _log.Write(s.Id, $"[Memory Guard] Restart skipped: {request.Error}"); }
        }

        private Watch NewWatch(ServerInstance s, Process p)
        {
            var w = new Watch { ProcessId = p.Id };
            try
            {
                dynamic? game = _plugins.Create(s.Game, s.Config);
                object? query = game == null ? null : Dyn.Get((object)game, "QueryMethod");
                w.Query = query;
                w.QueryUsesGamePort = query is EOS; // EOS queries the game port (legacy behaviour)
            }
            catch { /* no query support */ }
            return w;
        }

        private static ServerSample? LockedLast(Watch w)
        {
            lock (w.History) { return w.History.Count == 0 ? null : w.History[^1]; }
        }
    }
}
