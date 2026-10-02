using WindowsGSM.Hosting;
using WindowsGSM.Installer;

namespace WindowsGSM.Core.Tests;

/// <summary>DepotDownloader is the standard for installs and updates; SteamCMD only by explicit exception.</summary>
public class SteamContentPolicyTests
{
    private static string WriteServerConfig(string id, params string[] lines)
    {
        _ = TestData.DataRoot;
        string dir = Path.Combine(WgsmEnvironment.DataRoot, "servers", id, "configs");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "WindowsGSM.cfg"), lines);
        return id;
    }

    [Fact]
    public void Server_with_no_preference_uses_DepotDownloader()
    {
        string id = WriteServerConfig("201", "servergame=\"Rust Dedicated Server\"");
        Assert.Equal(SteamContentTool.DepotDownloader, SteamContentPolicy.Choose(id));
    }

    [Fact]
    public void Legacy_depotdownloader_off_flag_does_not_mean_SteamCMD()
    {
        // Older servers were saved with depotdownloader="0" by default — that was never a user choice.
        string id = WriteServerConfig("202", "servergame=\"Rust Dedicated Server\"", "depotdownloader=\"0\"");
        Assert.Equal(SteamContentTool.DepotDownloader, SteamContentPolicy.Choose(id));
    }

    [Fact]
    public void Explicit_override_forces_SteamCMD()
    {
        string id = WriteServerConfig("203", "servergame=\"Rust Dedicated Server\"", $"{SteamContentPolicy.SteamCmdOverrideSetting}=\"1\"");
        Assert.Equal(SteamContentTool.SteamCMD, SteamContentPolicy.Choose(id));
    }

    [Fact]
    public void Workshop_downloads_use_SteamCMD()
    {
        string id = WriteServerConfig("204", "servergame=\"Rust Dedicated Server\"");
        Assert.Equal(SteamContentTool.SteamCMD, SteamContentPolicy.Choose(id, workshopItem: "123456"));
    }
}
