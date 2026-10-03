using WindowsGSM.Agent.Hosting;
using WindowsGSM.Contracts;
using WindowsGSM.Engine.Operations;
using WindowsGSM.Hosting;
using WindowsGSM.Installer;
using EngineInstall = WindowsGSM.Engine.Services.InstallRequest;

namespace WindowsGSM.Agent.Api;

/// <summary>Machines, games, installing, the server list, jobs and live questions.</summary>
public static class MachineEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        // The machine list: just this agent for now; a hub (Phase 5) adds the machines it relays.
        // This machine first, then (on a hub) every paired machine — online or not.
        api.MapGet("/machines", (HttpContext http, AgentContext ctx, WindowsGSM.Agent.Hub.MachineRegistry registry, WindowsGSM.Agent.Hub.HubLinks links) =>
        {
            if (ctx.CurrentUser(http) == null) { return ApiResults.Unauthorized(); }
            var list = new List<MachineDto> { This(ctx) };
            list.AddRange(registry.All().OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Select(m => WindowsGSM.Agent.Hub.Forwarder.Describe(m, links)));
            return Results.Json(list);
        });

        var machine = api.MapGroup("/machines/{machine}").RequireMachine();

        machine.MapGet("", (AgentContext ctx) => Results.Json(This(ctx)));

        // ── Games ──

        machine.MapGet("/games", (AgentContext ctx) => Results.Json(ctx.Engine.Games.All().Select(ctx.ToDto)));

        machine.MapGet("/games/branches", async (AgentContext ctx, string game) =>
        {
            var info = ctx.Engine.Games.Get(game);
            if (info == null) { return ApiResults.NotFound("Unknown game."); }
            if (!info.IsSteam) { return Results.Json(Array.Empty<SteamBranchDto>()); }
            try
            {
                var branches = await SteamAppInfo.GetBranchesAsync(info.AppId!);
                return Results.Json(branches.Select(b => new SteamBranchDto(b.Name, b.BuildId, b.PasswordRequired, b.UpdatedAt, b.Description)));
            }
            catch (Exception ex) { return ApiResults.Error(502, "steam_unavailable", $"Couldn't ask Steam for branches: {ex.Message}"); }
        });

        // Plugin artwork (built-in art ships with the web UI).
        machine.MapGet("/games/icon", (AgentContext ctx, string name) =>
        {
            var info = ctx.Engine.Games.Get(name);
            if (info is not { IsPlugin: true, Icon: not null } || !File.Exists(info.Icon)) { return ApiResults.NotFound(); }
            // Plugin logos are often 1 MB+; tiles get the 256 px copy.
            return Results.File(PluginStore.IconThumbnail(Path.GetFileName(Path.GetDirectoryName(info.Icon)!)) ?? info.Icon, "image/png");
        });

        // Game artwork: fetched from Steam once, then served from the agent's cache (a week in the browser).
        machine.MapGet("/games/art", async (HttpContext http, AgentContext ctx, WindowsGSM.Agent.Hosting.GameArt art, string name, string? kind) =>
        {
            if (ctx.Engine.Games.Get(name) == null) { return ApiResults.NotFound("Unknown game."); }
            string? file = await art.GetAsync(name, kind ?? "cover");
            if (file == null) { return ApiResults.NotFound("No artwork for this game."); }
            http.Response.Headers.CacheControl = "private, max-age=604800";
            return Results.File(file, file.EndsWith(".png") ? "image/png" : "image/jpeg");
        });

        // Machine readiness reveals folder paths, so it's admin-only.
        machine.MapGet("/readiness", async (HttpContext http, AgentContext ctx, bool? network) =>
        {
            if (!ctx.CurrentUser(http)!.IsAdmin) { return ApiResults.Forbidden(); }
            var checks = await ctx.Engine.Readiness.CheckMachineAsync(network ?? true);
            return Results.Json(checks.Select(c => new ReadinessCheckDto(c.Scope, c.Name, c.Status.ToString(), c.Message)));
        });

        // The same audit log as /audit, addressed by machine so a hub can show a member's.
        machine.MapGet("/audit", (HttpContext http, AgentContext ctx, int? limit, string? user, string? server, string? action) =>
            ctx.CurrentUser(http)!.IsAdmin
                ? Results.Json(ctx.Audit.Read(limit ?? 200, user, server, action))
                : ApiResults.Forbidden("Only admins and owners can do that."));

        // The log files on this machine (Logs page). Admins: they hold IP addresses and folder paths.
        machine.MapGet("/logs", (HttpContext http, AgentContext ctx) =>
            ctx.CurrentUser(http)!.IsAdmin
                ? Results.Json(LogFiles.List(WgsmEnvironment.DataRoot))
                : ApiResults.Forbidden("Only admins and owners can do that."));

        machine.MapGet("/logs/read", (HttpContext http, AgentContext ctx, string path) =>
        {
            if (!ctx.CurrentUser(http)!.IsAdmin) { return ApiResults.Forbidden("Only admins and owners can do that."); }
            try { return LogFiles.Read(WgsmEnvironment.DataRoot, path) is { } text ? Results.Json(text) : ApiResults.NotFound("No such log."); }
            catch (IOException ex) { return ApiResults.Error(409, "log_unavailable", $"Couldn't read the log: {ex.Message}"); }
        });

        machine.MapGet("/logs/download", (HttpContext http, AgentContext ctx, string path) =>
        {
            if (!ctx.CurrentUser(http)!.IsAdmin) { return ApiResults.Forbidden("Only admins and owners can do that."); }
            string? file = LogFiles.Resolve(WgsmEnvironment.DataRoot, path);
            if (file == null) { return ApiResults.NotFound("No such log."); }
            // Opened shared: the current day's log is still being written.
            var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Results.File(stream, "text/plain", Path.GetFileName(file));
        });

        machine.MapGet("/plugins/broken", (HttpContext http, AgentContext ctx) =>
            ctx.CurrentUser(http)!.IsAdmin
                ? Results.Json(ctx.Engine.Games.BrokenPlugins().Select(p => new BrokenPluginDto(p.FileName, p.Error)))
                : ApiResults.Forbidden());

        // ── Install / import ──

        machine.MapPost("/servers", (HttpContext http, AgentContext ctx, WindowsGSM.Agent.Hosting.ServerTemplates templates, InstallRequest body) =>
        {
            var user = ctx.CurrentUser(http)!;
            if (!user.CanOnMachine(Capability.Install, ctx.MachineId)) { return ApiResults.Forbidden("You need the Install permission on this machine."); }
            if (ctx.Engine.Games.Get(body.Game) == null) { return ApiResults.BadRequest("That game isn't available on this machine."); }
            if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 100) { return ApiResults.BadRequest("Give the server a name (up to 100 characters)."); }

            var template = string.IsNullOrEmpty(body.Template) ? null : templates.Get(body.Template);
            if (!string.IsNullOrEmpty(body.Template) && (template == null || !string.Equals(template.Game, body.Game, StringComparison.OrdinalIgnoreCase)))
            {
                return ApiResults.BadRequest("That template isn't for this game (or was deleted).");
            }
            // Game files in another folder (another drive): admins only — it writes wherever they point.
            if (!string.IsNullOrWhiteSpace(body.FilesFolder) && !user.IsAdmin) { return ApiResults.Forbidden("Only admins can put a server's files outside the WindowsGSM folder."); }
            var request = ctx.Engine.Provisioning.Install(new EngineInstall(body.Game, body.Name.Trim(), body.SteamBranch, body.SteamBranchPassword, body.Consents, string.IsNullOrWhiteSpace(body.FilesFolder) ? null : body.FilesFolder.Trim()));
            if (request.Accepted && request.Job != null && template != null) { templates.ApplyAfter(request.Job, template); }
            ctx.Record(http, "install", null, request.Accepted, request.Accepted ? $"{body.Game} / {body.Name}" : request.Error);
            return ApiResults.FromRequest(request, ctx);
        });

        // Import copies an existing folder from anywhere on this machine, so it's admin-only.
        machine.MapPost("/servers/import", (HttpContext http, AgentContext ctx, ImportRequest body) =>
        {
            if (!ctx.CurrentUser(http)!.IsAdmin) { return ApiResults.Forbidden("Only admins can import existing server folders."); }
            var request = ctx.Engine.Provisioning.Import(body.Game, body.Name, body.Folder);
            ctx.Record(http, "import", null, request.Accepted, request.Accepted ? $"{body.Game} / {body.Name} <- {body.Folder}" : request.Error);
            return ApiResults.FromRequest(request, ctx);
        });

        // ── Servers ──

        machine.MapGet("/servers", (HttpContext http, AgentContext ctx) =>
        {
            var user = ctx.CurrentUser(http)!;
            return Results.Json(ctx.Engine.Servers.All
                .Where(s => user.Can(Capability.View, ctx.MachineId, s.Id))
                .OrderBy(s => int.TryParse(s.Id, out int n) ? n : int.MaxValue)
                .Select(s => ctx.ToDto(s, user)));
        });

        // ── Jobs ──

        machine.MapGet("/jobs", (HttpContext http, AgentContext ctx, int? limit) =>
        {
            var user = ctx.CurrentUser(http)!;
            return Results.Json(ctx.Engine.Jobs.Snapshot()
                .Where(j => ctx.CanSeeJob(user, j.ServerId))
                .Take(Math.Clamp(limit ?? 50, 1, 200))
                .Select(ctx.ToDto));
        });

        machine.MapGet("/jobs/{jobId}", (HttpContext http, AgentContext ctx, string jobId) =>
        {
            var job = ctx.Engine.Jobs.Get(jobId)?.Snapshot();
            return job != null && ctx.CanSeeJob(ctx.CurrentUser(http)!, job.ServerId) ? Results.Json(ctx.ToDto(job)) : ApiResults.NotFound("No such job.");
        });

        machine.MapPost("/jobs/{jobId}/cancel", (HttpContext http, AgentContext ctx, string jobId) =>
        {
            var user = ctx.CurrentUser(http)!;
            var job = ctx.Engine.Jobs.Get(jobId)?.Snapshot();
            if (job == null || !ctx.CanSeeJob(user, job.ServerId)) { return ApiResults.NotFound("No such job."); }
            if (!ctx.CanSteerJob(user, job)) { return ApiResults.Forbidden(); }
            bool ok = ctx.Engine.Jobs.Cancel(jobId);
            ctx.Record(http, "job-cancel", job.ServerId, ok, job.Title);
            return ok ? Results.Accepted() : ApiResults.Conflict("That job has already finished.");
        });

        // ── Questions from plugins ("Accept the EULA?") waiting on a person ──

        machine.MapGet("/prompts", (HttpContext http, AgentContext ctx) =>
        {
            var user = ctx.CurrentUser(http)!;
            return Results.Json(ctx.Engine.Prompts.Pending().Where(p => ctx.CanSeeJob(user, p.ServerId)).Select(ctx.ToDto));
        });

        machine.MapPost("/prompts/{promptId}", (HttpContext http, AgentContext ctx, string promptId, PromptAnswer body) =>
        {
            var user = ctx.CurrentUser(http)!;
            var prompt = ctx.Engine.Prompts.Get(promptId);
            var job = prompt == null ? null : ctx.Engine.Jobs.Get(prompt.JobId)?.Snapshot();
            if (prompt == null || job == null || !ctx.CanSeeJob(user, prompt.ServerId)) { return ApiResults.NotFound("That question has already been answered or has expired."); }
            if (!ctx.CanSteerJob(user, job)) { return ApiResults.Forbidden(); }
            bool ok = ctx.Engine.Prompts.Answer(promptId, body.Answer, user.Username);
            ctx.Record(http, "prompt-answer", prompt.ServerId, ok, $"{prompt.Title}: {(body.Answer ? "yes" : "no")}");
            return ok ? Results.NoContent() : ApiResults.NotFound("That question has already been answered or has expired.");
        });
    }

    private static MachineDto This(AgentContext ctx) =>
        new(ctx.MachineId, ctx.Settings.MachineName, true, true, WgsmEnvironment.Version, DateTimeOffset.UtcNow,
            ctx.Metrics.Sample(), ctx.Engine.Servers.All.Count);
}
