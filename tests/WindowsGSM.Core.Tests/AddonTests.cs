using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Services;
using WindowsGSM.Functions;
using WindowsGSM.Hosting;

namespace WindowsGSM.Core.Tests;

/// <summary>Custom add-ons end to end (served from a local HTTP server), built-in tracking, update-on-start.</summary>
[Collection("Lifecycle")]
public class AddonTests
{
    private static async Task<JobSnapshot> Run(OperationRequest request)
    {
        Assert.True(request.Accepted, request.Error);
        return await request.Job!.Completion.WaitAsync(TimeSpan.FromSeconds(60));
    }

    /// <summary>Serves whatever <see cref="Zip"/> currently holds at any URL. No admin/URL ACL needed.</summary>
    // The zip server below runs on this PC, which custom add-on downloads normally refuse.
    static AddonTests() => WindowsGSM.Functions.Http.AllowThisPcForTests = true;

    [Fact]
    public async Task Custom_add_on_urls_cannot_reach_this_pc_or_link_local_addresses()
    {
        Assert.True(WindowsGSM.Functions.Http.Blocked(System.Net.IPAddress.Parse("127.0.0.1")));
        Assert.True(WindowsGSM.Functions.Http.Blocked(System.Net.IPAddress.Parse("169.254.169.254"))); // cloud metadata
        Assert.True(WindowsGSM.Functions.Http.Blocked(System.Net.IPAddress.Parse("::1")));
        Assert.True(WindowsGSM.Functions.Http.Blocked(System.Net.IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.True(WindowsGSM.Functions.Http.Blocked(System.Net.IPAddress.Parse("0.0.0.0")));
        Assert.True(WindowsGSM.Functions.Http.Blocked(System.Net.IPAddress.Parse("fe80::1")));
        Assert.False(WindowsGSM.Functions.Http.Blocked(System.Net.IPAddress.Parse("192.168.1.20"))); // a NAS on your network is fine
        Assert.False(WindowsGSM.Functions.Http.Blocked(System.Net.IPAddress.Parse("93.184.216.34")));

        using var server = new ZipServer();
        bool was = WindowsGSM.Functions.Http.AllowThisPcForTests;
        try
        {
            WindowsGSM.Functions.Http.AllowThisPcForTests = false;
            string file = Path.Combine(Path.GetTempPath(), "wgsm-blocked-" + Guid.NewGuid().ToString("N"));
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => WindowsGSM.Functions.Http.DownloadUserUrlAsync(server.Url, file));
            Assert.Contains("aren't allowed", ex.ToString());
            Assert.False(File.Exists(file) && new FileInfo(file).Length > 0);
            TestData.DeleteFile(file);
        }
        finally { WindowsGSM.Functions.Http.AllowThisPcForTests = was; }
    }

    private sealed class ZipServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        public byte[] Zip { get; set; } = Array.Empty<byte>();
        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/addon.zip";

        public ZipServer()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_stop.Token); } catch { return; }
                    using (client)
                    {
                        var stream = client.GetStream();
                        var buffer = new byte[4096];
                        if (await stream.ReadAsync(buffer) == 0) { continue; } // request line + headers; content doesn't matter
                        byte[] body = Zip;
                        byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/zip\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(head);
                        await stream.WriteAsync(body);
                    }
                }
            });
        }

        public void Dispose() { _stop.Cancel(); _listener.Stop(); }
    }

    private static byte[] MakeZip(params (string name, string content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    private static string Files(string id, string rel) => Path.Combine(ServerPath.GetServersServerFiles(id), rel);

    private static void ClearAddonState(string id)
    {
        foreach (var f in new[] { "customaddons.json", "installedaddons.json" })
        {
            string p = ServerPath.GetServersConfigs(id, f);
            if (File.Exists(p)) { TestData.DeleteFile(p); }
        }
    }

    [Fact]
    public async Task Custom_addon_installs_into_a_subfolder_and_ignores_escaping_entries()
    {
        EngineFixture.CreateServer("211");
        ClearAddonState("211");
        using var server = new ZipServer { Zip = MakeZip(("plugins/Kits.cs", "v1"), ("../../escaped.txt", "evil")) };
        using var engine = await EngineFixture.StartEngineAsync();

        var result = await Run(engine.Addons.InstallCustom("211", "Kits", server.Url, "oxide"));

        Assert.Equal(JobStatus.Succeeded, result.Status);
        Assert.Equal("v1", File.ReadAllText(Files("211", "oxide/plugins/Kits.cs")));
        Assert.False(File.Exists(Path.Combine(ServerPath.GetServers("211"), "escaped.txt")));
        Assert.False(File.Exists(Files("211", "escaped.txt")));
        var saved = Assert.Single(engine.Addons.ListCustom("211"));
        Assert.Equal(("Kits", "oxide"), (saved.Name, saved.Subfolder));
    }

    [Fact]
    public async Task Custom_addon_requests_are_validated_up_front()
    {
        EngineFixture.CreateServer("212");
        using var engine = await EngineFixture.StartEngineAsync();

        Assert.Contains("http", engine.Addons.InstallCustom("212", "x", "ftp://example.com/a.zip", "").Error);
        Assert.Contains("inside the server files", engine.Addons.InstallCustom("212", "x", "https://example.com/a.zip", "..\\..\\Windows").Error);
        Assert.False(engine.Addons.Install("212", "oxide").Accepted); // Oxide doesn't apply to this game
    }

    [Fact]
    public async Task Update_addons_on_start_refreshes_saved_custom_addons_first()
    {
        EngineFixture.CreateServer("213", extraSettings: "updateaddonsonstart=\"1\"");
        ClearAddonState("213");
        using var server = new ZipServer { Zip = MakeZip(("mod.txt", "v1")) };
        using var engine = await EngineFixture.StartEngineAsync();
        await Run(engine.Addons.InstallCustom("213", "Mod", server.Url, ""));
        Assert.Equal("v1", File.ReadAllText(Files("213", "mod.txt")));

        server.Zip = MakeZip(("mod.txt", "v2")); // a new release of the add-on
        var s = engine.Servers.Get("213")!;
        try
        {
            await Run(engine.Lifecycle.Start("213"));
            Assert.Equal("v2", File.ReadAllText(Files("213", "mod.txt")));
        }
        finally { EngineFixture.KillQuietly(s.Process); }
    }

    [Fact]
    public async Task Builtin_addons_apply_per_game_and_can_be_adopted_without_reinstalling()
    {
        // A Rust server: Oxide and Carbon apply; others don't.
        _ = TestData.DataRoot;
        string id = "214";
        string configs = ServerPath.GetServersConfigs(id);
        Directory.CreateDirectory(configs);
        Directory.CreateDirectory(ServerPath.GetServersServerFiles(id));
        File.WriteAllLines(Path.Combine(configs, "WindowsGSM.cfg"), new[] { "servergame=\"Rust Dedicated Server\"", "servername=\"Rusty\"" });
        ClearAddonState(id);
        // Oxide already on disk (e.g. installed by hand or as a custom add-on).
        string oxideDll = Files(id, @"RustDedicated_Data\Managed\Oxide.Core.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(oxideDll)!);
        File.WriteAllText(oxideDll, "x");
        using var engine = await EngineFixture.StartEngineAsync();

        var list = engine.Addons.List(id);
        Assert.Equal(new[] { "oxide", "carbon" }, list.Select(a => a.Key));
        Assert.Equal((true, false), (list[0].Present, list[0].Managed)); // present but not WindowsGSM's → never auto-updated

        Assert.True(engine.Addons.SetManaged(id, "oxide", true));
        Assert.True(engine.Addons.List(id)[0].Managed);
        Assert.False(engine.Addons.SetManaged(id, "not-an-addon", true));
    }

    /// <summary>An empty Rust server (Oxide and Carbon apply to it).</summary>
    private static string RustServer(string id)
    {
        _ = TestData.DataRoot;
        string configs = ServerPath.GetServersConfigs(id);
        Directory.CreateDirectory(configs);
        string files = ServerPath.GetServersServerFiles(id);
        if (Directory.Exists(files)) { Directory.Delete(files, true); }
        Directory.CreateDirectory(files);
        File.WriteAllLines(Path.Combine(configs, "WindowsGSM.cfg"), new[] { "servergame=\"Rust Dedicated Server\"", "servername=\"Rusty\"" });
        ClearAddonState(id);
        return id;
    }

    private static void Put(string id, string relative, string text = "x")
    {
        string path = Files(id, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public async Task Oxide_and_Carbon_cannot_both_go_on_one_server_and_Carbon_can_be_removed()
    {
        string id = RustServer("156");
        Put(id, @"carbon\managed\Carbon.dll");
        Put(id, "winhttp.dll");
        Put(id, "doorstop_config.ini");
        Put(id, @"carbon\plugins\Kits.cs", "mine");
        using var engine = await EngineFixture.StartEngineAsync();

        var oxide = engine.Addons.List(id).Single(a => a.Key == "oxide");
        Assert.Equal("Carbon", oxide.ConflictsWith);
        var refused = engine.Addons.Install(id, "oxide");
        Assert.False(refused.Accepted);
        Assert.Contains("Remove Carbon first", refused.Error);
        Assert.True(engine.Addons.List(id).Single(a => a.Key == "carbon").Removable);

        InstalledAddonStore.Mark(id, "carbon");
        Assert.Equal(JobStatus.Succeeded, (await Run(engine.Addons.Remove(id, "carbon"))).Status);
        Assert.False(File.Exists(Files(id, "winhttp.dll")));
        Assert.False(File.Exists(Files(id, @"carbon\managed\Carbon.dll")));
        Assert.Equal("mine", File.ReadAllText(Files(id, @"carbon\plugins\Kits.cs"))); // plugins stay
        Assert.False(InstalledAddonStore.Has(id, "carbon"));
        Assert.Null(engine.Addons.List(id).Single(a => a.Key == "oxide").ConflictsWith);
        Assert.False(engine.Addons.Remove(id, "carbon").Accepted); // already gone
    }

    [Fact]
    public async Task A_custom_Oxide_build_takes_over_from_the_built_in_one()
    {
        string id = RustServer("157");
        Put(id, @"RustDedicated_Data\Managed\Oxide.Core.dll");
        InstalledAddonStore.Mark(id, "oxide");
        using var server = new ZipServer { Zip = MakeZip((@"RustDedicated_Data/Managed/Oxide.Core.dll", "staging")) };
        // The link names the Oxide staging artifact; the query string keeps it on the local test server.
        string url = server.Url + "?u=https://downloads.oxidemod.com/artifacts/Oxide.Rust/staging/Oxide.Rust.zip";
        CustomAddonStore.Upsert(id, "staging", url, "");
        using var engine = await EngineFixture.StartEngineAsync();

        var oxide = engine.Addons.List(id).Single(a => a.Key == "oxide");
        Assert.Equal("staging", oxide.ProvidedBy);
        Assert.False(oxide.UpdateAvailable);

        // Update add-ons skips the release (no download from GitHub) and installs the staging build.
        var s = engine.Servers.Get(id)!;
        var job = engine.Jobs.Start("addon", id, "test", async ctx => { await engine.Addons.UpdateAllCoreAsync(s, ctx, ""); return null; });
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("staging", File.ReadAllText(Files(id, @"RustDedicated_Data\Managed\Oxide.Core.dll")));
        Assert.Contains(engine.Log.Tail(id, 50), line => line.Contains("skipped, the custom add-on 'staging' installs it"));

        // A Rust update puts Rust's own files back; right after, the Oxide that was there goes back on.
        File.WriteAllText(Files(id, @"RustDedicated_Data\Managed\Oxide.Core.dll"), "overwritten by the update");
        var after = engine.Jobs.Start("update", id, "test", async ctx => { await engine.Addons.ReapplyAfterGameUpdateAsync(s, ctx); return null; });
        await after.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("staging", File.ReadAllText(Files(id, @"RustDedicated_Data\Managed\Oxide.Core.dll")));
        Assert.Contains(engine.Log.Tail(id, 50), line => line.Contains("put back after the game update"));
    }

    [Fact]
    public async Task Custom_add_ons_can_be_edited()
    {
        EngineFixture.CreateServer("158");
        ClearAddonState("158");
        CustomAddonStore.Upsert("158", "Pack", "https://example.com/a.zip", "");
        CustomAddonStore.Upsert("158", "Other", "https://example.com/b.zip", "");
        using var engine = await EngineFixture.StartEngineAsync();
        string addonId = engine.Addons.ListCustom("158").Single(c => c.Name == "Pack").Id;

        Assert.Null(engine.Addons.EditCustom("158", addonId, "Pack 2", "https://example.com/a2.zip", "oxide"));
        var edited = engine.Addons.ListCustom("158").Single(c => c.Id == addonId);
        Assert.Equal(("Pack 2", "https://example.com/a2.zip", "oxide"), (edited.Name, edited.Url, edited.Subfolder));

        Assert.Contains("http", engine.Addons.EditCustom("158", addonId, "x", "ftp://example.com/a.zip", ""));
        Assert.Contains("already uses", engine.Addons.EditCustom("158", addonId, "x", "https://example.com/b.zip", ""));
        Assert.NotNull(engine.Addons.EditCustom("158", "nope", "x", "https://example.com/c.zip", ""));
    }

    [Fact]
    public void Extracting_over_an_existing_install_replaces_its_files()
    {
        // SourceMod, AMX Mod X and DayZSAL used to extract with "fail if the file exists", so a reinstall changed nothing.
        string dir = Path.Combine(Path.GetTempPath(), "wgsm-extract-" + Guid.NewGuid().ToString("N"));
        string zip = dir + ".zip";
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "addons"));
            File.WriteAllText(Path.Combine(dir, "addons", "sm.dll"), "old");
            File.WriteAllBytes(zip, MakeZip(("addons/sm.dll", "new")));
            Assert.Equal(0, AddonService.ExtractSafely(zip, dir));
            Assert.Equal("new", File.ReadAllText(Path.Combine(dir, "addons", "sm.dll")));
        }
        finally
        {
            TestData.DeleteFile(zip);
            if (Directory.Exists(dir)) { Directory.Delete(dir, true); }
        }
    }
}
