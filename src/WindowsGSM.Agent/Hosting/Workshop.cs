using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsGSM.Agent.Api;
using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Engine.Services;
using WindowsGSM.Functions;

namespace WindowsGSM.Agent.Hosting;

/// <summary>One Workshop item on a server.</summary>
/// <param name="Updated">Steam's last-updated time (unix seconds); <see cref="Installed"/> is the one on disk.</param>
/// <param name="Folder">Where it went, inside serverfiles.</param>
/// <param name="Keys">Arma/DayZ: the .bikey files it put in keys\ (removed with it).</param>
public sealed record WorkshopItem(string Id, string Title, long Size, long Updated, long Installed = 0, string? Folder = null, List<string>? Keys = null);

/// <summary>A server's Workshop setup (servers/&lt;id&gt;/configs/workshop.json).</summary>
public sealed class WorkshopSettings
{
    /// <summary>The GAME's Steam app id — Workshop items belong to it, not to the dedicated server's app.</summary>
    public string AppId { get; set; } = "";
    /// <summary>folder (each mod in its own folder) · arma (Arma 3 / DayZ: @Mod folders, keys, -mod=) · conan (modlist.txt).</summary>
    public string Style { get; set; } = "folder";
    /// <summary>For "folder": where inside serverfiles (default "workshop").</summary>
    public string Path { get; set; } = "workshop";
    /// <summary>Download with the saved Steam account (many games only let owners download their Workshop items).</summary>
    public bool UseSteamAccount { get; set; }
    public bool UpdateBeforeStart { get; set; }
    public List<WorkshopItem> Items { get; set; } = new();
}

