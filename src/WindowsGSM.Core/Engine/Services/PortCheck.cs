#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using WindowsGSM.Engine.Servers;

namespace WindowsGSM.Engine.Services
{
    /// <summary>
    /// Before a start: is a port this server needs already taken? Another WindowsGSM server that's running on
    /// it is named; anything else listening on the game port stops the start with a clear reason (the game
    /// itself would fail with a vague one, or worse, start and be unreachable). A busy query or RCON port is
    /// only a warning — the game still runs.
    /// </summary>
    public static class PortCheck
    {
        public sealed record Result(string? Error, IReadOnlyList<string> Warnings);

        /// <summary>Overridable by tests: what something on this machine listens on (TCP or UDP), as address + port.</summary>
        public static Func<IReadOnlyCollection<System.Net.IPEndPoint>> Listening { get; set; } = () =>
        {
            var all = new List<System.Net.IPEndPoint>();
            try
            {
                var props = IPGlobalProperties.GetIPGlobalProperties();
                all.AddRange(props.GetActiveTcpListeners());
                all.AddRange(props.GetActiveUdpListeners());
            }
            catch { /* can't tell — don't block a start over it */ }
            return all;
        };

        /// <summary>Overridable by tests: the process holding a port here (TCP or UDP), or null when unknown.</summary>
        public static Func<int, (int Pid, string Name, string? Path)?> Holder { get; set; } = FindHolder;

        public static Result Check(ServerInstance s, ServerRegistry servers)
        {
            var mine = new List<(string Label, int Port, bool Required)>();
            void Add(string label, string? value, bool required)
            {
                if (int.TryParse(value, out int port) && port is > 0 and <= 65535 && !mine.Any(m => m.Port == port)) { mine.Add((label, port, required)); }
            }
            Add("game port", s.Config.ServerPort, true);
            Add("query port", s.Config.ServerQueryPort, false);
            if (mine.Count == 0) { return new Result(null, Array.Empty<string>()); }

            var warnings = new List<string>();
            // Other servers here that are running (or about to) on the same ports.
            // (Servers bound to two different addresses of this machine may share a port.)
            static bool AnyAddress(string? ip) => string.IsNullOrWhiteSpace(ip) || ip == "0.0.0.0" || ip == "::";
            bool SameAddress(ServerInstance o) => AnyAddress(s.Config.ServerIP) || AnyAddress(o.Config.ServerIP)
                || string.Equals(s.Config.ServerIP?.Trim(), o.Config.ServerIP?.Trim(), StringComparison.OrdinalIgnoreCase);
            foreach (var other in servers.All.Where(o => o.Id != s.Id && o.State != ServerState.Stopped && SameAddress(o)))
            {
                // Its RCON port too, and every port set in its Extra start parameters (+app.port 28082, -queryport=27016…).
                var theirs = new[] { other.Config.ServerPort, other.Config.ServerQueryPort,
                    ConsoleService.RconConfigured(other.Config, out _, out int rcon) ? rcon.ToString() : null }.Concat(ExtraPorts(other.Config.ServerParam)).ToList();
                foreach (var (label, port, required) in mine)
                {
                    if (!theirs.Contains(port.ToString())) { continue; }
                    string text = $"Its {label} {port} is used by #{other.Id} {other.Name}, which is running. Stop that server or change the port in Settings.";
                    if (required) { return new Result(text, warnings); }
                    warnings.Add(text);
                }
            }

            // Something listening on our port clashes when either side takes every address, or they're the same one.
            System.Net.IPAddress.TryParse(s.Config.ServerIP?.Trim(), out var myIp);
            bool Clashes(System.Net.IPEndPoint ep) =>
                myIp == null || myIp.Equals(System.Net.IPAddress.Any) || myIp.Equals(System.Net.IPAddress.IPv6Any)
                || ep.Address.Equals(System.Net.IPAddress.Any) || ep.Address.Equals(System.Net.IPAddress.IPv6Any) || ep.Address.Equals(myIp);
            var listening = Listening();
            foreach (var (label, port, required) in mine)
            {
                if (!listening.Any(ep => ep.Port == port && Clashes(ep))) { continue; }
                var (who, advice) = Describe(s, servers, Holder(port));
                string text = $"Its {label} {port} is already in use by {who}.";
                if (required) { return new Result(text + " " + (advice ?? "Close that program, or change the port in Settings."), warnings); }
                warnings.Add(text + " Server browsers may not see it.");
            }
            return new Result(null, warnings);
        }

