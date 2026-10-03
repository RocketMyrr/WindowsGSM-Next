using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsGSM.Agent.Api;
using WindowsGSM.Agent.Notifications;
using WindowsGSM.Agent.Realtime;
using WindowsGSM.Engine.Events;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Engine.Services;

namespace WindowsGSM.Agent.Hosting;

/// <summary>
/// One "if this, then that" rule.
/// Trigger: empty (no players for Minutes), cpu (above Threshold % for Minutes), memory (above Threshold MB for
/// Minutes), crashed, joined (someone joins an empty server), disk (a drive WindowsGSM uses has under Threshold GB
/// free for Minutes — this machine, not a server; it can only notify).
/// Action: notify, stop, restart, backup, command (Command is sent to the console / RCON).
/// Servers: the ids it watches; empty = every server on this machine.
/// </summary>
public sealed class AutomationRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Trigger { get; set; } = "empty";
    public int Minutes { get; set; } = 60;
    public double Threshold { get; set; }
    public string Action { get; set; } = "notify";
    public string? Command { get; set; }
    public List<string> Servers { get; set; } = new();
    /// <summary>Don't fire again for the same server within this many minutes.</summary>
    public int CooldownMinutes { get; set; } = 30;
    public DateTimeOffset? LastFired { get; set; }
    public string? LastResult { get; set; }
}

/// <summary>
/// Automations: watches live samples (players, CPU, memory) and server events, and acts when a rule's condition
/// has held long enough — stop an empty server, restart one stuck at full CPU, tell you about a crash with its
/// last lines. Rules live in configs/next/automations.json (per machine) and act with the agent's own rights;
/// every action is in the server's log, the audit log and the notification centre.
/// </summary>
public sealed class Automations : IDisposable
{
    public static readonly string[] Triggers = { "empty", "cpu", "memory", "crashed", "joined", "disk" };
    public static readonly string[] Actions = { "notify", "stop", "restart", "backup", "command" };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly AgentContext _ctx;
    private readonly NotificationCentre _notes;
    private readonly string _file;
    private readonly object _gate = new();
    private readonly IDisposable _subscription;
    private List<AutomationRule> _rules = new();
    // (rule, server) → since when the condition holds; and when it last fired.
    private readonly Dictionary<(string Rule, string Server), DateTimeOffset> _since = new();
    private readonly Dictionary<(string Rule, string Server), DateTimeOffset> _fired = new();
    private readonly Dictionary<string, int> _lastPlayers = new();

    public Automations(AgentContext ctx, NotificationCentre notes)
    {
        _ctx = ctx;
        _notes = notes;
        _file = Path.Combine(global::WindowsGSM.Hosting.WgsmEnvironment.DataRoot, "configs", "next", "automations.json");
        try { if (File.Exists(_file)) { _rules = JsonSerializer.Deserialize<List<AutomationRule>>(File.ReadAllText(_file), Json) ?? new(); } }
        catch { _rules = new(); }
        _subscription = ctx.Engine.Events.Subscribe(OnEvent);
        _diskTimer = new Timer(_ => { try { CheckDisks(); } catch { /* next minute */ } }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private readonly Timer _diskTimer;

    /// <summary>Tests: the drives to watch, as (name, free bytes).</summary>
    public Func<IEnumerable<(string Drive, long Free)>>? DrivesOverride { get; set; }

    /// <summary>"Low disk space": every drive with the data folder or a server on it, checked once a minute.</summary>
    public void CheckDisks()
    {
        List<AutomationRule> rules;
        lock (_gate) { rules = _rules.Where(r => r.Enabled && r.Trigger == "disk").ToList(); }
        if (rules.Count == 0) { return; }
        var drives = (DrivesOverride?.Invoke() ?? Drives()).ToList();
        var now = Clock();
        foreach (var r in rules)
        {
            foreach (var (drive, free) in drives)
            {
                var key = (r.Id, "disk:" + drive);
                DateTimeOffset since;
                lock (_gate)
                {
                    if (free >= r.Threshold * 1024L * 1024 * 1024) { _since.Remove(key); continue; }
                    if (!_since.TryGetValue(key, out since)) { _since[key] = since = now; }
                }
                if (now - since < TimeSpan.FromMinutes(r.Minutes)) { continue; }
                lock (_gate) { _since.Remove(key); }
                ActLater(r, "", $"Drive {drive} has only {free / 1073741824.0:0.#} GB free (under {r.Threshold:0} GB).", withConsole: false, key: "disk:" + drive);
            }
        }
    }

    private IEnumerable<(string, long)> Drives()
    {
        var roots = new[] { global::WindowsGSM.Hosting.WgsmEnvironment.DataRoot }
            .Concat(_ctx.Engine.Servers.All.Select(s => WindowsGSM.Functions.ServerPath.GetServers(s.Id)))
            .Concat(_ctx.Engine.Servers.All.Select(s => WindowsGSM.Functions.ServerLocation.RealPath(s.Id))) // game files on another drive
            .Select(p => { try { return Path.GetPathRoot(Path.GetFullPath(p)); } catch { return null; } })
            .Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots!)
        {
            DriveInfo d;
            try { d = new DriveInfo(root); if (!d.IsReady) { continue; } } catch { continue; }
            yield return (d.Name, d.AvailableFreeSpace);
        }
    }

    /// <summary>Tests: "now".</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    public IReadOnlyList<AutomationRule> All() { lock (_gate) { return _rules.Select(Copy).ToList(); } }

    /// <summary>Adds or replaces a rule. Returns a problem, or null.</summary>
    public string? Save(AutomationRule rule)
    {
        string? problem = Check(rule);
        if (problem != null) { return problem; }
        lock (_gate)
        {
            var existing = _rules.FindIndex(r => r.Id == rule.Id);
            if (existing >= 0) { rule.LastFired = _rules[existing].LastFired; rule.LastResult = _rules[existing].LastResult; _rules[existing] = Copy(rule); }
            else { _rules.Add(Copy(rule)); }
            foreach (var k in _since.Keys.Where(k => k.Rule == rule.Id).ToList()) { _since.Remove(k); }
            Persist();
        }
        return null;
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            int removed = _rules.RemoveAll(r => r.Id == id);
            if (removed > 0) { Persist(); }
            return removed > 0;
        }
    }

