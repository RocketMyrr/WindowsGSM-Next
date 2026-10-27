using System.Diagnostics;
using System.Text.Json;
using WindowsGSM.Engine.Services;
using WindowsGSM.Functions;
using WindowsGSM.Hosting;

namespace WindowsGSM.Agent.Hosting;

/// <summary>One line of the report. Status: ok, info, warn or block (can't switch until it's sorted).</summary>
public sealed record DataFinding(string Area, string Status, string Title, string? Detail = null);

public sealed record DataReport(string Folder, bool CanAdopt, int Servers, IReadOnlyList<DataFinding> Findings);

/// <summary>
/// "What happens if WindowsGSM Next uses this folder?" — a dry run over an existing WindowsGSM data folder.
/// It reads and never writes: server configs are parsed directly, plugins are compiled in memory, and the
/// folder isn't locked. Used by <c>wgsm-agent --check-data</c> and by setup before adopting a folder in place.
/// </summary>
public static class DataCheck
{
    public static async Task<DataReport> RunAsync(string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var f = new List<DataFinding>();
        if (!Directory.Exists(folder))
        {
            f.Add(new("Folder", "block", "That folder doesn't exist."));
            return new DataReport(folder, false, 0, f);
        }

        // ── Who else is using it ──
        string? legacy = global::WindowsGSM.Hosting.DataRootLock.LegacyAppUsing(folder);
        if (legacy != null) { f.Add(new("Folder", "block", "The current WindowsGSM app is running from this folder.", $"Close it ({legacy}) before switching — two managers must never run the same servers.")); }
        string lockFile = Path.Combine(folder, "wgsm.lock");
        if (File.Exists(lockFile) && IsLocked(lockFile)) { f.Add(new("Folder", "block", "WindowsGSM Next is already running on this folder.")); }

        bool looksLikeWgsm = new[] { "servers", "configs", "plugins", "backups" }.Any(d => Directory.Exists(Path.Combine(folder, d)));
        if (!looksLikeWgsm)
        {
            bool empty = !Directory.EnumerateFileSystemEntries(folder).Any();
            f.Add(new("Folder", empty ? "ok" : "info", empty ? "An empty folder — a fresh start." : "This doesn't look like a WindowsGSM folder yet — it'll be set up as a new one."));
        }
        if (File.Exists(Path.Combine(folder, "WindowsGSM.exe"))) { f.Add(new("Folder", "info", "The current WindowsGSM app lives here.", "It stays as it is. Keep it for now in case you want to go back — just don't run both at once.")); }
        if (LegacyStartupTask.PointsAt(folder))
        {
            f.Add(new("Folder", "warn", "The current WindowsGSM app starts with Windows.",
                "Setup turns that off when you switch (Windows asks once for permission), so the two never run together after a restart."));
        }
        if (Directory.Exists(Path.Combine(folder, "configs", "next"))) { f.Add(new("Folder", "info", "WindowsGSM Next has used this folder before.", "Its accounts and settings are kept.")); }

        // ── Game plugins (compiled in memory; needed to recognise plugin games) ──
        var plugins = new PluginCatalog();
        string pluginDir = Path.Combine(folder, "plugins");
        bool pluginsCompiled = false;
        if (Directory.Exists(pluginDir) && Directory.EnumerateDirectories(pluginDir, "*.cs").Any())
        {
            try
            {
                WgsmEnvironment.Initialize(folder);
                await plugins.LoadAsync(writeLogs: false);
                pluginsCompiled = true;
                var loaded = plugins.Plugins.Where(p => p.IsLoaded).ToList();
                var broken = plugins.Plugins.Where(p => !p.IsLoaded).ToList();
                f.Add(new("Plugins", loaded.Count > 0 ? "ok" : "info", loaded.Count == 1 ? "1 plugin compiles and loads." : $"{loaded.Count} plugins compile and load."));
                foreach (var b in broken)
                {
                    f.Add(new("Plugins", "warn", $"{b.FileName} doesn't compile.", Short(b.Error) + " Servers using it won't start until it's fixed or updated (Game plugins page)."));
                }
            }
            catch (Exception ex) { f.Add(new("Plugins", "warn", "Couldn't check the plugins.", ex.Message)); }
        }
        var games = new GameCatalog(plugins);

        // ── Servers ──
        int servers = 0;
        string serversDir = Path.Combine(folder, "servers");
        var ports = new Dictionary<string, List<string>>();
        if (Directory.Exists(serversDir))
        {
            foreach (string dir in Directory.EnumerateDirectories(serversDir).OrderBy(d => int.TryParse(Path.GetFileName(d), out int n) ? n : int.MaxValue))
            {
                string id = Path.GetFileName(dir);
                if (!int.TryParse(id, out _)) { continue; }
                string cfgFile = Path.Combine(dir, "configs", "WindowsGSM.cfg");
                if (!File.Exists(cfgFile)) { f.Add(new("Servers", "warn", $"Server #{id} has no WindowsGSM.cfg.", "It will be skipped.")); continue; }
                servers++;
                var cfg = ReadCfg(cfgFile);
                string name = cfg.GetValueOrDefault("servername", $"Server #{id}");
                string game = cfg.GetValueOrDefault("servergame", "");
                bool known = games.Get(game) != null;
                if (!known)
                {
                    bool isPlugin = game.EndsWith(".cs]", StringComparison.OrdinalIgnoreCase);
                    f.Add(new("Servers", "warn", $"#{id} {name}: its game isn't available.",
                        isPlugin && !pluginsCompiled ? $"It needs the plugin for \"{game}\", which isn't in plugins/." : $"\"{game}\" — {(isPlugin ? "its plugin didn't load" : "not a game this version knows")}."));
                }
                if (!Directory.Exists(Path.Combine(dir, "serverfiles")) || !Directory.EnumerateFileSystemEntries(Path.Combine(dir, "serverfiles")).Any())
                {
                    f.Add(new("Servers", "info", $"#{id} {name}: no game files installed yet."));
                }
                foreach (string key in new[] { "serverport", "serverqueryport" })
                {
                    if (cfg.TryGetValue(key, out string? p) && p.Length > 0) { (ports.TryGetValue(p, out var l) ? l : ports[p] = new()).Add($"#{id}"); }
                }
                if (Directory.Exists(Path.Combine(dir, "configs", "Crontab")) && Directory.EnumerateFiles(Path.Combine(dir, "configs", "Crontab"), "*.csv").Any())
                {
                    f.Add(new("Schedules", "ok", $"#{id} {name}: its Crontab schedules carry over as they are."));
                }
                if (IsOn(cfg, "autostart")) { f.Add(new("Servers", "info", $"#{id} {name} starts automatically when the agent starts.")); }
            }
            foreach (var (port, ids) in ports.Where(kv => kv.Value.Distinct().Count() > 1))
            {
                f.Add(new("Servers", "warn", $"Port {port} is used by {string.Join(" and ", ids.Distinct())}.", "Only one can run at a time; change one of them in its Settings."));
            }
        }
        f.Insert(Math.Min(f.Count, f.FindIndex(x => x.Area == "Servers") is int i && i >= 0 ? i : f.Count),
            new("Servers", servers > 0 ? "ok" : "info", servers == 1 ? "1 server found — it'll appear as it is." : servers > 1 ? $"{servers} servers found — they'll appear as they are." : "No servers yet."));

        // ── Accounts, Discord bot, backups ──
        string users = Path.Combine(folder, "configs", "webdashboard", "users.json");
        string nextUsers = Path.Combine(folder, "configs", "next", "users.json");
        if (File.Exists(nextUsers))
        {
            f.Add(new("Accounts", "ok", "WindowsGSM Next's accounts here are kept."));
        }
        else if (File.Exists(users))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(users));
                int n = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
                f.Add(new("Accounts", "ok", $"{n} web dashboard account{(n == 1 ? "" : "s")} carry over — same passwords and 2FA."));
            }
            catch { f.Add(new("Accounts", "warn", "The web dashboard's accounts file couldn't be read.", "You'll create the owner account on first start instead.")); }
        }
        else { f.Add(new("Accounts", "info", "No web dashboard accounts — you'll create the owner account on first start.")); }

