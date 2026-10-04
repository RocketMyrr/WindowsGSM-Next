#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Engine.Services;
using WindowsGSM.Functions;

namespace WindowsGSM.Engine.Backups
{
    public enum BackupFormat { Next, LegacyDesktop, LegacyWeb }

    /// <summary>One backup archive. <see cref="Name"/> is relative to the server's backup folder ("web/x.zip" for legacy web backups).</summary>
    public sealed record BackupInfo(string Name, long Size, DateTimeOffset Created, BackupFormat Format);

    /// <summary>
    /// Backups and restores — one system replacing the legacy desktop and web backup systems.
    ///
    /// Safety over legacy:
    ///  • the new archive is written (to a .partial file) and completed BEFORE retention deletes anything;
    ///    legacy pruned first, so a failed backup had already cost you a good one;
    ///  • a restore extracts to a staging folder, swaps items in, and rolls back if anything fails; legacy
    ///    deleted the server folder first and extracted after — a failure left nothing;
    ///  • restores only write inside the server's own folder and its configured external locations; legacy
    ///    wrote to whatever absolute paths an archive's manifest named.
    /// Archives from the legacy app (both formats) can still be listed and restored.
    /// </summary>
    public sealed class BackupService
    {
        private const string ManifestName = "wgsm-backup.json";
        private const string LegacyManifestName = "backup_manifest.txt";

        private readonly ServerRegistry _servers;
        private readonly OperationGate _gate;
        private readonly JobManager _jobs;
        private readonly ServerLog _log;

        public BackupService(ServerRegistry servers, OperationGate gate, JobManager jobs, ServerLog log)
        {
            _servers = servers;
            _gate = gate;
            _jobs = jobs;
            _log = log;
        }

        // ─────────────────────────────── Requests ───────────────────────────────

        /// <summary>
        /// Backs up the configured paths (or, with <paramref name="everything"/>, all of serverfiles). A running
        /// server can be backed up; files it has locked are skipped and reported.
        /// </summary>
        public OperationRequest Backup(string id, bool everything = false)
        {
            var s = _servers.Get(id);
            if (s == null) { return OperationRequest.Rejected($"Server {id} doesn't exist."); }
            if (s.State != ServerState.Stopped && s.State != ServerState.Running)
            {
                return OperationRequest.Rejected($"{s.Name} is busy ({s.State}).");
            }
            if (!_gate.TryBegin(id, OperationKind.Backup, "Backup", out var lease, out var blockedBy))
            {
                return OperationRequest.Rejected($"{s.Name} is busy: {blockedBy}.");
            }
            return OperationRequest.Running(_jobs.Start("backup", id, $"Back up {s.Name}", async ctx =>
            {
                using (lease) { return await BackupCoreAsync(s, everything, ctx, notes: string.Empty).ConfigureAwait(false); }
            }));
        }

        /// <summary>A backup was written (server id, archive path) — the agent uploads it off-site from here.</summary>
        public Action<string, string>? Finished { get; set; }

        /// <summary>Restores <paramref name="name"/> (from <see cref="List"/>) onto a stopped server.</summary>
        /// <param name="keepSettings">
        /// Settings that keep their current values even when the backup's settings are restored — the scripts, for
        /// someone who isn't allowed to choose them (a backup could otherwise bring back one an admin removed).
        /// </param>
        public OperationRequest Restore(string id, string name, bool includeConfig = false, IReadOnlyCollection<string>? keepSettings = null)
        {
            var s = _servers.Get(id);
            if (s == null) { return OperationRequest.Rejected($"Server {id} doesn't exist."); }
            if (s.State != ServerState.Stopped) { return OperationRequest.Rejected($"Stop {s.Name} before restoring a backup."); }
            string? path = ResolveArchive(id, name);
            if (path == null) { return OperationRequest.Rejected("That backup doesn't exist."); }
            if (!_gate.TryBegin(id, OperationKind.Restore, "Restore", out var lease, out var blockedBy))
            {
                return OperationRequest.Rejected($"{s.Name} is busy: {blockedBy}.");
            }
            return OperationRequest.Running(_jobs.Start("restore", id, $"Restore {s.Name}", async ctx =>
            {
                using (lease) { return await RestoreCoreAsync(s, path, includeConfig, ctx, keepSettings).ConfigureAwait(false); }
            }));
        }

