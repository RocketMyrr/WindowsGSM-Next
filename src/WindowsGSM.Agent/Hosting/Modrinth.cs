using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsGSM.Functions;
using WindowsGSM.Installer;

namespace WindowsGSM.Agent.Hosting;

/// <summary>
/// Plugins (Paper, Purpur) and mods (Fabric) for a Minecraft server from Modrinth: search, install with required
/// dependencies, update to the newest version for the server's Minecraft version, remove. What WindowsGSM
/// installed is tracked in serverfiles\wgsm-modrinth.json, so updates never touch jars you added yourself.
/// </summary>
public sealed class Modrinth
{
    public sealed class Tracked
    {
        public string ProjectId { get; set; } = "";
        public string Slug { get; set; } = "";
        public string Title { get; set; } = "";
        public string VersionId { get; set; } = "";
        public string VersionNumber { get; set; } = "";
        public string File { get; set; } = "";
        public string? IconUrl { get; set; }
        public bool Dependency { get; set; }
        public DateTimeOffset InstalledAt { get; set; }
    }

    public sealed record Hit(string ProjectId, string Slug, string Title, string Description, string? IconUrl, long Downloads, bool Installed);
    public sealed record Context(string Flavor, string Version, string Folder, string[] Loaders, string ProjectType);

    private const string Api = "https://api.modrinth.com/v2";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly HttpClient _http;

