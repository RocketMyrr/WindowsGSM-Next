#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WindowsGSM.Hosting
{
    /// <summary>
    /// A backup of WindowsGSM's own setup — accounts, agent settings, automations, notification channels, the
    /// Discord bot, off-site settings, tags, restart warnings, templates, the hub's machines, plugins and each
    /// server's settings — not game files (server backups are for those). For a new PC, or after a disk dies.
    /// <para>
    /// Passwords and tokens are encrypted for this Windows account, so they can't simply be copied. They're either
    /// left out (and entered again after restoring) or carried encrypted with a passphrase you choose
    /// (AES-256-GCM, key from PBKDF2-SHA256), and encrypted for the restoring account again on the way in.
    /// </para>
    /// A restore is staged, then applied when the agent next starts — before anything reads the settings — after
    /// zipping up the current ones into backups\. Game servers keep running throughout.
    /// </summary>
    public static class SetupArchive
    {
        public const int Format = 1;
        private const string ManifestName = "wgsm-setup.json";
        private const string ExportPrefix = "wgsm-export:";
        private const int KdfIterations = 600_000;
        private const long MaxArchive = 200L * 1024 * 1024, MaxEntries = 20_000;

        /// <summary>WindowsGSM's own settings in configs\next (not sessions, sign-in keys, history or caches).</summary>
        private static readonly string[] NextFiles =
        {
            "agent.json", "users.json", "automations.json", "discord-bot.json", "notify-channels.json", "offsite.json",
            "restart-warnings.json", "tags.json", "machines.json", "steam.json",
        };

        public sealed class Manifest
        {
            public int Format { get; set; } = SetupArchive.Format;
            public string Version { get; set; } = "";
            public string MachineId { get; set; } = "";
            public string MachineName { get; set; } = "";
            public DateTimeOffset CreatedAt { get; set; }
            /// <summary>Set when passwords and tokens are inside, encrypted with a passphrase.</summary>
            public string? Salt { get; set; }
            /// <summary>A known value encrypted with the passphrase: tells a wrong passphrase apart at once.</summary>
            public string? Check { get; set; }
            public int SecretsIncluded { get; set; }
            public int SecretsLeftOut { get; set; }
            public List<string> Servers { get; set; } = new();
        }

        public sealed record ExportResult(int Files, int SecretsIncluded, int SecretsLeftOut);

        // ───────────────────────────── Export ─────────────────────────────

        /// <summary>Writes the setup to <paramref name="output"/> as a zip.</summary>
        public static ExportResult Export(string dataRoot, Stream output, string version, string machineId, string machineName, string? passphrase)
        {
            byte[]? key = null;
            var manifest = new Manifest { Version = version.TrimStart('v'), MachineId = machineId, MachineName = machineName, CreatedAt = DateTimeOffset.Now };
            if (!string.IsNullOrEmpty(passphrase))
            {
                byte[] salt = RandomNumberGenerator.GetBytes(16);
                key = DeriveKey(passphrase, salt);
                manifest.Salt = Convert.ToBase64String(salt);
                manifest.Check = Seal("WindowsGSM", key);
            }
            int files = 0;
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (full, entry) in Collect(dataRoot))
                {
                    if (entry.StartsWith("servers/", StringComparison.Ordinal)) { string id = entry.Split('/')[1]; if (!manifest.Servers.Contains(id)) { manifest.Servers.Add(id); } }
                    byte[] bytes = ReadShared(full);
                    if (entry.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) { bytes = OutgoingSecrets(bytes, key, manifest); }
                    var e = zip.CreateEntry(entry, CompressionLevel.Optimal);
                    using (var s = e.Open()) { s.Write(bytes, 0, bytes.Length); }
                    files++;
                }
                var m = zip.CreateEntry(ManifestName);
                using (var s = m.Open()) { JsonSerializer.Serialize(s, manifest, new JsonSerializerOptions { WriteIndented = true }); }
            }
            return new ExportResult(files, manifest.SecretsIncluded, manifest.SecretsLeftOut);
        }

        /// <summary>The files that make up the setup: (full path, path in the zip).</summary>
        private static IEnumerable<(string Full, string Entry)> Collect(string dataRoot)
        {
            string next = Path.Combine(dataRoot, "configs", "next");
            foreach (string name in NextFiles)
            {
                string f = Path.Combine(next, name);
                if (File.Exists(f)) { yield return (f, "configs/next/" + name); }
            }
            string templates = Path.Combine(next, "templates");
            if (Directory.Exists(templates))
            {
                foreach (string f in Directory.EnumerateFiles(templates, "*.json")) { yield return (f, "configs/next/templates/" + Path.GetFileName(f)); }
            }
            string plugins = Path.Combine(dataRoot, "plugins");
            if (Directory.Exists(plugins))
            {
                foreach (string f in Directory.EnumerateFiles(plugins, "*", SearchOption.AllDirectories).Where(Keep))
                {
                    yield return (f, "plugins/" + Path.GetRelativePath(plugins, f).Replace('\\', '/'));
                }
            }
            string servers = Path.Combine(dataRoot, "servers");
            if (Directory.Exists(servers))
            {
                foreach (string dir in Directory.EnumerateDirectories(servers))
                {
                    string configs = Path.Combine(dir, "configs");
                    if (!Directory.Exists(configs)) { continue; }
                    string id = Path.GetFileName(dir);
                    foreach (string f in Directory.EnumerateFiles(configs, "*", SearchOption.AllDirectories).Where(Keep))
                    {
                        string rel = Path.GetRelativePath(configs, f).Replace('\\', '/');
                        if (rel.StartsWith("history/", StringComparison.OrdinalIgnoreCase)) { continue; } // config history: past versions, not the setup
                        yield return (f, $"servers/{id}/configs/{rel}");
                    }
                }
            }
        }

        /// <summary>Not leftovers: previous copies, unreadable copies, half-written files.</summary>
        private static bool Keep(string f)
        {
            string n = Path.GetFileName(f);
            return !n.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) && !n.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                && !n.Contains(".unreadable-", StringComparison.OrdinalIgnoreCase) && !n.EndsWith(".wgsm-upload", StringComparison.OrdinalIgnoreCase)
                && new FileInfo(f).Length < 20 * 1024 * 1024;
        }

        private static byte[] ReadShared(string file)
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            return ms.ToArray();
        }

        /// <summary>Every "dpapi:" value: encrypted with the passphrase, or left out.</summary>
        private static byte[] OutgoingSecrets(byte[] json, byte[]? key, Manifest manifest)
        {
            JsonNode? root;
            try { root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
            catch (JsonException) { return json; } // not JSON after all: as it is
            bool changed = false;
            Walk(root, value =>
            {
                if (!Secret.IsProtected(value)) { return null; }
                changed = true;
                string plain = Secret.Unprotect(value);
                if (plain.Length == 0) { return ""; } // unreadable here too: nothing to carry
                if (key == null) { manifest.SecretsLeftOut++; return ""; }
                manifest.SecretsIncluded++;
                return ExportPrefix + Seal(plain, key);
            });
            return changed ? Encoding.UTF8.GetBytes(root!.ToJsonString(new JsonSerializerOptions { WriteIndented = true })) : json;
        }

        // ───────────────────────────── Restore ─────────────────────────────

        public sealed record StageResult(Manifest Manifest, int Files, IReadOnlyList<string> ServersSkipped);

        private static string Staging(string dataRoot) => Path.Combine(dataRoot, "configs", "next-restore");
        private static string PendingFile(string dataRoot) => Path.Combine(Staging(dataRoot), "pending.json");
        public static string ResultFile(string dataRoot) => Path.Combine(dataRoot, "configs", "next", "last-restore.json");

        public sealed class Pending
        {
            public Manifest Manifest { get; set; } = new();
            public bool KeepIdentity { get; set; }
            public string RequestedBy { get; set; } = "";
            public DateTimeOffset StagedAt { get; set; }
        }

        public sealed class Result
        {
            public DateTimeOffset RestoredAt { get; set; }
            public string From { get; set; } = "";
            public string Version { get; set; } = "";
            public int Files { get; set; }
            public string? SettingsBackup { get; set; }
            public List<string> Notes { get; set; } = new();
        }

        /// <summary>The restore waiting for the next start, or null.</summary>
        public static Pending? PendingRestore(string dataRoot) => File.Exists(PendingFile(dataRoot)) ? SafeJson.Read<Pending>(PendingFile(dataRoot)) : null;

        public static void CancelPending(string dataRoot)
        {
            try { if (Directory.Exists(Staging(dataRoot))) { Directory.Delete(Staging(dataRoot), true); } } catch { /* next start tidies */ }
        }

        /// <summary>
        /// Checks a setup backup and stages it for the next agent start. Throws <see cref="InvalidDataException"/>
        /// with a message worth showing when it can't be used (not a setup backup, a newer format, a wrong
        /// passphrase, an unsafe path).
        /// </summary>
        public static StageResult Stage(string dataRoot, Stream archive, string? passphrase, bool keepIdentity, string requestedBy)
        {
            using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
            var manifestEntry = zip.GetEntry(ManifestName) ?? throw new InvalidDataException("That isn't a WindowsGSM setup backup (no wgsm-setup.json inside).");
            Manifest manifest;
            using (var s = manifestEntry.Open()) { manifest = JsonSerializer.Deserialize<Manifest>(s) ?? throw new InvalidDataException("The backup's description can't be read."); }
            if (manifest.Format > Format) { throw new InvalidDataException($"This backup was made by a newer WindowsGSM ({manifest.Version}). Update this one first."); }
            if (zip.Entries.Count > MaxEntries || zip.Entries.Sum(e => e.Length) > MaxArchive) { throw new InvalidDataException("That backup is too big to be a WindowsGSM setup backup."); }

            byte[]? key = null;
            if (manifest.Salt != null && manifest.SecretsIncluded > 0)
            {
                if (string.IsNullOrEmpty(passphrase)) { throw new InvalidDataException("This backup includes passwords and tokens: enter the passphrase it was made with."); }
                key = DeriveKey(passphrase, Convert.FromBase64String(manifest.Salt));
                if (manifest.Check == null || Open(manifest.Check, key) != "WindowsGSM") { throw new InvalidDataException("That passphrase isn't the one this backup was made with."); }
            }

            string staging = Staging(dataRoot);
            CancelPending(dataRoot);
            Directory.CreateDirectory(staging);
            var skipped = new List<string>();
            int files = 0;
            try
            {
                foreach (var e in zip.Entries)
                {
                    if (e.FullName == ManifestName || e.FullName.EndsWith("/", StringComparison.Ordinal)) { continue; }
                    string name = e.FullName.Replace('\\', '/');
                    if (!Allowed(name)) { throw new InvalidDataException($"The backup contains a file it shouldn't ({name})."); }
                    string target = Path.GetFullPath(Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { throw new InvalidDataException($"The backup contains an unsafe path ({name})."); }
                    byte[] bytes;
                    using (var s = e.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); bytes = ms.ToArray(); }
                    if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) { bytes = IncomingSecrets(bytes, key); }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, bytes);
                    files++;
                }
                foreach (string id in manifest.Servers.Where(id => !Directory.Exists(Path.Combine(dataRoot, "servers", id)))) { skipped.Add(id); }
                SafeJson.Write(PendingFile(dataRoot), new Pending { Manifest = manifest, KeepIdentity = keepIdentity, RequestedBy = requestedBy, StagedAt = DateTimeOffset.Now });
            }
            catch { CancelPending(dataRoot); throw; }
            return new StageResult(manifest, files, skipped);
        }

        /// <summary>Only the kinds of files an export writes.</summary>
        private static bool Allowed(string entry)
        {
            if (entry.Contains("..", StringComparison.Ordinal) || entry.StartsWith("/", StringComparison.Ordinal) || entry.Contains(':')) { return false; }
            if (entry.StartsWith("configs/next/templates/", StringComparison.Ordinal)) { return entry.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && entry.Count(c => c == '/') == 3; }
            if (entry.StartsWith("configs/next/", StringComparison.Ordinal)) { return NextFiles.Contains(entry["configs/next/".Length..]); }
            if (entry.StartsWith("plugins/", StringComparison.Ordinal)) { return true; }
            var parts = entry.Split('/');
            return parts.Length >= 4 && parts[0] == "servers" && parts[2] == "configs" && parts[1].Length > 0 && parts[1].All(char.IsLetterOrDigit);
        }

        /// <summary>"wgsm-export:" values: opened with the passphrase and encrypted for this Windows account.</summary>
        private static byte[] IncomingSecrets(byte[] json, byte[]? key)
        {
            JsonNode? root;
            try { root = JsonNode.Parse(json); }
            catch (JsonException) { return json; }
            bool changed = false;
            Walk(root, value =>
            {
                if (!value.StartsWith(ExportPrefix, StringComparison.Ordinal)) { return null; }
                changed = true;
                if (key == null) { return ""; }
                return Secret.Protect(Open(value[ExportPrefix.Length..], key) ?? "");
            });
            return changed ? Encoding.UTF8.GetBytes(root!.ToJsonString(new JsonSerializerOptions { WriteIndented = true })) : json;
        }

        /// <summary>
        /// At engine start: applies a staged restore (after zipping up the current settings), then removes the
        /// staging. Servers the backup has settings for but that don't exist here are left out.
        /// </summary>
        public static void ApplyPending(string dataRoot, Action<string> log)
        {
            var pending = PendingRestore(dataRoot);
            string staging = Staging(dataRoot);
            if (pending == null) { if (Directory.Exists(staging)) { CancelPending(dataRoot); } return; }

            string? backup = DataFormat.Backup(dataRoot, -1, "wgsm-settings-before-restore");
            var result = new Result { RestoredAt = DateTimeOffset.Now, From = $"{pending.Manifest.MachineName} ({pending.Manifest.CreatedAt:yyyy-MM-dd HH:mm})", Version = pending.Manifest.Version, SettingsBackup = backup };
            string next = Path.Combine(dataRoot, "configs", "next");
            string? thisMachine = null;
            try { thisMachine = JsonNode.Parse(File.ReadAllText(Path.Combine(next, "agent.json")))?["MachineId"]?.GetValue<string>(); } catch { /* new install */ }

            foreach (string f in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(staging, f);
                if (rel.Equals("pending.json", StringComparison.OrdinalIgnoreCase)) { continue; }
                string[] parts = rel.Split(Path.DirectorySeparatorChar);
                if (parts[0] == "servers" && !Directory.Exists(Path.Combine(dataRoot, "servers", parts[1]))) { continue; }
                string target = Path.Combine(dataRoot, rel);
                byte[] bytes = File.ReadAllBytes(f);
                if (rel.Equals(Path.Combine("configs", "next", "agent.json"), StringComparison.OrdinalIgnoreCase)) { bytes = AdjustAgentSettings(bytes, pending.KeepIdentity, thisMachine, result.Notes); }
                if (rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(target)) { bytes = KeepCurrentSecrets(bytes, target); }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target)) { File.Copy(target, target + ".bak", overwrite: true); }
                File.WriteAllBytes(target, bytes);
                result.Files++;
            }
            int skipped = pending.Manifest.Servers.Count(id => !Directory.Exists(Path.Combine(dataRoot, "servers", id)));
            if (skipped > 0) { result.Notes.Add($"{skipped} server(s) in the backup aren't on this PC; their settings were left out. Restore those servers from their own backups (or move them here), then restore the setup again if you want their settings."); }
            if (pending.Manifest.SecretsLeftOut > 0) { result.Notes.Add($"{pending.Manifest.SecretsLeftOut} password(s) and token(s) weren't in the backup. Ones this PC already had were kept; check the rest (Discord bot, notification channels, off-site backups, Steam account, HTTPS) and enter any that are missing."); }
            SafeJson.Write(ResultFile(dataRoot), result, new JsonSerializerOptions { WriteIndented = true });
            CancelPending(dataRoot);
            log($"Restored WindowsGSM's setup from {result.From}: {result.Files} file(s)" + (backup != null ? $"; the settings it replaced are in {Path.GetFileName(backup)}." : "."));
        }

        /// <summary>
        /// A password or token the backup left out (emptied) keeps the value this PC already has in the same place —
        /// so restoring your own backup on the same PC doesn't wipe the Discord token, webhooks and so on.
        /// </summary>
        private static byte[] KeepCurrentSecrets(byte[] incoming, string currentFile)
        {
            JsonNode? next, current;
            try { next = JsonNode.Parse(incoming); current = JsonNode.Parse(File.ReadAllText(currentFile)); }
            catch (Exception ex) when (ex is JsonException or IOException) { return incoming; }
            bool changed = false;
            void Merge(JsonNode? n, JsonNode? c)
            {
                if (n is JsonObject no && c is JsonObject co)
                {
                    foreach (var key in no.Select(kv => kv.Key).ToList())
                    {
                        if (no[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0
                            && co[key] is JsonValue cv && cv.TryGetValue<string>(out var cs) && Secret.IsProtected(cs))
                        {
                            no[key] = cs;
                            changed = true;
                        }
                        else { Merge(no[key], co[key]); }
                    }
                }
                else if (n is JsonArray na && c is JsonArray ca)
                {
                    // Lists (channels, admins…) line up by "Id" when they have one, otherwise by position.
                    for (int i = 0; i < na.Count; i++)
                    {
                        string? id = (na[i] as JsonObject)?["Id"]?.ToString();
                        JsonNode? match = id != null ? ca.FirstOrDefault(x => (x as JsonObject)?["Id"]?.ToString() == id) : i < ca.Count ? ca[i] : null;
                        Merge(na[i], match);
                    }
                }
            }
            Merge(next, current);
            return changed ? Encoding.UTF8.GetBytes(next!.ToJsonString(new JsonSerializerOptions { WriteIndented = true })) : incoming;
        }

        /// <summary>This PC keeps its own identity unless the backup is taking over from a PC that's gone.</summary>
        private static byte[] AdjustAgentSettings(byte[] json, bool keepIdentity, string? thisMachine, List<string> notes)
        {
            JsonNode? node;
            try { node = JsonNode.Parse(json); } catch (JsonException) { return json; }
            if (node is not JsonObject o) { return json; }
            if (!keepIdentity)
            {
                if (thisMachine != null) { o["MachineId"] = thisMachine; }
                if (o["HubUrl"] is JsonValue hub && hub.ToString().Length > 0)
                {
                    o["HubUrl"] = null; o["HubCredential"] = null; o["HubName"] = null; o["HubCertThumbprint"] = null;
                    notes.Add("This PC wasn't joined to the backup's hub (it's a different machine): join it from Machines → Join a hub.");
                }
            }
            else { notes.Add("This PC took over the backed-up machine's identity: turn the old PC's WindowsGSM off so the two don't clash."); }
            return Encoding.UTF8.GetBytes(o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        // ───────────────────────────── Helpers ─────────────────────────────

        /// <summary>Calls <paramref name="replace"/> for every string value; a non-null answer replaces it.</summary>
        private static void Walk(JsonNode? node, Func<string, string?> replace)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var key in o.Select(kv => kv.Key).ToList())
                    {
                        var child = o[key];
                        if (child is JsonValue v && v.TryGetValue<string>(out var s)) { if (replace(s) is { } r) { o[key] = r; } }
                        else { Walk(child, replace); }
                    }
                    break;
                case JsonArray a:
                    for (int i = 0; i < a.Count; i++)
                    {
                        if (a[i] is JsonValue v && v.TryGetValue<string>(out var s)) { if (replace(s) is { } r) { a[i] = r; } }
                        else { Walk(a[i], replace); }
                    }
                    break;
            }
        }

        private static byte[] DeriveKey(string passphrase, byte[] salt) =>
            Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, KdfIterations, HashAlgorithmName.SHA256, 32);

        private static string Seal(string plain, byte[] key)
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(12), data = Encoding.UTF8.GetBytes(plain), cipher = new byte[data.Length], tag = new byte[16];
            using (var aes = new AesGcm(key, 16)) { aes.Encrypt(nonce, data, cipher, tag); }
            return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
        }

        private static string? Open(string sealedValue, byte[] key)
        {
            try
            {
                byte[] all = Convert.FromBase64String(sealedValue);
                byte[] nonce = all[..12], tag = all[12..28], cipher = all[28..], plain = new byte[cipher.Length];
                using (var aes = new AesGcm(key, 16)) { aes.Decrypt(nonce, cipher, tag, plain); }
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException) { return null; }
        }
    }
}
