using WindowsGSM.Contracts;
using WindowsGSM.Functions;

namespace WindowsGSM.Agent.Api;

/// <summary>
/// Where game files live: the drives this machine offers (with free space), a check of a chosen folder, each
/// server's location, and moving a server's files to another drive (admins — it writes wherever they point).
/// </summary>
public static class LocationEndpoints
{
    /// <summary>
    /// The request comes from someone using the panel on this very PC: loopback, not passed on by a proxy, and not
    /// relayed by the hub for a viewer elsewhere.
    /// </summary>
    public static bool OnThisPc(HttpContext http)
    {
        var ip = http.Connection.RemoteIpAddress;
        if (ip == null || !System.Net.IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip)) { return false; }
        return !http.Request.Headers.ContainsKey("X-Forwarded-For") && !http.Request.Headers.ContainsKey(AgentContext.InternalHeader);
    }

    public static void Map(RouteGroupBuilder api, RouteGroupBuilder server)
    {
        var machine = api.MapGroup("/machines/{machine}/file-places").RequireMachine();

        // The drives to choose from, and (with ?folder=) whether a folder will do.
        machine.MapGet("", (HttpContext http, AgentContext ctx, string? folder) =>
        {
            if (!ctx.CurrentUser(http)!.IsAdmin) { return ApiResults.Forbidden(); }
            string dataDrive = Path.GetPathRoot(Path.GetFullPath(global::WindowsGSM.Hosting.WgsmEnvironment.DataRoot)) ?? "";
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
                .Select(d => new
                {
                    name = d.Name.TrimEnd('\\'),
                    label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? null : d.VolumeLabel,
                    free = d.AvailableFreeSpace, total = d.TotalSize,
                    removable = d.DriveType == DriveType.Removable,
                    windowsGsm = string.Equals(d.Name, dataDrive, StringComparison.OrdinalIgnoreCase),
                    suggestion = Path.Combine(d.Name, "GameServers"),
                })
                .ToList();
            string? problem = folder == null ? null : ServerLocation.Validate(folder);
            return Results.Json(new { drives, usual = Path.Combine(global::WindowsGSM.Hosting.WgsmEnvironment.DataRoot, "servers"), folder, problem });
        });

        server.MapGet("/files-location", (HttpContext http, AgentContext ctx) =>
        {
            string id = Scopes.Server(http).Id;
            bool elsewhere = ServerLocation.IsElsewhere(id);
            string real = ServerLocation.RealPath(id);
            long? free = null;
            try { var d = new DriveInfo(Path.GetPathRoot(real)!); if (d.IsReady) { free = d.AvailableFreeSpace; } } catch { /* not connected */ }
            return Results.Json(new
            {
                elsewhere, path = real, usual = ServerLocation.LinkPath(id), drive = Path.GetPathRoot(real)?.TrimEnd('\\'), free, problem = ServerLocation.Problem(id),
                // Explorer opens on this PC's screen, so it's only offered to someone using the panel on this PC.
                canOpen = OnThisPc(http) && Scopes.User(http).Can(Capability.Files, ctx.MachineId, id) && Directory.Exists(real),
            });
        }).Needs(Capability.View);

        // Opens the server's game files in Windows Explorer — on this PC, for someone at this PC.
        server.MapPost("/open-folder", (HttpContext http, AgentContext ctx) =>
        {
            if (!OnThisPc(http)) { return ApiResults.Forbidden("Explorer opens on the server PC's screen, so this only works from that PC."); }
            string id = Scopes.Server(http).Id;
            string real = ServerLocation.RealPath(id);
            if (!Directory.Exists(real)) { return ApiResults.NotFound(ServerLocation.Problem(id) ?? "The server's files folder isn't there yet."); }
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{real}\"") { UseShellExecute = true }); }
            catch (Exception ex) { return ApiResults.Error(500, "explorer", "Couldn't open Explorer: " + ex.Message); }
            return Results.NoContent();
        }).Needs(Capability.Files);

        // Move the files: to { folder } (it gets a folder of its own inside), or back to the usual place with folder = null.
        server.MapPost("/move-files", (HttpContext http, AgentContext ctx, MoveFilesRequest body) =>
        {
            if (!Scopes.User(http).IsAdmin) { return ApiResults.Forbidden("Only admins can move a server's files."); }
            var s = Scopes.Server(http);
            string? folder = string.IsNullOrWhiteSpace(body.Folder) ? null : body.Folder.Trim();
            var request = ctx.Engine.Provisioning.MoveFiles(s.Id, folder);
            ctx.Record(http, "move-files", s.Id, request.Accepted, request.Error ?? (folder ?? "back to the usual place"));
            return ApiResults.FromRequest(request, ctx);
        });
    }
}

public sealed record MoveFilesRequest(string? Folder);