        /// <summary>Every backup for the server, newest first — this system's and the legacy app's.</summary>
        public IReadOnlyList<BackupInfo> List(string id)
        {
            var settings = BackupSettings.Load(id);
            string root = settings.ResolveLocation();
            var result = new List<BackupInfo>();

            foreach (var file in Directory.EnumerateFiles(root, "*.zip"))
            {
                var format = Path.GetFileName(file).StartsWith("WGSM-Backup-Server-", StringComparison.OrdinalIgnoreCase) ? BackupFormat.LegacyDesktop : BackupFormat.Next;
                result.Add(Info(file, Path.GetFileName(file), format));
            }
            string web = Path.Combine(root, "web");
            if (Directory.Exists(web))
            {
                foreach (var file in Directory.EnumerateFiles(web, "*.zip")) { result.Add(Info(file, "web/" + Path.GetFileName(file), BackupFormat.LegacyWeb)); }
            }
            return result.OrderByDescending(b => b.Created).ToList();
        }

        public bool Delete(string id, string name)
        {
            string? path = ResolveArchive(id, name);
            if (path == null) { return false; }
            try { File.Delete(path); _log.Write(id, $"Backup deleted: {name}"); return true; }
            catch { return false; }
        }

        /// <summary>Full path of a listed backup, or null (names are matched against the listing, never used raw).</summary>
        public string? ResolveArchive(string id, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) { return null; }
            var match = List(id).FirstOrDefault(b => string.Equals(b.Name, name.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
            return match == null ? null : Path.Combine(BackupSettings.Load(id).ResolveLocation(), match.Name.Replace('/', Path.DirectorySeparatorChar));
        }

        // ─────────────────────────────── Backup ───────────────────────────────

        private sealed class Manifest
        {
            public int Format { get; set; } = 1;
            public string ServerId { get; set; } = string.Empty;
            public string Game { get; set; } = string.Empty;
            public DateTimeOffset Created { get; set; }
            public bool WholeServerFiles { get; set; }
            public List<string> Paths { get; set; } = new List<string>();
            public List<ExternalEntry> External { get; set; } = new List<ExternalEntry>();
        }

        private sealed class ExternalEntry
        {
            public int Index { get; set; }
            public string Path { get; set; } = string.Empty;
            public bool IsFile { get; set; }
        }

        internal async Task<string?> BackupCoreAsync(ServerInstance s, bool everything, JobContext job, string notes)
        {
            var settings = BackupSettings.Load(s.Id);
            bool running = s.Process != null;
            if (!running)
            {
                s.SetState(ServerState.BackingUp);
                // Like legacy: stray processes from this server's folder would hold files open.
                await LifecycleService.EndLeftoverProcessesAsync(s.Id).ConfigureAwait(false);
            }

            _log.Write(s.Id, "Action: Backup" + notes);
            if (running) { _log.Write(s.Id, "[NOTICE] Backing up while the server runs — files it's writing may be skipped or inconsistent."); }

            string? partial = null;
            try
            {
                string location = settings.ResolveLocation();
                string serverFiles = Path.GetFullPath(ServerPath.GetServersServerFiles(s.Id));
                string configs = Path.GetFullPath(ServerPath.GetServersConfigs(s.Id));
                bool whole = everything || settings.Paths.Count == 0;

                job.Report(0, "Collecting files");
                var manifest = new Manifest { ServerId = s.Id, Game = s.Game, Created = DateTimeOffset.Now, WholeServerFiles = whole };
                var items = new List<(string source, string entry)>();

                // Never back up the backup folder into itself if it lives inside serverfiles.
                string locationFull = Path.GetFullPath(location).TrimEnd('\\') + "\\";
                bool Skip(string file) => file.StartsWith(locationFull, StringComparison.OrdinalIgnoreCase);

                if (whole)
                {
                    if (Directory.Exists(serverFiles)) { AddTree(items, serverFiles, serverFiles, "serverfiles", Skip); }
                }
                else
                {
                    foreach (string rel in settings.Paths)
                    {
                        if (!TryJail(serverFiles, rel, out string full)) { continue; }
                        if (File.Exists(full)) { items.Add((full, "serverfiles/" + BackupSettings.NormalizeRelative(rel))); }
                        else if (Directory.Exists(full)) { AddTree(items, full, serverFiles, "serverfiles", Skip); }
                        else { continue; }
                        manifest.Paths.Add(BackupSettings.NormalizeRelative(rel));
                    }
                    if (manifest.Paths.Count == 0) { return "None of the configured backup paths exist."; }
                }

                if (Directory.Exists(configs)) { AddTree(items, configs, configs, "configs", _ => false); }

                for (int i = 0; i < settings.ExternalLocations.Count; i++)
                {
                    string ext = Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.ExternalLocations[i]));
                    bool isFile = File.Exists(ext);
                    if (!isFile && !Directory.Exists(ext)) { _log.Write(s.Id, $"[NOTICE] Backup location not found, skipped: {ext}"); continue; }
                    manifest.External.Add(new ExternalEntry { Index = i, Path = ext, IsFile = isFile });
                    if (isFile) { items.Add((ext, $"external/{i}/{Path.GetFileName(ext)}")); }
                    else { AddTree(items, ext, ext, $"external/{i}", Skip); }
                }

                long total = Math.Max(1, items.Sum(i => SafeLength(i.source)));
                string stem = $"wgsm-{s.Id}-{DateTime.Now:yyyyMMdd-HHmmss}";
                string name = stem + ".zip";
                for (int n = 2; File.Exists(Path.Combine(location, name)); n++) { name = $"{stem}-{n}.zip"; } // two in one second
                string final = Path.Combine(location, name);
                partial = final + ".partial";

                long done = 0;
                int skipped = 0;
                job.Report(0, "Compressing");
                await Task.Run(() =>
                {
                    using var zip = ZipFile.Open(partial, ZipArchiveMode.Create);
                    foreach (var (source, entryName) in items)
                    {
                        job.Cancellation.ThrowIfCancellationRequested();
                        try
                        {
                            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                            using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            using var dst = entry.Open();
                            src.CopyTo(dst);
                        }
                        catch (IOException) { skipped++; } // locked by the running server
                        catch (UnauthorizedAccessException) { skipped++; }
                        done += SafeLength(source);
                        job.Report((int)(done * 100 / total));
                    }
                    var manifestEntry = zip.CreateEntry(ManifestName);
                    using var writer = new StreamWriter(manifestEntry.Open());
                    writer.Write(JsonConvert.SerializeObject(manifest, Formatting.Indented));
                }, job.Cancellation).ConfigureAwait(false);

                File.Move(partial, final);
                partial = null;
                if (skipped > 0) { _log.Write(s.Id, $"[NOTICE] Backup skipped {skipped} file(s) that were in use."); }

                // Only now — with the new backup safely written — apply retention.
                int pruned = Prune(s.Id, settings, keep: name);
                _log.Write(s.Id, $"Server: Backuped ({name}, {FormatSize(new FileInfo(final).Length)}{(pruned > 0 ? $", {pruned} old backup(s) removed" : string.Empty)})");

                if (!string.IsNullOrWhiteSpace(settings.CopyTo))
                {
                    job.Report(100, "Copying to the second location");
                    await CopyToSecondAsync(s.Id, settings, final, job.Cancellation).ConfigureAwait(false);
                }
                try { Finished?.Invoke(s.Id, final); } catch (Exception ex) { _log.Write(s.Id, $"[NOTICE] After the backup: {ex.Message}"); }
                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Write(s.Id, "Server: Fail to backup");
                _log.Write(s.Id, "[ERROR] " + ex.Message);
                return ex.Message;
            }
            finally
            {
                if (partial != null) { try { File.Delete(partial); } catch { /* best effort */ } }
                if (!running) { s.SetState(ServerState.Stopped); }
            }
        }

