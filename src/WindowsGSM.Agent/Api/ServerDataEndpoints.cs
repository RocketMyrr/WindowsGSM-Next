using WindowsGSM.Agent.Hosting;
using Microsoft.AspNetCore.Http.Features;
using WindowsGSM.Contracts;
using WindowsGSM.Engine.Backups;
using WindowsGSM.Engine.Services;

namespace WindowsGSM.Agent.Api;

/// <summary>One server's files, backups, add-ons and schedules.</summary>
public static class ServerDataEndpoints
{
    /// <summary>Largest upload (mods and archives can be big).</summary>
    public const long MaxUploadBytes = 1024L * 1024 * 1024;

    public static void Map(RouteGroupBuilder server)
    {
        MapFiles(server);
        MapBackups(server);
        MapAddons(server);
        MapSchedules(server);
    }

    // ───────────────────────────── Files ─────────────────────────────

    private static void MapFiles(RouteGroupBuilder server)
    {
        server.MapGet("/files", (HttpContext http, AgentContext ctx, string? path) => Files(() =>
        {
            var entries = ctx.Engine.Files.List(Scopes.Server(http).Id, path, out string normalized);
            return Results.Json(new FolderDto(normalized, entries.Select(e => new FileEntryDto(e.Name, e.IsDirectory, e.Size, e.Modified)).ToList()));
        })).Needs(Capability.Files);

        server.MapGet("/files/content", (HttpContext http, AgentContext ctx, string path) => Files(() =>
        {
            var f = ctx.Engine.Files.Read(Scopes.Server(http).Id, path);
            return Results.Json(new TextFileDto(f.Path, f.Name, f.Size, f.Modified, f.Content, f.Binary, f.ReadOnly, f.Note));
        })).Needs(Capability.Files);

        server.MapPut("/files/content", async (HttpContext http, AgentContext ctx, ConfigHistory history, FileWriteRequest body) =>
        {
            var s = Scopes.Server(http);
            try
            {
                string full = ctx.Engine.Files.Resolve(s.Id, body.Path);
                history.Keep(s.Id, ctx.Engine.Files.Relative(s.Id, full), full, Scopes.User(http).Username, "File editor", body.Content);
                await ctx.Engine.Files.WriteAsync(s.Id, body.Path, body.Content, body.ExpectedModified, http.RequestAborted);
                ctx.Record(http, "file-edit", s.Id, true, body.Path);
                return Results.NoContent();
            }
            catch (FileOperationException ex) { ctx.Record(http, "file-edit", s.Id, false, $"{body.Path}: {ex.Message}"); return ApiResults.FromFileProblem(ex); }
        }).Needs(Capability.Files);

        server.MapGet("/files/download", (HttpContext http, AgentContext ctx, string path) => Files(() =>
        {
            string full = ctx.Engine.Files.ForDownload(Scopes.Server(http).Id, path);
            return Results.File(full, "application/octet-stream", Path.GetFileName(full), enableRangeProcessing: true);
        })).Needs(Capability.Files);

        server.MapPost("/files/folder", (HttpContext http, AgentContext ctx, CreateFolderRequest body) =>
            FileChange(http, ctx, "mkdir", () => ctx.Engine.Files.CreateFolder(Scopes.Server(http).Id, body.Path, body.Name))).Needs(Capability.Files);

        server.MapPost("/files/rename", (HttpContext http, AgentContext ctx, RenameRequest body) =>
            FileChange(http, ctx, "rename", () => body.Path + " → " + ctx.Engine.Files.Rename(Scopes.Server(http).Id, body.Path, body.NewName))).Needs(Capability.Files);

        server.MapPost("/files/delete", (HttpContext http, AgentContext ctx, PathRequest body) =>
            FileChange(http, ctx, "file-delete", () => { ctx.Engine.Files.Delete(Scopes.Server(http).Id, body.Path); return body.Path; })).Needs(Capability.Files);

        server.MapPost("/files/upload", async (HttpContext http, AgentContext ctx, string? path) =>
        {
            var s = Scopes.Server(http);
            // Two separate limits apply to big uploads: Kestrel's request body cap and the form reader's own
            // multipart cap (128 MB by default). Raising only the first still failed anything over 128 MB.
            var size = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (size is { IsReadOnly: false }) { size.MaxRequestBodySize = MaxUploadBytes; }
            http.Features.Set<IFormFeature>(new FormFeature(http.Request, new FormOptions { MultipartBodyLengthLimit = MaxUploadBytes }));
            if (!http.Request.HasFormContentType) { return ApiResults.BadRequest("Expected a file upload."); }

            IFormCollection form;
            try { form = await http.Request.ReadFormAsync(http.RequestAborted); }
            catch (Exception ex) { return ApiResults.BadRequest("Upload failed: " + ex.Message); }

            var saved = new List<string>();
            var failed = new List<string>();
            foreach (var file in form.Files)
            {
                try
                {
                    await using var stream = file.OpenReadStream();
                    saved.Add(await ctx.Engine.Files.SaveUploadAsync(s.Id, path, file.FileName, stream, http.RequestAborted));
                }
                catch (FileOperationException ex) { failed.Add($"{file.FileName}: {ex.Message}"); }
                catch (IOException ex) { failed.Add($"{file.FileName}: {ex.Message}"); }
            }
            ctx.Record(http, "upload", s.Id, saved.Count > 0, $"{path}/ ({saved.Count} saved{(failed.Count > 0 ? $", {failed.Count} failed" : "")})");
            if (saved.Count == 0) { return ApiResults.BadRequest("No files were uploaded.", failed); }
            return Results.Json(new { saved, failed });
        }).Needs(Capability.Files).DisableAntiforgery();
    }

