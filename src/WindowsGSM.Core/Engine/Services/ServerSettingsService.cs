#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using WindowsGSM.Engine.Events;
using WindowsGSM.Engine.Servers;
using WindowsGSM.Functions;
using WindowsGSM.Installer;

namespace WindowsGSM.Engine.Services
{
    /// <summary>A game-declared setting with its current value.</summary>
    public sealed record CustomSettingValue(string Key, string Label, string Value, IReadOnlyList<string> Options);

    /// <summary>Everything the config editor shows for one server.</summary>
    /// <param name="Values">Every editable standard setting (see <see cref="ServerSettingsService.EditableKeys"/>) → value.</param>
    /// <param name="CustomReplacesBuiltIns">The game supplies a full schema; hide name/map/GSLT like legacy did.</param>
    public sealed record ServerSettings(string ServerId, string Game, bool IsSteam, string? SteamBranchLastInstalled,
        IReadOnlyDictionary<string, string> Values, IReadOnlyList<CustomSettingValue> Custom, bool CustomReplacesBuiltIns, bool CanCapture = true);

    /// <summary>
    /// Reads and writes a server's WindowsGSM.cfg for editors. Only known keys can be written — the standard
    /// settings on an allow list plus the keys the game itself declares — so a client can't inject arbitrary
    /// settings (legacy applied the same rule to custom settings on the web).
    /// </summary>
    public sealed class ServerSettingsService
    {
        private static readonly string[] Bools =
        {
            ServerConfig.SettingName.AutoRestart, ServerConfig.SettingName.AutoStart, ServerConfig.SettingName.AutoUpdate,
            ServerConfig.SettingName.UpdateOnStart, ServerConfig.SettingName.UpdateAddonsOnStart,
            ServerConfig.SettingName.DiscordAlert, ServerConfig.SettingName.RestartCrontab,
            ServerConfig.SettingName.EmbedConsole, ServerConfig.SettingName.ShowConsole,
            ServerConfig.SettingName.AutoStartAlert, ServerConfig.SettingName.AutoRestartAlert, ServerConfig.SettingName.AutoUpdateAlert,
            ServerConfig.SettingName.AutoIpUpdateAlert, ServerConfig.SettingName.RestartCrontabAlert, ServerConfig.SettingName.CrashAlert,
            ServerConfig.SettingName.SkipUserSetup, ServerConfig.SettingName.MemoryGuard, SteamContentPolicy.SteamCmdOverrideSetting,
            "perfsample", // ask the game for FPS/TPS over RCON (absent = on)
            ServerScripts.BlocksStartKey,
        };

        private static readonly string[] Ports =
        {
            ServerConfig.SettingName.ServerPort, ServerConfig.SettingName.ServerQueryPort, ServerConfig.SettingName.RconPort,
        };

        private static readonly string[] PositiveNumbers =
        {
            ServerConfig.SettingName.ServerMaxPlayer, ServerConfig.SettingName.MemoryGuardThresholdMb, ServerConfig.SettingName.MemoryGuardSustainMinutes,
            WorldSave.WaitKey, WorldSave.StopTimeoutKey, ServerScripts.TimeoutKey,
        };

        private static readonly string[] Text =
        {
            ServerConfig.SettingName.ServerName, ServerConfig.SettingName.ServerIP, ServerConfig.SettingName.ServerMap,
            ServerConfig.SettingName.ServerGSLT, ServerConfig.SettingName.ServerParam,
            ServerConfig.SettingName.SteamBranch, ServerConfig.SettingName.SteamBranchPassword,
            ServerConfig.SettingName.DiscordMessage, ServerConfig.SettingName.DiscordWebhook,
            ServerConfig.SettingName.CrontabFormat, ServerConfig.SettingName.RconIp, ServerConfig.SettingName.RconPassword,
            ServerConfig.SettingName.CPUPriority, ServerConfig.SettingName.CPUAffinity,
            WorldSave.CommandKey,
            ServerScripts.BeforeStartKey, ServerScripts.AfterStopKey, // admins only (checked by the API), .bat / .ps1 only
        };

        /// <summary>
        /// Standard settings an editor may change. Deliberately absent: the game and bookkeeping values the engine
        /// maintains itself. The scripts (which run a program) are here, but only admins may change them — the API
        /// checks — and only .bat / .ps1 files that exist.
        /// </summary>
        public static IReadOnlyList<string> EditableKeys { get; } = Text.Concat(Ports).Concat(PositiveNumbers).Concat(Bools).ToArray();

