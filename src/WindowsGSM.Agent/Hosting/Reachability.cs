using System.Collections;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using WindowsGSM.Agent.Api;
using WindowsGSM.Engine.Servers;

namespace WindowsGSM.Agent.Hosting;

/// <summary>One finding of "can players reach this server?" — Status is pass, warn, fail or info.</summary>
public sealed record ReachCheck(string Name, string Status, string Message);

public sealed record ReachReport(string? PublicIp, string? LocalIp, IReadOnlyList<ReachCheck> Checks);

/// <summary>
/// "Can players reach my server?" in plain words: is the game listening on its port, is its address one others
/// can use, does Windows Firewall let it in, is this machine behind a router (so ports need forwarding), and —
/// for Steam games — is it on Steam's public server list (the legacy app's check, which is the real proof).
/// </summary>
public sealed class Reachability
{
    private readonly AgentContext _ctx;
    private readonly HttpClient _http;

    public Reachability(AgentContext ctx, HttpClient? http = null)
    {
        _ctx = ctx;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    /// <summary>Swappable for tests: the internet-facing lookups.</summary>
    public Func<Task<string?>> PublicIp { get; set; } = null!;
    /// <summary>Router port forwarding (UPnP), to report whether it's done.</summary>
    public PortForwarding? Forwarding { get; set; }
    public Func<string, Task<IReadOnlyList<(string Addr, int GamePort)>?>> SteamServersAt { get; set; } = null!;

    public async Task<ReachReport> CheckAsync(string id)
    {
        PublicIp ??= FetchPublicIpAsync;
        SteamServersAt ??= FetchSteamServersAsync;
        var s = _ctx.Engine.Servers.Get(id) ?? throw new ArgumentException("No such server.");
        var checks = new List<ReachCheck>();
        var cfg = s.Config;
        int.TryParse(cfg.ServerPort, out int port);
        int.TryParse(cfg.ServerQueryPort, out int queryPort);
        bool running = s.State == ServerState.Running;
        string? localIp = LocalIPv4();

        // 1. Is it up and listening?
        if (!running)
        {
            checks.Add(new("Running", "info", "The server isn't running — start it to check the port and Steam's list too."));
        }
        else if (port > 0)
        {
            var (udp, tcp) = Listening(port);
            checks.Add(udp || tcp
                ? new("Listening", "pass", $"The game is listening on port {port} ({string.Join(" + ", new[] { udp ? "UDP" : null, tcp ? "TCP" : null }.Where(x => x != null))}).")
                : new("Listening", "fail", $"Nothing is listening on port {port}. The game may still be starting, or it uses a different port than its settings say."));
        }

        // 2. Is the address one other computers can use?
        string ip = (cfg.ServerIP ?? "").Trim();
        if (IPAddress.TryParse(ip, out var bound) && IPAddress.IsLoopback(bound))
        {
            checks.Add(new("Address", "warn", $"The server's IP is set to {ip}, which only this computer can use. If players can't join, set it to {localIp ?? "this machine's network address"} (or 0.0.0.0) in Settings."));
        }
        else
        {
            checks.Add(new("Address", "pass", $"Players connect to port {(port > 0 ? port : "?")}{(queryPort > 0 && queryPort != port ? $"; the server browser uses {queryPort}" : "")}."));
        }

        // 3. Windows Firewall
        string? exe = null;
        try { exe = running ? s.Process?.MainModule?.FileName : null; } catch { /* access denied */ }
        var firewall = FirewallAllows(exe, port);
        checks.Add(firewall switch
        {
            true => new("Firewall", "pass", "Windows Firewall lets the game in."),
            false => new("Firewall", "warn", exe == null
                ? $"No Windows Firewall rule opens port {port}. Use \"Allow through firewall\" on the server's overview (one Windows prompt)."
                : $"No Windows Firewall rule lets {Path.GetFileName(exe)} in. Use \"Allow through firewall\" on the server's overview (one Windows prompt)."),
            null => new("Firewall", "info", "Couldn't read the Windows Firewall rules."),
        });

        // 4. Behind a router?
        string? publicIp = null;
        try { publicIp = await PublicIp(); } catch { /* offline */ }
        if (publicIp == null)
        {
            checks.Add(new("Internet", "warn", "Couldn't find this machine's public IP — is it online?"));
        }
        else if (LocalAddresses().Contains(publicIp))
        {
            checks.Add(new("Router", "pass", $"This machine has the public IP {publicIp} itself — no port forwarding needed."));
        }
        else if (Forwarding != null && PortForwarding.Enabled(s) && Forwarding.Get(id) is { At: not null } fwd)
        {
            checks.Add(fwd.Problem == null
                ? new("Router", "pass", $"Ports {string.Join(", ", fwd.Entries.Select(e => e.Port).Distinct())} are forwarded to {fwd.LocalIp} automatically (UPnP on {fwd.Router}).")
                : new("Router", "warn", $"Automatic port forwarding didn't work: {fwd.Problem}"));
        }
        else
        {
            string ports = string.Join(" and ", new[] { port, queryPort }.Where(p => p > 0).Distinct());
            checks.Add(new("Router", "info", $"This machine is behind a router (public IP {publicIp}, local {localIp ?? "?"}). For players outside your network, forward UDP/TCP {(ports.Length > 0 ? ports : "the game's ports")} to {localIp ?? "this machine"} on the router — or turn on \"Forward ports automatically\" below (UPnP)."));
        }

        // 5. Steam's server list: the proof that the internet can see it.
        bool steamGame = _ctx.Engine.Games.Get(s.Game)?.IsSteam == true;
        if (steamGame && running && publicIp != null)
        {
            IReadOnlyList<(string Addr, int GamePort)>? listed = null;
            try { listed = await SteamServersAt(publicIp); } catch { /* Steam unreachable */ }
            if (listed == null)
            {
                checks.Add(new("Steam list", "info", "Couldn't ask Steam right now — try again in a minute."));
            }
            else if (listed.Any(x => x.GamePort == port || x.Addr.EndsWith(":" + queryPort) || x.Addr.EndsWith(":" + port)))
            {
                checks.Add(new("Steam list", "pass", "It's on Steam's public server list — players anywhere can find and join it."));
            }
            else
            {
                bool fresh = s.StartedAt is { } at && DateTimeOffset.UtcNow - at < TimeSpan.FromMinutes(3);
                checks.Add(new("Steam list", fresh ? "info" : "fail", fresh
                    ? "Not on Steam's list yet — it can take a few minutes after starting. Check again shortly."
                    : "Not on Steam's public server list, so players outside your network probably can't reach it. Forward the ports above on your router (and check the firewall)."));
            }
        }
        else if (steamGame && !running)
        {
            checks.Add(new("Steam list", "info", "Start the server to check whether it shows on Steam's public list."));
        }

        return new ReachReport(publicIp, localIp, checks);
    }

    private static (bool Udp, bool Tcp) Listening(int port)
    {
        try
        {
            var props = IPGlobalProperties.GetIPGlobalProperties();
            return (props.GetActiveUdpListeners().Any(e => e.Port == port), props.GetActiveTcpListeners().Any(e => e.Port == port));
        }
        catch { return (false, false); }
    }

    /// <summary>true: an enabled inbound allow rule covers the program or the port; false: none does; null: couldn't tell.</summary>
    private static bool? FirewallAllows(string? exe, int port)
    {
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
            foreach (dynamic rule in (IEnumerable)policy.Rules)
            {
                try
                {
                    if (!(bool)rule.Enabled || (int)rule.Direction != 1 || (int)rule.Action != 1) { continue; }
                    string? app = rule.ApplicationName as string;
                    if (exe != null && app != null && string.Equals(Environment.ExpandEnvironmentVariables(app), exe, StringComparison.OrdinalIgnoreCase)) { return true; }
                    if (app == null && port > 0 && PortMatches(rule.LocalPorts as string, port)) { return true; }
                }
                catch { /* odd rule */ }
            }
            return false;
        }
        catch { return null; }
    }

    private static bool PortMatches(string? ports, int port)
    {
        if (string.IsNullOrWhiteSpace(ports)) { return false; }
        foreach (string part in ports.Split(',', StringSplitOptions.TrimEntries))
        {
            string[] range = part.Split('-');
            if (range.Length == 1 && int.TryParse(range[0], out int p) && p == port) { return true; }
            if (range.Length == 2 && int.TryParse(range[0], out int a) && int.TryParse(range[1], out int b) && port >= a && port <= b) { return true; }
        }
        return false;
    }

    private static string? LocalIPv4()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530); // no packets are sent; this just picks the outgoing interface
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch { return null; }
    }

    private static HashSet<string> LocalAddresses()
    {
        var set = new HashSet<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var a in ni.GetIPProperties().UnicastAddresses) { set.Add(a.Address.ToString()); }
            }
        }
        catch { /* best effort */ }
        return set;
    }

    private async Task<string?> FetchPublicIpAsync()
    {
        string text = (await _http.GetStringAsync("https://api.ipify.org")).Trim();
        return IPAddress.TryParse(text, out _) ? text : null;
    }

    private async Task<IReadOnlyList<(string Addr, int GamePort)>?> FetchSteamServersAsync(string publicIp)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync($"https://api.steampowered.com/ISteamApps/GetServersAtAddress/v0001?addr={Uri.EscapeDataString(publicIp)}&format=json"));
        var response = doc.RootElement.GetProperty("response");
        if (!response.TryGetProperty("servers", out var servers)) { return Array.Empty<(string, int)>(); }
        return servers.EnumerateArray()
            .Select(x => (x.TryGetProperty("addr", out var a) ? a.GetString() ?? "" : "", x.TryGetProperty("gameport", out var g) && g.TryGetInt32(out int gp) ? gp : 0))
            .ToList();
    }
}
