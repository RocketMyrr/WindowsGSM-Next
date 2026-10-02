using System.Text.RegularExpressions;
using WindowsGSM.Agent.Hosting;

namespace WindowsGSM.Agent.Api;

/// <summary>
/// Updating WindowsGSM itself, per machine (/machines/{m}/agent/update…): on a hub the same calls reach every
/// member, which is how "update all machines" works. Admins can look and check; owners install and roll back.
/// </summary>
public static class UpdateEndpoints
{
    public sealed record UpdateSettingsRequest(string Repo, bool Prerelease);

    public static void Map(RouteGroupBuilder api)
    {
        // Restart the agent (owners; e.g. from a phone after changing a setting that needs it). Game servers keep
        // running. Only an installed copy can do this: the launcher starts the agent again once this one has gone.
        api.MapGroup("/machines/{machine}/agent/restart").RequireMachine().MapPost("", (HttpContext http, AgentContext ctx, SelfUpdate update, IHostApplicationLifetime life) =>
        {
            if (!ctx.CurrentUser(http)!.IsOwner) { return ApiResults.Forbidden("Only owners can restart WindowsGSM."); }
            if (update.Launcher == null || !File.Exists(update.Launcher)) { return ApiResults.BadRequest("This copy wasn't installed with setup, so it can't restart itself — restart it on that computer."); }
            ctx.Record(http, "agent-restart", null, true, null);
            ctx.Engine.Log.Write("Agent", "Restarting the agent (from the panel). Game servers keep running.");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(update.Launcher, $"--agent-start --quiet --wait-pid {Environment.ProcessId}")
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(update.Launcher)!,
            });
            _ = Task.Run(async () => { await Task.Delay(1000); life.StopApplication(); });
            return Results.Json(new { restarting = true }, statusCode: 202);
        });

        var machine = api.MapGroup("/machines/{machine}/agent/update").RequireMachine();

        machine.MapGet("", (HttpContext http, AgentContext ctx, SelfUpdate update) =>
            ctx.CurrentUser(http)!.IsAdmin ? Results.Json(ToDto(update, ctx)) : ApiResults.Forbidden());

        machine.MapPost("/check", async (HttpContext http, AgentContext ctx, SelfUpdate update) =>
        {
            if (!ctx.CurrentUser(http)!.IsAdmin) { return ApiResults.Forbidden(); }
            await update.CheckAsync();
            return Results.Json(ToDto(update, ctx));
        });

        machine.MapPost("/apply", (HttpContext http, AgentContext ctx, SelfUpdate update, IHostApplicationLifetime life) =>
        {
            if (!ctx.CurrentUser(http)!.IsOwner) { return ApiResults.Forbidden("Only owners can update WindowsGSM."); }
            if (!update.Installed) { return ApiResults.BadRequest("This copy wasn't installed with setup, so it can't update itself."); }
            if (!update.Available) { return ApiResults.BadRequest("There's no newer version to install."); }
            ctx.Record(http, "app-update", null, true, $"{update.Current} → {update.Latest!.Version}");
            // Downloading takes a while: carry on in the background; the page polls the status.
            _ = Task.Run(() => update.ApplyAsync(() => { life.StopApplication(); return Task.CompletedTask; }));
            return Results.Accepted();
        });

        machine.MapPost("/rollback", (HttpContext http, AgentContext ctx, SelfUpdate update, IHostApplicationLifetime life) =>
        {
            if (!ctx.CurrentUser(http)!.IsOwner) { return ApiResults.Forbidden("Only owners can roll back WindowsGSM."); }
            string? previous = update.Previous;
            string? problem = update.Rollback(() => { life.StopApplication(); return Task.CompletedTask; });
            ctx.Record(http, "app-rollback", null, problem == null, problem ?? $"{update.Current} → {previous}");
            return problem == null ? Results.Accepted() : ApiResults.BadRequest(problem);
        });

        machine.MapPut("/settings", async (HttpContext http, AgentContext ctx, SelfUpdate update, UpdateSettingsRequest body) =>
        {
            if (!ctx.CurrentUser(http)!.IsOwner) { return ApiResults.Forbidden(); }
            string repo = (body.Repo ?? "").Trim();
            bool isUrl = Uri.TryCreate(repo, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);
            if (!isUrl && !Regex.IsMatch(repo, @"^[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}$")) { return ApiResults.BadRequest("The update feed is a GitHub repository (owner/name) or the address of a JSON feed."); }
            ctx.Settings.UpdateRepo = repo;
            ctx.Settings.UpdatePrerelease = body.Prerelease;
            ctx.Settings.Save();
            ctx.Record(http, "app-update-settings", null, true, $"{repo}{(body.Prerelease ? " (incl. pre-releases)" : "")}");
            await update.CheckAsync();
            return Results.Json(ToDto(update, ctx));
        });
    }

    private static object ToDto(SelfUpdate u, AgentContext ctx) => new
    {
        installed = u.Installed,
        current = u.Current,
        latest = u.Latest?.Version,
        latestName = u.Latest?.Name,
        notes = u.Latest?.Notes,
        published = u.Latest?.Published,
        size = u.Latest?.Size,
        available = u.Available,
        previous = u.Previous,
        state = u.State.ToString(),
        percent = u.Percent,
        error = u.Error,
        checkedAt = u.CheckedAt,
        repo = ctx.Settings.UpdateRepo,
        prerelease = ctx.Settings.UpdatePrerelease,
    };
}
