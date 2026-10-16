#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace WindowsGSM.Functions
{
    /// <summary>
    /// NEXT: the right Java for a Minecraft server.jar. Legacy took the newest Java it found in Program Files\Java (by
    /// comparing version text, so "9" beat "25") and, only when there was none at all, ran Oracle's JDK 22 installer —
    /// so a PC with Java 8 started Minecraft 26.x with Java 8 and it exited at once. Now: the jar says which Java it
    /// needs; an installed one that's new enough is used (the oldest such, as older Minecraft and mods dislike much
    /// newer Java); otherwise Eclipse Temurin of that version is downloaded into WindowsGSM's own folder (a zip,
    /// checksum-verified — nothing is installed on the PC).
    /// </summary>
    public static class JavaRuntimes
    {
        /// <summary>One Java on this PC.</summary>
        public sealed record Runtime(string Exe, int Major);

        /// <summary>Where WindowsGSM keeps the Java it downloads (data\java\temurin-25\…).</summary>
        public static string OwnFolder => Path.Combine(Hosting.WgsmEnvironment.DataRoot, "java");

        /// <summary>Swappable for tests: Adoptium's API.</summary>
        public static Func<string, Task<string>> FetchJson { get; set; } = url => Http.DownloadStringAsync(url);
        public static Func<string, string, Task> Download { get; set; } = (url, path) => Http.DownloadFileAsync(url, path);

        /// <summary>The Java major version a server jar needs, or null when it doesn't say (then any Java is tried).</summary>
        public static int? RequiredMajor(string jarPath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(jarPath);
                // Vanilla (and jars built from it): version.json → "java_version": 25.
                var version = zip.GetEntry("version.json");
                if (version != null)
                {
                    using var reader = new StreamReader(version.Open());
                    if (JObject.Parse(reader.ReadToEnd())["java_version"]?.Value<int?>() is int v && v > 0) { return v; }
                }
                // Otherwise the main class itself: class file version 69 = Java 25 (major - 44).
                var manifest = zip.GetEntry("META-INF/MANIFEST.MF");
                if (manifest == null) { return null; }
                using var m = new StreamReader(manifest.Open());
                var main = Regex.Match(m.ReadToEnd(), @"^Main-Class:\s*(\S+)", RegexOptions.Multiline);
                if (!main.Success) { return null; }
                var cls = zip.GetEntry(main.Groups[1].Value.Replace('.', '/') + ".class");
                if (cls == null) { return null; }
                using var s = cls.Open();
                var head = new byte[8];
                if (s.Read(head, 0, 8) < 8 || head[0] != 0xCA || head[1] != 0xFE) { return null; }
                int classVersion = head[6] << 8 | head[7];
                return classVersion > 44 ? classVersion - 44 : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Every Java found: WindowsGSM's own, JAVA_HOME, the usual vendors' folders and PATH.</summary>
        public static IReadOnlyList<Runtime> Installed()
        {
            var roots = new List<string>();
            void Children(string? dir) { try { if (dir != null && Directory.Exists(dir)) { roots.AddRange(Directory.EnumerateDirectories(dir)); } } catch { /* no access */ } }

            Children(OwnFolder);
            if (Environment.GetEnvironmentVariable("JAVA_HOME") is string home && home.Length > 0) { roots.Add(home.Trim('"')); }
            foreach (string pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(p => p.Length > 0).Distinct())
            {
                foreach (string vendor in new[] { "Java", "Eclipse Adoptium", "Eclipse Foundation", "Microsoft", "Zulu", "Amazon Corretto", "BellSoft", "Semeru", "OpenJDK" })
                {
                    Children(Path.Combine(pf, vendor));
                }
            }
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Where(d => d.Length > 0))
            {
                try { if (File.Exists(Path.Combine(dir, "java.exe"))) { roots.Add(Path.GetDirectoryName(dir.TrimEnd('\\'))!); } } catch { /* bad entry */ }
            }

            var found = new Dictionary<string, Runtime>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (string exe in new[] { Path.Combine(root, "bin", "java.exe"), Path.Combine(root, "jre", "bin", "java.exe") })
                {
                    if (!File.Exists(exe) || found.ContainsKey(exe)) { continue; }
                    if (MajorOf(root, exe) is int major) { found[exe] = new Runtime(exe, major); break; }
                }
            }
            return found.Values.ToList();
        }

        /// <summary>The oldest installed Java that is at least <paramref name="major"/> (any Java when null).</summary>
        public static Runtime? Best(IEnumerable<Runtime> installed, int? major) =>
            installed.Where(r => major == null || r.Major >= major).OrderBy(r => major == null ? -r.Major : r.Major).FirstOrDefault();

        /// <summary>
        /// java.exe for this jar: an installed Java that's new enough, or Temurin of the needed version downloaded into
        /// WindowsGSM's folder. <paramref name="note"/> hears what happened (for the server's log).
        /// </summary>
        public static async Task<string> ForJarAsync(string jarPath, Action<string> note)
        {
            int? needs = RequiredMajor(jarPath);
            if (Best(Installed(), needs) is Runtime ok) { return ok.Exe; }

            int major = needs ?? 25;
            note($"This server needs Java {major}, which isn't installed. Downloading Eclipse Temurin {major} for WindowsGSM's own use (about 60 MB)…");
            string exe = await DownloadTemurinAsync(major).ConfigureAwait(false);
            note($"Java {major} is ready ({exe}).");
            return exe;
        }

        /// <summary>Downloads Temurin's JRE (JDK if there's no JRE build) for <paramref name="major"/>, checks it, unpacks it.</summary>
        public static async Task<string> DownloadTemurinAsync(int major)
        {
            string target = Path.Combine(OwnFolder, $"temurin-{major}");
            JToken? asset = null;
            foreach (string image in new[] { "jre", "jdk" })
            {
                var list = JArray.Parse(await FetchJson($"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?os=windows&architecture=x64&image_type={image}&vendor=eclipse").ConfigureAwait(false));
                asset = list.FirstOrDefault(a => ((string?)a["binary"]?["package"]?["name"])?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true);
                if (asset != null) { break; }
            }
            var package = asset?["binary"]?["package"] ?? throw new InvalidOperationException($"Adoptium has no Java {major} for Windows x64.");
            string link = (string?)package["link"] ?? throw new InvalidOperationException("Adoptium gave no download link.");
            string checksum = ((string?)package["checksum"] ?? "").Trim();
            if (!link.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || checksum.Length != 64) { throw new InvalidOperationException("Adoptium's answer didn't look right; nothing was downloaded."); }

            Directory.CreateDirectory(OwnFolder);
            string zip = Path.Combine(OwnFolder, $"temurin-{major}.zip.part");
            string staging = target + ".unpacking";
            try
            {
                await Download(link, zip).ConfigureAwait(false);
                string actual;
                using (var stream = File.OpenRead(zip)) { actual = Convert.ToHexString(SHA256.HashData(stream)); }
                if (!actual.Equals(checksum, StringComparison.OrdinalIgnoreCase)) { throw new InvalidDataException("The Java download didn't match Adoptium's checksum, so it wasn't used."); }

                if (Directory.Exists(staging)) { Directory.Delete(staging, true); }
                Engine.Services.AddonService.ExtractSafely(zip, staging);
                // The zip holds one folder (jdk-25.0.4.1+1-jre); that becomes temurin-25.
                string inner = Directory.EnumerateDirectories(staging).FirstOrDefault(d => File.Exists(Path.Combine(d, "bin", "java.exe")))
                    ?? (File.Exists(Path.Combine(staging, "bin", "java.exe")) ? staging : throw new InvalidDataException("The Java download had no java.exe."));
                if (Directory.Exists(target)) { Directory.Delete(target, true); }
                Directory.Move(inner, target);
            }
            finally
            {
                try { File.Delete(zip); } catch { /* temp */ }
                try { if (Directory.Exists(staging)) { Directory.Delete(staging, true); } } catch { /* temp */ }
            }
            return Path.Combine(target, "bin", "java.exe");
        }

        /// <summary>A Java's major version: from its "release" file (no need to run it), else from "java -version".</summary>
        private static int? MajorOf(string root, string exe)
        {
            try
            {
                foreach (string release in new[] { Path.Combine(root, "release"), Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(exe)!)!, "release") })
                {
                    if (!File.Exists(release)) { continue; }
                    var m = Regex.Match(File.ReadAllText(release), "JAVA_VERSION=\"([^\"]+)\"");
                    if (m.Success) { return Major(m.Groups[1].Value); }
                }
                return Major(JavaVersionText(exe));
            }
            catch (Exception) { return null; }
        }

        /// <summary>"1.8.0_491" → 8, "25.0.1" → 25, "17" → 17.</summary>
        public static int? Major(string? version)
        {
            var m = Regex.Match(version ?? "", @"^(\d+)(?:\.(\d+))?");
            if (!m.Success) { return null; }
            int first = int.Parse(m.Groups[1].Value);
            return first == 1 && m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : first;
        }

        private static string JavaVersionText(string exe)
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "-version")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            })!;
            var err = p.StandardError.ReadToEndAsync();
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } return ""; }
            var m = Regex.Match(err.Result + output.Result, "version \"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : "";
        }
    }
}
