using WindowsGSM.Engine.Events;
using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Engine.Services;
using WindowsGSM.Functions;

namespace WindowsGSM.Core.Tests;

/// <summary>Updates through the plugin's own updater, as jobs with progress, behind the gate.</summary>
[Collection("Lifecycle")]
public class UpdateTests
{
    private static async Task<JobSnapshot> Run(OperationRequest request)
    {
        Assert.True(request.Accepted, request.Error);
        return await request.Job!.Completion.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static string Marker(string id) => ServerPath.GetServersServerFiles(id, "updated.txt");

    [Fact]
    public async Task Update_runs_the_plugins_updater_reports_progress_and_returns_to_stopped()
    {
        EngineFixture.CreateServer("231");
        using var engine = await EngineFixture.StartEngineAsync();
        var events = EngineFixture.Record(engine, "231");

        var result = await Run(engine.Updates.Update("231", validate: true));

        Assert.Equal(JobStatus.Succeeded, result.Status);
        Assert.Equal("validate", File.ReadAllText(Marker("231")).Trim());
        Assert.Contains("fake download 50%", result.RecentLog);           // downloader output reached the job
        Assert.Equal(ServerState.Stopped, engine.Servers.Get("231")!.State);
        lock (events)
        {
            Assert.Contains(events, e => e is JobChanged { Job.Percent: 50 }); // live progress reached subscribers
            var states = events.OfType<ServerStateChanged>().Select(e => e.To).ToList();
            Assert.Equal(new[] { ServerState.Updating, ServerState.Stopped }, states);
        }
    }

    [Fact]
    public async Task A_failing_updater_fails_the_job_with_the_exit_code()
    {
        EngineFixture.CreateServer("232", mode: "update_fail");
        using var engine = await EngineFixture.StartEngineAsync();

        var result = await Run(engine.Updates.Update("232"));

        Assert.Equal(JobStatus.Failed, result.Status);
        Assert.Contains("exited with code 3", result.Error);
        Assert.Equal(ServerState.Stopped, engine.Servers.Get("232")!.State);
    }

    [Fact]
    public async Task Update_is_refused_while_the_server_runs()
    {
        EngineFixture.CreateServer("233");
        using var engine = await EngineFixture.StartEngineAsync();
        var s = engine.Servers.Get("233")!;
        try
        {
            await Run(engine.Lifecycle.Start("233"));
            var refused = engine.Updates.Update("233");
            Assert.False(refused.Accepted);
            Assert.Contains("Stop", refused.Error);
        }
        finally { EngineFixture.KillQuietly(s.Process); }
    }

    [Fact]
    public async Task Update_on_start_runs_before_the_server_starts()
    {
        EngineFixture.CreateServer("234", extraSettings: "updateonstart=\"1\"");
        using var engine = await EngineFixture.StartEngineAsync();
        var events = EngineFixture.Record(engine, "234");
        var s = engine.Servers.Get("234")!;
        try
        {
            var started = await Run(engine.Lifecycle.Start("234"));

            Assert.Equal(JobStatus.Succeeded, started.Status);
            Assert.Equal(ServerState.Running, s.State);
            Assert.Equal("update", File.ReadAllText(Marker("234")).Trim());
            lock (events)
            {
                var log = events.OfType<ServerLogged>().Select(l => l.Message).ToList();
                int update = log.FindIndex(m => m.StartsWith("Action: Update") && m.EndsWith("| Update on Start"));
                int start = log.FindIndex(m => m == "Action: Start");
                Assert.True(update >= 0 && start > update, "update should be logged before the start:\n" + string.Join("\n", log));
            }
        }
        finally { EngineFixture.KillQuietly(s.Process); }
    }
}
