using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsGSM.Agent.Api;
using WindowsGSM.Agent.Notifications;
using WindowsGSM.Agent.Realtime;

namespace WindowsGSM.Agent.Hosting;

/// <summary>A release on the update feed.</summary>
public sealed record AppRelease(string Version, string Name, string? Notes, string ZipUrl, string? ShaUrl, long Size, DateTimeOffset? Published, bool Prerelease);

/// <summary>
/// Updates WindowsGSM itself. Releases come from the GitHub repository in the agent's settings (assets named
/// WindowsGSM-&lt;version&gt;.zip, with a .sha256 beside them). An update downloads and verifies the zip, unpacks
/// its versions\&lt;version&gt;\ next to the running one, and asks the launcher to switch once this agent has
/// stopped — game servers keep running and are picked up again by the new agent. The previous version stays
/// on disk for an instant rollback. Only available when installed with setup (the launcher tells us where).
/// </summary>
public sealed class SelfUpdate : IDisposable
{
    public enum UpdateState { Idle, Checking, Downloading, Installing, Restarting, Error }

    private static readonly Regex AssetName = new(@"^WindowsGSM-(?<v>\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?)\.zip$", RegexOptions.IgnoreCase);

    private readonly AgentContext _ctx;
    private readonly NotificationCentre _notifications;
    private readonly HttpClient _http;
    private readonly Action<string> _log;
    private readonly Timer _timer;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private string? _announced;

    public SelfUpdate(AgentContext ctx, NotificationCentre notifications, Action<string> log, HttpClient? http = null)
    {
        _ctx = ctx;
        _notifications = notifications;
        _log = log;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any()) { _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WindowsGSM", "2")); }
        InstallRoot = Environment.GetEnvironmentVariable("WGSM_INSTALL_ROOT") is { Length: > 0 } r && Directory.Exists(r) ? r : null;
        Launcher = Environment.GetEnvironmentVariable("WGSM_LAUNCHER") is { Length: > 0 } l && File.Exists(l) ? l : null;
        _timer = new Timer(_ => _ = CheckAsync(quiet: true), null, TimeSpan.FromMinutes(3), TimeSpan.FromHours(6));
    }

    public string? InstallRoot { get; set; }
    public string? Launcher { get; set; }
    public bool Installed => InstallRoot != null && Launcher != null;
    public string Current { get; set; } = global::WindowsGSM.Hosting.WgsmEnvironment.Version.TrimStart('v');

    public UpdateState State { get; private set; } = UpdateState.Idle;
    public int? Percent { get; private set; }
    public string? Error { get; private set; }
    public AppRelease? Latest { get; private set; }
    public DateTimeOffset? CheckedAt { get; private set; }
    public bool Available => Latest != null && Compare(Latest.Version, Current) > 0;

