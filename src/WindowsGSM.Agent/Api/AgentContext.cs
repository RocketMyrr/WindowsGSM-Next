using System.Security.Claims;
using WindowsGSM.Agent.Hosting;
using WindowsGSM.Agent.Security;
using WindowsGSM.Contracts;
using WindowsGSM.Engine;
using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Engine.Services;

namespace WindowsGSM.Agent.Api;

/// <summary>Everything an API handler needs, in one place (registered as a singleton).</summary>
public sealed class AgentContext
{
    public const string SessionClaim = "sid";
    private const string UserItem = "wgsm.user";

    public AgentContext(WgsmEngine engine, AgentSettings settings, string configDir, UserStore users, SessionStore sessions, AuditLog audit, MachineMetrics metrics)
    {
        Engine = engine;
        Settings = settings;
        ConfigDir = configDir;
        Users = users;
        Sessions = sessions;
        Audit = audit;
        Metrics = metrics;
        Tags = new Hosting.ServerTags(configDir);
        // A deleted server's tags go with it (a new server can reuse the number).
        engine.Events.Subscribe<WindowsGSM.Engine.Events.ServerListChanged>(e => { if (e.Removed) { Tags.Forget(e.ServerId); } });
    }

    public WgsmEngine Engine { get; }
    public AgentSettings Settings { get; }
    public string ConfigDir { get; }
    public UserStore Users { get; }
    public SessionStore Sessions { get; }
    public AuditLog Audit { get; }
    public MachineMetrics Metrics { get; }
    public Hosting.ServerTags Tags { get; }
    /// <summary>Whether a server has a game update waiting (set by the update watcher).</summary>
    public Func<string, bool> UpdateAvailable { get; set; } = _ => false;
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public string MachineId => Settings.MachineId;

    /// <summary>"local" is an alias for this machine, so a single-machine UI never needs to know its id.</summary>
    public bool IsThisMachine(string? machine) =>
        string.Equals(machine, "local", StringComparison.OrdinalIgnoreCase) || string.Equals(machine, MachineId, StringComparison.OrdinalIgnoreCase);

    // ── Who is asking ──

    /// <summary>The signed-in user for this request (validated against the session store once per request).</summary>
    public AgentUser? CurrentUser(HttpContext http)
    {
        if (http.Items.TryGetValue(UserItem, out var cached)) { return cached as AgentUser; }
        AgentUser? user = Delegated(http);
        string? name = http.User.Identity?.IsAuthenticated == true ? http.User.Identity.Name : null;
        if (user == null && name != null)
        {
            var u = Users.Get(name);
            if (u is { Enabled: true } && Sessions.IsValid(http.User.FindFirstValue(SessionClaim), name)) { user = u; }
        }
        http.Items[UserItem] = user;
        return user;
    }

    public static string? SessionId(HttpContext http) => http.User.FindFirstValue(SessionClaim);

    public static string? Ip(HttpContext http) =>
        http.Items.TryGetValue(ClientIpItem, out var forwarded) && forwarded is string ip ? ip : http.Connection.RemoteIpAddress?.ToString();

    public void Record(HttpContext http, string action, string? server, bool ok, string? detail = null)
    {
        var user = CurrentUser(http);
        string? who = user?.Via != null ? $"{user.Username} (via {user.Via})" : user?.Username ?? http.User.Identity?.Name;
        Audit.Write(who, Ip(http), action, server, ok, detail);
    }

    // ── Requests relayed by this machine's hub ──

    public const string InternalHeader = "X-WGSM-Internal";
    public const string DelegateHeader = "X-WGSM-Delegate";
    public const string ClientIpHeader = "X-WGSM-Client-Ip";
    private const string ClientIpItem = "wgsm.clientip";

    /// <summary>
    /// A per-process secret. The link to the hub replays each relayed request against this agent over loopback
    /// with this secret, so the request runs through the normal API — every permission check included — as the
    /// hub's signed-in user. It never leaves this process, so the header can't be forged from outside.
    /// </summary>
    public string InternalSecret { get; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    private AgentUser? Delegated(HttpContext http)
    {
        string secret = http.Request.Headers[InternalHeader].ToString();
        if (secret.Length == 0) { return null; }
        var remote = http.Connection.RemoteIpAddress;
        bool loopback = remote == null || System.Net.IPAddress.IsLoopback(remote);
        if (!loopback || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(secret), System.Text.Encoding.ASCII.GetBytes(InternalSecret)))
        {
            return null;
        }
        var user = Hub.Delegation.Decode(http.Request.Headers[DelegateHeader].ToString());
        if (user == null) { return null; }
        string clientIp = http.Request.Headers[ClientIpHeader].ToString();
        if (clientIp.Length > 0) { http.Items[ClientIpItem] = clientIp; }
        return user;
    }

