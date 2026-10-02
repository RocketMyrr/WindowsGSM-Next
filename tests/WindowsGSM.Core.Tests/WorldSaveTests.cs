using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Services;
using WindowsGSM.Functions;

namespace WindowsGSM.Core.Tests;

/// <summary>Saving the world before a stop: the known commands, a server's own, "-" for none — and Kill skips it.</summary>
[Collection("Lifecycle")]
public class WorldSaveTests
{
    private static async Task<JobSnapshot> Run(OperationRequest request)
    {
        Assert.True(request.Accepted, request.Error);
        return await request.Job!.Completion.WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task A_stop_sends_the_save_command_first_and_a_kill_does_not()
    {
        EngineFixture.CreateServer("214", extraSettings: new[] { "savecommand=\"save-everything\"", "savewait=\"0\"" });
        using var engine = await EngineFixture.StartEngineAsync();
        var s = engine.Servers.Get("214")!;
        try
        {
            await Run(engine.Lifecycle.Start("214"));
            await Run(engine.Lifecycle.Stop("214"));
            var log = engine.Log.Tail("214", 50);
            Assert.Contains(log, l => l.Contains("Saving the world before stopping (save-everything)"));
            // The test game has no console to type into and no RCON: the stop carries on regardless.
            Assert.Contains(log, l => l.Contains("Couldn't send the save command"));
            Assert.Equal(WindowsGSM.Engine.Servers.ServerState.Stopped, s.State);

            await Run(engine.Lifecycle.Start("214"));
            int before = engine.Log.Tail("214", 200).Count(l => l.Contains("Saving the world"));
            await Run(engine.Lifecycle.Kill("214"));
            Assert.Equal(before, engine.Log.Tail("214", 200).Count(l => l.Contains("Saving the world")));
        }
        finally { EngineFixture.KillQuietly(s.Process); }
    }

    [Fact]
    public void Known_games_have_their_save_command_and_a_server_can_override_or_turn_it_off()
    {
        EngineFixture.CreateServer("215");
        using var engine = EngineFixture.StartEngineAsync().GetAwaiter().GetResult();
        var s = engine.Servers.Get("215")!;
        Assert.Equal(("server.save", 10), WorldSave.For(s, "258550"));       // Rust
        Assert.Equal(("", 0), WorldSave.For(s, "999999"));                   // unknown
        ServerConfig.SetSetting("215", WorldSave.CommandKey, "-");
        s.ReloadConfig();
        Assert.Equal(("", 0), WorldSave.For(s, "258550"));                   // turned off
        ServerConfig.SetSetting("215", WorldSave.CommandKey, "save now");
        ServerConfig.SetSetting("215", WorldSave.WaitKey, "3");
        s.ReloadConfig();
        Assert.Equal(("save now", 3), WorldSave.For(s, "258550"));
        Assert.Equal(30, WorldSave.StopTimeout(s));
    }
}
