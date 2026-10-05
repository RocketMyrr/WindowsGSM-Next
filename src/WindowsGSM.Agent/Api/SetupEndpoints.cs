using WindowsGSM.Agent.Hosting;
using WindowsGSM.Hosting;

namespace WindowsGSM.Agent.Api;

/// <summary>
/// Diagnostics for bug reports (admins), and backing up / restoring WindowsGSM's own setup (owners).
/// </summary>
public static class SetupEndpoints
{
    public sealed record SetupBackupRequest(string? Passphrase);

    public static void Map(RouteGroupBuilder api)
    {
        var machine = api.MapGroup("/machines/{machine}").RequireMachine();

        machine.MapGet("/diagnostics", async (HttpContext http, AgentContext ctx) =>
        {
            if (!ctx.CurrentUser(http)!.IsAdmin) { return ApiResults.Forbidden("Only admins and owners can export diagnostics."); }
            var ms = new MemoryStream();
            await Diagnostics.WriteAsync(ctx, ms);
            ms.Position = 0;
            ctx.Record(http, "diagnostics", null, true);
            return Results.File(ms, "application/zip", $"wgsm-diagnostics-{Safe(ctx.Settings.MachineName)}-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        });

        // POST, not GET: the passphrase mustn't end up in an address (history, logs).
        machine.MapPost("/setup-backup", (HttpContext http, AgentContext ctx, SetupBackupRequest body) =>
        {
            if (!ctx.CurrentUser(http)!.IsOwner) { return ApiResults.Forbidden("Only owners can back up WindowsGSM's setup — it includes everyone's accounts."); }
            string? pass = string.IsNullOrEmpty(body.Passphrase) ? null : body.Passphrase;
            if (pass != null && pass.Length < 8) { return ApiResults.BadRequest("Use a passphrase of at least 8 characters."); }
            var ms = new MemoryStream();
            var result = SetupArchive.Export(WgsmEnvironment.DataRoot, ms, WgsmEnvironment.Version, ctx.MachineId, ctx.Settings.MachineName, pass);
            ms.Position = 0;
            ctx.Record(http, "setup-backup", null, true, $"{result.Files} files" + (pass != null ? $", {result.SecretsIncluded} password(s)/token(s) included" : $", {result.SecretsLeftOut} password(s)/token(s) left out"));
            http.Response.Headers["X-WGSM-Secrets-Included"] = result.SecretsIncluded.ToString();
            http.Response.Headers["X-WGSM-Secrets-Left-Out"] = result.SecretsLeftOut.ToString();
            return Results.File(ms, "application/zip", $"wgsm-setup-{Safe(ctx.Settings.MachineName)}-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        });

        machine.MapGet("/setup-restore", (HttpContext http, AgentContext ctx) =>
        {
            if (!ctx.CurrentUser(http)!.IsOwner) { return ApiResults.Forbidden(); }
            var pending = SetupArchive.PendingRestore(WgsmEnvironment.DataRoot);
            var last = File.Exists(SetupArchive.ResultFile(WgsmEnvironment.DataRoot)) ? SafeJson.Read<SetupArchive.Result>(SetupArchive.ResultFile(WgsmEnvironment.DataRoot)) : null;
            return Results.Json(new
            {
                pending = pending == null ? null : new { from = pending.Manifest.MachineName, created = pending.Manifest.CreatedAt, version = pending.Manifest.Version, pending.KeepIdentity, pending.StagedAt, by = pending.RequestedBy },
                last,
            });
        });

        machine.MapPost("/setup-restore", async (HttpContext http, AgentContext ctx) =>
        {
            var user = ctx.CurrentUser(http)!;
            if (!user.IsOwner) { return ApiResults.Forbidden("Only owners can restore WindowsGSM's setup."); }
            long max = 210L * 1024 * 1024;
            var size = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (size is { IsReadOnly: false }) { size.MaxRequestBodySize = max; }
            http.Features.Set<Microsoft.AspNetCore.Http.Features.IFormFeature>(new Microsoft.AspNetCore.Http.Features.FormFeature(http.Request,
                new Microsoft.AspNetCore.Http.Features.FormOptions { MultipartBodyLengthLimit = max }));
            if (!http.Request.HasFormContentType) { return ApiResults.BadRequest("Expected the backup file."); }
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file == null || file.Length == 0) { return ApiResults.BadRequest("Choose the setup backup (.zip) to restore."); }
            string? pass = form["passphrase"].ToString() is { Length: > 0 } p ? p : null;
            bool keepIdentity = string.Equals(form["keepIdentity"], "true", StringComparison.OrdinalIgnoreCase);
            try
            {
                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, http.RequestAborted);
                buffer.Position = 0;
                var staged = SetupArchive.Stage(WgsmEnvironment.DataRoot, buffer, pass, keepIdentity, user.Username);
                ctx.Record(http, "setup-restore", null, true, $"staged from {staged.Manifest.MachineName} ({staged.Manifest.CreatedAt:yyyy-MM-dd HH:mm}), {staged.Files} files — applies at the next agent start");
                return Results.Json(new
                {
                    from = staged.Manifest.MachineName, created = staged.Manifest.CreatedAt, version = staged.Manifest.Version, staged.Files,
                    secretsIncluded = staged.Manifest.SecretsIncluded, secretsLeftOut = staged.Manifest.SecretsLeftOut,
                    serversSkipped = staged.ServersSkipped, sameMachine = staged.Manifest.MachineId == ctx.MachineId,
                });
            }
            catch (InvalidDataException ex) { ctx.Record(http, "setup-restore", null, false, ex.Message); return ApiResults.BadRequest(ex.Message); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { return ApiResults.BadRequest("That file isn't a readable setup backup: " + ex.Message); }
        }).DisableAntiforgery();

        machine.MapDelete("/setup-restore", (HttpContext http, AgentContext ctx) =>
        {
            if (!ctx.CurrentUser(http)!.IsOwner) { return ApiResults.Forbidden(); }
            SetupArchive.CancelPending(WgsmEnvironment.DataRoot);
            ctx.Record(http, "setup-restore", null, true, "cancelled");
            return Results.NoContent();
        });
    }

    private static string Safe(string name) => new string(name.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray()).Trim('-') is { Length: > 0 } s ? s : "machine";
}
