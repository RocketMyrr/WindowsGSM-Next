using System.Net;
using WindowsGSM.Contracts;
using WindowsGSM.Core.Tests;

namespace WindowsGSM.Agent.Tests;

/// <summary>Driving a real server through the API: power, console, jobs, players, settings, schedules.</summary>
[Collection("Agent")]
public class ServerApiTests
{
    private readonly AgentFixture _f;
    public ServerApiTests(AgentFixture f) => _f = f;

    private async Task<JobDto> WaitForJob(ApiClient c, string jobId)
    {
        JobDto job = null!;
        await EngineFixture.WaitUntil(() =>
        {
            job = c.GetJsonAsync<JobDto>($"/api/v2/machines/local/jobs/{jobId}").GetAwaiter().GetResult();
            return job.Status != "Running";
        }, $"job {jobId} to finish");
        return job;
    }

    [Fact]
    public async Task Start_console_players_and_stop_through_the_api()
    {
        var owner = _f.Owner;
        var start = await owner.PostAsync(_f.ServerUrl("101", "/start"));
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var accepted = await ApiClient.Read<JobAccepted>(start);
        Assert.Equal("Succeeded", (await WaitForJob(owner, accepted.JobId)).Status);
        try
        {
            var server = await owner.GetJsonAsync<ServerDto>(_f.ServerUrl("101"));
            Assert.Equal("Running", server.State);
            Assert.Equal(Capability.All, server.Can);

            // Starting twice is refused with the engine's reason, not an error page.
            var again = await owner.PostAsync(_f.ServerUrl("101", "/start"));
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Equal("rejected", (await ApiClient.Read<ApiError>(again)).Code);

            // Console: the first read returns everything, then only what's new.
            ConsoleDto console = null!;
            await EngineFixture.WaitUntil(() =>
            {
                console = owner.GetJsonAsync<ConsoleDto>(_f.ServerUrl("101", "/console")).GetAwaiter().GetResult();
                return console.Lines.Any(l => l.Contains("127.0.0.1"));
            }, "ping output in the console");
            Assert.True(console.Reset);
            var next = await owner.GetJsonAsync<ConsoleDto>(_f.ServerUrl("101", $"/console?since={console.Seq}&generation={console.Generation}"));
            Assert.False(next.Reset);

            // Players via the game's own query (the fake one answers 3/10).
            await _f.Engine.Monitor.TickAsync();
            await _f.Engine.Monitor.TickAsync();
            var players = await owner.GetJsonAsync<List<PlayerDto>>(_f.ServerUrl("101", "/players"));
            Assert.Equal(new[] { "Alice", "Bob", "Cara" }, players.Select(p => p.Name).ToArray());
            server = await owner.GetJsonAsync<ServerDto>(_f.ServerUrl("101"));
            Assert.Equal(3, server.Players);
            Assert.NotEmpty(await owner.GetJsonAsync<List<SampleDto>>(_f.ServerUrl("101", "/metrics")));

            var blank = await owner.PostAsync(_f.ServerUrl("101", "/console"), new CommandRequest("  "));
            Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        }
        finally
        {
            var stop = await owner.PostAsync(_f.ServerUrl("101", "/stop"));
            Assert.Equal(HttpStatusCode.Accepted, stop.StatusCode);
            await WaitForJob(owner, (await ApiClient.Read<JobAccepted>(stop)).JobId);
        }

        var audit = await owner.GetJsonAsync<List<AuditDto>>("/api/v2/audit?server=101");
        Assert.Contains(audit, e => e.Action == "start" && e.Ok && e.User == AgentFixture.OwnerName);
        Assert.Contains(audit, e => e.Action == "stop" && e.Ok);

        var logs = await owner.GetJsonAsync<LogDto>(_f.ServerUrl("101", "/logs"));
        Assert.Contains(logs.Lines, l => l.Contains("[#101]"));
    }

