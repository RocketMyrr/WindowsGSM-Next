#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using WindowsGSM.Functions;

namespace WindowsGSM.Installer
{
    /// <summary>
    /// NEXT: which Minecraft: Java Edition server runs — Vanilla (Mojang), Paper, Purpur or Fabric — and which version.
    /// The choice is kept next to the server (serverfiles\wgsm-minecraft.json); updates then fetch the newest build
    /// of that software for the same Minecraft version (or the newest version, when "latest" was picked), instead of
    /// the legacy behaviour of always replacing server.jar with the newest Vanilla.
    /// </summary>
    public static class MinecraftSoftware
    {
        public sealed record Flavor(string Id, string Name, string? AddonFolder, string Description);

        public static readonly IReadOnlyList<Flavor> Flavors = new[]
        {
            new Flavor("vanilla", "Vanilla", null, "Mojang's own server. No plugins or mods."),
            new Flavor("paper", "Paper", "plugins", "Fast, and runs Bukkit/Spigot/Paper plugins. The usual choice."),
            new Flavor("purpur", "Purpur", "plugins", "Paper with extra gameplay settings. Runs Paper plugins."),
            new Flavor("fabric", "Fabric", "mods", "Lightweight mod loader — for server-side Fabric mods."),
            new Flavor("neoforge", "NeoForge", "mods", "The modern Forge mod loader — for NeoForge mods and modpacks."),
        };

        private const string NeoForgeMaven = "https://maven.neoforged.net/releases/net/neoforged/neoforge";

        /// <summary>
        /// NeoForge's version → the Minecraft version it's for: "21.1.209" → 1.21.1, "21.0.5" → 1.21, "26.3.0.90" → 26.3,
        /// "26.1.1.4" → 26.1.1 (from 26 on, Minecraft's own year-based numbers). Null for one it can't read.
        /// </summary>
        public static string? NeoForgeMinecraft(string neo)
        {
            var parts = neo.Split('-')[0].Split('.');
            if (parts.Length < 3 || !parts.All(p => int.TryParse(p, out _))) { return null; }
            int a = int.Parse(parts[0]), b = int.Parse(parts[1]), c = int.Parse(parts[2]);
            return a < 26
                ? (b == 0 ? $"1.{a}" : $"1.{a}.{b}")
                : (parts.Length >= 4 && c != 0 ? $"{a}.{b}.{c}" : $"{a}.{b}");
        }

        /// <summary>The Java a Minecraft version needs, to run the NeoForge installer (the server itself is checked from its jar).</summary>
        public static int JavaFor(string minecraft)
        {
            var p = minecraft.Split('.').Select(x => int.TryParse(x, out int n) ? n : 0).ToArray();
            if (p.Length > 0 && p[0] >= 26) { return 25; }
            int minor = p.Length > 1 ? p[1] : 0, patch = p.Length > 2 ? p[2] : 0;
            return minor > 20 || (minor == 20 && patch >= 5) ? 21 : minor >= 18 ? 17 : 8;
        }

        private static async Task<List<string>> NeoForgeBuildsAsync(CancellationToken token)
        {
            var j = await GetJsonAsync("https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge", token);
            return j["versions"]!.AsArray().Select(v => (string)v!).Where(v => !v.Contains('-')).ToList(); // no betas
        }

        /// <summary>What's installed (serverfiles\wgsm-minecraft.json).</summary>
        public sealed class Installed
        {
            public string Flavor { get; set; } = "vanilla";
            public string Version { get; set; } = "";
            public string? Build { get; set; }
            /// <summary>Updates move to newer Minecraft versions too (picked "latest").</summary>
            public bool FollowLatest { get; set; }
            public DateTimeOffset InstalledAt { get; set; }
        }

        public sealed record Download(string Version, string? Build, string Url, string? Sha256, string? Sha1, string FileName);

        private static readonly HttpClient Http = CreateClient();
        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsGSM-Next/2.0 (+https://github.com/WindowsGSM/WindowsGSM)");
            return c;
        }

        /// <summary>Tests: swap the HTTP client.</summary>
        public static HttpClient? HttpOverride { get; set; }
        private static HttpClient Client => HttpOverride ?? Http;

        public static Flavor? Find(string? id) => Flavors.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

