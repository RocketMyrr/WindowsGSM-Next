using WindowsGSM.Desktop;

namespace WindowsGSM.Agent.Tests;

/// <summary>The desktop app finding its agent: data folder, address from agent.json.</summary>
public class DesktopTests
{
    [Fact]
    public void The_data_folder_comes_from_the_command_line_or_the_apps_folder_without_a_trailing_slash()
    {
        string here = Path.Combine(Path.GetTempPath(), "wgsm-desktop-app") + Path.DirectorySeparatorChar;
        Assert.Equal(Path.Combine(Path.GetTempPath(), "wgsm-desktop-app"), AgentLocator.DataRoot(Array.Empty<string>(), here));
        Assert.Equal(@"D:\wgsm-data", AgentLocator.DataRoot(new[] { "--minimized", "--data", @"D:\wgsm-data\" }, here));
    }

    [Fact]
    public void The_address_follows_the_agents_settings()
    {
        string root = Path.Combine(Path.GetTempPath(), "wgsm-desktop-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal("http://localhost:8971/", AgentLocator.UrlFor(root).ToString());
            Directory.CreateDirectory(Path.Combine(root, "configs", "next"));
            File.WriteAllText(Path.Combine(root, "configs", "next", "agent.json"), """{ "Port": 9443, "UseHttps": true }""");
            Assert.Equal("https://localhost:9443/", AgentLocator.UrlFor(root).ToString());
            File.WriteAllText(Path.Combine(root, "configs", "next", "agent.json"), "not json");
            Assert.Equal("http://localhost:8971/", AgentLocator.UrlFor(root).ToString());
        }
        finally { WindowsGSM.Core.Tests.TestData.DeleteDirectory(root); }
    }

    [Fact]
    public void Only_this_machine_counts_as_local()
    {
        Assert.True(AgentLocator.IsLocal(new Uri("https://localhost:8971/")));
        Assert.True(AgentLocator.IsLocal(new Uri("http://127.0.0.1:8971/")));
        Assert.False(AgentLocator.IsLocal(new Uri("https://games.example.com/")));
    }
}