    public static string? Check(AutomationRule r)
    {
        if (!Triggers.Contains(r.Trigger)) { return $"\"{r.Trigger}\" isn't a trigger."; }
        if (!Actions.Contains(r.Action)) { return $"\"{r.Action}\" isn't an action."; }
        if (r.Trigger is "empty" or "cpu" or "memory" or "disk" && (r.Minutes < 1 || r.Minutes > 7 * 24 * 60)) { return "Pick how long, between 1 minute and a week."; }
        if (r.Trigger == "disk" && (r.Threshold < 1 || r.Threshold > 100_000)) { return "Free space is in GB (at least 1)."; }
        if (r.Trigger == "disk" && r.Action != "notify") { return "Low disk space can only notify you (it's about the machine, not one server)."; }
        if (r.Trigger == "cpu" && (r.Threshold <= 0 || r.Threshold > 100)) { return "CPU is a percentage between 1 and 100."; }
        if (r.Trigger == "memory" && r.Threshold < 64) { return "Memory is in MB (at least 64)."; }
        if (r.Action == "command" && string.IsNullOrWhiteSpace(r.Command)) { return "Type the command to send."; }
        if (r.Command != null && r.Command.IndexOfAny(new[] { '\r', '\n' }) >= 0) { return "The command must be one line."; }
        if (r.Trigger is "crashed" && r.Action is "stop") { return "A crashed server is already stopped."; }
        if (r.CooldownMinutes < 0 || r.CooldownMinutes > 7 * 24 * 60) { return "The pause between runs must be between 0 minutes and a week."; }
        if ((r.Name ?? "").Length > 80) { return "Keep the name under 80 characters."; }
        return null;
    }

    // ───────────────────────────── Watching ─────────────────────────────

    private void OnEvent(EngineEvent e)
    {
        try
        {
            switch (e)
            {
                case ServerMetricsSampled m: OnSample(m.ServerId, m.Sample); break;
                case ServerAlert { Kind: AlertKind.Crashed } a: Fire("crashed", a.ServerId, $"{Name(a.ServerId)} crashed.", withConsole: true); break;
                case ServerStateChanged { To: not ServerState.Running } s:
                    lock (_gate) { foreach (var k in _since.Keys.Where(k => k.Server == s.ServerId).ToList()) { _since.Remove(k); } _lastPlayers.Remove(s.ServerId); }
                    break;
            }
        }
        catch { /* never let a rule break the event stream */ }
    }

    private void OnSample(string server, ServerSample sample)
    {
        var now = Clock();
        List<AutomationRule> rules;
        lock (_gate) { rules = _rules.Where(r => r.Enabled && Applies(r, server)).ToList(); }

        // Someone joined an empty server (from the last known count to more than zero).
        if (sample.Players is int players)
        {
            int before;
            lock (_gate) { before = _lastPlayers.TryGetValue(server, out int b) ? b : -1; _lastPlayers[server] = players; }
            if (before == 0 && players > 0)
            {
                foreach (var r in rules.Where(r => r.Trigger == "joined")) { ActLater(r, server, $"Someone joined {Name(server)} ({players} online).", withConsole: false); }
            }
        }

        foreach (var r in rules.Where(r => r.Trigger is "empty" or "cpu" or "memory"))
        {
            bool holds = r.Trigger switch
            {
                "empty" => sample.Players == 0,          // unknown (no query answer) never counts as empty
                "cpu" => sample.CpuPercent >= r.Threshold,
                "memory" => sample.MemoryMb >= r.Threshold,
                _ => false,
            };
            var key = (r.Id, server);
            DateTimeOffset since;
            lock (_gate)
            {
                if (!holds) { _since.Remove(key); continue; }
                if (!_since.TryGetValue(key, out since)) { _since[key] = since = now; }
            }
            if (now - since < TimeSpan.FromMinutes(r.Minutes)) { continue; }
            lock (_gate) { _since.Remove(key); } // the clock starts again after acting
            string what = r.Trigger switch
            {
                "empty" => $"{Name(server)} has had no players for {Words(r.Minutes)}.",
                "cpu" => $"{Name(server)} used over {r.Threshold:0}% CPU for {Words(r.Minutes)}.",
                _ => $"{Name(server)} used over {r.Threshold:0} MB of memory for {Words(r.Minutes)}.",
            };
            ActLater(r, server, what, withConsole: false);
        }
    }

