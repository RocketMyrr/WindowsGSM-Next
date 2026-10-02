using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using WindowsGSM.Agent.Hosting;
using WindowsGSM.Agent.Notifications;

namespace WindowsGSM.Agent.Tests;

/// <summary>Phase 7: the data-folder dry run and self-updating (download, verify, unpack, hand over).</summary>
[Collection("Agent")]
public class ReleaseTests
{
    private readonly AgentFixture _f;
    public ReleaseTests(AgentFixture f) => _f = f;

    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "wgsm-release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // ───────────────────────────── Dry run ─────────────────────────────

    [Fact]
    public async Task The_dry_run_explains_a_legacy_folder_and_changes_nothing()
    {
        string root = TempDir();
        try
        {
            void Server(string id, string game, string port, bool autostart = false)
            {
                Directory.CreateDirectory(Path.Combine(root, "servers", id, "configs"));
                Directory.CreateDirectory(Path.Combine(root, "servers", id, "serverfiles"));
                File.WriteAllText(Path.Combine(root, "servers", id, "configs", "WindowsGSM.cfg"),
                    $"servergame=\"{game}\"\nservername=\"Server {id}\"\nserverport=\"{port}\"\nautostart=\"{(autostart ? 1 : 0)}\"\n");
            }
            Server("1", "Rust Dedicated Server", "28015", autostart: true);
            Server("2", "Mystery Game [Mystery.cs]", "28015");
            Directory.CreateDirectory(Path.Combine(root, "configs", "webdashboard"));
            File.WriteAllText(Path.Combine(root, "configs", "webdashboard", "users.json"), "[{\"Username\":\"a\"},{\"Username\":\"b\"}]");
            Directory.CreateDirectory(Path.Combine(root, "configs", "discordbot"));
            File.WriteAllText(Path.Combine(root, "configs", "discordbot", "token.txt"), "x");
            File.WriteAllText(Path.Combine(root, "configs", "discordbot", "adminIDs.txt"), "1|A|0\n");
            var before = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray();

            var report = await DataCheck.RunAsync(root);

            Assert.True(report.CanAdopt);
            Assert.Equal(2, report.Servers);
            Assert.Contains(report.Findings, x => x.Area == "Servers" && x.Status == "warn" && x.Title.Contains("#2") && x.Title.Contains("isn't available"));
            Assert.Contains(report.Findings, x => x.Title.Contains("Port 28015") && x.Status == "warn");
            Assert.Contains(report.Findings, x => x.Title.Contains("#1") && x.Title.Contains("starts automatically"));
            Assert.Contains(report.Findings, x => x.Area == "Accounts" && x.Title.StartsWith("2 web dashboard accounts"));
            Assert.Contains(report.Findings, x => x.Area == "Discord" && x.Title.Contains("1 admin"));
            Assert.Contains("Nothing was changed.", DataCheck.Format(report));
            Assert.Equal(before, Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToArray());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task A_missing_folder_blocks()
    {
        var report = await DataCheck.RunAsync(Path.Combine(Path.GetTempPath(), "wgsm-nope-" + Guid.NewGuid().ToString("N")));
        Assert.False(report.CanAdopt);
    }

    // ───────────────────────────── Self-update ─────────────────────────────

    private sealed class FakeFeed : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Files = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(Files.TryGetValue(request.RequestUri!.ToString(), out var b)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static byte[] Package(string version, bool withLauncher = true)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(text); }
            if (withLauncher) { Add("WindowsGSM.exe", "new launcher " + version); }
            Add($"versions/{version}/wgsm-agent.exe", "agent " + version);
            Add($"versions/{version}/WindowsGSM.exe", "app " + version);
            Add($"versions/{version}/refs/System.Runtime.dll", "ref");
            Add("README.txt", "hi");
        }
        return ms.ToArray();
    }

    private (SelfUpdate Update, FakeFeed Feed, string Root, List<string> Handovers) Updater(string current = "2.0.0")
    {
        string root = TempDir();
        Directory.CreateDirectory(Path.Combine(root, "versions", current));
        File.WriteAllText(Path.Combine(root, "WindowsGSM.exe"), "old launcher");
        File.WriteAllText(Path.Combine(root, "install.json"), $"{{\"current\":\"{current}\",\"previous\":null,\"data\":\"x\"}}");
        var feed = new FakeFeed();
        var handovers = new List<string>();
        var update = new SelfUpdate(_f.Context, _f.App.Services.GetRequiredService<NotificationCentre>(), _ => { }, new HttpClient(feed))
        {
            InstallRoot = root,
            Launcher = Path.Combine(root, "WindowsGSM.exe"),
            Current = current,
            HandOverOverride = (what, _) => handovers.Add(what),
        };
        return (update, feed, root, handovers);
    }

