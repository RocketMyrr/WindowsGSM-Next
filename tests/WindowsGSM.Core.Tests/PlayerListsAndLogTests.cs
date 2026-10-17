using WindowsGSM.Engine.Services;
using WindowsGSM.Functions;
using WindowsGSM.Hosting;

namespace WindowsGSM.Core.Tests;

/// <summary>Minecraft's player lists, the console from a log file after the agent restarts, and the game config's address.</summary>
[Collection("Lifecycle")]
public class PlayerListsAndLogTests
{
    private static string Minecraft(string id, string game = "Minecraft: Java Edition Server", params string[] properties)
    {
        _ = TestData.DataRoot;
        string configs = ServerPath.GetServersConfigs(id);
        Directory.CreateDirectory(configs);
        string files = ServerPath.GetServersServerFiles(id);
        if (Directory.Exists(files)) { Directory.Delete(files, true); }
        Directory.CreateDirectory(files);
        File.WriteAllLines(Path.Combine(configs, "WindowsGSM.cfg"), new[] { $"servergame=\"{game}\"", "servername=\"Blocks\"" });
        File.WriteAllLines(Path.Combine(files, "server.properties"), properties);
        return id;
    }

    [Fact]
    public async Task A_stopped_Java_servers_whitelist_and_ops_are_edited_in_their_files()
    {
        string id = Minecraft("143", properties: new[] { "white-list=true", "online-mode=true" });
        var fetch = AccessLists.FetchProfile;
        AccessLists.FetchProfile = name => name.Equals("Notch", StringComparison.OrdinalIgnoreCase)
            ? Task.FromResult("""{"id":"069a79f444e94726a5befca90e38aaf5","name":"Notch"}""")
            : throw new HttpRequestException("404");
        try
        {
            using var engine = await EngineFixture.StartEngineAsync();
            Assert.Null(await engine.Access.ChangeAsync(id, "whitelist", "notch", add: true, by: "test"));
            Assert.Null(await engine.Access.ChangeAsync(id, "ops", "Notch", add: true, by: "test"));
            Assert.Contains("doesn't know", await engine.Access.ChangeAsync(id, "whitelist", "NoSuchPlayer", add: true, by: "test"));
            Assert.Contains("letters", await engine.Access.ChangeAsync(id, "whitelist", "bad name!", add: true, by: "test"));

            var lists = engine.Access.Get(id)!;
            var whitelist = lists.Single(l => l.Key == "whitelist");
            Assert.True(whitelist.Enforced);
            Assert.Equal(new[] { "Notch" }, whitelist.Names); // Mojang's spelling
            string written = File.ReadAllText(ServerPath.GetServersServerFiles(id, "whitelist.json"));
            Assert.Contains("069a79f4-44e9-4726-a5be-fca90e38aaf5", written);
            Assert.Contains("\"level\": 4", File.ReadAllText(ServerPath.GetServersServerFiles(id, "ops.json")));

            Assert.Null(await engine.Access.ChangeAsync(id, "whitelist", "Notch", add: false, by: "test"));
            Assert.Empty(engine.Access.Get(id)!.Single(l => l.Key == "whitelist").Names);
        }
        finally { AccessLists.FetchProfile = fetch; }
    }

    [Fact]
    public async Task Offline_mode_and_Bedrock_need_no_lookup()
    {
        Assert.Equal("b50ad385-829d-3141-a216-7e7d7539ba7f", AccessLists.OfflineUuid("Notch"));

        string bedrock = Minecraft("144", "Minecraft: Bedrock Edition Server", "allow-list=false");
        using var engine = await EngineFixture.StartEngineAsync();
        Assert.Null(await engine.Access.ChangeAsync(bedrock, "allowlist", "Steve Smith", add: true, by: null));
        var allow = Assert.Single(engine.Access.Get(bedrock)!);
        Assert.Equal((false, "Steve Smith"), (allow.Enforced!.Value, Assert.Single(allow.Names)));
        Assert.Null(engine.Access.Get("141")); // not Minecraft: no lists
    }

    [Fact]
    public void The_log_file_comes_from_the_start_parameters()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wgsm-logarg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "server.log"), "");
            Assert.Equal(Path.Combine(dir, "server.log"), ConsoleLogTail.LogFileArgument("+server.identity \"x\" -logfile \"server.log\" -noconsole", dir));
            Assert.Equal(Path.Combine(dir, "server.log"), ConsoleLogTail.LogFileArgument("-logFile server.log", dir));
            Assert.Null(ConsoleLogTail.LogFileArgument("-logfile -", dir));        // to the console itself
            Assert.Null(ConsoleLogTail.LogFileArgument("-logfile missing.log", dir)); // not there
            Assert.Null(ConsoleLogTail.LogFileArgument("-batchmode", dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_reattached_server_shows_its_log_file_in_the_console()
    {
        EngineFixture.CreateServer("145");
        using var engine = await EngineFixture.StartEngineAsync();
        var s = engine.Servers.Get("145")!;
        string log = ServerPath.GetServersServerFiles("145", "server.log");
        File.WriteAllLines(log, new[] { "old line 1", "old line 2" });
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        s.SetProcess(me);
        try
        {
            ConsoleLogTail.Follow(s, me, log);
            await EngineFixture.WaitUntil(() => s.Console.Get().Contains("old line 2"), "the log's recent lines");
            File.AppendAllText(log, "new line" + Environment.NewLine);
            await EngineFixture.WaitUntil(() => s.Console.Get().Contains("new line"), "a line written later");
        }
        finally { s.ClearProcessIf(me); }
    }

    [Fact]
    public void An_address_the_game_config_binds_but_this_PC_lacks_is_reported()
    {
        string id = Minecraft("146", properties: new[] { "server-ip=203.0.113.250", "server-port=25565" });
        Assert.Equal("203.0.113.250", ReadinessService.BoundInGameConfig(id));
        Assert.False(LocalAddresses.IsUsable("203.0.113.250"));
        File.WriteAllLines(ServerPath.GetServersServerFiles(id, "server.properties"), new[] { "server-ip=", "server-port=25565" });
        Assert.Equal("", ReadinessService.BoundInGameConfig(id)); // every address: fine
        File.Delete(ServerPath.GetServersServerFiles(id, "server.properties"));
        Assert.Null(ReadinessService.BoundInGameConfig(id));
    }
}
