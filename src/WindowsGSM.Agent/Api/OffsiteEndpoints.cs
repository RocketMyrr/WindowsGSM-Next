using WindowsGSM.Agent.Hosting;
using WindowsGSM.Contracts;
using WindowsGSM.Engine.Backups;

namespace WindowsGSM.Agent.Api;

/// <summary>Off-site backups: the machine's bucket (admins), and each server's backups there.</summary>
public static class OffsiteEndpoints
{
    public sealed record OffsiteRequest(bool Enabled, string? Provider, string? Endpoint, string? Region, string? Bucket, string? AccessKeyId, string? SecretAccessKey, string? Prefix, int KeepCount);

    public static void Map(RouteGroupBuilder api, RouteGroupBuilder server)
    {
        var admin = api.MapGroup("/machines/{machine}/offsite").RequireMachine().AddEndpointFilter(async (efc, next) =>
        {
            var user = efc.HttpContext.RequestServices.GetRequiredService<AgentContext>().CurrentUser(efc.HttpContext);
            if (user == null) { return ApiResults.Unauthorized(); }
            if (!user.IsAdmin) { return ApiResults.Forbidden("Only admins and owners can set up off-site backups."); }
            return await next(efc);
        });

        admin.MapGet("", (OffsiteBackups offsite) => Results.Json(ToDto(offsite.Settings)));

        admin.MapPut("", (HttpContext http, AgentContext ctx, OffsiteBackups offsite, OffsiteRequest body) =>
        {
            var next = Build(offsite.Settings, body, out string? problem);
            if (problem != null) { return ApiResults.BadRequest(problem); }
            offsite.Apply(next!);
            ctx.Record(http, "offsite", null, true, $"{(next!.Enabled ? "on" : "off")}, bucket {next.Bucket}, keep {next.KeepCount}{(body.SecretAccessKey != null ? ", key changed" : "")}");
            return Results.Json(ToDto(next));
        });

        // Checks settings (as typed, before saving) by writing, reading and deleting a small file.
        admin.MapPost("/test", async (HttpContext http, OffsiteBackups offsite, OffsiteRequest body) =>
        {
            var candidate = Build(offsite.Settings, body, out string? problem);
            if (problem != null) { return ApiResults.BadRequest(problem); }
            string? failed = await offsite.TestAsync(candidate!, http.RequestAborted);
            string where = candidate!.FileServer ? OffsiteFileServer.Address(candidate).Host : candidate.Bucket;
            string warning = candidate.Provider == "ftp" ? " Plain FTP sends your password and backups unencrypted — use FTPS or SFTP if the server offers them." : "";
            return Results.Json(new { ok = failed == null, message = failed ?? $"Connected: wrote, read back and removed a test file on {where}.{warning}" });
        });

        server.MapGet("/backups/offsite", async (HttpContext http, OffsiteBackups offsite) =>
        {
            var s = Scopes.Server(http);
            if (!offsite.Settings.Ready) { return Results.Json(new { ready = false, upload = BackupSettings.Load(s.Id).UploadOffsite, backups = Array.Empty<RemoteBackup>() }); }
            try { return Results.Json(new { ready = true, upload = BackupSettings.Load(s.Id).UploadOffsite, backups = await offsite.ListAsync(s.Id, http.RequestAborted) }); }
            catch (Exception ex) when (ex is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested)
            {
                return ApiResults.Error(502, "unavailable", "Couldn't reach the off-site storage: " + ex.Message);
            }
        }).Needs(Capability.Backup);

        server.MapPost("/backups/offsite/{name}/download", (HttpContext http, AgentContext ctx, OffsiteBackups offsite, string name) =>
        {
            var s = Scopes.Server(http);
            var (job, problem) = offsite.Download(s.Id, name);
            ctx.Record(http, "offsite-download", s.Id, job != null, problem ?? name);
            return job == null ? ApiResults.BadRequest(problem!) : ApiResults.FromRequest(WindowsGSM.Engine.Services.OperationRequest.Running(job), ctx);
        }).Needs(Capability.Restore);

        // Upload one of the backups on this PC now.
        server.MapPost("/backups/{name}/upload-offsite", (HttpContext http, AgentContext ctx, OffsiteBackups offsite, string name) =>
        {
            var s = Scopes.Server(http);
            if (!offsite.Settings.Ready) { return ApiResults.BadRequest("Off-site backups aren't set up (Agent settings → Off-site backups)."); }
            string? archive = ctx.Engine.Backups.ResolveArchive(s.Id, name);
            if (archive == null) { return ApiResults.NotFound("No such backup."); }
            var job = offsite.Upload(s.Id, archive);
            ctx.Record(http, "offsite-upload", s.Id, true, name);
            return ApiResults.FromRequest(WindowsGSM.Engine.Services.OperationRequest.Running(job), ctx);
        }).Needs(Capability.Backup);
    }