        private static string MarkerPath(string serverId) => ServerPath.GetServersServerFiles(serverId, "wgsm-minecraft.json");

        public static Installed? Read(string serverId)
        {
            return global::WindowsGSM.Hosting.SafeJson.Read<Installed>(MarkerPath(serverId));
        }

        private static void Write(string serverId, Installed info) =>
            global::WindowsGSM.Hosting.SafeJson.Write(MarkerPath(serverId), info, new JsonSerializerOptions { WriteIndented = true });

        private static async Task<JsonNode> GetJsonAsync(string url, CancellationToken token) =>
            JsonNode.Parse(await Client.GetStringAsync(url, token)) ?? throw new InvalidDataException("Empty answer from " + new Uri(url).Host);

        /// <summary>Stable Minecraft versions this software offers, newest first.</summary>
        public static async Task<List<string>> VersionsAsync(string flavor, CancellationToken token = default)
        {
            switch (flavor)
            {
                case "vanilla":
                {
                    var j = await GetJsonAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", token);
                    return j["versions"]!.AsArray().Where(v => (string?)v!["type"] == "release").Select(v => (string)v!["id"]!).ToList();
                }
                case "paper":
                {
                    var j = await GetJsonAsync("https://fill.papermc.io/v3/projects/paper", token);
                    return j["versions"]!.AsObject().SelectMany(g => g.Value!.AsArray().Select(v => (string)v!)).Where(Stable).ToList();
                }
                case "purpur":
                {
                    var j = await GetJsonAsync("https://api.purpurmc.org/v2/purpur", token);
                    return j["versions"]!.AsArray().Select(v => (string)v!).Where(Stable).Reverse().ToList();
                }
                case "neoforge":
                    return (await NeoForgeBuildsAsync(token)).Select(NeoForgeMinecraft).Where(v => v != null).Select(v => v!).Distinct().Reverse().ToList();
                case "fabric":
                {
                    var j = await GetJsonAsync("https://meta.fabricmc.net/v2/versions/game", token);
                    return j.AsArray().Where(v => (bool?)v!["stable"] == true).Select(v => (string)v!["version"]!).ToList();
                }
                default: throw new ArgumentException("Unknown server software.");
            }
        }

        private static bool Stable(string v) => !v.Contains('-') && !v.Contains("pre", StringComparison.OrdinalIgnoreCase) && !v.Contains("rc", StringComparison.OrdinalIgnoreCase);

        /// <summary>Where to download <paramref name="version"/> ("latest" = the newest) of the software.</summary>
        public static async Task<Download> ResolveAsync(string flavor, string version, CancellationToken token = default)
        {
            if (version == "latest") { version = (await VersionsAsync(flavor, token)).FirstOrDefault() ?? throw new InvalidDataException("No versions offered."); }
            switch (flavor)
            {
                case "vanilla":
                {
                    var j = await GetJsonAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", token);
                    var entry = j["versions"]!.AsArray().FirstOrDefault(v => (string?)v!["id"] == version) ?? throw new InvalidDataException($"Minecraft {version} doesn't exist.");
                    var pkg = await GetJsonAsync((string)entry["url"]!, token);
                    var server = pkg["downloads"]?["server"] ?? throw new InvalidDataException($"Minecraft {version} has no server download.");
                    return new Download(version, null, (string)server["url"]!, null, (string?)server["sha1"], "server.jar");
                }
                case "paper":
                {
                    var j = await GetJsonAsync($"https://fill.papermc.io/v3/projects/paper/versions/{Uri.EscapeDataString(version)}/builds/latest", token);
                    var d = j["downloads"]?["server:default"] ?? throw new InvalidDataException($"Paper has no build for {version}.");
                    return new Download(version, j["id"]?.ToString(), (string)d["url"]!, (string?)d["checksums"]?["sha256"], null, (string)d["name"]!);
                }
                case "purpur":
                {
                    var j = await GetJsonAsync($"https://api.purpurmc.org/v2/purpur/{Uri.EscapeDataString(version)}", token);
                    string build = (string?)j["builds"]?["latest"] ?? throw new InvalidDataException($"Purpur has no build for {version}.");
                    return new Download(version, build, $"https://api.purpurmc.org/v2/purpur/{Uri.EscapeDataString(version)}/{build}/download", null, null, $"purpur-{version}-{build}.jar");
                }
                case "neoforge":
                {
                    string build = (await NeoForgeBuildsAsync(token)).LastOrDefault(b => NeoForgeMinecraft(b) == version)
                        ?? throw new InvalidDataException($"NeoForge has no release for Minecraft {version}.");
                    string url = $"{NeoForgeMaven}/{build}/neoforge-{build}-installer.jar";
                    string sha256 = (await Client.GetStringAsync(url + ".sha256", token)).Trim();
                    return new Download(version, build, url, sha256.Length == 64 ? sha256 : null, null, $"neoforge-{build}-installer.jar");
                }
                case "fabric":
                {
                    var loaders = await GetJsonAsync("https://meta.fabricmc.net/v2/versions/loader", token);
                    var installers = await GetJsonAsync("https://meta.fabricmc.net/v2/versions/installer", token);
                    string loader = (string)loaders.AsArray().First(v => (bool?)v!["stable"] == true)!["version"]!;
                    string installer = (string)installers.AsArray().First(v => (bool?)v!["stable"] == true)!["version"]!;
                    return new Download(version, loader, $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(version)}/{loader}/{installer}/server/jar", null, null, $"fabric-server-{version}.jar");
                }
                default: throw new ArgumentException("Unknown server software.");
            }
        }

