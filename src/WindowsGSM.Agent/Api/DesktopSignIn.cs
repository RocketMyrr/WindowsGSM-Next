namespace WindowsGSM.Agent.Api;

/// <summary>
/// The desktop app on the server itself stays signed in: whoever last signed in through it (on this computer)
/// is remembered, and when its session ends (expired, agent restarted with sessions cleared…) it signs back in
/// as them through /local/desktop-session — which needs the key only this Windows account's apps can read.
/// Signing out in the desktop app forgets it. Turned off from the tray ("Stay signed in on this computer").
/// </summary>
public static class DesktopSignIn
{
    public const string UserAgentMark = "WindowsGSM-Desktop";
    private static string File_ => Path.Combine(global::WindowsGSM.Hosting.WgsmEnvironment.DataRoot, "configs", "next", "desktop-user.txt");

    /// <summary>A request from the desktop app on this computer (loopback, its user-agent mark).</summary>
    public static bool IsDesktop(HttpContext http)
    {
        var ip = http.Connection.RemoteIpAddress;
        if (ip == null || !System.Net.IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip)) { return false; }
        if (http.Request.Headers.ContainsKey("X-Forwarded-For")) { return false; }
        return http.Request.Headers.UserAgent.ToString().Contains(UserAgentMark, StringComparison.Ordinal);
    }

    public static void Remember(HttpContext http, string username)
    {
        if (!IsDesktop(http)) { return; }
        try { Directory.CreateDirectory(Path.GetDirectoryName(File_)!); File.WriteAllText(File_, username); } catch { /* not essential */ }
    }

    public static void Forget(HttpContext http)
    {
        if (!IsDesktop(http)) { return; }
        try { File.Delete(File_); } catch { }
    }

    public static string? Remembered()
    {
        try { return File.Exists(File_) ? File.ReadAllText(File_).Trim() : null; } catch { return null; }
    }
}
