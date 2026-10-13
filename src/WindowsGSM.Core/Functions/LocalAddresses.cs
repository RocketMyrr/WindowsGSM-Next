#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WindowsGSM.Functions
{
    /// <summary>
    /// NEXT: this PC's own IPv4 addresses, and which one a game server should listen on. At home the router hands
    /// the PC its address (DHCP) and it can change; a server set to the old one can't open its socket and quits
    /// ("address not available", Rust's "Couldn't initialize Steam Server"). The public address belongs to the
    /// router, never to the PC, so it isn't one of these either.
    /// </summary>
    public static class LocalAddresses
    {
        /// <summary>Swappable for tests: the PC's adapters as (address, adapter name/description, has a gateway, virtual/VPN).</summary>
        public static Func<IReadOnlyList<Adapter>> Source { get; set; } = Read;

        public sealed record Adapter(string Address, string Name, bool HasGateway, bool Virtual);

        /// <summary>
        /// True when a server can listen on <paramref name="ip"/> here: one of this PC's addresses, or "every
        /// address" (empty, 0.0.0.0) — anything that isn't a plain IPv4 address is left to the game.
        /// </summary>
        public static bool IsUsable(string? ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) { return true; }
            if (!IPAddress.TryParse(ip.Trim(), out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork) { return true; }
            if (parsed.Equals(IPAddress.Any) || IPAddress.IsLoopback(parsed)) { return true; }
            return Source().Any(a => a.Address == parsed.ToString());
        }

        /// <summary>
        /// The address a server should use now: on the same network as <paramref name="previous"/> when there is
        /// one (192.168.4.87 → 192.168.4.71), otherwise the real network connection — not a VPN, not Hyper-V/WSL or
        /// a virtual machine's adapter, and one with a gateway (the router). Null when there's nothing better.
        /// </summary>
        public static string? Best(string? previous = null)
        {
            var real = Source().Where(a => !a.Virtual).ToList();
            if (real.Count == 0) { return null; }
            if (previous != null && IPAddress.TryParse(previous.Trim(), out var old) && old.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] o = old.GetAddressBytes();
                var same = real.FirstOrDefault(a => IPAddress.Parse(a.Address).GetAddressBytes().Take(3).SequenceEqual(o.Take(3)));
                if (same != null) { return same.Address; }
            }
            return (real.FirstOrDefault(a => a.HasGateway) ?? real[0]).Address;
        }

        private static readonly string[] VirtualWords =
        {
            "virtual", "hyper-v", "vethernet", "wsl", "vmware", "virtualbox", "vpn", "nordlynx", "wireguard", "tap-", "tap ",
            "openvpn", "tailscale", "zerotier", "hamachi", "radmin", "loopback", "bluetooth", "teredo", "isatap",
        };

        private static IReadOnlyList<Adapter> Read()
        {
            var list = new List<Adapter>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) { continue; }
                    IPInterfaceProperties props;
                    try { props = nic.GetIPProperties(); } catch (NetworkInformationException) { continue; }
                    string label = (nic.Name + " " + nic.Description).ToLowerInvariant();
                    bool isVirtual = nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp
                        || VirtualWords.Any(w => label.Contains(w));
                    bool gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                    foreach (var u in props.UnicastAddresses)
                    {
                        if (u.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(u.Address)) { continue; }
                        if (u.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)) { continue; } // no address from the router
                        list.Add(new Adapter(u.Address.ToString(), nic.Name, gateway, isVirtual));
                    }
                }
            }
            catch (NetworkInformationException) { /* no answer from Windows: nothing to offer */ }
            return list;
        }
    }
}