    private void Fire(string trigger, string server, string what, bool withConsole)
    {
        List<AutomationRule> rules;
        lock (_gate) { rules = _rules.Where(r => r.Enabled && r.Trigger == trigger && Applies(r, server)).ToList(); }
        foreach (var r in rules) { ActLater(r, server, what, withConsole); }
    }

    private static bool Applies(AutomationRule r, string server) => r.Servers.Count == 0 || r.Servers.Contains(server, StringComparer.OrdinalIgnoreCase);

    // ───────────────────────────── Acting ─────────────────────────────

    /// <summary>Off the event thread: sending a command can wait on RCON.</summary>
    private void ActLater(AutomationRule r, string server, string what, bool withConsole, string? key = null) =>
        _ = Task.Run(() => { try { Act(r, server, what, withConsole, key); } catch { /* reported in the next run */ } });

    private void Act(AutomationRule r, string server, string what, bool withConsole, string? cooldownKey = null)
    {
        var now = Clock();
        var key = (r.Id, cooldownKey ?? server);
        lock (_gate)
        {
            if (_fired.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(r.CooldownMinutes)) { return; }
            _fired[key] = now;
        }
        string label = string.IsNullOrWhiteSpace(r.Name) ? "Automation" : r.Name.Trim();
        var engine = _ctx.Engine;
        string? error = null;
        string done;
        switch (r.Action)
        {
            case "stop": error = engine.Lifecycle.Stop(server).Error; done = "stopped it"; break;
            case "restart": error = engine.Lifecycle.Restart(server).Error; done = "restarted it"; break;
            case "backup": error = engine.Backups.Backup(server).Error; done = "started a backup"; break;
            case "command":
                var sent = engine.Console.SendAsync(server, r.Command!, "Automation").GetAwaiter().GetResult();
                error = sent.Sent ? null : sent.Error;
                done = $"sent \"{r.Command}\"";
                break;
            default: done = "let you know"; break;
        }

        string result = error == null ? $"{what} → {done}." : $"{what} → couldn't: {error}";
        bool machineWide = server.Length == 0; // e.g. low disk space
        engine.Log.Write(machineWide ? "Automation" : server, $"[Automation] {label}: {result}");
        string text = result;
        if (withConsole)
        {
            string tail = string.Join("\n", WindowsGSM.Functions.ServerConsole.For(server).Get().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(15));
            if (tail.Length > 0) { text += "\n\nLast lines:\n" + tail; }
        }
        _notes.Record("automation", machineWide ? label : $"{label}: {Name(server)}", text, _ctx.MachineId, machineWide ? null : server, machineWide ? null : Name(server), Visibility.Admin);
        lock (_gate)
        {
            var stored = _rules.FirstOrDefault(x => x.Id == r.Id);
            if (stored != null) { stored.LastFired = now; stored.LastResult = result; Persist(); }
        }
    }

    private string Name(string id) => _ctx.Engine.Servers.Get(id)?.Name ?? $"Server #{id}";

    private static string Words(int minutes) => minutes % 60 == 0 && minutes >= 60
        ? $"{minutes / 60} hour{(minutes == 60 ? "" : "s")}" : $"{minutes} minute{(minutes == 1 ? "" : "s")}";

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        string temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_rules, Json));
        File.Move(temp, _file, overwrite: true);
    }

    private static AutomationRule Copy(AutomationRule r) => new()
    {
        Id = r.Id, Name = r.Name ?? "", Enabled = r.Enabled, Trigger = r.Trigger, Minutes = r.Minutes, Threshold = r.Threshold, Action = r.Action,
        Command = string.IsNullOrWhiteSpace(r.Command) ? null : r.Command.Trim(), Servers = r.Servers?.ToList() ?? new(),
        CooldownMinutes = r.CooldownMinutes, LastFired = r.LastFired, LastResult = r.LastResult,
    };

    public void Dispose() { _subscription.Dispose(); _diskTimer.Dispose(); }
}
