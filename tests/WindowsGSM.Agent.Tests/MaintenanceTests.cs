using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WindowsGSM.Agent.Api;
using WindowsGSM.Agent.Hosting;
using WindowsGSM.Agent.Security;
using WindowsGSM.Contracts;
using WindowsGSM.Core.Tests;
using WindowsGSM.Engine.Backups;
using WindowsGSM.Engine.Services;
using WindowsGSM.Functions;

namespace WindowsGSM.Agent.Tests;

/// <summary>Ports, firewall status, backups (second copy, test), password recovery, the desktop's stop, plugin roll-back.</summary>
[Collection("Agent")]
public class MaintenanceTests
{
    private readonly AgentFixture _f;
    public MaintenanceTests(AgentFixture f) => _f = f;

    private async Task<JobDto> Finished(string jobId)
    {
        JobDto job = null!;
        await EngineFixture.WaitUntil(() => (job = _f.Owner.GetJsonAsync<JobDto>($"/api/v2/machines/local/jobs/{jobId}").GetAwaiter().GetResult()).Status != "Running", "job " + jobId);
        return job;
    }

    [Fact]
    public async Task A_server_whose_game_port_is_taken_doesnt_start_and_says_why()
    {
        await _f.Owner.PostAsync(_f.ServerUrl("102", "/kill"));
        await EngineFixture.WaitUntil(() => _f.Engine.Servers.Get("102")!.State == WindowsGSM.Engine.Servers.ServerState.Stopped, "102 stopped");
        var before = PortCheck.Listening;
        PortCheck.Listening = () => new[] { new System.Net.IPEndPoint(System.Net.IPAddress.Any, EngineFixture.Port("102")) };
        try
        {
            var res = await _f.Owner.PostAsync(_f.ServerUrl("102", "/start"));
            var job = await Finished((await ApiClient.Read<JobAccepted>(res)).JobId);
            Assert.Equal("Failed", job.Status);
            Assert.Contains("already in use", job.Error);
            Assert.Equal(WindowsGSM.Engine.Servers.ServerState.Stopped, _f.Engine.Servers.Get("102")!.State);
        }
        finally { PortCheck.Listening = before; }
    }

