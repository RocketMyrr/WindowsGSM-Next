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
                string path = history.Restore(ctx, id, version, Scopes.User(http).Username);
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
