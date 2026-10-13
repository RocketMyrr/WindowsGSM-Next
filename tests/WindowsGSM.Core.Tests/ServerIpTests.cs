using WindowsGSM.Engine.Events;
using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Services;
using WindowsGSM.Functions;

namespace WindowsGSM.Core.Tests;

/// <summary>
/// A server whose IP isn't an address of this PC any more (at home the router gave the PC a new one; or it was set
/// to the router's public address): the game couldn't open its socket and quit. Now the start moves it to this PC's
/// current address — unless the server's switch is off — and the health check says so either way.
/// </summary>
[Collection("Lifecycle")]
public class ServerIpTests
{
    /// <summary>A home PC: its network address, a VPN (NordVPN-like) and a Hyper-V adapter.</summary>
    private static readonly LocalAddresses.Adapter[] HomePc =
    {
        new("172.27.160.1", "vEthernet (WSL)", false, true),
        new("10.5.0.2", "NordLynx", true, true),
        new("192.168.56.1", "Ethernet 2", false, false), // a virtual machine host-only network: no router behind it
        new("192.0.2.71", "Ethernet", true, false),
    };

    private static void WithAdapters(Action test)
    {
        var before = LocalAddresses.Source;
        LocalAddresses.Source = () => HomePc;
        try { test(); } finally { LocalAddresses.Source = before; }
    }

    [Fact]
    public void The_PCs_real_network_address_is_picked_not_a_VPN_or_virtual_adapter()
    {
        WithAdapters(() =>
        {
            Assert.Equal("192.0.2.71", LocalAddresses.Best("192.0.2.87"));   // same network as the old address
            Assert.Equal("192.0.2.71", LocalAddresses.Best("108.6.100.143")); // the router's public address: the real connection
            Assert.Equal("192.0.2.71", LocalAddresses.Best());                // a new server
            Assert.Equal("192.168.56.1", LocalAddresses.Best("192.168.56.9")); // that network, when the old address was on it

            Assert.True(LocalAddresses.IsUsable("192.0.2.71"));
            Assert.True(LocalAddresses.IsUsable("10.5.0.2"));   // one the PC has, even a VPN's, is left alone
            Assert.True(LocalAddresses.IsUsable("0.0.0.0"));
            Assert.True(LocalAddresses.IsUsable(""));
            Assert.True(LocalAddresses.IsUsable("127.0.0.1"));
            Assert.True(LocalAddresses.IsUsable("myserver.example.com")); // not an IPv4 address: the game's business
            Assert.False(LocalAddresses.IsUsable("192.0.2.87"));
            Assert.False(LocalAddresses.IsUsable("108.6.100.143"));
        });
    }

    private static ReadinessCheck IpCheck(WindowsGSM.Engine.WgsmEngine engine, string id)
    {
        var all = engine.Readiness.CheckServer(id);
        return all.FirstOrDefault(c => c.Name == "Server IP") ?? throw new Xunit.Sdk.XunitException("No Server IP check in: " + string.Join("; ", all.Select(c => $"{c.Name}={c.Status} {c.Message}")));
    }

    private static async Task<JobSnapshot> Run(OperationRequest request)
    {
        Assert.True(request.Accepted, request.Error);
        return await request.Job!.Completion.WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task A_server_on_an_old_address_moves_to_the_current_one_when_it_starts()
    {
        var before = LocalAddresses.Source;
        LocalAddresses.Source = () => HomePc;
        EngineFixture.CreateServer("150", "long", false, "serverip=\"192.0.2.87\"");
        using var engine = await EngineFixture.StartEngineAsync();
        var events = EngineFixture.Record(engine, "150");
        try
        {
            var check = IpCheck(engine, "150");
            Assert.Equal(CheckStatus.Warning, check.Status);
            Assert.Contains("updated automatically", check.Message);

            Assert.Equal(JobStatus.Succeeded, (await Run(engine.Lifecycle.Start("150"))).Status);
            Assert.Equal("192.0.2.71", engine.Servers.Get("150")!.Config.ServerIP);
            Assert.Equal("192.0.2.71", new ServerConfig("150").ServerIP); // saved
            Assert.Contains(engine.Log.Tail("150", 50), l => l.Contains("Server IP changed from 192.0.2.87 to 192.0.2.71"));
            lock (events) { Assert.Contains(events, e => e is ServerAlert { Kind: AlertKind.ServerIpChanged }); }
            Assert.Equal(CheckStatus.Pass, IpCheck(engine, "150").Status);
            await Run(engine.Lifecycle.Stop("150"));
        }
        finally { LocalAddresses.Source = before; }
    }

    [Fact]
    public async Task With_the_switch_off_the_address_is_kept_and_the_reason_is_given()
    {
        var before = LocalAddresses.Source;
        LocalAddresses.Source = () => HomePc;
        EngineFixture.CreateServer("151", "long", false, "serverip=\"192.0.2.87\"", "autoserverip=\"0\"");
        using var engine = await EngineFixture.StartEngineAsync();
        var events = EngineFixture.Record(engine, "151");
        try
        {
            var check = IpCheck(engine, "151");
            Assert.Equal(CheckStatus.Fail, check.Status);
            Assert.Contains("192.0.2.71", check.Message); // says what this PC's address is

            await Run(engine.Lifecycle.Start("151")); // the stand-in game doesn't open a socket, so it still runs
            Assert.Equal("192.0.2.87", new ServerConfig("151").ServerIP);
            Assert.Contains(engine.Log.Tail("151", 50), l => l.Contains("isn't an address of this PC"));
            lock (events) { Assert.DoesNotContain(events, e => e is ServerAlert { Kind: AlertKind.ServerIpChanged }); }
            await Run(engine.Lifecycle.Stop("151"));
        }
        finally { LocalAddresses.Source = before; }
    }
}
