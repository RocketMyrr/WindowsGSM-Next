using System.Net;
using System.Text.Json;
using WindowsGSM.Agent.Hub;
using WindowsGSM.Contracts;
using WindowsGSM.Core.Tests;
using WindowsGSM.Functions;

namespace WindowsGSM.Agent.Tests;

/// <summary>Copying and moving servers, config history, and storage clean-up.</summary>
[Collection("Agent")]
public class ServerToolsTests
{
    private readonly AgentFixture _f;
    public ServerToolsTests(AgentFixture f) => _f = f;

    private async Task<JobDto> Finished(string jobId)
    {
        JobDto job = null!;
        await EngineFixture.WaitUntil(() => (job = _f.Owner.GetJsonAsync<JobDto>($"/api/v2/machines/local/jobs/{jobId}").GetAwaiter().GetResult()).Status != "Running", "job " + jobId, 60000);
        return job;
    }

    private async Task Stopped(string id)
    {
        await _f.Owner.PostAsync(_f.ServerUrl(id, "/kill"));
        await EngineFixture.WaitUntil(() => _f.Engine.Servers.Get(id)!.State == WindowsGSM.Engine.Servers.ServerState.Stopped, id + " stopped");
    }

    private async Task DeleteServer(string id)
    {
        var res = await _f.Owner.DeleteAsync(_f.ServerUrl(id));
        if (res.StatusCode == HttpStatusCode.Accepted) { await Finished((await ApiClient.Read<JobAccepted>(res)).JobId); }
    }

    [Fact]
    public async Task A_stopped_server_is_copied_with_its_files_on_new_ports()
    {
        await Stopped("103");
        File.WriteAllText(ServerPath.GetServersServerFiles("103", "copy-me.txt"), "world data");
        var viewer = await _f.UserAsync("cloneviewer", Role.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync(_f.ServerUrl("103", "/clone"), new { name = "Nope" })).StatusCode);

