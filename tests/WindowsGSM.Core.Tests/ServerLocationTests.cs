using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Engine.Services;
using WindowsGSM.Functions;

namespace WindowsGSM.Core.Tests;

/// <summary>
/// Game files on another drive: serverfiles becomes a junction to a folder elsewhere. Install, move there and back,
/// back up and restore, delete, and a drive that isn't connected. The "other drive" is a folder under %TEMP% (on C:,
/// while the test data root is next to the test binaries), so these run across two real drives here.
/// </summary>
[Collection("Lifecycle")]
public class ServerLocationTests
{
    private static async Task<JobSnapshot> Run(OperationRequest request)
    {
        Assert.True(request.Accepted, request.Error);
        return await request.Job!.Completion.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static string OtherDrive() => Path.Combine(Path.GetTempPath(), "wgsm-elsewhere-" + Guid.NewGuid().ToString("N")[..8]);

    private static bool IsJunction(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    [Fact]
    public async Task A_server_installed_elsewhere_runs_from_there_and_deleting_it_removes_those_files_too()
    {
        string other = OtherDrive();
        using var engine = await EngineFixture.StartEngineAsync();
        try
        {
            var job = await Run(engine.Provisioning.Install(new InstallRequest(EngineFixture.GameName, "Far Away", Consents: new[] { UserPrompt.Keys.Eula }, FilesFolder: other)));
            Assert.Equal(JobStatus.Succeeded, job.Status);
            string id = job.ServerId!;

            string link = ServerLocation.LinkPath(id), real = ServerLocation.RealPath(id);
            Assert.True(IsJunction(link));
            Assert.StartsWith(other, real, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Far Away", real);
            Assert.True(File.Exists(Path.Combine(real, "game.exe")));                 // the game wrote through the usual path…
            Assert.Equal(real, ServerConfig.GetSetting(id, ServerLocation.SettingKey)); // …and where it really is is recorded
            Assert.Contains(engine.Files.List(id, null, out _), e => e.Name == "game.exe"); // the file manager sees it

            Assert.Equal(JobStatus.Succeeded, (await Run(engine.Provisioning.Delete(id))).Status);
            Assert.False(Directory.Exists(real));                                     // gone from the other drive too
            Assert.False(Directory.Exists(ServerPath.GetServers(id)));
        }
        finally { try { Directory.Delete(other, true); } catch { } }
    }

    [Fact]
    public async Task Files_move_to_another_drive_and_back_and_the_old_copy_goes()
    {
        string other = OtherDrive();
        EngineFixture.CreateServer("236");
        string usual = ServerLocation.LinkPath("236");
        Directory.CreateDirectory(Path.Combine(usual, "world", "region"));
        File.WriteAllText(Path.Combine(usual, "game.exe"), "game");
        File.WriteAllText(Path.Combine(usual, "world", "region", "r.0.0.mca"), new string('x', 5000));
        Directory.CreateDirectory(Path.Combine(usual, "empty-folder"));
        using var engine = await EngineFixture.StartEngineAsync();
        try
        {
            Assert.Equal(JobStatus.Succeeded, (await Run(engine.Provisioning.MoveFiles("236", other))).Status);
            string real = ServerLocation.RealPath("236");
            Assert.True(IsJunction(usual));
            Assert.StartsWith(other, real, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(5000, new FileInfo(Path.Combine(real, "world", "region", "r.0.0.mca")).Length);
            Assert.True(Directory.Exists(Path.Combine(real, "empty-folder")));       // empty folders come along
            Assert.Equal("game", File.ReadAllText(Path.Combine(usual, "game.exe")));  // still reachable the usual way
            Assert.False(Directory.Exists(usual + ".wgsm-moving"));                   // the original copy was removed

            // Moving onto itself, or into WindowsGSM's own folder, is refused before anything happens.
            Assert.False(engine.Provisioning.MoveFiles("236", ServerPath.Get("servers")).Accepted);

            Assert.Equal(JobStatus.Succeeded, (await Run(engine.Provisioning.MoveFiles("236", null))).Status);
            Assert.False(IsJunction(usual));
            Assert.Equal(5000, new FileInfo(Path.Combine(usual, "world", "region", "r.0.0.mca")).Length);
            Assert.False(Directory.Exists(real));                                     // the far copy was removed
            Assert.Equal("", ServerConfig.GetSetting("236", ServerLocation.SettingKey));
            Assert.Equal(ServerState.Stopped, engine.Servers.Get("236")!.State);
        }
        finally
        {
            try { ServerLocation.Unlink("236"); } catch { }
            try { Directory.Delete(other, true); } catch { }
        }
    }

    [Fact]
    public async Task Backups_of_a_server_elsewhere_hold_its_files_and_restore_back_there()
    {
        string other = OtherDrive();
        EngineFixture.CreateServer("237");
        string usual = ServerLocation.LinkPath("237");
        File.WriteAllText(Path.Combine(usual, "save.dat"), "day 1");
        using var engine = await EngineFixture.StartEngineAsync();
        try
        {
            Assert.Equal(JobStatus.Succeeded, (await Run(engine.Provisioning.MoveFiles("237", other))).Status);
            string real = ServerLocation.RealPath("237");

            Assert.Equal(JobStatus.Succeeded, (await Run(engine.Backups.Backup("237"))).Status);
            var backup = engine.Backups.List("237").First();
            Assert.True(backup.Size > 0);

            File.WriteAllText(Path.Combine(real, "save.dat"), "day 2 — broken");
            Assert.Equal(JobStatus.Succeeded, (await Run(engine.Backups.Restore("237", backup.Name))).Status);

            Assert.True(IsJunction(usual));                                           // still on the other drive…
            Assert.Equal(real, ServerLocation.RealPath("237"));
            Assert.Equal("day 1", File.ReadAllText(Path.Combine(real, "save.dat")));  // …with the restored files there
            Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(real)!, "*.wgsm-old-*")); // the replaced copy was cleaned up
        }
        finally
        {
            try { ServerLocation.Unlink("237"); } catch { }
            try { Directory.Delete(other, true); } catch { }
        }
    }

    [Fact]
    public async Task A_disconnected_drive_is_named_and_the_server_wont_start()
    {
        string other = OtherDrive();
        EngineFixture.CreateServer("238");
        using var engine = await EngineFixture.StartEngineAsync();
        try
        {
            Assert.Equal(JobStatus.Succeeded, (await Run(engine.Provisioning.MoveFiles("238", other))).Status);
            Directory.Delete(other, true); // as if the drive was unplugged

            Assert.Contains("missing", ServerLocation.Problem("238"));
            var start = await Run(engine.Lifecycle.Start("238"));
            Assert.Equal(JobStatus.Failed, start.Status);
            Assert.Contains("missing", start.Error);
            Assert.False(engine.Provisioning.MoveFiles("238", null).Accepted); // nothing to move from
        }
        finally
        {
            try { ServerLocation.Unlink("238"); } catch { }
            try { Directory.Delete(other, true); } catch { }
        }
    }

    [Theory]
    [InlineData(@"\\nas\games", "Network shares")]
    [InlineData(@"C:\", "not the whole drive")]
    public void Unsuitable_folders_are_refused_in_plain_words(string folder, string expected) =>
        Assert.Contains(expected, ServerLocation.Validate(folder));

    [Fact]
    public void A_folder_inside_WindowsGSM_is_refused_and_a_fresh_one_is_fine()
    {
        Assert.Contains("inside WindowsGSM", ServerLocation.Validate(Path.Combine(TestData.DataRoot, "elsewhere")));
        Assert.Null(ServerLocation.Validate(OtherDrive()));
        Assert.Equal(Path.Combine(@"E:\Games", "7 - My Server"), ServerLocation.FolderFor(@"E:\Games", "7", "My Server"));
        Assert.Equal(Path.Combine(@"E:\Games", "7 - ab"), ServerLocation.FolderFor(@"E:\Games", "7", "a<>b"));
    }
}
