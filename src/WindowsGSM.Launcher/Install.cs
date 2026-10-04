using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace WindowsGSM.Launcher
{
    /// <summary>
    /// An install: this launcher's folder, holding versions\&lt;version&gt;\ (the app) and install.json:
    /// which version is current, which came before it (for rollback), and the data folder.
    /// </summary>
    internal sealed class Install
    {
        public string Root { get; private set; }
        public string Current { get; set; }
        public string Previous { get; set; }
        /// <summary>The game servers folder; null when installed only to control other PCs (no agent runs here).</summary>
        public string Data { get; set; }
        public bool ControlOnly => Data == null;

        public string File => Path.Combine(Root, "install.json");
        public string VersionsDir => Path.Combine(Root, "versions");
        public string VersionDir(string version) => Path.Combine(VersionsDir, version);
        public string DesktopExe => Path.Combine(VersionDir(Current), "WindowsGSM.exe");
        public string AgentExe => Path.Combine(VersionDir(Current), "wgsm-agent.exe");

        public static Install Load(string root)
        {
            string file = Path.Combine(root, "install.json");
            if (!System.IO.File.Exists(file)) { return null; }
            try
            {
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(System.IO.File.ReadAllText(file));
                var i = new Install
                {
                    Root = root,
                    Current = d.ContainsKey("current") ? d["current"] as string : null,
                    Previous = d.ContainsKey("previous") ? d["previous"] as string : null,
                    Data = d.ContainsKey("data") ? d["data"] as string : null,
                };
                return string.IsNullOrWhiteSpace(i.Current) ? null : i;
            }
            catch { return null; }
        }

        public static Install Create(string root, string version, string data) =>
            new Install { Root = root, Current = version, Data = data };

        public void Save()
        {
            var d = new Dictionary<string, object> { ["current"] = Current, ["previous"] = Previous, ["data"] = Data };
            string tmp = File + ".tmp";
            System.IO.File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(d));
            if (System.IO.File.Exists(File)) { System.IO.File.Replace(tmp, File, null); } else { System.IO.File.Move(tmp, File); }
        }

        /// <summary>Makes <paramref name="version"/> current (the old one becomes the rollback target) and prunes older ones.</summary>
        public void SwitchTo(string version)
        {
            if (!Directory.Exists(VersionDir(version))) { throw new InvalidOperationException($"Version {version} isn't installed."); }
            if (version == Current) { return; }
            Previous = Current;
            Current = version;
            Save();
            Prune();
        }

        /// <summary>Keeps the current and previous versions; removes the rest (skipping any still in use).</summary>
        public void Prune()
        {
            if (!Directory.Exists(VersionsDir)) { return; }
            foreach (string dir in Directory.GetDirectories(VersionsDir))
            {
                string v = Path.GetFileName(dir);
                if (v == Current || v == Previous) { continue; }
                try { Directory.Delete(dir, true); } catch { /* still running from there; next time */ }
            }
        }

        /// <summary>Starts a program from the current version, telling it where the launcher and install are.</summary>
        public Process Start(string exe, IEnumerable<string> args, bool hidden)
        {
            // Installed only to control other PCs: no data folder, and the app is told so.
            var psi = new ProcessStartInfo(exe, Quote((Data == null ? new[] { "--remote" } : new[] { "--data", Data }).Concat(args)))
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe),
                CreateNoWindow = hidden,
            };
            psi.EnvironmentVariables["WGSM_LAUNCHER"] = Path.Combine(Root, "WindowsGSM.exe");
            psi.EnvironmentVariables["WGSM_INSTALL_ROOT"] = Root;
            return Process.Start(psi);
        }

        /// <summary>The default install folder (per user, no administrator needed).</summary>
        public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WindowsGSM");

        /// <summary>An installed copy on this PC (from Apps &amp; features, or the default folder), or null.</summary>
        public static Install FindInstalled()
        {
            var places = new List<string>();
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Shell.UninstallKeyPath))
                {
                    if (key?.GetValue("InstallLocation") is string loc && loc.Length > 0) { places.Add(loc); }
                }
            }
            catch { /* no entry */ }
            places.Add(DefaultRoot);
            foreach (string place in places)
            {
                var i = Load(place);
                if (i != null && Directory.Exists(i.VersionDir(i.Current))) { return i; }
            }
            return null;
        }

        /// <summary>The files and folders setup puts in an install folder — all uninstall ever removes.</summary>
        public static readonly string[] OwnEntries = { "versions", "downloads", "WindowsGSM.exe", "WindowsGSM.exe.config", "WindowsGSM.exe.old", "install.json", "install.json.tmp" };

        /// <summary>
        /// Where to install for a folder the user picked: an empty or new folder, or an existing install, as is;
        /// any other folder gets a WindowsGSM subfolder — so the app never mixes with (or uninstalls) other files.
        /// </summary>
        public static string ResolveRoot(string chosen)
        {
            string full = Path.GetFullPath(chosen.Trim()).TrimEnd('\\');
            if (!Directory.Exists(full) || !Directory.EnumerateFileSystemEntries(full).Any()) { return full; }
            if (System.IO.File.Exists(Path.Combine(full, "install.json")) || Directory.Exists(Path.Combine(full, "versions"))) { return full; }
            if (string.Equals(Path.GetFileName(full), "WindowsGSM", StringComparison.OrdinalIgnoreCase)
                && Directory.EnumerateFileSystemEntries(full).All(e => OwnEntries.Contains(Path.GetFileName(e), StringComparer.OrdinalIgnoreCase))) { return full; }
            return Path.Combine(full, "WindowsGSM");
        }

        /// <summary>True when one folder is the other or inside it.</summary>
        public static bool Overlaps(string a, string b)
        {
            string x = Path.GetFullPath(a).TrimEnd('\\') + "\\", y = Path.GetFullPath(b).TrimEnd('\\') + "\\";
            return x.StartsWith(y, StringComparison.OrdinalIgnoreCase) || y.StartsWith(x, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Null if this user can create files there (or in its nearest existing parent), else why not.</summary>
        public static string CanWrite(string folder)
        {
            string probe = Path.GetFullPath(folder);
            while (!Directory.Exists(probe)) { probe = Path.GetDirectoryName(probe); if (probe == null) { return "That drive isn't available."; } }
            try
            {
                string test = Path.Combine(probe, ".wgsm-write-test-" + Guid.NewGuid().ToString("N"));
                System.IO.File.WriteAllText(test, "");
                System.IO.File.Delete(test);
                return null;
            }
            catch (UnauthorizedAccessException) { return "Windows only lets administrators write there (Program Files and similar). Pick a folder in your user profile, or keep the suggested one."; }
            catch (IOException ex) { return ex.Message; }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFile(string path);

        /// <summary>
        /// Removes the "downloaded from the internet" mark Windows puts on files from a zip, so the installed copy
        /// doesn't trigger SmartScreen again every time it starts.
        /// </summary>
        public static void Unblock(string file)
        {
            try { DeleteFile(file + ":Zone.Identifier"); } catch { /* not marked */ }
        }

        public static string Quote(IEnumerable<string> args) =>
            string.Join(" ", args.Select(a => a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? a : "\"" + a.TrimEnd('\\').Replace("\"", "\\\"") + "\""));

        /// <summary>The newest versions\&lt;v&gt; folder next to a launcher (a fresh download holds exactly one).</summary>
        public static string NewestVersionIn(string root)
        {
            string dir = Path.Combine(root, "versions");
            if (!Directory.Exists(dir)) { return null; }
            return Directory.GetDirectories(dir).Select(Path.GetFileName)
                .Where(v => System.IO.File.Exists(Path.Combine(dir, v, "wgsm-agent.exe")))
                .OrderByDescending(v => v, Comparer<string>.Create(CompareVersions)).FirstOrDefault();
        }

        /// <summary>"2.0.10" &gt; "2.0.9"; a release beats its pre-releases ("2.1.0" &gt; "2.1.0-beta.2").</summary>
        public static int CompareVersions(string a, string b)
        {
            string[] pa = (a ?? "").TrimStart('v').Split(new[] { '-' }, 2), pb = (b ?? "").TrimStart('v').Split(new[] { '-' }, 2);
            Version va, vb;
            if (!Version.TryParse(pa[0], out va)) { va = new Version(0, 0); }
            if (!Version.TryParse(pb[0], out vb)) { vb = new Version(0, 0); }
            int c = va.CompareTo(vb);
            if (c != 0) { return c; }
            bool preA = pa.Length > 1, preB = pb.Length > 1;
            if (preA != preB) { return preA ? -1 : 1; }
            return preA ? ComparePre(pa[1], pb[1]) : 0;
        }

        /// <summary>"alpha.2" &lt; "alpha.10" &lt; "beta.1".</summary>
        private static int ComparePre(string a, string b)
        {
            string[] x = a.Split('.'), y = b.Split('.');
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                if (i >= x.Length) { return -1; }
                if (i >= y.Length) { return 1; }
                int nx, ny;
                int c = int.TryParse(x[i], out nx) && int.TryParse(y[i], out ny) ? nx.CompareTo(ny) : string.CompareOrdinal(x[i], y[i]);
                if (c != 0) { return c; }
            }
            return 0;
        }
    }
}
