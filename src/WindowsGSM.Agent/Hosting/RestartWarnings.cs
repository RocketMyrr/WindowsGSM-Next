using System.Text.Json;
using WindowsGSM.Agent.Api;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Engine.Services;

namespace WindowsGSM.Agent.Hosting;

/// <summary>How a server warns its players before a scheduled restart, update or stop.</summary>
public sealed class WarningSettings
{
    public bool Enabled { get; set; }
    /// <summary>How long before, in seconds (e.g. 300 and 60 for "in 5 minutes" and "in 1 minute").</summary>
    public List<int> Leads { get; set; } = new() { 300, 60 };
    /// <summary>The game's broadcast command; {message} is replaced. Most games: "say {message}".</summary>
    public string Command { get; set; } = "say {message}";
    /// <summary>{action} → "restarting" / "updating" / "shutting down"; {time} → "5 minutes".</summary>
    public string Message { get; set; } = "Server {action} in {time}.";
}

/// <summary>
/// Posts "Server restarting in 5 minutes." (and 1 minute…) into a server's chat before each scheduled restart,
/// update or stop — from its own schedules, the legacy restart setting and crontab files alike. Settings are
/// the agent's (configs/next/restart-warnings.json), per server.
/// </summary>
public sealed class RestartWarnings : IDisposable
{
    public static readonly int[] AllowedLeads = { 1800, 900, 600, 300, 120, 60, 30, 10 };
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly AgentContext _ctx;
    private readonly string _file;
    private readonly object _gate = new();
    private Dictionary<string, WarningSettings> _settings;
    private readonly HashSet<string> _sent = new();
    private readonly Timer _timer;

    public RestartWarnings(AgentContext ctx)
    {
        _ctx = ctx;
        _file = Path.Combine(ctx.ConfigDir, "restart-warnings.json");
        try { _settings = File.Exists(_file) ? JsonSerializer.Deserialize<Dictionary<string, WarningSettings>>(File.ReadAllText(_file)) ?? new() : new(); }
        catch { _settings = new(); }
        _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        _subscription = ctx.Engine.Events.Subscribe<WindowsGSM.Engine.Events.ServerListChanged>(e => { if (e.Removed) { Forget(e.ServerId); } });
    }

    private readonly IDisposable _subscription;

    public WarningSettings Get(string server)
    {
        lock (_gate) { return _settings.TryGetValue(server, out var s) ? Copy(s) : new WarningSettings(); }
    }

    public string? Set(string server, WarningSettings s)
    {
        var leads = (s.Leads ?? new()).Distinct().OrderByDescending(x => x).ToList();
        if (leads.Any(l => !AllowedLeads.Contains(l))) { return "Pick warning times from the list."; }
        if (s.Enabled && leads.Count == 0) { return "Pick at least one warning time."; }
        string command = (s.Command ?? "").Trim();
        if (!command.Contains("{message}")) { return "The command needs {message} where the text goes, e.g. say {message}."; }
        if (command.Length > 200 || command.IndexOfAny(new[] { '\r', '\n' }) >= 0) { return "Keep the command to one short line."; }
        string message = (s.Message ?? "").Trim();
        if (message.Length == 0 || message.Length > 200 || message.IndexOfAny(new[] { '\r', '\n' }) >= 0) { return "Write a short one-line message."; }
        lock (_gate)
        {
            _settings[server] = new WarningSettings { Enabled = s.Enabled, Leads = leads, Command = command, Message = message };
            try
            {
                File.WriteAllText(_file + ".tmp", JsonSerializer.Serialize(_settings, Json));
                File.Move(_file + ".tmp", _file, overwrite: true);
            }
            catch (Exception ex) { return $"Couldn't save: {ex.Message}"; }
        }
        return null;
    }

    public void Forget(string server) { lock (_gate) { _settings.Remove(server); } }

    // ───────────────────────────── Sending ─────────────────────────────

    public static string ActionWord(ScheduledAction a) => a switch
    {
        ScheduledAction.Restart => "restarting",
        ScheduledAction.Update => "updating",
        _ => "shutting down",
    };

    public static string TimeWords(int seconds) =>
        seconds >= 60 ? $"{seconds / 60} minute{(seconds / 60 == 1 ? "" : "s")}" : $"{seconds} second{(seconds == 1 ? "" : "s")}";

    public static string Compose(WarningSettings s, ScheduledAction action, int lead) =>
        s.Command.Replace("{message}", s.Message.Replace("{action}", ActionWord(action)).Replace("{time}", TimeWords(lead)));

    /// <summary>
    /// Which warnings are due now for an event at <paramref name="at"/>. A warning is only sent close to its
    /// moment — if the agent (re)started after it was due, saying "in 5 minutes" two minutes out would be wrong.
    /// </summary>
    public static IEnumerable<int> Due(DateTime now, DateTime at, IEnumerable<int> leads) =>
        leads.Where(l => now >= at.AddSeconds(-l) && now < at && now - at.AddSeconds(-l) < TimeSpan.FromSeconds(30));

    private async Task TickAsync()
    {
        List<(string Server, WarningSettings Settings)> active;
        lock (_gate) { active = _settings.Where(kv => kv.Value.Enabled).Select(kv => (kv.Key, Copy(kv.Value))).ToList(); }
        DateTime now = _ctx.Engine.Scheduler.Clock();
        foreach (var (id, s) in active)
        {
            var server = _ctx.Engine.Servers.Get(id);
            if (server == null || server.State != ServerState.Running) { continue; }
            foreach (var e in _ctx.Engine.Scheduler.GetSchedules(id))
            {
                if (!e.Enabled || e.Action is not (ScheduledAction.Restart or ScheduledAction.Update or ScheduledAction.Stop)) { continue; }
                if (_ctx.Engine.Scheduler.NextOccurrence(e, now.AddSeconds(-1)) is not DateTime at) { continue; }
                foreach (int lead in Due(now, at, s.Leads))
                {
                    string key = $"{id}|{e.Cron}|{e.Action}|{at:O}|{lead}";
                    lock (_gate) { if (!_sent.Add(key)) { continue; } if (_sent.Count > 5000) { _sent.Clear(); _sent.Add(key); } }
                    try { await _ctx.Engine.Console.SendAsync(id, Compose(s, e.Action, lead), "Restart warning", false); }
                    catch { /* the next warning may get through */ }
                }
            }
        }
    }

    public async Task<string?> SendTestAsync(string server)
    {
        var s = Get(server);
        var result = await _ctx.Engine.Console.SendAsync(server, s.Command.Replace("{message}", "Test: this is how restart warnings will look."), "Restart warning test", false);
        return result.Sent ? null : result.Error ?? "The server didn't take the command.";
    }

    private static WarningSettings Copy(WarningSettings s) => new() { Enabled = s.Enabled, Leads = s.Leads.ToList(), Command = s.Command, Message = s.Message };

    public void Dispose() { _timer.Dispose(); _subscription.Dispose(); }
}
