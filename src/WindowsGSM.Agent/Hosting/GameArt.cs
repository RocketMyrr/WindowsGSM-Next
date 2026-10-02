using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindowsGSM.Agent.Hosting;

/// <summary>
/// Real game artwork for the UI. The built-in icons are 16×16 favicons, so the agent fetches each game's
/// Steam store art once — the portrait cover (for tiles) and the wide header (for banners) — and keeps it in
/// the data folder. Browsers only ever load it from the agent itself, so the page's strict content security
/// policy stays intact.
///
/// A dedicated server's Steam app isn't the game's store app (Rust's server is 258550, the game 252490), so
/// built-in games use a checked list, and plugin games are looked up by name in Steam's store search —
/// accepted only on a clear name match, so a game never shows someone else's art.
/// </summary>
public sealed class GameArt
{
    /// <summary>Built-in game → the store app id whose art to show. Null = not on Steam (keeps its initials tile).</summary>
    public static readonly IReadOnlyDictionary<string, int?> Curated = new Dictionary<string, int?>(StringComparer.Ordinal)
    {
        ["7 Days to Die Dedicated Server"] = 251570,
        ["ARK: Survival Evolved Dedicated Server"] = 346110,
        ["Avorion Dedicated Server"] = 445220,
        ["Barotrauma Dedicated Server"] = 602960,
        ["BlackWake Dedicated Server"] = 420290,
        ["Conan Exiles Dedicated Server"] = 440900,
        ["Counter-Strike: 1.6 Dedicated Server"] = 10,
        ["Counter-Strike: Condition Zero Dedicated Server"] = 80,
        ["Counter-Strike: Global Offensive Dedicated Server"] = 730,
        ["Counter-Strike: Source Dedicated Server"] = 240,
        ["Day of Defeat Dedicated Server"] = 30,
        ["Day of Defeat: Source Dedicated Server"] = 300,
        ["DayZ Dedicated Server"] = 221100,
        ["Deathmatch Classic Dedicated Server"] = 40,
        ["Eco Dedicated Server"] = 382310,
        ["Empyrion - Galactic Survival Dedicated Server"] = 383120,
        ["Garry's Mod Dedicated Server"] = 4000,
        ["Grand Theft Auto V Dedicated Server (FiveM)"] = 271590,
        ["Half-Life 2: Deathmatch Dedicated Server"] = 320,
        ["Half-Life: Opposing Force Dedicated Server"] = 50,
        ["Heat Dedicated Server"] = null,
        ["Insurgency Dedicated Server"] = 222880,
        ["Insurgency: Sandstorm Dedicated Server"] = 581320,
        ["Left 4 Dead 2 Dedicated Server"] = 550,
        ["Minecraft: Bedrock Edition Server"] = null,
        ["Minecraft: Java Edition Server"] = null,
        ["Minecraft: Pocket Edition Server (PocketMine-MP)"] = null,
        ["Mordhau Dedicated Server"] = 629760,
        ["No More Room in Hell Dedicated Server"] = 224260,
        ["Onset Dedicated Server"] = 1105810,
        ["Outlaws of the Old West Dedicated Server"] = 840800,
        ["Post Scriptum Dedicated Server"] = 736220,
        ["Reign Of Kings Dedicated Server"] = 344760,
        ["Ricochet Dedicated Server"] = 60,
        ["Risk of Rain 2 Dedicated Server"] = 632360,
        ["Rust Dedicated Server"] = 252490,
        ["Source SDK Base 2013 Dedicated Server"] = null,
        ["Space Engineers Dedicated Server"] = 244850,
        ["Squad Dedicated Server"] = 393380,
        ["Stormworks Dedicated Server"] = 573090,
        ["Team Fortress 2 Dedicated Server"] = 440,
        ["Team Fortress Classic Dedicated Server"] = 20,
        ["The Forest Dedicated Server"] = 242760,
        ["Unturned Dedicated Server"] = 304930,
        ["Vintage Story Dedicated Server"] = null,
        ["Zombie Panic Source Dedicated Server"] = 17500,
    };

    private const long MaxImageBytes = 3 * 1024 * 1024;
    private static readonly TimeSpan RetryMissingAfter = TimeSpan.FromDays(7);

    private readonly string _dir;
    private readonly HttpClient _http;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inflight = new();
    private readonly object _mapGate = new();
    private Dictionary<string, MapEntry> _map;

    private sealed class MapEntry
    {
        public int? AppId { get; set; }
        public DateTimeOffset CheckedAt { get; set; }
    }