        /// <summary>Ports set in Extra start parameters: any switch with "port" in its name, followed by a number.</summary>
        internal static IEnumerable<string> ExtraPorts(string? extra) =>
            Regex.Matches(extra ?? string.Empty, @"(?:^|\s)[+-][\w.]*port\w*(?:\s+|=)""?(\d{1,5})(?=[""\s]|$)", RegexOptions.IgnoreCase)
                .Select(m => int.Parse(m.Groups[1].Value).ToString());

        /// <summary>
        /// Who holds the port, in words (the program, its PID and where it runs from), and what to do when that's
        /// one of WindowsGSM's servers: this one left running from before, or another one that opens the same port.
        /// </summary>
        private static (string Who, string? Advice) Describe(ServerInstance s, ServerRegistry servers, (int Pid, string Name, string? Path)? holder)
        {
            if (holder is not { } h) { return ("another program on this computer", null); }
            if (h.Pid == 4) { return ("Windows itself (System, PID 4: a Windows service such as IIS or HTTP.sys)", null); }
            string who = $"{h.Name} (PID {h.Pid}{(h.Path != null ? ", " + h.Path : "")})";
            foreach (var o in servers.All)
            {
                bool runsThere = false;
                try { runsThere = h.Path != null && h.Path.StartsWith(Path.GetFullPath(Functions.ServerPath.GetServersServerFiles(o.Id)).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase); }
                catch { /* unreadable folder setting: compare by PID only */ }
                if (!runsThere) { try { runsThere = o.Process is { } p && p.Id == h.Pid; } catch { /* exited */ } }
                if (!runsThere) { continue; }
                return o.Id == s.Id
                    ? (who + ": this server's own game, still running from before", $"End it in Task Manager (Details, PID {h.Pid}), then start again.")
                    : (who + $": the game of #{o.Id} {o.Name}, which opens this port too", "Change the port on one of them in Settings.");
            }
            return (who, null);
        }

        // The owning process isn't in .NET's listener lists; Windows' extended TCP/UDP tables have it (what netstat -ano shows).
        [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);
        [DllImport("iphlpapi.dll")] private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);

        internal static (int Pid, string Name, string? Path)? FindHolder(int port)
        {
            // (TCP?, address family, where the port and PID sit in a row, row size): TCP listeners and UDP, IPv4 and IPv6.
            foreach (var (tcp, family, portAt, pidAt, rowSize) in new[] { (true, 2, 8, 20, 24), (true, 23, 20, 52, 56), (false, 2, 4, 8, 12), (false, 23, 20, 24, 28) })
            {
                foreach (int pid in Pids(tcp, family, portAt, pidAt, rowSize, port))
                {
                    if (pid == 0) { continue; }
                    if (pid == 4) { return (4, "System", null); }
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        string? path = null;
                        try { path = p.MainModule?.FileName; } catch { /* another user's or protected process */ }
                        return (pid, p.ProcessName + ".exe", path);
                    }
                    catch { /* exited meanwhile */ }
                }
            }
            return null;
        }

        private static List<int> Pids(bool tcp, int family, int portAt, int pidAt, int rowSize, int port)
        {
            var found = new List<int>();
            int size = 0;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr buffer = size > 0 ? Marshal.AllocHGlobal(size) : IntPtr.Zero;
                try
                {
                    // Listener table (TCP_TABLE_OWNER_PID_LISTENER = 3; UDP_TABLE_OWNER_PID = 1).
                    uint result = tcp ? GetExtendedTcpTable(buffer, ref size, false, family, 3, 0) : GetExtendedUdpTable(buffer, ref size, false, family, 1, 0);
                    if (result == 122 || buffer == IntPtr.Zero) { continue; } // too small (the table grew): again with the new size
                    if (result != 0) { return found; }
                    int count = Marshal.ReadInt32(buffer);
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr row = buffer + 4 + i * rowSize;
                        int rowPort = (Marshal.ReadByte(row, portAt) << 8) | Marshal.ReadByte(row, portAt + 1); // network byte order
                        if (rowPort == port) { found.Add(Marshal.ReadInt32(row, pidAt)); }
                    }
                    return found;
                }
                finally { if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); } }
            }
            return found;
        }
    }
}
