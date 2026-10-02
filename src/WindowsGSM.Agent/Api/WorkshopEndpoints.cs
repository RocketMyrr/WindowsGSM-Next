using WindowsGSM.Agent.Hosting;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Api;

/// <summary>A server's Steam Workshop mods: settings, add by link, download/update, remove.</summary>
public static class WorkshopEndpoints
{
    public sealed record AddRequest(string Link);
    public sealed record UpdateRequest(bool Everything = false);

    public static void Map(RouteGroupBuilder server)
    {
        server.MapGet("/workshop", (HttpContext http, AgentContext ctx, Workshop workshop) =>
        {
            var s = Scopes.Server(http);
            string? serverApp = ctx.Engine.Games.Get(s.Game)?.AppId;
            object? known = serverApp != null && Workshop.Known.TryGetValue(serverApp, out var k) ? new { appId = k.AppId, style = k.Style, path = k.Path, game = k.Game } : null;
            return Results.Json(new { settings = workshop.Load(s.Id), known, steam = serverApp != null });
        }).Needs(Capability.Addons);

        server.MapPut("/workshop/settings", (HttpContext http, AgentContext ctx, Workshop workshop, WorkshopSettings body) =>
        {
            var s = Scopes.Server(http);
            var current = workshop.Load(s.Id);
            body.Items = current.Items; // the list changes through add/remove only
            body.Path = (body.Path ?? "").Trim().Replace('\\', '/').Trim('/');
            if (Workshop.CheckSettings(body) is { } problem) { return ApiResults.BadRequest(problem); }
            workshop.Save(s.Id, body);
            ctx.Record(http, "workshop-settings", s.Id, true, $"app {body.AppId}, {body.Style}");
            return Results.NoContent();
        }).Needs(Capability.Addons);

        server.MapPost("/workshop/items", async (HttpContext http, AgentContext ctx, Workshop workshop, AddRequest body) =>
        {
            var s = Scopes.Server(http);
            try
            {
                var (added, problems) = await workshop.AddAsync(s.Id, body.Link ?? "", http.RequestAborted);
                ctx.Record(http, "workshop-add", s.Id, added.Count > 0, $"{added.Count} mod(s){(problems.Count > 0 ? "; " + string.Join("; ", problems) : "")}");
                return Results.Json(new { added, problems });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                return ApiResults.Error(502, "steam_unavailable", "Couldn't reach Steam: " + ex.Message);
            }
        }).Needs(Capability.Addons);

        server.MapPost("/workshop/refresh", async (HttpContext http, Workshop workshop) =>
        {
            try { return Results.Json(await workshop.RefreshAsync(Scopes.Server(http).Id, http.RequestAborted)); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                return ApiResults.Error(502, "steam_unavailable", "Couldn't reach Steam: " + ex.Message);
            }
        }).Needs(Capability.Addons);

        server.MapPost("/workshop/update", (HttpContext http, AgentContext ctx, Workshop workshop, UpdateRequest? body) =>
        {
            var s = Scopes.Server(http);
            var request = workshop.Update(s.Id, body?.Everything ?? false);
            ctx.Record(http, "workshop-update", s.Id, request.Accepted, request.Error);
            return ApiResults.FromRequest(request, ctx);
        }).Needs(Capability.Addons);

        server.MapDelete("/workshop/items/{itemId}", (HttpContext http, AgentContext ctx, Workshop workshop, string itemId) =>
        {
            var s = Scopes.Server(http);
            string? problem = workshop.Remove(s.Id, itemId);
            ctx.Record(http, "workshop-remove", s.Id, problem == null, problem ?? itemId);
            return problem == null ? Results.NoContent() : ApiResults.BadRequest(problem);
        }).Needs(Capability.Addons);
    }
}