    private static object ToDto(OffsiteSettings s) => new
    {
        enabled = s.Enabled, provider = s.Provider, endpoint = s.Endpoint, region = s.Region, bucket = s.Bucket, accessKeyId = s.AccessKeyId,
        hasSecret = s.SecretAccessKey.Length > 0, prefix = s.Prefix, keepCount = s.KeepCount, ready = s.Ready, pinned = s.Pin.Length > 0 ? s.Pin : null,
    };

    /// <summary>The settings a request asks for (the saved secret kept when none is sent), or why they won't do.</summary>
    private static OffsiteSettings? Build(OffsiteSettings current, OffsiteRequest body, out string? problem)
    {
        problem = null;
        string provider = (body.Provider ?? "other").Trim();
        if (OffsiteFileServer.Providers.Contains(provider)) { return BuildFileServer(current, body, provider, out problem); }
        string endpoint = (body.Endpoint ?? "").Trim().TrimEnd('/');
        if (endpoint.Length > 0 && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")))
        {
            problem = "The endpoint should be the service's S3 address, like https://s3.us-west-004.backblazeb2.com.";
            return null;
        }
        string bucket = (body.Bucket ?? "").Trim();
        if (bucket.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(bucket, "^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$")) { problem = "That isn't a bucket name (3–63 lowercase letters, digits, dots or dashes)."; return null; }
        string prefix = (body.Prefix ?? "windowsgsm").Trim().Trim('/');
        if (prefix.Length > 100 || prefix.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' or '/'))) { problem = "The folder name can use letters, digits, - _ . and /."; return null; }
        if (body.KeepCount is < 0 or > 1000) { problem = "Keep between 0 (all) and 1000 backups."; return null; }
        var next = new OffsiteSettings
        {
            Enabled = body.Enabled, Provider = (body.Provider ?? "other").Trim(), Endpoint = endpoint, Region = (body.Region ?? "").Trim(), Bucket = bucket,
            AccessKeyId = (body.AccessKeyId ?? "").Trim(), SecretAccessKey = body.SecretAccessKey == null ? current.SecretAccessKey : body.SecretAccessKey.Trim(),
            Prefix = prefix, KeepCount = body.KeepCount,
        };
        if (next.Enabled && !next.Ready) { problem = "Fill in the bucket, key ID and secret key first."; return null; }
        return next;
    }

    /// <summary>FTP, FTPS or SFTP: the host (and port), username, password and folder.</summary>
    private static OffsiteSettings? BuildFileServer(OffsiteSettings current, OffsiteRequest body, string provider, out string? problem)
    {
        problem = null;
        string host = (body.Endpoint ?? "").Trim();
        foreach (string scheme in new[] { "sftp://", "ftps://", "ftp://" }) { if (host.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) { host = host[scheme.Length..]; } }
        host = host.TrimEnd('/');
        if (host.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(host, @"^[A-Za-z0-9.\-]+(:\d{1,5})?$|^\[[0-9A-Fa-f:]+\](:\d{1,5})?$"))
        {
            problem = "The server should be a name or address, with a port if it isn't the usual one — like backup.example.com or 192.168.1.20:2121.";
            return null;
        }
        string prefix = (body.Prefix ?? "windowsgsm").Trim().Trim('/');
        if (prefix.Length > 200 || prefix.Contains("..") || prefix.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' or '/' or ' '))) { problem = "The folder can use letters, digits, spaces, - _ . and /."; return null; }
        if (body.KeepCount is < 0 or > 1000) { problem = "Keep between 0 (all) and 1000 backups."; return null; }
        bool sameServer = current.Provider == provider && string.Equals(current.Endpoint, host, StringComparison.OrdinalIgnoreCase);
        var next = new OffsiteSettings
        {
            Enabled = body.Enabled, Provider = provider, Endpoint = host,
            AccessKeyId = (body.AccessKeyId ?? "").Trim(), SecretAccessKey = body.SecretAccessKey == null ? current.SecretAccessKey : body.SecretAccessKey,
            Prefix = prefix, KeepCount = body.KeepCount,
            Pin = sameServer ? current.Pin : "", // another server (or saved again on purpose): its identity is learned again
        };
        if (next.Enabled && !next.Ready) { problem = "Fill in the server, username and password first."; return null; }
        return next;
    }
}
