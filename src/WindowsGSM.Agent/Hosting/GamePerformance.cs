using System.Globalization;
using System.Text.RegularExpressions;
using WindowsGSM.Agent.Api;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Engine.Services;

namespace WindowsGSM.Agent.Hosting;

/// <summary>
/// How smoothly the game itself runs — server FPS (Rust, Source games) or TPS (Minecraft) — asked over RCON once a
/// minute and kept in the history database. CPU and RAM can look fine while the game lags; this shows it.
/// Only for running servers with RCON set up; nothing is written to the server's log.
/// </summary>
public sealed partial class GamePerformance : IDisposable
{
    /// <summary>How to ask one kind of game, and read the answer.</summary>
    public sealed record Probe(string Kind, string Command, Func<string, double?> Parse, double? Target, string? Fallback = null);

    public sealed record Latest(string Kind, double Value, double? Target, DateTimeOffset At);

    private readonly AgentContext _ctx;
    private readonly MetricsHistory _history;
    private readonly Timer _timer;
    private readonly Dictionary<string, Latest> _latest = new();
    private readonly Dictionary<string, DateTimeOffset> _unsupportedUntil = new(); // servers whose game didn't answer: try again later
    private readonly Dictionary<string, DateTimeOffset> _lastAsked = new();

    /// <summary>How often each server is asked. Games log every RCON connection, so not every minute.</summary>
    public TimeSpan Every { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Setting (WindowsGSM.cfg): "0" = don't ask this server for its FPS/TPS.</summary>
    public const string SettingKey = "perfsample";
    private readonly object _gate = new();
    private int _running;

    /// <summary>Tests: answers instead of real RCON.</summary>
    public Func<string, string, Task<string?>>? AskOverride { get; set; }

    public GamePerformance(AgentContext ctx, MetricsHistory history)
    {
        _ctx = ctx;
        _history = history;
        _timer = new Timer(_ => _ = SampleAllAsync(), null, TimeSpan.FromSeconds(75), TimeSpan.FromMinutes(1));
    }

    /// <summary>The probe for a game, or null when it has no performance command WindowsGSM knows.</summary>
    public Probe? ProbeFor(ServerInstance s)
    {
        string? appId = _ctx.Engine.Games.Get(s.Game)?.AppId;
        if (ConsoleService.UsesRustRcon(s.Game) || appId == "258550")
        {
            return new Probe("FPS", "serverinfo", reply => Number(RustFramerate().Match(reply)), null);
        }
        if (s.Game.Contains("Minecraft", StringComparison.OrdinalIgnoreCase))
        {
            return new Probe("TPS", "tps", MinecraftTps, 20, Fallback: "tick query"); // Paper/Spigot; Vanilla 1.20.3+ has "tick query"
        }
        if (appId != null && SourceGames.Contains(appId))
        {
            return new Probe("FPS", "stats", SourceStatsFps, null);
        }
        return null;
    }

    /// <summary>Dedicated servers of Source-engine games, whose "stats" command reports FPS.</summary>
    private static readonly HashSet<string> SourceGames = new()
    {
        "730", "740", "232250", "4020", "222860", "237410", "232290", "232330", "232370", "295230", "17505", "17585", "2311190", "462310", "1136190",
    };

    [GeneratedRegex("\"Framerate\"\\s*:\\s*([0-9.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex RustFramerate();

    [GeneratedRegex("§.")]
    private static partial Regex MinecraftColours();

    /// <summary>Paper/Spigot "tps": "TPS from last 1m, 5m, 15m: 20.0, 19.98, 20.0" (or "*20.0"); vanilla "tick query": average ms per tick.</summary>
    internal static double? MinecraftTps(string reply)
    {
        string text = MinecraftColours().Replace(reply, "");
        var m = Regex.Match(text, @"TPS from last[^:]*:\s*\*?([0-9.]+)", RegexOptions.IgnoreCase);
        if (m.Success) { return Math.Min(20, Number(m) ?? 0); }
        m = Regex.Match(text, @"Average time per tick:\s*([0-9.]+)\s*ms", RegexOptions.IgnoreCase);
        if (m.Success && Number(m) is double ms && ms > 0) { return Math.Min(20, 1000 / ms); }
        return null;
    }

    /// <summary>Source "stats": a header row with an FPS column, then the values.</summary>
    internal static double? SourceStatsFps(string reply)
    {
        var lines = reply.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i + 1 < lines.Length; i++)
        {
            var head = Regex.Split(lines[i], @"\s+");
            int col = Array.FindIndex(head, c => c.Equals("FPS", StringComparison.OrdinalIgnoreCase));
            if (col < 0) { continue; }
            var values = Regex.Split(lines[i + 1], @"\s+");
            if (col < values.Length && double.TryParse(values[col], NumberStyles.Float, CultureInfo.InvariantCulture, out double fps)) { return fps; }
        }
        return null;
    }

    private static double? Number(Match m) =>
        m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    public Latest? LatestFor(string id)
    {
        lock (_gate) { return _latest.GetValueOrDefault(id); }
    }

    /// <summary>One round: every running server with RCON and a known probe.</summary>
    public async Task SampleAllAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) { return; }
        try
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var s in _ctx.Engine.Servers.All)
            {
                if (s.State != ServerState.Running || s.StartedAt is not { } started || now - started < TimeSpan.FromMinutes(1)) { continue; }
                if (!ConsoleService.RconConfigured(s.Config, out _, out _) && AskOverride == null) { continue; }
                if (s.Config.GetCustomSetting(SettingKey, "") == "0") { continue; }
                lock (_gate)
                {
                    if (_unsupportedUntil.TryGetValue(s.Id, out var until) && until > now) { continue; }
                    if (_lastAsked.TryGetValue(s.Id, out var last) && now - last < Every - TimeSpan.FromSeconds(5)) { continue; }
                    _lastAsked[s.Id] = now;
                }
                var probe = ProbeFor(s);
                if (probe == null) { continue; }
                await SampleAsync(s, probe, now);
            }
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    private async Task SampleAsync(ServerInstance s, Probe probe, DateTimeOffset now)
    {
        Task<string?> Ask(string command) => AskOverride != null ? AskOverride(s.Id, command) : _ctx.Engine.Console.QueryRconAsync(s.Id, command);
        string? reply = await Ask(probe.Command);
        double? value = reply == null ? null : probe.Parse(reply);
        if (value == null && reply != null && probe.Fallback != null)
        {
            reply = await Ask(probe.Fallback);
            value = reply == null ? null : probe.Parse(reply);
        }
        lock (_gate)
        {
            if (value == null) { _unsupportedUntil[s.Id] = now.AddMinutes(15); return; } // no answer: don't keep asking every minute
            _unsupportedUntil.Remove(s.Id);
            _latest[s.Id] = new Latest(probe.Kind, Math.Round(value.Value, 1), probe.Target, now);
        }
        _history.RecordPerf(s.Id, now, value.Value);
    }

    public void Dispose() => _timer.Dispose();
}
