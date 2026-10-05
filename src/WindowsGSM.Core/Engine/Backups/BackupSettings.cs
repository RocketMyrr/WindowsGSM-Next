#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using WindowsGSM.Functions;
using WindowsGSM.Hosting;

namespace WindowsGSM.Engine.Backups
{
    /// <summary>
    /// One backup configuration per server (servers/{id}/configs/backup.json), replacing the legacy app's two
    /// separate systems: the desktop BackupConfig.cfg (keep N backups, save/file locations) and the web
    /// panel's webbackup.json (pick folders, keep-for-days, backup-before-start). On first load it is built
    /// from whatever of those exist — they're read, never changed or deleted.
    /// </summary>
    public sealed class BackupSettings
    {
        public const string FileName = "backup.json";

        /// <summary>Paths inside serverfiles to back up (relative, "/" separated). Empty = all of serverfiles.</summary>
        public List<string> Paths { get; set; } = new List<string>();

        /// <summary>
        /// Folders or files OUTSIDE the server folder to include — some games keep worlds elsewhere (Valheim:
        /// AppData\LocalLow). Restores only ever write back to locations listed here, so a foreign backup
        /// can't write anywhere else on the machine.
        /// </summary>
        public List<string> ExternalLocations { get; set; } = new List<string>();

        /// <summary>Back up automatically before the server starts (and before an auto-restart).</summary>
        public bool BeforeStart { get; set; }

        /// <summary>Keep at most this many backups (0 = no limit). Legacy default: 3.</summary>
        public int KeepCount { get; set; } = 3;

        /// <summary>Delete backups older than this many days (0 = no limit).</summary>
        public int KeepDays { get; set; }

        /// <summary>Where archives go. Blank = backups/{id} in the data folder.</summary>
        public string Location { get; set; } = string.Empty;

        /// <summary>
        /// A second place each new backup is copied to (another drive, a network share). Blank = none. The same
        /// keep-count/keep-days apply there. If the copy fails the backup still counts — the log says why.
        /// </summary>
        public string CopyTo { get; set; } = string.Empty;

        /// <summary>Also upload each new backup to the machine's off-site storage (set up in Agent settings).</summary>
        public bool UploadOffsite { get; set; }

        [JsonIgnore] public string ServerId { get; private set; } = string.Empty;

        /// <summary>The folder archives are written to (created if needed).</summary>
        public string ResolveLocation()
        {
            string dir = string.IsNullOrWhiteSpace(Location)
                ? Path.Combine(WgsmEnvironment.DataRoot, "backups", ServerId)
                : Environment.ExpandEnvironmentVariables(Location);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string FilePath(string serverId) => ServerPath.GetServersConfigs(serverId, FileName);

        public static BackupSettings Load(string serverId)
        {
            // Damaged: the previous copy; with none, rebuilt from the legacy settings (the damaged file is kept aside).
            BackupSettings? settings = global::WindowsGSM.Hosting.SafeJson.ReadWith(FilePath(serverId),
                text => JsonConvert.DeserializeObject<BackupSettings>(text, global::WindowsGSM.Hosting.SafeJson.LenientNewtonsoft()));

            settings ??= FromLegacy(serverId);
            settings.ServerId = serverId;
            settings.Paths = settings.Paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(NormalizeRelative).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return settings;
        }

        public void Save()
        {
            global::WindowsGSM.Hosting.SafeJson.WriteText(FilePath(ServerId), JsonConvert.SerializeObject(this, Formatting.Indented));
        }

        /// <summary>Builds settings from the legacy desktop + web configs (without touching them).</summary>
        private static BackupSettings FromLegacy(string serverId)
        {
            var result = new BackupSettings();
            string serverFiles = Path.GetFullPath(ServerPath.GetServersServerFiles(serverId));

            // Desktop BackupConfig.cfg: key="value" lines, lists separated by ; or |.
            try
            {
                string legacy = ServerPath.GetServersConfigs(serverId, "BackupConfig.cfg");
                if (File.Exists(legacy))
                {
                    foreach (string raw in File.ReadAllLines(legacy))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("//")) { continue; }
                        int eq = line.IndexOf('=');
                        if (eq <= 0) { continue; }
                        string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                        string value = Environment.ExpandEnvironmentVariables(line.Substring(eq + 1).Trim().Trim('"'));

                        if (key == "maximumbackups" && int.TryParse(value, out int max)) { result.KeepCount = max <= 0 ? 1 : max; }
                        else if (key == "backuplocation" && !IsDefaultLocation(value, serverId)) { result.Location = value; }
                        else if (key == "saveslocation" || key == "fileslocation")
                        {
                            foreach (string item in value.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().Trim('"')))
                            {
                                string full = Path.IsPathRooted(item) ? Path.GetFullPath(item) : Path.GetFullPath(Path.Combine(serverFiles, item));
                                if (string.Equals(full.TrimEnd('\\'), serverFiles.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) { continue; } // = everything
                                if (full.StartsWith(serverFiles.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                                {
                                    result.Paths.Add(Path.GetRelativePath(serverFiles, full));
                                }
                                else { result.ExternalLocations.Add(full); }
                            }
                        }
                    }
                }
            }
            catch { /* keep defaults */ }

            // Web panel webbackup.json.
            try
            {
                string web = ServerPath.GetServersConfigs(serverId, "webbackup.json");
                if (File.Exists(web))
                {
                    var w = JsonConvert.DeserializeAnonymousType(File.ReadAllText(web), new { Paths = new List<string>(), BeforeStart = false, RetentionDays = 7 });
                    if (w != null)
                    {
                        result.Paths.AddRange(w.Paths ?? new List<string>());
                        result.BeforeStart |= w.BeforeStart;
                        result.KeepDays = Math.Max(0, w.RetentionDays);
                    }
                }
            }
            catch { /* keep defaults */ }

            // The desktop app's own "backup on start" switch lives in WindowsGSM.cfg.
            try { result.BeforeStart |= new ServerConfig(serverId).BackupOnStart; } catch { /* no config */ }

            return result;
        }

        private static bool IsDefaultLocation(string value, string serverId)
        {
            try
            {
                string def = Path.GetFullPath(Path.Combine(WgsmEnvironment.DataRoot, "backups", serverId)).TrimEnd('\\');
                return string.Equals(Path.GetFullPath(value).TrimEnd('\\'), def, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        internal static string NormalizeRelative(string path) =>
            path.Replace('\\', '/').Trim().Trim('/');
    }
}
