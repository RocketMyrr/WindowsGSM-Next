using WindowsGSM.Agent.Hosting;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Api;

/// <summary>Earlier versions of a server's config files: list, compare with now, put back.</summary>
public static class ConfigHistoryEndpoints
{
    public static void Map(RouteGroupBuilder server)
    {
        // Versions hold what the files held (passwords included), so this needs what editing them needs.
        server.MapGet("/config-history", (HttpContext http, ConfigHistory history, string? path) =>
            Results.Json(history.List(Scopes.Server(http).Id, path))).Needs(Capability.EditConfig);

        // One version, with the file as it is now (for "what changed").
        server.MapGet("/config-history/{version}", (HttpContext http, AgentContext ctx, ConfigHistory history, string version) =>
        {
            string id = Scopes.Server(http).Id;
            if (history.Get(id, version) is not { } found) { return ApiResults.NotFound("That version isn't there any more."); }
            string? current = null;
            try
            {
                string file = ConfigHistory.FileFor(ctx, id, found.Version.Path);
                if (File.Exists(file)) { current = ConfigHistory.Decode(File.ReadAllBytes(file)); }
            }
            catch { /* the file was removed or moved */ }
            return Results.Json(new { version = found.Version, content = found.Content, current });
        }).Needs(Capability.EditConfig);

        server.MapPost("/config-history/{version}/restore", (HttpContext http, AgentContext ctx, ConfigHistory history, string version) =>
        {
            string id = Scopes.Server(http).Id;
            try
            {
                // Putting back older settings mustn't let someone who isn't an admin bring back (or change) a script:
                // for them, the scripts stay as they are now.
                Dictionary<string, string>? keep = null;
                if (!Scopes.User(http).IsAdmin)
                {
                    var now = new WindowsGSM.Functions.ServerConfig(id);
                    keep = new()
                    {
                        [WindowsGSM.Engine.Services.ServerScripts.BeforeStartKey] = now.BatchFile ?? string.Empty,
                        [WindowsGSM.Engine.Services.ServerScripts.AfterStopKey] = now.GetCustomSetting(WindowsGSM.Engine.Services.ServerScripts.AfterStopKey, string.Empty),
                    };
                }
                string path = history.Restore(ctx, id, version, Scopes.User(http).Username);
                if (keep != null && path == ConfigHistory.SettingsPath)
                {
                    foreach (var (key, value) in keep) { WindowsGSM.Functions.ServerConfig.SetSetting(id, key, value); }
                    ctx.Engine.Servers.Get(id)?.ReloadConfig();
                }
                ctx.Record(http, "config-restore", id, true, path);
                return Results.NoContent();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WindowsGSM.Engine.Services.FileOperationException)
            {
                ctx.Record(http, "config-restore", id, false, ex.Message);
                return ApiResults.BadRequest(ex.Message);
            }
        }).Needs(Capability.EditConfig);
    }
}
