#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Engine.Services;

namespace WindowsGSM.Engine.GameConfig
{
    /// <summary>
    /// Finds a game server's own config files and turns them into editable settings. Known locations per game
    /// come first (server.properties for Minecraft, cfg/server.cfg for Source games, serverconfig.xml for
    /// 7 Days to Die, Saved/Config/WindowsServer/*.ini for Unreal games…); then a scan of the server's folder
    /// ranks anything else that looks like a config. Everything stays inside the server's files.
    /// </summary>
    public sealed class GameConfigService
    {
        public const long MaxFileBytes = 1024 * 1024;
        private const int MaxDepth = 7;
        private const int MaxVisited = 40_000;
        private const int MaxResults = 40;

        private static readonly string[] ConfigExtensions = { ".cfg", ".ini", ".properties", ".json", ".xml", ".yaml", ".yml", ".eco", ".dat" };
        private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            "steamapps", "logs", "log", "crashes", "crashdumps", "backups", "backup", "cache", ".git", "node_modules", "__pycache__",
            "Binaries", "Content", "Engine", "Plugins", "Mods", "mods", "maps", "Maps", "materials", "models", "sound", "sounds", "resource",
            "bin", "Managed", "MonoBleedingEdge", "jre", "java", "libraries", "versions", "world", "worlds", "Saves", "saves",
        };

        /// <summary>Known config locations: game name contains a keyword → path patterns ("*" = any one name).</summary>
        private static readonly (string Keyword, string Pattern, string Label)[] Known =
        {
            ("Minecraft", "server.properties", "Server properties"),
            ("PocketMine", "pocketmine.yml", "PocketMine settings"),
            ("7 Days to Die", "serverconfig.xml", "Server config"),
            ("ARK", "ShooterGame/Saved/Config/WindowsServer/GameUserSettings.ini", "Game user settings"),
            ("ARK", "ShooterGame/Saved/Config/WindowsServer/Game.ini", "Game rules"),
            ("Conan", "ConanSandbox/Saved/Config/WindowsServer/ServerSettings.ini", "Server settings"),
            ("Conan", "ConanSandbox/Saved/Config/WindowsServer/Game.ini", "Game rules"),
            ("Conan", "ConanSandbox/Saved/Config/WindowsServer/Engine.ini", "Engine"),
            ("Space Engineers", "*/SpaceEngineers-Dedicated.cfg", "Dedicated server config"),
            ("Space Engineers", "SpaceEngineers-Dedicated.cfg", "Dedicated server config"),
            ("Squad", "SquadGame/ServerConfig/Server.cfg", "Server config"),
            ("Squad", "SquadGame/ServerConfig/Rcon.cfg", "RCON"),
            ("Post Scriptum", "PostScriptum/ServerConfig/Server.cfg", "Server config"),
            ("Mordhau", "Mordhau/Saved/Config/WindowsServer/Game.ini", "Game settings"),
            ("Mordhau", "Mordhau/Saved/Config/WindowsServer/Engine.ini", "Engine"),
            ("Insurgency: Sandstorm", "Insurgency/Saved/Config/WindowsServer/Game.ini", "Game settings"),
            ("Insurgency: Sandstorm", "Insurgency/Saved/Config/WindowsServer/Engine.ini", "Engine"),
            ("DayZ", "serverDZ.cfg", "Server config"),
            ("Empyrion", "dedicated.yaml", "Dedicated server config"),
            ("Barotrauma", "serversettings.xml", "Server settings"),
            ("Avorion", "galaxies/*/server.ini", "Galaxy server settings"),
            ("Stormworks", "server_config.xml", "Server config"),
            ("Vintage Story", "serverconfig.json", "Server config"),
            ("Onset", "server_config.json", "Server config"),
            ("Eco", "Configs/Network.eco", "Network"),
            ("Eco", "Configs/Difficulty.eco", "Difficulty"),
            ("Unturned", "Servers/*/Server/Commands.dat", "Server commands"),
            ("Unturned", "Servers/*/Config.json", "Config"),
            ("The Forest", "config.cfg", "Server config"),
            ("Rust", "server/*/cfg/server.cfg", "Server config"),
            ("FiveM", "server.cfg", "Server config"),
        };

        /// <summary>Source-engine games keep server.cfg in &lt;mod&gt;/cfg/.</summary>
        private static readonly string[] SourceGames = { "Counter-Strike", "Team Fortress", "Garry's Mod", "Left 4 Dead", "Half-Life", "Day of Defeat", "Insurgency Dedicated", "No More Room", "Zombie Panic", "Deathmatch Classic", "Ricochet", "Source SDK" };

        private readonly ServerRegistry _servers;
        private readonly ServerFiles _files;

