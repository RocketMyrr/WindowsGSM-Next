#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Functions;

namespace WindowsGSM.Engine.Services
{
    /// <summary>One player list: who's on it, and whether the game enforces it (null when it always does).</summary>
    public sealed record AccessList(string Key, string Label, string Hint, bool? Enforced, IReadOnlyList<string> Names);

    /// <summary>
    /// NEXT: Minecraft's whitelist and operators (Java: whitelist.json, ops.json; Bedrock: allowlist.json), editable from
    /// the Players tab. A running server is told with its own commands (it rewrites these files itself, so editing them
    /// under it would be undone); a stopped one has its files edited.
    /// </summary>
    public sealed class AccessLists
    {
        private sealed record Kind(string Key, string Label, string File, string Hint, string? Switch, string AddCommand, string RemoveCommand);

        private static readonly Kind[] Java =
        {
            new("whitelist", "Whitelist", "whitelist.json", "Only these players can join while the whitelist is on (white-list=true in server.properties).", "white-list", "whitelist add {0}", "whitelist remove {0}"),
            new("ops", "Operators", "ops.json", "Operators can use every server command.", null, "op {0}", "deop {0}"),
        };

        private static readonly Kind[] Bedrock =
        {
            new("allowlist", "Allowlist", "allowlist.json", "Only these players can join while the allowlist is on (allow-list=true in server.properties).", "allow-list", "allowlist add \"{0}\"", "allowlist remove \"{0}\""),
        };

        private readonly ServerRegistry _servers;
        private readonly ConsoleService _console;
        private readonly ServerLog _log;

        /// <summary>Swappable for tests: Mojang's name → UUID lookup.</summary>
        public static Func<string, Task<string>> FetchProfile { get; set; } = name => Http.DownloadStringAsync($"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(name)}");

        public AccessLists(ServerRegistry servers, ConsoleService console, ServerLog log)
        {
            _servers = servers;
            _console = console;
            _log = log;
        }

        private static Kind[]? KindsFor(ServerInstance s) =>
            string.Equals(s.Game, GameServer.MC.FullName, StringComparison.OrdinalIgnoreCase) ? Java
            : string.Equals(s.Game, GameServer.MCBE.FullName, StringComparison.OrdinalIgnoreCase) ? Bedrock
            : null;

        /// <summary>The server's lists, or null when its game has none WindowsGSM edits.</summary>
        public IReadOnlyList<AccessList>? Get(string id)
        {
            var s = _servers.Get(id);
            var kinds = s == null ? null : KindsFor(s);
            if (s == null || kinds == null) { return null; }
            string files = ServerPath.GetServersServerFiles(id);
            var props = Properties(Path.Combine(files, "server.properties"));
            return kinds.Select(k => new AccessList(k.Key, k.Label, k.Hint,
                k.Switch == null ? null : props.TryGetValue(k.Switch, out string? on) && on.Equals("true", StringComparison.OrdinalIgnoreCase),
                Read(Path.Combine(files, k.File)).Select(e => (string?)e["name"] ?? "").Where(n => n.Length > 0).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList())).ToList();
        }

        /// <summary>Adds (or removes) <paramref name="name"/>. Null when done, else what went wrong.</summary>
        public async Task<string?> ChangeAsync(string id, string list, string name, bool add, string? by)
        {
            var s = _servers.Get(id);
            var kind = s == null ? null : KindsFor(s)?.FirstOrDefault(k => k.Key == list);
            if (s == null || kind == null) { return "This server has no such list."; }
            name = (name ?? "").Trim();
            bool bedrock = ReferenceEquals(KindsFor(s), Bedrock);
            if (!(bedrock ? Regex.IsMatch(name, "^[A-Za-z0-9 ]{1,16}$") : Regex.IsMatch(name, "^[A-Za-z0-9_]{1,16}$")))
            {
                return bedrock ? "Enter a gamertag: letters, numbers and spaces, up to 16." : "Enter a Minecraft name: letters, numbers and _, up to 16.";
            }

            // Running: the server's own command (it keeps these files in memory and writes them back).
            if (s.Process != null && s.State == ServerState.Running)
            {
                var result = await _console.SendAsync(id, string.Format(add ? kind.AddCommand : kind.RemoveCommand, name), by).ConfigureAwait(false);
                return result.Sent ? null : $"Couldn't tell the server: {result.Error ?? "no console or RCON to send the command to"}.";
            }

            string file = ServerPath.GetServersServerFiles(id, kind.File);
            var entries = Read(file);
            entries.RemoveAll(e => string.Equals((string?)e["name"], name, StringComparison.OrdinalIgnoreCase));
            if (add)
            {
                if (bedrock) { entries.Add(new JObject { ["ignoresPlayerLimit"] = false, ["name"] = name }); }
                else
                {
                    var (uuid, exact) = await UuidAsync(s.Id, name).ConfigureAwait(false);
                    if (uuid == null) { return $"Mojang doesn't know a player called {name}."; }
                    var entry = new JObject { ["uuid"] = uuid, ["name"] = exact };
                    if (kind.Key == "ops") { entry["level"] = 4; entry["bypassesPlayerLimit"] = false; }
                    entries.Add(entry);
                }
            }
            File.WriteAllText(file, new JArray(entries).ToString(Newtonsoft.Json.Formatting.Indented));
            _log.Write(id, $"Players: {(add ? "added" : "removed")} {name} {(add ? "to" : "from")} {kind.Label.ToLowerInvariant()}{(by != null ? $" (by {by})" : "")}");
            return null;
        }

        /// <summary>
        /// A Java player's UUID: Mojang's for online-mode servers; for offline-mode ones, the one the server computes
        /// from the name ("OfflinePlayer:" + name, MD5, version 3).
        /// </summary>
        private static async Task<(string? Uuid, string Name)> UuidAsync(string serverId, string name)
        {
            var props = Properties(ServerPath.GetServersServerFiles(serverId, "server.properties"));
            if (props.TryGetValue("online-mode", out string? online) && online.Equals("false", StringComparison.OrdinalIgnoreCase)) { return (OfflineUuid(name), name); }
            try
            {
                var profile = JObject.Parse(await FetchProfile(name).ConfigureAwait(false));
                string? raw = (string?)profile["id"];
                if (raw == null || raw.Length != 32) { return (null, name); }
                return ($"{raw[..8]}-{raw[8..12]}-{raw[12..16]}-{raw[16..20]}-{raw[20..]}", (string?)profile["name"] ?? name);
            }
            catch { return (null, name); } // not found (Mojang answers 404) or unreachable
        }

        public static string OfflineUuid(string name)
        {
            byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
            hash[6] = (byte)((hash[6] & 0x0f) | 0x30); // version 3
            hash[8] = (byte)((hash[8] & 0x3f) | 0x80); // IETF variant
            string hex = Convert.ToHexString(hash).ToLowerInvariant();
            return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
        }

        private static List<JObject> Read(string file)
        {
            try { return File.Exists(file) ? JArray.Parse(File.ReadAllText(file)).OfType<JObject>().ToList() : new List<JObject>(); }
            catch { return new List<JObject>(); }
        }

        private static Dictionary<string, string> Properties(string file)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(file)) { return result; }
                foreach (string line in File.ReadLines(file))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0 && !line.TrimStart().StartsWith("#")) { result[line[..eq].Trim()] = line[(eq + 1)..].Trim(); }
                }
            }
            catch { /* unreadable */ }
            return result;
        }
    }
}
