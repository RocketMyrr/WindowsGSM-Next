#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Functions;

namespace WindowsGSM.Engine.Services
{
    /// <summary>A built-in add-on as it applies to one server.</summary>
    /// <param name="Present">Its files are on disk (maybe put there by a custom add-on).</param>
    /// <param name="Managed">WindowsGSM installed or adopted it, so "update add-ons" keeps it current.</param>
    /// <param name="InstalledVersion">The version on disk, when the add-on's version can be read (Oxide, Carbon).</param>
    /// <param name="LatestVersion">The newest release on GitHub, once asked (see <see cref="AddonVersions"/>).</param>
    /// <param name="Staging">The installed one is a staging/development build, not the release Update installs.</param>
    /// <param name="ConflictsWith">An add-on on this server it can't run alongside (Oxide and Carbon), so it can't be installed.</param>
    /// <param name="Removable">WindowsGSM can take it off this server again.</param>
    /// <param name="ProvidedBy">A custom add-on on this server installs it instead (e.g. an Oxide staging build), so
    /// "update add-ons" runs that one and leaves the built-in release alone.</param>
    public sealed record AddonState(string Key, string Label, bool Present, bool Managed,
        string? InstalledVersion = null, string? LatestVersion = null, bool Staging = false,
        string? ConflictsWith = null, bool Removable = false, string? ProvidedBy = null)
    {
        public bool UpdateAvailable => Present && ProvidedBy == null && AddonVersions.Newer(LatestVersion, InstalledVersion);
    }

    /// <summary>
    /// Built-in add-ons (Oxide, Carbon, SourceMod, …) and custom zip add-ons. Port of the legacy web add-on
    /// registry, CustomAddonInstallCore and GameServer_UpdateAddons, reusing the legacy installers as-is.
    ///
    /// Tracking rule carried over from legacy: "update add-ons" only refreshes built-ins WindowsGSM installed
    /// (or that you adopted) — files that are merely present, e.g. an Oxide staging build from a custom
    /// add-on, are never overwritten.
    /// </summary>
    public sealed class AddonService
    {
        private sealed record Definition(string Key, string Label, Func<ServerTable, bool?> Exists, Func<ServerTable, Task<bool>> Install);

        private static readonly Definition[] BuiltIn =
        {
            new("oxide",        "Oxide (uMod)",          InstallAddons.IsOxideModExists,            InstallAddons.OxideMod),
            new("carbon",       "Carbon",                InstallAddons.IsCarbonModExists,           InstallAddons.CarbonMod),
            new("sourcemod",    "SourceMod + MetaMod",   InstallAddons.IsSourceModAndMetaModExists, InstallAddons.SourceModAndMetaMod),
            new("amxmodx",      "AMX Mod X + MetaMod-P", InstallAddons.IsAMXModXAndMetaModPExists,  InstallAddons.AMXModXAndMetaModP),
            new("dayzsal",      "DayZSAL Mod Server",    InstallAddons.IsDayZSALModServerExists,     InstallAddons.DayZSALModServer),
            new("windroseplus", "Windrose Plus",         InstallAddons.IsWindrosePlusExists,         InstallAddons.WindrosePlus),
        };

        /// <summary>Mod frameworks that can't run together on one server: both change how Rust loads plugins.</summary>
        private static readonly Dictionary<string, string> Conflicts = new() { ["oxide"] = "carbon", ["carbon"] = "oxide" };

        /// <summary>Download links that are a build of a built-in add-on (Oxide's staging/develop artifacts, Carbon's other channels).</summary>
        private static readonly (string Key, string[] Marks)[] Builds =
        {
            ("oxide", new[] { "oxidemod", "Oxide.Rust" }),
            ("carbon", new[] { "CarbonCommunity/Carbon", "carbonmod" }),
        };

        /// <summary>Verify files (set by the engine): restores the game's own files after Oxide, which patches them, is removed.</summary>
        public Func<ServerInstance, JobContext, Task<string?>>? Verify { get; set; }

        private readonly ServerRegistry _servers;
        private readonly OperationGate _gate;
        private readonly JobManager _jobs;
        private readonly ServerLog _log;