        string bot = Path.Combine(folder, "configs", "discordbot");
        if (File.Exists(Path.Combine(bot, "token.txt")))
        {
            int admins = File.Exists(Path.Combine(bot, "adminIDs.txt")) ? File.ReadAllLines(Path.Combine(bot, "adminIDs.txt")).Count(l => l.Trim().Length > 0) : 0;
            f.Add(new("Discord", "ok", $"The Discord bot's token and {admins} admin{(admins == 1 ? "" : "s")} carry over.", "It starts switched off — turn off the old app's bot, then switch it on under Discord bot. Only /panel, /list and /stats remain."));
        }

        string backups = Path.Combine(folder, "backups");
        if (Directory.Exists(backups))
        {
            var zips = Directory.EnumerateFiles(backups, "*.zip", SearchOption.AllDirectories).Select(p => new FileInfo(p)).ToList();
            if (zips.Count > 0) { f.Add(new("Backups", "ok", $"{zips.Count} backup{(zips.Count == 1 ? " stays" : "s stay")} listed and restorable ({Size(zips.Sum(z => z.Length))}).")); }
        }

        // ── Space and the bits that don't carry over ──
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(folder)!);
            double freeGb = drive.AvailableFreeSpace / 1024d / 1024 / 1024;
            f.Add(new("Disk", freeGb < 10 ? "warn" : "ok", $"{freeGb:0} GB free on {drive.Name.TrimEnd('\\')}{(freeGb < 10 ? " — updates and backups need room." : ".")}"));
        }
        catch { /* network path */ }
        f.Add(new("Settings", "info", "The old app's own options (start with Windows, its window, its web dashboard on port 8970) don't carry over.",
            "Turn on Start with Windows in the new app's tray menu or Agent settings. The new panel is on port 8971."));

        bool canAdopt = !f.Any(x => x.Status == "block");
        return new DataReport(folder, canAdopt, servers, f);
    }

    /// <summary>Plain text, for the command line.</summary>
    public static string Format(DataReport r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"WindowsGSM Next — data folder check: {r.Folder}");
        sb.AppendLine(r.CanAdopt ? "Ready to switch (nothing below blocks it)." : "Not ready — sort out the items marked BLOCK first.");
        sb.AppendLine();
        foreach (var g in r.Findings.GroupBy(x => x.Area))
        {
            sb.AppendLine(g.Key);
            foreach (var x in g)
            {
                sb.AppendLine($"  [{x.Status.ToUpperInvariant(),-5}] {x.Title}");
                if (x.Detail != null) { sb.AppendLine($"          {x.Detail}"); }
            }
        }
        sb.AppendLine();
        sb.AppendLine("Nothing was changed.");
        return sb.ToString();
    }

    private static Dictionary<string, string> ReadCfg(string file)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadAllLines(file))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) { continue; }
            d[line[..eq].Trim()] = line[(eq + 1)..].Trim().Trim('"');
        }
        return d;
    }

    private static bool IsOn(Dictionary<string, string> cfg, string key) => cfg.TryGetValue(key, out var v) && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static string Short(string? s) => string.IsNullOrWhiteSpace(s) ? "Unknown error." : (s.Length > 240 ? s[..240] + "…" : s).Replace('\n', ' ');

    private static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1 << 20):0} MB";

    private static bool IsLocked(string file)
    {
        try { using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None); return false; }
        catch (IOException) { return true; }
        catch { return false; }
    }

}