    [Fact]
    public async Task Settings_accept_known_keys_only_and_save_nothing_if_anything_is_wrong()
    {
        var owner = _f.Owner;
        var before = await owner.GetJsonAsync<ServerSettingsDto>(_f.ServerUrl("102", "/settings"));
        Assert.Equal("Test 102", before.Values["servername"]);
        Assert.Equal(string.Empty, before.Values["batchfile"]); // the before-start script: none

        var bad = await owner.PatchAsync(_f.ServerUrl("102", "/settings"), new SettingsUpdateRequest(new Dictionary<string, string?>
        {
            ["servername"] = "Renamed",
            ["serverport"] = "99999",
            ["batchfile"] = "C:\\evil.bat",
        }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var problems = (await ApiClient.Read<ApiError>(bad)).Details!;
        Assert.Contains(problems, p => p.Contains("serverport"));
        Assert.Contains(problems, p => p.Contains("batchfile"));
        Assert.Equal("Test 102", (await owner.GetJsonAsync<ServerSettingsDto>(_f.ServerUrl("102", "/settings"))).Values["servername"]);

        var good = await owner.PatchAsync(_f.ServerUrl("102", "/settings"), new SettingsUpdateRequest(new Dictionary<string, string?>
        {
            ["servername"] = "Renamed 102",
            ["autorestart"] = "true",
        }));
        Assert.Equal(HttpStatusCode.NoContent, good.StatusCode);
        var after = await owner.GetJsonAsync<ServerSettingsDto>(_f.ServerUrl("102", "/settings"));
        Assert.Equal("Renamed 102", after.Values["servername"]);
        Assert.Equal("1", after.Values["autorestart"]);
        Assert.Equal("Renamed 102", (await owner.GetJsonAsync<ServerDto>(_f.ServerUrl("102"))).Name);

        // Audit names the settings but never their values.
        var audit = await owner.GetJsonAsync<List<AuditDto>>("/api/v2/audit?server=102&action=settings");
        Assert.Contains(audit, e => e.Ok && e.Detail!.Contains("servername") && !e.Detail.Contains("Renamed"));

        await owner.PatchAsync(_f.ServerUrl("102", "/settings"), new SettingsUpdateRequest(new Dictionary<string, string?> { ["servername"] = "Test 102", ["autorestart"] = "0" }));
    }

    [Fact]
    public async Task Schedules_round_trip_and_reject_programs_and_bad_cron()
    {
        var owner = _f.Owner;
        var exec = await owner.PutAsync(_f.ServerUrl("103", "/schedules"), new SchedulesRequest(new[] { new ScheduleEntryRequest("0 6 * * *", "Exec", "cmd.exe") }));
        Assert.Equal(HttpStatusCode.BadRequest, exec.StatusCode);
        var badCron = await owner.PutAsync(_f.ServerUrl("103", "/schedules"), new SchedulesRequest(new[] { new ScheduleEntryRequest("every day", "Restart") }));
        Assert.Equal(HttpStatusCode.BadRequest, badCron.StatusCode);

        var ok = await owner.PutAsync(_f.ServerUrl("103", "/schedules"), new SchedulesRequest(new[]
        {
            new ScheduleEntryRequest("0 6 * * *", "Restart"),
            new ScheduleEntryRequest("*/30 * * * *", "Command", "say hello"),
        }));
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        var list = await owner.GetJsonAsync<List<ScheduleDto>>(_f.ServerUrl("103", "/schedules"));
        Assert.Contains(list, s => s.Action == "Restart" && s.Source == "Managed" && s.Next != null);
        Assert.Contains(list, s => s.Action == "Command" && s.Payload == "say hello");

        // Scheduling commands needs the Console right as well as Schedules.
        var scheduler = await _f.UserAsync("scheduler", Role.Member,
            new Dictionary<string, Capability> { [$"{_f.MachineId}/103"] = Capability.View | Capability.Schedules });
        var cmd = await scheduler.PutAsync(_f.ServerUrl("103", "/schedules"), new SchedulesRequest(new[] { new ScheduleEntryRequest("0 * * * *", "Command", "kick all") }));
        Assert.Equal(HttpStatusCode.Forbidden, cmd.StatusCode);
    }

    [Fact]
    public async Task Games_list_includes_built_ins_and_plugins_with_consents()
    {
        var games = await _f.Owner.GetJsonAsync<List<GameDto>>("/api/v2/machines/local/games");
        var mc = Assert.Single(games, g => g.Name == "Minecraft: Java Edition Server");
        Assert.Contains("eula", mc.Consents);
        Assert.Equal("/img/games/mc.png", mc.IconUrl);
        var test = Assert.Single(games, g => g.Name == EngineFixture.GameName);
        Assert.True(test.IsPlugin);
        Assert.Contains("eula", test.Consents); // found by reading the plugin's source
        Assert.Contains(games, g => g.IsSteam && g.AppId == "258550"); // Rust
    }

    [Fact]
    public async Task Unknown_api_paths_answer_json_404()
    {
        var res = await _f.Owner.GetAsync("/api/v2/nope");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("not_found", (await ApiClient.Read<ApiError>(res)).Code);
    }
}
