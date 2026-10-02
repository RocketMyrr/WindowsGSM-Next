using WindowsGSM.Engine.Events;
using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Watchdog;

namespace WindowsGSM.Core.Tests;

/// <summary>Pure engine pieces: crash-loop maths, the operation gate, jobs, the event bus, CPU affinity.</summary>
public class EngineUnitTests
{
    // ── Crash-loop policy (numbers must match the legacy watchdog exactly) ──

    [Fact]
    public void Crash_loop_policy_matches_the_legacy_schedule()
    {
        var options = new CrashLoopOptions(); // legacy defaults: 10 min window, soft 3, hard 6, 15 s → 300 s
        var crashes = new List<DateTimeOffset>();
        var t0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        var decisions = Enumerable.Range(0, 6).Select(i => CrashLoopPolicy.OnCrash(crashes, t0.AddSeconds(i * 30), options)).ToList();

        Assert.Equal(CrashResponse.RestartNow, decisions[0].Response);
        Assert.Equal(CrashResponse.RestartNow, decisions[1].Response);
        Assert.Equal(CrashResponse.RestartNow, decisions[2].Response);
        Assert.Equal((CrashResponse.RestartAfterDelay, 15.0), (decisions[3].Response, decisions[3].Delay.TotalSeconds));
        Assert.Equal((CrashResponse.RestartAfterDelay, 30.0), (decisions[4].Response, decisions[4].Delay.TotalSeconds));
        Assert.Equal(CrashResponse.Suspend, decisions[5].Response);
    }

    [Fact]
    public void Crashes_outside_the_window_are_forgotten_and_backoff_is_capped()
    {
        var options = new CrashLoopOptions { HardThreshold = 100 };
        var crashes = new List<DateTimeOffset>();
        var t0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        for (int i = 0; i < 3; i++) { CrashLoopPolicy.OnCrash(crashes, t0, options); }
        var afterQuietSpell = CrashLoopPolicy.OnCrash(crashes, t0.AddMinutes(11), options);
        Assert.Equal((CrashResponse.RestartNow, 1), (afterQuietSpell.Response, afterQuietSpell.CrashesInWindow));

        crashes.Clear();
        CrashDecision last = default;
        for (int i = 0; i < 20; i++) { last = CrashLoopPolicy.OnCrash(crashes, t0, options); }
        Assert.Equal(300, last.Delay.TotalSeconds); // capped at BackoffMax
    }

    // ── Operation gate ──

    [Fact]
    public void Different_servers_no_longer_block_each_other()
    {
        var gate = new OperationGate();
        Assert.True(gate.TryBegin("1", OperationKind.Update, "Update", out var a, out _));
        Assert.True(gate.TryBegin("2", OperationKind.Start, "Start", out var b, out _)); // legacy refused this
        a!.Dispose(); b!.Dispose();
    }

    [Fact]
    public void Same_server_is_exclusive_until_released_and_kill_always_wins()
    {
        var gate = new OperationGate();
        Assert.True(gate.TryBegin("1", OperationKind.Update, "Update", out var update, out _));
        Assert.False(gate.TryBegin("1", OperationKind.Start, "Start", out _, out var blockedBy));
        Assert.Equal("Update", blockedBy);

        Assert.True(gate.TryBegin("1", OperationKind.Kill, "Kill", out var kill, out _));
        update!.Dispose(); // the interrupted operation releasing late must not free the Kill's claim
        Assert.True(gate.IsBusy("1"));
        kill!.Dispose();
        Assert.False(gate.IsBusy("1"));
    }

    [Fact]
    public void Global_operation_excludes_everything_both_ways()
    {
        var gate = new OperationGate();
        Assert.True(gate.TryBegin("1", OperationKind.Start, "Start", out var s1, out _));
        Assert.False(gate.TryBeginGlobal(OperationKind.Stop, "Stop all", out _, out _));
        s1!.Dispose();

        Assert.True(gate.TryBeginGlobal(OperationKind.Stop, "Stop all", out var all, out _));
        Assert.False(gate.TryBegin("5", OperationKind.Start, "Start", out _, out var blockedBy));
        Assert.Equal("Stop all", blockedBy);
        all!.Dispose();
        Assert.True(gate.TryBegin("5", OperationKind.Start, "Start", out var s5, out _));
        s5!.Dispose();
    }