        private readonly ServerRegistry _servers;
        private readonly GameCatalog _games;
        private readonly EventBus _events;

        public ServerSettingsService(ServerRegistry servers, GameCatalog games, EventBus events)
        {
            _servers = servers;
            _games = games;
            _events = events;
        }

        public ServerSettings? Get(string id)
        {
            var s = _servers.Get(id);
            if (s == null) { return null; }
            s.ReloadConfig();
            var cfg = s.Config;

            var raw = ReadRaw(s.Id);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in EditableKeys) { values[key] = raw.TryGetValue(key, out var v) ? v : string.Empty; }

            var schema = _games.Settings(s.Game, out bool replaces);
            var custom = schema.Select(g => new CustomSettingValue(g.Key, g.Label, cfg.GetCustomSetting(g.Key, g.DefaultValue), g.Options)).ToList();
            var game = _games.Get(s.Game);
            return new ServerSettings(s.Id, s.Game, game?.IsSteam ?? false, cfg.SteamBranchLastInstalled, values, custom, replaces, game?.CanCapture ?? true);
        }

        /// <summary>
        /// Applies <paramref name="changes"/> (key → new value). All-or-nothing: returns the problems and writes
        /// nothing if any key is unknown or any value is invalid.
        /// </summary>
        public IReadOnlyList<string> Update(string id, IReadOnlyDictionary<string, string?> changes)
        {
            var s = _servers.Get(id);
            if (s == null) { return new[] { "No such server." }; }
            if (changes == null || changes.Count == 0) { return Array.Empty<string>(); }

            var custom = new HashSet<string>(_games.Settings(s.Game, out _).Select(g => g.Key), StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            var accepted = new List<(string key, string value)>();
            foreach (var (rawKey, rawValue) in changes)
            {
                string key = rawKey?.Trim() ?? string.Empty;
                string value = (rawValue ?? string.Empty).Trim();
                string? canonical = EditableKeys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                                    ?? custom.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                if (canonical == null) { errors.Add($"'{key}' isn't a setting this server has."); continue; }
                if (value.IndexOfAny(new[] { '\r', '\n' }) >= 0) { errors.Add($"{canonical}: must be a single line."); continue; }

                if (Bools.Contains(canonical))
                {
                    if (value is "true" or "True") { value = "1"; }
                    if (value is "false" or "False") { value = "0"; }
                    if (value is not ("0" or "1")) { errors.Add($"{canonical}: expected on or off."); continue; }
                }
                else if (Ports.Contains(canonical))
                {
                    if (value.Length > 0 && (!int.TryParse(value, out int port) || port < 1 || port > 65535)) { errors.Add($"{canonical}: expected a port between 1 and 65535."); continue; }
                }
                else if (ServerScripts.AdminKeys.Contains(canonical, StringComparer.OrdinalIgnoreCase))
                {
                    if (ServerScripts.Validate(value) is string bad) { errors.Add($"{canonical}: {bad}"); continue; }
                    value = ServerScripts.Clean(value);
                }
                else if (PositiveNumbers.Contains(canonical))
                {
                    if (value.Length > 0 && (!int.TryParse(value, out int n) || n < 0)) { errors.Add($"{canonical}: expected a whole number."); continue; }
                }
                accepted.Add((canonical, value));
            }
            if (errors.Count > 0) { return errors; }

            foreach (var (key, value) in accepted) { ServerConfig.SetSetting(s.Id, key, value); }
            s.ReloadConfig();
            _events.Publish(new ServerConfigChanged(s.Id, accepted.Select(a => a.key).ToList()));
            return Array.Empty<string>();
        }

        /// <summary>Every key=value in the config file as saved (quotes removed).</summary>
        private static Dictionary<string, string> ReadRaw(string id)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string file = ServerPath.GetServersConfigs(id, "WindowsGSM.cfg");
            if (!System.IO.File.Exists(file)) { return result; }
            foreach (string line in System.IO.File.ReadAllLines(file))
            {
                string[] kv = line.Split(new[] { '=' }, 2);
                if (kv.Length != 2 || kv[0].Length == 0) { continue; }
                string value = kv[1].TrimEnd('\r');
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') { value = value.Substring(1, value.Length - 2); }
                result[kv[0]] = value;
            }
            return result;
        }
    }
}