    public GameArt(string dataRoot, Action<string> log, HttpClient? http = null)
    {
        _dir = Path.Combine(dataRoot, "cache", "art");
        Directory.CreateDirectory(_dir);
        _log = log;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        if (http == null) { _http.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsGSM-Agent/2.0"); }
        _map = LoadMap();
    }

    /// <summary>Whether a game can have art at all (so the UI doesn't ask for art that can't exist).</summary>
    public static bool MayHaveArt(string game) => !Curated.TryGetValue(game, out var id) || id != null;

    /// <summary>The cached image file for a game ("cover" or "header"), fetching it the first time. Null = no art.</summary>
    public Task<string?> GetAsync(string game, string kind)
    {
        kind = kind == "header" ? "header" : "cover";
        return _inflight.GetOrAdd($"{game}|{kind}", _ => new Lazy<Task<string?>>(() => FetchAsync(game, kind))).Value
            .ContinueWith(t => { _inflight.TryRemove($"{game}|{kind}", out _); return t.IsCompletedSuccessfully ? t.Result : null; }, TaskScheduler.Default);
    }

    private async Task<string?> FetchAsync(string game, string kind)
    {
        int? appId = await ResolveAppIdAsync(game);
        if (appId == null) { return null; }

        string file = Path.Combine(_dir, $"{appId}-{kind}.jpg");
        if (File.Exists(file)) { return file; }
        string missing = file + ".none";
        if (File.Exists(missing) && DateTime.UtcNow - File.GetLastWriteTimeUtc(missing) < RetryMissingAfter) { return null; }

        string[] names = kind == "cover" ? new[] { "library_600x900.jpg", "header.jpg" } : new[] { "header.jpg" };
        foreach (string name in names)
        {
            foreach (string baseUrl in new[] { "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps", "https://cdn.cloudflare.steamstatic.com/steam/apps" })
            {
                byte[]? bytes = await TryDownloadImageAsync($"{baseUrl}/{appId}/{name}");
                if (bytes == null) { continue; }
                string temp = file + ".tmp";
                await File.WriteAllBytesAsync(temp, bytes);
                File.Move(temp, file, overwrite: true);
                try { File.Delete(missing); } catch { /* none */ }
                return file;
            }
        }
        await File.WriteAllTextAsync(missing, DateTimeOffset.UtcNow.ToString("o"));
        return null;
    }

    private async Task<byte[]?> TryDownloadImageAsync(string url)
    {
        try
        {
            using var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!res.IsSuccessStatusCode) { return null; }
            if (res.Content.Headers.ContentLength > MaxImageBytes) { return null; }
            if (res.Content.Headers.ContentType?.MediaType?.StartsWith("image/") != true) { return null; }
            byte[] bytes = await res.Content.ReadAsByteArrayAsync();
            bool jpeg = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8;
            bool png = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
            return (jpeg || png) && bytes.Length <= MaxImageBytes ? bytes : null; // only real images are ever served
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
    }

    // ── Which store app ──

    private async Task<int?> ResolveAppIdAsync(string game)
    {
        if (Curated.TryGetValue(game, out var curated)) { return curated; }
        lock (_mapGate)
        {
            if (_map.TryGetValue(game, out var known) && (known.AppId != null || DateTimeOffset.UtcNow - known.CheckedAt < RetryMissingAfter)) { return known.AppId; }
        }
        int? found = await SearchStoreAsync(CleanName(game));
        lock (_mapGate)
        {
            _map[game] = new MapEntry { AppId = found, CheckedAt = DateTimeOffset.UtcNow };
            SaveMap();
        }
        if (found != null) { _log($"Artwork for {game}: Steam app {found}."); }
        return found;
    }

    /// <summary>"Valheim Dedicated Server [Valheim.cs]" → "Valheim".</summary>
    public static string CleanName(string game)
    {
        string name = Regex.Replace(game, @"\s*\[[^\]]*\]\s*$", "");
        name = Regex.Replace(name, @"\s*\((FiveM|PocketMine-MP)\)\s*$", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"\s+(Dedicated\s+)?Server$", "", RegexOptions.IgnoreCase);
        return name.Trim();
    }

    private static string Normalise(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");

    private async Task<int?> SearchStoreAsync(string term)
    {
        if (term.Length < 2) { return null; }
        try
        {
            string url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term)}&l=english&cc=US";
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            if (!doc.RootElement.TryGetProperty("items", out var items)) { return null; }
            string want = Normalise(term);
            foreach (var item in items.EnumerateArray())
            {
                string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string got = Normalise(name);
                // A clear match only: the same name, or the game's name plus a subtitle ("Valheim" / "Valheim: …").
                if (got == want || (got.StartsWith(want) && name.Length > term.Length && ":-–— ".Contains(name[term.Length])))
                {
                    return item.GetProperty("id").GetInt32();
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { }
        return null;
    }

    private Dictionary<string, MapEntry> LoadMap()
    {
        try
        {
            string file = Path.Combine(_dir, "art.json");
            if (File.Exists(file)) { return JsonSerializer.Deserialize<Dictionary<string, MapEntry>>(File.ReadAllText(file)) ?? new(); }
        }
        catch { /* rebuild */ }
        return new();
    }

    private void SaveMap()
    {
        try
        {
            string file = Path.Combine(_dir, "art.json");
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(_map, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch { /* the map is only a cache */ }
    }
}
