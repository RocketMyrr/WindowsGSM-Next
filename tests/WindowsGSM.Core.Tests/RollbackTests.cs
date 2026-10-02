using System.Diagnostics;
using System.IO.Compression;
using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Services;
using WindowsGSM.Functions;
using WindowsGSM.Installer;

namespace WindowsGSM.Core.Tests;

/// <summary>Roll back a game update from DepotDownloader's own manifests (not backups), then hold updates.</summary>
[Collection("Lifecycle")]
public class RollbackTests
{
    /// <summary>Writes depot.config the way DepotDownloader does: deflated protobuf, field 1 = { 1: depot, 2: manifest }.</summary>
    internal static void WriteDepotConfig(string serverId, IReadOnlyDictionary<uint, ulong> depots)
    {
        var proto = new List<byte>();
        foreach (var (depot, manifest) in depots)
        {
            var entry = new List<byte> { 0x08 };
            entry.AddRange(Varint(depot));
            entry.Add(0x10);
            entry.AddRange(Varint(manifest));
            proto.Add(0x0A);
            proto.AddRange(Varint((ulong)entry.Count));
            proto.AddRange(entry);
        }
        Directory.CreateDirectory(DepotHistory.ToolFolder(serverId));
        using var file = File.Create(Path.Combine(DepotHistory.ToolFolder(serverId), "depot.config"));
        using var deflate = new DeflateStream(file, CompressionLevel.Optimal);
        deflate.Write(proto.ToArray());
    }

    private static IEnumerable<byte> Varint(ulong v)
    {
        do { byte b = (byte)(v & 0x7F); v >>= 7; yield return v != 0 ? (byte)(b | 0x80) : b; } while (v != 0);
    }

