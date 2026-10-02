#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace WindowsGSM.Engine.GameConfig
{
    /// <summary>
    /// JSON config: every leaf value (string, number, true/false) becomes a setting named by its path.
    /// Arrays are shown but read-only. Saving re-writes the document with the file's own indentation.
    /// </summary>
    internal sealed class JsonConfig
    {
        private readonly JsonNode _root;
        private readonly Dictionary<string, JsonNode> _leaves = new();
        private readonly int _indent;
        public List<ConfigEntry> Entries { get; } = new();

        private JsonConfig(JsonNode root, int indent)
        {
            _root = root;
            _indent = indent;
        }

        public static JsonConfig Parse(string text)
        {
            JsonNode? root;
            try { root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
            catch (JsonException ex) { throw new ConfigException("This file isn't valid JSON: " + ex.Message); }
            if (root == null) { throw new ConfigException("This file is empty."); }
            var doc = new JsonConfig(root, DetectIndent(text));
            doc.Walk(root, null, null);
            return doc;
        }

        private void Walk(JsonNode node, string? path, string? section)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (name, child) in obj.ToList())
                    {
                        if (child == null) { continue; }
                        string p = path == null ? name : $"{path}.{name}";
                        if (child is JsonObject) { Walk(child, p, p.Replace(".", " › ")); }
                        else { Walk(child, p, section); }
                    }
                    break;
                case JsonArray arr:
                    Entries.Add(new ConfigEntry(path ?? "$", section, LastSegment(path), arr.ToJsonString(), "text", "A list — edit it in the file editor.", ReadOnly: true));
                    break;
                case JsonValue v:
                    string id = path ?? "$";
                    _leaves[id] = v;
                    string text = v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString();
                    string type = v.GetValueKind() switch { JsonValueKind.True or JsonValueKind.False => "bool", JsonValueKind.Number => "number", _ => "text" };
                    Entries.Add(new ConfigEntry(id, section, LastSegment(path), text, type, null));
                    break;
            }
        }

        private static string LastSegment(string? path) => path == null ? "(value)" : path.Substring(path.LastIndexOf('.') + 1);

        public string Apply(IEnumerable<ConfigChange> changes)
        {
            foreach (var change in changes)
            {
                if (!_leaves.TryGetValue(change.Id, out var node)) { throw new ConfigException($"Setting '{change.Id}' can't be changed here."); }
                var value = (JsonValue)node;
                JsonNode replacement = value.GetValueKind() switch
                {
                    JsonValueKind.True or JsonValueKind.False => bool.TryParse(change.Value, out bool b) ? JsonValue.Create(b) : throw new ConfigException($"{change.Id}: expected true or false."),
                    JsonValueKind.Number => decimal.TryParse(change.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d)
                        ? JsonNode.Parse(d.ToString(CultureInfo.InvariantCulture))! : throw new ConfigException($"{change.Id}: expected a number."),
                    _ => JsonValue.Create(change.Value ?? string.Empty),
                };
                var parent = node.Parent ?? throw new ConfigException("The top-level value can't be changed here.");
                if (parent is JsonObject obj) { obj[node.GetPropertyName()] = replacement; }
                _leaves[change.Id] = replacement;
            }
            var options = new JsonSerializerOptions { WriteIndented = true, IndentSize = _indent, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            return _root.ToJsonString(options) + Environment.NewLine;
        }

        private static int DetectIndent(string text)
        {
            foreach (string line in text.Split('\n').Skip(1))
            {
                int n = line.Length - line.TrimStart(' ').Length;
                if (n > 0 && line.Trim().Length > 0) { return Math.Clamp(n, 1, 8); }
            }
            return 2;
        }
    }

}