    private static IResult Files(Func<IResult> action)
    {
        try { return action(); }
        catch (FileOperationException ex) { return ApiResults.FromFileProblem(ex); }
        catch (UnauthorizedAccessException) { return ApiResults.Forbidden("Windows denied access to that file."); }
        catch (IOException ex) { return ApiResults.Conflict(ex.Message); }
    }

    private static IResult FileChange(HttpContext http, AgentContext ctx, string action, Func<string> change)
    {
        var s = Scopes.Server(http);
        try
        {
            string detail = change();
            ctx.Record(http, action, s.Id, true, detail);
            return Results.NoContent();
        }
        catch (FileOperationException ex) { ctx.Record(http, action, s.Id, false, ex.Message); return ApiResults.FromFileProblem(ex); }
        catch (UnauthorizedAccessException) { ctx.Record(http, action, s.Id, false, "access denied"); return ApiResults.Forbidden("Windows denied access to that file."); }
        catch (IOException ex) { ctx.Record(http, action, s.Id, false, ex.Message); return ApiResults.Conflict(ex.Message); }
    }

    // ───────────────────────────── Backups ─────────────────────────────

    private static void MapBackups(RouteGroupBuilder server)
    {
        server.MapGet("/backups", (HttpContext http, AgentContext ctx) =>
            Results.Json(ctx.Engine.Backups.List(Scopes.Server(http).Id).Select(b => new BackupDto(b.Name, b.Size, b.Created, b.Format.ToString()))))
            .Needs(Capability.Backup);

        server.MapPost("/backups", (HttpContext http, AgentContext ctx, BackupRequest? body) =>
        {
            var s = Scopes.Server(http);
            var request = ctx.Engine.Backups.Backup(s.Id, everything: body?.Everything ?? false);
            ctx.Record(http, "backup", s.Id, request.Accepted, request.Error);
            return ApiResults.FromRequest(request, ctx);
        }).Needs(Capability.Backup);

        server.MapDelete("/backups/{name}", (HttpContext http, AgentContext ctx, string name) =>
        {
            var s = Scopes.Server(http);
            bool ok = ctx.Engine.Backups.Delete(s.Id, name);
            ctx.Record(http, "backup-delete", s.Id, ok, name);
            return ok ? Results.NoContent() : ApiResults.NotFound("No such backup.");
        }).Needs(Capability.Backup);

        server.MapPost("/backups/{name}/test", (HttpContext http, AgentContext ctx, string name) =>
        {
            var s = Scopes.Server(http);
            var request = ctx.Engine.Backups.Verify(s.Id, name);
            ctx.Record(http, "backup-test", s.Id, request.Accepted, request.Accepted ? name : request.Error);
            return ApiResults.FromRequest(request, ctx);
        }).Needs(Capability.Backup);

        server.MapGet("/backups/{name}/download", (HttpContext http, AgentContext ctx, string name) =>
        {
            string? full = ctx.Engine.Backups.ResolveArchive(Scopes.Server(http).Id, name);
            return full == null ? ApiResults.NotFound("No such backup.") : Results.File(full, "application/zip", Path.GetFileName(full), enableRangeProcessing: true);
        }).Needs(Capability.Backup);

        server.MapPost("/restore", (HttpContext http, AgentContext ctx, RestoreRequest body) =>
        {
            var s = Scopes.Server(http);
            // Someone who can't choose scripts doesn't get one back from an old backup's settings either.
            var keep = Scopes.User(http).IsAdmin ? null : WindowsGSM.Engine.Services.ServerScripts.AdminKeys;
            var request = ctx.Engine.Backups.Restore(s.Id, body.Name, body.IncludeConfig, keep);
            ctx.Record(http, "restore", s.Id, request.Accepted, request.Accepted ? body.Name : request.Error);
            return ApiResults.FromRequest(request, ctx);
        }).Needs(Capability.Restore);

        server.MapGet("/backups/settings", (HttpContext http, WindowsGSM.Agent.Hosting.OffsiteBackups offsite) =>
        {
            var b = BackupSettings.Load(Scopes.Server(http).Id);
            return Results.Json(new BackupSettingsDto(b.Paths, b.ExternalLocations, b.BeforeStart, b.KeepCount, b.KeepDays, b.Location, b.CopyTo, b.UploadOffsite, offsite.Settings.Ready));
        }).Needs(Capability.Backup);

        server.MapPut("/backups/settings", (HttpContext http, AgentContext ctx, BackupSettingsDto body) =>
        {
            var s = Scopes.Server(http);
            var user = Scopes.User(http);
            var b = BackupSettings.Load(s.Id);

            // Where archives go, and folders outside the server, reach beyond the server's own files: a
            // backup of any folder can then be downloaded. Only admins may change those two.
            var external = (body.ExternalLocations ?? Array.Empty<string>()).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            string location = body.Location?.Trim() ?? string.Empty;
            string copyTo = body.CopyTo == null ? b.CopyTo : body.CopyTo.Trim();
            bool uploadOffsite = body.UploadOffsite ?? b.UploadOffsite;
            // Sending the server's files off the machine is an admin's decision too.
            bool reachesOutside = !external.SequenceEqual(b.ExternalLocations, StringComparer.OrdinalIgnoreCase)
                                  || !string.Equals(location, b.Location, StringComparison.OrdinalIgnoreCase)
                                  || !string.Equals(copyTo, b.CopyTo, StringComparison.OrdinalIgnoreCase)
                                  || uploadOffsite != b.UploadOffsite;
            if (reachesOutside && !user.IsAdmin) { return ApiResults.Forbidden("Only admins can change the backup locations, off-site uploads or folders outside the server."); }
            if (copyTo.Length > 0 && !Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(copyTo))) { return ApiResults.BadRequest("The second backup location must be a full path (D:\\Backups or \\\\nas\\share)."); }
            if (body.KeepCount < 0 || body.KeepDays < 0) { return ApiResults.BadRequest("Retention can't be negative."); }

            var paths = (body.Paths ?? Array.Empty<string>()).Select(p => p.Trim().Replace('\\', '/').Trim('/')).Where(p => p.Length > 0).ToList();
            foreach (string p in paths)
            {
                try { ctx.Engine.Files.Resolve(s.Id, p); }
                catch (FileOperationException) { return ApiResults.BadRequest($"\"{p}\" isn't inside the server's files."); }
            }

            b.Paths = paths;
            b.ExternalLocations = external;
            b.Location = location;
            b.CopyTo = copyTo;
            b.UploadOffsite = uploadOffsite;
            b.BeforeStart = body.BeforeStart;
            b.KeepCount = body.KeepCount;
            b.KeepDays = body.KeepDays;
            b.Save();
            ctx.Record(http, "backup-settings", s.Id, true, $"beforeStart={b.BeforeStart}, keep={b.KeepCount}/{b.KeepDays}d, paths={paths.Count}, external={external.Count}");
            return Results.NoContent();
        }).Needs(Capability.Backup);
    }

    // ───────────────────────────── Add-ons ─────────────────────────────

    private static void MapAddons(RouteGroupBuilder server)
    {
        server.MapGet("/addons", (HttpContext http, AgentContext ctx) =>
        {
            string id = Scopes.Server(http).Id;
            return Results.Json(new
            {
                builtIn = ctx.Engine.Addons.List(id).Select(a => new AddonDto(a.Key, a.Label, a.Present, a.Managed)),
                custom = ctx.Engine.Addons.ListCustom(id).Select(c => new CustomAddonDto(c.Id, c.Name, c.Url, c.Subfolder)),
            });
        }).Needs(Capability.Addons);

        server.MapPost("/addons/{key}/install", (HttpContext http, AgentContext ctx, string key) =>
        {
            var s = Scopes.Server(http);
            var request = ctx.Engine.Addons.Install(s.Id, key);
            ctx.Record(http, "addon-install", s.Id, request.Accepted, request.Accepted ? key : request.Error);
            return ApiResults.FromRequest(request, ctx);
        }).Needs(Capability.Addons);

        server.MapPost("/addons/{key}/manage", (HttpContext http, AgentContext ctx, string key, AddonManageRequest body) =>
        {
            var s = Scopes.Server(http);
            bool ok = ctx.Engine.Addons.SetManaged(s.Id, key, body.Managed);
            ctx.Record(http, body.Managed ? "addon-adopt" : "addon-release", s.Id, ok, key);
            return ok ? Results.NoContent() : ApiResults.NotFound("No such add-on.");
        }).Needs(Capability.Addons);

        server.MapPost("/addons/custom", (HttpContext http, AgentContext ctx, CustomAddonRequest body) =>
        {
            var s = Scopes.Server(http);
            var request = ctx.Engine.Addons.InstallCustom(s.Id, body.Name?.Trim() ?? string.Empty, body.Url?.Trim() ?? string.Empty, body.Subfolder?.Trim() ?? string.Empty);
            ctx.Record(http, "addon-custom", s.Id, request.Accepted, request.Accepted ? $"{body.Name} <- {body.Url}" : request.Error);
            return ApiResults.FromRequest(request, ctx);
        }).Needs(Capability.Addons);

        server.MapPost("/addons/custom/{addonId}/install", (HttpContext http, AgentContext ctx, string addonId) =>
        {
            var s = Scopes.Server(http);
            var request = ctx.Engine.Addons.ReinstallCustom(s.Id, addonId);
            ctx.Record(http, "addon-custom", s.Id, request.Accepted, request.Accepted ? addonId : request.Error);
            return ApiResults.FromRequest(request, ctx);
        }).Needs(Capability.Addons);

        server.MapDelete("/addons/custom/{addonId}", (HttpContext http, AgentContext ctx, string addonId) =>
        {
            var s = Scopes.Server(http);
            bool ok = ctx.Engine.Addons.RemoveCustom(s.Id, addonId);
            ctx.Record(http, "addon-custom-remove", s.Id, ok, addonId);
            return ok ? Results.NoContent() : ApiResults.NotFound("No such add-on.");
        }).Needs(Capability.Addons);
    }

    // ───────────────────────────── Schedules ─────────────────────────────

    private static void MapSchedules(RouteGroupBuilder server)
    {
        server.MapGet("/schedules", (HttpContext http, AgentContext ctx) =>
            Results.Json(ctx.Engine.Scheduler.GetSchedules(Scopes.Server(http).Id).Select(e =>
            {
                DateTime? next = e.Enabled ? ctx.Engine.Scheduler.NextOccurrence(e) : null;
                return new ScheduleDto(e.Cron, e.Action.ToString(), e.Payload, e.Arguments, e.Enabled, e.Source.ToString(),
                    next == null ? null : new DateTimeOffset(next.Value));
            })));

        // Replaces the schedules managed here (legacy crontab files and the restart setting are untouched).
        server.MapPut("/schedules", (HttpContext http, AgentContext ctx, SchedulesRequest body) =>
        {
            var s = Scopes.Server(http);
            var user = Scopes.User(http);
            var entries = new List<ScheduleEntry>();
            foreach (var e in body.Entries ?? Array.Empty<ScheduleEntryRequest>())
            {
                if (!Enum.TryParse<ScheduledAction>(e.Action, ignoreCase: true, out var action) || !Enum.IsDefined(action))
                {
                    return ApiResults.BadRequest($"\"{e.Action}\" isn't a schedule action.");
                }
                // A scheduled command runs with the scheduler's rights later — it needs the Console right now.
                if (action is ScheduledAction.Command or ScheduledAction.Rcon && !user.Can(Capability.Console, ctx.MachineId, s.Id))
                {
                    return ApiResults.Forbidden("Scheduling console commands needs the Console permission.");
                }
                entries.Add(new ScheduleEntry(e.Cron?.Trim() ?? string.Empty, action, e.Payload?.Trim() ?? string.Empty, string.Empty, e.Enabled));
            }
            string? problem = ctx.Engine.Scheduler.SaveManaged(s.Id, entries);
            ctx.Record(http, "schedules", s.Id, problem == null, problem ?? $"{entries.Count} schedule(s)");
            return problem == null ? Results.NoContent() : ApiResults.BadRequest(problem);
        }).Needs(Capability.Schedules);

        // The legacy "restart on schedule" setting, edited (or switched off to delete it) from the schedules list.
        server.MapPut("/schedules/restart-setting", (HttpContext http, AgentContext ctx, RestartSettingRequest body) =>
        {
            var s = Scopes.Server(http);
            string? problem = ctx.Engine.Scheduler.SetRestartSetting(s.Id, body.Cron, body.Enabled);
            ctx.Record(http, "schedules", s.Id, problem == null, problem ?? (body.Enabled ? $"restart setting: {body.Cron}" : "restart setting off"));
            return problem == null ? Results.NoContent() : ApiResults.BadRequest(problem);
        }).Needs(Capability.Schedules);
    }
}
