using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace WindowsGSM.Agent.Hosting;

/// <summary>A router that can forward ports (UPnP Internet Gateway Device).</summary>
public interface IPortGateway
{
    /// <summary>The router's name, for the panel.</summary>
    string Name { get; }
    /// <summary>This machine's address as the router sees it (where forwarded ports go).</summary>
    string LocalIp { get; }
    Task<string?> ExternalIpAsync(CancellationToken token);
    /// <summary>Null when forwarded, else the router's reason.</summary>
    Task<string?> AddAsync(int port, string protocol, string description, CancellationToken token);
    Task<string?> DeleteAsync(int port, string protocol, CancellationToken token);
}

/// <summary>
/// UPnP port forwarding without extra libraries: find the router with SSDP (a multicast "who's a gateway?"), read
/// its description for the WANIPConnection (or WANPPPConnection) service, then send it SOAP requests.
/// </summary>
public static class Upnp
{
    private static readonly string[] SearchTargets =
    {
        "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
        "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
    };

    /// <summary>The router, or null with the reason (no reply: UPnP is off on the router, or there isn't one).</summary>
    public static async Task<(IPortGateway? Gateway, string? Problem)> DiscoverAsync(HttpClient http, TimeSpan wait, CancellationToken token)
    {
        var locations = new List<Uri>();
        using (var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
        {
            var target = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
            foreach (string st in SearchTargets)
            {
                byte[] msg = Encoding.ASCII.GetBytes($"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nST: {st}\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\n\r\n");
                try { await udp.SendAsync(msg, msg.Length, target); } catch (SocketException) { /* no network */ }
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(wait);
            try
            {
                while (true)
                {
                    UdpReceiveResult reply;
                    // Windows reports an ICMP "port unreachable" from an earlier send as a reset on the next read: skip it.
                    try { reply = await udp.ReceiveAsync(timeout.Token); }
                    catch (SocketException) { continue; }
                    string text = Encoding.ASCII.GetString(reply.Buffer);
                    foreach (string line in text.Split("\r\n"))
                    {
                        // Only a description on the device that answered (not some other address it names).
                        if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(line[9..].Trim(), UriKind.Absolute, out var uri)
                            && uri.Scheme == Uri.UriSchemeHttp && IPAddress.TryParse(uri.Host, out var host) && host.Equals(reply.RemoteEndPoint.Address)
                            && !locations.Contains(uri))
                        {
                            locations.Add(uri);
                        }
                    }
                    if (locations.Count > 0 && timeout.Token.CanBeCanceled) { timeout.CancelAfter(TimeSpan.FromMilliseconds(300)); } // a moment for others
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { /* done listening */ }
        }
        if (locations.Count == 0) { return (null, "No router answered. Turn on UPnP in your router's settings (often under Advanced → NAT or UPnP), or forward the ports by hand."); }

        string? lastProblem = null;
        foreach (var location in locations)
        {
            try
            {
                var gw = await FromDescriptionAsync(http, location, token);
                if (gw != null) { return (gw, null); }
                lastProblem = "The router answered but doesn't offer port forwarding over UPnP.";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException) { lastProblem = $"Couldn't read the router's UPnP description: {ex.Message}"; }
        }
        return (null, lastProblem);
    }

    /// <summary>Reads a gateway's device description and finds its port-mapping service.</summary>
    public static async Task<SoapGateway?> FromDescriptionAsync(HttpClient http, Uri location, CancellationToken token)
    {
        var doc = XDocument.Parse(await http.GetStringAsync(location, token));
        XNamespace ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
        string name = doc.Descendants(ns + "friendlyName").FirstOrDefault()?.Value ?? location.Host;
        var urlBase = doc.Descendants(ns + "URLBase").FirstOrDefault()?.Value;
        var baseUri = Uri.TryCreate(urlBase, UriKind.Absolute, out var b) ? b : location;
        foreach (var service in doc.Descendants(ns + "service"))
        {
            string type = service.Element(ns + "serviceType")?.Value ?? "";
            string? control = service.Element(ns + "controlURL")?.Value;
            if (control == null || !(type.Contains(":WANIPConnection:") || type.Contains(":WANPPPConnection:"))) { continue; }
            return new SoapGateway(http, new Uri(baseUri, control), type, name, LocalAddressFor(location.Host));
        }
        return null;
    }

    /// <summary>The address of the interface that talks to <paramref name="host"/> (nothing is sent).</summary>
    internal static string LocalAddressFor(string host)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(host, 1900);
            return ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
        }
        catch { return "127.0.0.1"; }
    }
}

/// <summary>Port mappings over SOAP (UPnP IGD WANIPConnection / WANPPPConnection).</summary>
public sealed class SoapGateway : IPortGateway
{
    private readonly HttpClient _http;
    private readonly Uri _control;
    private readonly string _service;