        /// <summary>
        /// Downloads the software and swaps it in as server.jar (the old one is kept as server.jar.previous).
        /// Returns what's installed now.
        /// </summary>
        public static async Task<Installed> InstallAsync(string serverId, string flavor, string version, Action<string>? log = null, CancellationToken token = default)
        {
            if (Find(flavor) == null) { throw new ArgumentException("Unknown server software."); }
            bool follow = version == "latest";
            var d = await ResolveAsync(flavor, version, token);
            log?.Invoke($"Downloading {Find(flavor)!.Name} {d.Version}{(d.Build != null ? $" (build {d.Build})" : "")}…");

            string dir = ServerPath.GetServersServerFiles(serverId);
            Directory.CreateDirectory(dir);
            string jar = Path.Combine(dir, "server.jar"), temp = jar + ".download";
            try
            {
                using (var response = await Client.GetAsync(d.Url, HttpCompletionOption.ResponseHeadersRead, token))
                {
                    response.EnsureSuccessStatusCode();
                    await using var output = File.Create(temp);
                    await response.Content.CopyToAsync(output, token);
                }
                Verify(temp, d);
            }
            catch { try { File.Delete(temp); } catch { /* locked */ } throw; }

            if (flavor == "neoforge")
            {
                // NeoForge's download is its installer: it fetches Minecraft and the libraries, and writes how to start.
                await RunNeoForgeInstallerAsync(dir, temp, d, log, token);
            }
            else
            {
                if (File.Exists(jar)) { File.Copy(jar, jar + ".previous", overwrite: true); }
                File.Move(temp, jar, overwrite: true);
            }
            AcceptEula(dir);
            var info = new Installed { Flavor = flavor, Version = d.Version, Build = d.Build, FollowLatest = follow, InstalledAt = DateTimeOffset.UtcNow };
            Write(serverId, info);
            var addons = Find(flavor)!.AddonFolder;
            if (addons != null) { Directory.CreateDirectory(Path.Combine(dir, addons)); }
            log?.Invoke($"Installed {Find(flavor)!.Name} {d.Version}{(d.Build != null ? $" build {d.Build}" : "")}{(flavor == "neoforge" ? "" : " as server.jar")}.");
            return info;
        }

        /// <summary>The file NeoForge starts from for <paramref name="build"/> (relative to the server files).</summary>
        public static string NeoForgeArgs(string build) => Path.Combine("libraries", "net", "neoforged", "neoforge", build, "win_args.txt");

        /// <summary>Minecraft's own server jar inside a NeoForge install (it says which Java it needs).</summary>
        public static string NeoForgeMinecraftJar(string minecraft) => Path.Combine("libraries", "net", "minecraft", "server", minecraft, $"server-{minecraft}.jar");

