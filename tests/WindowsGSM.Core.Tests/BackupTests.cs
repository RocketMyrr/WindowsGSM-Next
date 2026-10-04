using System.IO.Compression;
using WindowsGSM.Engine;
using WindowsGSM.Engine.Backups;
using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Engine.Services;
using WindowsGSM.Functions;
using WindowsGSM.Hosting;

namespace WindowsGSM.Core.Tests;

/// <summary>
/// One backup system with the safety legacy lacked: retention after success, staged restores with
/// rollback, and restores that can't write outside the server's allowed locations. Legacy archives restore.
/// </summary>
[Collection("Lifecycle")]
public class BackupTests
{
    private static async Task<JobSnapshot> Run(OperationRequest request)
    {
        Assert.True(request.Accepted, request.Error);
        return await request.Job!.Completion.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static string Files(string id, string rel = "") => Path.Combine(ServerPath.GetServersServerFiles(id), rel);

    private static void Write(string id, string rel, string content)
    {
        string path = Files(id, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void Settings(string id, Action<BackupSettings> change)
    {
        var s = BackupSettings.Load(id);
        change(s);
        s.Save();
    }

    private static void ResetBackups(string id)
    {
        string dir = Path.Combine(WgsmEnvironment.DataRoot, "backups", id);
        if (Directory.Exists(dir)) { TestData.DeleteDirectory(dir); }
        foreach (var f in new[] { "backup.json", "BackupConfig.cfg", "webbackup.json" })
        {
            string p = ServerPath.GetServersConfigs(id, f);
            if (File.Exists(p)) { TestData.DeleteFile(p); }
        }
    }

    [Fact]
    public async Task Whole_backup_then_restore_brings_back_exactly_the_old_files()
    {
        EngineFixture.CreateServer("221");
        ResetBackups("221");
        Write("221", "world/level.dat", "original");
        Write("221", "server.cfg", "cfg-original");
        using var engine = await EngineFixture.StartEngineAsync();

        Assert.Equal(JobStatus.Succeeded, (await Run(engine.Backups.Backup("221"))).Status);
        var backup = Assert.Single(engine.Backups.List("221"));
        Assert.Equal(BackupFormat.Next, backup.Format);
        using (var zip = ZipFile.OpenRead(Path.Combine(WgsmEnvironment.DataRoot, "backups", "221", backup.Name)))
        {
            Assert.Contains(zip.Entries, e => e.FullName == "serverfiles/world/level.dat");
            Assert.Contains(zip.Entries, e => e.FullName == "configs/WindowsGSM.cfg");
            Assert.Contains(zip.Entries, e => e.FullName == "wgsm-backup.json");
        }

        Write("221", "world/level.dat", "changed");
        Write("221", "created-after.txt", "new");

        var restored = await Run(engine.Backups.Restore("221", backup.Name));

        Assert.Equal(JobStatus.Succeeded, restored.Status);
        Assert.Equal("original", File.ReadAllText(Files("221", "world/level.dat")));
        Assert.Equal("cfg-original", File.ReadAllText(Files("221", "server.cfg")));
        Assert.False(File.Exists(Files("221", "created-after.txt")));     // whole restore = exactly the backup
        Assert.Empty(Directory.GetDirectories(ServerPath.GetServers("221"), ".restore-staging-*"));
        Assert.Empty(Directory.GetDirectories(ServerPath.GetServers("221"), "*.wgsm-old-*"));
        Assert.Equal(ServerState.Stopped, engine.Servers.Get("221")!.State);
    }

    [Fact]
    public async Task Retention_keeps_the_newest_and_only_prunes_after_a_successful_backup()
    {
        EngineFixture.CreateServer("222");
        ResetBackups("222");
        Write("222", "a.txt", "a");
        Settings("222", s => s.KeepCount = 2);
        using var engine = await EngineFixture.StartEngineAsync();

        for (int i = 0; i < 4; i++) { Assert.Equal(JobStatus.Succeeded, (await Run(engine.Backups.Backup("222"))).Status); }
        var names = engine.Backups.List("222").Select(b => b.Name).ToList();
        Assert.Equal(2, names.Count);

        // A backup that fails (nothing to back up) must not cost an existing backup.
        Settings("222", s => s.Paths = new List<string> { "does-not-exist" });
        Assert.Equal(JobStatus.Failed, (await Run(engine.Backups.Backup("222"))).Status);
        Assert.Equal(names, engine.Backups.List("222").Select(b => b.Name).ToList());
    }

    [Fact]
    public async Task Selective_backup_restores_only_its_paths()
    {
        EngineFixture.CreateServer("223");
        ResetBackups("223");
        Write("223", "world/save.dat", "world-v1");
        Write("223", "logs/big.log", "log-v1");
        Settings("223", s => s.Paths = new List<string> { "world" });
        using var engine = await EngineFixture.StartEngineAsync();

        await Run(engine.Backups.Backup("223"));
        Write("223", "world/save.dat", "world-v2");
        Write("223", "logs/big.log", "log-v2");

        await Run(engine.Backups.Restore("223", engine.Backups.List("223").Single().Name));

        Assert.Equal("world-v1", File.ReadAllText(Files("223", "world/save.dat")));
        Assert.Equal("log-v2", File.ReadAllText(Files("223", "logs/big.log"))); // not in the backup → untouched
    }

    [Fact]
    public async Task A_failed_restore_puts_every_original_back()
    {
        EngineFixture.CreateServer("224");
        ResetBackups("224");
        Write("224", "a/one.txt", "a-v1");
        Write("224", "b/two.txt", "b-v1");
        Settings("224", s => s.Paths = new List<string> { "a", "b" });
        using var engine = await EngineFixture.StartEngineAsync();
        await Run(engine.Backups.Backup("224"));

        Write("224", "a/one.txt", "a-v2");
        Write("224", "b/two.txt", "b-v2");

        // Hold a file open inside "b" so the restore can swap "a" but then fails on "b".
        JobSnapshot result;
        using (new FileStream(Files("224", "b/two.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await Run(engine.Backups.Restore("224", engine.Backups.List("224").Single().Name));
        }

        Assert.Equal(JobStatus.Failed, result.Status);
        Assert.Equal("a-v2", File.ReadAllText(Files("224", "a/one.txt"))); // "a" was rolled back, not left restored
        Assert.Equal("b-v2", File.ReadAllText(Files("224", "b/two.txt")));
        Assert.Empty(Directory.GetDirectories(Files("224"), "*.wgsm-old-*"));
        Assert.Equal(ServerState.Stopped, engine.Servers.Get("224")!.State);
    }

    [Fact]
    public async Task Restore_never_writes_outside_the_servers_allowed_locations()
    {
        // A legacy-format archive whose manifest points at an arbitrary folder on the machine.
        EngineFixture.CreateServer("225");
        ResetBackups("225");
        Write("225", "inside.txt", "server-v2");
        string outside = Path.Combine(Path.GetTempPath(), "wgsm-outside-" + Guid.NewGuid().ToString("N"));
        string archiveDir = Path.Combine(WgsmEnvironment.DataRoot, "backups", "225");
        Directory.CreateDirectory(archiveDir);
        string archive = Path.Combine(archiveDir, "WGSM-Backup-Server-225-20260101120000.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("backup_manifest.txt").Open()))
            {
                w.WriteLine($"0|F|{Files("225", "inside.txt")}");
                w.WriteLine($"1|D|{outside}");
            }
            using (var w = new StreamWriter(zip.CreateEntry("save_0/inside.txt").Open())) { w.Write("server-v1"); }
            using (var w = new StreamWriter(zip.CreateEntry("save_1/evil.txt").Open())) { w.Write("should never be written"); }
        }
        using var engine = await EngineFixture.StartEngineAsync();

        var backup = engine.Backups.List("225").Single();
        Assert.Equal(BackupFormat.LegacyDesktop, backup.Format);
        Assert.Equal(JobStatus.Succeeded, (await Run(engine.Backups.Restore("225", backup.Name))).Status);

        Assert.Equal("server-v1", File.ReadAllText(Files("225", "inside.txt"))); // legacy archive restored…
        Assert.False(Directory.Exists(outside));                                  // …but only where allowed
    }

    [Fact]
    public async Task Legacy_web_backups_are_listed_and_restorable()
    {
        EngineFixture.CreateServer("226");
        ResetBackups("226");
        Write("226", "maps/map1.bsp", "v2");
        string web = Path.Combine(WgsmEnvironment.DataRoot, "backups", "226", "web");
        Directory.CreateDirectory(web);
        using (var zip = ZipFile.Open(Path.Combine(web, "backup-20260101-120000.zip"), ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("maps/map1.bsp").Open())) { w.Write("v1"); }
        using var engine = await EngineFixture.StartEngineAsync();

        var backup = engine.Backups.List("226").Single();
        Assert.Equal(("web/backup-20260101-120000.zip", BackupFormat.LegacyWeb), (backup.Name, backup.Format));
        await Run(engine.Backups.Restore("226", backup.Name));
        Assert.Equal("v1", File.ReadAllText(Files("226", "maps/map1.bsp")));
    }

    [Fact]
    public void Settings_are_built_from_both_legacy_backup_configs()
    {
        EngineFixture.CreateServer("227", extraSettings: "backuponstart=\"1\"");
        ResetBackups("227");
        File.WriteAllLines(ServerPath.GetServersConfigs("227", "BackupConfig.cfg"), new[]
        {
            "// comment",
            $"saveslocation=\"{Files("227", "Saved")}|C:\\SomewhereElse\\Worlds\"",
            "maximumbackups=\"5\"",
        });
        File.WriteAllText(ServerPath.GetServersConfigs("227", "webbackup.json"), "{\"Paths\":[\"maps\"],\"BeforeStart\":false,\"RetentionDays\":14}");

        var s = BackupSettings.Load("227");

        Assert.Equal(5, s.KeepCount);
        Assert.Equal(14, s.KeepDays);
        Assert.True(s.BeforeStart); // from the desktop app's backuponstart switch
        Assert.Equal(new[] { "Saved", "maps" }, s.Paths);
        Assert.Equal(new[] { @"C:\SomewhereElse\Worlds" }, s.ExternalLocations);
        Assert.True(File.Exists(ServerPath.GetServersConfigs("227", "BackupConfig.cfg"))); // legacy files untouched
    }

    [Fact]
    public async Task Backup_before_start_runs_first_and_restart_skips_it()
    {
        EngineFixture.CreateServer("228");
        ResetBackups("228");
        Write("228", "a.txt", "a");
        Settings("228", s => s.BeforeStart = true);
        using var engine = await EngineFixture.StartEngineAsync();
        engine.Lifecycle.RestartPause = TimeSpan.FromMilliseconds(50);
        var s = engine.Servers.Get("228")!;
        try
        {
            await Run(engine.Lifecycle.Start("228"));
            Assert.Single(engine.Backups.List("228"));
            await Run(engine.Lifecycle.Restart("228"));
            Assert.Single(engine.Backups.List("228")); // a plain restart doesn't back up (legacy behaviour)
        }
        finally { EngineFixture.KillQuietly(s.Process); }
    }
}