    private static void Manifest(string serverId, uint depot, ulong manifest, DateTime at)
    {
        string path = Path.Combine(DepotHistory.ToolFolder(serverId), $"{depot}_{manifest}.manifest");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, at);
    }

    /// <summary>Three updates: May (both depots), June (only 4001 changed), July (both). Installed now: July.</summary>
    private static void History(string id)
    {
        var may = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        Manifest(id, 4001, 111, may); Manifest(id, 4002, 222, may.AddMinutes(1)); Manifest(id, 228989, 999, may.AddMinutes(1));
        Manifest(id, 4001, 333, new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc));
        var jul = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        Manifest(id, 4001, 555, jul); Manifest(id, 4002, 666, jul.AddSeconds(30));
        WriteDepotConfig(id, new Dictionary<uint, ulong> { [4001] = 555, [4002] = 666, [228989] = 999 });
    }

    [Fact]
    public void Builds_are_rebuilt_from_the_manifests_carrying_unchanged_depots_forward()
    {
        EngineFixture.CreateServer("251", mode: "steam");
        History("251");

        var builds = DepotHistory.Builds("251");

        Assert.Equal(3, builds.Count);
        Assert.True(builds[0].Current);
        Assert.Equal(new Dictionary<uint, ulong> { [4001] = 555, [4002] = 666, [228989] = 999 }, builds[0].Depots);
        Assert.Equal(new Dictionary<uint, ulong> { [4001] = 333, [4002] = 222, [228989] = 999 }, builds[1].Depots); // June: 4002 carried from May
        Assert.Equal(new Dictionary<uint, ulong> { [4001] = 111, [4002] = 222, [228989] = 999 }, builds[2].Depots);
        Assert.Null(builds[1].BuildId);

        // A recorded build gets its Steam build number.
        DepotHistory.Record("251", "18000000");
        Assert.Equal("18000000", DepotHistory.Builds("251")[0].BuildId);
    }

    [Fact]
    public async Task Rolling_back_downloads_those_manifests_and_holds_updates()
    {
        EngineFixture.CreateServer("252", mode: "steam");
        History("252");
        using var engine = await EngineFixture.StartEngineAsync();
        var june = DepotHistory.Builds("252")[1];
        IReadOnlyDictionary<uint, ulong>? asked = null;
        engine.Updates.RollbackRunner = (id, app, depots, anonymous) =>
        {
            Assert.Equal("4000", app);
            asked = depots;
            WriteDepotConfig(id, depots); // what DepotDownloader does
            var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
            return Task.FromResult<(Process?, string?)>((p, null));
        };

        var request = engine.Updates.Rollback("252", june.Key);
        Assert.True(request.Accepted, request.Error);
        var job = await request.Job!.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(june.Depots, asked);
        Assert.True(DepotHistory.Builds("252").Single(b => b.Current).Key == june.Key);
        var s = engine.Servers.Get("252")!;
        Assert.True(UpdateService.IsHeld(s));

        // The current build can't be "rolled back" to, and resuming clears the hold.
        Assert.False(engine.Updates.Rollback("252", june.Key).Accepted);
        engine.Updates.SetHold("252", false);
        Assert.False(UpdateService.IsHeld(engine.Servers.Get("252")!));
    }

    [Fact]
    public async Task A_roll_back_that_doesnt_land_on_the_build_fails()
    {
        EngineFixture.CreateServer("253", mode: "steam");
        History("253");
        using var engine = await EngineFixture.StartEngineAsync();
        engine.Updates.RollbackRunner = (id, app, depots, anonymous) =>
        {
            var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
            return Task.FromResult<(Process?, string?)>((p, null)); // "succeeds" but nothing changed (Steam refused the manifest)
        };

        var job = await engine.Updates.Rollback("253", DepotHistory.Builds("253")[2].Key).Job!.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("aren't that build", job.Error);
        Assert.False(UpdateService.IsHeld(engine.Servers.Get("253")!));
    }

    [Fact]
    public async Task A_scheduled_update_is_skipped_while_held_and_verify_checks_the_held_build()
    {
        EngineFixture.CreateServer("255", "steam", false, "updatehold=\"1\"");
        History("255");
        WriteDepotConfig("255", new Dictionary<uint, ulong> { [4001] = 333, [4002] = 222, [228989] = 999 }); // rolled back to June
        using var engine = await EngineFixture.StartEngineAsync();
        var events = EngineFixture.Record(engine, "255");

        // Scheduled update: skipped, and the hold stays.
        Assert.Null(engine.Scheduler.SaveManaged("255", new[] { new ScheduleEntry("0 6 * * *", ScheduledAction.Update) }));
        var now = new DateTime(2026, 1, 1, 5, 59, 0);
        engine.Scheduler.Clock = () => now;
        await engine.Scheduler.TickAsync();
        now = now.AddMinutes(2);
        await engine.Scheduler.TickAsync();
        lock (events) { Assert.Contains(events, e => e is WindowsGSM.Engine.Events.ServerLogged l && l.Message.Contains("Update skipped")); }
        Assert.True(UpdateService.IsHeld(engine.Servers.Get("255")!));

        // Verify files: re-checks exactly the held manifests (a plain validate would install the newest build).
        IReadOnlyDictionary<uint, ulong>? asked = null;
        engine.Updates.RollbackRunner = (id, app, depots, anonymous) =>
        {
            asked = depots;
            var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
            return Task.FromResult<(Process?, string?)>((p, null));
        };
        var request = engine.Updates.Update("255", validate: true);
        Assert.True(request.Accepted, request.Error);
        var job = await request.Job!.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(new Dictionary<uint, ulong> { [4001] = 333, [4002] = 222, [228989] = 999 }, asked);
        Assert.False(File.Exists(ServerPath.GetServersServerFiles("255", "updated.txt"))); // the plugin's updater never ran
        Assert.True(UpdateService.IsHeld(engine.Servers.Get("255")!));
    }

    [Fact]
    public async Task Update_on_start_is_skipped_while_updates_are_held()
    {
        EngineFixture.CreateServer("254", "long", false, "updateonstart=\"1\"", "updatehold=\"1\"");
        using var engine = await EngineFixture.StartEngineAsync();
        var s = engine.Servers.Get("254")!;
        try
        {
            var request = engine.Lifecycle.Start("254");
            Assert.True(request.Accepted, request.Error);
            var job = await request.Job!.Completion.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Contains(job.RecentLog, l => l.Contains("updates are on hold"));
            Assert.False(File.Exists(ServerPath.GetServersServerFiles("254", "updated.txt")));
        }
        finally { EngineFixture.KillQuietly(s.Process); }
    }
}
