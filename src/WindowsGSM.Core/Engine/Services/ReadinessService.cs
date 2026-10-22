#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using WindowsGSM.Engine.Backups;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Functions;
using WindowsGSM.Hosting;

namespace WindowsGSM.Engine.Services
{
    public enum CheckStatus { Pass, Info, Warning, Fail }

    /// <summary>One readiness check. <see cref="Scope"/> is App, Network, Server or Backup.</summary>
    /// <param name="Action">Something the user can do about it: for a missing runtime, the key to install it.</param>
    public sealed record ReadinessCheck(string Scope, string Name, CheckStatus Status, string Message, string? Action = null);

    /// <summary>
    /// "Is this machine / server ready to run?" — port of the legacy readiness checks, adjusted for Next:
    /// DepotDownloader is the tool that matters (SteamCMD is optional), and not running as administrator is
    /// normal (the agent runs as the signed-in user on purpose).
    /// </summary>
    public sealed class ReadinessService
    {
        private readonly ServerRegistry _servers;
        private readonly PluginCatalog _plugins;

        public ReadinessService(ServerRegistry servers, PluginCatalog plugins)
        {
            _servers = servers;
            _plugins = plugins;
        }

        /// <summary>Machine-wide checks. <paramref name="network"/> also asks a public service for this machine's IP.</summary>
        public async Task<IReadOnlyList<ReadinessCheck>> CheckMachineAsync(bool network = true)
        {
            string root = WgsmEnvironment.DataRoot;
            var checks = new List<ReadinessCheck>
            {
                Writable("App", "Data folder writable", root),
                Writable("App", "Logs folder writable", Path.Combine(root, "logs")),
                Writable("App", "Backups folder writable", Path.Combine(root, "backups")),
                Elevation(),
                Tool("DepotDownloader", ServerPath.GetBin("depotdownloader", "DepotDownloader.exe"), required: true),
                Tool("SteamCMD", ServerPath.GetBin("steamcmd", "steamcmd.exe"), required: false),
                Java(),
                DiskSpace(root),
                Plugins(),
                FirewallSummary(),
                SettingsFiles(),
            };
            if (network) { checks.Add(await PublicIpAsync().ConfigureAwait(false)); }
            return checks;
        }

        /// <summary>WindowsGSM's own settings files: any that couldn't be read, and data from a newer version.</summary>
        private static ReadinessCheck SettingsFiles()
        {
            var problems = SafeJson.Problems;
            if (problems.Count > 0)
            {
                bool lost = problems.Any(p => !p.Recovered);
                string list = string.Join(" ", problems.Select(p => $"{Path.GetFileName(p.File)} {p.Message}" + (p.KeptAs != null ? $" (the original is kept as {p.KeptAs})." : ".")));
                return new ReadinessCheck("App", "Settings files", lost ? CheckStatus.Fail : CheckStatus.Warning,
                    list + (lost ? " Put back a copy from a backup (the backups folder, or your own), or fix the kept file and rename it back, then restart the agent." : ""));
            }
            if (DataFormat.NewerDataWarning is { } newer) { return new ReadinessCheck("App", "Settings files", CheckStatus.Warning, newer); }
            return new ReadinessCheck("App", "Settings files", CheckStatus.Pass, "All of WindowsGSM's settings files read fine.");
        }

        public IReadOnlyList<ReadinessCheck> CheckServer(string id)
        {
            var s = _servers.Get(id);
            if (s == null) { return new[] { new ReadinessCheck("Server", "Server", CheckStatus.Fail, "No such server.") }; }
            var cfg = s.Config;
            var checks = new List<ReadinessCheck>
            {
                Folder("Server", "Server folder", ServerPath.GetServers(id)),
                Folder("Server", "Server files folder", ServerPath.GetServersServerFiles(id)),
                Folder("Server", "Server configs folder", ServerPath.GetServersConfigs(id)),
                Executable(s),
                Port("Server port", cfg.ServerPort),
                Port("Query port", cfg.ServerQueryPort, optional: true),
                PortCollisions(s),
                ServerIp(s),
                Firewall(s),
            };

            checks.AddRange(RuntimeChecks(s));
            if (GameConfigAddress(s) is ReadinessCheck bind) { checks.Add(bind); }
            if (MinecraftRcon(s) is ReadinessCheck rcon) { checks.Add(rcon); }

            var backup = BackupSettings.Load(id);
            try { checks.Add(Writable("Backup", "Backup location writable", backup.ResolveLocation())); }
            catch (Exception ex) { checks.Add(new ReadinessCheck("Backup", "Backup location writable", CheckStatus.Fail, ex.Message)); }
            foreach (string external in backup.ExternalLocations)
            {
                string path = Environment.ExpandEnvironmentVariables(external);
                bool exists = Directory.Exists(path) || File.Exists(path);
                checks.Add(new ReadinessCheck("Backup", "Extra backup location", exists ? CheckStatus.Pass : CheckStatus.Warning,
                    exists ? $"Found {path}" : $"Missing {path} — it may appear after the server's first start."));
            }
            return checks;
        }