        public AddonService(ServerRegistry servers, OperationGate gate, JobManager jobs, ServerLog log)
        {
            _servers = servers;
            _gate = gate;
            _jobs = jobs;
            _log = log;
        }

        /// <summary>Built-in add-ons that apply to this server's game, with their state.</summary>
        public IReadOnlyList<AddonState> List(string id)
        {
            var s = _servers.Get(id);
            if (s == null) { return Array.Empty<AddonState>(); }
            var present = Present(s);
            var managed = InstalledAddonStore.Load(id);
            var custom = CustomAddonStore.Load(id);
            var result = new List<AddonState>();
            foreach (var d in BuiltIn.Where(d => present.ContainsKey(d.Key))) // the rest aren't for this game
            {
                bool exists = present[d.Key];
                var (installed, staging) = exists ? AddonVersions.Installed(d.Key, id) : (null, false);
                result.Add(new AddonState(d.Key, d.Label, exists, managed.Contains(d.Key), installed, AddonVersions.LatestKnown(d.Key), staging,
                    ConflictWith(d.Key, present), exists && Conflicts.ContainsKey(d.Key), ProvidedBy(d.Key, custom)?.Name));
            }
            return result;
        }

        public IReadOnlyList<CustomAddon> ListCustom(string id) => CustomAddonStore.Load(id);

        /// <summary>Adopts (or releases) a built-in add-on without reinstalling — for live servers that can't stop.</summary>
        public bool SetManaged(string id, string key, bool managed)
        {
            if (_servers.Get(id) == null || BuiltIn.All(d => d.Key != key)) { return false; }
            if (managed) { InstalledAddonStore.Mark(id, key); } else { InstalledAddonStore.Unmark(id, key); }
            _log.Write(id, $"Add-on: {Label(key)} {(managed ? "is now managed by WindowsGSM" : "is no longer auto-updated")}");
            return true;
        }

        public OperationRequest Install(string id, string key)
        {
            var s = _servers.Get(id);
            var d = BuiltIn.FirstOrDefault(x => x.Key == key);
            if (s == null) { return OperationRequest.Rejected($"Server {id} doesn't exist."); }
            if (d == null) { return OperationRequest.Rejected($"Unknown add-on \"{key}\"."); }
            if (d.Exists(Row(s)) == null) { return OperationRequest.Rejected($"{d.Label} isn't available for {s.Game}."); }
            if (ConflictWith(key, Present(s)) is string other)
            {
                return OperationRequest.Rejected($"{other} is installed on {s.Name}, and {d.Label} can't run alongside it. Remove {other} first.");
            }
            return Run(s, $"Install {d.Label} on {s.Name}", async ctx =>
            {
                ctx.Report(stage: $"Installing {d.Label}");
                _log.Write(s.Id, $"Action: Install Add-on {d.Label}");
                bool ok = await d.Install(Row(s)).ConfigureAwait(false);
                if (!ok) { _log.Write(s.Id, $"[ERROR] {d.Label} install failed"); return $"{d.Label} install failed."; }
                InstalledAddonStore.Mark(s.Id, d.Key);
                _log.Write(s.Id, $"Add-on: {d.Label} installed");
                return null;
            });
        }

        /// <summary>Downloads a zip and extracts it into the server files (optionally a subfolder), saving it for later updates.</summary>
        public OperationRequest InstallCustom(string id, string name, string url, string subfolder)
        {
            var s = _servers.Get(id);
            if (s == null) { return OperationRequest.Rejected($"Server {id} doesn't exist."); }
            string? invalid = ValidateCustom(url, subfolder, s.Id);
            if (invalid != null) { return OperationRequest.Rejected(invalid); }
            return Run(s, $"Install {(string.IsNullOrWhiteSpace(name) ? "custom add-on" : name)} on {s.Name}", async ctx =>
            {
                string? error = await InstallCustomCoreAsync(s, name, url, subfolder, ctx).ConfigureAwait(false);
                if (error == null) { CustomAddonStore.Upsert(s.Id, name, url, subfolder); }
                return error;
            });
        }