    [Fact]
    public async Task Firewall_status_is_reported_for_a_server()
    {
        var r = await _f.Owner.GetJsonAsync<JsonElement>(_f.ServerUrl("101", "/firewall"));
        Assert.Contains(r.GetProperty("status").GetProperty("state").GetString(), new[] { "allowed", "missing", "blocked", "unknown" });
        // Changing Windows Firewall is for admins (the test never actually asks Windows).
        var viewer = await _f.UserAsync("fwviewer", Role.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/v2/machines/local/firewall")).StatusCode);
    }

    [Fact]
    public async Task Backups_are_copied_to_a_second_place_and_can_be_tested()
    {
        string second = Path.Combine(Path.GetTempPath(), "wgsm-second-" + Guid.NewGuid().ToString("N"));
        var settings = BackupSettings.Load("103");
        string oldCopy = settings.CopyTo;
        try
        {
            // Admin-only, and it must be a full path.
            var op = await _f.UserAsync("backupop2", Role.Operator);
            var dto = new BackupSettingsDto(settings.Paths, settings.ExternalLocations, settings.BeforeStart, settings.KeepCount, settings.KeepDays, settings.Location, second);
            Assert.Equal(HttpStatusCode.Forbidden, (await op.PutAsync(_f.ServerUrl("103", "/backups/settings"), dto)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await _f.Owner.PutAsync(_f.ServerUrl("103", "/backups/settings"), dto with { CopyTo = "relative\\folder" })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await _f.Owner.PutAsync(_f.ServerUrl("103", "/backups/settings"), dto)).StatusCode);

            File.WriteAllText(ServerPath.GetServersServerFiles("103", "world2.dat"), "world");
            var res = await _f.Owner.PostAsync(_f.ServerUrl("103", "/backups"), new BackupRequest());
            Assert.Equal("Succeeded", (await Finished((await ApiClient.Read<JobAccepted>(res)).JobId)).Status);
            var latest = (await _f.Owner.GetJsonAsync<List<BackupDto>>(_f.ServerUrl("103", "/backups"))).OrderByDescending(b => b.Created).First();
            Assert.True(File.Exists(Path.Combine(second, latest.Name)), "copied to the second location");

            // Testing a good backup passes…
            var test = await _f.Owner.PostAsync(_f.ServerUrl("103", $"/backups/{latest.Name}/test"));
            Assert.Equal("Succeeded", (await Finished((await ApiClient.Read<JobAccepted>(test)).JobId)).Status);

            // …and a damaged one fails: flip a byte inside a stored file's data.
            string path = _f.Engine.Backups.ResolveArchive("103", latest.Name)!;
            string damaged = Path.Combine(Path.GetDirectoryName(path)!, "wgsm-103-20200101-000000.zip");
            using (var zip = ZipFile.Open(damaged, ZipArchiveMode.Create))
            {
                var e = zip.CreateEntry("serverfiles/big.dat", CompressionLevel.NoCompression);
                using var w = e.Open();
                w.Write(Encoding.ASCII.GetBytes(new string('x', 4096)));
            }
            byte[] bytes = File.ReadAllBytes(damaged);
            int at = Encoding.ASCII.GetString(bytes).IndexOf("xxxx", StringComparison.Ordinal);
            bytes[at + 100] = (byte)'y';
            File.WriteAllBytes(damaged, bytes);
            var bad = await _f.Owner.PostAsync(_f.ServerUrl("103", "/backups/wgsm-103-20200101-000000.zip/test"));
            var badJob = await Finished((await ApiClient.Read<JobAccepted>(bad)).JobId);
            Assert.Equal("Failed", badJob.Status);
            Assert.True(badJob.Error!.Contains("damaged") || badJob.Error.Contains("can't be read"), badJob.Error);
            WindowsGSM.Core.Tests.TestData.DeleteFile(damaged);
        }
        finally
        {
            var s = BackupSettings.Load("103");
            s.CopyTo = oldCopy;
            s.Save();
            if (Directory.Exists(second)) { WindowsGSM.Core.Tests.TestData.DeleteDirectory(second); }
        }
    }

    [Fact]
    public void A_forgotten_password_can_be_reset_on_the_machine()
    {
        string root = Path.Combine(Path.GetTempPath(), "wgsm-reset-" + Guid.NewGuid().ToString("N"));
        string config = Path.Combine(root, "configs", "next");
        try
        {
            Assert.False(PasswordReset.Run(root, "boss", false).Ok); // no data folder

            var users = new UserStore(config);
            Assert.Null(users.Create("boss", "the-old-password-1", Role.Owner, enabled: true, grants: null));
            new SessionStore(config, TimeSpan.FromHours(1)).Create("boss", null, null);

            var missing = PasswordReset.Run(root, "nobody", false);
            Assert.False(missing.Ok);
            Assert.Contains("boss", missing.Message);

            var result = PasswordReset.Run(root, "boss", disableTwoFactor: true);
            Assert.True(result.Ok, result.Message);
            string password = result.Message.Split("  ")[1].Split('\n')[0].Trim();

            var after = new UserStore(config);
            Assert.Equal(LoginOutcome.Ok, after.Validate("boss", password, null, out _));
            Assert.NotEqual(LoginOutcome.Ok, after.Validate("boss", "the-old-password-1", null, out _));
            Assert.Empty(new SessionStore(config, TimeSpan.FromHours(1)).ForUser("boss"));

            // Not while WindowsGSM runs on the folder.
            using (WindowsGSM.Hosting.DataRootLock.Acquire(root)) { Assert.Contains("Stop it first", PasswordReset.Run(root, "boss", false).Message); }
        }
        finally { if (Directory.Exists(root)) { WindowsGSM.Core.Tests.TestData.DeleteDirectory(root); } }
    }

    [Fact]
    public async Task Stop_everything_is_only_for_the_desktop_app_on_this_computer()
    {
        // The test server's clients aren't loopback, and none has the key — nothing is stopped.
        var res = await _f.Owner.PostAsync("/api/v2/local/stop-everything");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var withWrongKey = _f.NewClient();
        withWrongKey.Http.DefaultRequestHeaders.Add(LocalEndpoints.KeyHeader, "0000");
        Assert.Equal(HttpStatusCode.Forbidden, (await withWrongKey.PostAsync("/api/v2/local/stop-everything")).StatusCode);
        Assert.True(File.Exists(Path.Combine(WindowsGSM.Hosting.WgsmEnvironment.DataRoot, "configs", "next", LocalEndpoints.KeyFile)));
    }

    private const string Source = """
        using System.Diagnostics;
        using System.Threading.Tasks;
        using WindowsGSM.Functions;

        namespace WindowsGSM.Plugins
        {
            public class RollbackGame
            {
                public Plugin Plugin = new Plugin { name = "WindowsGSM.RollbackGame", author = "someone", description = "x", version = "VERSION", url = "", color = "#ffffff" };
                private readonly ServerConfig _serverData;
                public string Error, Notice;
                public string FullName = "Rollback Game Dedicated Server";
                public string StartPath = "";
                public bool AllowsEmbedConsole = true;
                public int PortIncrements = 1;
                public object QueryMethod = null;
                public string Port = "41000", QueryPort = "41001", Defaultmap = "", Maxplayers = "4", Additional = "";
                public RollbackGame(ServerConfig serverData) { _serverData = serverData; }
                public async Task<Process> Start() => null;
                public async Task Stop(Process p) { }
                public async Task<Process> Install() => null;
                public bool IsInstallValid() => true;
                public bool IsImportValid(string path) => true;
                public async Task<Process> Update(bool validate = false, string custom = null) => null;
            }
        }
        """;

    [Fact]
    public async Task A_plugin_update_that_breaks_can_be_rolled_back()
    {
        async Task<JsonElement> Upload(string source)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(source)), "file", "RollbackGame.cs");
            return await ApiClient.Read<JsonElement>(await _f.Owner.Http.PostAsync("/api/v2/machines/local/plugins/upload", form));
        }
        try
        {
            Assert.True((await Upload(Source.Replace("VERSION", "1.0"))).GetProperty("loaded").GetBoolean());
            var broken = await Upload(Source.Replace("VERSION", "2.0").Replace("=> true;", "=> true"));
            Assert.False(broken.GetProperty("loaded").GetBoolean());
            Assert.True(broken.GetProperty("hasPrevious").GetBoolean());

            var viewer = await _f.UserAsync("pluginviewer", Role.Admin);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/v2/machines/local/plugins/RollbackGame.cs/previous")).StatusCode);

            var back = await ApiClient.Read<JsonElement>(await _f.Owner.PostAsync("/api/v2/machines/local/plugins/RollbackGame.cs/previous"));
            Assert.True(back.GetProperty("loaded").GetBoolean(), back.GetProperty("error").ToString());
            Assert.Equal("1.0", back.GetProperty("version").GetString());
            Assert.True(back.GetProperty("hasPrevious").GetBoolean()); // the broken one, to switch again
        }
        finally
        {
            foreach (string dir in new[] { ServerPath.GetPlugins("RollbackGame.cs"), PluginStore.PreviousPath("RollbackGame.cs") })
            {
                if (Directory.Exists(dir)) { WindowsGSM.Core.Tests.TestData.DeleteDirectory(dir); }
            }
            await _f.Engine.Plugins.LoadAsync();
        }
    }
}
