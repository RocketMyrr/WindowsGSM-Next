using WindowsGSM.Agent.Hosting;

namespace WindowsGSM.Agent.Api;

/// <summary>Community plugins on a machine: what's installed, what's on GitHub, and installing/removing (owners).</summary>
public static class PluginEndpoints
{
    public sealed record InstallPluginRequest(string Repo);

    private static async Task<byte[]> ReadAll(IFormFile file, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        await using var stream = file.OpenReadStream();
        await stream.CopyToAsync(buffer, token);
        return buffer.ToArray();
    }

    public static void Map(RouteGroupBuilder api)
    {
        var machine = api.MapGroup("/machines/{machine}/plugins").RequireMachine();

        machine.MapGet("", (HttpContext http, AgentContext ctx, PluginStore plugins) =>
            ctx.CurrentUser(http)!.IsAdmin ? Results.Json(plugins.Installed()) : ApiResults.Forbidden());

        machine.MapGet("/catalog", async (HttpContext http, AgentContext ctx, PluginStore plugins, string? q) =>
        {
            if (!ctx.CurrentUser(http)!.IsAdmin) { return ApiResults.Forbidden(); }
            try { return Results.Json(await plugins.SearchAsync(q, http.RequestAborted)); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                return ApiResults.Error(502, "github_unavailable", ex is InvalidOperationException ? ex.Message : $"Couldn't search GitHub: {ex.Message}");
            }
        });

        // Plugin logos (admins browse this page). A week in the browser: they rarely change.
        machine.MapGet("/{file}/icon", (HttpContext http, AgentContext ctx, string file) =>
        {
            if (!ctx.CurrentUser(http)!.IsAdmin) { return ApiResults.Forbidden(); }
            string? path = PluginStore.IconThumbnail(file);
            if (path == null) { return ApiResults.NotFound(); }
            http.Response.Headers.CacheControl = "private, max-age=604800";
            return Results.File(path, "image/png");
        });

        machine.MapGet("/catalog/icon", async (HttpContext http, AgentContext ctx, PluginStore plugins, string repo, string? branch, string game) =>
        {
            if (!ctx.CurrentUser(http)!.IsAdmin) { return ApiResults.Forbidden(); }
            string? path = await plugins.CatalogIconAsync(repo, string.IsNullOrWhiteSpace(branch) ? "main" : branch, game, http.RequestAborted);
            if (path == null) { return ApiResults.NotFound(); }
            http.Response.Headers.CacheControl = "private, max-age=604800";
            return Results.File(path, "image/png");
        });

        machine.MapPost("", async (HttpContext http, AgentContext ctx, PluginStore plugins, InstallPluginRequest body) =>
        {
            if (!ctx.CurrentUser(http)!.IsOwner) { return ApiResults.Forbidden("Only owners can install plugins — they run as code on this machine."); }
            try
            {
                var installed = await plugins.InstallAsync(body.Repo?.Trim() ?? "", http.RequestAborted);
                ctx.Record(http, "plugin-install", null, installed.Loaded, $"{body.Repo} → {installed.File}{(installed.Loaded ? "" : ": " + installed.Error)}");
                return Results.Json(installed);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException or InvalidDataException or IOException)
            {
                ctx.Record(http, "plugin-install", null, false, $"{body.Repo}: {ex.Message}");
                return ApiResults.BadRequest(ex.Message);
            }
        });

        // Adding a plugin by hand: its .cs file (plus an optional logo PNG), or a .zip of the plugin folder.
        machine.MapPost("/upload", async (HttpContext http, AgentContext ctx, PluginStore plugins) =>
        {
            if (!ctx.CurrentUser(http)!.IsOwner) { return ApiResults.Forbidden("Only owners can add plugins — they run as code on this machine."); }
            long max = PluginStore.MaxDownload + 2 * 1024 * 1024;
            var size = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (size is { IsReadOnly: false }) { size.MaxRequestBodySize = max; }
            http.Features.Set<Microsoft.AspNetCore.Http.Features.IFormFeature>(new Microsoft.AspNetCore.Http.Features.FormFeature(http.Request,
                new Microsoft.AspNetCore.Http.Features.FormOptions { MultipartBodyLengthLimit = max }));
            if (!http.Request.HasFormContentType) { return ApiResults.BadRequest("Expected a file upload."); }

            string name = "";
            try
            {
                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault(f => f.Name != "logo");
                if (file == null) { return ApiResults.BadRequest("Choose the plugin's .cs file or a .zip."); }
                name = file.FileName;
                byte[] content = await ReadAll(file, http.RequestAborted);
                byte[]? logo = form.Files.GetFile("logo") is { Length: > 0 } l ? await ReadAll(l, http.RequestAborted) : null;
                var installed = await plugins.InstallFileAsync(name, content, logo, http.RequestAborted);
                ctx.Record(http, "plugin-install", null, installed.Loaded, $"{name} (uploaded) → {installed.File}{(installed.Loaded ? "" : ": " + installed.Error)}");
                return Results.Json(installed);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or IOException)
            {
                ctx.Record(http, "plugin-install", null, false, $"{name} (uploaded): {ex.Message}");
                return ApiResults.BadRequest(ex.Message);
            }
        }).DisableAntiforgery();

        machine.MapPost("/{file}/previous", async (HttpContext http, AgentContext ctx, PluginStore plugins, string file) =>
        {
            if (!ctx.CurrentUser(http)!.IsOwner) { return ApiResults.Forbidden("Only owners can change plugins — they run as code on this machine."); }
            try
            {
                var restored = await plugins.RestorePreviousAsync(file, http.RequestAborted);
                ctx.Record(http, "plugin-install", null, restored.Loaded, $"{file}: previous version restored{(restored.Loaded ? "" : ": " + restored.Error)}");
                return Results.Json(restored);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
            {
                return ApiResults.BadRequest(ex.Message);
            }
        });

        machine.MapDelete("/{file}", async (HttpContext http, AgentContext ctx, PluginStore plugins, string file) =>
        {
            if (!ctx.CurrentUser(http)!.IsOwner) { return ApiResults.Forbidden("Only owners can remove plugins."); }
            string? problem = await plugins.RemoveAsync(file);
            ctx.Record(http, "plugin-remove", null, problem == null, problem ?? file);
            return problem == null ? Results.NoContent() : ApiResults.BadRequest(problem);
        });
    }
}