    [Fact]
    public async Task Storage_clean_up_never_offers_the_running_or_previous_version()
    {
        // The version string reads "v2.0.0-alpha.2"; the folder is "2.0.0-alpha.2". Only the older one may go.
        string running = global::WindowsGSM.Hosting.WgsmEnvironment.Version.Split('+')[0].TrimStart('v');
        var (update, _, root, _) = Updater(running);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "versions", "1.0.0"));
            Directory.CreateDirectory(Path.Combine(root, "versions", "0.9.0"));
            File.WriteAllText(Path.Combine(root, "install.json"), $"{{\"current\":\"{running}\",\"previous\":\"1.0.0\",\"data\":\"x\"}}");

            var report = await new DiskSpace(_f.Context, update).ReportAsync(fresh: true);
            var old = report.Cleanup.Single(c => c.Key == "old-versions");
            Assert.Equal(1, old.Count); // 0.9.0 only
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task An_update_is_downloaded_verified_unpacked_and_handed_to_the_launcher()
    {
        var (update, feed, root, handovers) = Updater();
        try
        {
            byte[] zip = Package("2.0.1");
            feed.Files["https://feed/WindowsGSM-2.0.1.zip"] = zip;
            feed.Files["https://feed/WindowsGSM-2.0.1.zip.sha256"] = Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant() + "  WindowsGSM-2.0.1.zip");
            update.FeedOverride = _ => Task.FromResult<IReadOnlyList<AppRelease>>(new[]
            {
                new AppRelease("2.0.1", "2.0.1", "Fixes", "https://feed/WindowsGSM-2.0.1.zip", "https://feed/WindowsGSM-2.0.1.zip.sha256", zip.Length, null, false),
                new AppRelease("2.1.0-beta.1", "beta", null, "https://feed/beta.zip", null, 1, null, true),
            });
            _f.Context.Settings.UpdatePrerelease = false;
            await update.CheckAsync();
            Assert.True(update.Available);
            Assert.Equal("2.0.1", update.Latest!.Version); // the pre-release is skipped when they're off

            Assert.Null(await update.ApplyAsync(() => Task.CompletedTask));
            Assert.Equal("agent 2.0.1", File.ReadAllText(Path.Combine(root, "versions", "2.0.1", "wgsm-agent.exe")));
            Assert.True(File.Exists(Path.Combine(root, "versions", "2.0.1", "refs", "System.Runtime.dll")));
            Assert.False(File.Exists(Path.Combine(root, "versions", "2.0.1", "README.txt")));
            Assert.Equal("new launcher 2.0.1", File.ReadAllText(Path.Combine(root, "WindowsGSM.exe")));
            Assert.Equal("old launcher", File.ReadAllText(Path.Combine(root, "WindowsGSM.exe.old")));
            Assert.Equal(new[] { "--switch \"2.0.1\"" }, handovers);
        }
        finally { _f.Context.Settings.UpdatePrerelease = true; Directory.Delete(root, true); }
    }

    [Fact]
    public async Task A_download_that_doesnt_match_its_checksum_is_refused()
    {
        var (update, feed, root, handovers) = Updater();
        try
        {
            feed.Files["https://feed/z.zip"] = Package("2.0.1");
            feed.Files["https://feed/z.sha256"] = Encoding.ASCII.GetBytes(new string('0', 64));
            update.FeedOverride = _ => Task.FromResult<IReadOnlyList<AppRelease>>(new[] { new AppRelease("2.0.1", "2.0.1", null, "https://feed/z.zip", "https://feed/z.sha256", 1, null, false) });
            await update.CheckAsync();
            string? error = await update.ApplyAsync(() => Task.CompletedTask);
            Assert.Contains("checksum", error);
            Assert.False(Directory.Exists(Path.Combine(root, "versions", "2.0.1")));
            Assert.Empty(handovers);
            Assert.Equal(SelfUpdate.UpdateState.Error, update.State);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Versions_sort_like_people_expect()
    {
        Assert.True(SelfUpdate.Compare("2.0.10", "2.0.9") > 0);
        Assert.True(SelfUpdate.Compare("2.1.0", "2.1.0-beta.2") > 0);
        Assert.True(SelfUpdate.Compare("2.0.0-alpha.10", "2.0.0-alpha.2") > 0);
        Assert.True(SelfUpdate.Compare("2.0.0-beta.1", "2.0.0-alpha.9") > 0);
        Assert.Equal(0, SelfUpdate.Compare("v2.0.0", "2.0.0"));
    }

    [Fact]
    public async Task Updates_are_owner_only_and_need_an_installed_copy()
    {
        var admin = await _f.UserAsync("updateadmin", Contracts.Role.Admin);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v2/machines/local/agent/update")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync("/api/v2/machines/local/agent/update/apply")).StatusCode);
        // The test agent wasn't installed with setup: it says so instead of trying.
        var res = await _f.Owner.PostAsync("/api/v2/machines/local/agent/update/apply");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("setup", (await ApiClient.Read<Contracts.ApiError>(res)).Error);
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Owner.PutAsync("/api/v2/machines/local/agent/update/settings", new { repo = "not a repo", prerelease = true })).StatusCode);
    }
}
