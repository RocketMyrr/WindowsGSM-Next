using WindowsGSM.Functions;
using WindowsGSM.GameServer;

namespace WindowsGSM.Core.Tests;

/// <summary>Vanilla Minecraft's installed version, read from its log — including the two-part "26.x" versions.</summary>
public class MinecraftBuildTests
{
    [Theory]
    [InlineData("[12:00:01] [Server thread/INFO]: Starting minecraft server version 1.21.4", "1.21.4")]
    [InlineData("[12:00:01] [Server thread/INFO]: Starting minecraft server version 26.3", "26.3")]
    [InlineData("[12:00:01] [Server thread/INFO]: Starting minecraft server version 1.21", "1.21")]
    public void The_local_build_comes_from_the_log(string line, string version)
    {
        EngineFixture.CreateServer("249");
        string logs = ServerPath.GetServersServerFiles("249", "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllLines(Path.Combine(logs, "latest.log"), new[] { "[12:00:00] [main/INFO]: Loading", line, "[12:00:02] [Server thread/INFO]: Done" });

        Assert.Equal(version, new MC(new ServerConfig("249")).GetLocalBuild());
        File.Delete(Path.Combine(logs, "latest.log")); // and the file isn't left open
    }
}
