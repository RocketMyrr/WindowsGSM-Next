using System.Net;
using System.Net.Sockets;

namespace WindowsGSM.Agent.Tests;

/// <summary>The agent also answers on ::1 (so "localhost" doesn't wait for IPv6 to fail), but only when it can.</summary>
public class LocalAddressTests
{
    [Fact]
    public void Ipv6_loopback_is_used_only_when_free()
    {
        if (!Socket.OSSupportsIPv6) { return; } // IPv6 switched off: nothing to check, and the agent skips it too
        using var holder = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        try { holder.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0)); }
        catch (SocketException) { return; } // no ::1 on this machine
        holder.Listen();
        int taken = ((IPEndPoint)holder.LocalEndPoint!).Port;

        Assert.False(AgentApp.Ipv6LoopbackFree(taken));

        holder.Close();
        Assert.True(AgentApp.Ipv6LoopbackFree(taken));
    }
}