        public GameConfigService(ServerRegistry servers, ServerFiles files)
        {
            _servers = servers;
            _files = files;
        }

        // ───────────────────────────── Discovery ─────────────────────────────

        public IReadOnlyList<ConfigFileInfo> Discover(string id)
        {
            var s = _servers.Get(id) ?? throw new ConfigException("No such server.");
            string root = _files.Root(id);
            if (!Directory.Exists(root)) { return Array.Empty<ConfigFileInfo>(); }

            var found = new Dictionary<string, (ConfigFileInfo Info, int Score)>(StringComparer.OrdinalIgnoreCase);
            void AddFile(string full, bool known, string? label, int score)
            {
                var fi = new FileInfo(full);
                if (!fi.Exists || fi.Length > MaxFileBytes) { return; }
                string rel;
                try { _files.Resolve(id, _files.Relative(id, full)); rel = _files.Relative(id, full); } catch { return; } // skip anything a link takes outside
                var format = FormatOf(full);
                if (format == null) { return; }
                if (found.TryGetValue(rel, out var existing) && existing.Score >= score) { return; }
                found[rel] = (new ConfigFileInfo(rel, fi.Name, format.Value, fi.Length, fi.LastWriteTimeUtc, known, label), score);
            }

            // 1. Known locations for this game.
            foreach (var (keyword, pattern, label) in Known.Where(k => s.Game.Contains(k.Keyword, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (string full in Expand(root, pattern)) { AddFile(full, true, label, 1000); }
            }
            if (SourceGames.Any(g => s.Game.Contains(g, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (string full in Expand(root, "*/cfg/server.cfg")) { AddFile(full, true, "Server config", 1000); }
            }

            // 2. Everything else that looks like a config, ranked.
            int visited = 0;
            var stack = new Stack<(string Dir, int Depth)>();
            stack.Push((root, 0));
            while (stack.Count > 0 && visited < MaxVisited)
            {
                var (dir, depth) = stack.Pop();
                IEnumerable<string> entries;
                try { entries = Directory.EnumerateFileSystemEntries(dir).ToList(); } catch { continue; }
                foreach (string entry in entries)
                {
                    if (++visited > MaxVisited) { break; }
                    string name = Path.GetFileName(entry);
                    FileAttributes attrs;
                    try { attrs = File.GetAttributes(entry); } catch { continue; }
                    if (attrs.HasFlag(FileAttributes.Directory))
                    {
                        if (attrs.HasFlag(FileAttributes.ReparsePoint) || depth >= MaxDepth || SkipDirs.Contains(name) || name.StartsWith('.')) { continue; }
                        stack.Push((entry, depth + 1));
                        continue;
                    }
                    int score = Score(_files.Relative(id, entry));
                    if (score > 0) { AddFile(entry, false, null, score); }
                }
            }

            return found.Values
                .OrderByDescending(f => f.Score)
                .ThenBy(f => f.Info.Path.Count(c => c == '/'))
                .ThenBy(f => f.Info.Path, StringComparer.OrdinalIgnoreCase)
                .Take(MaxResults)
                .Select(f => f.Info)
                .ToList();
        }

        /// <summary>How likely a file is to be a server setting someone wants to edit (0 = not a config).</summary>
        private static int Score(string rel)
        {
            string name = Path.GetFileName(rel);
            string lower = rel.ToLowerInvariant();
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (!ConfigExtensions.Contains(ext)) { return 0; }
            if (lower.EndsWith(".deps.json") || lower.EndsWith(".runtimeconfig.json") || lower.Contains("manifest") || name.StartsWith("appmanifest", StringComparison.OrdinalIgnoreCase)) { return 0; }
            if (ext == ".dat" && !name.Equals("Commands.dat", StringComparison.OrdinalIgnoreCase)) { return 0; }
            int score = 10;
            if (lower.Contains("saved/config/windowsserver/")) { score += 80; }
            if (Regex.IsMatch(name, "server|setting|config|game(user)?settings|dedicated", RegexOptions.IgnoreCase)) { score += 50; }
            if (lower.Contains("/cfg/") || lower.StartsWith("cfg/") || lower.Contains("config")) { score += 20; }
            if (ext == ".xml" && !Regex.IsMatch(name, "server|config|setting", RegexOptions.IgnoreCase)) { score -= 8; }
            if (ext == ".json" && !Regex.IsMatch(name, "server|config|setting", RegexOptions.IgnoreCase)) { score -= 8; }
            score -= rel.Count(c => c == '/') * 2; // shallower first
            return Math.Max(score, 1);
        }

        private static IEnumerable<string> Expand(string root, string pattern)
        {
            IEnumerable<string> current = new[] { root };
            foreach (string part in pattern.Split('/'))
            {
                bool last = part == pattern.Split('/').Last();
                current = current.SelectMany(dir =>
                {
                    if (!Directory.Exists(dir)) { return Enumerable.Empty<string>(); }
                    if (part == "*") { try { return Directory.EnumerateDirectories(dir); } catch { return Enumerable.Empty<string>(); } }
                    string candidate = Path.Combine(dir, part);
                    return last ? (File.Exists(candidate) ? new[] { candidate } : Enumerable.Empty<string>()) : (Directory.Exists(candidate) ? new[] { candidate } : Enumerable.Empty<string>());
                }).ToList();
            }
            return current;
        }

        public static ConfigFormat? FormatOf(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".properties": return ConfigFormat.Properties;
                case ".ini": return ConfigFormat.Ini;
                case ".json": case ".eco": return ConfigFormat.Json;
                case ".xml": return ConfigFormat.Xml;
                case ".yaml": case ".yml": return ConfigFormat.Yaml;
                case ".dat": return ConfigFormat.Cfg;
                case ".cfg":
                    // Some games (Space Engineers) write XML with a .cfg extension.
                    try
                    {
                        using var reader = new StreamReader(path);
                        char[] buf = new char[64];
                        int n = reader.Read(buf, 0, buf.Length);
                        return new string(buf, 0, n).TrimStart('﻿', ' ', '\r', '\n', '\t').StartsWith("<") ? ConfigFormat.Xml : ConfigFormat.Cfg;
                    }
                    catch { return ConfigFormat.Cfg; }
                default: return null;
            }
        }

        // ───────────────────────────── Read / write ─────────────────────────────

        public ParsedConfig Read(string id, string path)
        {
            string full = Checked(id, path, out var format);
            var (text, _) = ReadText(full);
            var entries = format switch
            {
                ConfigFormat.Json => JsonConfig.Parse(text).Entries,
                ConfigFormat.Xml => XmlConfig.Parse(text).Entries,
                _ => LineConfig.Parse(text, format).Entries,
            };
            string? note = format == ConfigFormat.Json ? "Saving rewrites this JSON file neatly; comments in it (if any) aren't kept." : null;
            if (entries.Count == 0) { note = "No settings could be read from this file — open it in the file editor instead."; }
            return new ParsedConfig(_files.Relative(id, full), format, File.GetLastWriteTimeUtc(full), entries, note);
        }

        /// <summary>
        /// Applies changes. With <paramref name="expectedModified"/>, refuses if the file changed since it was read
        /// (the game may have rewritten it) — the settings' ids refer to that version.
        /// </summary>
        public ParsedConfig Write(string id, string path, IReadOnlyList<ConfigChange> changes, DateTimeOffset? expectedModified)
        {
            string full = Checked(id, path, out var format);
            if (expectedModified != null && Math.Abs((File.GetLastWriteTimeUtc(full) - expectedModified.Value.UtcDateTime).TotalSeconds) > 1)
            {
                throw new FileOperationException("This file changed on disk since you opened it (the server may have rewritten it). Reload it and make your changes again.", FileProblem.Conflict);
            }
            if (changes.Count == 0) { return Read(id, path); }

            var (text, encoding) = ReadText(full);
            string updated = format switch
            {
                ConfigFormat.Json => JsonConfig.Parse(text).Apply(changes),
                ConfigFormat.Xml => XmlConfig.Parse(text).Apply(changes),
                _ => LineConfig.Parse(text, format).Apply(changes),
            };
            string temp = full + ".wgsm-save";
            File.WriteAllText(temp, updated, encoding);
            File.Move(temp, full, overwrite: true);
            return Read(id, path);
        }

        private string Checked(string id, string path, out ConfigFormat format)
        {
            if (_servers.Get(id) == null) { throw new ConfigException("No such server."); }
            string full = _files.Resolve(id, path);
            if (!File.Exists(full)) { throw new FileOperationException("File not found.", FileProblem.NotFound); }
            if (new FileInfo(full).Length > MaxFileBytes) { throw new FileOperationException("This file is too large to edit as settings.", FileProblem.TooLarge); }
            format = FormatOf(full) ?? throw new ConfigException("This isn't a config file type WindowsGSM can read as settings.");
            return full;
        }

        /// <summary>Reads text and remembers whether the file had a UTF-8 byte-order mark, so saving keeps it.</summary>
        private static (string Text, Encoding Encoding) ReadText(string full)
        {
            byte[] bytes = File.ReadAllBytes(full);
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            {
                var enc = bytes[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode;
                return (enc.GetString(bytes, 2, bytes.Length - 2), enc);
            }
            return (new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), new UTF8Encoding(bom));
        }
    }
}