    // ── Mapping ──

    public ServerDto ToDto(ServerInstance s, AgentUser user)
    {
        var cfg = s.Config;
        var sample = Engine.Monitor.Latest(s.Id);
        var op = Engine.Operations.Current(s.Id);
        int? maxPlayers = sample?.MaxPlayers ?? (int.TryParse(cfg.ServerMaxPlayer, out int mp) ? mp : null);
        return new ServerDto(
            MachineId, s.Id, s.Name, s.Game, s.State.ToString(), op?.Description,
            cfg.ServerIP, cfg.ServerPort, cfg.ServerQueryPort,
            sample?.Players, maxPlayers, sample?.CpuPercent, sample?.MemoryMb, s.StartedAt,
            cfg.AutoStart, cfg.AutoRestart, cfg.AutoUpdate, s.CrashLoopSuspended,
            IconUrl(s.Game), user.On(MachineId, s.Id), ArtUrl(s.Game, "cover"), ArtUrl(s.Game, "header"), Tags.Get(s.Id), UpdateAvailable(s.Id), WindowsGSM.Engine.Services.UpdateService.IsHeld(s));
    }

    public JobDto ToDto(JobSnapshot j) =>
        new(MachineId, j.Id, j.Kind, j.ServerId, j.Title, j.Status.ToString(), j.Percent, j.Stage, j.Error, j.StartedAt, j.EndedAt, j.RecentLog);

    public PromptDto ToDto(PendingPrompt p) =>
        new(MachineId, p.Id, p.JobId, p.ServerId, p.Key, p.Title, p.Message, p.AskedAt, p.ExpiresAt);

    public GameDto ToDto(GameInfo g) =>
        new(g.Name, g.IsPlugin, g.IsSteam, g.AppId, IconUrl(g), g.Description, g.Author, g.Version, g.Color, g.Consents, ArtUrl(g.Name, "cover"), ArtUrl(g.Name, "header"));

    /// <summary>Game artwork (fetched from Steam and cached by the agent), or null when the game has none.</summary>
    public string? ArtUrl(string game, string kind) =>
        string.IsNullOrWhiteSpace(game) || !GameArt.MayHaveArt(game) ? null
            : $"/api/v2/machines/{MachineId}/games/art?kind={kind}&name={Uri.EscapeDataString(game)}";

    public string? IconUrl(string game)
    {
        var info = string.IsNullOrWhiteSpace(game) ? null : Engine.Games.All().FirstOrDefault(g => g.Name == game);
        return info == null ? null : IconUrl(info);
    }

    /// <summary>Built-in art ships with the web UI (img/games/…); plugin art is served from the plugin's folder.</summary>
    private string? IconUrl(GameInfo g)
    {
        if (g.Icon == null) { return null; }
        return g.IsPlugin
            ? $"/api/v2/machines/{MachineId}/games/icon?name={Uri.EscapeDataString(g.Name)}"
            : "/img/games/" + Path.GetFileName(g.Icon).ToLowerInvariant();
    }

    // ── Who may see / answer jobs ──

    /// <summary>
    /// A server's jobs are visible to anyone who can see the server. Machine-level jobs (installs in progress)
    /// need Install on the machine.
    /// </summary>
    public bool CanSeeJob(AgentUser user, string? serverId) =>
        serverId != null && Engine.Servers.Get(serverId) != null
            ? user.Can(Capability.View, MachineId, serverId)
            : user.CanOnMachine(Capability.Install, MachineId);

    /// <summary>Cancelling a job, or answering its question, needs the right that could start it.</summary>
    public bool CanSteerJob(AgentUser user, JobSnapshot job)
    {
        if (user.IsAdmin) { return true; }
        if (job.Kind is "install" or "import" || job.ServerId == null || Engine.Servers.Get(job.ServerId) == null)
        {
            return user.CanOnMachine(Capability.Install, MachineId);
        }
        var needed = job.Kind switch
        {
            "backup" => Capability.Backup,
            "restore" => Capability.Restore,
            "addon" => Capability.Addons,
            "start" => Capability.Start,
            "stop" => Capability.Stop,
            "restart" or "auto-restart" => Capability.Restart,
            "kill" => Capability.Kill,
            "delete" => Capability.Delete,
            _ => Capability.Update, // update, validate
        };
        return user.Can(needed, MachineId, job.ServerId);
    }
}
