using WindowsGSM.Contracts;
using WindowsGSM.Installer;

namespace WindowsGSM.Agent.Api;

/// <summary>One server: details, power actions, update, console, logs, players, metrics and settings.</summary>
public static class ServerEndpoints
{
    public static RouteGroupBuilder Map(RouteGroupBuilder api)
    {
        var server = api.MapGroup("/machines/{machine}/servers/{id}").RequireServer();

        server.MapGet("", (HttpContext http, AgentContext ctx) => Results.Json(ctx.ToDto(Scopes.Server(http), Scopes.User(http))));

        // ── Power ──

        server.MapPost("/start", (HttpContext http, AgentContext ctx) => Act(http, ctx, "start", id => ctx.Engine.Lifecycle.Start(id))).Needs(Capability.Start);
        server.MapPost("/stop", (HttpContext http, AgentContext ctx) => Act(http, ctx, "stop", id => ctx.Engine.Lifecycle.Stop(id))).Needs(Capability.Stop);
        server.MapPost("/restart", (HttpContext http, AgentContext ctx) => Act(http, ctx, "restart", id => ctx.Engine.Lifecycle.Restart(id))).Needs(Capability.Restart);
        server.MapPost("/kill", (HttpContext http, AgentContext ctx) => Act(http, ctx, "kill", id => ctx.Engine.Lifecycle.Kill(id))).Needs(Capability.Kill);

        // ── Update ──

        server.MapPost("/update", (HttpContext http, AgentContext ctx) => Act(http, ctx, "update", id => ctx.Engine.Updates.Update(id))).Needs(Capability.Update);
        server.MapPost("/validate", (HttpContext http, AgentContext ctx) => Act(http, ctx, "validate", id => ctx.Engine.Updates.Update(id, validate: true))).Needs(Capability.Update);

        server.MapGet("/update-check", async (HttpContext http, AgentContext ctx, Hosting.UpdateWatch watch) =>
        {
            var check = await watch.CheckNowAsync(Scopes.Server(http).Id);
            return Results.Json(new UpdateCheckDto(check.LocalBuild, check.RemoteBuild, check.Available, check.Error));
        }).Needs(Capability.Update);

        // Roll back a game update: the builds DepotDownloader has installed (its own manifests — not backups).
        server.MapGet("/builds", (HttpContext http, AgentContext ctx) =>
        {
            var s = ctx.Engine.Servers.Get(Scopes.Server(http).Id)!;
            var builds = DepotHistory.Builds(s.Id);
            return Results.Json(new
            {
                held = WindowsGSM.Engine.Services.UpdateService.IsHeld(s),
                builds = builds.Select(b => new { key = b.Key, installedAt = b.InstalledAt, buildId = b.BuildId, current = b.Current, depots = b.Depots.Count }),
            });
        }).Needs(Capability.Update);

        server.MapPost("/rollback", (HttpContext http, AgentContext ctx, RollbackRequest body) =>
            Act(http, ctx, "rollback", id => ctx.Engine.Updates.Rollback(id, body.Key ?? ""))).Needs(Capability.Update);

        server.MapPost("/update-hold", (HttpContext http, AgentContext ctx, UpdateHoldRequest body) =>
        {
            var s = Scopes.Server(http);
            ctx.Engine.Updates.SetHold(s.Id, body.Held);
            ctx.Record(http, "update-hold", s.Id, true, body.Held ? "updates on hold" : "updates resumed");
            ctx.Engine.Events.Publish(new WindowsGSM.Engine.Events.ServerConfigChanged(s.Id, new[] { "updatehold" }));
            return Results.NoContent();
        }).Needs(Capability.Update);

        server.MapDelete("", (HttpContext http, AgentContext ctx) => Act(http, ctx, "delete", id => ctx.Engine.Provisioning.Delete(id))).Needs(Capability.Delete);

        // ── Console ──

        server.MapGet("/console", (HttpContext http, long? since, int? generation) =>
        {
            var (lines, seq, gen, reset) = Scopes.Server(http).Console.GetSince(since ?? -1, generation ?? -1);
            return Results.Json(new ConsoleDto(lines.Select(l => l.TrimEnd('\r')).ToList(), seq, gen, reset));
        }).Needs(Capability.Console);

        server.MapPost("/console", async (HttpContext http, AgentContext ctx, CommandRequest body) =>
        {
            var s = Scopes.Server(http);
            string command = body.Command?.Trim() ?? string.Empty;
            if (command.Length == 0) { return ApiResults.BadRequest("Type a command first."); }
            if (command.Length > 2000 || command.IndexOfAny(new[] { '\r', '\n' }) >= 0) { return ApiResults.BadRequest("Commands are one line, up to 2000 characters."); }

            var user = Scopes.User(http);
            var result = await ctx.Engine.Console.SendAsync(s.Id, command, user.Username, body.PreferRcon);
            ctx.Record(http, "command", s.Id, result.Sent, result.Sent ? $"{command} ({result.Route})" : $"{command}: {result.Error}");
            return Results.Json(new CommandResultDto(result.Sent, result.Route?.ToString(), result.Reply, result.Error));
        }).Needs(Capability.Console);

        // ── Logs, players, metrics ──

        server.MapGet("/logs", (HttpContext http, AgentContext ctx, int? count) =>
            Results.Json(new LogDto(ctx.Engine.Log.Tail(Scopes.Server(http).Id, Math.Clamp(count ?? 200, 1, 2000)))));

        // The game's own console window on this machine's screen (servers whose output isn't captured): show / hide.
        server.MapGet("/console-window", (HttpContext http, AgentContext ctx) =>
        {
            var s = ctx.Engine.Servers.Get(Scopes.Server(http).Id)!;
            return Results.Json(new
            {
                hasWindow = s.ConsoleWindow != IntPtr.Zero && s.Process != null, visible = s.ConsoleWindowVisible, captured = s.Config.EmbedConsole,
                // Kept running through an agent restart: a captured console can't be reconnected until it restarts.
                reattached = s.Reattached && s.Process != null,
                rcon = WindowsGSM.Engine.Services.ConsoleService.RconConfigured(s.Config, out _, out _),
            });
        }).Needs(Capability.Console);

        server.MapPost("/console-window", (HttpContext http, AgentContext ctx, ConsoleWindowRequest body) =>
        {
            var s = Scopes.Server(http);
            string? problem = ctx.Engine.Lifecycle.SetConsoleWindowVisible(s.Id, body.Visible);
            ctx.Record(http, "console-window", s.Id, problem == null, problem ?? (body.Visible ? "shown" : "hidden"));
            return problem == null ? Results.NoContent() : ApiResults.BadRequest(problem);
        }).Needs(Capability.Console);

        // How the player query is doing — "why don't I see players?" in plain words.
        server.MapGet("/players/query", (HttpContext http, AgentContext ctx) => Results.Json(ctx.Engine.Monitor.QueryState(Scopes.Server(http).Id)));

        server.MapGet("/players", (HttpContext http, AgentContext ctx) =>
            Results.Json(ctx.Engine.Monitor.Players(Scopes.Server(http).Id)
                .Select(p => new PlayerDto(p.Id, p.Name, p.Score, p.TimeConnected?.TotalSeconds))));

        server.MapGet("/metrics", (HttpContext http, AgentContext ctx) =>
            Results.Json(ctx.Engine.Monitor.History(Scopes.Server(http).Id)
                .Select(m => new SampleDto(m.At, m.CpuPercent, m.MemoryMb, m.Players, m.MaxPlayers))));

        // ── Tags (labels for finding and filtering servers) ──

        server.MapPut("/tags", (HttpContext http, AgentContext ctx, TagsRequest body) =>
        {
            string id = Scopes.Server(http).Id;
            string? problem = ctx.Tags.Set(id, body.Tags);
            if (problem != null) { return ApiResults.BadRequest(problem); }
            ctx.Engine.Events.Publish(new WindowsGSM.Engine.Events.ServerConfigChanged(id, new[] { "tags" }));
            return Results.Json(ctx.Tags.Get(id));
        }).Needs(Capability.EditConfig);

        // ── Settings ──

        server.MapGet("/settings", (HttpContext http, AgentContext ctx) =>
        {
            var settings = ctx.Engine.Settings.Get(Scopes.Server(http).Id);
            if (settings == null) { return ApiResults.NotFound("No such server."); }
            return Results.Json(new ServerSettingsDto(ctx.MachineId, settings.ServerId, settings.Game, settings.IsSteam, settings.SteamBranchLastInstalled,
                settings.Values, settings.Custom.Select(c => new CustomSettingDto(c.Key, c.Label, c.Value, c.Options)).ToList(), settings.CustomReplacesBuiltIns,
                KnownSaveCommand(ctx, Scopes.Server(http))));
        }).Needs(Capability.EditConfig);

        // The game's own save command, shown as the default (a server can set its own, or "-" for none).
        static string? KnownSaveCommand(AgentContext ctx, WindowsGSM.Engine.Servers.ServerInstance s)
        {
            string? appId = ctx.Engine.Games.Get(s.Game)?.AppId;
            if (appId != null && WindowsGSM.Engine.Services.WorldSave.Known.TryGetValue(appId, out var k)) { return k.Command; }
            return s.Game.Contains("Minecraft", StringComparison.OrdinalIgnoreCase) ? "save-all" : null;
        }

        server.MapPatch("/settings", (HttpContext http, AgentContext ctx, WindowsGSM.Agent.Hosting.ConfigHistory history, SettingsUpdateRequest body) =>
        {
            var s = Scopes.Server(http);
            history.Keep(s.Id, WindowsGSM.Agent.Hosting.ConfigHistory.SettingsPath, WindowsGSM.Functions.ServerPath.GetServersConfigs(s.Id, "WindowsGSM.cfg"), Scopes.User(http).Username, "Settings");
            var problems = ctx.Engine.Settings.Update(s.Id, body.Values ?? new Dictionary<string, string?>());
            // Log which settings changed, never their values (webhooks and passwords live here).
            ctx.Record(http, "settings", s.Id, problems.Count == 0,
                problems.Count == 0 ? string.Join(", ", body.Values?.Keys ?? Enumerable.Empty<string>()) : string.Join("; ", problems));
            return problems.Count == 0 ? Results.NoContent() : ApiResults.BadRequest("Nothing was saved — some settings aren't valid.", problems);
        }).Needs(Capability.EditConfig);

        server.MapGet("/steam/branches", async (HttpContext http, AgentContext ctx) =>
        {
            var game = ctx.Engine.Games.Get(Scopes.Server(http).Game);
            if (game is not { IsSteam: true }) { return Results.Json(Array.Empty<SteamBranchDto>()); }
            try
            {
                var branches = await SteamAppInfo.GetBranchesAsync(game.AppId!);
                return Results.Json(branches.Select(b => new SteamBranchDto(b.Name, b.BuildId, b.PasswordRequired, b.UpdatedAt, b.Description)));
            }
            catch (Exception ex) { return ApiResults.Error(502, "steam_unavailable", $"Couldn't ask Steam for branches: {ex.Message}"); }
        }).Needs(Capability.EditConfig);

        server.MapGet("/readiness", (HttpContext http, AgentContext ctx) =>
            Results.Json(ctx.Engine.Readiness.CheckServer(Scopes.Server(http).Id)
                .Select(c => new ReadinessCheckDto(c.Scope, c.Name, c.Status.ToString(), c.Message)))).Needs(Capability.EditConfig);

        return server;
    }

    private static IResult Act(HttpContext http, AgentContext ctx, string action, Func<string, Engine.Services.OperationRequest> start)
    {
        var s = Scopes.Server(http);
        var request = start(s.Id);
        ctx.Record(http, action, s.Id, request.Accepted, request.Error);
        return ApiResults.FromRequest(request, ctx);
    }
}

public sealed record ConsoleWindowRequest(bool Visible);
public sealed record RollbackRequest(string? Key);
public sealed record UpdateHoldRequest(bool Held);
