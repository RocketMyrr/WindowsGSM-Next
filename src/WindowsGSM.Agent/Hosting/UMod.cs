using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Engine.Services;
using WindowsGSM.Functions;

namespace WindowsGSM.Agent.Hosting;

/// <summary>
/// Rust plugins from uMod (umod.org) for a server running Oxide or Carbon: search, install (with the plugins it
/// says it requires), update all, remove. Oxide and Carbon load, reload and unload plugin files on their own, so none
/// of this needs the server stopped. What WindowsGSM installed — or was asked to keep up to date — is tracked in
/// serverfiles\wgsm-umod.json; plugins added by hand are shown but never touched, and a tracked plugin someone has
/// edited since is left alone by updates (their changes would be lost).
/// </summary>
public sealed class UMod
{
    public sealed class Tracked
    {
        public string Name { get; set; } = "";     // uMod's name = the file name without .cs
        public string Title { get; set; } = "";
        public string Author { get; set; } = "";
        public string Version { get; set; } = "";
        public string Checksum { get; set; } = ""; // SHA-1 of the file as installed (to notice later edits)
        public string? Url { get; set; }
        public string? IconUrl { get; set; }
        public bool Dependency { get; set; }
        public DateTimeOffset InstalledAt { get; set; }
    }

    /// <summary>A plugin on uMod.</summary>
    public sealed record Hit(string Name, string Title, string Description, string Author, string Version, string? IconUrl, string? Url, long Downloads, bool Installed);

    /// <summary>A plugin file in the folder, tracked or not, with what its [Info] attribute says.</summary>
    public sealed record Present(string Name, string? Title, string? Author, string? Version, bool Tracked, bool Edited);

    /// <summary>Oxide or Carbon, and where it loads plugins from.</summary>
    public sealed record Context(string Framework, string Folder);

