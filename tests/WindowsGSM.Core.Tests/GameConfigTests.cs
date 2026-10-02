using WindowsGSM.Engine.GameConfig;

namespace WindowsGSM.Core.Tests;

/// <summary>
/// Game config files as settings: real-world samples from each format parse into sensible entries, and saving
/// changes exactly the edited values — every other byte (comments, blank lines, order, quoting) is kept.
/// </summary>
public class GameConfigTests
{
    private static ConfigEntry Find(IEnumerable<ConfigEntry> entries, string key) => Assert.Single(entries, e => e.Key == key);

    /// <summary>Applies one change and asserts the only difference is that line.</summary>
    private static string Change(string text, ConfigFormat format, string key, string value, string expectedLine)
    {
        var parsed = LineConfig.Parse(text, format);
        var entry = Find(parsed.Entries, key);
        string output = parsed.Apply(new[] { new ConfigChange(entry.Id, value) });
        var before = text.Replace("\r\n", "\n").Split('\n');
        var after = output.Replace("\r\n", "\n").Split('\n');
        Assert.Equal(before.Length, after.Length);
        var diff = before.Zip(after).Where(p => p.First != p.Second).ToList();
        var line = Assert.Single(diff);
        Assert.Equal(expectedLine, line.Second);
        return output;
    }

    [Fact]
    public void Minecraft_server_properties()
    {
        const string text = "#Minecraft server properties\n#Mon Sep 29 12:00:00 UTC 2026\nallow-flight=false\ndifficulty=easy\nmax-players=20\nmotd=A Minecraft Server: welcome\nlevel-seed=\n";
        var entries = LineConfig.Parse(text, ConfigFormat.Properties).Entries;
        Assert.Equal("bool", Find(entries, "allow-flight").Type);
        Assert.Equal("number", Find(entries, "max-players").Type);
        Assert.Equal("A Minecraft Server: welcome", Find(entries, "motd").Value);
        Assert.Equal("", Find(entries, "level-seed").Value);
        string output = Change(text, ConfigFormat.Properties, "max-players", "32", "max-players=32");
        Assert.EndsWith("\n", output);
    }

    [Fact]
    public void Source_server_cfg_keeps_quotes_comments_and_adds_quotes_when_needed()
    {
        const string text = "// Server name\r\nhostname \"My CS Server\"\r\nsv_cheats 0 // no cheats\r\nsv_password \"\"\r\nmp_timelimit 30\r\nexec banned_user.cfg\r\n";
        var entries = LineConfig.Parse(text, ConfigFormat.Cfg).Entries;
        var host = Find(entries, "hostname");
        Assert.Equal("My CS Server", host.Value);
        Assert.Equal("Server name", host.Comment);
        Assert.Equal("no cheats", Find(entries, "sv_cheats").Comment);

        Change(text, ConfigFormat.Cfg, "hostname", "Friday Night Frags", "hostname \"Friday Night Frags\"");
        Change(text, ConfigFormat.Cfg, "sv_cheats", "1", "sv_cheats 1 // no cheats");
        Change(text, ConfigFormat.Cfg, "mp_timelimit", "45 minutes", "mp_timelimit \"45 minutes\""); // gained a space → quoted
        string crlf = Change(text, ConfigFormat.Cfg, "sv_password", "secret", "sv_password \"secret\"");
        Assert.Contains("\r\n", crlf); // line endings kept
    }

    [Fact]
    public void DayZ_style_cfg_with_semicolons_and_classes()
    {
        const string text = "hostname = \"EU #1\";\t// Server name\nmaxPlayers = 60;\nverifySignatures = 2;\nclass Missions\n{\n    class DayZ\n    {\n        template=\"dayzOffline.chernarusplus\";\n    };\n};\n";
        var entries = LineConfig.Parse(text, ConfigFormat.Cfg).Entries;
        Assert.Equal("EU #1", Find(entries, "hostname").Value);
        Assert.Equal("60", Find(entries, "maxPlayers").Value);
        Change(text, ConfigFormat.Cfg, "maxPlayers", "80", "maxPlayers = 80;");
        Change(text, ConfigFormat.Cfg, "hostname", "EU #2", "hostname = \"EU #2\";\t// Server name");
    }

    [Fact]
    public void Unreal_ini_with_sections_and_bool_case()
    {
        const string text = "[ServerSettings]\n; Allow third person\nAllowThirdPersonPlayer=True\nDifficultyOffset=0.200000\nServerPassword=\n\n[SessionSettings]\nSessionName=My Ark Server\n";
        var entries = LineConfig.Parse(text, ConfigFormat.Ini).Entries;
        var tp = Find(entries, "AllowThirdPersonPlayer");
        Assert.Equal("ServerSettings", tp.Section);
        Assert.Equal("bool", tp.Type);
        Assert.Equal("Allow third person", tp.Comment);
        Assert.Equal("SessionSettings", Find(entries, "SessionName").Section);
        Change(text, ConfigFormat.Ini, "AllowThirdPersonPlayer", "false", "AllowThirdPersonPlayer=False"); // file's own spelling kept
        Change(text, ConfigFormat.Ini, "SessionName", "Dino Island", "SessionName=Dino Island");
    }

