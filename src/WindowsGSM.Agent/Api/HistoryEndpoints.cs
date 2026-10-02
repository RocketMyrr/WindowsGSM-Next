using WindowsGSM.Agent.Hosting;

namespace WindowsGSM.Agent.Api;

/// <summary>Long-term charts (machine and server) and player history, from this machine's own history store.</summary>
public static class HistoryEndpoints
{
    public static void Map(RouteGroupBuilder api, RouteGroupBuilder server)
    {
        // The machine's CPU / memory / disk (everyone who can see the machine sees its health on the overview).
        api.MapGroup("/machines/{machine}").RequireMachine().MapGet("/history", (MetricsHistory history, string? range) =>
        {
            if (MetricsHistory.Range(range) is not { } r) { return ApiResults.BadRequest("Range is one of 6h, 24h, 7d, 30d, 1y."); }
            return Results.Json(history.Query(null, r.Span, r.BucketSeconds).Select(p => new { at = p.At, cpu = p.Cpu, ram = p.Ram, disk = p.Disk }));
        });

        server.MapGet("/history", (HttpContext http, MetricsHistory history, string? range) =>
        {
            if (MetricsHistory.Range(range) is not { } r) { return ApiResults.BadRequest("Range is one of 6h, 24h, 7d, 30d, 1y."); }
            return Results.Json(history.Query(Scopes.Server(http).Id, r.Span, r.BucketSeconds)
                .Select(p => new { at = p.At, cpu = p.Cpu, ram = p.Ram, players = p.Players, maxPlayers = p.MaxPlayers }));
        });

        server.MapGet("/uptime", (HttpContext http, MetricsHistory history) => Results.Json(history.Uptime(Scopes.Server(http).Id)));

        // In-game performance (server FPS / TPS over RCON), for the overview's chart.
        server.MapGet("/performance", (HttpContext http, AgentContext ctx, MetricsHistory history, GamePerformance perf, string? range) =>
        {
            var s = ctx.Engine.Servers.Get(Scopes.Server(http).Id)!;
            var probe = perf.ProbeFor(s);
            var r = range == "1h" ? (TimeSpan.FromHours(1), 60) : MetricsHistory.Range(range);
            if (r == null) { return ApiResults.BadRequest("Unknown range."); }
            var points = history.QueryPerf(s.Id, r.Value.Item1, r.Value.Item2);
            return Results.Json(new
            {
                supported = probe != null,
                kind = probe?.Kind,
                command = probe?.Command,
                target = probe?.Target,
                rcon = WindowsGSM.Engine.Services.ConsoleService.RconConfigured(s.Config, out _, out _),
                latest = perf.LatestFor(s.Id),
                points = points.Select(p => new { at = p.At, avg = p.Avg, min = p.Min }),
            });
        });

        // The overview's extra numbers: the most players online at once today (servers you can see).
        api.MapGroup("/machines/{machine}").RequireMachine().MapGet("/summary", (HttpContext http, AgentContext ctx, MetricsHistory history) =>
        {
            var user = ctx.CurrentUser(http);
            if (user == null) { return ApiResults.Unauthorized(); }
            var visible = ctx.Engine.Servers.All.Where(s => user.Can(WindowsGSM.Contracts.Capability.View, ctx.MachineId, s.Id)).Select(s => s.Id).ToList();
            return Results.Json(new { peakToday = history.PeakPlayers(visible, new DateTimeOffset(DateTime.Today)) });
        });

        server.MapGet("/players/history", (HttpContext http, MetricsHistory history, int? days) =>
        {
            string id = Scopes.Server(http).Id;
            int d = Math.Clamp(days ?? 30, 1, 365);
            return Results.Json(new
            {
                days = d,
                top = history.TopPlayers(id, d, 25),
                recent = history.Sessions(id, 40),
            });
        });
    }
}