    /// <summary>The version to go back to, if the launcher kept one.</summary>
    public string? Previous
    {
        get
        {
            if (!Installed) { return null; }
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(InstallRoot!, "install.json")));
                return doc.RootElement.TryGetProperty("previous", out var p) && p.ValueKind == JsonValueKind.String && Directory.Exists(Path.Combine(InstallRoot!, "versions", p.GetString()!)) ? p.GetString() : null;
            }
            catch { return null; }
        }
    }

    /// <summary>Swappable for tests: the feed's releases.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<AppRelease>>>? FeedOverride { get; set; }
    /// <summary>Swappable for tests: how the switch is started once files are in place.</summary>
    public Action<string, bool>? HandOverOverride { get; set; }

    // ───────────────────────────── Checking ─────────────────────────────

    public async Task CheckAsync(bool quiet = false)
    {
        if (!await _busy.WaitAsync(0)) { return; }
        try
        {
            State = UpdateState.Checking;
            Error = null;
            var releases = FeedOverride != null ? await FeedOverride(CancellationToken.None) : await FetchAsync();
            bool pre = _ctx.Settings.UpdatePrerelease;
            Latest = releases.Where(r => pre || !r.Prerelease).OrderByDescending(r => r.Version, Comparer<string>.Create(Compare)).FirstOrDefault();
            CheckedAt = DateTimeOffset.UtcNow;
            State = UpdateState.Idle;
            if (Available && _announced != Latest!.Version)
            {
                _announced = Latest.Version;
                _notifications.Record("appUpdate", $"WindowsGSM {Latest.Version} is available",
                    $"This machine runs {Current}. {(Installed ? "Update from Agent settings → Updates." : "Download it from the releases page.")}",
                    _ctx.MachineId, null, null, Visibility.Admin);
            }
        }
        catch (Exception ex)
        {
            State = quiet ? UpdateState.Idle : UpdateState.Error;
            Error = ex is HttpRequestException or TaskCanceledException ? $"Couldn't reach the update feed: {ex.Message}" : ex.Message;
            CheckedAt = DateTimeOffset.UtcNow;
        }
        finally { _busy.Release(); }
    }

    private async Task<IReadOnlyList<AppRelease>> FetchAsync()
    {
        string repo = _ctx.Settings.UpdateRepo.Trim();
        if (repo.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || repo.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return await FetchManifestAsync(repo);
        }
        if (!Regex.IsMatch(repo, @"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")) { throw new InvalidOperationException("The update feed isn't a GitHub repository (owner/name)."); }
        using var doc = JsonDocument.Parse(await _http.GetStringAsync($"https://api.github.com/repos/{repo}/releases?per_page=30"));
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
    private async Task<IReadOnlyList<AppRelease>> FetchManifestAsync(string url)
    {
        var baseUri = new Uri(url);
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
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

    // ───────────────────────────── Installing ─────────────────────────────

    /// <summary>Downloads, verifies and unpacks the latest release, then hands over to the launcher. Returns an error or null.</summary>
    public async Task<string?> ApplyAsync(Func<Task> stopAgent)
    {
        if (!Installed) { return "This copy wasn't installed with setup, so it can't update itself."; }
        if (Latest == null || !Available) { return "There's no newer version to install."; }
        if (!await _busy.WaitAsync(0)) { return "An update is already in progress."; }
        var release = Latest;
        try
        {
            Error = null;
            State = UpdateState.Downloading;
            Percent = 0;
            string downloads = Path.Combine(InstallRoot!, "downloads");
            Directory.CreateDirectory(downloads);
            string zip = Path.Combine(downloads, $"WindowsGSM-{release.Version}.zip");
            await DownloadAsync(release.ZipUrl, zip, release.Size);

            // Refuse anything that doesn't match its published checksum.
            if (release.ShaUrl == null) { throw new InvalidOperationException("The release has no checksum (.sha256), so it can't be verified."); }
            string expected = (await _http.GetStringAsync(release.ShaUrl)).Trim().Split(' ', '\t', '\n')[0].ToLowerInvariant();
            string actual;
            await using (var fs = File.OpenRead(zip)) { actual = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant(); }
            if (actual != expected) { File.Delete(zip); throw new InvalidOperationException("The download didn't match its checksum — it was deleted. Try again."); }

            State = UpdateState.Installing;
            Percent = null;
            await Task.Run(() => Unpack(zip, release.Version));
            try { File.Delete(zip); } catch { /* next time */ }

            State = UpdateState.Restarting;
            _log($"Updating to {release.Version}: restarting the agent (game servers keep running).");
            HandOver($"--switch \"{release.Version}\"", stopAgent);
            return null;
        }
        catch (Exception ex)
        {
            State = UpdateState.Error;
            Error = ex.Message;
            _log($"Update failed: {ex.Message}");
            return ex.Message;
        }
        finally { _busy.Release(); }
    }

    public string? Rollback(Func<Task> stopAgent)
    {
        if (!Installed) { return "This copy wasn't installed with setup."; }
        if (Previous == null) { return "There's no previous version to go back to."; }
        State = UpdateState.Restarting;
        _log($"Going back to {Previous}: restarting the agent (game servers keep running).");
        HandOver("--rollback", stopAgent);
        return null;
    }

    private void HandOver(string what, Func<Task> stopAgent)
    {
        if (HandOverOverride != null) { HandOverOverride(what, true); return; }
        Process.Start(new ProcessStartInfo(Launcher!, $"{what} --wait-pid {Environment.ProcessId} --start-agent")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = InstallRoot!,
        });
        _ = Task.Run(async () => { await Task.Delay(1000); await stopAgent(); });
    }

    private async Task DownloadAsync(string url, string file, long size)
    {
        using var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        res.EnsureSuccessStatusCode();
        long total = res.Content.Headers.ContentLength ?? size;
        await using var src = await res.Content.ReadAsStreamAsync();
        await using var dst = File.Create(file + ".part");
        var buffer = new byte[1 << 16];
        long done = 0;
        int read;
        while ((read = await src.ReadAsync(buffer)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read));
            done += read;
            if (total > 0) { Percent = (int)(done * 100 / total); }
        }
        dst.Close();
        File.Move(file + ".part", file, overwrite: true);
    }

    /// <summary>
    /// Puts versions\&lt;version&gt;\ from the zip next to the running version (staged, then renamed into place) and
    /// refreshes the launcher if the release carries a newer one.
    /// </summary>
    public void Unpack(string zipFile, string version)
    {
        // The version names a folder (and goes on the launcher's command line): never trust a feed's spelling.
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^[0-9A-Za-z][0-9A-Za-z.+-]{0,39}$") || version.Contains(".."))
        {
            throw new InvalidOperationException($"\"{version}\" isn't a valid version.");
        }
        string versions = Path.Combine(InstallRoot!, "versions");
        string target = Path.Combine(versions, version), staging = target + ".partial";
        if (Directory.Exists(staging)) { Directory.Delete(staging, true); }
        Directory.CreateDirectory(staging);
        string prefix = $"versions/{version}/";
        byte[]? launcher = null;
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
                    launcher = ms.ToArray();
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
        if (launcher != null && Launcher != null && !File.ReadAllBytes(Launcher).AsSpan().SequenceEqual(launcher))
        {
            // Old copies from earlier updates go when they can; one that's still running gets a fresh name.
            foreach (string stale in Directory.EnumerateFiles(Path.GetDirectoryName(Launcher)!, Path.GetFileName(Launcher) + ".*old"))
            {
                try { File.Delete(stale); } catch { /* still running; next time */ }
            }
            string old = Launcher + ".old";
            if (File.Exists(old)) { old = $"{Launcher}.{DateTime.UtcNow:yyyyMMddHHmmss}.old"; }
            File.Move(Launcher, old);
            File.WriteAllBytes(Launcher, launcher);
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

    public void Dispose() => _timer.Dispose();
}
