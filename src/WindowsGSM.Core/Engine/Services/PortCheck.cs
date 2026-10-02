#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
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
                var theirs = new[] { other.Config.ServerPort, other.Config.ServerQueryPort };
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
                string text = $"Its {label} {port} is already in use by another program on this computer.";
                if (required) { return new Result(text + " Close that program, or change the port in Settings.", warnings); }
                warnings.Add(text + " Server browsers may not see it.");
            }
            return new Result(null, warnings);
        }
    }
}
