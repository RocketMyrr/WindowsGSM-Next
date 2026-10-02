using WindowsGSM.Functions;
using WindowsGSM.Hosting;

namespace WindowsGSM.Core.Tests;

/// <summary>
/// New servers get a port block no other configured server uses. The legacy version read ports off the
/// WPF grid and compared against them in a single pass, so it could hand out a port that was taken.
/// </summary>
[Collection("DataRoot-servers")] // these tests create server folders; keep them serial
public class PortAllocationTests
{
    private static void Server(string id, string port, string queryPort)
    {
        _ = TestData.DataRoot;
        string dir = Path.Combine(WgsmEnvironment.DataRoot, "servers", id, "configs");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "WindowsGSM.cfg"), new[]
        {
            "servergame=\"Rust Dedicated Server\"", $"serverport=\"{port}\"", $"serverqueryport=\"{queryPort}\""
        });
    }

    [Fact]
    public void Skips_every_block_that_overlaps_an_existing_server()
    {
        // Two servers occupying 30015-30018; a new server wanting 30015 in blocks of 2 must land on 30019.
        Server("301", "30015", "30016");
        Server("302", "30017", "30018");

        var fresh = new ServerConfig("399");
        Assert.Equal("30019", fresh.GetAvailablePort("30015", 2));
    }

    [Fact]
    public void Leaves_a_free_default_port_alone()
    {
        var fresh = new ServerConfig("398");
        Assert.Equal("41000", fresh.GetAvailablePort("41000", 1));
    }
}
