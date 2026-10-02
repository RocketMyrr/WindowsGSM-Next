#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
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
        };

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
            try { return File.Exists(MarkerPath(serverId)) ? JsonSerializer.Deserialize<Installed>(File.ReadAllText(MarkerPath(serverId))) : null; }
            catch { return null; }
        }

        private static void Write(string serverId, Installed info) =>
            File.WriteAllText(MarkerPath(serverId), JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));

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

            if (File.Exists(jar)) { File.Copy(jar, jar + ".previous", overwrite: true); }
            File.Move(temp, jar, overwrite: true);
            AcceptEula(dir);
            var info = new Installed { Flavor = flavor, Version = d.Version, Build = d.Build, FollowLatest = follow, InstalledAt = DateTimeOffset.UtcNow };
            Write(serverId, info);
            var addons = Find(flavor)!.AddonFolder;
            if (addons != null) { Directory.CreateDirectory(Path.Combine(dir, addons)); }
            log?.Invoke($"Installed {Find(flavor)!.Name} {d.Version}{(d.Build != null ? $" build {d.Build}" : "")} as server.jar.");
            return info;
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

        private static void AcceptEula(string dir)
        {
            string eula = Path.Combine(dir, "eula.txt");
            if (File.Exists(eula) && File.ReadAllText(eula).Contains("eula=true")) { return; }
            File.WriteAllLines(eula, new[]
            {
                "#By changing the setting below to TRUE you are indicating your agreement to our EULA (https://aka.ms/MinecraftEULA).",
                "#Generated by WindowsGSM",
                "eula=true",
            });
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
