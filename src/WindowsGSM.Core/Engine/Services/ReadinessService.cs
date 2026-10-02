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
    public sealed record ReadinessCheck(string Scope, string Name, CheckStatus Status, string Message);

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
            };
            if (network) { checks.Add(await PublicIpAsync().ConfigureAwait(false)); }
            return checks;
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
                Firewall(s),
            };

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
                string path = JavaHelper.FindJavaExecutableAbsolutePath();
                return string.IsNullOrWhiteSpace(path)
                    ? new ReadinessCheck("App", "Java runtime", CheckStatus.Info, "Java wasn't found. Only Minecraft: Java Edition needs it, and it's offered during that install.")
                    : new ReadinessCheck("App", "Java runtime", CheckStatus.Pass, $"Found {path}");
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
