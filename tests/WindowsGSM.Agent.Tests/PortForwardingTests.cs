using System.Net;
using System.Text;
using WindowsGSM.Agent.Hosting;
using WindowsGSM.Functions;

namespace WindowsGSM.Agent.Tests;

/// <summary>Router port forwarding (UPnP): the SOAP the router gets, and forwarding/cleanup per server against a fake router.</summary>
[Collection("Agent")]
public class PortForwardingTests
{
    private readonly AgentFixture _f;
    public PortForwardingTests(AgentFixture f) => _f = f;

    private sealed class FakeRouter : IPortGateway
    {
        public readonly HashSet<(int, string)> Mapped = new();
        public string Name => "Test router";
        public string LocalIp => "192.168.1.50";
        public int? Conflict;
        public Task<string?> ExternalIpAsync(CancellationToken token) => Task.FromResult<string?>("203.0.113.9");
        public Task<string?> AddAsync(int port, string protocol, string description, CancellationToken token)
        {
            if (port == Conflict) { return Task.FromResult<string?>("Another device already has this port forwarded on the router."); }
            lock (Mapped) { Mapped.Add((port, protocol)); }
            return Task.FromResult<string?>(null);
        }
        public Task<string?> DeleteAsync(int port, string protocol, CancellationToken token)
        {
            lock (Mapped) { Mapped.Remove((port, protocol)); }
            return Task.FromResult<string?>(null);
        }
    }

    [Fact]
    public async Task Ports_are_forwarded_moved_and_removed()
    {
        const string id = "102";
        var router = new FakeRouter();
        using var upnp = new PortForwarding(_f.Context) { DiscoverOverride = _ => Task.FromResult<(IPortGateway?, string?)>((router, null)) };
        string oldPort = ServerConfig.GetSetting(id, ServerConfig.SettingName.ServerPort), oldQuery = ServerConfig.GetSetting(id, ServerConfig.SettingName.ServerQueryPort);
        try
        {
            ServerConfig.SetSetting(id, ServerConfig.SettingName.ServerPort, "27100");
            ServerConfig.SetSetting(id, ServerConfig.SettingName.ServerQueryPort, "27101");
            _f.Engine.Servers.Get(id)!.ReloadConfig();

            var st = await upnp.SetAsync(id, true);
            Assert.Null(st.Problem);
            Assert.Equal("203.0.113.9", st.ExternalIp);
            Assert.Equal(new HashSet<(int, string)> { (27100, "UDP"), (27100, "TCP"), (27101, "UDP"), (27101, "TCP") }, router.Mapped);

            // The query port changes: the old one is removed from the router.
            ServerConfig.SetSetting(id, ServerConfig.SettingName.ServerQueryPort, "27105");
            _f.Engine.Servers.Get(id)!.ReloadConfig();
            await upnp.ApplyAsync(id);
            Assert.DoesNotContain((27101, "UDP"), router.Mapped);
            Assert.Contains((27105, "TCP"), router.Mapped);

            // A port another device has: reported, the rest still forwarded.
            router.Conflict = 27105;
            st = await upnp.ApplyAsync(id);
            Assert.Contains("Another device", st.Problem);

            await upnp.SetAsync(id, false);
            Assert.Empty(router.Mapped);
            Assert.False(upnp.Get(id).Enabled);
        }
        finally
        {
            ServerConfig.SetSetting(id, ServerConfig.SettingName.ServerPort, oldPort);
            ServerConfig.SetSetting(id, ServerConfig.SettingName.ServerQueryPort, oldQuery);
            ServerConfig.SetSetting(id, PortForwarding.SettingKey, "");
            _f.Engine.Servers.Get(id)!.ReloadConfig();
        }
    }

    [Fact]
    public async Task No_router_explains_how_to_fix_it()
    {
        using var upnp = new PortForwarding(_f.Context) { DiscoverOverride = _ => Task.FromResult<(IPortGateway?, string?)>((null, "No router answered. Turn on UPnP")) };
        try
        {
            var st = await upnp.SetAsync("101", true);
            Assert.True(st.Enabled);
            Assert.Contains("UPnP", st.Problem);
        }
        finally { ServerConfig.SetSetting("101", PortForwarding.SettingKey, ""); _f.Engine.Servers.Get("101")!.ReloadConfig(); }
    }

    /// <summary>A UPnP router's description and SOAP control endpoint.</summary>
    private sealed class FakeIgd : HttpMessageHandler
    {
        public readonly List<(string Action, string Body)> Calls = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get)
            {
                return Xml("""
                    <?xml version="1.0"?><root xmlns="urn:schemas-upnp-org:device-1-0"><device><friendlyName>Home Router</friendlyName>
                    <deviceList><device><deviceList><device><serviceList><service>
                    <serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType><controlURL>/ctl/IPConn</controlURL>
                    </service></serviceList></device></deviceList></device></deviceList></device></root>
                    """);
            }
            string action = request.Headers.GetValues("SOAPAction").Single();
            string body = await request.Content!.ReadAsStringAsync(token);
            lock (Calls) { Calls.Add((action, body)); }
            Assert.Equal("http://192.168.1.1:5000/ctl/IPConn", request.RequestUri!.ToString());
            if (action.Contains("GetExternalIPAddress")) { return Xml("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:GetExternalIPAddressResponse xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\"><NewExternalIPAddress>203.0.113.7</NewExternalIPAddress></u:GetExternalIPAddressResponse></s:Body></s:Envelope>"); }
            if (body.Contains("<NewExternalPort>27015</NewExternalPort>") && action.Contains("AddPortMapping"))
            {
                return Xml("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>718</errorCode><errorDescription>ConflictInMappingEntry</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>", HttpStatusCode.InternalServerError);
            }
            return Xml("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body/></s:Envelope>");
        }

        private static HttpResponseMessage Xml(string xml, HttpStatusCode code = HttpStatusCode.OK) =>
            new(code) { Content = new StringContent(xml.Trim(), Encoding.UTF8, "text/xml") };
    }

    [Fact]
    public async Task The_router_gets_proper_SOAP_and_its_faults_read_as_plain_words()
    {
        var igd = new FakeIgd();
        var gw = await Upnp.FromDescriptionAsync(new HttpClient(igd), new Uri("http://192.168.1.1:5000/rootDesc.xml"), CancellationToken.None);
        Assert.NotNull(gw);
        Assert.Equal("Home Router", gw!.Name);

        Assert.Equal("203.0.113.7", await gw.ExternalIpAsync(CancellationToken.None));
        Assert.Null(await gw.AddAsync(7777, "UDP", "WindowsGSM #1 <Test & co>", CancellationToken.None));
        var add = igd.Calls.Last();
        Assert.Equal("\"urn:schemas-upnp-org:service:WANIPConnection:1#AddPortMapping\"", add.Action);
        Assert.Contains("<NewExternalPort>7777</NewExternalPort>", add.Body);
        Assert.Contains("<NewProtocol>UDP</NewProtocol>", add.Body);
        Assert.Contains("&lt;Test &amp; co&gt;", add.Body); // escaped

        Assert.Contains("Another device", await gw.AddAsync(27015, "UDP", "x", CancellationToken.None));
        Assert.Null(await gw.DeleteAsync(7777, "UDP", CancellationToken.None));
    }
}
