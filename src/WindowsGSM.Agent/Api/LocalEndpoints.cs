using System.Net;
using System.Security.Cryptography;
using System.Text;
using WindowsGSM.Engine.Servers;

namespace WindowsGSM.Agent.Api;

/// <summary>
/// Requests from the desktop app on this machine (no sign-in): "stop everything and quit". Allowed only from
/// this computer (loopback) with the key in configs\next\local-control.key, a new one each time the agent
/// starts — readable by this Windows account, so by the desktop app it runs, and not by a web page.
/// </summary>
public static class LocalEndpoints
{
    public const string KeyHeader = "X-WGSM-Local-Key";
    public const string KeyFile = "local-control.key";

    /// <summary>Writes a fresh key for this run (the agent calls this at start).</summary>
    public static string WriteKey(string configDir)
    {
        string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Directory.CreateDirectory(configDir);
        Restrict(configDir);
        File.WriteAllText(Path.Combine(configDir, KeyFile), key);
        return key;
    }

    /// <summary>
    /// configs\next holds accounts (password hashes, 2FA secrets), sessions and this key. A data folder on a second
    /// drive is usually readable by every Windows account on the machine; this folder is limited to the account
    /// the agent runs as, SYSTEM and Administrators (inherited by what's in it). Best effort.
    /// </summary>
    public static void Restrict(string dir)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        try
        {
            var info = new DirectoryInfo(dir);
            var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            var acl = new System.Security.AccessControl.DirectorySecurity();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var inherit = System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
            foreach (var who in new System.Security.Principal.SecurityIdentifier[]
            {
                me,
                new(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                new(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
            })
            {
                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(who, System.Security.AccessControl.FileSystemRights.FullControl,
                    inherit, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            }
            info.SetAccessControl(acl);
        }
        catch { /* a folder we can't secure still works */ }
    }

    public static void Map(RouteGroupBuilder api, string key)
    {
        byte[] expected = Encoding.UTF8.GetBytes(key);
        bool Allowed(HttpContext http)
        {
            var ip = http.Connection.RemoteIpAddress;
            if (ip == null || !IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip)) { return false; }
            if (http.Request.Headers.ContainsKey("X-Forwarded-For")) { return false; }
            byte[] given = Encoding.UTF8.GetBytes(http.Request.Headers[KeyHeader].ToString());
            return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
        }

        // The desktop app signing back in as whoever last signed in through it on this computer (see DesktopSignIn).
        api.MapPost("/local/desktop-session", async (HttpContext http, AgentContext ctx) =>
        {
            if (!Allowed(http)) { return ApiResults.Forbidden("Only the WindowsGSM app on this computer can do that."); }
            string? name = DesktopSignIn.Remembered();
            var user = ctx.Users.Get(name);
            if (user is not { Enabled: true }) { return ApiResults.NotFound("Sign in once in the app; it remembers you after that."); }
            await AuthEndpoints.SignInAsync(http, ctx, user);
            ctx.Audit.Write(user.Username, AgentContext.Ip(http), "login", null, true, "desktop app on this computer (signed back in)");
            return Results.Json(new { username = user.Username });
        });

        // Just the agent (Start menu "Stop agent", the tray): game servers keep running and are picked up again
        // when it starts.
        api.MapPost("/local/stop-agent", (HttpContext http, AgentContext ctx, IHostApplicationLifetime lifetime) =>
        {
            if (!Allowed(http)) { return ApiResults.Forbidden("Only the WindowsGSM app on this computer can do that."); }
            ctx.Engine.Log.Write("Agent", "Stopping the agent (asked from this computer). Game servers keep running.");
            _ = Task.Run(async () => { await Task.Delay(500); lifetime.StopApplication(); });
            return Results.Json(new { stopping = true }, statusCode: 202);
        });

        // Stops every game server (normally, the way Stop does), waits for them — at most 3 minutes, then
        // whatever is left is ended — and then the agent itself. Answers straight away with how many.
        api.MapPost("/local/stop-everything", (HttpContext http, AgentContext ctx, IHostApplicationLifetime lifetime) =>
        {
            if (!Allowed(http)) { return ApiResults.Forbidden("Only the WindowsGSM app on this computer can do that."); }
            var running = ctx.Engine.Servers.All.Where(s => s.State != ServerState.Stopped).ToList();
            ctx.Engine.Log.Write("Agent", $"Stop everything and quit (from the desktop app): stopping {running.Count} server(s), then the agent.");
            _ = Task.Run(async () =>
            {
                foreach (var s in running) { ctx.Engine.Lifecycle.Stop(s.Id); }
                var deadline = DateTime.UtcNow.AddMinutes(3);
                while (DateTime.UtcNow < deadline && ctx.Engine.Servers.All.Any(s => s.State != ServerState.Stopped)) { await Task.Delay(1000); }
                foreach (var s in ctx.Engine.Servers.All.Where(s => s.State != ServerState.Stopped))
                {
                    ctx.Engine.Log.Write("Agent", $"{s.Name} didn't stop in time — ending it.");
                    try { ctx.Engine.Lifecycle.Kill(s.Id); } catch { /* best effort */ }
                }
                await Task.Delay(2000);
                lifetime.StopApplication();
            });
            return Results.Json(new { servers = running.Count }, statusCode: 202);
        });
    }
}
