#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using WindowsGSM.Functions;

namespace WindowsGSM.Engine.Services
{
    /// <summary>A game this machine can install.</summary>
    /// <param name="Icon">Built-in: the legacy image path ("Images/Games/mc.png"). Plugin: the plugin's PNG on disk, or null.</param>
    /// <param name="Consents">Questions the install may ask (UserPrompt keys) — the wizard offers them up front.</param>
    /// <param name="CanCapture">
    /// The game's output can be captured into the panel (its plugin's AllowsEmbedConsole, as shipped). Games that
    /// can't — Rust, ARK, DayZ… — always run in a console window of their own; legacy greyed the option out for them.
    /// </param>
    public sealed record GameInfo(string Name, bool IsPlugin, string? AppId, bool LoginAnonymous, string? Icon,
        string? Description, string? Author, string? Version, string? Color, IReadOnlyList<string> Consents, bool CanCapture = true)
    {
        public bool IsSteam => !string.IsNullOrWhiteSpace(AppId);
    }

    /// <summary>A plugin that failed to compile or load.</summary>
    public sealed record BrokenPlugin(string FileName, string? Error);

    /// <summary>A per-game setting a plugin (or built-in) declares for the config editor.</summary>
    public sealed record GameSetting(string Key, string Label, string DefaultValue, IReadOnlyList<string> Options);

    /// <summary>
    /// The games this machine can run, with what the UI needs to present them: Steam app id, icon, the
    /// consents an install may ask for, and each game's custom setting schema. Port of the legacy MainWindow
    /// helpers (GetSteamAppId, GetCustomServerSettings…), which read plugin members by reflection.
    /// </summary>
    public sealed class GameCatalog
    {
        private readonly PluginCatalog _plugins;

        public GameCatalog(PluginCatalog plugins) => _plugins = plugins;

        private (object? source, IReadOnlyList<GameInfo> games) _cache;

        /// <summary>Every game, built-in and plugin, by name. Cached until the plugin list is reloaded.</summary>
        public IReadOnlyList<GameInfo> All()
        {
            var cache = _cache;
            if (cache.games != null && ReferenceEquals(cache.source, _plugins.Plugins)) { return cache.games; }
            var games = Build();
            _cache = (_plugins.Plugins, games);
            return games;
        }

        private IReadOnlyList<GameInfo> Build()
        {
            var list = new List<GameInfo>();
            var builtIn = GameServer.Data.Icon.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, true, true);
            if (builtIn != null)
            {
                foreach (DictionaryEntry e in builtIn)
                {
                    string name = (string)e.Key;
                    var info = Describe(name, e.Value as string, null);
                    if (info != null) { list.Add(info); }
                }
            }
            foreach (var p in _plugins.Plugins.Where(p => p.IsLoaded))
            {
                var info = Describe(p.FullName, null, p);
                if (info != null) { list.Add(info); }
            }
            return list.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public GameInfo? Get(string game) =>
            string.IsNullOrWhiteSpace(game) ? null : All().FirstOrDefault(g => string.Equals(g.Name, game, StringComparison.Ordinal));

        public IReadOnlyList<BrokenPlugin> BrokenPlugins() =>
            _plugins.Plugins.Where(p => !p.IsLoaded).Select(p => new BrokenPlugin(p.FileName, p.Error)).ToList();

        /// <summary>
        /// The game's custom settings. <paramref name="replacesBuiltIns"/> is true when the game declares a full
        /// schema (CustomServerSetting objects) that replaces the standard name/map/GSLT fields — legacy hid
        /// those fields in that case.
        /// </summary>
        public IReadOnlyList<GameSetting> Settings(string game, out bool replacesBuiltIns)
        {
            replacesBuiltIns = false;
            object? server = Create(game);
            if (server == null) { return Array.Empty<GameSetting>(); }

            object? raw = Member(server, "CustomSettings");
            if (raw == null) { return Array.Empty<GameSetting>(); }

            var items = raw is IEnumerable e && raw is not string ? e.Cast<object>().ToList() : new List<object> { raw };
            replacesBuiltIns = items.Any(i => i is CustomServerSetting);
            bool schema = replacesBuiltIns;

            return items.Select(ToSetting)
                .Where(s => s != null
                    && !string.Equals(s.Key, ServerConfig.SettingName.ServerParam, StringComparison.OrdinalIgnoreCase)
                    && (schema || !ServerConfig.IsBuiltInSetting(s.Key)))
                .GroupBy(s => s!.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()!)
                .ToList();
        }