    [Fact]
    public void Palworld_option_settings_become_individual_settings()
    {
        const string text = "[/Script/Pal.PalGameWorldSettings]\nOptionSettings=(Difficulty=None,DayTimeSpeedRate=1.000000,ServerName=\"Default Palworld Server\",bIsPvP=False,ServerPlayerMaxNum=32)\n";
        var entries = LineConfig.Parse(text, ConfigFormat.Ini).Entries;
        Assert.Equal(5, entries.Count);
        Assert.Equal("Default Palworld Server", Find(entries, "ServerName").Value);
        Assert.Equal("bool", Find(entries, "bIsPvP").Type);
        Change(text, ConfigFormat.Ini, "ServerPlayerMaxNum", "16",
            "OptionSettings=(Difficulty=None,DayTimeSpeedRate=1.000000,ServerName=\"Default Palworld Server\",bIsPvP=False,ServerPlayerMaxNum=16)");
        Change(text, ConfigFormat.Ini, "ServerName", "Pal Pals",
            "OptionSettings=(Difficulty=None,DayTimeSpeedRate=1.000000,ServerName=\"Pal Pals\",bIsPvP=False,ServerPlayerMaxNum=32)");
    }

    [Fact]
    public void Simple_yaml_nested_mappings()
    {
        const string text = "ServerConfig:\n  Srv_Port: 30000\n  Srv_Name: \"My Empyrion\"   # shown in the browser\n  Srv_Public: true\nGameConfig:\n  GameName: DediGame\n  Mode: Survival\n";
        var entries = LineConfig.Parse(text, ConfigFormat.Yaml).Entries;
        var name = Find(entries, "Srv_Name");
        Assert.Equal("ServerConfig", name.Section);
        Assert.Equal("My Empyrion", name.Value);
        Assert.Equal("shown in the browser", name.Comment);
        Assert.Equal("GameConfig", Find(entries, "Mode").Section);
        Change(text, ConfigFormat.Yaml, "Srv_Name", "Space!", "  Srv_Name: \"Space!\"   # shown in the browser");
        Change(text, ConfigFormat.Yaml, "Mode", "Creative", "  Mode: Creative");
    }

    [Fact]
    public void Seven_days_to_die_property_xml()
    {
        const string text = "<?xml version=\"1.0\"?>\n<ServerSettings>\n\t<!-- Server name -->\n\t<property name=\"ServerName\" value=\"My Game Host\"/>\n\t<property name=\"ServerPort\" value=\"26900\"/>\t\t<!-- Port you want the server to listen on. -->\n\t<property name=\"EACEnabled\" value=\"true\"/>\n</ServerSettings>\n";
        var cfg = XmlConfig.Parse(text);
        var name = Find(cfg.Entries, "ServerName");
        Assert.Equal("My Game Host", name.Value);
        Assert.Equal("Server name", name.Comment);
        Assert.Equal("Port you want the server to listen on.", Find(cfg.Entries, "ServerPort").Comment);
        Assert.Equal("bool", Find(cfg.Entries, "EACEnabled").Type);

        string output = cfg.Apply(new[] { new ConfigChange(Find(cfg.Entries, "ServerPort").Id, "26910") });
        Assert.Equal(text.Replace("value=\"26900\"", "value=\"26910\""), output);
    }

    [Fact]
    public void Leaf_element_xml_like_space_engineers()
    {
        const string text = "<?xml version=\"1.0\"?>\n<MyConfigDedicated xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\n  <SessionSettings>\n    <GameMode>Survival</GameMode>\n    <MaxPlayers>4</MaxPlayers>\n  </SessionSettings>\n  <ServerName>SE Server</ServerName>\n</MyConfigDedicated>\n";
        var cfg = XmlConfig.Parse(text);
        Assert.Equal("SessionSettings", Find(cfg.Entries, "MaxPlayers").Section);
        string output = cfg.Apply(new[] { new ConfigChange(Find(cfg.Entries, "MaxPlayers").Id, "12") });
        Assert.Equal(text.Replace("<MaxPlayers>4</MaxPlayers>", "<MaxPlayers>12</MaxPlayers>"), output);
    }

    [Fact]
    public void Json_leaf_values_keep_their_types()
    {
        const string text = "{\n    \"ServerName\": \"Vintage\",\n    \"Port\": 42420,\n    \"AllowPvP\": true,\n    \"WorldConfig\": { \"Seed\": \"abc\", \"MapSizeX\": 1024000 },\n    \"Roles\": [ \"admin\" ]\n}\n";
        var cfg = JsonConfig.Parse(text);
        Assert.Equal("number", Find(cfg.Entries, "Port").Type);
        Assert.Equal("bool", Find(cfg.Entries, "AllowPvP").Type);
        Assert.Equal("WorldConfig", Find(cfg.Entries, "Seed").Section);
        Assert.True(Find(cfg.Entries, "Roles").ReadOnly);

        string output = cfg.Apply(new[] { new ConfigChange("Port", "42421"), new ConfigChange("AllowPvP", "false"), new ConfigChange("WorldConfig.Seed", "xyz") });
        Assert.Contains("\"Port\": 42421", output);
        Assert.Contains("\"AllowPvP\": false", output);
        Assert.Contains("\"Seed\": \"xyz\"", output);
        Assert.Contains("    \"ServerName\"", output); // 4-space indentation kept
        Assert.Throws<ConfigException>(() => JsonConfig.Parse(text).Apply(new[] { new ConfigChange("Port", "lots") }));
    }

    [Fact]
    public void Multi_line_values_are_refused()
    {
        var parsed = LineConfig.Parse("a=1\n", ConfigFormat.Properties);
        Assert.Throws<ConfigException>(() => parsed.Apply(new[] { new ConfigChange(parsed.Entries[0].Id, "1\nb=2") }));
    }
}
