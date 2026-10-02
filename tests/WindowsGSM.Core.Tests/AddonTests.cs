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
                        await stream.ReadAsync(buffer); // request line + headers; content doesn't matter
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
            if (File.Exists(p)) { File.Delete(p); }
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
}