    public SoapGateway(HttpClient http, Uri control, string serviceType, string name, string localIp)
    {
        _http = http;
        _control = control;
        _service = serviceType;
        Name = name;
        LocalIp = localIp;
    }

    public string Name { get; }
    public string LocalIp { get; }

    public async Task<string?> ExternalIpAsync(CancellationToken token)
    {
        var (ok, body) = await CallAsync("GetExternalIPAddress", Array.Empty<(string, string)>(), token);
        return ok ? Value(body, "NewExternalIPAddress") : null;
    }

    public async Task<string?> AddAsync(int port, string protocol, string description, CancellationToken token)
    {
        (string, string)[] Args(int lease) => new[]
        {
            ("NewRemoteHost", ""), ("NewExternalPort", port.ToString()), ("NewProtocol", protocol), ("NewInternalPort", port.ToString()),
            ("NewInternalClient", LocalIp), ("NewEnabled", "1"), ("NewPortMappingDescription", description), ("NewLeaseDuration", lease.ToString()),
        };
        var (ok, body) = await CallAsync("AddPortMapping", Args(0), token);
        if (!ok && Value(body, "errorCode") is "402" or "501" or "724" or "726") { (ok, body) = await CallAsync("AddPortMapping", Args(7 * 24 * 3600), token); } // some routers refuse permanent leases
        return ok ? null : Fault(body);
    }

    public async Task<string?> DeleteAsync(int port, string protocol, CancellationToken token)
    {
        var (ok, body) = await CallAsync("DeletePortMapping", new[] { ("NewRemoteHost", ""), ("NewExternalPort", port.ToString()), ("NewProtocol", protocol) }, token);
        return ok || Value(body, "errorCode") == "714" ? null : Fault(body); // 714 = no such mapping: already gone
    }

    private async Task<(bool Ok, string Body)> CallAsync(string action, IEnumerable<(string Name, string Value)> args, CancellationToken token)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>");
        sb.Append($"<u:{action} xmlns:u=\"{_service}\">");
        foreach (var (name, value) in args) { sb.Append($"<{name}>{SecurityElement.Escape(value)}</{name}>"); }
        sb.Append($"</u:{action}></s:Body></s:Envelope>");
        using var request = new HttpRequestMessage(HttpMethod.Post, _control) { Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/xml") };
        request.Content.Headers.ContentType!.CharSet = "\"utf-8\"";
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{_service}#{action}\"");
        using var response = await _http.SendAsync(request, token);
        return (response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(token));
    }

    private static string? Value(string xml, string element)
    {
        try { return XDocument.Parse(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == element)?.Value; }
        catch { return null; }
    }

    private static string Fault(string body)
    {
        string? code = Value(body, "errorCode"), text = Value(body, "errorDescription");
        return code switch
        {
            "718" => "Another device already has this port forwarded on the router.",
            "606" or "401" => "The router refused (it only lets some devices change port forwarding).",
            null => "The router didn't accept the request.",
            _ => $"The router said: {text ?? "error"} ({code}).",
        };
    }
}