    // ── Jobs ──

    [Fact]
    public async Task Jobs_report_success_failure_exceptions_and_cancellation()
    {
        var bus = new EventBus();
        var changes = new List<JobSnapshot>();
        bus.Subscribe<JobChanged>(e => { lock (changes) { changes.Add(e.Job); } });
        var jobs = new JobManager(bus);

        var ok = await jobs.Start("t", "1", "ok", async ctx => { ctx.Report(40, "Half way"); ctx.Log("hello"); await Task.Yield(); return null; }).Completion;
        Assert.Equal((JobStatus.Succeeded, 100), (ok.Status, ok.Percent));
        Assert.Equal("Half way", ok.Stage);
        Assert.Contains("hello", ok.RecentLog);

        var failed = await jobs.Start("t", "1", "fail", _ => Task.FromResult<string?>("nope")).Completion;
        Assert.Equal((JobStatus.Failed, "nope"), (failed.Status, failed.Error));

        var threw = await jobs.Start("t", "1", "throw", _ => throw new InvalidOperationException("boom")).Completion;
        Assert.Equal((JobStatus.Failed, "boom"), (threw.Status, threw.Error));

        var running = jobs.Start("t", "1", "cancel", async ctx => { await Task.Delay(Timeout.Infinite, ctx.Cancellation); return null; });
        Assert.True(jobs.Cancel(running.Id));
        var cancelled = await running.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(JobStatus.Cancelled, cancelled.Status);

        lock (changes) { Assert.Contains(changes, c => c.Title == "ok" && c.Status == JobStatus.Succeeded); }
    }

    [Fact]
    public async Task Throttled_progress_still_delivers_the_latest_value()
    {
        var bus = new EventBus();
        var seen = new List<int?>();
        bus.Subscribe<JobChanged>(e => { lock (seen) { seen.Add(e.Job.Percent); } });
        var jobs = new JobManager(bus);
        var release = new TaskCompletionSource();

        var job = jobs.Start("t", "1", "burst", async ctx =>
        {
            ctx.Report(10, "Downloading");            // stage change: published immediately
            ctx.Report(20); ctx.Report(35); ctx.Report(50); // inside the throttle window
            await release.Task;                           // download "pauses" here
            return null;
        });

        await Task.Delay(600);
        lock (seen) { Assert.Equal(50, seen.Last()); } // the pause shows 50, not a stale 10
        release.SetResult();
        await job.Completion;
    }

    // ── Event bus ──

    [Fact]
    public void A_throwing_subscriber_does_not_stop_the_others()
    {
        var bus = new EventBus();
        int received = 0;
        bus.Subscribe(_ => throw new Exception("bad subscriber"));
        using (bus.Subscribe(_ => received++))
        {
            bus.Publish(new ServerLogged("1", LogLevel.Info, "x"));
        }
        bus.Publish(new ServerLogged("1", LogLevel.Info, "y")); // unsubscribed now
        Assert.Equal(1, received);
    }

    // ── CPU affinity ──

    [Fact]
    public void Affinity_mask_is_built_from_the_bit_string()
    {
        int n = Environment.ProcessorCount;
        string onlyHighest = "1" + new string('0', n - 1);
        Assert.Equal(1L << (n - 1), (long)Functions.CPU.Affinity.GetAffinityIntPtr(onlyHighest));

        // Over-long strings are cut to the processor count (legacy turned them into garbage).
        string overLong = new string('0', n - 1) + "1" + "0000";
        Assert.Equal(new string('0', n - 1) + "1", Functions.CPU.Affinity.GetAffinityValidatedString(overLong));
    }
}
