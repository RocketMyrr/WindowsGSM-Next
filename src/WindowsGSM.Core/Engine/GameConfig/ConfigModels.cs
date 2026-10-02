#nullable enable
using System;
using System.Collections.Generic;

namespace WindowsGSM.Engine.GameConfig
{
    /// <summary>How a game config file is written.</summary>
    public enum ConfigFormat
    {
        /// <summary>key=value lines, # comments (Minecraft server.properties).</summary>
        Properties,
        /// <summary>Console-style "key value" or "key = value;" lines, // comments (Source server.cfg, DayZ serverDZ.cfg).</summary>
        Cfg,
        /// <summary>[Section] + key=value, ; or # comments (Unreal Game.ini, Palworld, Avorion…).</summary>
        Ini,
        /// <summary>XML: name/value properties (7 Days to Die) or leaf elements (Space Engineers, Barotrauma).</summary>
        Xml,
        /// <summary>JSON objects (Vintage Story, Onset, Eco).</summary>
        Json,
        /// <summary>Simple YAML: nested "key: value" mappings (Empyrion).</summary>
        Yaml,
    }

    /// <summary>A config file found for a server. <see cref="Known"/> = a file we know this game uses.</summary>
    public sealed record ConfigFileInfo(string Path, string Name, ConfigFormat Format, long Size, DateTimeOffset Modified, bool Known, string? Label);

    /// <summary>
    /// One setting. <see cref="Id"/> identifies it for saving. <see cref="Type"/> is bool, number or text.
    /// <see cref="Comment"/> is the file's own explanation (the comment lines above it, or on the same line).
    /// </summary>
    public sealed record ConfigEntry(string Id, string? Section, string Key, string Value, string Type, string? Comment, bool ReadOnly = false);

    public sealed record ParsedConfig(string Path, ConfigFormat Format, DateTimeOffset Modified, IReadOnlyList<ConfigEntry> Entries, string? Note);

    public sealed record ConfigChange(string Id, string Value);

    public sealed class ConfigException : Exception
    {
        public ConfigException(string message) : base(message) { }
    }

    internal static class ValueTypes
    {
        public static string Infer(string value)
        {
            string v = value.Trim();
            if (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("false", StringComparison.OrdinalIgnoreCase)) { return "bool"; }
            if (v.Length > 0 && v.Length < 20 && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) { return "number"; }
            return "text";
        }

        /// <summary>Keeps the file's own spelling of booleans ("True" stays capitalised).</summary>
        public static string MatchBoolCase(string original, string replacement)
        {
            if (!(replacement.Equals("true", StringComparison.OrdinalIgnoreCase) || replacement.Equals("false", StringComparison.OrdinalIgnoreCase))) { return replacement; }
            string o = original.Trim();
            if (o == "True" || o == "False") { return char.ToUpperInvariant(replacement[0]) + replacement.Substring(1).ToLowerInvariant(); }
            if (o == "TRUE" || o == "FALSE") { return replacement.ToUpperInvariant(); }
            if (o == "true" || o == "false") { return replacement.ToLowerInvariant(); }
            return replacement;
        }
    }
}
