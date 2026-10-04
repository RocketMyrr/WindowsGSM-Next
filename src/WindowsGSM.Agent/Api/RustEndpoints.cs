using WindowsGSM.Agent.Hosting;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Api;

/// <summary>Rust servers: plugins from uMod for Oxide or Carbon.</summary>
public static class RustEndpoints
{
    public static void Map(RouteGroupBuilder server)
    {
        var rust = server.MapGroup("/rust");

        // What's installed, which framework, and the "update before start" switch.
        rust.MapGet("", (HttpContext http, AgentContext ctx, UMod umod) =>
        {
            var s = ctx.Engine.Servers.Get(Scopes.Server(http).Id)!;
            if (!UMod.IsRust(s)) { return ApiResults.BadRequest("Only for Rust servers."); }
            var c = UMod.ContextFor(s.Id);
            return Results.Json(new
            {
                framework = c?.Framework, folder = c?.Folder,
                plugins = c == null ? null : umod.Plugins(s.Id, c),
                tracked = umod.List(s.Id),
                updateBeforeStart = s.Config.GetCustomSetting(UModBeforeStart.SettingKey, "") == "1",
            });
        }).Needs(Capability.View);

        // Which tracked plugins have a newer version on uMod (asks uMod: a moment per plugin).
        rust.MapGet("/plugins/updates", async (HttpContext http, AgentContext ctx, UMod umod) =>
        {
            var s = Scopes.Server(http);
            try { return Results.Json(await umod.UpdatesAvailableAsync(s.Id, http.RequestAborted)); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return ApiResults.Error(502, "unavailable", $"Couldn't reach uMod: {ex.Message}"); }
        }).Needs(Capability.View);

        rust.MapGet("/plugins/search", async (HttpContext http, AgentContext ctx, UMod umod, string? q) =>
        {
            var s = Scopes.Server(http);
            if (UMod.ContextFor(s.Id) == null) { return ApiResults.BadRequest("Install Oxide or Carbon first (Add-ons tab) — Rust loads plugins through them."); }
            try { return Results.Json(await umod.SearchAsync(s.Id, (q ?? "").Trim(), http.RequestAborted)); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { return ApiResults.Error(502, "unavailable", $"Couldn't reach uMod: {ex.Message}"); }
        }).Needs(Capability.Addons);

        rust.MapPost("/plugins", async (HttpContext http, AgentContext ctx, UMod umod, UModRequest body) =>
        {
            var s = Scopes.Server(http);
            var c = UMod.ContextFor(s.Id);
            if (c == null) { return ApiResults.BadRequest("Install Oxide or Carbon first (Add-ons tab)."); }
            if (!UMod.IsValidName(body.Name)) { return ApiResults.BadRequest("Pick a plugin."); }
            try
            {
                var done = await umod.InstallAsync(s.Id, c, body.Name!, http.RequestAborted);
                string what = string.Join(", ", done.Select(d => $"{d.Title} {d.Version}"));
                ctx.Record(http, "umod", s.Id, true, "installed " + what);
                if (done.Count > 0) { ctx.Engine.Log.Write(s.Id, $"uMod: installed {what}."); }
                return Results.Json(new { installed = done });
            }
            catch (InvalidOperationException ex) { return ApiResults.BadRequest(ex.Message); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { return ApiResults.Error(502, "unavailable", $"Couldn't reach uMod: {ex.Message}"); }
        }).Needs(Capability.Addons);

        rust.MapPost("/plugins/update", async (HttpContext http, AgentContext ctx, UMod umod) =>
        {
            var s = Scopes.Server(http);
            var c = UMod.ContextFor(s.Id);
            if (c == null) { return ApiResults.BadRequest("Install Oxide or Carbon first (Add-ons tab)."); }
            try
            {
                var (changes, skipped) = await umod.UpdateAllAsync(s.Id, c, http.RequestAborted);
                ctx.Record(http, "umod", s.Id, true, changes.Count == 0 ? "all up to date" : "updated " + string.Join("; ", changes));
                if (changes.Count > 0) { ctx.Engine.Log.Write(s.Id, "uMod: " + string.Join("; ", changes)); }
                return Results.Json(new { changes, skipped });
            }
            catch (InvalidOperationException ex) { return ApiResults.BadRequest(ex.Message); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { return ApiResults.Error(502, "unavailable", $"Couldn't reach uMod: {ex.Message}"); }
        }).Needs(Capability.Addons);

        // A plugin added by hand: keep it up to date from uMod from now on.
        rust.MapPost("/plugins/{name}/track", async (HttpContext http, AgentContext ctx, UMod umod, string name) =>
        {
            var s = Scopes.Server(http);
            var c = UMod.ContextFor(s.Id);
            if (c == null) { return ApiResults.BadRequest("Install Oxide or Carbon first (Add-ons tab)."); }
            try
            {
                var t = await umod.TrackAsync(s.Id, c, name, http.RequestAborted);
                ctx.Record(http, "umod", s.Id, true, $"keeping {t.Title} up to date");
                return Results.Json(t);
            }
            catch (InvalidOperationException ex) { return ApiResults.BadRequest(ex.Message); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { return ApiResults.Error(502, "unavailable", $"Couldn't reach uMod: {ex.Message}"); }
        }).Needs(Capability.Addons);

        rust.MapDelete("/plugins/{name}/track", (HttpContext http, AgentContext ctx, UMod umod, string name) =>
        {
            var s = Scopes.Server(http);
            if (!umod.Untrack(s.Id, name)) { return ApiResults.NotFound("It isn't kept up to date."); }
            ctx.Record(http, "umod", s.Id, true, $"no longer updating {name}");
            return Results.NoContent();
        }).Needs(Capability.Addons);

        rust.MapDelete("/plugins/{name}", (HttpContext http, AgentContext ctx, UMod umod, string name) =>
        {
            var s = Scopes.Server(http);
            var c = UMod.ContextFor(s.Id);
            if (c == null) { return ApiResults.BadRequest("Nothing installed."); }
            if (!umod.Remove(s.Id, c, name)) { return ApiResults.NotFound("Not installed by WindowsGSM — remove it in the Files tab."); }
            ctx.Record(http, "umod", s.Id, true, "removed " + name);
            ctx.Engine.Log.Write(s.Id, $"uMod: removed {name}.");
            return Results.NoContent();
        }).Needs(Capability.Addons);

        // "Update uMod plugins before every start".
        rust.MapPut("/update-before-start", (HttpContext http, AgentContext ctx, UModSettingRequest body) =>
        {
            var s = ctx.Engine.Servers.Get(Scopes.Server(http).Id)!;
            WindowsGSM.Functions.ServerConfig.SetSetting(s.Id, UModBeforeStart.SettingKey, body.On ? "1" : "0");
            s.ReloadConfig();
            ctx.Record(http, "umod", s.Id, true, body.On ? "update before start: on" : "update before start: off");
            return Results.NoContent();
        }).Needs(Capability.Addons);
    }
}

public sealed record UModRequest(string? Name);
public sealed record UModSettingRequest(bool On);
