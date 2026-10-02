using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Engine.Services;
using WindowsGSM.Functions;
using WindowsGSM.Hosting;

namespace WindowsGSM.Core.Tests;

/// <summary>Install / import / delete through the real plugin loader, as jobs.</summary>
[Collection("Lifecycle")]
public class ProvisioningTests
{
    private static async Task<JobSnapshot> Run(OperationRequest request)
    {
        Assert.True(request.Accepted, request.Error);
        return await request.Job!.Completion.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static InstallRequest Request(string name, bool acceptEula = true) =>
        new(EngineFixture.GameName, name, Consents: acceptEula ? new[] { UserPrompt.Keys.Eula } : null);

    [Fact]
    public async Task Install_creates_a_registered_configured_server_that_can_start()
    {
        using var engine = await EngineFixture.StartEngineAsync();

        var job = await Run(engine.Provisioning.Install(Request("Fresh Install")));

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Contains("fake install 70%", job.RecentLog);
        var s = engine.Servers.Get(job.ServerId!)!;
        Assert.NotNull(s);
        Assert.Equal(("Fresh Install", EngineFixture.GameName), (s.Config.ServerName, s.Config.ServerGame));
        Assert.False(string.IsNullOrWhiteSpace(s.Config.ServerPort));
        await EngineFixture.WaitUntil(() => File.Exists(ServerPath.GetServersServerFiles(s.Id, "server.cfg")), "plugin's CreateServerCFG");
        try
        {
            Assert.Equal(JobStatus.Succeeded, (await Run(engine.Lifecycle.Start(s.Id))).Status);
            Assert.Equal(ServerState.Running, s.State);
        }
        finally
        {
            EngineFixture.KillQuietly(s.Process); // the engine notices the exit and marks it stopped
            await EngineFixture.WaitUntil(() => s.State == ServerState.Stopped, "server stopped before cleanup");
            await Run(engine.Provisioning.Delete(s.Id));
        }
    }

    [Fact]
    public async Task Declining_the_EULA_fails_cleanly_and_leaves_no_half_installed_server()
    {
        using var engine = await EngineFixture.StartEngineAsync();

        var job = await Run(engine.Provisioning.Install(Request("No Consent", acceptEula: false)));

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("EULA declined", job.Error);
        Assert.Null(engine.Servers.Get(job.ServerId!));
        Assert.False(Directory.Exists(ServerPath.GetServers(job.ServerId!)));                         // cleaned up
        Assert.NotEmpty(Directory.GetFiles(ServerPath.GetLogs("servers", job.ServerId!), "install-failure-*.txt")); // but explained
    }

    [Fact]
    public async Task Simultaneous_installs_get_different_server_ids()
    {
        using var engine = await EngineFixture.StartEngineAsync();

        var a = engine.Provisioning.Install(Request("Twin A"));
        var b = engine.Provisioning.Install(Request("Twin B"));
        var ja = await Run(a);
        var jb = await Run(b);

        Assert.Equal(JobStatus.Succeeded, ja.Status);
        Assert.Equal(JobStatus.Succeeded, jb.Status);
        Assert.NotEqual(ja.ServerId, jb.ServerId);
        await Run(engine.Provisioning.Delete(ja.ServerId!));
        await Run(engine.Provisioning.Delete(jb.ServerId!));
    }

    [Fact]
    public async Task Import_copies_an_existing_installation_and_validates_it_first()
    {
        string source = Path.Combine(Path.GetTempPath(), "wgsm-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(source, "worlds"));
        File.WriteAllText(Path.Combine(source, "game.exe"), "fake");
        File.WriteAllText(Path.Combine(source, "worlds", "w1.dat"), "world");
        using var engine = await EngineFixture.StartEngineAsync();
        try
        {
            var refused = engine.Provisioning.Import(EngineFixture.GameName, "Bad", Path.GetTempPath());
            Assert.False(refused.Accepted);
            Assert.Equal("game.exe not found", refused.Error);

            var job = await Run(engine.Provisioning.Import(EngineFixture.GameName, "Imported", source));

            Assert.Equal(JobStatus.Succeeded, job.Status);
            Assert.Equal("world", File.ReadAllText(ServerPath.GetServersServerFiles(job.ServerId!, @"worlds\w1.dat")));
            Assert.Equal("Imported", engine.Servers.Get(job.ServerId!)!.Config.ServerName);
            Assert.True(File.Exists(Path.Combine(source, "game.exe"))); // a copy — the original is left alone
            await Run(engine.Provisioning.Delete(job.ServerId!));
        }
        finally { Directory.Delete(source, recursive: true); }
    }

    [Fact]
    public async Task Delete_refuses_a_running_server_and_removes_a_stopped_one()
    {
        using var engine = await EngineFixture.StartEngineAsync();
        var install = await Run(engine.Provisioning.Install(Request("To Delete")));
        string id = install.ServerId!;
        var s = engine.Servers.Get(id)!;
        try
        {
            await Run(engine.Lifecycle.Start(id));
            Assert.Contains("Stop", engine.Provisioning.Delete(id).Error);
            await Run(engine.Lifecycle.Stop(id));
        }
        finally { EngineFixture.KillQuietly(s.Process); }

        var deleted = await Run(engine.Provisioning.Delete(id));

        Assert.Equal(JobStatus.Succeeded, deleted.Status);
        Assert.Null(engine.Servers.Get(id));
        Assert.False(Directory.Exists(ServerPath.GetServers(id)));
    }
}
