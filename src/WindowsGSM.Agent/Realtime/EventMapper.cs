using System.Text.Json.Serialization;
using WindowsGSM.Agent.Api;
using WindowsGSM.Agent.Security;
using WindowsGSM.Contracts;
using WindowsGSM.Engine.Events;
using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Services;

namespace WindowsGSM.Agent.Realtime;

/// <summary>
/// Who may see an event, in a form that travels between machines: a member agent sends it with each event,
/// and the hub evaluates it for its own users against the member's machine id.
/// </summary>
/// <param name="Kind">server (needs <see cref="Cap"/> on the server), job (the server, or Install on the machine), machine (anything on it), admin, or all.</param>
public sealed record Visibility(string Kind, Capability Cap = Capability.View)
{
    public static readonly Visibility All = new("all");
    public static readonly Visibility Admin = new("admin");
    public static Visibility Server(Capability cap = Capability.View) => new("server", cap);
    public static readonly Visibility Job = new("job");
    /// <summary>Admins, and anyone with a permission on something on that machine.</summary>
    public static readonly Visibility Machine = new("machine");

    /// <summary>The rule as a check for one user on one machine.</summary>
    public Func<AgentUser, bool> For(string machine, string? server, Func<AgentUser, string?, bool>? localJobCheck = null) => Kind switch
    {
        "all" => _ => true,
        "admin" => u => u.IsAdmin,
        "machine" => u => u.IsAdmin || u.Grants.Keys.Any(k => k.StartsWith(machine + "/", StringComparison.OrdinalIgnoreCase) || k.StartsWith("*/", StringComparison.Ordinal)),
        "server" => server == null ? (u => u.IsAdmin) : (u => u.Can(Capability.View | Cap, machine, server)),
        "job" => localJobCheck != null ? (u => localJobCheck(u, server))
            : (u => server != null ? u.Can(Capability.View, machine, server) || u.CanOnMachine(Capability.Install, machine) : u.CanOnMachine(Capability.Install, machine)),
        _ => u => u.IsAdmin,
    };
}

/// <summary>An engine event ready for the wire. <see cref="ConsoleOf"/> is set for console lines (topic depends on the machine).</summary>
public sealed record MappedEvent(string Type, string Topic, string? Server, object? Data, Visibility Visibility)
{
    [JsonIgnore] public bool IsConsole => Type == "console";
}

/// <summary>An event as the stream published it, for in-process listeners (notifications).</summary>
public sealed record StreamEvent(string Machine, string Type, string? Server, Visibility Visibility, object? Data, DateTimeOffset At);

/// <summary>Turns engine events into stream events — used by the local stream and by a member feeding its hub.</summary>
public static class EventMapper
{
    public static MappedEvent? Map(EngineEvent e, AgentContext ctx) => e switch
    {
        ServerStateChanged s => new("serverState", "servers", s.ServerId, new { from = s.From.ToString(), to = s.To.ToString() }, Visibility.Server()),
        ServerListChanged l => new("serverList", "servers", l.ServerId, new { removed = l.Removed }, l.Removed ? Visibility.All : Visibility.Server()),
        ServerConfigChanged c => new("serverConfig", "servers", c.ServerId, new { keys = c.Keys }, Visibility.Server()),
        PlayersChanged p => new("players", "servers", p.ServerId, p.Players.Select(x => new PlayerDto(x.Id, x.Name, x.Score, x.TimeConnected?.TotalSeconds)).ToList(), Visibility.Server()),
        ServerMetricsSampled ms => new("metrics", "metrics", ms.ServerId, new SampleDto(ms.Sample.At, ms.Sample.CpuPercent, ms.Sample.MemoryMb, ms.Sample.Players, ms.Sample.MaxPlayers), Visibility.Server()),
        ConsoleLineAdded cl => new("console", "console", cl.ServerId, new { line = cl.Line }, Visibility.Server(Capability.Console)),
        JobChanged j => new("job", "jobs", j.Job.ServerId, ctx.ToDto(j.Job), Visibility.Job),
        PromptRaised pr => new("prompt", "jobs", pr.Prompt.ServerId, ctx.ToDto(pr.Prompt), Visibility.Job),
        PromptResolved r => new("promptResolved", "jobs", r.ServerId, new { promptId = r.PromptId, jobId = r.JobId, answer = r.Answer, answeredBy = r.AnsweredBy }, Visibility.Job),
        ServerAlert a => new("alert", "alerts", a.ServerId, new { kind = a.Kind.ToString(), title = a.Title, text = a.Text }, Visibility.Server()),
        // "System"/"Web" lines are about the whole machine: admins only.
        ServerLogged lg => ctx.Engine.Servers.Get(lg.ServerId) != null
            ? new("log", "logs", lg.ServerId, new { source = lg.ServerId, level = lg.Level.ToString(), message = lg.Message }, Visibility.Server())
            : new("log", "logs", null, new { source = lg.ServerId, level = lg.Level.ToString(), message = lg.Message }, Visibility.Admin),
        _ => null,
    };

    /// <summary>Console topics are per machine and server: "console:{machine}/{server}".</summary>
    public static string TopicFor(MappedEvent m, string machine) => m.IsConsole ? $"console:{machine}/{m.Server}" : m.Topic;
}