        var res = await _f.Owner.PostAsync(_f.ServerUrl("103", "/clone"), new { name = "Copy of 103" });
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var job = await Finished((await ApiClient.Read<JobAccepted>(res)).JobId);
        Assert.Equal("Succeeded", job.Status);
        var copy = _f.Engine.Servers.All.Single(s => s.Name == "Copy of 103");
        try
        {
            var original = _f.Engine.Servers.Get("103")!;
            Assert.Equal(original.Game, copy.Game);
            Assert.NotEqual(original.Config.ServerPort, copy.Config.ServerPort);
            Assert.False(copy.Config.AutoStart);
            Assert.Equal("world data", File.ReadAllText(ServerPath.GetServersServerFiles(copy.Id, "copy-me.txt")));
            Assert.False(Directory.Exists(ServerPath.GetServersConfigs(copy.Id, "history"))); // its own history starts empty
        }
        finally { await DeleteServer(copy.Id); }
    }

    [Fact]
    public async Task A_server_moves_to_another_machine_through_the_hub()
    {
        await Stopped("103");
        File.WriteAllText(ServerPath.GetServersServerFiles("103", "move-me.txt"), new string('x', 20_000));
        // The test "remote" machine replays against this same agent, so the moved server lands here too.
        var code = await ApiClient.Read<JsonElement>(await _f.Owner.PostAsync("/api/v2/hub/pairing-codes"));
        var paired = await _f.NewClient().PostAsync("/api/v2/hub/pair", new { code = code.GetProperty("code").GetString(), machineId = "m-move", machineName = "Move Box", version = "test" });
        string credential = (await ApiClient.Read<JsonElement>(paired)).GetProperty("credential").GetString()!;
        var settings = new AgentSettings { MachineName = "Move Box", HubUrl = "http://localhost", HubCredential = credential, HubName = "Test hub" };
        var link = new MemberLink(_f.Context, () => new HttpClient(_f.Server.CreateHandler()) { BaseAddress = _f.Server.BaseAddress }, async (uri, auth, _, token) =>
        {
            var ws = _f.Server.CreateWebSocketClient();
            ws.ConfigureRequest = r => r.Headers.Authorization = auth;
            return await ws.ConnectAsync(new Uri("ws://localhost/api/v2/hub/link"), token);
        }, _ => { }, "m-move", settings);
        link.Start();
        try
        {
            await EngineFixture.WaitUntil(() => _f.Owner.GetJsonAsync<List<MachineDto>>("/api/v2/machines").GetAwaiter().GetResult().Any(m => m.Id == "m-move" && m.Online), "the machine to come online");

            Assert.Equal(HttpStatusCode.BadRequest, (await _f.Owner.PostAsync("/api/v2/move", new { sourceMachine = "m-move", serverId = "103", targetMachine = "m-move", deleteOriginal = false })).StatusCode);
            var res = await _f.Owner.PostAsync("/api/v2/move", new { sourceMachine = _f.Context.MachineId, serverId = "103", targetMachine = "m-move", name = "Moved 103", deleteOriginal = false });
            Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
            var job = await Finished((await ApiClient.Read<JobAccepted>(res)).JobId);
            Assert.True(job.Status == "Succeeded", job.Error);

            var moved = _f.Engine.Servers.All.Single(s => s.Name == "Moved 103");
            Assert.Equal(new string('x', 20_000), File.ReadAllText(ServerPath.GetServersServerFiles(moved.Id, "move-me.txt")));
            Assert.NotNull(_f.Engine.Servers.Get("103")); // kept: deleteOriginal was off
            Assert.Empty(Directory.Exists(Path.Combine(WindowsGSM.Hosting.WgsmEnvironment.DataRoot, "cache", "transfers"))
                ? Directory.GetFiles(Path.Combine(WindowsGSM.Hosting.WgsmEnvironment.DataRoot, "cache", "transfers"), "export-*") : Array.Empty<string>());
            await DeleteServer(moved.Id);
        }
        finally
        {
            await link.StopAsync();
            await _f.Owner.DeleteAsync("/api/v2/hub/machines/m-move");
        }
    }

    [Fact]
    public async Task Settings_changes_keep_history_that_can_be_compared_and_put_back()
    {
        string before = _f.Engine.Servers.Get("102")!.Config.ServerName;
        Assert.Equal(HttpStatusCode.NoContent, (await _f.Owner.PatchAsync(_f.ServerUrl("102", "/settings"), new SettingsUpdateRequest(new Dictionary<string, string?> { ["servername"] = "Renamed for history" }))).StatusCode);

        var versions = await _f.Owner.GetJsonAsync<List<JsonElement>>(_f.ServerUrl("102", "/config-history?path=settings"));
        var v = versions.First();
        Assert.Equal("Settings", v.GetProperty("why").GetString());
        Assert.Equal("owner", v.GetProperty("by").GetString());

        var detail = await _f.Owner.GetJsonAsync<JsonElement>(_f.ServerUrl("102", $"/config-history/{v.GetProperty("id").GetString()}"));
        Assert.Contains($"servername=\"{before}\"", detail.GetProperty("content").GetString());
        Assert.Contains("servername=\"Renamed for history\"", detail.GetProperty("current").GetString());

        var viewer = await _f.UserAsync("histviewer", Role.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(_f.ServerUrl("102", "/config-history"))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _f.Owner.PostAsync(_f.ServerUrl("102", $"/config-history/{v.GetProperty("id").GetString()}/restore"))).StatusCode);
        Assert.Equal(before, new ServerConfig("102").ServerName);
        // Putting back kept the renamed version too — so it can be undone.
        var after = await _f.Owner.GetJsonAsync<List<JsonElement>>(_f.ServerUrl("102", "/config-history?path=settings"));
        Assert.Equal("Put back", after.First().GetProperty("why").GetString());
    }

    [Fact]
    public async Task Edited_files_keep_their_earlier_versions()
    {
        string rel = "history-test.cfg";
        File.WriteAllText(ServerPath.GetServersServerFiles("101", rel), "maxplayers=10\n");
        Assert.Equal(HttpStatusCode.NoContent, (await _f.Owner.PutAsync(_f.ServerUrl("101", "/files/content"), new FileWriteRequest(rel, "maxplayers=20\n", null))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _f.Owner.PutAsync(_f.ServerUrl("101", "/files/content"), new FileWriteRequest(rel, "maxplayers=20\n", null))).StatusCode);
        var versions = await _f.Owner.GetJsonAsync<List<JsonElement>>(_f.ServerUrl("101", "/config-history?path=" + rel));
        var only = Assert.Single(versions); // saving without a change keeps nothing
        var detail = await _f.Owner.GetJsonAsync<JsonElement>(_f.ServerUrl("101", $"/config-history/{only.GetProperty("id").GetString()}"));
        Assert.Equal("maxplayers=10\n", detail.GetProperty("content").GetString());
        File.Delete(ServerPath.GetServersServerFiles("101", rel));
    }

    [Fact]
    public async Task Storage_shows_usage_and_cleans_up_only_what_it_lists()
    {
        string logs = Path.Combine(WindowsGSM.Hosting.WgsmEnvironment.DataRoot, "logs");
        Directory.CreateDirectory(logs);
        string old = Path.Combine(logs, "L20000101.log"), recent = Path.Combine(logs, $"L{DateTime.Now:yyyyMMdd}.log");
        File.WriteAllText(old, "old log");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-90));
        if (!File.Exists(recent)) { File.WriteAllText(recent, "today"); }

        var viewer = await _f.UserAsync("diskviewer", Role.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v2/machines/local/disk")).StatusCode);

        var report = await _f.Owner.GetJsonAsync<JsonElement>("/api/v2/machines/local/disk?fresh=true");
        Assert.Contains(report.GetProperty("servers").EnumerateArray(), s => s.GetProperty("id").GetString() == "101");
        var oldLogs = report.GetProperty("cleanup").EnumerateArray().Single(c => c.GetProperty("key").GetString() == "old-logs");
        Assert.True(oldLogs.GetProperty("count").GetInt32() >= 1);

        var res = await ApiClient.Read<JsonElement>(await _f.Owner.PostAsync("/api/v2/machines/local/disk/cleanup", new { keys = new[] { "old-logs" } }));
        Assert.True(res.GetProperty("freed").GetInt64() > 0);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }
}
