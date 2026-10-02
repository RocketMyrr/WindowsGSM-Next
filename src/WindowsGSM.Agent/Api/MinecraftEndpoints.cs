using WindowsGSM.Agent.Hosting;
using WindowsGSM.Contracts;
using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Engine.Services;
using WindowsGSM.Installer;

namespace WindowsGSM.Agent.Api;

/// <summary>Minecraft: Java Edition servers: which server software and version, and plugins/mods from Modrinth.</summary>
public static class MinecraftEndpoints
{
    public const string JavaGame = "Minecraft: Java Edition Server";

    private static bool IsJava(ServerInstance s) => string.Equals(s.Game, JavaGame, StringComparison.OrdinalIgnoreCase);

    public static void Map(RouteGroupBuilder server)
    {
        var mc = server.MapGroup("/minecraft");

        mc.MapGet("", (HttpContext http, AgentContext ctx, Modrinth modrinth) =>
        {
            var s = ctx.Engine.Servers.Get(Scopes.Server(http).Id)!;
            if (!IsJava(s)) { return ApiResults.BadRequest("Only for Minecraft: Java Edition servers."); }
            var installed = MinecraftSoftware.Read(s.Id);
            var c = Modrinth.ContextFor(s.Id);
            return Results.Json(new
            {
                installed,
                flavors = MinecraftSoftware.Flavors,
                addons = c == null ? null : new
                {
                    kind = c.ProjectType, folder = c.Folder,
                    tracked = modrinth.List(s.Id),
                    others = modrinth.Others(s.Id, c),
                },
            });
        }).Needs(Capability.View);

        mc.MapGet("/versions", async (HttpContext http, AgentContext ctx, string flavor) =>
        {
            if (MinecraftSoftware.Find(flavor) == null) { return ApiResults.BadRequest("Unknown server software."); }
            try { return Results.Json(await MinecraftSoftware.VersionsAsync(flavor.ToLowerInvariant(), http.RequestAborted)); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException)
            {
                return ApiResults.Error(502, "unavailable", $"Couldn't get the version list: {ex.Message}");
            }
        }).Needs(Capability.Update);

        // Switch software or version: a job, like an update (the server must be stopped).
        mc.MapPost("/software", (HttpContext http, AgentContext ctx, MinecraftSoftwareRequest body) =>
        {
            var s = ctx.Engine.Servers.Get(Scopes.Server(http).Id)!;
            if (!IsJava(s)) { return ApiResults.BadRequest("Only for Minecraft: Java Edition servers."); }
            var flavor = MinecraftSoftware.Find(body.Flavor);
            string version = body.Version?.Trim() ?? "";
            if (flavor == null) { return ApiResults.BadRequest("Pick the server software."); }
            if (version.Length is 0 or > 32 || version.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_'))) { return ApiResults.BadRequest("Pick a version."); }
            if (s.State != ServerState.Stopped) { return ApiResults.Conflict($"Stop {s.Name} first."); }
            if (!ctx.Engine.Operations.TryBegin(s.Id, OperationKind.Update, "Server software", out var lease, out var busy)) { return ApiResults.Conflict($"{s.Name} is busy: {busy}."); }
            var request = OperationRequest.Running(ctx.Engine.Jobs.Start("update", s.Id, $"Install {flavor.Name} {version} on {s.Name}", async job =>
            {
                using (lease)
                {
                    s.SetState(ServerState.Updating);
                    try
                    {
                        job.Report(0, $"Downloading {flavor.Name}");
                        var info = await MinecraftSoftware.InstallAsync(s.Id, flavor.Id, version, job.Log, job.Cancellation);
                        ctx.Engine.Log.Write(s.Id, $"Server software: {flavor.Name} {info.Version}{(info.Build != null ? $" build {info.Build}" : "")}.");
                        return null;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or ArgumentException or System.Text.Json.JsonException)
                    {
                        return $"Couldn't install {flavor.Name} {version}: {ex.Message}";
                    }
                    finally { s.SetState(ServerState.Stopped); }
                }
            }));
            ctx.Record(http, "minecraft-software", s.Id, true, $"{flavor.Name} {version}");
            return ApiResults.FromRequest(request, ctx);
        }).Needs(Capability.Update);

        mc.MapGet("/addons/search", async (HttpContext http, AgentContext ctx, Modrinth modrinth, string? q) =>
        {
            var s = Scopes.Server(http);
            var c = Modrinth.ContextFor(s.Id);
            if (c == null) { return ApiResults.BadRequest("Install Paper, Purpur or Fabric first — Vanilla can't load plugins or mods."); }
            try { return Results.Json(await modrinth.SearchAsync(s.Id, c, (q ?? "").Trim(), http.RequestAborted)); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return ApiResults.Error(502, "unavailable", $"Couldn't reach Modrinth: {ex.Message}"); }
        }).Needs(Capability.Addons);

        mc.MapPost("/addons", async (HttpContext http, AgentContext ctx, Modrinth modrinth, ModrinthInstallRequest body) =>
        {
            var s = Scopes.Server(http);
            var c = Modrinth.ContextFor(s.Id);
            if (c == null) { return ApiResults.BadRequest("Install Paper, Purpur or Fabric first."); }
            if (string.IsNullOrWhiteSpace(body.ProjectId) || body.ProjectId.Length > 64) { return ApiResults.BadRequest("Pick a project."); }
            try
            {
                var done = await modrinth.InstallAsync(s.Id, c, body.ProjectId.Trim(), http.RequestAborted);
                ctx.Record(http, "modrinth", s.Id, true, "installed " + string.Join(", ", done.Select(d => $"{d.Title} {d.VersionNumber}")));
                ctx.Engine.Log.Write(s.Id, $"Modrinth: installed {string.Join(", ", done.Select(d => $"{d.Title} {d.VersionNumber}"))}.");
                return Results.Json(new { installed = done, restart = ctx.Engine.Servers.Get(s.Id)!.State != ServerState.Stopped });
            }
            catch (InvalidOperationException ex) { return ApiResults.BadRequest(ex.Message); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return ApiResults.Error(502, "unavailable", $"Couldn't reach Modrinth: {ex.Message}"); }
        }).Needs(Capability.Addons);

        mc.MapPost("/addons/update", async (HttpContext http, AgentContext ctx, Modrinth modrinth) =>
        {
            var s = ctx.Engine.Servers.Get(Scopes.Server(http).Id)!;
            var c = Modrinth.ContextFor(s.Id);
            if (c == null) { return ApiResults.BadRequest("Install Paper, Purpur or Fabric first."); }
            if (s.State != ServerState.Stopped) { return ApiResults.Conflict($"Stop {s.Name} first — a running server has its {c.Folder} open."); }
            try
            {
                var changes = await modrinth.UpdateAllAsync(s.Id, c, http.RequestAborted);
                ctx.Record(http, "modrinth", s.Id, true, changes.Count == 0 ? "all up to date" : "updated " + string.Join("; ", changes));
                if (changes.Count > 0) { ctx.Engine.Log.Write(s.Id, "Modrinth: " + string.Join("; ", changes)); }
                return Results.Json(new { changes });
            }
            catch (InvalidOperationException ex) { return ApiResults.BadRequest(ex.Message); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return ApiResults.Error(502, "unavailable", $"Couldn't reach Modrinth: {ex.Message}"); }
        }).Needs(Capability.Addons);

        mc.MapDelete("/addons/{project}", (HttpContext http, AgentContext ctx, Modrinth modrinth, string project) =>
        {
            var s = ctx.Engine.Servers.Get(Scopes.Server(http).Id)!;
            var c = Modrinth.ContextFor(s.Id);
            if (c == null) { return ApiResults.BadRequest("Nothing installed."); }
            if (s.State != ServerState.Stopped) { return ApiResults.Conflict($"Stop {s.Name} first."); }
            if (!modrinth.Remove(s.Id, c, project)) { return ApiResults.NotFound("Not installed by WindowsGSM."); }
            ctx.Record(http, "modrinth", s.Id, true, "removed " + project);
            return Results.NoContent();
        }).Needs(Capability.Addons);
    }
}

public sealed record MinecraftSoftwareRequest(string? Flavor, string? Version);
public sealed record ModrinthInstallRequest(string? ProjectId);
