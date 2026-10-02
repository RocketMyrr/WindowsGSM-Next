using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WindowsGSM.Agent.Hosting;
using WindowsGSM.Contracts;
using WindowsGSM.Core.Tests;
using WindowsGSM.Engine.Services;

namespace WindowsGSM.Agent.Tests;

/// <summary>Update badges, restart warnings, the reachability check and the Discord bot's settings API.</summary>
[Collection("Agent")]
public class FleetFeatureTests
{
    private readonly AgentFixture _f;
    public FleetFeatureTests(AgentFixture f) => _f = f;

    [Fact]
    public async Task A_waiting_update_shows_on_the_server_and_is_announced_once()
    {
        var watch = _f.App.Services.GetRequiredService<UpdateWatch>();
        var old = watch.Check;
        string remote = "200";
        watch.Check = id => Task.FromResult(new UpdateCheck("100", remote, false, id == "103" && remote != "100", null));
        try
        {
            await watch.CheckNowAsync("103");
            await EngineFixture.WaitUntil(() => _f.Owner.GetJsonAsync<List<ServerDto>>("/api/v2/machines/local/servers").GetAwaiter().GetResult().Single(s => s.Id == "103").UpdateAvailable, "the badge");
            await watch.CheckNowAsync("103"); // same version again: no second notification
            var items = (await _f.Owner.GetJsonAsync<JsonElement>("/api/v2/notifications")).GetProperty("items").EnumerateArray()
                .Where(i => i.GetProperty("kind").GetString() == "updateAvailable" && i.GetProperty("server").GetString() == "103").ToList();
            Assert.Single(items);

            remote = "100"; // updated: nothing waiting any more
            var check = await _f.Owner.GetJsonAsync<UpdateCheckDto>(_f.ServerUrl("103", "/update-check"));
            Assert.False(check.UpdateAvailable);
            Assert.False((await _f.Owner.GetJsonAsync<List<ServerDto>>("/api/v2/machines/local/servers")).Single(s => s.Id == "103").UpdateAvailable);
        }
        finally { watch.Check = old; }
    }

    [Fact]
    public void Warnings_go_out_at_their_moment_and_never_late()
    {
        var at = new DateTime(2026, 10, 1, 6, 0, 0);
        int[] leads = { 300, 60 };
        Assert.Empty(RestartWarnings.Due(at.AddMinutes(-6), at, leads));
        Assert.Equal(new[] { 300 }, RestartWarnings.Due(at.AddSeconds(-295), at, leads));
        Assert.Empty(RestartWarnings.Due(at.AddMinutes(-3), at, leads)); // too late to say "in 5 minutes"
        Assert.Equal(new[] { 60 }, RestartWarnings.Due(at.AddSeconds(-58), at, leads));
        Assert.Empty(RestartWarnings.Due(at, at, leads));

        var s = new WarningSettings { Command = "broadcast {message}", Message = "Server {action} in {time}!" };
        Assert.Equal("broadcast Server restarting in 5 minutes!", RestartWarnings.Compose(s, ScheduledAction.Restart, 300));
        Assert.Equal("broadcast Server shutting down in 1 minute!", RestartWarnings.Compose(s, ScheduledAction.Stop, 60));
        Assert.Equal("broadcast Server updating in 30 seconds!", RestartWarnings.Compose(s, ScheduledAction.Update, 30));
    }

    [Fact]
    public async Task Warning_settings_are_checked_and_saved()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Owner.PutAsync(_f.ServerUrl("102", "/restart-warnings"), new { enabled = true, leads = new[] { 300 }, command = "say hi", message = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Owner.PutAsync(_f.ServerUrl("102", "/restart-warnings"), new { enabled = true, leads = new[] { 42 }, command = "say {message}", message = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _f.Owner.PutAsync(_f.ServerUrl("102", "/restart-warnings"), new { enabled = true, leads = new[] { 60, 300 }, command = "say {message}", message = "Server {action} in {time}." })).StatusCode);
        var w = await _f.Owner.GetJsonAsync<JsonElement>(_f.ServerUrl("102", "/restart-warnings"));
        Assert.True(w.GetProperty("enabled").GetBoolean());
        Assert.Equal(new[] { 300, 60 }, w.GetProperty("leads").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        var viewer = await _f.UserAsync("warnviewer", Role.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsync(_f.ServerUrl("102", "/restart-warnings"), new { enabled = false, leads = new[] { 60 }, command = "say {message}", message = "x" })).StatusCode);
    }

    [Fact]
    public async Task The_reachability_report_explains_the_router_and_address()
    {
        var reach = _f.App.Services.GetRequiredService<Reachability>();
        reach.PublicIp = () => Task.FromResult<string?>("203.0.113.5");
        reach.SteamServersAt = _ => Task.FromResult<IReadOnlyList<(string, int)>?>(Array.Empty<(string, int)>());
        var report = await _f.Owner.GetJsonAsync<ReachReport>(_f.ServerUrl("103", "/reachability"));
        Assert.Equal("203.0.113.5", report.PublicIp);
        Assert.Contains(report.Checks, c => c.Name == "Router" && c.Message.Contains("forward"));
        Assert.Contains(report.Checks, c => c.Name == "Address");
        Assert.Contains(report.Checks, c => c.Name == "Firewall");
    }

    [Fact]
    public async Task The_bot_is_owner_only_and_never_shows_its_token()
    {
        var admin = await _f.UserAsync("botadmin", Role.Admin);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v2/discord-bot")).StatusCode);

        var bad = await _f.Owner.PutAsync("/api/v2/discord-bot", new { enabled = false, token = "x.y.z-secret", postActions = true, admins = new[] { new { discordId = "not-a-number", name = "A", servers = new[] { "*" } } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var ok = await _f.Owner.PutAsync("/api/v2/discord-bot", new { enabled = false, token = "abc.def.ghijklmnop-secret", postActions = true, admins = new[] { new { discordId = "123456789012345678", name = "Alice", servers = new[] { $"{_f.MachineId}/101" } } } });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        string json = await ok.Content.ReadAsStringAsync();
        Assert.DoesNotContain("abc.def", json);
        var cfg = JsonDocument.Parse(json).RootElement;
        Assert.True(cfg.GetProperty("hasToken").GetBoolean());
        Assert.Equal("Off", cfg.GetProperty("status").GetProperty("state").GetString());

        // Saving again without a token keeps it.
        var again = await ApiClient.Read<JsonElement>(await _f.Owner.PutAsync("/api/v2/discord-bot", new { enabled = false, postActions = false, admins = Array.Empty<object>() }));
        Assert.True(again.GetProperty("hasToken").GetBoolean());
    }
}