        /// <summary>Runs NeoForge's installer in the server folder with a Java that fits, then removes it.</summary>
        private static async Task RunNeoForgeInstallerAsync(string dir, string installer, Download d, Action<string>? log, CancellationToken token)
        {
            string java = await JavaRuntimes.ForMajorAsync(JavaFor(d.Version), line => log?.Invoke(line));
            log?.Invoke($"Running the NeoForge {d.Build} installer (it downloads Minecraft {d.Version} and its libraries)…");
            var psi = new System.Diagnostics.ProcessStartInfo(java)
            {
                WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-jar"); psi.ArgumentList.Add(installer); psi.ArgumentList.Add("--installServer"); psi.ArgumentList.Add(dir);
            try
            {
                using var p = System.Diagnostics.Process.Start(psi)!;
                var output = new StringBuilder();
                p.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (output) { output.AppendLine(e.Data); } } };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) { lock (output) { output.AppendLine(e.Data); } } };
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMinutes(15));
                try { await p.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw new TimeoutException("The NeoForge installer didn't finish in 15 minutes."); }
                string tail;
                lock (output) { tail = string.Join(" | ", output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(3)); }
                if (p.ExitCode != 0 || !File.Exists(Path.Combine(dir, NeoForgeArgs(d.Build!))))
                {
                    throw new InvalidOperationException($"The NeoForge installer failed (exit code {p.ExitCode}): {tail}");
                }
            }
            finally
            {
                try { File.Delete(installer); } catch { /* temp */ }
                try { File.Delete(installer + ".log"); File.Delete(Path.Combine(dir, Path.GetFileName(installer) + ".log")); } catch { /* its log */ }
            }
        }

        /// <summary>A jar (zip) of the right checksum.</summary>
        private static void Verify(string file, Download d)
        {
            var bytes = new byte[2];
            using (var f = File.OpenRead(file)) { if (f.Read(bytes, 0, 2) != 2 || bytes[0] != 'P' || bytes[1] != 'K') { throw new InvalidDataException("The download isn't a Java archive."); } }
            if (d.Sha256 != null && !Hash(file, SHA256.Create()).Equals(d.Sha256, StringComparison.OrdinalIgnoreCase)) { throw new InvalidDataException("The download's checksum doesn't match — try again."); }
            if (d.Sha1 != null && !Hash(file, SHA1.Create()).Equals(d.Sha1, StringComparison.OrdinalIgnoreCase)) { throw new InvalidDataException("The download's checksum doesn't match — try again."); }
        }

        private static string Hash(string file, HashAlgorithm algorithm)
        {
            using (algorithm)
            using (var f = File.OpenRead(file)) { return Convert.ToHexString(algorithm.ComputeHash(f)); }
        }

        /// <summary>
        /// NEXT: the EULA was accepted when the server was installed; if eula.txt has gone (deleted by hand, or a files
        /// restore without it), it's written again so the server doesn't stop at "You need to agree to the EULA".
        /// True when it had to be written.
        /// </summary>
        internal static bool AcceptEula(string dir)
        {
            string eula = Path.Combine(dir, "eula.txt");
            if (File.Exists(eula) && File.ReadAllText(eula).Contains("eula=true")) { return false; }
            File.WriteAllLines(eula, new[]
            {
                "#By changing the setting below to TRUE you are indicating your agreement to our EULA (https://aka.ms/MinecraftEULA).",
                "#Generated by WindowsGSM",
                "eula=true",
            });
            return true;
        }

        /// <summary>For update checks: "paper 1.21.4 #232".</summary>
        public static string Label(Installed i) => $"{i.Flavor} {i.Version}{(i.Build != null ? " #" + i.Build : "")}";

        /// <summary>What an update would install now (same Minecraft version unless it follows the latest).</summary>
        public static async Task<string> RemoteLabelAsync(Installed i, CancellationToken token = default)
        {
            var d = await ResolveAsync(i.Flavor, i.FollowLatest ? "latest" : i.Version, token);
            return Label(new Installed { Flavor = i.Flavor, Version = d.Version, Build = d.Build });
        }

        /// <summary>Updates in place: the newest build of the chosen software (keeps the Minecraft version unless it follows "latest").</summary>
        public static Task<Installed> UpdateAsync(string serverId, Installed i, Action<string>? log = null, CancellationToken token = default) =>
            InstallAsync(serverId, i.Flavor, i.FollowLatest ? "latest" : i.Version, log, token);
    }
}
