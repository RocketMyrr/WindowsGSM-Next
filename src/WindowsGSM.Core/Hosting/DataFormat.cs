#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace WindowsGSM.Hosting
{
    /// <summary>
    /// Which version of WindowsGSM Next's own data layout a data folder has (configs/next/data-format.json), and the
    /// steps that bring an older one up to date when the layout ever changes.
    /// <para>
    /// Adding a field never needs a step — older files simply lack it and get the default, and older versions skip
    /// fields they don't know (see <see cref="SafeJson"/>). A step is for a change an old file can't absorb on its
    /// own: a renamed or moved setting, a split file, a value whose meaning changed. Add it to <see cref="Steps"/>
    /// with <see cref="Current"/> + 1 and raise <see cref="Current"/>.
    /// </para>
    /// Before any step runs, WindowsGSM's settings (configs/next and each server's own .json files) are zipped into
    /// backups/ so an upgrade can always be undone by hand.
    /// </summary>
    public static class DataFormat
    {
        /// <summary>The layout this version writes. 1 = everything up to and including 2.0 beta.</summary>
        public static int Current { get; internal set; } = 1; // settable only so tests can rehearse an upgrade

        public const string FileName = "data-format.json";

        /// <summary>One upgrade step: brings the data folder from <c>To - 1</c> to <c>To</c>.</summary>
        public sealed record Step(int To, string What, Action<string> Apply);

        /// <summary>The upgrade steps, oldest first. None yet: the layout hasn't changed since it was stamped.</summary>
        public static IReadOnlyList<Step> Steps { get; set; } = Array.Empty<Step>();

        public sealed class Stamp
        {
            public int Format { get; set; }
            /// <summary>The newest WindowsGSM version that has used this data folder.</summary>
            public string LastVersion { get; set; } = string.Empty;
            public DateTimeOffset UpdatedAt { get; set; }
        }

        /// <summary>
        /// Set when the data folder was last used by a newer layout than this version knows (after going back to an
        /// older version): shown in Health checks.
        /// </summary>
        public static string? NewerDataWarning { get; private set; }

        public static string StampFile(string dataRoot) => Path.Combine(dataRoot, "configs", "next", FileName);

        /// <summary>Reads the stamp, runs any upgrade steps (after a backup) and records this version. Called at engine start.</summary>
        public static void Prepare(string dataRoot, string appVersion, Action<string> log)
        {
            NewerDataWarning = null;
            string file = StampFile(dataRoot);
            var stamp = SafeJson.Read<Stamp>(file);
            appVersion = appVersion.TrimStart('v');

            // No stamp: a new data folder, or one from before stamps existed (2.0.0-alpha.7 and earlier) — both are 1.
            int from = stamp?.Format > 0 ? stamp.Format : 1;

            if (from > Current)
            {
                NewerDataWarning = $"This data folder was last used by WindowsGSM {stamp!.LastVersion}, which stores some settings in a newer way than this version ({appVersion}). " +
                                   "Your settings are kept as they are; anything this version doesn't understand is left alone. Updating again picks everything back up.";
                log(NewerDataWarning);
                return; // never stamp it down
            }

            var due = Steps.Where(s => s.To > from && s.To <= Current).OrderBy(s => s.To).ToList();
            if (due.Count > 0)
            {
                string? backup = Backup(dataRoot, from);
                log($"Updating WindowsGSM's data from format {from} to {Current}" + (backup != null ? $" (settings backed up to {backup})" : "") + ".");
                foreach (var step in due)
                {
                    step.Apply(dataRoot);
                    log($"Data format {step.To}: {step.What}");
                    Write(file, step.To, appVersion, stamp); // a step that's done stays done, even if a later one fails
                }
            }

            // Record the newest version that has used this folder (an older one going back doesn't overwrite it).
            if (stamp == null || stamp.Format != Current || Compare(appVersion, stamp.LastVersion) > 0)
            {
                Write(file, Current, Compare(appVersion, stamp?.LastVersion ?? "") >= 0 ? appVersion : stamp!.LastVersion, stamp);
            }
        }

        private static void Write(string file, int format, string version, Stamp? previous) =>
            SafeJson.Write(file, new Stamp { Format = format, LastVersion = version, UpdatedAt = DateTimeOffset.Now },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        /// <summary>Zips WindowsGSM's own settings (not game files, logs or backups) into backups/; returns the zip.</summary>
        public static string? Backup(string dataRoot, int fromFormat, string? name = null)
        {
            try
            {
                string dir = Path.Combine(dataRoot, "backups");
                Directory.CreateDirectory(dir);
                string zip = Path.Combine(dir, $"{name ?? $"wgsm-settings-before-format-{fromFormat + 1}"}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
                using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
                string next = Path.Combine(dataRoot, "configs", "next");
                if (Directory.Exists(next))
                {
                    foreach (string f in Directory.EnumerateFiles(next, "*", SearchOption.AllDirectories))
                    {
                        string rel = Path.GetRelativePath(dataRoot, f);
                        if (rel.Contains(Path.DirectorySeparatorChar + "keys" + Path.DirectorySeparatorChar) || f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) { continue; }
                        archive.CreateEntryFromFile(f, rel.Replace('\\', '/'));
                    }
                }
                string servers = Path.Combine(dataRoot, "servers");
                if (Directory.Exists(servers))
                {
                    foreach (string configs in Directory.EnumerateDirectories(servers).Select(s => Path.Combine(s, "configs")).Where(Directory.Exists))
                    {
                        foreach (string f in Directory.EnumerateFiles(configs, "*.json"))
                        {
                            archive.CreateEntryFromFile(f, Path.GetRelativePath(dataRoot, f).Replace('\\', '/'));
                        }
                    }
                }
                return zip;
            }
            catch { return null; } // a failed backup is logged by the caller's message (no path)
        }

        /// <summary>"2.0.10" &gt; "2.0.9"; a release beats its pre-releases; "" is oldest.</summary>
        private static int Compare(string a, string b)
        {
            if (string.IsNullOrEmpty(b)) { return string.IsNullOrEmpty(a) ? 0 : 1; }
            if (string.IsNullOrEmpty(a)) { return -1; }
            string[] pa = a.TrimStart('v').Split('-', 2), pb = b.TrimStart('v').Split('-', 2);
            if (!Version.TryParse(pa[0], out var va)) { va = new Version(0, 0); }
            if (!Version.TryParse(pb[0], out var vb)) { vb = new Version(0, 0); }
            int c = va.CompareTo(vb);
            if (c != 0) { return c; }
            if ((pa.Length > 1) != (pb.Length > 1)) { return pa.Length > 1 ? -1 : 1; }
            if (pa.Length == 1) { return 0; }
            string[] x = pa[1].Split('.'), y = pb[1].Split('.');
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                if (i >= x.Length) { return -1; }
                if (i >= y.Length) { return 1; }
                int d = int.TryParse(x[i], out int nx) && int.TryParse(y[i], out int ny) ? nx.CompareTo(ny) : string.CompareOrdinal(x[i], y[i]);
                if (d != 0) { return d; }
            }
            return 0;
        }
    }
}
