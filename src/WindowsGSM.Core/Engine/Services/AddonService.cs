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
    public sealed record AddonState(string Key, string Label, bool Present, bool Managed);

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
            var row = Row(s);
            var managed = InstalledAddonStore.Load(id);
            var result = new List<AddonState>();
            foreach (var d in BuiltIn)
            {
                bool? exists;
                try { exists = d.Exists(row); } catch { continue; }
                if (exists == null) { continue; } // not for this game
                result.Add(new AddonState(d.Key, d.Label, exists == true, managed.Contains(d.Key)));
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
                foreach (var d in BuiltIn.Where(d => managed.Contains(d.Key)))
                {
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

                foreach (var c in CustomAddonStore.Load(s.Id))
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