        public OperationRequest ReinstallCustom(string id, string addonId)
        {
            var s = _servers.Get(id);
            var c = s == null ? null : CustomAddonStore.Get(id, addonId);
            if (s == null || c == null) { return OperationRequest.Rejected("That add-on doesn't exist."); }
            return Run(s, $"Reinstall {c.Name} on {s.Name}", ctx => InstallCustomCoreAsync(s, c.Name, c.Url, c.Subfolder, ctx));
        }

        public bool RemoveCustom(string id, string addonId) => CustomAddonStore.Remove(id, addonId);

        /// <summary>Changes a saved custom add-on's name, link or folder (used from its next install). Null when saved, else the problem.</summary>
        public string? EditCustom(string id, string addonId, string name, string url, string subfolder)
        {
            if (_servers.Get(id) == null || CustomAddonStore.Get(id, addonId) == null) { return "That add-on doesn't exist."; }
            if (ValidateCustom(url, subfolder, id) is string invalid) { return invalid; }
            return CustomAddonStore.Update(id, addonId, string.IsNullOrWhiteSpace(name) ? "Custom add-on" : name, url, subfolder)
                ? null : "Another custom add-on already uses that link and folder.";
        }

        /// <summary>
        /// Takes Oxide or Carbon off a stopped server, so the other can go on. Carbon: its loader and program files
        /// go; its plugins, configs and data stay. Oxide: its files go, then Verify files puts back the game files
        /// Oxide had replaced; the oxide folder (plugins, configs, data) stays.
        /// </summary>
        public OperationRequest Remove(string id, string key)
        {
            var s = _servers.Get(id);
            var d = BuiltIn.FirstOrDefault(x => x.Key == key);
            if (s == null) { return OperationRequest.Rejected($"Server {id} doesn't exist."); }
            if (d == null || !Conflicts.ContainsKey(key)) { return OperationRequest.Rejected($"{d?.Label ?? key} can't be removed by WindowsGSM."); }
            if (d.Exists(Row(s)) != true) { return OperationRequest.Rejected($"{d.Label} isn't installed on {s.Name}."); }
            if (s.State != ServerState.Stopped) { return OperationRequest.Rejected($"Stop {s.Name} before removing {d.Label}."); }
            return Run(s, $"Remove {d.Label} from {s.Name}", async ctx =>
            {
                ctx.Report(stage: $"Removing {d.Label}");
                _log.Write(s.Id, $"Action: Remove Add-on {d.Label}");
                string root = ServerPath.GetServersServerFiles(s.Id);
                try
                {
                    if (key == "carbon")
                    {
                        foreach (string f in new[] { "winhttp.dll", "doorstop_config.ini", ".doorstop_version" }) { File.Delete(Path.Combine(root, f)); }
                        foreach (string dir in new[] { "managed", "native", "tools" })
                        {
                            string path = Path.Combine(root, "carbon", dir);
                            if (Directory.Exists(path)) { Directory.Delete(path, true); }
                        }
                    }
                    else
                    {
                        foreach (string f in Directory.EnumerateFiles(Path.Combine(root, "RustDedicated_Data", "Managed"), "Oxide.*.dll")) { File.Delete(f); }
                    }
                }
                catch (Exception ex)
                {
                    _log.Write(s.Id, $"[ERROR] Removing {d.Label} failed: {ex.Message}");
                    return $"Couldn't remove {d.Label}: {ex.Message}";
                }

                InstalledAddonStore.Unmark(s.Id, key);
                _log.Write(s.Id, $"Add-on: {d.Label} removed");
                if (key != "oxide") { return null; }

                // Oxide replaced some of Rust's own files (Assembly-CSharp and others); Steam puts the originals back.
                if (Verify == null || UpdateService.IsHeld(s)) { return "Oxide was removed. Run Verify files before starting, to put back Rust's own files."; }
                ctx.Report(stage: "Verifying files");
                string? error = await Verify(s, ctx).ConfigureAwait(false);
                return error == null ? null : $"Oxide was removed, but Verify files failed ({error}). Run it again before starting.";
            });
        }

        // ─────────────────────────────── Update add-ons ───────────────────────────────