        private static ReadinessCheck Writable(string scope, string name, string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                string probe = Path.Combine(path, $".wgsm_write_test_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probe, "test");
                File.Delete(probe);
                return new ReadinessCheck(scope, name, CheckStatus.Pass, $"Writable: {path}");
            }
            catch (Exception ex) { return new ReadinessCheck(scope, name, CheckStatus.Fail, $"{path} isn't writable: {ex.Message}"); }
        }

        private static ReadinessCheck Folder(string scope, string name, string path) =>
            Directory.Exists(path)
                ? new ReadinessCheck(scope, name, CheckStatus.Pass, $"Found {path}")
                : new ReadinessCheck(scope, name, CheckStatus.Fail, $"Missing {path}");

        private static ReadinessCheck Elevation()
        {
            bool admin;
            try { using var id = WindowsIdentity.GetCurrent(); admin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { admin = false; }
            return new ReadinessCheck("App", "Administrator rights", CheckStatus.Info, admin
                ? "Running as administrator."
                : "Running as the signed-in user (normal). Adding firewall rules asks for administrator approval once.");
        }

        private static ReadinessCheck Tool(string name, string path, bool required) =>
            File.Exists(path)
                ? new ReadinessCheck("App", name, CheckStatus.Pass, $"Found {path}")
                : new ReadinessCheck("App", name, CheckStatus.Info, required
                    ? $"{name} isn't downloaded yet — it's fetched automatically on the first Steam install or update."
                    : $"{name} isn't installed. It's only needed for Workshop content or servers set to use it.");

        private static ReadinessCheck Java()
        {
            try
            {
                var found = JavaRuntimes.Installed().Select(r => r.Major).Distinct().OrderByDescending(m => m).ToList();
                return found.Count == 0
                    ? new ReadinessCheck("App", "Java runtime", CheckStatus.Info, "Java wasn't found. Only Minecraft: Java Edition needs it; a server gets the Java its version needs on its first start.")
                    : new ReadinessCheck("App", "Java runtime", CheckStatus.Pass, $"Java {string.Join(", ", found)} found. A Minecraft server uses the one its version needs (and gets it when missing).");
            }
            catch (Exception ex) { return new ReadinessCheck("App", "Java runtime", CheckStatus.Warning, "Java detection failed: " + ex.Message); }
        }

        private static ReadinessCheck DiskSpace(string root)
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(root)!);
                double freeGb = drive.AvailableFreeSpace / 1024.0 / 1024 / 1024;
                double percent = 100.0 * drive.AvailableFreeSpace / drive.TotalSize;
                var status = freeGb < 2 ? CheckStatus.Fail : freeGb < 10 || percent < 10 ? CheckStatus.Warning : CheckStatus.Pass;
                return new ReadinessCheck("App", "Disk space", status, $"{freeGb:0.#} GB free on {drive.Name} ({percent:0}%)");
            }
            catch (Exception ex) { return new ReadinessCheck("App", "Disk space", CheckStatus.Warning, "Disk space check failed: " + ex.Message); }
        }

        private ReadinessCheck Plugins()
        {
            var failed = _plugins.Plugins.Where(p => !p.IsLoaded).ToList();
            return failed.Count == 0
                ? new ReadinessCheck("App", "Plugins", CheckStatus.Pass, $"{_plugins.Plugins.Count} plugin(s) loaded.")
                : new ReadinessCheck("App", "Plugins", CheckStatus.Warning, $"{failed.Count} plugin(s) failed to load: {string.Join(", ", failed.Select(p => p.FileName))}");
        }

        private static async Task<ReadinessCheck> PublicIpAsync()
        {
            try
            {
                string ip = (await Http.DownloadStringAsync("https://ipinfo.io/ip").ConfigureAwait(false))?.Trim() ?? string.Empty;
                return string.IsNullOrWhiteSpace(ip)
                    ? new ReadinessCheck("Network", "Public IP", CheckStatus.Warning, "The public IP lookup returned nothing.")
                    : new ReadinessCheck("Network", "Public IP", CheckStatus.Pass, $"This machine is reachable as {ip} from the internet (if ports are forwarded).");
            }
            catch (Exception ex) { return new ReadinessCheck("Network", "Public IP", CheckStatus.Warning, "The public IP lookup failed: " + ex.Message); }
        }

        /// <summary>The server program's full path (null when the game doesn't name one).</summary>
        private string? ProgramPath(ServerInstance s)
        {
            dynamic? game = _plugins.Create(s.Game, s.Config);
            if (game == null) { return null; }
            string startPath = (string)(Dyn.Get((object)game, "StartPath")?.ToString() ?? string.Empty);
            return string.IsNullOrWhiteSpace(startPath) ? null : ServerPath.GetServersServerFiles(s.Id, startPath);
        }

        /// <summary>The Windows runtimes the server program needs (Visual C++, DirectX, .NET), from the program itself.</summary>
        private IEnumerable<ReadinessCheck> RuntimeChecks(ServerInstance s)
        {
            IReadOnlyList<Runtimes.Need> needs;
            try { needs = ProgramPath(s) is string exe ? Runtimes.For(exe) : Array.Empty<Runtimes.Need>(); }
            catch (Exception ex) { return new[] { new ReadinessCheck("Server", "Runtimes", CheckStatus.Warning, "Couldn't read what the server program needs: " + ex.Message) }; }
            return needs.Select(n => new ReadinessCheck("Server", n.Name, n.Installed ? CheckStatus.Pass : CheckStatus.Fail, n.Detail, n.Installed ? null : "runtime:" + n.Key));
        }

        private ReadinessCheck Executable(ServerInstance s)
        {
            try
            {
                dynamic? game = _plugins.Create(s.Game, s.Config);
                if (game == null) { return new ReadinessCheck("Server", "Server program", CheckStatus.Fail, $"The game '{s.Game}' isn't available on this machine."); }
                string startPath = (string)(Dyn.Get((object)game, "StartPath")?.ToString() ?? string.Empty);
                if (string.IsNullOrWhiteSpace(startPath)) { return new ReadinessCheck("Server", "Server program", CheckStatus.Info, "This game doesn't name a program file to check."); }
                string full = ServerPath.GetServersServerFiles(s.Id, startPath);
                return File.Exists(full)
                    ? new ReadinessCheck("Server", "Server program", CheckStatus.Pass, $"Found {full}")
                    : new ReadinessCheck("Server", "Server program", CheckStatus.Fail, $"Missing {full} — the server may need installing or validating.");
            }
            catch (Exception ex) { return new ReadinessCheck("Server", "Server program", CheckStatus.Fail, ex.Message); }
        }

        private static ReadinessCheck Port(string name, string? value, bool optional = false)
        {
            if (string.IsNullOrWhiteSpace(value) && optional) { return new ReadinessCheck("Server", name, CheckStatus.Info, "Not set."); }
            bool valid = int.TryParse(value, out int port) && port is >= 1 and <= 65535;
            return new ReadinessCheck("Server", name, valid ? CheckStatus.Pass : CheckStatus.Fail, valid ? $"{value} is valid." : $"'{value}' isn't a valid port.");
        }

        private ReadinessCheck Firewall(ServerInstance s)
        {
            var f = GameFirewall.Status(s, _plugins);
            var status = f.State switch { "allowed" => CheckStatus.Pass, "missing" or "blocked" => CheckStatus.Warning, _ => CheckStatus.Info };
            return new ReadinessCheck("Server", "Windows Firewall", status, f.Message);
        }

        private ReadinessCheck FirewallSummary()
        {
            var rules = GameFirewall.ReadRules();
            var problems = _servers.All.Select(s => (s, f: GameFirewall.Status(s, _plugins, rules))).Where(x => x.f.State is "missing" or "blocked").Select(x => $"#{x.s.Id} {x.s.Name}").ToList();
            return problems.Count == 0
                ? new ReadinessCheck("Network", "Game servers through Windows Firewall", CheckStatus.Pass, "Every server's program is allowed in (or doesn't name one).")
                : new ReadinessCheck("Network", "Game servers through Windows Firewall", CheckStatus.Warning,
                    $"No firewall rule (or a blocking one) for {string.Join(", ", problems)} — players on other computers probably can't connect. Use \"Allow through firewall\" here or on the server's overview.");
        }

        /// <summary>NEXT: the server's IP must be an address of this PC (or 0.0.0.0) — otherwise the game can't open its socket.</summary>
        private static ReadinessCheck ServerIp(ServerInstance s)
        {
            string? ip = s.Config.ServerIP?.Trim();
            if (LocalAddresses.IsUsable(ip)) { return new ReadinessCheck("Server", "Server IP", CheckStatus.Pass, string.IsNullOrEmpty(ip) || ip == "0.0.0.0" ? "Listens on every address of this PC." : $"{ip} is an address of this PC."); }
            string? now = LocalAddresses.Best(ip);
            bool auto = s.Config.GetCustomSetting(LifecycleService.AutoServerIpKey, "1") != "0";
            string where = now != null ? $" This PC's address is {now}." : "";
            return auto
                ? new ReadinessCheck("Server", "Server IP", CheckStatus.Warning, $"{ip} isn't an address of this PC (its address changed, or it's the router's public one).{where} It's updated automatically at the next start.")
                : new ReadinessCheck("Server", "Server IP", CheckStatus.Fail, $"{ip} isn't an address of this PC, so the game can't start on it.{where} Set Server IP in Settings, or turn on \"Update the IP when this PC's address changes\".");
        }

        /// <summary>
        /// NEXT: the address the game's own config binds (Minecraft's server.properties server-ip), which WindowsGSM
        /// doesn't change: when it isn't an address of this PC the game can't open its port ("Cannot assign requested
        /// address"). Only read, never written. Null when the game has no such setting.
        /// </summary>
        private static ReadinessCheck? GameConfigAddress(ServerInstance s)
        {
            string? bound = BoundInGameConfig(s.Id);
            if (bound == null) { return null; } // the game's config doesn't set one
            if (LocalAddresses.IsUsable(bound)) { return new ReadinessCheck("Server", "Game config address", CheckStatus.Pass, bound.Length == 0 ? "server.properties: server-ip is empty, so the game listens on every address of this PC." : $"server.properties: server-ip={bound} is an address of this PC."); }
            string? now = LocalAddresses.Best(bound);
            return new ReadinessCheck("Server", "Game config address", CheckStatus.Fail,
                $"server.properties has server-ip={bound}, which isn't an address of this PC, so the game can't open its port. " +
                $"Change it under Configs to {(now != null ? now + ", or " : "")}leave it empty (every address of this PC).");
        }

        /// <summary>
        /// NEXT: Minecraft: Java with RCON off. Without it, WindowsGSM can't stop the server cleanly after the agent restarts,
        /// read its TPS, or change its whitelist while it runs. The file is only changed when someone presses Turn on RCON.
        /// </summary>
        private static ReadinessCheck? MinecraftRcon(ServerInstance s)
        {
            if (!string.Equals(s.Game, GameServer.MC.FullName, StringComparison.OrdinalIgnoreCase)) { return null; }
            string file = ServerPath.GetServersServerFiles(s.Id, "server.properties");
            if (!File.Exists(file)) { return null; }
            var props = File.ReadAllLines(file).Select(l => l.Split('=', 2)).Where(kv => kv.Length == 2).GroupBy(kv => kv[0].Trim()).ToDictionary(g => g.Key, g => g.Last()[1].Trim());
            bool on = props.TryGetValue("enable-rcon", out var v) && v.Equals("true", StringComparison.OrdinalIgnoreCase);
            bool known = ConsoleService.RconConfigured(s.Config, out _, out _);
            if (on && known) { return new ReadinessCheck("Server", "RCON", CheckStatus.Pass, "RCON is on, and WindowsGSM uses it."); }
            string port = props.TryGetValue("rcon.port", out var p) && p.Length > 0 ? p : "25575";
            return new ReadinessCheck("Server", "RCON", CheckStatus.Info, on
                ? $"RCON is on in server.properties, but WindowsGSM doesn't know it. Turn on RCON copies port {port} and the password to Settings."
                : "RCON is off. Without it WindowsGSM can't stop this server cleanly after the agent restarts, show its TPS, or change its whitelist while it runs. " +
                  "Turn on RCON sets it up in server.properties (keeping anything already there) and in Settings; it works from the next start.",
                Action: "rcon:on");
        }

        /// <summary>server-ip from the server's server.properties ("" when empty), or null when there's no such setting.</summary>
        public static string? BoundInGameConfig(string serverId)
        {
            try
            {
                string file = ServerPath.GetServersServerFiles(serverId, "server.properties");
                if (!File.Exists(file)) { return null; }
                foreach (string line in File.ReadLines(file))
                {
                    string t = line.Trim();
                    if (t.StartsWith("server-ip=", StringComparison.Ordinal)) { return t.Substring("server-ip=".Length).Trim(); }
                }
            }
            catch { /* unreadable: nothing to say */ }
            return null;
        }

        private ReadinessCheck PortCollisions(ServerInstance s)
        {
            var mine = new[] { s.Config.ServerPort, s.Config.ServerQueryPort }.Where(p => !string.IsNullOrWhiteSpace(p)).ToHashSet();
            var clashes = _servers.All
                .Where(o => o.Id != s.Id && (mine.Contains(o.Config.ServerPort ?? "") || mine.Contains(o.Config.ServerQueryPort ?? "")))
                .Select(o => $"#{o.Id} {o.Name}")
                .ToList();
            return clashes.Count == 0
                ? new ReadinessCheck("Server", "Port clashes", CheckStatus.Pass, "No other server uses these ports.")
                : new ReadinessCheck("Server", "Port clashes", CheckStatus.Warning, "Shares a port with " + string.Join(", ", clashes) + " — they can't run at the same time.");
        }
    }
}
