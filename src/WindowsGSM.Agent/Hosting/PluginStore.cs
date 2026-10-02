using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using WindowsGSM.Agent.Api;
using WindowsGSM.Functions;

namespace WindowsGSM.Agent.Hosting;

/// <summary>A community plugin found on GitHub.</summary>
public sealed record CatalogPlugin(string Repo, string Name, string Game, string Owner, string? Description, string Url,
    int Stars, DateTimeOffset? UpdatedAt, string? Installed, string? Branch = null);

/// <summary>A plugin in this machine's plugins/ folder.</summary>
public sealed record InstalledPlugin(string File, string Game, string? Name, string? Author, string? Version, string? Description,
    string? Url, string? Repo, bool Loaded, string? Error, int Servers, bool HasIcon = false, bool HasPrevious = false);

/// <summary>
/// Community game plugins: find them on GitHub (repositories named WindowsGSM.&lt;Game&gt;, the convention the
/// legacy app searched for), install or update one into plugins/, remove unused ones, and recompile the set so
/// new games show up without a restart. Plugins are C# code run by the agent, so only owners may change them.
/// </summary>
public sealed class PluginStore
{
    public const long MaxDownload = 25 * 1024 * 1024;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private readonly AgentContext _ctx;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _changing = new(1, 1);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<CatalogPlugin> Items)> _searches = new();

    public PluginStore(AgentContext ctx, HttpClient? http = null)
    {
        _ctx = ctx;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any()) { _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WindowsGSM", "2")); }
    }

    /// <summary>Swappable for tests: owner/name → the repository's zip bytes.</summary>
    public Func<string, CancellationToken, Task<byte[]>>? DownloadOverride { get; set; }

    // ───────────────────────────── Installed ─────────────────────────────

    public IReadOnlyList<InstalledPlugin> Installed()
    {
        var servers = _ctx.Engine.Servers.All;
        return _ctx.Engine.Plugins.Plugins
            .Where(p => p.FileName != "NoValidPlugin")
            .Select(p => new InstalledPlugin(p.FileName, p.FullName, p.Plugin?.name, p.Plugin?.author, p.Plugin?.version, p.Plugin?.description,
                p.Plugin?.url, RepoOf(p.Plugin?.url), p.IsLoaded, p.IsLoaded ? null : p.Error, servers.Count(s => s.Game == p.FullName),
                IconPath(p.FileName) != null, Directory.Exists(PreviousPath(p.FileName))))
            .OrderBy(p => p.File, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ───────────────────────────── Catalog ─────────────────────────────

    /// <summary>Plugin repositories on GitHub matching <paramref name="term"/> (or the most starred ones).</summary>
    public async Task<IReadOnlyList<CatalogPlugin>> SearchAsync(string? term, CancellationToken token)
    {
        term = (term ?? "").Trim();
        if (term.Length > 60) { term = term[..60]; }
        string cacheKey = term.ToLowerInvariant();
        IReadOnlyList<CatalogPlugin> found;
        if (_searches.TryGetValue(cacheKey, out var hit) && DateTimeOffset.UtcNow - hit.At < CacheFor) { found = hit.Items; }
        else
        {
            // GitHub search: "WindowsGSM.<term> in:name". Without a term, every WindowsGSM.* repository, most starred first.
            string q = $"WindowsGSM.{term} in:name";
            using var res = await _http.GetAsync($"https://api.github.com/search/repositories?q={Uri.EscapeDataString(q)}&sort=stars&order=desc&per_page=60", token);
            if ((int)res.StatusCode is 403 or 429) { throw new InvalidOperationException("GitHub is limiting searches right now — try again in a minute."); }
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(token));
            var list = new List<CatalogPlugin>();
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                string name = item.GetProperty("name").GetString() ?? "";
                if (!name.StartsWith("WindowsGSM.", StringComparison.OrdinalIgnoreCase) || name.Length <= "WindowsGSM.".Length) { continue; }
                if (item.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True) { continue; }
                string game = name["WindowsGSM.".Length..];
                list.Add(new CatalogPlugin(item.GetProperty("full_name").GetString() ?? "", name, game,
                    item.GetProperty("owner").GetProperty("login").GetString() ?? "",
                    item.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                    item.GetProperty("html_url").GetString() ?? "",
                    item.GetProperty("stargazers_count").GetInt32(),
                    item.TryGetProperty("pushed_at", out var u) && u.ValueKind == JsonValueKind.String ? u.GetDateTimeOffset() : null,
                    null,
                    item.TryGetProperty("default_branch", out var br) && br.ValueKind == JsonValueKind.String ? br.GetString() : null));
            }
            found = list;
            _searches[cacheKey] = (DateTimeOffset.UtcNow, found);
        }

        // Mark what's already here (by repository, or by the plugin file the repository would install).
        var installed = Installed();
        return found.Select(c => c with
        {
            Installed = installed.FirstOrDefault(p => string.Equals(p.Repo, c.Repo, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.File, c.Game + ".cs", StringComparison.OrdinalIgnoreCase))?.File,
        }).ToList();
    }

    // ───────────────────────────── Logos ─────────────────────────────

    /// <summary>An installed plugin's logo: plugins/X.cs/X.png (what the legacy app showed), if it has one.</summary>
    public static string? IconPath(string file)
    {
        if (file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) { return null; }
        string path = ServerPath.GetPlugins(file, Path.GetFileNameWithoutExtension(file) + ".png");
        return File.Exists(path) ? path : null;
    }

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private const int MaxLogoDownload = 8 * 1024 * 1024;

    /// <summary>A logo no bigger than <paramref name="max"/> px (as PNG), or null if it isn't a readable image.</summary>
    public static byte[]? Shrink(byte[] image, int max = 256)
    {
        try
        {
            using var input = new MemoryStream(image);
            using var source = System.Drawing.Image.FromStream(input);
            if (source.Width <= max && source.Height <= max) { return image; }
            double scale = Math.Min((double)max / source.Width, (double)max / source.Height);
            int w = Math.Max(1, (int)Math.Round(source.Width * scale)), h = Math.Max(1, (int)Math.Round(source.Height * scale));
            using var bitmap = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                g.DrawImage(source, 0, 0, w, h);
            }
            using var output = new MemoryStream();
            bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException) { return null; }
    }

    /// <summary>An installed plugin's logo at tile size, cached next to the catalog's (by the file's time stamp).</summary>
    public static string? IconThumbnail(string file)
    {
        string? path = IconPath(file);
        if (path == null) { return null; }
        string dir = Path.Combine(global::WindowsGSM.Hosting.WgsmEnvironment.DataRoot, "cache", "plugin-icons");
        string thumb = Path.Combine(dir, $"local_{Path.GetFileNameWithoutExtension(file)}_{File.GetLastWriteTimeUtc(path).Ticks}.png");
        if (File.Exists(thumb)) { return thumb; }
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length <= 64 * 1024) { return path; } // already small
            byte[]? small = Shrink(bytes);
            if (small == null) { return path; }
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(thumb, small);
            return thumb;
        }
        catch (IOException) { return path; }
    }

    /// <summary>
    /// A catalog plugin's logo, from its repository (&lt;Game&gt;.cs/&lt;Game&gt;.png, the plugin layout), fetched by the
    /// agent — the page may only show images from here — and cached in cache/plugin-icons, shrunk to 256 px (plugin
    /// logos are often 1 MB+). Only PNGs; "none" is remembered for a day so missing logos aren't asked for again.
    /// </summary>
    public async Task<string?> CatalogIconAsync(string repo, string branch, string game, CancellationToken token)
    {
        if (!IsRepo(repo) || !System.Text.RegularExpressions.Regex.IsMatch(branch, @"^[A-Za-z0-9._/-]{1,60}$") || branch.Contains("..")
            || !System.Text.RegularExpressions.Regex.IsMatch(game, @"^[A-Za-z0-9._-]{1,60}$")) { return null; }
        string dir = Path.Combine(global::WindowsGSM.Hosting.WgsmEnvironment.DataRoot, "cache", "plugin-icons");
        string name = repo.Replace('/', '_') + ".png", file = Path.Combine(dir, name), none = file + ".none";
        if (File.Exists(file)) { return file; }
        if (File.Exists(none) && DateTime.UtcNow - File.GetLastWriteTimeUtc(none) < TimeSpan.FromDays(1)) { return null; }
        Directory.CreateDirectory(dir);
        try
        {
            using var res = await _http.GetAsync($"https://raw.githubusercontent.com/{repo}/{branch}/{game}.cs/{game}.png", HttpCompletionOption.ResponseHeadersRead, token);
            if (res.IsSuccessStatusCode && (res.Content.Headers.ContentLength ?? 0) < MaxLogoDownload)
            {
                byte[] bytes = await res.Content.ReadAsByteArrayAsync(token);
                if (bytes.Length < MaxLogoDownload && bytes.AsSpan().StartsWith(PngSignature) && Shrink(bytes) is { } small)
                {
                    await File.WriteAllBytesAsync(file, small, token);
                    return file;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; } // try again later
        catch (IOException) { return File.Exists(file) ? file : null; } // another request for the same logo got there first
        try { await File.WriteAllTextAsync(none, "", token); } catch (IOException) { /* same */ }
        return null;
    }

    // ───────────────────────────── Changing ─────────────────────────────

    /// <summary>Installs (or updates) the plugin in GitHub repository <paramref name="repo"/> ("owner/name"). Returns it once loaded.</summary>
    public async Task<InstalledPlugin> InstallAsync(string repo, CancellationToken token)
    {
        repo = RepoOf(repo) ?? repo; // a pasted https://github.com/owner/name link works too
        if (!IsRepo(repo)) { throw new ArgumentException("That isn't a GitHub repository (owner/name, or its github.com link)."); }
        byte[] zip = DownloadOverride != null ? await DownloadOverride(repo, token) : await DownloadAsync(repo, token);
        await _changing.WaitAsync(token);
        try
        {
            string file = Extract(zip);
            await _ctx.Engine.Plugins.LoadAsync();
            return Installed().FirstOrDefault(p => string.Equals(p.File, file, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"{file} was installed but didn't show up in the plugin list.");
        }
        finally { _changing.Release(); }
    }

    /// <summary>
    /// Installs a plugin from files the owner has: a plugin's .cs file (optionally with its logo), or a .zip of the
    /// plugin folder or repository. Same checks as installing from GitHub; replaces a plugin with the same name.
    /// </summary>
    public async Task<InstalledPlugin> InstallFileAsync(string fileName, byte[] content, byte[]? logo, CancellationToken token)
    {
        if (content.Length == 0) { throw new ArgumentException("That file is empty."); }
        if (content.LongLength > MaxDownload) { throw new ArgumentException("That file is too big to be a plugin."); }
        if (logo != null && (logo.Length > 1024 * 1024 || !logo.AsSpan().StartsWith(PngSignature))) { throw new ArgumentException("The logo must be a PNG under 1 MB."); }
        string name = Path.GetFileName(fileName ?? "");
        bool isZip = name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        if (!isZip && !System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z0-9_]{1,80}\.cs$"))
        {
            throw new ArgumentException("Choose the plugin's .cs file (named like Palworld.cs — letters, digits and _ only) or a .zip of it.");
        }

        await _changing.WaitAsync(token);
        try
        {
            string file = isZip ? Extract(content) : WriteSingle(name, content);
            if (logo != null) { await File.WriteAllBytesAsync(ServerPath.GetPlugins(file, Path.GetFileNameWithoutExtension(file) + ".png"), logo, token); }
            await _ctx.Engine.Plugins.LoadAsync();
            return Installed().FirstOrDefault(p => string.Equals(p.File, file, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"{file} was added but didn't show up in the plugin list.");
        }
        catch (InvalidDataException) { throw new ArgumentException("That .zip file is damaged or isn't a zip."); }
        finally { _changing.Release(); }
    }

    /// <summary>plugins/X.cs/X.cs from a lone .cs file — keeping the logo and anything else already in that folder.</summary>
    private static string WriteSingle(string name, byte[] content)
    {
        string folder = ServerPath.GetPlugins(name);
        if (Directory.Exists(folder)) { KeepAsPrevious(folder, move: false); }
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, name), temp = target + ".new";
        File.WriteAllBytes(temp, content);
        File.Move(temp, target, overwrite: true);
        return name;
    }

    /// <summary>Removes a plugin no server uses. Returns an error, or null.</summary>
    public async Task<string?> RemoveAsync(string file)
    {
        var plugin = Installed().FirstOrDefault(p => string.Equals(p.File, file, StringComparison.OrdinalIgnoreCase));
        if (plugin == null) { return "No such plugin."; }
        if (plugin.Servers > 0) { return $"{plugin.Servers} server{(plugin.Servers == 1 ? " uses" : "s use")} this game. Delete {(plugin.Servers == 1 ? "it" : "them")} first."; }
        await _changing.WaitAsync();
        try
        {
            string folder = Path.GetFullPath(ServerPath.GetPlugins(plugin.File));
            string root = Path.GetFullPath(ServerPath.GetPlugins());
            if (!folder.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { return "Invalid plugin folder."; }
            if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
            if (Directory.Exists(PreviousPath(plugin.File))) { Directory.Delete(PreviousPath(plugin.File), recursive: true); }
            await _ctx.Engine.Plugins.LoadAsync();
            return null;
        }
        finally { _changing.Release(); }
    }

    private async Task<byte[]> DownloadAsync(string repo, CancellationToken token)
    {
        using var res = await _http.GetAsync($"https://api.github.com/repos/{repo}/zipball", HttpCompletionOption.ResponseHeadersRead, token);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) { throw new InvalidOperationException("GitHub doesn't have that repository (it may be private or deleted)."); }
        res.EnsureSuccessStatusCode();
        if (res.Content.Headers.ContentLength > MaxDownload) { throw new InvalidOperationException("That repository is too big to be a plugin."); }
        await using var stream = await res.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxDownload) { throw new InvalidOperationException("That repository is too big to be a plugin."); }
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Finds the plugin folder in a repository archive — a folder named X.cs holding X.cs — and copies it to
    /// plugins/X.cs, replacing an older copy. Returns "X.cs".
    /// </summary>
    public static string Extract(byte[] zipBytes)
    {
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        string? folder = null;
        foreach (var entry in zip.Entries)
        {
            string[] parts = entry.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length - 1 && folder == null; i++)
            {
                if (parts[i].EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && string.Equals(parts[i + 1], parts[i], StringComparison.OrdinalIgnoreCase))
                {
                    folder = string.Join("/", parts.Take(i + 1)) + "/";
                }
            }
            if (folder != null) { break; }
        }
        string name;
        if (folder != null) { name = folder.TrimEnd('/').Split('/').Last(); }
        else
        {
            // A zip of the folder's contents (X.cs, X.png… with no X.cs folder around them): exactly one .cs file.
            var sources = zip.Entries.Where(e => !e.FullName.EndsWith('/') && e.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
            if (sources.Count != 1) { throw new InvalidOperationException("This doesn't contain a WindowsGSM plugin (a folder named Something.cs with Something.cs inside)."); }
            name = sources[0].Name;
            string full = sources[0].FullName.Replace('\\', '/');
            folder = full[..^name.Length];
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { throw new InvalidOperationException("Invalid plugin name."); }
        string root = Path.GetFullPath(ServerPath.GetPlugins());
        Directory.CreateDirectory(root);
        string destination = Path.GetFullPath(Path.Combine(root, name));
        string staging = destination + ".new";
        if (Directory.Exists(staging)) { Directory.Delete(staging, true); }
        Directory.CreateDirectory(staging);

        long total = 0;
        foreach (var entry in zip.Entries)
        {
            string path = entry.FullName.Replace('\\', '/');
            int at = folder.Length == 0 ? 0 : path.IndexOf(folder, StringComparison.OrdinalIgnoreCase);
            if (at < 0) { continue; }
            string relative = path[(at + folder.Length)..];
            if (relative.Length == 0) { continue; }
            string target = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException("The plugin archive contains an unsafe path."); }
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            total += entry.Length;
            if (total > MaxDownload * 4) { throw new InvalidOperationException("The plugin unpacks to something far too big."); }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }

        // Swap in the new copy (the old one is only removed once the new one is complete).
        if (Directory.Exists(destination)) { KeepAsPrevious(destination, move: true); }
        Directory.Move(staging, destination);
        return name;
    }

    // ───────────────────────────── Previous version ─────────────────────────────

    /// <summary>Where the copy a plugin had before its last install/update is kept: cache/plugin-previous/X.cs.</summary>
    public static string PreviousPath(string file) =>
        Path.Combine(global::WindowsGSM.Hosting.WgsmEnvironment.DataRoot, "cache", "plugin-previous", Path.GetFileName(file));

    /// <summary>Keeps <paramref name="folder"/> as the plugin's previous version (replacing an older one).</summary>
    private static void KeepAsPrevious(string folder, bool move)
    {
        string previous = PreviousPath(Path.GetFileName(folder));
        if (Directory.Exists(previous)) { Directory.Delete(previous, true); }
        Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
        if (move) { Directory.Move(folder, previous); return; }
        foreach (string f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(previous, Path.GetRelativePath(folder, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target);
        }
    }

    /// <summary>
    /// Puts back the version a plugin had before its last install/update — for an update that doesn't compile or
    /// breaks the game. The version being replaced becomes the "previous" one, so this can be undone too.
    /// </summary>
    public async Task<InstalledPlugin> RestorePreviousAsync(string file, CancellationToken token)
    {
        if (file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("Invalid plugin name."); }
        string previous = PreviousPath(file);
        if (!Directory.Exists(previous)) { throw new InvalidOperationException("There's no previous version of this plugin to go back to."); }
        await _changing.WaitAsync(token);
        try
        {
            string current = ServerPath.GetPlugins(file);
            string swap = previous + ".swap";
            if (Directory.Exists(swap)) { Directory.Delete(swap, true); }
            Directory.Move(previous, swap);
            if (Directory.Exists(current)) { Directory.Move(current, previous); }
            Directory.Move(swap, current);
            await _ctx.Engine.Plugins.LoadAsync();
            return Installed().FirstOrDefault(p => string.Equals(p.File, file, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"{file} was put back but didn't show up in the plugin list.");
        }
        finally { _changing.Release(); }
    }

    private static bool IsRepo(string? repo) =>
        repo != null && repo.Length <= 140 && System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}$");

    /// <summary>"https://github.com/owner/name(.git)(/...)" → "owner/name".</summary>
    public static string? RepoOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) { return null; }
        if (!u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && !u.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)) { return null; }
        var parts = u.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) { return null; }
        string repo = $"{parts[0]}/{(parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1])}";
        return IsRepo(repo) ? repo : null;
    }
}