        /// <summary>
        /// Copies a finished backup to <see cref="BackupSettings.CopyTo"/> and applies retention there. Never fails
        /// the backup: a share that's offline is logged and tried again with the next backup.
        /// </summary>
        private async Task CopyToSecondAsync(string id, BackupSettings settings, string archive, CancellationToken token)
        {
            string? partial = null;
            try
            {
                string dir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.CopyTo.Trim()));
                if (string.Equals(dir.TrimEnd('\\'), Path.GetFullPath(settings.ResolveLocation()).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    _log.Write(id, "[NOTICE] The second backup location is the same as the first — not copied.");
                    return;
                }
                Directory.CreateDirectory(dir);
                string target = Path.Combine(dir, Path.GetFileName(archive));
                partial = target + ".partial";
                using (var src = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
                using (var dst = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                {
                    await src.CopyToAsync(dst, token).ConfigureAwait(false);
                }
                File.Move(partial, target, overwrite: true);
                partial = null;

                var mine = Directory.EnumerateFiles(dir, $"wgsm-{id}-*.zip").Select(f => Info(f, Path.GetFileName(f), BackupFormat.Next))
                    .OrderByDescending(b => b.Created).ToList();
                var remove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (settings.KeepCount > 0) { foreach (var b in mine.Skip(settings.KeepCount)) { remove.Add(b.Name); } }
                if (settings.KeepDays > 0) { foreach (var b in mine.Where(b => b.Created < DateTimeOffset.Now.AddDays(-settings.KeepDays))) { remove.Add(b.Name); } }
                remove.Remove(Path.GetFileName(archive));
                foreach (string n in remove) { try { File.Delete(Path.Combine(dir, n)); } catch { /* next time */ } }
                _log.Write(id, $"Backup copied to {dir}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Write(id, $"[NOTICE] Couldn't copy the backup to the second location ({settings.CopyTo}): {ex.Message} — the backup itself is fine.");
            }
            finally
            {
                if (partial != null) { try { File.Delete(partial); } catch { /* best effort */ } }
            }
        }

        // ─────────────────────────────── Test ───────────────────────────────

        /// <summary>
        /// Checks a backup without restoring it: every file in the archive is read back and checked against its
        /// checksum, and the archive must hold something restorable. Nothing on disk changes.
        /// </summary>
        public OperationRequest Verify(string id, string name)
        {
            var s = _servers.Get(id);
            if (s == null) { return OperationRequest.Rejected($"Server {id} doesn't exist."); }
            string? path = ResolveArchive(id, name);
            if (path == null) { return OperationRequest.Rejected("That backup doesn't exist."); }
            return OperationRequest.Running(_jobs.Start("backup-test", id, $"Test backup of {s.Name}", async ctx =>
            {
                string? error = await Task.Run(() => VerifyArchive(id, path, ctx), ctx.Cancellation).ConfigureAwait(false);
                if (error != null) { _log.Write(id, $"[ERROR] Backup test failed ({name}): {error}"); }
                return error;
            }));
        }

        private string? VerifyArchive(string id, string path, JobContext job)
        {
            try
            {
                using var zip = ZipFile.OpenRead(path);
                var files = zip.Entries.Where(e => !e.FullName.EndsWith("/")).ToList();
                if (files.Count == 0) { return "The backup is empty."; }
                long total = Math.Max(1, files.Sum(e => e.Length)), done = 0;
                var buffer = new byte[1 << 16];
                foreach (var entry in files)
                {
                    job.Cancellation.ThrowIfCancellationRequested();
                    uint crc = 0xFFFFFFFF;
                    long length = 0;
                    using (var stream = entry.Open())
                    {
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            crc = Crc32.Update(crc, buffer, read);
                            length += read;
                        }
                    }
                    crc ^= 0xFFFFFFFF;
                    if (length != entry.Length || crc != entry.Crc32) { return $"{entry.FullName} is damaged (its contents don't match the checksum)."; }
                    done += entry.Length;
                    job.Report((int)(done * 100 / total), "Checking files");
                }

                bool next = zip.GetEntry(ManifestName) != null;
                if (next && !files.Any(e => e.FullName.StartsWith("serverfiles/", StringComparison.OrdinalIgnoreCase)
                                         || e.FullName.StartsWith("external/", StringComparison.OrdinalIgnoreCase)
                                         || e.FullName.StartsWith("configs/", StringComparison.OrdinalIgnoreCase)))
                {
                    return "The backup has no server files in it.";
                }
                _log.Write(id, $"Backup test passed: {Path.GetFileName(path)} — {files.Count} file(s), {FormatSize(total)}, all intact.");
                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return $"The backup can't be read: {ex.Message}";
            }
        }

        /// <summary>Applies keep-count and keep-days to this system's archives. Never deletes <paramref name="keep"/>.</summary>
        private int Prune(string id, BackupSettings settings, string keep)
        {
            var mine = List(id).Where(b => b.Format == BackupFormat.Next && b.Name.StartsWith($"wgsm-{id}-", StringComparison.OrdinalIgnoreCase)).ToList();
            var remove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (settings.KeepCount > 0) { foreach (var b in mine.Skip(settings.KeepCount)) { remove.Add(b.Name); } }
            if (settings.KeepDays > 0)
            {
                var cutoff = DateTimeOffset.Now.AddDays(-settings.KeepDays);
                foreach (var b in mine.Where(b => b.Created < cutoff)) { remove.Add(b.Name); }
            }
            remove.Remove(keep);

            int removed = 0;
            string root = settings.ResolveLocation();
            foreach (string name in remove)
            {
                try { File.Delete(Path.Combine(root, name)); removed++; } catch { /* in use — next time */ }
            }
            return removed;
        }

        // ─────────────────────────────── Restore ───────────────────────────────

        /// <summary>What to put where: <see cref="Source"/> (in staging) replaces <see cref="Target"/>.</summary>
        private sealed record RestoreItem(string Source, string Target);

        private async Task<string?> RestoreCoreAsync(ServerInstance s, string archive, bool includeConfig, JobContext job, IReadOnlyCollection<string>? keepSettings = null)
        {
            // What those settings are now, to put back after the backup's settings are in.
            var kept = includeConfig && keepSettings is { Count: > 0 }
                ? keepSettings.ToDictionary(k => k, k => RawSetting(s.Id, k), StringComparer.OrdinalIgnoreCase)
                : null;
            s.SetState(ServerState.Restoring);
            _log.Write(s.Id, $"Action: Restore Backup ({Path.GetFileName(archive)})");

            string serverDir = Path.GetFullPath(ServerPath.GetServers(s.Id));
            string staging = Path.Combine(serverDir, $".restore-staging-{DateTime.Now:yyyyMMddHHmmss}");
            var swapped = new List<(string target, string? old)>();
            string stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            try
            {
                job.Report(0, "Extracting backup");
                // Extracting to staging next to the server keeps the later swaps as same-volume renames.
                // ExtractToDirectory refuses entries that would land outside the staging folder.
                await Task.Run(() => ZipFile.ExtractToDirectory(archive, staging), job.Cancellation).ConfigureAwait(false);

                job.Report(60, "Checking contents");
                var plan = Plan(s.Id, staging, includeConfig);
                if (plan.Count == 0) { return "The backup doesn't contain anything this server can restore."; }

                job.Report(70, "Restoring files");
                foreach (var item in plan)
                {
                    job.Cancellation.ThrowIfCancellationRequested();
                    string? old = null;
                    if (File.Exists(item.Target) || Directory.Exists(item.Target))
                    {
                        old = item.Target.TrimEnd('\\') + $".wgsm-old-{stamp}";
                        Move(item.Target, old);
                    }
                    swapped.Add((item.Target, old)); // recorded before placing, so a failed placement is rolled back too
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Target)!);
                    Place(item.Source, item.Target);
                }

                // All in — discard what was replaced.
                foreach (var (_, old) in swapped) { if (old != null) { DeleteQuietly(old); } }
                if (kept != null)
                {
                    foreach (var (key, value) in kept) { ServerConfig.SetSetting(s.Id, key, value); }
                    s.ReloadConfig();
                }
                _log.Write(s.Id, "Server: Restored");
                return null;
            }
            catch (Exception ex)
            {
                // Roll back: put every original back where it was.
                for (int i = swapped.Count - 1; i >= 0; i--)
                {
                    var (target, old) = swapped[i];
                    try
                    {
                        DeleteQuietly(target);
                        if (old != null) { Move(old, target); }
                    }
                    catch (Exception rollbackEx) { _log.Write(s.Id, $"[ERROR] Rollback of {target} failed: {rollbackEx.Message}"); }
                }
                _log.Write(s.Id, "Server: Fail to restore backup");
                _log.Write(s.Id, "[ERROR] " + ex.Message + (swapped.Count > 0 ? " — your previous files were put back." : string.Empty));
                if (ex is OperationCanceledException) { throw; }
                return ex.Message;
            }
            finally
            {
                DeleteQuietly(staging);
                s.SetState(ServerState.Stopped);
            }
        }

        /// <summary>A setting's value exactly as WindowsGSM.cfg has it ("" when absent).</summary>
        private static string RawSetting(string id, string key)
        {
            string file = ServerPath.GetServersConfigs(id, "WindowsGSM.cfg");
            if (!File.Exists(file)) { return string.Empty; }
            foreach (string line in File.ReadLines(file))
            {
                int eq = line.IndexOf('=');
                if (eq > 0 && string.Equals(line[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase)) { return line[(eq + 1)..].Trim().Trim('"'); }
            }
            return string.Empty;
        }

        /// <summary>Works out what the extracted archive restores, for every supported format.</summary>
        private static List<RestoreItem> Plan(string id, string staging, bool includeConfig)
        {
            var settings = BackupSettings.Load(id);
            // NEXT: the real folder — for game files on another drive, a restore swaps and writes there, so the link keeps
            // pointing at the restored files (restoring onto the link itself would put them back on this drive).
            string serverFiles = ServerLocation.RealPath(id);
            string configs = Path.GetFullPath(ServerPath.GetServersConfigs(id));
            var allowedExternal = new HashSet<string>(
                settings.ExternalLocations.Select(l => Path.GetFullPath(Environment.ExpandEnvironmentVariables(l)).TrimEnd('\\')),
                StringComparer.OrdinalIgnoreCase);
            var plan = new List<RestoreItem>();

            void AddConfigs(string dir)
            {
                if (!includeConfig || !Directory.Exists(dir)) { return; }
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    plan.Add(new RestoreItem(f, Path.Combine(configs, Path.GetRelativePath(dir, f))));
                }
            }

            string nextManifest = Path.Combine(staging, ManifestName);
            string legacyManifest = Path.Combine(staging, LegacyManifestName);

            if (File.Exists(nextManifest))
            {
                var manifest = JsonConvert.DeserializeObject<Manifest>(File.ReadAllText(nextManifest)) ?? new Manifest();
                string sf = Path.Combine(staging, "serverfiles");
                if (manifest.WholeServerFiles) { if (Directory.Exists(sf)) { plan.Add(new RestoreItem(sf, serverFiles)); } }
                else
                {
                    foreach (string rel in manifest.Paths)
                    {
                        if (!TryJail(serverFiles, rel, out string target) || !TryJail(sf, rel, out string source)) { continue; }
                        if (File.Exists(source) || Directory.Exists(source)) { plan.Add(new RestoreItem(source, target)); }
                    }
                }
                foreach (var ext in manifest.External)
                {
                    if (!allowedExternal.Contains(Path.GetFullPath(ext.Path).TrimEnd('\\'))) { continue; } // not configured here — never written
                    string dir = Path.Combine(staging, "external", ext.Index.ToString(CultureInfo.InvariantCulture));
                    string source = ext.IsFile ? Path.Combine(dir, Path.GetFileName(ext.Path)) : dir;
                    if (File.Exists(source) || Directory.Exists(source)) { plan.Add(new RestoreItem(source, ext.Path)); }
                }
                AddConfigs(Path.Combine(staging, "configs"));
            }
            else if (File.Exists(legacyManifest))
            {
                // Legacy desktop "saves" backup: lines "index|D|path" / "index|F|path", content under save_{index}.
                foreach (string line in File.ReadAllLines(legacyManifest))
                {
                    var parts = line.Split(new[] { '|' }, 3);
                    if (parts.Length < 2 || !int.TryParse(parts[0], out int index)) { continue; }
                    bool isFile = parts.Length == 3 && string.Equals(parts[1], "F", StringComparison.OrdinalIgnoreCase);
                    string original = Environment.ExpandEnvironmentVariables(parts.Length == 3 ? parts[2] : parts[1]).Trim();
                    if (string.IsNullOrWhiteSpace(original)) { continue; }
                    string full = Path.GetFullPath(original).TrimEnd('\\');
                    string dir = Path.Combine(staging, $"save_{index}");
                    string? source = isFile ? (Directory.Exists(dir) ? Directory.GetFiles(dir).FirstOrDefault() : null) : dir;
                    if (source == null || !(File.Exists(source) || Directory.Exists(source))) { continue; }

                    bool insideServer = string.Equals(full, serverFiles.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                                     || full.StartsWith(serverFiles.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
                    if (insideServer || allowedExternal.Contains(full)) { plan.Add(new RestoreItem(source, full)); }
                }
            }
            else if (Directory.Exists(Path.Combine(staging, "serverfiles")) || Directory.Exists(Path.Combine(staging, "configs")))
            {
                // Legacy desktop "whole server" backup: a copy of servers/{id}.
                string sf = Path.Combine(staging, "serverfiles");
                if (Directory.Exists(sf)) { plan.Add(new RestoreItem(sf, serverFiles)); }
                AddConfigs(Path.Combine(staging, "configs"));
            }
            else
            {
                // Legacy web backup: entries relative to serverfiles. Replace each top-level item it contains.
                foreach (string entry in Directory.EnumerateFileSystemEntries(staging))
                {
                    plan.Add(new RestoreItem(entry, Path.Combine(serverFiles, Path.GetFileName(entry))));
                }
            }
            return plan;
        }

        // ─────────────────────────────── Helpers ───────────────────────────────

        private static void AddTree(List<(string, string)> items, string dir, string relativeTo, string prefix, Func<string, bool> skip)
        {
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (skip(f) || Path.GetFileName(f).Contains(".wgsm-old-") || f.Contains("\\.restore-staging-")) { continue; }
                items.Add((f, prefix + "/" + Path.GetRelativePath(relativeTo, f).Replace('\\', '/')));
            }
        }

        /// <summary>Resolves a relative path inside <paramref name="root"/>, refusing anything that escapes it.</summary>
        internal static bool TryJail(string root, string relative, out string full)
        {
            full = string.Empty;
            try
            {
                string rootFull = Path.GetFullPath(root).TrimEnd('\\');
                string candidate = Path.GetFullPath(Path.Combine(rootFull, BackupSettings.NormalizeRelative(relative).Replace('/', '\\')));
                if (!candidate.StartsWith(rootFull + "\\", StringComparison.OrdinalIgnoreCase)) { return false; }
                full = candidate;
                return true;
            }
            catch { return false; }
        }

        private static void Move(string from, string to)
        {
            if (Directory.Exists(from)) { Directory.Move(from, to); } else { File.Move(from, to); }
        }

        /// <summary>Moves when on the same volume (instant); copies across volumes (external locations).</summary>
        private static void Place(string source, string target)
        {
            bool sameVolume = string.Equals(Path.GetPathRoot(Path.GetFullPath(source)), Path.GetPathRoot(Path.GetFullPath(target)), StringComparison.OrdinalIgnoreCase);
            if (sameVolume) { Move(source, target); return; }
            if (File.Exists(source)) { File.Copy(source, target, overwrite: true); return; }
            foreach (string f in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(target, Path.GetRelativePath(source, f));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(f, dest, overwrite: true);
            }
        }

        private static void DeleteQuietly(string path)
        {
            try
            {
                if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
                else if (File.Exists(path)) { File.Delete(path); }
            }
            catch { /* leave it — never fail a restore over cleanup */ }
        }

        private static long SafeLength(string file)
        {
            try { return new FileInfo(file).Length; } catch { return 0; }
        }

        private static BackupInfo Info(string file, string name, BackupFormat format)
        {
            var fi = new FileInfo(file);
            var m = Regex.Match(fi.Name, @"(\d{8})-?(\d{6})");
            DateTimeOffset created = m.Success && DateTime.TryParseExact(m.Groups[1].Value + m.Groups[2].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt)
                ? new DateTimeOffset(dt)
                : new DateTimeOffset(fi.LastWriteTime);
            return new BackupInfo(name, fi.Length, created, format);
        }

        private static string FormatSize(long bytes) =>
            bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB"
            : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.0} MB"
            : $"{Math.Max(1, bytes / 1024)} KB";
    }

    /// <summary>CRC-32 (the zip checksum), for testing backups.</summary>
    internal static class Crc32
    {
        private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
        {
            uint c = (uint)i;
            for (int k = 0; k < 8; k++) { c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; }
            return c;
        }).ToArray();

        public static uint Update(uint crc, byte[] data, int count)
        {
            for (int i = 0; i < count; i++) { crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8); }
            return crc;
        }
    }

    /// <summary>"Backup before start": runs before a start or auto-restart (not a plain restart, like legacy).</summary>
    public sealed class BackupBeforeStartStep : IPreStartStep
    {
        private readonly BackupService _backups;
        public BackupBeforeStartStep(BackupService backups) => _backups = backups;

        public string Name => "Backup before start";

        public async Task RunAsync(ServerInstance server, StartReason reason, JobContext job)
        {
            if (reason == StartReason.Restart || !BackupSettings.Load(server.Id).BeforeStart) { return; }
            string? error = await _backups.BackupCoreAsync(server, everything: false, job, notes: " | Backup on Start").ConfigureAwait(false);
            if (error != null) { job.Log("Backup before start failed: " + error + " — starting anyway."); }
        }
    }
}