        /// <summary>Refreshes every managed built-in and every saved custom add-on. Caller holds the gate.</summary>
        internal async Task UpdateAllCoreAsync(ServerInstance s, JobContext job, string notes)
        {
            bool wasRunning = s.Process != null;
            if (!wasRunning) { s.SetState(ServerState.UpdatingAddons); }
            _log.Write(s.Id, "Action: Update Addons" + notes);
            try
            {
                bool any = false;
                var managed = InstalledAddonStore.Load(s.Id);
                var row = Row(s);
                var custom = CustomAddonStore.Load(s.Id);
                foreach (var d in BuiltIn.Where(d => managed.Contains(d.Key)))
                {
                    if (ProvidedBy(d.Key, custom) is CustomAddon by)
                    {
                        _log.Write(s.Id, $"Action: Update Addons: {d.Label}: skipped, the custom add-on '{by.Name}' installs it");
                        continue;
                    }
                    bool? exists;
                    try { exists = d.Exists(row); }
                    catch (Exception ex) { _log.Write(s.Id, $"Action: Update Addons: {d.Label}: existence check failed ({ex.Message})"); continue; }
                    if (exists != true) { continue; } // not applicable, or no longer present

                    job.Report(stage: $"Updating {d.Label}");
                    try
                    {
                        bool ok = await d.Install(row).ConfigureAwait(false);
                        _log.Write(s.Id, $"Action: Update Addons: {d.Label}: {(ok ? "updated successfully" : "update failed")}");
                        any |= ok;
                    }
                    catch (Exception ex) { _log.Write(s.Id, $"Action: Update Addons: {d.Label}: update threw exception: {ex.Message}"); }
                }

                foreach (var c in custom)
                {
                    job.Report(stage: $"Updating {c.Name}");
                    string? error = await InstallCustomCoreAsync(s, c.Name, c.Url, c.Subfolder, job).ConfigureAwait(false);
                    _log.Write(s.Id, error == null ? $"Action: Update Addons: custom '{c.Name}': updated" : $"Action: Update Addons: custom '{c.Name}': failed ({error})");
                    any |= error == null;
                }

                if (!any) { _log.Write(s.Id, "Action: Update Addons: No addons updated."); }
            }
            finally
            {
                if (!wasRunning) { s.SetState(ServerState.Stopped); }
            }
        }

        // ─────────────────────────────── Helpers ───────────────────────────────

        private OperationRequest Run(ServerInstance s, string title, Func<JobContext, Task<string?>> work)
        {
            if (s.State != ServerState.Stopped && s.State != ServerState.Running) { return OperationRequest.Rejected($"{s.Name} is busy ({s.State})."); }
            if (!_gate.TryBegin(s.Id, OperationKind.Addon, "Add-on install", out var lease, out var blockedBy))
            {
                return OperationRequest.Rejected($"{s.Name} is busy: {blockedBy}.");
            }
            return OperationRequest.Running(_jobs.Start("addon", s.Id, title, async ctx =>
            {
                using (lease)
                {
                    bool wasRunning = s.Process != null;
                    if (wasRunning) { _log.Write(s.Id, "[NOTICE] Installing an add-on while the server runs — some files may be in use."); }
                    else { s.SetState(ServerState.UpdatingAddons); }
                    try { return await work(ctx).ConfigureAwait(false); }
                    finally { if (!wasRunning) { s.SetState(ServerState.Stopped); } }
                }
            }));
        }

