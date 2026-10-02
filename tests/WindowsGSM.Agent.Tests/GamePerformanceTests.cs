using System.Text.Json;
using WindowsGSM.Agent.Hosting;
using WindowsGSM.Engine.Servers;

namespace WindowsGSM.Agent.Tests;

/// <summary>Server FPS / TPS: reading each game's answer, and sampling into history.</summary>
[Collection("Agent")]
public class GamePerformanceTests
{
    private readonly AgentFixture _f;
    public GamePerformanceTests(AgentFixture f) => _f = f;

    [Theory]
    [InlineData("§6TPS from last 1m, 5m, 15m: §a*20.0, §a19.97, §a20.0", 20.0)]
    [InlineData("TPS from last 1m, 5m, 15m: 14.21, 18.0, 19.5", 14.21)]
    [InlineData("Target tick rate: 20.0 per second.\nAverage time per tick: 80.0ms (Target: 50.0ms)", 12.5)]
    [InlineData("Unknown command", null)]
    public void Minecraft_tps(string reply, double? tps) => Assert.Equal(tps, GamePerformance.MinecraftTps(reply));

    [Fact]
    public void Source_stats_fps()
    {
        const string reply = "CPU   In_(KB/s)  Out_(KB/s)  Uptime  Map_changes  FPS      Players  Connects\n10.00 1.23       4.56        120     1            63.71    5        12\n";
        Assert.Equal(63.71, GamePerformance.SourceStatsFps(reply));
        Assert.Null(GamePerformance.SourceStatsFps("Unknown command \"stats\""));
    }

    [Fact]
    public async Task Samples_are_kept_and_served_for_the_chart()
    {
        // The test game has no FPS/TPS command, so it is never asked; the history store and API still work.
        var s = _f.Engine.Servers.Get("101")!;
        using var history = new MetricsHistory(_f.Context, Path.Combine(Path.GetTempPath(), $"wgsm-perf-{Guid.NewGuid():N}.db"));
        using var perf = new GamePerformance(_f.Context, history);
        Assert.Null(perf.ProbeFor(s)); // the test game has no performance command

        // Probes by game: unknown → none; the parsers do the rest.
        var asked = new List<string>();
        perf.AskOverride = (id, cmd) => { asked.Add(cmd); return Task.FromResult<string?>("TPS from last 1m, 5m, 15m: 18.5, 19, 20"); };
        await perf.SampleAllAsync();
        Assert.Empty(asked); // nothing to ask a game WindowsGSM has no probe for

        history.RecordPerf("101", DateTimeOffset.UtcNow.AddMinutes(-3), 19.0);
        history.RecordPerf("101", DateTimeOffset.UtcNow.AddMinutes(-2), 12.0);
        var points = history.QueryPerf("101", TimeSpan.FromHours(1), 60);
        Assert.Equal(2, points.Count);
        Assert.Equal(12.0, points[1].Min);

        var res = await _f.Owner.GetJsonAsync<JsonElement>(_f.ServerUrl("101", "/performance?range=1h"));
        Assert.False(res.GetProperty("supported").GetBoolean());
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await _f.Owner.GetAsync(_f.ServerUrl("101", "/performance?range=2w"))).StatusCode);
    }
}
