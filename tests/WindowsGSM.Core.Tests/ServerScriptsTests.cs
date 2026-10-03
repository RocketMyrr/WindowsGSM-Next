using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Engine.Services;
using WindowsGSM.Functions;

namespace WindowsGSM.Core.Tests;

/// <summary>A server's own scripts: before every start and after every stop, .bat and .ps1 only, run for real.</summary>
[Collection("Lifecycle")]
public class ServerScriptsTests
{
    private static async Task<JobSnapshot> Run(OperationRequest request)
    {
        Assert.True(request.Accepted, request.Error);
        return await request.Job!.Completion.WaitAsync(TimeSpan.FromSeconds(90));
    }

    /// <summary>Writes a script into a folder of its own (with a space in the name, as real paths often have).</summary>
    private static string Script(string name, string content)
    {
        string dir = Path.Combine(TestData.DataRoot, "my scripts");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Only_bat_and_ps1_files_that_exist_can_be_chosen()
    {
        string bat = Script("ok.bat", "@echo off");
        string ps1 = Script("ok.ps1", "Write-Output hi");
        string exe = Script("tool.exe", "not really");
        string cmd = Script("tool.cmd", "@echo off");

        Assert.Null(ServerScripts.Validate(""));     // none
        Assert.Null(ServerScripts.Validate(bat));
        Assert.Null(ServerScripts.Validate(ps1.ToUpperInvariant()));
        Assert.Null(ServerScripts.Validate($"\"{bat}\""));  // Explorer's "Copy as path"
        Assert.Equal(bat, ServerScripts.Clean($" \"{bat}\" "));
        Assert.Contains("Only .bat and .ps1", ServerScripts.Validate(exe));
        Assert.Contains("Only .bat and .ps1", ServerScripts.Validate(cmd));
        Assert.Contains("full path", ServerScripts.Validate("rotate.bat"));
        Assert.Contains("no file", ServerScripts.Validate(Path.Combine(Path.GetDirectoryName(bat)!, "missing.bat")));
    }

    [Fact]
    public async Task The_scripts_run_before_every_start_and_after_every_stop_in_the_servers_folder()
    {
        string before = Script("rotate logs.bat", "@echo off\r\necho rotated for %WGSM_SERVER_ID% at %WGSM_SCRIPT_WHEN%\r\necho %WGSM_SERVER_ID% %WGSM_SCRIPT_WHEN%> before-start.txt\r\npause\r\n");
        string after = Script("clean up.ps1", "Write-Output \"cleaned $env:WGSM_SERVER_NAME\"\r\nSet-Content -Path 'after-stop.txt' -Value \"$env:WGSM_SERVER_ID $env:WGSM_SCRIPT_WHEN\"\r\n");
        EngineFixture.CreateServer("216", extraSettings: new[] { $"batchfile=\"{before}\"", $"afterstopscript=\"{after}\"", "savewait=\"0\"" });
        using var engine = await EngineFixture.StartEngineAsync();
        var s = engine.Servers.Get("216")!;
        string files = ServerLocation.RealPath("216");
        try
        {
            await Run(engine.Lifecycle.Start("216"));
            Assert.Equal(ServerState.Running, s.State);
            // Ran in the server's files folder, with its details — and "pause" didn't hold it up.
            Assert.Equal("216 start", File.ReadAllText(Path.Combine(files, "before-start.txt")).Trim());
            var log = engine.Log.Tail("216", 50);
            Assert.Contains(log, l => l.Contains("[Script] rotated for 216 at start"));
            Assert.Contains(log, l => l.Contains("Script (before start): rotate logs.bat finished"));
            Assert.False(File.Exists(Path.Combine(files, "after-stop.txt")));

            await Run(engine.Lifecycle.Stop("216"));
            Assert.Equal("216 stop", File.ReadAllText(Path.Combine(files, "after-stop.txt")).Trim());
            Assert.Contains(engine.Log.Tail("216", 50), l => l.Contains("[Script] cleaned"));

            // Force stop runs neither the save nor the after-stop script.
            File.Delete(Path.Combine(files, "after-stop.txt"));
            await Run(engine.Lifecycle.Start("216"));
            await Run(engine.Lifecycle.Kill("216"));
            Assert.False(File.Exists(Path.Combine(files, "after-stop.txt")));
        }
        finally { EngineFixture.KillQuietly(s.Process); }
    }

    [Fact]
    public async Task A_failing_script_is_logged_and_only_stops_the_start_when_asked_to()
    {
        string failing = Script("fails.bat", "@echo off\r\necho something went wrong\r\nexit /b 3\r\n");
        EngineFixture.CreateServer("217", extraSettings: new[] { $"batchfile=\"{failing}\"" });
        using var engine = await EngineFixture.StartEngineAsync();
        var s = engine.Servers.Get("217")!;
        try
        {
            // By default the server starts anyway.
            await Run(engine.Lifecycle.Start("217"));
            Assert.Equal(ServerState.Running, s.State);
            Assert.Contains(engine.Log.Tail("217", 50), l => l.Contains("finished with exit code 3"));
            await Run(engine.Lifecycle.Kill("217"));

            // With "don't start if it fails", it doesn't.
            ServerConfig.SetSetting("217", ServerScripts.BlocksStartKey, "1");
            var job = await Run(engine.Lifecycle.Start("217"));
            Assert.Equal(ServerState.Stopped, s.State);
            Assert.Contains("before-start script", job.Error);
        }
        finally { EngineFixture.KillQuietly(s.Process); }
    }

    [Fact]
    public async Task A_script_that_runs_too_long_is_stopped()
    {
        string slow = Script("slow.bat", "@echo off\r\nping -n 60 127.0.0.1 > nul\r\n");
        EngineFixture.CreateServer("218", extraSettings: new[] { $"batchfile=\"{slow}\"", "scripttimeout=\"5\"" });
        using var engine = await EngineFixture.StartEngineAsync();
        var s = engine.Servers.Get("218")!;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool ok = await ServerScripts.RunAsync(s, slow, "before start", engine.Log);
        Assert.False(ok);
        Assert.InRange(watch.Elapsed.TotalSeconds, 4, 20);
        Assert.Contains(engine.Log.Tail("218", 20), l => l.Contains("was still running after 5 s"));
    }
}