        private async Task<string?> InstallCustomCoreAsync(ServerInstance s, string name, string url, string subfolder, JobContext job)
        {
            string? invalid = ValidateCustom(url, subfolder, s.Id);
            if (invalid != null) { return invalid; }
            string root = Path.GetFullPath(ServerPath.GetServersServerFiles(s.Id));
            string target = root;
            if (!string.IsNullOrWhiteSpace(subfolder)) { Backups.BackupService.TryJail(root, subfolder, out target); Directory.CreateDirectory(target); }

            string label = string.IsNullOrWhiteSpace(name) ? "custom add-on" : name;
            string tempZip = Path.Combine(Path.GetTempPath(), "wgsm-addon-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                job.Report(stage: $"Downloading {label}");
                _log.Write(s.Id, $"Add-on: downloading {label}…");
                await Http.DownloadUserUrlAsync(url, tempZip).ConfigureAwait(false); // never this PC or link-local addresses
                if (!File.Exists(tempZip) || new FileInfo(tempZip).Length == 0) { return "Download failed or the file was empty."; }

                job.Report(stage: $"Extracting {label}");
                int skipped = await Task.Run(() => ExtractSafely(tempZip, target)).ConfigureAwait(false);
                if (skipped > 0) { _log.Write(s.Id, $"[NOTICE] {label}: skipped {skipped} archive entr{(skipped == 1 ? "y" : "ies")} that pointed outside the server folder."); }
                _log.Write(s.Id, $"Add-on: {label} installed");
                return null;
            }
            catch (InvalidDataException) { return "That URL didn't return a valid .zip file."; }
            catch (Exception ex) { return ex.Message; }
            finally { try { File.Delete(tempZip); } catch { /* temp */ } }
        }

        private static string? ValidateCustom(string url, string subfolder, string serverId)
        {
            if (string.IsNullOrWhiteSpace(url)) { return "A download URL is required."; }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return "The URL must start with http:// or https://.";
            }
            string root = Path.GetFullPath(ServerPath.GetServersServerFiles(serverId));
            if (!Directory.Exists(root)) { return "Server files folder not found — install the server first."; }
            if (!string.IsNullOrWhiteSpace(subfolder) && !Backups.BackupService.TryJail(root, subfolder, out _)) { return "The target folder must be inside the server files."; }
            return null;
        }

        /// <summary>Extracts over existing files, skipping any entry that would land outside <paramref name="target"/> (zip-slip). Returns entries skipped.</summary>
        internal static int ExtractSafely(string zipPath, string target)
        {
            string root = Path.GetFullPath(target).TrimEnd('\\');
            int skipped = 0;
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                string dest = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!dest.Equals(root, StringComparison.OrdinalIgnoreCase) && !dest.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
                if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(dest); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
            return skipped;
        }

        /// <summary>The installed add-on <paramref name="key"/> can't run with, by label; null when there's none.</summary>
        private static string? ConflictWith(string key, IReadOnlyDictionary<string, bool> present) =>
            Conflicts.TryGetValue(key, out var other) && present.TryGetValue(other, out bool there) && there ? Label(other) : null;

        /// <summary>The saved custom add-on whose link is a build of built-in <paramref name="key"/>, if any.</summary>
        internal static CustomAddon? ProvidedBy(string key, IEnumerable<CustomAddon> custom)
        {
            var marks = Builds.FirstOrDefault(b => b.Key == key).Marks;
            return marks == null ? null : custom.FirstOrDefault(c => c.Url != null && marks.Any(m => c.Url.Contains(m, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>Built-ins for this server's game → whether they're on disk (games they don't apply to are left out).</summary>
        private static Dictionary<string, bool> Present(ServerInstance s)
        {
            var row = Row(s);
            var result = new Dictionary<string, bool>();
            foreach (var d in BuiltIn)
            {
                try { if (d.Exists(row) is bool exists) { result[d.Key] = exists; } } catch { /* leave it out */ }
            }
            return result;
        }

        private static ServerTable Row(ServerInstance s) => new ServerTable { ID = s.Id, Game = s.Game, Name = s.Name };

        private static string Label(string key) => BuiltIn.FirstOrDefault(d => d.Key == key)?.Label ?? key;
    }

    /// <summary>"Update add-ons on start": runs before every start/restart/auto-restart when enabled.</summary>
    public sealed class UpdateAddonsOnStartStep : IPreStartStep
    {
        private readonly AddonService _addons;
        public UpdateAddonsOnStartStep(AddonService addons) => _addons = addons;

        public string Name => "Update add-ons on start";

        public Task RunAsync(ServerInstance server, StartReason reason, JobContext job) =>
            server.Config.UpdateAddonsOnStart ? _addons.UpdateAllCoreAsync(server, job, " | Update Addons on Start") : Task.CompletedTask;
    }
}
