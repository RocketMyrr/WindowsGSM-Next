using WindowsGSM.Functions;
using WindowsGSM.Hosting;

namespace WindowsGSM.Core.Tests;

/// <summary>
/// Characterization tests for the on-disk formats Next must read exactly like the legacy app does
/// (Next adopts existing data folders in place). Each test uses its own server id so tests can run in
/// parallel against the shared temp data root.
/// </summary>
public class ConfigStoreTests
{
    private static string ConfigPath(string id) =>
        Path.Combine(WgsmEnvironment.DataRoot, "servers", id, "configs", "WindowsGSM.cfg");

    private static void WriteRaw(string id, params string[] lines)
    {
        _ = TestData.DataRoot;
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath(id))!);
        File.WriteAllLines(ConfigPath(id), lines);
    }

    [Fact]
    public void Legacy_config_file_reads_every_known_field()
    {
        WriteRaw("501",
            "servergame=\"Rust Dedicated Server\"",
            "servername=\"My Rust\"",
            "serverip=\"10.0.0.5\"",
            "serverport=\"28015\"",
            "serverqueryport=\"28016\"",
            "servermaxplayer=\"100\"",
            "autorestart=\"1\"",
            "autostart=\"0\"",
            "embedconsole=\"1\"",
            "crontabformat=\"0 6 * * *\"",
            "cpupriority=\"2\"",
            "memoryguard=\"1\"",
            "memoryguardthresholdmb=\"8192\"",
            "rconport=\"28017\"",
            "steambranch=\"staging\"",
            "",
            "// a comment line",
            "oxide_umod=\"yes\"");

        var cfg = new ServerConfig("501");

        Assert.Equal("Rust Dedicated Server", cfg.ServerGame);
        Assert.Equal("My Rust", cfg.ServerName);
        Assert.Equal("10.0.0.5", cfg.ServerIP);
        Assert.Equal("28015", cfg.ServerPort);
        Assert.Equal("28016", cfg.ServerQueryPort);
        Assert.Equal("100", cfg.ServerMaxPlayer);
        Assert.True(cfg.AutoRestart);
        Assert.False(cfg.AutoStart);
        Assert.True(cfg.EmbedConsole);
        Assert.Equal("0 6 * * *", cfg.CrontabFormat);
        Assert.True(cfg.MemoryGuard);
        Assert.Equal(8192, cfg.MemoryGuardThresholdMb);
        Assert.Equal("28017", cfg.RconPort);
        Assert.Equal("staging", cfg.SteamBranch);
        Assert.Equal("yes", cfg.CustomSettings["oxide_umod"]); // unknown keys are kept, not dropped
    }

    [Fact]
    public void Hand_edited_unquoted_values_no_longer_break_the_whole_config()
    {
        // Legacy threw on `autostart=1` / `servermap=` and the entire file became unreadable.
        WriteRaw("502",
            "servername=\"Still Readable\"",
            "autostart=1",
            "servermap=",
            "serverport=\"27015\"");

        var cfg = new ServerConfig("502");

        Assert.Equal("Still Readable", cfg.ServerName);
        Assert.True(cfg.AutoStart);
        Assert.Equal(string.Empty, cfg.ServerMap);
        Assert.Equal("27015", cfg.ServerPort);
    }

    [Fact]
    public void SetSetting_updates_in_place_and_appends_new_keys_without_touching_others()
    {
        WriteRaw("503", "servername=\"Before\"", "serverport=\"27015\"", "custom_key=\"x\"");

        ServerConfig.SetSetting("503", ServerConfig.SettingName.ServerName, "After");
        ServerConfig.SetSetting("503", ServerConfig.SettingName.AutoStart, "1");

        var lines = File.ReadAllLines(ConfigPath("503"));
        Assert.Equal("servername=\"After\"", lines[0]);           // position preserved
        Assert.Equal("serverport=\"27015\"", lines[1]);
        Assert.Equal("custom_key=\"x\"", lines[2]);
        Assert.Equal("autostart=\"1\"", lines[3]);                // appended
        Assert.False(File.Exists(ConfigPath("503") + ".tmp"));   // atomic write leaves nothing behind

        var cfg = new ServerConfig("503");
        Assert.Equal("After", cfg.ServerName);
        Assert.True(cfg.AutoStart);
    }

    [Fact]
    public async Task Concurrent_SetSetting_calls_lose_no_updates()
    {
        WriteRaw("504", "servername=\"x\"");

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            Task.Run(() => ServerConfig.SetSetting("504", "key" + i, i.ToString()))));

        var cfg = new ServerConfig("504");
        for (int i = 0; i < 40; i++) { Assert.Equal(i.ToString(), cfg.CustomSettings["key" + i]); }
    }

    [Fact]
    public void SetSetting_on_a_missing_config_does_nothing()
    {
        _ = TestData.DataRoot;
        ServerConfig.SetSetting("505", ServerConfig.SettingName.ServerName, "x");
        Assert.False(File.Exists(ConfigPath("505")));
    }

    [Fact]
    public void Build_cache_round_trips_and_ignores_blank_builds()
    {
        _ = TestData.DataRoot;
        Directory.CreateDirectory(ServerPath.GetServersServerFiles("506"));

        Assert.Equal(string.Empty, BuildCache.Read("506"));
        BuildCache.Write("506", " 18342201 \n");
        Assert.Equal("18342201", BuildCache.Read("506"));
        BuildCache.Write("506", "   ");                       // blank never overwrites a real build
        Assert.Equal("18342201", BuildCache.Read("506"));
    }

    [Fact]
    public void Installed_addon_store_is_case_insensitive_and_persists()
    {
        _ = TestData.DataRoot;
        InstalledAddonStore.Mark("507", "oxide");
        InstalledAddonStore.Mark("507", "OXIDE"); // same key
        Assert.True(InstalledAddonStore.Has("507", "Oxide"));
        Assert.Single(InstalledAddonStore.Load("507"));

        InstalledAddonStore.Unmark("507", "oxide");
        Assert.False(InstalledAddonStore.Has("507", "oxide"));
    }

    [Fact]
    public void Custom_addons_dedupe_by_url_and_subfolder()
    {
        _ = TestData.DataRoot;
        var a = CustomAddonStore.Upsert("508", "Oxide staging", "https://example.com/oxide.zip", "");
        var b = CustomAddonStore.Upsert("508", "Oxide staging (renamed)", "HTTPS://EXAMPLE.COM/oxide.zip", null!);
        var c = CustomAddonStore.Upsert("508", "Other", "https://example.com/oxide.zip", "RustDedicated_Data");

        Assert.Equal(a.Id, b.Id);                                   // same url + subfolder → same entry
        Assert.NotEqual(a.Id, c.Id);                                // different subfolder → new entry
        Assert.Equal("Oxide staging (renamed)", CustomAddonStore.Get("508", a.Id)!.Name);
        Assert.Equal(2, CustomAddonStore.Load("508").Count);

        Assert.True(CustomAddonStore.Remove("508", a.Id));
        Assert.False(CustomAddonStore.Remove("508", a.Id));
        Assert.Single(CustomAddonStore.Load("508"));
    }
}