    private const string Site = "https://umod.org";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Regex NameRule = new("^[A-Za-z0-9_]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex InfoAttribute = new(@"\[Info\(\s*""(?<title>[^""]*)""\s*,\s*""(?<author>[^""]*)""\s*,\s*""(?<version>[^""]*)""", RegexOptions.Compiled);
    private static readonly Regex Requires = new(@"^\s*//\s*Requires:\s*(?<name>[A-Za-z0-9_]+)", RegexOptions.Compiled | RegexOptions.Multiline);
    private readonly HttpClient _http;

    public UMod(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any()) { _http.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsGSM-Next/2.0 (+https://github.com/WindowsGSM/WindowsGSM)"); }
    }

    public static bool IsRust(ServerInstance s) => string.Equals(s.Game, GameServer.RUST.FullName, StringComparison.Ordinal);

    /// <summary>Oxide or Carbon on this server (Oxide if both), or null: plugins need one of them (Add-ons tab).</summary>
    public static Context? ContextFor(string serverId)
    {
        if (File.Exists(ServerPath.GetServersServerFiles(serverId, "RustDedicated_Data", "Managed", "Oxide.Core.dll"))) { return new Context("Oxide", Path.Combine("oxide", "plugins")); }
        if (File.Exists(ServerPath.GetServersServerFiles(serverId, "carbon", "managed", "Carbon.dll"))) { return new Context("Carbon", Path.Combine("carbon", "plugins")); }
        return null;
    }

    public static bool IsValidName(string? name) => name != null && NameRule.IsMatch(name);

    private static string TrackFile(string serverId) => ServerPath.GetServersServerFiles(serverId, "wgsm-umod.json");
    private static string PluginFile(string serverId, Context c, string name) => Path.Combine(ServerPath.GetServersServerFiles(serverId, c.Folder), name + ".cs");

    public List<Tracked> List(string serverId)
    {
        return global::WindowsGSM.Hosting.SafeJson.Read<List<Tracked>>(TrackFile(serverId), Json) ?? new();
    }

    private static void Save(string serverId, List<Tracked> list) => global::WindowsGSM.Hosting.SafeJson.Write(TrackFile(serverId), list, Json);

    /// <summary>Every plugin file in the folder, with its [Info] details and whether WindowsGSM keeps it up to date.</summary>
    public List<Present> Plugins(string serverId, Context c)
    {
        string dir = ServerPath.GetServersServerFiles(serverId, c.Folder);
        if (!Directory.Exists(dir)) { return new(); }
        var tracked = List(serverId).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var list = new List<Present>();
        foreach (string file in Directory.EnumerateFiles(dir, "*.cs"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }
            var info = InfoAttribute.Match(text);
            bool isTracked = tracked.TryGetValue(name, out var t);
            list.Add(new Present(name, info.Success ? info.Groups["title"].Value : null, info.Success ? info.Groups["author"].Value : null,
                info.Success ? info.Groups["version"].Value : null, isTracked, isTracked && !Sha1(File.ReadAllBytes(file)).Equals(t!.Checksum, StringComparison.OrdinalIgnoreCase)));
        }
        return list.OrderBy(p => p.Title ?? p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Rust plugins on uMod: by relevance for a search, the most downloaded for none.</summary>
    public async Task<List<Hit>> SearchAsync(string serverId, string query, CancellationToken token)
    {
        string sort = string.IsNullOrWhiteSpace(query) ? "&sort=downloads&sortdir=desc" : "";
        string url = $"{Site}/plugins/search.json?query={Uri.EscapeDataString(query ?? "")}&page=1{sort}&categories%5B%5D=rust";
        var data = JsonNode.Parse(await _http.GetStringAsync(url, token))!["data"]!.AsArray();
        var installed = List(serverId).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var c = ContextFor(serverId);
        return data.Where(x => (string?)x!["distribution"] == "download" && IsValidName((string?)x["name"]))
            .Select(x => new Hit((string)x!["name"]!, (string?)x["title"] ?? (string)x["name"]!, (string?)x["description"] ?? "", (string?)x["author"] ?? "",
                (string?)x["latest_release_version"] ?? "", (string?)x["icon_url"], (string?)x["url"], (long?)x["downloads"] ?? 0,
                installed.Contains((string)x["name"]!) || (c != null && File.Exists(PluginFile(serverId, c, (string)x["name"]!)))))
            .ToList();
    }

    /// <summary>The plugin called exactly <paramref name="name"/> on uMod (for Rust, downloadable), or null.</summary>
    private async Task<JsonNode?> FindAsync(string name, CancellationToken token)
    {
        string url = $"{Site}/plugins/search.json?query={Uri.EscapeDataString(name)}&page=1&categories%5B%5D=rust";
        var data = JsonNode.Parse(await _http.GetStringAsync(url, token))!["data"]!.AsArray();
        return data.FirstOrDefault(x => string.Equals((string?)x!["name"], name, StringComparison.Ordinal) && (string?)x["distribution"] == "download");
    }

    /// <summary>Installs a plugin and the plugins it requires ("// Requires: Name" in its source). Returns what was installed.</summary>
    public async Task<List<Tracked>> InstallAsync(string serverId, Context c, string name, CancellationToken token, bool dependency = false, HashSet<string>? seen = null)
    {
        seen ??= new(StringComparer.OrdinalIgnoreCase);
        var done = new List<Tracked>();
        if (!IsValidName(name)) { throw new InvalidOperationException("That isn't a uMod plugin name."); }
        if (!seen.Add(name)) { return done; }
        if (List(serverId).Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { return done; }
        // A dependency that's already there (added by hand) is used as it is.
        if (dependency && File.Exists(PluginFile(serverId, c, name))) { return done; }
        if (!dependency && File.Exists(PluginFile(serverId, c, name)))
        {
            throw new InvalidOperationException($"{name}.cs is already in {c.Folder} (added by hand). Use \"Keep up to date\" on it instead.");
        }

        var plugin = await FindAsync(name, token) ?? throw new InvalidOperationException($"There's no Rust plugin called {name} on uMod.");
        var (t, source) = await DownloadAsync(serverId, c, plugin, token);
        t.Dependency = dependency;
        var list = List(serverId);
        list.Add(t);
        Save(serverId, list);
        done.Add(t);

        foreach (Match m in Requires.Matches(source))
        {
            try { done.AddRange(await InstallAsync(serverId, c, m.Groups["name"].Value, token, dependency: true, seen)); }
            catch (InvalidOperationException) { /* not on uMod (or not downloadable): Oxide will say so when it loads */ }
        }
        return done;
    }

    /// <summary>
    /// Starts keeping a plugin that was added by hand up to date — if it's the uMod plugin of that name. Its current
    /// file counts as "as installed"; the next "Update all" brings it to uMod's newest version.
    /// </summary>
    public async Task<Tracked> TrackAsync(string serverId, Context c, string name, CancellationToken token)
    {
        if (!IsValidName(name)) { throw new InvalidOperationException("That isn't a plugin name."); }
        string file = PluginFile(serverId, c, name);
        if (!File.Exists(file)) { throw new InvalidOperationException($"{name}.cs isn't in {c.Folder}."); }
        var list = List(serverId);
        if (list.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { throw new InvalidOperationException($"{name} is already kept up to date."); }
        var plugin = await FindAsync(name, token) ?? throw new InvalidOperationException($"There's no Rust plugin called {name} on uMod — it may be private or from somewhere else.");
        var info = InfoAttribute.Match(File.ReadAllText(file));
        var t = Describe(plugin, info.Success ? info.Groups["version"].Value : "", Sha1(File.ReadAllBytes(file)));
        list.Add(t);
        Save(serverId, list);
        return t;
    }

    /// <summary>
    /// Brings every tracked plugin to uMod's newest version. A plugin edited since WindowsGSM installed it is skipped
    /// (updating would throw the edits away). Returns "title: old → new" per change, and what was skipped and why.
    /// </summary>
    public async Task<(List<string> Changes, List<string> Skipped)> UpdateAllAsync(string serverId, Context c, CancellationToken token)
    {
        var changes = new List<string>();
        var skipped = new List<string>();
        var list = List(serverId);
        foreach (var t in list.ToList())
        {
            string file = PluginFile(serverId, c, t.Name);
            if (!File.Exists(file)) { list.Remove(t); Save(serverId, list); continue; } // removed by hand
            if (!Sha1(File.ReadAllBytes(file)).Equals(t.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add($"{t.Title}: changed by hand since it was installed — not updated (remove and install it again to take uMod's)");
                continue;
            }
            JsonNode? plugin;
            try { plugin = await FindAsync(t.Name, token); } catch (HttpRequestException) { skipped.Add($"{t.Title}: couldn't reach uMod"); continue; }
            if (plugin == null) { skipped.Add($"{t.Title}: no longer on uMod"); continue; }
            if (string.Equals((string?)plugin["latest_release_version_checksum"], t.Checksum, StringComparison.OrdinalIgnoreCase)) { continue; } // up to date
            var (fresh, _) = await DownloadAsync(serverId, c, plugin, token);
            changes.Add($"{t.Title}: {t.Version} → {fresh.Version}");
            fresh.Dependency = t.Dependency;
            list[list.IndexOf(t)] = fresh;
            Save(serverId, list);
        }
        return (changes, skipped);
    }

    /// <summary>How many tracked plugins have a newer version on uMod (for the tab's badge).</summary>
    public async Task<List<string>> UpdatesAvailableAsync(string serverId, CancellationToken token)
    {
        var names = new List<string>();
        foreach (var t in List(serverId))
        {
            try
            {
                var plugin = await FindAsync(t.Name, token);
                if (plugin != null && !string.Equals((string?)plugin["latest_release_version_checksum"], t.Checksum, StringComparison.OrdinalIgnoreCase)) { names.Add(t.Name); }
            }
            catch (HttpRequestException) { /* checked again next time */ }
        }
        return names;
    }

    /// <summary>Removes a plugin WindowsGSM installed (its config and data stay). Oxide unloads it on its own.</summary>
    public bool Remove(string serverId, Context c, string name)
    {
        var list = List(serverId);
        var t = list.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (t == null) { return false; }
        try { File.Delete(PluginFile(serverId, c, t.Name)); } catch { /* in use: left for the user */ }
        list.Remove(t);
        Save(serverId, list);
        return true;
    }

    /// <summary>Stops keeping a plugin up to date (the file stays).</summary>
    public bool Untrack(string serverId, string name)
    {
        var list = List(serverId);
        int removed = list.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (removed > 0) { Save(serverId, list); }
        return removed > 0;
    }

    private async Task<(Tracked, string Source)> DownloadAsync(string serverId, Context c, JsonNode plugin, CancellationToken token)
    {
        string name = (string)plugin["name"]!;
        if (!IsValidName(name)) { throw new InvalidOperationException("Unexpected plugin name."); }
        string url = (string?)plugin["download_url"] ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !(uri.Host.Equals("umod.org", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".umod.org", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Unexpected download address.");
        }
        byte[] data = await _http.GetByteArrayAsync(uri, token);
        string checksum = Sha1(data);
        string? expected = (string?)plugin["latest_release_version_checksum"];
        if (expected != null && !checksum.Equals(expected, StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException("The download's checksum doesn't match — try again."); }

        string dir = ServerPath.GetServersServerFiles(serverId, c.Folder);
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, name + ".cs");
        string temp = target + ".download";
        await File.WriteAllBytesAsync(temp, data, token);
        File.Move(temp, target, overwrite: true); // Oxide/Carbon notice the change and (re)load it
        string source = System.Text.Encoding.UTF8.GetString(data);
        var info = InfoAttribute.Match(source);
        return (Describe(plugin, info.Success ? info.Groups["version"].Value : (string?)plugin["latest_release_version"] ?? "", checksum), source);
    }

    private static Tracked Describe(JsonNode plugin, string version, string checksum) => new()
    {
        Name = (string)plugin["name"]!,
        Title = (string?)plugin["title"] ?? (string)plugin["name"]!,
        Author = (string?)plugin["author"] ?? "",
        Version = version,
        Checksum = checksum,
        Url = (string?)plugin["url"],
        IconUrl = (string?)plugin["icon_url"],
        InstalledAt = DateTimeOffset.UtcNow,
    };

    private static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();
}

/// <summary>"Update uMod plugins before every start" (Rust tab): brings the tracked plugins up to date first.</summary>
public sealed class UModBeforeStart : IPreStartStep
{
    public const string SettingKey = "umodupdateonstart";
    private readonly UMod _umod;
    public UModBeforeStart(UMod umod) => _umod = umod;
    public string Name => "Update uMod plugins";

    public async Task RunAsync(ServerInstance server, StartReason reason, JobContext job)
    {
        if (!UMod.IsRust(server) || server.Config.GetCustomSetting(SettingKey, "") != "1") { return; }
        var c = UMod.ContextFor(server.Id);
        if (c == null || _umod.List(server.Id).Count == 0) { return; }
        try
        {
            job.Report(stage: "Updating uMod plugins");
            var (changes, skipped) = await _umod.UpdateAllAsync(server.Id, c, job.Cancellation);
            if (changes.Count > 0) { job.Log("uMod: " + string.Join("; ", changes)); }
            foreach (string s in skipped) { job.Log("uMod: " + s); }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            job.Log("Couldn't update uMod plugins (" + ex.Message + ") — starting anyway.");
        }
    }
}
