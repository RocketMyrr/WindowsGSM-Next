using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsGSM.Agent.Api;
using WindowsGSM.Agent.Hub;
using WindowsGSM.Agent.Realtime;
using WindowsGSM.Agent.Security;

namespace WindowsGSM.Agent.Notifications;

/// <summary>One thing worth telling someone about. <see cref="Kind"/> is one of <see cref="NotificationKinds.All"/>.</summary>
public sealed record NotificationEntry(long Id, DateTimeOffset At, string Kind, string Severity, string Title, string Text,
    string Machine, string? Server, string? ServerName, Visibility Visibility);

/// <summary>The kinds of notification, their severity, and how they're described to people choosing rules.</summary>
public static class NotificationKinds
{
    public sealed record KindInfo(string Id, string Severity, string Label, string Group);

    public static readonly IReadOnlyList<KindInfo> All = new KindInfo[]
    {
        new("crashed", "bad", "A server crashed", "Problems"),
        new("crashLoop", "bad", "Auto-restart gave up (keeps crashing)", "Problems"),
        new("memoryGuard", "warn", "Restarted for using too much memory", "Problems"),
        new("jobFailed", "bad", "An install, update, backup or restore failed", "Problems"),
        new("appCrash", "bad", "The WindowsGSM agent crashed (and restarted)", "Problems"),
        new("machineOffline", "bad", "A machine went offline", "Machines"),
        new("machineOnline", "good", "A machine came back online", "Machines"),
        new("autoRestarted", "info", "Auto-restart brought a server back", "Activity"),
        new("autoStarted", "info", "A server started with the agent", "Activity"),
        new("scheduledRestart", "info", "Scheduled restart", "Activity"),
        new("updateAvailable", "info", "A game update is available", "Activity"),
        new("appUpdate", "info", "A new version of WindowsGSM is available", "Machines"),
        new("autoUpdated", "good", "Auto-update installed a new version", "Activity"),
        new("joinCode", "info", "Join code found (e.g. Palworld, Enshrouded)", "Activity"),
        new("automation", "info", "An automation ran", "Activity"),
    };

    public static KindInfo? Get(string id) => All.FirstOrDefault(k => k.Id == id);

    /// <summary>Engine alert kinds → notification kinds.</summary>
    public static string? FromAlert(string alertKind) => alertKind switch
    {
        "Crashed" => "crashed",
        "CrashLoopSuspended" => "crashLoop",
        "MemoryGuard" => "memoryGuard",
        "AutoRestarted" => "autoRestarted",
        "AutoStarted" => "autoStarted",
        "ScheduledRestart" => "scheduledRestart",
        "AutoUpdated" => "autoUpdated",
        "JoinCode" => "joinCode",
        _ => null,
    };
}