/// <summary>
/// Steam Workshop mods for a server: add items or whole collections by link, download them (DepotDownloader, the
/// project's standard — anonymously, or with the Steam account saved for SteamCMD), keep them up to date, and
/// put them where the game wants them. Steam's public Web API supplies names, sizes and "last updated".
/// </summary>
public sealed class Workshop
{
    /// <summary>Known games: dedicated server app → (game's Workshop app, style, path).</summary>
    public static readonly IReadOnlyDictionary<string, (string AppId, string Style, string Path, string Game)> Known = new Dictionary<string, (string, string, string, string)>
    {
        ["223350"] = ("221100", "arma", "", "DayZ"),
        ["233780"] = ("107410", "arma", "", "Arma 3"),
        ["443030"] = ("440900", "conan", "ConanSandbox/Mods", "Conan Exiles"),
        ["298740"] = ("244850", "folder", "workshop", "Space Engineers"),
        ["380870"] = ("108600", "folder", "workshop", "Project Zomboid"),
        ["1110390"] = ("304930", "folder", "workshop", "Unturned"),
        ["4020"] = ("4000", "folder", "garrysmod/addons", "Garry's Mod"),
        ["1829350"] = ("1604030", "folder", "workshop", "V Rising"),
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly AgentContext _ctx;
    private readonly HttpClient _http;

    public Workshop(AgentContext ctx, HttpClient? http = null)
    {
        _ctx = ctx;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>Tests: replaces the download (appId, fileId, dir) → error or null.</summary>
    public Func<string, string, string, Task<string?>>? DownloadOverride { get; set; }

    private static string FileOf(string id) => ServerPath.GetServersConfigs(id, "workshop.json");

    public WorkshopSettings Load(string id)
    {
        try { if (File.Exists(FileOf(id))) { return JsonSerializer.Deserialize<WorkshopSettings>(File.ReadAllText(FileOf(id)), Json) ?? new(); } }
        catch { /* rebuilt below */ }
        var s = new WorkshopSettings();
        // A known game fills itself in.
        string? serverApp = _ctx.Engine.Games.Get(_ctx.Engine.Servers.Get(id)?.Game ?? "")?.AppId;
        if (serverApp != null && Known.TryGetValue(serverApp, out var k)) { s.AppId = k.AppId; s.Style = k.Style; s.Path = k.Path; }
        return s;
    }

    public void Save(string id, WorkshopSettings s)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FileOf(id))!);
        string temp = FileOf(id) + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(s, Json));
        File.Move(temp, FileOf(id), overwrite: true);
    }

    public static string? CheckSettings(WorkshopSettings s)
    {
        if (!Regex.IsMatch(s.AppId ?? "", @"^\d{1,10}$")) { return "The game's Steam app id is a number (find it in the game's Steam store link)."; }
        if (s.Style is not ("folder" or "arma" or "conan")) { return "Unknown install style."; }
        if ((s.Path ?? "").Contains("..") || System.IO.Path.IsPathRooted(s.Path ?? "")) { return "The folder must be inside the server's files."; }
        return null;
    }

    // ───────────────────────────── Steam ─────────────────────────────

    /// <summary>Workshop ids in a pasted link (…?id=123), a number, or several of either.</summary>
    public static IReadOnlyList<string> IdsIn(string text) =>
        Regex.Matches(text ?? "", @"(?:[?&]id=)?(\d{6,20})").Select(m => m.Groups[1].Value).Distinct().ToList();

    private sealed record Details(string Id, string Title, long Size, long Updated, string? AppId, bool Ok);

    private async Task<List<Details>> DetailsAsync(IReadOnlyList<string> ids, CancellationToken token)
    {
        var form = new List<KeyValuePair<string, string>> { new("itemcount", ids.Count.ToString()) };
        for (int i = 0; i < ids.Count; i++) { form.Add(new($"publishedfileids[{i}]", ids[i])); }
        using var res = await _http.PostAsync("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/", new FormUrlEncodedContent(form), token);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(token));
        var list = new List<Details>();
        foreach (var d in doc.RootElement.GetProperty("response").GetProperty("publishedfiledetails").EnumerateArray())
        {
            bool ok = d.TryGetProperty("result", out var r) && r.GetInt32() == 1;
            string Str(string n) => d.TryGetProperty(n, out var v) ? v.ToString() : "";
            long Num(string n) => d.TryGetProperty(n, out var v) && long.TryParse(v.ToString(), out long x) ? x : 0;
            list.Add(new Details(Str("publishedfileid"), Str("title"), Num("file_size"), Num("time_updated"), Str("consumer_app_id"), ok));
        }
        return list;
    }

    /// <summary>For each id that's a collection, its items (ids that aren't collections are absent).</summary>
    private async Task<Dictionary<string, List<string>>> CollectionsAsync(IReadOnlyList<string> ids, CancellationToken token)
    {
        var form = new List<KeyValuePair<string, string>> { new("collectioncount", ids.Count.ToString()) };
        for (int i = 0; i < ids.Count; i++) { form.Add(new($"publishedfileids[{i}]", ids[i])); }
        using var res = await _http.PostAsync("https://api.steampowered.com/ISteamRemoteStorage/GetCollectionDetails/v1/", new FormUrlEncodedContent(form), token);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(token));
        var found = new Dictionary<string, List<string>>();
        foreach (var c in doc.RootElement.GetProperty("response").GetProperty("collectiondetails").EnumerateArray())
        {
            if (!c.TryGetProperty("children", out var children)) { continue; }
            found[c.GetProperty("publishedfileid").ToString()] = children.EnumerateArray().Select(x => x.GetProperty("publishedfileid").ToString()).ToList();
        }
        return found;
    }

    /// <summary>Adds items (or every item of a collection) from a link. Returns what was added and any problems.</summary>
    public async Task<(List<WorkshopItem> Added, List<string> Problems)> AddAsync(string id, string link, CancellationToken token)
    {
        var settings = Load(id);
        var ids = IdsIn(link);
        if (ids.Count == 0) { return (new(), new() { "Paste a Workshop link (steamcommunity.com/sharedfiles/filedetails/?id=…) or the item's number." }); }
        var details = await DetailsAsync(ids, token);
        var collections = await CollectionsAsync(ids, token);
        var problems = new List<string>();
        var expanded = new List<string>();
        foreach (var d in details)
        {
            if (!d.Ok) { problems.Add($"{d.Id}: Steam doesn't know this item (removed, private or friends-only)."); continue; }
            if (collections.TryGetValue(d.Id, out var children) && children.Count > 0) { expanded.AddRange(children); continue; }
            expanded.Add(d.Id);
        }
        var items = expanded.Count == 0 ? new List<Details>() : (await DetailsAsync(expanded.Distinct().ToList(), token)).Where(d => d.Ok).ToList();

        // An empty app id is taken from the first item (each item says which game it belongs to).
        if (string.IsNullOrEmpty(settings.AppId) && items.FirstOrDefault()?.AppId is { Length: > 0 } guess) { settings.AppId = guess; }
        var added = new List<WorkshopItem>();
        foreach (var d in items)
        {
            if (d.AppId != settings.AppId) { problems.Add($"\"{d.Title}\" is a mod for another game (app {d.AppId})."); continue; }
            if (settings.Items.Any(i => i.Id == d.Id)) { continue; }
            var item = new WorkshopItem(d.Id, d.Title, d.Size, d.Updated);
            settings.Items.Add(item);
            added.Add(item);
        }
        Save(id, settings);
        return (added, problems);
    }

    /// <summary>Refreshes names, sizes and "last updated" from Steam (to show what needs updating).</summary>
    public async Task<WorkshopSettings> RefreshAsync(string id, CancellationToken token)
    {
        var settings = Load(id);
        if (settings.Items.Count == 0) { return settings; }
        var details = (await DetailsAsync(settings.Items.Select(i => i.Id).ToList(), token)).Where(d => d.Ok).ToDictionary(d => d.Id);
        settings.Items = settings.Items.Select(i => details.TryGetValue(i.Id, out var d) ? i with { Title = d.Title, Size = d.Size, Updated = d.Updated } : i).ToList();
        Save(id, settings);
        return settings;
    }

    // ───────────────────────────── Installing ─────────────────────────────

    /// <summary>Downloads every item that's missing or out of date (and removes uninstalled leftovers), as a job.</summary>
    public OperationRequest Update(string id, bool everything = false)
    {
        var s = _ctx.Engine.Servers.Get(id);
        if (s == null) { return OperationRequest.Rejected("No such server."); }
        if (s.State != ServerState.Stopped) { return OperationRequest.Rejected($"Stop {s.Name} first — a running game has its mods open."); }
        if (!_ctx.Engine.Operations.TryBegin(id, OperationKind.Addon, "Workshop mods", out var lease, out var busy)) { return OperationRequest.Rejected($"{s.Name} is busy: {busy}."); }
        return OperationRequest.Running(_ctx.Engine.Jobs.Start("workshop", id, $"Workshop mods for {s.Name}", async job =>
        {
            using (lease) { return await UpdateCoreAsync(s, job, everything); }
        }));
    }

    public async Task<string?> UpdateCoreAsync(ServerInstance s, JobContext job, bool everything)
    {
        var settings = await RefreshAsync(s.Id, job.Cancellation);
        if (CheckSettings(settings) is { } bad) { return bad; }
        string files = ServerPath.GetServersServerFiles(s.Id);
        var todo = settings.Items.Where(i => everything || i.Installed != i.Updated || i.Folder == null || !Directory.Exists(System.IO.Path.Combine(files, i.Folder))).ToList();
        int done = 0;
        var failed = new List<string>();
        foreach (var item in todo)
        {
            job.Cancellation.ThrowIfCancellationRequested();
            job.Report(done * 100 / Math.Max(1, todo.Count), $"Downloading {item.Title} ({done + 1} of {todo.Count})");
            string staging = System.IO.Path.Combine(files, ".workshop-download", item.Id);
            try
            {
                if (Directory.Exists(staging)) { Directory.Delete(staging, true); }
                string? error = DownloadOverride != null
                    ? await DownloadOverride(settings.AppId, item.Id, staging)
                    : await Download(settings, item, staging, job);
                if (error != null) { failed.Add($"{item.Title}: {error}"); continue; }
                var placed = Place(s.Id, settings, item, staging);
                int at = settings.Items.FindIndex(i => i.Id == item.Id);
                settings.Items[at] = placed with { Installed = item.Updated };
                _ctx.Engine.Log.Write(s.Id, $"Workshop: {item.Title} ({item.Id}) installed");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failed.Add($"{item.Title}: {ex.Message}");
            }
            finally
            {
                try { if (Directory.Exists(staging)) { Directory.Delete(staging, true); } } catch { }
                done++;
            }
        }
        try { Directory.Delete(System.IO.Path.Combine(files, ".workshop-download")); } catch { /* not empty or gone */ }
        Save(s.Id, settings);
        Wire(s.Id, settings);
        job.Report(100, "Done");
        if (failed.Count > 0)
        {
            foreach (string f in failed) { job.Log(f); _ctx.Engine.Log.Write(s.Id, "[NOTICE] Workshop: " + f); }
            return failed.Count == todo.Count ? "No mod could be downloaded: " + failed[0] : $"{failed.Count} of {todo.Count} mods couldn't be downloaded (see the activity log).";
        }
        return null;
    }

    private static async Task<string?> Download(WorkshopSettings settings, WorkshopItem item, string dir, JobContext job)
    {
        var (exit, output) = await WindowsGSM.Installer.DepotDownloader.DownloadWorkshopItemAsync(settings.AppId, item.Id, dir, !settings.UseSteamAccount, job.Log, _ => { }, job.Cancellation);
        if (exit == 0 && Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any(e => !System.IO.Path.GetFileName(e).StartsWith(".DepotDownloader"))) { return null; }
        string text = output ?? "";
        if (text.Contains("InvalidPassword", StringComparison.OrdinalIgnoreCase) || text.Contains("Steam Guard", StringComparison.OrdinalIgnoreCase) || text.Contains("2FA", StringComparison.OrdinalIgnoreCase))
        {
            return "Steam needs a sign-in code for the saved account — sign in once with DepotDownloader on the server (it remembers the device), or use an account without Steam Guard for servers.";
        }
        if (!settings.UseSteamAccount && (text.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase) || text.Contains("No subscription", StringComparison.OrdinalIgnoreCase)
            || text.Contains("denied", StringComparison.OrdinalIgnoreCase)))
        {
            return "Steam wouldn't give it to an anonymous download — this game only lets owners download Workshop items. Turn on \"Use the Steam account\".";
        }
        string last = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
        return exit == null ? output : $"DepotDownloader stopped with code {exit}{(last.Length > 0 ? ": " + last : ".")}";
    }

    /// <summary>Moves a downloaded item into place, by style. Returns the item with its folder (and keys).</summary>
    private static WorkshopItem Place(string id, WorkshopSettings settings, WorkshopItem item, string staging)
    {
        string files = ServerPath.GetServersServerFiles(id);
        foreach (string meta in Directory.EnumerateDirectories(staging, ".DepotDownloader", SearchOption.TopDirectoryOnly)) { Directory.Delete(meta, true); }

        string folder = settings.Style switch
        {
            "arma" => "@" + Safe(item.Title, item.Id),
            "conan" => System.IO.Path.Combine(settings.Path ?? "ConanSandbox/Mods", item.Id),
            _ => System.IO.Path.Combine(string.IsNullOrWhiteSpace(settings.Path) ? "workshop" : settings.Path, item.Id),
        };
        string target = System.IO.Path.GetFullPath(System.IO.Path.Combine(files, folder));
        if (!target.StartsWith(System.IO.Path.GetFullPath(files).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException("Unsafe mod folder."); }
        // A renamed mod leaves its old folder behind otherwise.
        if (item.Folder != null && !string.Equals(item.Folder, folder, StringComparison.OrdinalIgnoreCase))
        {
            string old = System.IO.Path.Combine(files, item.Folder);
            if (Directory.Exists(old)) { Directory.Delete(old, true); }
        }
        if (Directory.Exists(target)) { Directory.Delete(target, true); }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        Directory.Move(staging, target);

        var keys = new List<string>();
        if (settings.Style == "arma")
        {
            string keysDir = System.IO.Path.Combine(files, "keys");
            foreach (string key in Directory.EnumerateFiles(target, "*.bikey", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(keysDir);
                File.Copy(key, System.IO.Path.Combine(keysDir, System.IO.Path.GetFileName(key)), overwrite: true);
                keys.Add(System.IO.Path.GetFileName(key));
            }
        }
        return item with { Folder = folder.Replace('\\', '/'), Keys = keys };
    }

    /// <summary>Removes an item: its folder, its keys, and its place in -mod= / modlist.txt.</summary>
    public string? Remove(string id, string itemId)
    {
        var settings = Load(id);
        var item = settings.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null) { return "No such mod on this server."; }
        var s = _ctx.Engine.Servers.Get(id);
        if (s != null && s.State != ServerState.Stopped && item.Folder != null) { return $"Stop {s.Name} first — it has the mod open."; }
        string files = ServerPath.GetServersServerFiles(id);
        if (item.Folder != null)
        {
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(files, item.Folder));
            if (dir.StartsWith(System.IO.Path.GetFullPath(files).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && Directory.Exists(dir)) { Directory.Delete(dir, true); }
        }
        foreach (string key in item.Keys ?? new())
        {
            // Only if no other mod brought the same key.
            if (settings.Items.Any(o => o.Id != itemId && (o.Keys ?? new()).Contains(key))) { continue; }
            try { File.Delete(System.IO.Path.Combine(files, "keys", key)); } catch { }
        }
        settings.Items.Remove(item);
        Save(id, settings);
        Wire(id, settings, removedFolder: item.Folder);
        return null;
    }

    /// <summary>
    /// Tells the game about its mods. arma: the -mod= start parameter lists every installed @Mod (anything else
    /// already in it stays). conan: modlist.txt lists their .pak files. folder: nothing (the game's own config).
    /// </summary>
    private void Wire(string id, WorkshopSettings settings, string? removedFolder = null)
    {
        string files = ServerPath.GetServersServerFiles(id);
        var installed = settings.Items.Where(i => i.Folder != null && Directory.Exists(System.IO.Path.Combine(files, i.Folder))).ToList();
        if (settings.Style == "arma")
        {
            var cfg = new ServerConfig(id);
            string param = cfg.ServerParam ?? "";
            var m = Regex.Match(param, @"-mod=(""[^""]*""|\S*)");
            var mods = m.Success ? m.Groups[1].Value.Trim('"').Split(';', StringSplitOptions.RemoveEmptyEntries).ToList() : new List<string>();
            var ours = settings.Items.Select(i => i.Folder).Where(f => f != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (removedFolder != null) { ours.Add(removedFolder); }
            mods = mods.Where(x => !ours.Contains(x)).Concat(installed.Select(i => i.Folder!)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string value = mods.Count == 0 ? "" : $"-mod=\"{string.Join(";", mods)}\"";
            string updated = m.Success ? (value.Length == 0 ? param.Remove(m.Index, m.Length) : param.Remove(m.Index, m.Length).Insert(m.Index, value)) : (value.Length == 0 ? param : (param + " " + value));
            updated = Regex.Replace(updated, @"\s{2,}", " ").Trim();
            if (updated != param) { ServerConfig.SetSetting(id, ServerConfig.SettingName.ServerParam, updated); _ctx.Engine.Servers.Get(id)?.ReloadConfig(); }
        }
        else if (settings.Style == "conan")
        {
            string modsDir = System.IO.Path.Combine(files, string.IsNullOrWhiteSpace(settings.Path) ? "ConanSandbox/Mods" : settings.Path);
            Directory.CreateDirectory(modsDir);
            var paks = installed.SelectMany(i => Directory.EnumerateFiles(System.IO.Path.Combine(files, i.Folder!), "*.pak", SearchOption.AllDirectories)).ToList();
            File.WriteAllLines(System.IO.Path.Combine(modsDir, "modlist.txt"), paks.Select(p => "*" + p.Replace('/', '\\')));
        }
    }

    private static string Safe(string title, string fallback)
    {
        string s = Regex.Replace(title ?? "", @"[^A-Za-z0-9 _\-\.]", "").Trim().Replace(' ', '_');
        if (s.Length > 48) { s = s[..48]; }
        return s.Length == 0 ? fallback : s;
    }
}

/// <summary>"Update Workshop mods before start" (only when that's switched on for the server).</summary>
public sealed class WorkshopBeforeStart : IPreStartStep
{
    private readonly Workshop _workshop;
    public WorkshopBeforeStart(Workshop workshop) => _workshop = workshop;
    public string Name => "Update Workshop mods";

    public async Task RunAsync(ServerInstance server, StartReason reason, JobContext job)
    {
        var settings = _workshop.Load(server.Id);
        if (!settings.UpdateBeforeStart || settings.Items.Count == 0) { return; }
        try
        {
            string? error = await _workshop.UpdateCoreAsync(server, job, everything: false);
            if (error != null) { job.Log("Workshop update before start: " + error + " — starting anyway."); }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { job.Log("Couldn't reach Steam to check mods — starting anyway."); }
    }
}