/// <summary>The legacy app's "Start on boot": a scheduled task named WGSM that runs its WindowsGSM.exe at sign-in.</summary>
public static class LegacyStartupTask
{
    /// <summary>Whether that task exists, is on, and starts the app in <paramref name="folder"/>.</summary>
    public static bool PointsAt(string folder)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", "/query /tn WGSM /xml") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            string xml = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            if (p.ExitCode != 0) { return false; }
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            if (doc.Descendants().Any(e => e.Name.LocalName == "Enabled" && e.Parent?.Name.LocalName == "Settings" && e.Value.Trim() == "false")) { return false; }
            string? command = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Command")?.Value.Trim().Trim('"');
            string? dir = command == null ? null : Path.GetDirectoryName(command)?.TrimEnd(Path.DirectorySeparatorChar);
            return dir != null && string.Equals(dir, Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

/// <summary>
/// NEXT: the old app opened on this folder while the agent runs (its shortcut, or its own start-with-Windows): two
/// managers would fight over the same servers. Checked every minute; said loudly each time it appears.
/// </summary>
public sealed class LegacyAppWatch : IDisposable
{
    private readonly Timer _timer;
    private string? _seen;

    public LegacyAppWatch(WindowsGSM.Agent.Api.AgentContext ctx, WindowsGSM.Agent.Notifications.NotificationCentre centre)
    {
        _timer = new Timer(_ =>
        {
            string? pid = global::WindowsGSM.Hosting.DataRootLock.LegacyAppUsing(WgsmEnvironment.DataRoot);
            if (pid != null && pid != _seen)
            {
                ctx.Engine.Log.Write("Agent", $"[ERROR] The old WindowsGSM app ({pid}) is running on this data folder — close it: two managers fight over the same servers.");
                centre.Record("legacyApp", "The old WindowsGSM app is running on this folder",
                    $"It ({pid}) and WindowsGSM Next both manage the same servers now — expect double starts and restarts. Close the old app, and turn off its \"Start on boot\".",
                    ctx.MachineId, null, null, WindowsGSM.Agent.Realtime.Visibility.Admin);
            }
            _seen = pid;
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public void Dispose() => _timer.Dispose();
}