/// <summary>
/// The notification centre: remembers what happened across every machine this agent can see (server alerts,
/// failed jobs, machines dropping off), so it's still there when you open the panel later. Kept in
/// configs/next/notifications.json (the newest <see cref="Keep"/>), with each person's "read up to" marker.
/// New entries go out live as "notification" events and to any matching <see cref="NotificationChannels"/>.
/// </summary>
public sealed class NotificationCentre : IDisposable
{
    public const int Keep = 500;
    /// <summary>A machine must stay away this long before "offline" is announced (restarts and updates are quick).</summary>
    public static TimeSpan OfflineGrace { get; set; } = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false, Converters = { new JsonStringEnumConverter() } };

    private sealed class State
    {
        public long NextId { get; set; } = 1;
        public List<NotificationEntry> Entries { get; set; } = new();
        public Dictionary<string, long> ReadUpTo { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly AgentContext _ctx;
    private readonly EventStream _stream;
    private readonly MachineRegistry _machines;
    private readonly string _file;
    private readonly object _gate = new();
    private readonly State _state;
    private readonly HashSet<string> _failedJobs = new();
    private readonly Dictionary<string, CancellationTokenSource> _pendingOffline = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _announcedOffline = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _saver;
    private bool _dirty;

    public NotificationCentre(AgentContext ctx, EventStream stream, MachineRegistry machines)
    {
        _ctx = ctx;
        _stream = stream;
        _machines = machines;
        _file = Path.Combine(ctx.ConfigDir, "notifications.json");
        _state = global::WindowsGSM.Hosting.SafeJson.Read<State>(_file, Json) ?? new();
        _saver = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        stream.Published += OnPublished;
    }

    /// <summary>Raised for each new entry (the channels send it on).</summary>
    public event Action<NotificationEntry>? Added;

    // ───────────────────────────── Reading ─────────────────────────────

    public IReadOnlyList<NotificationEntry> For(AgentUser user, int limit)
    {
        lock (_gate)
        {
            return _state.Entries.AsEnumerable().Reverse().Where(e => Visible(e, user)).Take(Math.Clamp(limit, 1, Keep)).ToList();
        }
    }

    public long ReadUpTo(AgentUser user) { lock (_gate) { return _state.ReadUpTo.GetValueOrDefault(user.Username); } }

    public int Unread(AgentUser user)
    {
        lock (_gate)
        {
            long read = _state.ReadUpTo.GetValueOrDefault(user.Username);
            return _state.Entries.Count(e => e.Id > read && Visible(e, user));
        }
    }

    public void MarkRead(AgentUser user, long upTo)
    {
        lock (_gate)
        {
            long max = _state.NextId - 1;
            _state.ReadUpTo[user.Username] = Math.Clamp(upTo, _state.ReadUpTo.GetValueOrDefault(user.Username), max);
            _dirty = true;
        }
    }

    private static bool Visible(NotificationEntry e, AgentUser user)
    {
        try { return e.Visibility.For(e.Machine, e.Server)(user); } catch { return false; }
    }

    // ───────────────────────────── Recording ─────────────────────────────

    private void OnPublished(StreamEvent e)
    {
        switch (e.Type)
        {
            case "alert":
            {
                var d = Element(e.Data);
                string? kind = NotificationKinds.FromAlert(Str(d, "kind") ?? "");
                if (kind == null) { return; }
                string? name = ServerName(e.Machine, e.Server);
                string title = Str(d, "title") ?? kind;
                // "Join code found" doesn't say where; everything else already starts with the server's name.
                if (name != null && !title.Contains(name, StringComparison.OrdinalIgnoreCase)) { title = $"{name}: {title}"; }
                Record(kind, title, Str(d, "text") ?? "", e.Machine, e.Server, name, Visibility.Server());
                break;
            }
            case "job":
            {
                var d = Element(e.Data);
                if (Str(d, "status") != "Failed") { return; }
                string jobKey = $"{e.Machine}/{Str(d, "id")}";
                lock (_gate) { if (!_failedJobs.Add(jobKey)) { return; } if (_failedJobs.Count > 2000) { _failedJobs.Clear(); _failedJobs.Add(jobKey); } }
                string? server = Str(d, "serverId");
                Record("jobFailed", $"{Str(d, "title") ?? "A job"} failed", Str(d, "error") ?? "See the activity panel for details.",
                    e.Machine, server, ServerName(e.Machine, server), Visibility.Job);
                break;
            }
            case "machine":
            {
                var d = Element(e.Data);
                bool online = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("online", out var o) && o.ValueKind == JsonValueKind.True;
                OnMachine(e.Machine, online);
                break;
            }
        }
    }

    private void OnMachine(string machine, bool online)
    {
        bool back;
        lock (_gate)
        {
            if (_pendingOffline.Remove(machine, out var pending)) { pending.Cancel(); }
            // Only say it's back if we said it was gone.
            back = online && _announcedOffline.Remove(machine);
        }
        if (online)
        {
            if (back) { Record("machineOnline", $"{MachineName(machine)} is back online", "Its servers can be controlled again.", machine, null, null, Visibility.Machine); }
            return;
        }
        lock (_gate)
        {
            var cts = new CancellationTokenSource();
            _pendingOffline[machine] = cts;
            _ = Task.Delay(OfflineGrace, cts.Token).ContinueWith(t =>
            {
                if (t.IsCanceled) { return; }
                lock (_gate)
                {
                    if (!_pendingOffline.Remove(machine)) { return; }
                    if (_machines.Get(machine) == null) { return; } // removed, not offline
                    _announcedOffline.Add(machine);
                }
                Record("machineOffline", $"{MachineName(machine)} went offline",
                    "Its servers keep running on their own; the panel shows their last known state until it reconnects.", machine, null, null, Visibility.Machine);
            }, TaskScheduler.Default);
        }
    }

    /// <summary>Adds an entry (also used for "send a test").</summary>
    public NotificationEntry Record(string kind, string title, string text, string machine, string? server, string? serverName, Visibility visibility)
    {
        NotificationEntry entry;
        lock (_gate)
        {
            string severity = NotificationKinds.Get(kind)?.Severity ?? "info";
            entry = new NotificationEntry(_state.NextId++, DateTimeOffset.UtcNow, kind, severity, Trim(title, 200), Trim(text, 2000), machine, server, serverName, visibility);
            _state.Entries.Add(entry);
            if (_state.Entries.Count > Keep) { _state.Entries.RemoveRange(0, _state.Entries.Count - Keep); }
            _dirty = true;
        }
        _stream.Publish(machine, "notification", "alerts", server, visibility, ToDto(entry));
        try { Added?.Invoke(entry); } catch { /* channels log their own failures */ }
        return entry;
    }

    public static object ToDto(NotificationEntry e) => new
    {
        id = e.Id, at = e.At, kind = e.Kind, severity = e.Severity, title = e.Title, text = e.Text,
        machine = e.Machine, server = e.Server, serverName = e.ServerName,
    };

    public string MachineName(string machine) =>
        _ctx.IsThisMachine(machine) ? _ctx.Settings.MachineName : _machines.Get(machine)?.Name ?? machine;

    private string? ServerName(string machine, string? server)
    {
        if (server == null) { return null; }
        if (_ctx.IsThisMachine(machine)) { return _ctx.Engine.Servers.Get(server)?.Name; }
        return _machines.Get(machine)?.Snapshot.FirstOrDefault(s => s.Id == server)?.Name;
    }

    private static JsonElement Element(object? data)
    {
        try { return data is JsonElement el ? el : JsonSerializer.SerializeToElement(data, EventStream.Json); }
        catch { return default; }
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public void Flush()
    {
        JsonElement snapshot;
        lock (_gate)
        {
            if (!_dirty) { return; }
            _dirty = false;
            snapshot = JsonSerializer.SerializeToElement(_state, Json); // a copy, written outside the lock
        }
        try
        {
            global::WindowsGSM.Hosting.SafeJson.Write(_file, snapshot, Json);
        }
        catch { lock (_gate) { _dirty = true; } }
    }

    public void Dispose()
    {
        _stream.Published -= OnPublished;
        _saver.Dispose();
        Flush();
    }
}
