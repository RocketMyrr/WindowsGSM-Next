using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

// Also compiled into the desktop app (its own copy, in its own namespace).
#if WGSM_DESKTOP
namespace WindowsGSM.Desktop.Shared;
#else
namespace WindowsGSM.Agent.Hosting;
#endif

/// <summary>A release on the update feed.</summary>
public sealed record AppRelease(string Version, string Name, string? Notes, string ZipUrl, string? ShaUrl, long Size, DateTimeOffset? Published, bool Prerelease);

/// <summary>
/// Reading the update feed, downloading and checking a release, and putting it next to the running version.
/// Shared by the agent's own updater (<see cref="SelfUpdate"/>) and the desktop app when it only controls other PCs
/// (no agent of its own to update it) — the desktop project compiles this file in, so it uses nothing else.
/// </summary>
public static class AppReleases
{
    /// <summary>Where releases come from unless the agent's settings say otherwise.</summary>
    public const string DefaultRepo = "RocketMyrr/WindowsGSM-Next";

    private static readonly Regex AssetName = new(@"^WindowsGSM-(?<v>\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?)\.zip$", RegexOptions.IgnoreCase);

    /// <summary>The releases on a feed: a GitHub repository (owner/name), or the address of a JSON feed.</summary>
    public static async Task<IReadOnlyList<AppRelease>> FetchAsync(HttpClient http, string repo)
    {
        repo = repo.Trim();
        if (repo.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || repo.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return await FetchManifestAsync(http, repo);
        }
        if (!Regex.IsMatch(repo, @"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")) { throw new InvalidOperationException("The update feed isn't a GitHub repository (owner/name)."); }
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{repo}/releases?per_page=30"));
        var list = new List<AppRelease>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.GetBoolean()) { continue; }
            var assets = r.GetProperty("assets").EnumerateArray().ToList();
            foreach (var a in assets)
            {
                var m = AssetName.Match(a.GetProperty("name").GetString() ?? "");
                if (!m.Success) { continue; }
                string zipName = a.GetProperty("name").GetString()!;
                var sha = assets.FirstOrDefault(x => string.Equals(x.GetProperty("name").GetString(), zipName + ".sha256", StringComparison.OrdinalIgnoreCase));
                list.Add(new AppRelease(m.Groups["v"].Value, r.GetProperty("name").GetString() ?? m.Groups["v"].Value,
                    r.TryGetProperty("body", out var b) ? b.GetString() : null, a.GetProperty("browser_download_url").GetString()!,
                    sha.ValueKind == JsonValueKind.Object ? sha.GetProperty("browser_download_url").GetString() : null,
                    a.GetProperty("size").GetInt64(), r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String ? p.GetDateTimeOffset() : null,
                    r.TryGetProperty("prerelease", out var pr) && pr.GetBoolean()));
            }
        }
        return list;
    }

    /// <summary>
    /// A self-hosted feed: a JSON array of {"version","zip","sha256","notes","prerelease"} where zip and sha256
    /// are URLs (relative ones resolve against the feed's address).
    /// </summary>
    private static async Task<IReadOnlyList<AppRelease>> FetchManifestAsync(HttpClient http, string url)
    {
        var baseUri = new Uri(url);
        using var doc = JsonDocument.Parse(await http.GetStringAsync(url));
        var list = new List<AppRelease>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            string? version = r.TryGetProperty("version", out var v) ? v.GetString() : null;
            string? zip = r.TryGetProperty("zip", out var z) ? z.GetString() : null;
            if (version == null || zip == null) { continue; }
            string? sha = r.TryGetProperty("sha256", out var h) ? h.GetString() : null;
            list.Add(new AppRelease(version.TrimStart('v'), $"WindowsGSM {version}", r.TryGetProperty("notes", out var n) ? n.GetString() : null,
                new Uri(baseUri, zip).ToString(), sha == null ? null : new Uri(baseUri, sha).ToString(), 0, null,
                r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True));
        }
        return list;
    }

    /// <summary>
    /// Downloads a release into <paramref name="downloads"/> and checks it against its published checksum; returns
    /// the zip. Anything that doesn't match is deleted and refused.
    /// </summary>
    public static async Task<string> DownloadAsync(HttpClient http, AppRelease release, string downloads, Action<int>? percent = null)
    {
        Directory.CreateDirectory(downloads);
        string zip = Path.Combine(downloads, $"WindowsGSM-{release.Version}.zip");
        using (var res = await http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            res.EnsureSuccessStatusCode();
            long total = res.Content.Headers.ContentLength ?? release.Size;
            await using var src = await res.Content.ReadAsStreamAsync();
            await using (var dst = File.Create(zip + ".part"))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    if (total > 0) { percent?.Invoke((int)(done * 100 / total)); }
                }
            }
            File.Move(zip + ".part", zip, overwrite: true);
        }

        if (release.ShaUrl == null) { File.Delete(zip); throw new InvalidOperationException("The release has no checksum (.sha256), so it can't be verified."); }
        string expected = (await http.GetStringAsync(release.ShaUrl)).Trim().Split(' ', '\t', '\n')[0].ToLowerInvariant();
        string actual;
        await using (var fs = File.OpenRead(zip)) { actual = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant(); }
        if (actual != expected) { File.Delete(zip); throw new InvalidOperationException("The download didn't match its checksum — it was deleted. Try again."); }
        return zip;
    }

    /// <summary>
    /// Puts versions\&lt;version&gt;\ from the zip next to the running version (staged, then renamed into place) and
    /// refreshes the launcher if the release carries a newer one.
    /// </summary>
    public static void Unpack(string installRoot, string? launcher, string zipFile, string version)
    {
        // The version names a folder (and goes on the launcher's command line): never trust a feed's spelling.
        if (!Regex.IsMatch(version, @"^[0-9A-Za-z][0-9A-Za-z.+-]{0,39}$") || version.Contains(".."))
        {
            throw new InvalidOperationException($"\"{version}\" isn't a valid version.");
        }
        string versions = Path.Combine(installRoot, "versions");
        string target = Path.Combine(versions, version), staging = target + ".partial";
        if (Directory.Exists(staging)) { Directory.Delete(staging, true); }
        Directory.CreateDirectory(staging);
        string prefix = $"versions/{version}/";
        byte[]? newLauncher = null;
        using (var zip = ZipFile.OpenRead(zipFile))
        {
            bool any = false;
            foreach (var e in zip.Entries)
            {
                string name = e.FullName.Replace('\\', '/');
                if (string.Equals(name, "WindowsGSM.exe", StringComparison.OrdinalIgnoreCase))
                {
                    using var ms = new MemoryStream();
                    using (var s = e.Open()) { s.CopyTo(ms); }
                    newLauncher = ms.ToArray();
                    continue;
                }
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || name.Length == prefix.Length) { continue; }
                string path = Path.GetFullPath(Path.Combine(staging, name[prefix.Length..].Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException("The update contains an unsafe path."); }
                if (name.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                e.ExtractToFile(path, overwrite: true);
                any = true;
            }
            if (!any || !File.Exists(Path.Combine(staging, "wgsm-agent.exe"))) { throw new InvalidOperationException($"The download doesn't contain WindowsGSM {version}."); }
        }
        if (Directory.Exists(target)) { Directory.Delete(target, true); }
        Directory.Move(staging, target);

        // The launcher is running only briefly (or waiting on this agent), so swap it by renaming.
        if (newLauncher != null && launcher != null && !File.ReadAllBytes(launcher).AsSpan().SequenceEqual(newLauncher))
        {
            // Old copies from earlier updates go when they can; one that's still running gets a fresh name.
            foreach (string stale in Directory.EnumerateFiles(Path.GetDirectoryName(launcher)!, Path.GetFileName(launcher) + ".*old"))
            {
                try { File.Delete(stale); } catch { /* still running; next time */ }
            }
            string old = launcher + ".old";
            if (File.Exists(old)) { old = $"{launcher}.{DateTime.UtcNow:yyyyMMddHHmmss}.old"; }
            File.Move(launcher, old);
            File.WriteAllBytes(launcher, newLauncher);
        }
    }

    /// <summary>Semantic-ish order: "2.0.10" &gt; "2.0.9"; a release beats its pre-releases.</summary>
    public static int Compare(string a, string b)
    {
        string[] pa = a.TrimStart('v').Split('-', 2), pb = b.TrimStart('v').Split('-', 2);
        if (!Version.TryParse(pa[0], out var va)) { va = new Version(0, 0); }
        if (!Version.TryParse(pb[0], out var vb)) { vb = new Version(0, 0); }
        int c = va.CompareTo(vb);
        if (c != 0) { return c; }
        if ((pa.Length > 1) != (pb.Length > 1)) { return pa.Length > 1 ? -1 : 1; }
        return pa.Length > 1 ? ComparePre(pa[1], pb[1]) : 0;
    }

    /// <summary>"alpha.2" &lt; "alpha.10" &lt; "beta.1".</summary>
    private static int ComparePre(string a, string b)
    {
        string[] x = a.Split('.'), y = b.Split('.');
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            if (i >= x.Length) { return -1; }
            if (i >= y.Length) { return 1; }
            int c = int.TryParse(x[i], out int nx) && int.TryParse(y[i], out int ny) ? nx.CompareTo(ny) : string.CompareOrdinal(x[i], y[i]);
            if (c != 0) { return c; }
        }
        return 0;
    }
}
