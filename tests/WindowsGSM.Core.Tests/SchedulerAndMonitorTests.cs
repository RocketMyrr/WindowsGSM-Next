using WindowsGSM.Engine.Events;
using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Engine.Services;
using WindowsGSM.Functions;
using WindowsGSM.Hosting;

namespace WindowsGSM.Core.Tests;

/// <summary>Scheduler (legacy crontab compatibility + managed schedules) and the monitor (players, health, memory guard).</summary>
[Collection("Lifecycle")]
public class SchedulerAndMonitorTests
{
    private static async Task<JobSnapshot> Run(OperationRequest request)
    {
        Assert.True(request.Accepted, request.Error);
        return await request.Job!.Completion.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static void CrontabCsv(string id, params string[] lines)
    {
        string dir = ServerPath.GetServersConfigs(id, "Crontab");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "tasks.csv"), lines);
    }

    // ── Legacy .csv format ──

    [Fact]
    public void Legacy_crontab_files_parse_every_documented_form()
    {
        var entries = SchedulerService.ParseCrontabFile(new[]
        {
            "// comment",
            "5 1 * * *;exec;cmd.exe;/C \"C:\\Full\\Path\\To\\test.bat\"",
            "5 * * * *;ServerConsoleCommand;cheat serverchat hello; with a semicolon",
            "5 6 * * *;RconCommand;say restart in 5",
            "* 2 * * *;restart",
            "not a cron;restart",
        }).ToList();

        Assert.Equal(4, entries.Count);
        Assert.Equal((ScheduledAction.Exec, "cmd.exe", "/C \"C:\\Full\\Path\\To\\test.bat\""), (entries[0].Action, entries[0].Payload, entries[0].Arguments));
        Assert.Equal((ScheduledAction.Command, "cheat serverchat hello; with a semicolon"), (entries[1].Action, entries[1].Payload));
        Assert.Equal((ScheduledAction.Rcon, "say restart in 5"), (entries[2].Action, entries[2].Payload));
        Assert.Equal(ScheduledAction.Restart, entries[3].Action);
        Assert.All(entries, e => Assert.Equal(ScheduleSource.CrontabFile, e.Source));
    }

    [Fact]
    public async Task Managed_schedules_reject_programs_and_bad_expressions()
    {
        EngineFixture.CreateServer("201");
        using var engine = await EngineFixture.StartEngineAsync();

        Assert.Contains("Crontab .csv", engine.Scheduler.SaveManaged("201", new[] { new ScheduleEntry("* * * * *", ScheduledAction.Exec, "cmd.exe") }));
        Assert.Contains("isn't a valid schedule", engine.Scheduler.SaveManaged("201", new[] { new ScheduleEntry("every day", ScheduledAction.Restart) }));
        Assert.Null(engine.Scheduler.SaveManaged("201", new[] { new ScheduleEntry("0 6 * * *", ScheduledAction.Backup) }));
        Assert.Contains(engine.Scheduler.GetSchedules("201"), e => e.Source == ScheduleSource.Managed && e.Action == ScheduledAction.Backup);
    }

    // ── Firing (time is moved with the scheduler's clock, not waited for) ──

    [Fact]
    public async Task Restart_schedule_restarts_a_running_server_at_its_time()
    {
        EngineFixture.CreateServer("202", extraSettings: new[] { "restartcrontab=\"1\"", "crontabformat=\"0 6 * * *\"" });
        using var engine = await EngineFixture.StartEngineAsync();
        engine.Lifecycle.RestartPause = TimeSpan.FromMilliseconds(50);
        var s = engine.Servers.Get("202")!;
        var now = new DateTime(2026, 1, 1, 5, 59, 0);
        engine.Scheduler.Clock = () => now;
        System.Diagnostics.Process? before = null;
        try
        {
            await Run(engine.Lifecycle.Start("202"));
            before = s.Process;
            await engine.Scheduler.TickAsync(); // arms: next run 06:00
            now = new DateTime(2026, 1, 1, 6, 0, 1);
            await engine.Scheduler.TickAsync(); // due

            await EngineFixture.WaitUntil(() => s.Process != null && s.Process.Id != before!.Id && s.State == ServerState.Running, "scheduled restart");
        }
        finally { EngineFixture.KillQuietly(before); EngineFixture.KillQuietly(s.Process); }
    }

    [Fact]
    public async Task Crontab_file_tasks_only_run_with_restart_on_schedule_on_like_legacy()
    {
        EngineFixture.CreateServer("203", extraSettings: "restartcrontab=\"0\"");
        CrontabCsv("203", "0 6 * * *;ServerConsoleCommand;say hi");
        using var engine = await EngineFixture.StartEngineAsync();
        var events = EngineFixture.Record(engine, "203");
        var s = engine.Servers.Get("203")!;
        var now = new DateTime(2026, 1, 1, 5, 59, 0);
        engine.Scheduler.Clock = () => now;
        try
        {
            await Run(engine.Lifecycle.Start("203"));
            await engine.Scheduler.TickAsync();
            now = now.AddMinutes(2);
            await engine.Scheduler.TickAsync();
            lock (events) { Assert.DoesNotContain(events, e => e is ServerLogged l && l.Message.Contains("Scheduled Command")); }
        }
        finally { EngineFixture.KillQuietly(s.Process); }
    }

    [Fact]
    public async Task Managed_start_schedule_starts_a_stopped_server()
    {
        EngineFixture.CreateServer("204");
        using var engine = await EngineFixture.StartEngineAsync();
        Assert.Null(engine.Scheduler.SaveManaged("204", new[] { new ScheduleEntry("30 7 * * *", ScheduledAction.Start) }));
        var s = engine.Servers.Get("204")!;
        var now = new DateTime(2026, 1, 1, 7, 29, 0);
        engine.Scheduler.Clock = () => now;
        try
        {
            await engine.Scheduler.TickAsync();
            Assert.Equal(ServerState.Stopped, s.State); // arming never fires a run it didn't see coming
            now = new DateTime(2026, 1, 1, 7, 30, 2);
            await engine.Scheduler.TickAsync();
            await EngineFixture.WaitUntil(() => s.State == ServerState.Running, "scheduled start");
        }
        finally { EngineFixture.KillQuietly(s.Process); }
    }

    [Fact]
    public async Task Auto_start_starts_only_servers_that_ask_for_it()
    {
        EngineFixture.CreateServer("205", extraSettings: "autostart=\"1\"");
        EngineFixture.CreateServer("206");
        using var engine = await EngineFixture.StartEngineAsync();
        var a = engine.Servers.Get("205")!;
        var b = engine.Servers.Get("206")!;
        try
        {
            engine.Scheduler.StartAutoStartServers();
            await EngineFixture.WaitUntil(() => a.State == ServerState.Running, "auto-start server");
            Assert.Equal(ServerState.Stopped, b.State);
        }
        finally
        {
            EngineFixture.KillQuietly(a.Process);
            // Leave nothing that would auto-start in other tests' engines.
            EngineFixture.CreateServer("205");
        }
    }

    // ── Monitor ──

    [Fact]
    public async Task Monitor_samples_memory_and_reports_players_from_the_games_query()
    {
        EngineFixture.CreateServer("207", mode: "players");
        using var engine = await EngineFixture.StartEngineAsync();
        var events = EngineFixture.Record(engine, "207");
        var s = engine.Servers.Get("207")!;
        try
        {
            await Run(engine.Lifecycle.Start("207"));
            await engine.Monitor.TickAsync();
            await engine.Monitor.TickAsync();

            var sample = engine.Monitor.Latest("207")!;
            Assert.True(sample.MemoryMb > 0);
            Assert.Equal((3, 10), (sample.Players, sample.MaxPlayers));
            Assert.Equal(2, engine.Monitor.History("207").Count);
            lock (events)
            {
                var players = Assert.Single(events.OfType<PlayersChanged>()); // published once, not every tick
                Assert.Equal(new[] { "Alice", "Bob", "Cara" }, players.Players.Select(p => p.Name));
            }
        }
        finally { EngineFixture.KillQuietly(s.Process); }
    }

    [Fact]
    public async Task The_player_query_finds_the_port_the_game_really_answers_on()
    {
        // The game answers A2S on its game port; the configured query port is wrong (nothing there).
        using var game = new A2SQueryTests.FakeServer { Reply = req => new() { A2SQueryTests.Single(req[4] == 0x54 ? A2SQueryTests.Info(4, 20) : A2SQueryTests.Players("Ann", "Ben", "Cy", "Di")) } };
        int wrong;
        using (var probe = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0))) { wrong = ((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Port; }
        EngineFixture.CreateServer("212", mode: "a2s");
        ServerConfig.SetSetting("212", ServerConfig.SettingName.ServerIP, "203.0.113.9"); // a public address — must not be asked
        ServerConfig.SetSetting("212", ServerConfig.SettingName.ServerPort, game.Port.ToString());
        ServerConfig.SetSetting("212", ServerConfig.SettingName.ServerQueryPort, wrong.ToString());
        using var engine = await EngineFixture.StartEngineAsync();
        var s = engine.Servers.Get("212")!;
        try
        {
            await Run(engine.Lifecycle.Start("212"));
            await engine.Monitor.TickAsync();
            await engine.Monitor.TickAsync(); // second miss → looks for the port in the background
            await EngineFixture.WaitUntil(() => engine.Monitor.QueryState("212").Endpoint == $"127.0.0.1:{game.Port}", "the answering port to be found");
            await engine.Monitor.TickAsync();

            var sample = engine.Monitor.Latest("212")!;
            Assert.Equal((4, 20), (sample.Players, sample.MaxPlayers));
            Assert.Equal(new[] { "Ann", "Ben", "Cy", "Di" }, engine.Monitor.Players("212").Select(p => p.Name));
            var state = engine.Monitor.QueryState("212");
            Assert.True(state.Answering);
            Assert.Contains($"port {game.Port}", state.Note);
        }
        finally { EngineFixture.KillQuietly(s.Process); }
    }

    [Fact]
    public async Task A_server_that_stops_answering_its_query_is_killed_and_restarted()
    {
        EngineFixture.CreateServer("208", mode: "hung", autoRestart: true);
        using var engine = await EngineFixture.StartEngineAsync();
        engine.Monitor.Options.HealthChecks = true;
        engine.Monitor.Options.HealthGrace = TimeSpan.Zero;
        engine.Monitor.Options.HealthInterval = TimeSpan.Zero;
        engine.Monitor.Options.HealthFailuresBeforeRestart = 2;
        var s = engine.Servers.Get("208")!;
        System.Diagnostics.Process? first = null;
        try
        {
            await Run(engine.Lifecycle.Start("208"));
            first = s.Process;
            await engine.Monitor.TickAsync();
            await engine.Monitor.TickAsync(); // second silent probe → treated as hung

            await EngineFixture.WaitUntil(() => first!.HasExited, "hung process to be killed");
            await EngineFixture.WaitUntil(() => s.State == ServerState.Running && s.Process?.Id != first!.Id, "auto-restart after the hang");
        }
        finally { EngineFixture.KillQuietly(first); EngineFixture.KillQuietly(s.Process); }
    }

    [Fact]
    public async Task Memory_guard_restarts_a_server_that_stays_over_its_threshold()
    {
        EngineFixture.CreateServer("209", extraSettings: new[] { "memoryguard=\"1\"", "memoryguardthresholdmb=\"1\"", "memoryguardsustainminutes=\"1\"" });
        using var engine = await EngineFixture.StartEngineAsync();
        engine.Lifecycle.RestartPause = TimeSpan.FromMilliseconds(50);
        var events = EngineFixture.Record(engine, "209");
        var s = engine.Servers.Get("209")!;
        System.Diagnostics.Process? first = null;
        try
        {
            await Run(engine.Lifecycle.Start("209"));
            first = s.Process;
            await engine.Monitor.TickAsync();                                 // over 1 MB → starts the clock
            Assert.NotNull(s.MemoryOverThresholdSince);
            s.MemoryOverThresholdSince = DateTimeOffset.Now.AddMinutes(-2);   // …as if two minutes had passed
            await engine.Monitor.TickAsync();

            await EngineFixture.WaitUntil(() => s.State == ServerState.Running && s.Process?.Id != first!.Id, "memory-guard restart");
            lock (events) { Assert.Contains(events, e => e is ServerAlert { Kind: AlertKind.MemoryGuard }); }
        }
        finally { EngineFixture.KillQuietly(first); EngineFixture.KillQuietly(s.Process); }
    }
}
