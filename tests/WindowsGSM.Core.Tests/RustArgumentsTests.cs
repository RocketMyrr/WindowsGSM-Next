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
    public void The_map_box_is_used_and_quotes_cannot_break_the_start_line()
    {
        string id = RustServer("141", "servermap=\"Barren\"");
        var cfg = new ServerConfig(id) { ServerName = "Bob \"Best\" +rcon.password \"x\"" };
        string args = RUST.Arguments(cfg);
        Assert.Contains("+server.level \"Barren\"", args);
        Assert.Contains("+server.hostname \"Bob 'Best' +rcon.password 'x'\"", args); // stays one value
        // A seed with the World size box; a link ignores both.
        ServerConfig.SetSetting(id, RUST.WorldSizeKey, "3500");
        cfg = new ServerConfig(id) { ServerMap = "689777" };
        Assert.Contains("+server.seed 689777 +server.maxplayers 50", RUST.Arguments(cfg).Replace(" +server.worldsize 3500", ""));
        Assert.Contains("+server.worldsize 3500", RUST.Arguments(cfg));
        Assert.DoesNotContain("+server.level ", RUST.Arguments(cfg));
        cfg.ServerMap = "https://example.com/maps/custom.map";
        Assert.Contains("+server.levelurl \"https://example.com/maps/custom.map\"", RUST.Arguments(cfg));
        Assert.DoesNotContain("worldsize", RUST.Arguments(cfg));
        ServerConfig.SetSetting(id, RUST.WorldSizeKey, "99999"); // out of Rust's range: left out
        Assert.DoesNotContain("worldsize", RUST.Arguments(new ServerConfig(id) { ServerMap = "1" }));
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

/// <summary>The settings form offers Rust's World size box, and keeps the usual boxes.</summary>
[Collection("Lifecycle")]
public class RustSettingsTests
{
    [Fact]
    public async Task Rust_has_a_world_size_box_beside_the_usual_ones()
    {
        using var engine = await EngineFixture.StartEngineAsync();
        var settings = engine.Games.Settings(RUST.FullName, out bool replaces);
        Assert.False(replaces); // name, map and token boxes stay
        Assert.Contains(settings, g => g.Key == RUST.WorldSizeKey && g.Label.StartsWith("World size"));
    }
}

/// <summary>Rust's RCON "playerlist" gives the names its query leaves out.</summary>
public class RustPlayerListTests
{
    [Fact]
    public void Names_come_from_the_playerlist_reply_even_with_other_console_lines_mixed_in()
    {
        string reply = "[Event] something happened\n[\n  {\n    \"SteamID\": \"76561198000000001\",\n    \"DisplayName\": \"Eric \\\"Rocket\\\"\",\n    \"Ping\": 40,\n    \"ConnectedSeconds\": 125\n  },\n  {\n    \"SteamID\": \"76561198000000002\",\n    \"DisplayName\": \"Steve\",\n    \"ConnectedSeconds\": 3\n  }\n]\nSaving complete";
        var players = WindowsGSM.Engine.Services.MonitorService.ParseRustPlayerList(reply);
        Assert.Equal(new[] { "Eric \"Rocket\"", "Steve" }, players.Select(p => p.Name));
        Assert.Equal(TimeSpan.FromSeconds(125), players[0].TimeConnected);
        Assert.Empty(WindowsGSM.Engine.Services.MonitorService.ParseRustPlayerList("Unknown command"));
    }
}