        private GameInfo? Describe(string name, string? builtInIcon, PluginMetadata? plugin)
        {
            object? server;
            try { server = Create(name); } catch { return null; }
            if (server == null) { return null; }

            string? appId = Member(server, "AppId")?.ToString();
            object? anon = Member(server, "loginAnonymous");
            bool loginAnonymous = anon == null || (bool.TryParse(anon.ToString(), out bool a) && a);

            string? icon = builtInIcon;
            if (plugin != null)
            {
                icon = plugin.GameImage != null && File.Exists(plugin.GameImage) ? plugin.GameImage : null;
            }

            return new GameInfo(name, plugin != null, string.IsNullOrWhiteSpace(appId) ? null : appId, loginAnonymous, icon,
                plugin?.Plugin?.description, plugin?.Plugin?.author, plugin?.Plugin?.version, plugin?.Plugin?.color,
                ConsentsFor(name, plugin), CanCapture(server));
        }

        /// <summary>Whether a plugin instance, as created (before WindowsGSM sets anything), allows capturing its console.</summary>
        internal static bool CanCapture(object server) => Member(server, "AllowsEmbedConsole") is not bool allows || allows;

        private static IReadOnlyList<string> ConsentsFor(string name, PluginMetadata? plugin)
        {
            if (name == GameServer.MC.FullName) { return new[] { UserPrompt.Keys.Eula, UserPrompt.Keys.InstallJava }; }
            if (name == GameServer.MCBE.FullName) { return new[] { UserPrompt.Keys.Eula }; }
            if (plugin == null) { return Array.Empty<string>(); }

            // Plugins can't declare their questions, so look for the tell-tale EULA prompt in the source.
            try
            {
                string source = ServerPath.GetPlugins(plugin.FileName, plugin.FileName);
                if (File.Exists(source))
                {
                    string text = File.ReadAllText(source);
                    if (text.IndexOf("CreateYesNoPromptV1", StringComparison.Ordinal) >= 0
                        && text.IndexOf("EULA", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return new[] { UserPrompt.Keys.Eula };
                    }
                }
            }
            catch { /* best effort */ }
            return Array.Empty<string>();
        }

        private object? Create(string game) => (object?)_plugins.Create(game, null!);

        private static GameSetting? ToSetting(object item)
        {
            switch (item)
            {
                case null: return null;
                case CustomServerSetting cs:
                    return string.IsNullOrWhiteSpace(cs.Key) ? null
                        : new GameSetting(cs.Key, string.IsNullOrWhiteSpace(cs.Label) ? cs.Key : cs.Label, cs.DefaultValue ?? string.Empty, cs.Options ?? Array.Empty<string>());
                case string key:
                    return string.IsNullOrWhiteSpace(key) ? null : new GameSetting(key, key, string.Empty, Array.Empty<string>());
            }

            string? k = Member(item, "Key")?.ToString() ?? Member(item, "Name")?.ToString() ?? Member(item, "SettingName")?.ToString();
            if (string.IsNullOrWhiteSpace(k)) { return null; }
            string label = Member(item, "Label")?.ToString() ?? Member(item, "DisplayName")?.ToString() ?? k;
            string def = Member(item, "DefaultValue")?.ToString() ?? Member(item, "Default")?.ToString() ?? string.Empty;
            object? rawOptions = Member(item, "Options") ?? Member(item, "Values") ?? Member(item, "AllowedValues");
            var options = rawOptions is IEnumerable oe && rawOptions is not string
                ? oe.Cast<object>().Where(o => o != null).Select(o => o.ToString()!).ToArray()
                : Array.Empty<string>();
            return new GameSetting(k, label, def, options);
        }

        private static object? Member(object source, string name)
        {
            var type = source.GetType();
            var property = type.GetProperty(name);
            if (property != null) { return property.GetValue(source); }
            return type.GetField(name)?.GetValue(source);
        }
    }
}
