using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using WindowsGSM.Agent.Api;
using WindowsGSM.Agent.Notifications;
using WindowsGSM.Agent.Realtime;

namespace WindowsGSM.Agent.Hosting;

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

    private Task<IReadOnlyList<AppRelease>> FetchAsync() => AppReleases.FetchAsync(_http, _ctx.Settings.UpdateRepo);

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
            // Refuses anything that doesn't match its published checksum.
            string zip = await AppReleases.DownloadAsync(_http, release, Path.Combine(InstallRoot!, "downloads"), p => Percent = p);

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

    /// <summary>
    /// Puts versions\&lt;version&gt;\ from the zip next to the running version (staged, then renamed into place) and
    /// refreshes the launcher if the release carries a newer one.
    /// </summary>
    public void Unpack(string zipFile, string version) => AppReleases.Unpack(InstallRoot!, Launcher, zipFile, version);

    /// <summary>Semantic-ish order: "2.0.10" &gt; "2.0.9"; a release beats its pre-releases.</summary>
    public static int Compare(string a, string b) => AppReleases.Compare(a, b);

    public void Dispose() => _timer.Dispose();
}