    public Modrinth(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any()) { _http.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsGSM-Next/2.0 (+https://github.com/WindowsGSM/WindowsGSM)"); }
    }

    /// <summary>The server's software decides the folder and which loaders fit; null for Vanilla (nothing to add).</summary>
    public static Context? ContextFor(string serverId)
    {
        var i = MinecraftSoftware.Read(serverId);
        if (i == null) { return null; }
        return i.Flavor switch
        {
            "paper" => new Context("paper", i.Version, "plugins", new[] { "paper", "spigot", "bukkit" }, "plugin"),
            "purpur" => new Context("purpur", i.Version, "plugins", new[] { "purpur", "paper", "spigot", "bukkit" }, "plugin"),
            "fabric" => new Context("fabric", i.Version, "mods", new[] { "fabric" }, "mod"),
            _ => null,
        };
    }

    private static string TrackFile(string serverId) => ServerPath.GetServersServerFiles(serverId, "wgsm-modrinth.json");

    public List<Tracked> List(string serverId)
    {
        try { return File.Exists(TrackFile(serverId)) ? JsonSerializer.Deserialize<List<Tracked>>(File.ReadAllText(TrackFile(serverId)), Json) ?? new() : new(); }
        catch { return new(); }
    }

    private static void Save(string serverId, List<Tracked> list) => File.WriteAllText(TrackFile(serverId), JsonSerializer.Serialize(list, Json));

    /// <summary>Jars in the folder that WindowsGSM didn't install (shown, never touched).</summary>
    public List<string> Others(string serverId, Context c)
    {
        string dir = ServerPath.GetServersServerFiles(serverId, c.Folder);
        if (!Directory.Exists(dir)) { return new(); }
        var mine = List(serverId).Select(t => t.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateFiles(dir, "*.jar").Select(Path.GetFileName).Where(f => f != null && !mine.Contains(f!)).Select(f => f!).OrderBy(f => f).ToList();
    }

    public async Task<List<Hit>> SearchAsync(string serverId, Context c, string query, CancellationToken token)
    {
        // Any loader that runs here (Paper runs Spigot and Bukkit plugins too), for this Minecraft version.
        var facets = new List<string[]> { c.Loaders.Select(l => $"categories:{l}").ToArray(), new[] { $"versions:{c.Version}" } };
        if (c.ProjectType == "mod") { facets.Add(new[] { "server_side:required", "server_side:optional" }); }
        string url = $"{Api}/search?limit=20&index=relevance&query={Uri.EscapeDataString(query ?? "")}&facets={Uri.EscapeDataString(JsonSerializer.Serialize(facets))}";
        var j = JsonNode.Parse(await _http.GetStringAsync(url, token))!;
        var installed = List(serverId).Select(t => t.ProjectId).ToHashSet();
        return j["hits"]!.AsArray().Select(x => new Hit((string)x!["project_id"]!, (string)x["slug"]!, (string)x["title"]!, (string?)x["description"] ?? "",
            (string?)x["icon_url"], (long?)x["downloads"] ?? 0, installed.Contains((string)x["project_id"]!))).ToList();
    }

    /// <summary>The newest version of a project for this server's Minecraft version and loader, or null.</summary>
    private async Task<JsonNode?> BestVersionAsync(string projectId, Context c, CancellationToken token)
    {
        string url = $"{Api}/project/{Uri.EscapeDataString(projectId)}/version?loaders={Uri.EscapeDataString(JsonSerializer.Serialize(c.Loaders))}&game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { c.Version }))}";
        var list = JsonNode.Parse(await _http.GetStringAsync(url, token))!.AsArray();
        return list.FirstOrDefault(v => (string?)v!["version_type"] == "release") ?? list.FirstOrDefault();
    }

    /// <summary>Installs a project (and its required dependencies). Returns what was installed.</summary>
    public async Task<List<Tracked>> InstallAsync(string serverId, Context c, string projectId, CancellationToken token, bool dependency = false, HashSet<string>? seen = null)
    {
        seen ??= new();
        var done = new List<Tracked>();
        if (!seen.Add(projectId)) { return done; }
        var list = List(serverId);
        if (list.Any(t => t.ProjectId == projectId)) { return done; }

        var version = await BestVersionAsync(projectId, c, token) ?? throw new InvalidOperationException($"No version of that {(c.ProjectType == "plugin" ? "plugin" : "mod")} works with {c.Flavor} {c.Version}.");
        var project = JsonNode.Parse(await _http.GetStringAsync($"{Api}/project/{Uri.EscapeDataString(projectId)}", token))!;
        var t = await DownloadAsync(serverId, c, version, token);
        t.ProjectId = (string)project["id"]!;
        t.Slug = (string)project["slug"]!;
        t.Title = (string)project["title"]!;
        t.IconUrl = (string?)project["icon_url"];
        t.Dependency = dependency;
        list = List(serverId);
        list.Add(t);
        Save(serverId, list);
        done.Add(t);

        foreach (var dep in version["dependencies"]?.AsArray() ?? new JsonArray())
        {
            if ((string?)dep!["dependency_type"] != "required" || (string?)dep["project_id"] is not { } depId) { continue; }
            try { done.AddRange(await InstallAsync(serverId, c, depId, token, dependency: true, seen)); }
            catch (InvalidOperationException) { /* a dependency without a fitting version: the plugin may still load */ }
        }
        return done;
    }

    private async Task<Tracked> DownloadAsync(string serverId, Context c, JsonNode version, CancellationToken token)
    {
        var files = version["files"]!.AsArray();
        var file = files.FirstOrDefault(f => (bool?)f!["primary"] == true) ?? files.FirstOrDefault() ?? throw new InvalidOperationException("That version has no file.");
        string name = Path.GetFileName((string)file["filename"]!);
        if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { throw new InvalidOperationException("That download isn't a .jar."); }
        string url = (string)file["url"]!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !(uri.Host.Equals("modrinth.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".modrinth.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Unexpected download address.");
        }

        string dir = ServerPath.GetServersServerFiles(serverId, c.Folder);
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, name + ".download");
        byte[] data = await _http.GetByteArrayAsync(uri, token);
        string? sha512 = (string?)file["hashes"]?["sha512"];
        if (sha512 != null && !Convert.ToHexString(SHA512.HashData(data)).Equals(sha512, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The download's checksum doesn't match — try again.");
        }
        await File.WriteAllBytesAsync(temp, data, token);
        File.Move(temp, Path.Combine(dir, name), overwrite: true);
        return new Tracked { VersionId = (string)version["id"]!, VersionNumber = (string?)version["version_number"] ?? "", File = name, InstalledAt = DateTimeOffset.UtcNow };
    }

    /// <summary>Moves every tracked project to its newest fitting version. Returns "title: old → new" for each change.</summary>
    public async Task<List<string>> UpdateAllAsync(string serverId, Context c, CancellationToken token)
    {
        var changes = new List<string>();
        var list = List(serverId);
        foreach (var t in list.ToList())
        {
            JsonNode? best;
            try { best = await BestVersionAsync(t.ProjectId, c, token); } catch (HttpRequestException) { continue; }
            if (best == null || (string)best["id"]! == t.VersionId) { continue; }
            var fresh = await DownloadAsync(serverId, c, best, token);
            if (!fresh.File.Equals(t.File, StringComparison.OrdinalIgnoreCase)) { TryDelete(Path.Combine(ServerPath.GetServersServerFiles(serverId, c.Folder), t.File)); }
            changes.Add($"{t.Title}: {t.VersionNumber} → {fresh.VersionNumber}");
            t.VersionId = fresh.VersionId; t.VersionNumber = fresh.VersionNumber; t.File = fresh.File; t.InstalledAt = fresh.InstalledAt;
            Save(serverId, list);
        }
        return changes;
    }

    public bool Remove(string serverId, Context c, string projectId)
    {
        var list = List(serverId);
        var t = list.FirstOrDefault(x => x.ProjectId == projectId);
        if (t == null) { return false; }
        TryDelete(Path.Combine(ServerPath.GetServersServerFiles(serverId, c.Folder), t.File));
        list.Remove(t);
        Save(serverId, list);
        return true;
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { /* in use: left for the user */ } }
}
