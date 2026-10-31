using System.Net;

namespace WindowsGSM.Agent.Tests;

/// <summary>Who counts as "this network" for the Let's Encrypt domain redirect (they keep any address).</summary>
public class DomainRedirectTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.4.69", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("169.254.83.107", true)]
    [InlineData("100.101.102.103", true)]   // Tailscale
    [InlineData("::ffff:192.168.1.2", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("108.6.100.143", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2001:4860:4860::8888", false)]
    public void Local_network_addresses(string ip, bool local) => Assert.Equal(local, AgentApp.IsLocalNetwork(IPAddress.Parse(ip)));
}

/// <summary>A member moved to the hub's domain keeps the address it joined with as a fallback, tried in turn.</summary>
public class HubAddressTests
{
    [Fact]
    public void After_a_failure_the_joined_address_takes_turns_with_the_domain()
    {
        var s = new AgentSettings { HubUrl = "https://games.example.com:8971", HubFallbackUrl = "https://192.168.4.69:8971", HubFallbackThumbprint = "AB12" };
        Assert.Equal(("https://games.example.com:8971", (string?)null), WindowsGSM.Agent.Hub.MemberLink.PickAddress(s, 0));
        Assert.Equal(("https://192.168.4.69:8971", (string?)"AB12"), WindowsGSM.Agent.Hub.MemberLink.PickAddress(s, 1));
        Assert.Equal(("https://games.example.com:8971", (string?)null), WindowsGSM.Agent.Hub.MemberLink.PickAddress(s, 2));
        s.HubFallbackUrl = null;
        Assert.Equal("https://games.example.com:8971", WindowsGSM.Agent.Hub.MemberLink.PickAddress(s, 1).Url);
    }
}
