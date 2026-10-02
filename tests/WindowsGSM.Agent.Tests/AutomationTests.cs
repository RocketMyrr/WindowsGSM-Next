using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WindowsGSM.Agent.Hosting;
using WindowsGSM.Contracts;
using WindowsGSM.Core.Tests;
using WindowsGSM.Engine.Events;
using WindowsGSM.Engine.Services;

namespace WindowsGSM.Agent.Tests;

/// <summary>Automations: rules are admin-only and checked, conditions must hold long enough, and cooldowns hold.</summary>
[Collection("Agent")]
public class AutomationTests
{
    private readonly AgentFixture _f;
    public AutomationTests(AgentFixture f) => _f = f;

    private Automations Rules => _f.App.Services.GetRequiredService<Automations>();

    [Fact]
    public async Task A_rule_fires_once_its_condition_has_held_long_enough_then_waits()
    {
        var viewer = await _f.UserAsync("autoviewer", Role.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v2/machines/local/automations")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Owner.PutAsync("/api/v2/machines/local/automations/bad1", new { trigger = "cpu", minutes = 5, threshold = 500, action = "notify", servers = new[] { "102" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Owner.PutAsync("/api/v2/machines/local/automations/bad2", new { trigger = "empty", minutes = 5, action = "command", servers = new[] { "102" } })).StatusCode);

        var put = await _f.Owner.PutAsync("/api/v2/machines/local/automations/cpu1", new { name = "Busy CPU", trigger = "cpu", minutes = 5, threshold = 80, action = "notify", servers = new[] { "102" }, cooldownMinutes = 60, enabled = true });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var now = DateTimeOffset.Now;
        Rules.Clock = () => now;
        int Count() => _f.Owner.GetJsonAsync<JsonElement>("/api/v2/notifications?limit=500").GetAwaiter().GetResult().GetProperty("items").EnumerateArray()
            .Count(n => n.GetProperty("kind").GetString() == "automation" && n.GetProperty("title").GetString()!.StartsWith("Busy CPU"));
        int before = Count();
        try
        {
            void Sample(double cpu, int? players = 3) => _f.Engine.Events.Publish(new ServerMetricsSampled("102", new ServerSample(now, cpu, 500, players, 10)));

            Sample(95);                       // starts the clock
            now = now.AddMinutes(3); Sample(95);
            now = now.AddMinutes(1); Sample(10); // dropped below — the clock resets
            now = now.AddMinutes(1); Sample(95);
            now = now.AddMinutes(4); Sample(95);
            await Task.Delay(500);
            Assert.Equal(before, Count());    // 4 minutes since the reset: not yet

            now = now.AddMinutes(2); Sample(95); // 6 minutes → fires
            await EngineFixture.WaitUntil(() => Count() == before + 1, "the automation to fire");

            now = now.AddMinutes(10); Sample(95); now = now.AddMinutes(6); Sample(95); // held again, but inside the hour's pause
            await Task.Delay(500);
            Assert.Equal(before + 1, Count());

            var rule = (await _f.Owner.GetJsonAsync<List<JsonElement>>("/api/v2/machines/local/automations")).Single(r => r.GetProperty("id").GetString() == "cpu1");
            Assert.Contains("used over 80% CPU", rule.GetProperty("lastResult").GetString());
        }
        finally
        {
            Rules.Clock = () => DateTimeOffset.Now;
            await _f.Owner.DeleteAsync("/api/v2/machines/local/automations/cpu1");
        }
    }

    [Fact]
    public async Task An_empty_rule_ignores_servers_whose_player_count_is_unknown()
    {
        await _f.Owner.PutAsync("/api/v2/machines/local/automations/empty1", new { name = "Empty test", trigger = "empty", minutes = 1, action = "notify", servers = new[] { "103" }, cooldownMinutes = 0, enabled = true });
        var now = DateTimeOffset.Now;
        Rules.Clock = () => now;
        int Count() => _f.Owner.GetJsonAsync<JsonElement>("/api/v2/notifications?limit=500").GetAwaiter().GetResult().GetProperty("items").EnumerateArray()
            .Count(n => n.GetProperty("title").GetString()!.StartsWith("Empty test"));
        int before = Count();
        try
        {
            // No query answer (players unknown) for ten minutes: not "empty".
            for (int i = 0; i < 10; i++) { _f.Engine.Events.Publish(new ServerMetricsSampled("103", new ServerSample(now, 1, 100, null, null))); now = now.AddMinutes(1); }
            await Task.Delay(500);
            Assert.Equal(before, Count());
            // Known to be empty for two minutes: fires.
            for (int i = 0; i < 3; i++) { _f.Engine.Events.Publish(new ServerMetricsSampled("103", new ServerSample(now, 1, 100, 0, 10))); now = now.AddMinutes(1); }
            await EngineFixture.WaitUntil(() => Count() == before + 1, "the empty rule to fire");
        }
        finally
        {
            Rules.Clock = () => DateTimeOffset.Now;
            await _f.Owner.DeleteAsync("/api/v2/machines/local/automations/empty1");
        }
    }

    [Fact]
    public async Task Low_disk_space_notifies_once_it_has_lasted()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Owner.PutAsync("/api/v2/machines/local/automations/disk0", new { trigger = "disk", minutes = 5, threshold = 20, action = "stop" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _f.Owner.PutAsync("/api/v2/machines/local/automations/disk1", new { name = "Disk test", trigger = "disk", minutes = 5, threshold = 20, action = "notify", cooldownMinutes = 60, enabled = true })).StatusCode);
        var now = DateTimeOffset.Now;
        long free = 50L * 1024 * 1024 * 1024;
        Rules.Clock = () => now;
        Rules.DrivesOverride = () => new[] { ("Z:\\", free) };
        int Count() => _f.Owner.GetJsonAsync<JsonElement>("/api/v2/notifications?limit=500").GetAwaiter().GetResult().GetProperty("items").EnumerateArray()
            .Count(n => n.GetProperty("title").GetString() == "Disk test");
        int before = Count();
        try
        {
            Rules.CheckDisks();                              // plenty free
            free = 5L * 1024 * 1024 * 1024;
            Rules.CheckDisks();                              // low: the clock starts
            now = now.AddMinutes(3); Rules.CheckDisks();
            await Task.Delay(300);
            Assert.Equal(before, Count());
            now = now.AddMinutes(3); Rules.CheckDisks();     // 6 minutes low → tells you
            await EngineFixture.WaitUntil(() => Count() == before + 1, "the low-disk notification");
            var n = _f.Owner.GetJsonAsync<JsonElement>("/api/v2/notifications?limit=500").GetAwaiter().GetResult().GetProperty("items").EnumerateArray()
                .First(x => x.GetProperty("title").GetString() == "Disk test");
            Assert.Contains("only 5 GB free", n.GetProperty("text").GetString());
        }
        finally
        {
            Rules.Clock = () => DateTimeOffset.Now;
            Rules.DrivesOverride = null;
            await _f.Owner.DeleteAsync("/api/v2/machines/local/automations/disk1");
        }
    }
}
