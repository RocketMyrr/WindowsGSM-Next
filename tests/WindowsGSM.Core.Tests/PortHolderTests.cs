using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using WindowsGSM.Engine.Services;

namespace WindowsGSM.Core.Tests;

/// <summary>A port that's taken names what holds it (from Windows' own tables), instead of "another program".</summary>
public class PortHolderTests
{
    [Fact]
    public void The_program_holding_a_udp_or_tcp_port_is_found()
    {
        int pid = Environment.ProcessId;
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var tcp = new TcpListener(IPAddress.IPv6Any, 0);
        tcp.Start();
        try
        {
            foreach (int port in new[] { ((IPEndPoint)udp.Client.LocalEndPoint!).Port, ((IPEndPoint)tcp.LocalEndpoint).Port })
            {
                var holder = PortCheck.FindHolder(port);
                Assert.NotNull(holder);
                Assert.Equal(pid, holder!.Value.Pid);
                Assert.Equal(Process.GetCurrentProcess().ProcessName + ".exe", holder.Value.Name);
            }
        }
        finally { tcp.Stop(); }
    }

    [Fact]
    public void Ports_in_extra_start_parameters_are_read()
    {
        Assert.Equal(new[] { "28082", "28022", "27016", "7777" },
            PortCheck.ExtraPorts("-noconsole +app.port 28082 +rcon.port \"28022\" -QueryPort=27016 -port=7777 +server.seed 12345 -logfile \"port.log\""));
        Assert.Empty(PortCheck.ExtraPorts(null));
    }

    [Fact]
    public void A_free_port_has_no_holder()
    {
        int port;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Any, 0))) { port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port; }
        Assert.Null(PortCheck.FindHolder(port));
    }
}
