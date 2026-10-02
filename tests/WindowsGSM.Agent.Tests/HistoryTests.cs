using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WindowsGSM.Agent.Hosting;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Tests;

/// <summary>Long-term history: minutes → hours, charts by range, player sessions, and who may read them.</summary>
[Collection("Agent")]
public class HistoryTests
{
    private readonly AgentFixture _f;
    public HistoryTests(AgentFixture f) => _f = f;

    private MetricsHistory History => _f.App.Services.GetRequiredService<MetricsHistory>();

    [Fact]
    public async Task Samples_become_minute_points_for_servers_and_the_machine()
    {
        var now = DateTimeOffset.UtcNow;
        // Whole minutes, and an even one: points on a 6-hour chart are two minutes wide.
        var t0 = DateTimeOffset.FromUnixTimeSeconds(now.AddMinutes(-30).ToUnixTimeSeconds() / 120 * 120);
        for (int i = 0; i < 10; i++)
        {
            // Two samples per minute: the point is their average.
            History.Record("103", t0.AddMinutes(i).AddSeconds(5), 10, 500, players: i, maxPlayers: 20);
            History.Record("103", t0.AddMinutes(i).AddSeconds(35), 30, 700, players: i + 1, maxPlayers: 20);
            History.Record(null, t0.AddMinutes(i).AddSeconds(5), 40, 60, disk: 70);
        }

        var points = await _f.Owner.GetJsonAsync<List<JsonElement>>(_f.ServerUrl("103", "/history?range=6h"));
        Assert.NotEmpty(points);
        var first = points.First(p => p.GetProperty("cpu").ValueKind == JsonValueKind.Number);
        Assert.Equal(20, first.GetProperty("cpu").GetDouble(), 1);
        Assert.Equal(600, first.GetProperty("ram").GetDouble(), 1);
        Assert.True(points.Max(p => p.GetProperty("players").ValueKind == JsonValueKind.Number ? p.GetProperty("players").GetInt32() : 0) >= 9);

        var machine = await _f.Owner.GetJsonAsync<List<JsonElement>>("/api/v2/machines/local/history?range=24h");
        Assert.Contains(machine, p => p.GetProperty("disk").ValueKind == JsonValueKind.Number && p.GetProperty("disk").GetDouble() == 70);

        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Owner.GetAsync(_f.ServerUrl("103", "/history?range=5m"))).StatusCode);
        // Someone who can't see server 103 can't read its history either.
        var other = await _f.UserAsync("history101", Role.Member, new() { [$"{_f.MachineId}/101"] = Capability.View });
        Assert.NotEqual(HttpStatusCode.OK, (await other.GetAsync(_f.ServerUrl("103", "/history"))).StatusCode);
    }

    [Fact]
    public void Old_minutes_roll_up_into_hours_and_stay_on_long_charts()
    {
        using var history = new MetricsHistory(_f.Context, Path.Combine(Path.GetTempPath(), $"wgsm-history-{Guid.NewGuid():N}.db"));
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-20);
        history.Record("9", old, 50, 1000);
        history.Record("9", old.AddMinutes(1), 50, 1000); // closes the first minute
        history.Record("9", now.AddMinutes(-2), 10, 100);
        history.Flush();

        history.Rollup(now);
        var month = history.Query("9", TimeSpan.FromDays(30), 7200, now);
        Assert.Contains(month, p => p.Cpu == 50);
        Assert.Contains(month, p => p.Cpu == 10);
        Assert.DoesNotContain(history.Query("9", TimeSpan.FromDays(1), 300, now), p => p.Cpu == 50);
    }

    [Fact]
    public async Task Player_sessions_add_up_to_who_plays_most()
    {
        // Totals on a server id nothing else touches: other tests stopping 101 would close these sessions early.
        var t0 = DateTimeOffset.UtcNow.AddHours(-2);
        History.RecordPlayers("hist-only", new[] { "Alice", "Bob" }, t0);
        History.RecordPlayers("hist-only", new[] { "Bob" }, t0.AddMinutes(10));
        History.RecordPlayers("hist-only", Array.Empty<string>(), t0.AddMinutes(30));
        var totals = History.TopPlayers("hist-only", 7, 10);
        Assert.Equal("Bob", totals[0].Name);
        Assert.Equal(1800, totals[0].Seconds, 0);
        Assert.Contains(totals, p => p.Name == "Alice" && Math.Abs(p.Seconds - 600) < 1);

        // And the API shape on a real server.
        History.RecordPlayers("101", new[] { "Carol" }, t0);
        History.RecordPlayers("101", Array.Empty<string>(), t0.AddMinutes(5));
        var res = await _f.Owner.GetJsonAsync<JsonElement>(_f.ServerUrl("101", "/players/history?days=7"));
        Assert.Contains(res.GetProperty("top").EnumerateArray(), p => p.GetProperty("name").GetString() == "Carol");
        Assert.True(res.GetProperty("recent").GetArrayLength() >= 1);
    }

    [Fact]
    public void Uptime_counts_running_minutes_since_the_server_was_first_seen_and_peaks_add_up()
    {
        var now = DateTimeOffset.UtcNow;
        var start = now.AddMinutes(-120);
        // Running (sampled) for the first hour, then stopped for an hour.
        for (int m = 0; m < 60; m++) { History.Record("uptime-a", start.AddMinutes(m), 5, 100, players: m == 30 ? 3 : 1); }
        History.Record("uptime-a", start.AddMinutes(61), 5, 100); // closes the last minute
        var up = History.Uptime("uptime-a", now);
        Assert.NotNull(up.Day);
        Assert.InRange(up.Day!.Value, 48, 53);       // ~half of the two hours it has existed
        Assert.Equal(up.Day, up.Week);              // the same: it's only two hours old
        Assert.Null(History.Uptime("never-seen", now).Day);

        // Peak: the most players at once across servers (3 + 4 in the same minute).
        History.Record("uptime-b", start.AddMinutes(30), 5, 100, players: 4);
        History.Record("uptime-b", start.AddMinutes(31), 5, 100, players: 0);
        History.Record("uptime-b", start.AddMinutes(32), 5, 100);
        Assert.Equal(7, History.PeakPlayers(new[] { "uptime-a", "uptime-b" }, start));
        Assert.Equal(3, History.PeakPlayers(new[] { "uptime-a" }, start));
    }
}
