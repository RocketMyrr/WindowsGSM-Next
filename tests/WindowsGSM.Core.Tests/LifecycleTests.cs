using System.Diagnostics;
using WindowsGSM.Engine;
using WindowsGSM.Engine.Events;
using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Engine.Services;
using WindowsGSM.Engine.Watchdog;
using WindowsGSM.Functions;
using WindowsGSM.Hosting;

namespace WindowsGSM.Core.Tests;

/// <summary>
/// Start / stop / restart / kill / crash / auto-restart / reattach with real processes, through the real
/// plugin loader and operation gate. Serial: these spawn processes and share the data folder.
/// </summary>
[Collection("Lifecycle")]
public class LifecycleTests
{
    private static async Task<JobSnapshot> Run(OperationRequest request)
    {
        Assert.True(request.Accepted, request.Error);
        return await request.Job!.Completion.WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Start_captures_console_output_and_stop_ends_the_process()
    {
        EngineFixture.CreateServer("241");
        using var engine = await EngineFixture.StartEngineAsync();
        var events = EngineFixture.Record(engine, "241");
        var s = engine.Servers.Get("241")!;
        Process? p = null;
        try
        {
            var started = await Run(engine.Lifecycle.Start("241"));
            Assert.Equal(JobStatus.Succeeded, started.Status);
            Assert.Equal(ServerState.Running, s.State);
            p = s.Process;
            Assert.NotNull(p);
            Assert.Equal(p!.Id, ServerCache.GetPID("241"));

            // Embedded console: the plugin's own ServerConsole instance must feed the server's canonical buffer.
            await EngineFixture.WaitUntil(() => s.Console.Get().Contains("127.0.0.1"), "console output from ping");
            lock (events) { Assert.Contains(events, e => e is ConsoleLineAdded); }

            var stopped = await Run(engine.Lifecycle.Stop("241"));
            Assert.Equal(JobStatus.Succeeded, stopped.Status);
            Assert.Equal(ServerState.Stopped, s.State);
            Assert.True(p.HasExited);
            Assert.Equal(-1, ServerCache.GetPID("241"));
            // Last output is kept after a stop (legacy cleared it) — it's what you need after an unexpected stop.
            Assert.Contains("127.0.0.1", s.Console.Get());

            lock (events)
            {
                var states = events.OfType<ServerStateChanged>().Select(e => e.To).ToList();
                Assert.Equal(new[] { ServerState.Starting, ServerState.Running, ServerState.Stopping, ServerState.Stopped }, states);
                Assert.DoesNotContain(events, e => e is ServerAlert { Kind: AlertKind.Crashed }); // a stop is not a crash
            }
        }
        finally { EngineFixture.KillQuietly(p); }
    }

    [Fact]
    public async Task Conflicting_requests_are_refused_with_a_reason()
    {
        EngineFixture.CreateServer("242");
        using var engine = await EngineFixture.StartEngineAsync();
        var s = engine.Servers.Get("242")!;
        try
        {
            var first = engine.Lifecycle.Start("242");
            var second = engine.Lifecycle.Start("242"); // while the first is still starting
            Assert.True(first.Accepted);
            Assert.False(second.Accepted);
            Assert.Contains("busy", second.Error);
            await first.Job!.Completion;

            var again = engine.Lifecycle.Start("242");
            Assert.False(again.Accepted);
            Assert.Contains("already running", again.Error);

            Assert.False(engine.Lifecycle.Stop("nope").Accepted);
        }
        finally { EngineFixture.KillQuietly(s.Process); }
    }

    [Fact]
    public async Task Kill_stops_it_immediately_and_is_not_reported_as_a_crash()
    {
        EngineFixture.CreateServer("243", autoRestart: true);
        using var engine = await EngineFixture.StartEngineAsync();
        var events = EngineFixture.Record(engine, "243");
        var s = engine.Servers.Get("243")!;

        await Run(engine.Lifecycle.Start("243"));
        var p = s.Process!;
        await Run(engine.Lifecycle.Kill("243"));

        Assert.Equal(ServerState.Stopped, s.State);
        await EngineFixture.WaitUntil(() => p.HasExited, "killed process to exit");
        await Task.Delay(500); // give a (wrong) crash handler the chance to run
        Assert.Equal(ServerState.Stopped, s.State);
        lock (events) { Assert.DoesNotContain(events, e => e is ServerAlert { Kind: AlertKind.Crashed }); }
    }

    [Fact]
    public async Task Restart_replaces_the_process()
    {
        EngineFixture.CreateServer("244");
        using var engine = await EngineFixture.StartEngineAsync();
        engine.Lifecycle.RestartPause = TimeSpan.FromMilliseconds(50);
        var s = engine.Servers.Get("244")!;
        Process? before = null, after = null;
        try
        {
            await Run(engine.Lifecycle.Start("244"));
            before = s.Process!;
            var restarted = await Run(engine.Lifecycle.Restart("244"));
            after = s.Process;

            Assert.Equal(JobStatus.Succeeded, restarted.Status);
            Assert.Equal(ServerState.Running, s.State);
            Assert.NotNull(after);
            Assert.NotEqual(before.Id, after!.Id);
            Assert.True(before.HasExited);
        }
        finally { EngineFixture.KillQuietly(before); EngineFixture.KillQuietly(after); }
    }

    [Fact]
    public async Task A_plugin_that_cannot_start_fails_the_job_with_its_own_error()
    {
        EngineFixture.CreateServer("245", mode: "fail");
        using var engine = await EngineFixture.StartEngineAsync();

        var result = await Run(engine.Lifecycle.Start("245"));

        Assert.Equal(JobStatus.Failed, result.Status);
        Assert.Equal("Test plugin refused to start", result.Error);
        Assert.Equal(ServerState.Stopped, engine.Servers.Get("245")!.State);
    }

    [Fact]
    public async Task A_crash_without_auto_restart_stops_the_server_and_writes_a_crash_report()
    {
        EngineFixture.CreateServer("246", mode: "crash");
        using var engine = await EngineFixture.StartEngineAsync();
        var events = EngineFixture.Record(engine, "246");
        var s = engine.Servers.Get("246")!;

        await Run(engine.Lifecycle.Start("246"));
        await EngineFixture.WaitUntil(() => { lock (events) { return events.Any(e => e is ServerAlert { Kind: AlertKind.Crashed }); } }, "crash alert");
        await EngineFixture.WaitUntil(() => s.State == ServerState.Stopped, "server to be stopped after the crash");

        string reports = ServerPath.GetLogs("servers", "246");
        Assert.NotEmpty(Directory.GetFiles(reports, "crash_*.log"));
        string report = File.ReadAllText(Directory.GetFiles(reports, "crash_*.log").OrderBy(f => f).Last());
        Assert.Contains("Server Name: Test 246", report);
        Assert.Contains("127.0.0.1", report); // captured console output is included
    }

    [Fact]
    public async Task Crash_loop_restarts_with_backoff_then_suspends()
    {
        EngineFixture.CreateServer("247", mode: "crash", autoRestart: true);
        var fastLoop = new CrashLoopOptions
        {
            SoftThreshold = 1, HardThreshold = 3,
            BackoffBase = TimeSpan.FromMilliseconds(100), BackoffMax = TimeSpan.FromMilliseconds(400),
        };
        using var engine = await EngineFixture.StartEngineAsync(fastLoop);
        var events = EngineFixture.Record(engine, "247");
        var s = engine.Servers.Get("247")!;

        await Run(engine.Lifecycle.Start("247"));
        await EngineFixture.WaitUntil(() => s.CrashLoopSuspended, "auto-restart to be suspended", timeoutMs: 45000);
        await EngineFixture.WaitUntil(() => s.State == ServerState.Stopped, "server stopped after suspension");

        lock (events)
        {
            Assert.Equal(3, events.Count(e => e is ServerAlert { Kind: AlertKind.Crashed }));
            Assert.Equal(2, events.Count(e => e is ServerAlert { Kind: AlertKind.AutoRestarted }));
            Assert.Single(events, e => e is ServerAlert { Kind: AlertKind.CrashLoopSuspended });
            Assert.Contains(events, e => e is ServerLogged l && l.Message.Contains("delaying auto-restart"));
        }

        // A manual start gives the server a clean slate.
        EngineFixture.CreateServer("247", mode: "long", autoRestart: true);
        try
        {
            await Run(engine.Lifecycle.Start("247"));
            Assert.False(s.CrashLoopSuspended);
        }
        finally { EngineFixture.KillQuietly(s.Process); }
    }

    [Fact]
    public async Task Only_one_engine_can_manage_a_data_folder_at_a_time()
    {
        using (var first = await EngineFixture.StartEngineAsync())
        {
            var ex = await Assert.ThrowsAsync<DataRootInUseException>(() => EngineFixture.StartEngineAsync());
            Assert.Contains("already managing this data folder", ex.Message);
        }
        // Released on dispose: the next engine starts fine.
        using var next = await EngineFixture.StartEngineAsync();
    }

    [Fact]
    public async Task A_new_engine_reattaches_running_servers_and_still_detects_their_crash()
    {
        // Engine A starts the server, then "goes away" (the agent restarted). Engine B must adopt the
        // still-running process AND notice when it dies — legacy re-adopted but never got the exit event.
        EngineFixture.CreateServer("248");
        Process? p = null;
        try
        {
            using (var a = await EngineFixture.StartEngineAsync())
            {
                await Run(a.Lifecycle.Start("248"));
                p = a.Servers.Get("248")!.Process;
            }

            using var b = await EngineFixture.StartEngineAsync();
            var s = b.Servers.Get("248")!;
            Assert.Equal(ServerState.Running, s.State);
            Assert.Equal(p!.Id, s.Process!.Id);

            EngineFixture.KillQuietly(p); // dies outside the engine's control
            await EngineFixture.WaitUntil(() => s.State == ServerState.Stopped, "reattached server's crash to be detected");
        }
        finally { EngineFixture.KillQuietly(p); }
    }
}
