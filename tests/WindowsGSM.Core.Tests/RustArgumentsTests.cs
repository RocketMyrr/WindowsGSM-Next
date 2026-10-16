using WindowsGSM.Functions;
using WindowsGSM.GameServer;

namespace WindowsGSM.Core.Tests;

/// <summary>Rust's start line holds only Rust's switches, the form, RCON and what you add; old defaults move out once.</summary>
[Collection("Lifecycle")]
public class RustArgumentsTests
{
    private static string RustServer(string id, params string[] settings)
    {
        _ = TestData.DataRoot;
        string configs = ServerPath.GetServersConfigs(id);
        Directory.CreateDirectory(configs);
        string files = ServerPath.GetServersServerFiles(id);
        if (Directory.Exists(files)) { Directory.Delete(files, true); }
        Directory.CreateDirectory(files);
        File.WriteAllLines(Path.Combine(configs, "WindowsGSM.cfg"), new[]
        {
            "servergame=\"Rust Dedicated Server\"", "servername=\"Empires Test\"", "serverip=\"192.168.4.71\"", "serverport=\"28015\"",
            "serverqueryport=\"28016\"", "servermaxplayer=\"50\"", "rconip=\"192.168.4.71\"", "rconport=\"0\"", "rconpassword=\"unused\"",
        }.Concat(settings));
        return id;
    }

    [Fact]
    public void The_start_line_is_only_the_form_rcon_and_your_extras()
    {
        string id = RustServer("141", "serverparam=\"-noconsole +server.seed 12345\"");
        string args = RUST.Arguments(new ServerConfig(id));
        Assert.Equal("-nographics -batchmode -silent-crashes +server.hostname \"Empires Test\" +server.ip 192.168.4.71 +server.port 28015 " +
                     "+server.queryport 28016 +server.maxplayers 50 -noconsole +server.seed 12345", args); // RCON port 0: no RCON

        ServerConfig.SetSetting(id, ServerConfig.SettingName.RconPort, "28017");
        Assert.Contains("+rcon.ip 192.168.4.71 +rcon.port 28017 +rcon.password \"unused\" +rcon.web 1 -noconsole", RUST.Arguments(new ServerConfig(id)));
    }

    [Fact]
    public void An_old_servercfg_moves_out_once_and_the_server_runs_the_same()
    {
        string id = RustServer("142", "serverparam=\"-noconsole\"");
        string old = ServerPath.GetServersServerFiles(id, "server.cfg");
        File.WriteAllLines(old, new[]
        {
            "+rcon.ip 0.0.0.0", "+rcon.port \"28015\"", "+rcon.password \"Secret123\"", "+rcon.web 1", "+server.tickrate 10",
            "+server.description \"Rust Dedicated Server - Managed by WindowsGSM\"", "+server.identity \"server1\"", "+server.seed 689777",
            "+server.worldsize 3000", "", "-logfile \"server.log\"",
        });
        // Rust's own config already sets the tick rate: that one stays.
        string rustCfg = ServerPath.GetServersServerFiles(id, Path.Combine("server", "server1", "cfg", "server.cfg"));
        Directory.CreateDirectory(Path.GetDirectoryName(rustCfg)!);
        File.WriteAllText(rustCfg, "server.tickrate 30\n");

        var cfg = new ServerConfig(id);
        Assert.NotNull(RUST.MoveOldDefaults(cfg));

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(old + ".old"));
        var rust = File.ReadAllLines(rustCfg);
        Assert.Contains("server.tickrate 30", rust);
        Assert.DoesNotContain("server.tickrate 10", rust);
        Assert.Contains("server.seed 689777", rust);
        Assert.Contains("server.worldsize 3000", rust);
        Assert.Contains("server.description \"Rust Dedicated Server - Managed by WindowsGSM\"", rust);
        Assert.DoesNotContain(rust, l => l.StartsWith("rcon.") || l.StartsWith("server.identity"));

        var saved = new ServerConfig(id);
        Assert.Equal(("28015", "Secret123", "0.0.0.0"), (saved.RconPort, saved.RconPassword, saved.RconIp));
        Assert.Equal("-logfile server.log +server.identity server1 -noconsole".Split(' ').OrderBy(x => x), saved.ServerParam.Replace("\"", "").Split(' ').OrderBy(x => x));

        string args = RUST.Arguments(saved);
        Assert.Contains("+rcon.ip 0.0.0.0 +rcon.port 28015 +rcon.password \"Secret123\" +rcon.web 1", args);
        Assert.DoesNotContain("tickrate", args);
        Assert.DoesNotContain("seed", args);

        Assert.Null(RUST.MoveOldDefaults(saved)); // done once
    }
}
