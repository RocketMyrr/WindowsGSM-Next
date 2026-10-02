#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace WindowsGSM.Engine.GameConfig
{
    /// <summary>
    /// Line-oriented config files: properties, console cfg, INI and simple YAML. Parsing remembers, for every
    /// value, exactly what surrounds it on its line; saving rewrites only the values that changed, so
    /// comments, blank lines, ordering and quoting all survive.
    /// </summary>
    internal sealed class LineConfig
    {
        private sealed class Slot
        {
            public required string Id;
            public required int Line;
            public required string Prefix;     // everything before the value (indent, key, separator, opening quote)
            public required string Suffix;     // everything after it (closing quote, ;, trailing comment)
            public required string Value;
            public char? Quote;
            public bool SpaceSeparated;        // "key value" cfg lines need quotes if the value gains spaces
            public Tuple? Tuple;               // Palworld-style (A=1,B="x") value
            public int TupleIndex = -1;
        }

        /// <summary>An INI value like (Difficulty=None,ServerName="My server",...) — each item is its own setting.</summary>
        private sealed class Tuple
        {
            public required string LinePrefix;
            public required string LineSuffix;
            public readonly List<(string Key, string Value, bool Quoted)> Items = new();
        }

        private readonly List<string> _lines;
        private readonly string _newline;
        private readonly bool _endsWithNewline;
        private readonly List<Slot> _slots = new();
        public List<ConfigEntry> Entries { get; } = new();

        private LineConfig(string text)
        {
            _newline = text.Contains("\r\n") ? "\r\n" : "\n";
            _endsWithNewline = text.EndsWith("\n");
            _lines = text.Replace("\r\n", "\n").Split('\n').ToList();
            if (_endsWithNewline) { _lines.RemoveAt(_lines.Count - 1); }
        }

        public static LineConfig Parse(string text, ConfigFormat format)
        {
            var doc = new LineConfig(text);
            switch (format)
            {
                case ConfigFormat.Properties: doc.ParseProperties(); break;
                case ConfigFormat.Cfg: doc.ParseCfg(); break;
                case ConfigFormat.Ini: doc.ParseIni(); break;
                case ConfigFormat.Yaml: doc.ParseYaml(); break;
                default: throw new ArgumentOutOfRangeException(nameof(format));
            }
            return doc;
        }

        // ───────────────────────────── Parsing ─────────────────────────────

        private readonly List<string> _pendingComment = new();

        private void Comment(string text) { string t = text.Trim(); if (t.Length > 0 && !t.All(c => c == '#' || c == '-' || c == '=' || c == '/' || c == ';')) { _pendingComment.Add(t); } }
        private void Blank() => _pendingComment.Clear();

        private string? TakeComment(string? inline)
        {
            var parts = _pendingComment.TakeLast(3).ToList();
            _pendingComment.Clear();
            if (!string.IsNullOrWhiteSpace(inline)) { parts.Add(inline.Trim()); }
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }

        private void Add(int line, string? section, string key, string prefix, string value, string suffix, char? quote, bool spaceSeparated, string? inlineComment)
        {
            var slot = new Slot { Id = line.ToString(), Line = line, Prefix = prefix, Suffix = suffix, Value = value, Quote = quote, SpaceSeparated = spaceSeparated };
            _slots.Add(slot);
            Entries.Add(new ConfigEntry(slot.Id, section, key, value, ValueTypes.Infer(value), TakeComment(inlineComment)));
        }

        private void ParseProperties()
        {
            for (int i = 0; i < _lines.Count; i++)
            {
                string line = _lines[i];
                string t = line.TrimStart();
                if (t.Length == 0) { Blank(); continue; }
                if (t[0] == '#' || t[0] == '!') { Comment(t.Substring(1)); continue; }
                int sep = line.IndexOf('=');
                if (sep < 0) { sep = line.IndexOf(':'); }
                if (sep < 0) { continue; }
                string key = line.Substring(0, sep).Trim();
                if (key.Length == 0) { continue; }
                int valueStart = sep + 1;
                while (valueStart < line.Length && (line[valueStart] == ' ' || line[valueStart] == '\t')) { valueStart++; }
                Add(i, null, key, line.Substring(0, valueStart), line.Substring(valueStart), string.Empty, null, false, null);
            }
        }

        private static readonly Regex CfgEquals = new(@"^(\s*)([A-Za-z0-9_.\-\[\]]+)(\s*=\s*)(.*)$", RegexOptions.Compiled);
        private static readonly Regex CfgSpace = new(@"^(\s*)([A-Za-z0-9_.\-+]+)(\s+)(\S.*)$", RegexOptions.Compiled);

        private void ParseCfg()
        {
            for (int i = 0; i < _lines.Count; i++)
            {
                string line = _lines[i];
                string t = line.Trim();
                if (t.Length == 0) { Blank(); continue; }
                if (t.StartsWith("//")) { Comment(t.Substring(2)); continue; }
                if (t[0] == '#' || t[0] == ';') { Comment(t.Substring(1)); continue; }
                if (t.Contains('{') || t.Contains('}') || t.StartsWith("class ", StringComparison.Ordinal)) { Blank(); continue; }

                var m = CfgEquals.Match(line);
                bool space = false;
                if (!m.Success) { m = CfgSpace.Match(line); space = true; }
                if (!m.Success) { continue; }
                string prefix = m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value;
                if (!SplitValue(m.Groups[4].Value, "//", allowSemicolon: !space, out string value, out char? quote, out string suffix, out string? comment)) { continue; }
                Add(i, null, m.Groups[2].Value, prefix + (quote.HasValue ? quote.Value.ToString() : ""), value, suffix, quote, space, comment);
            }
        }

        private void ParseIni()
        {
            string? section = null;
            for (int i = 0; i < _lines.Count; i++)
            {
                string line = _lines[i];
                string t = line.Trim();
                if (t.Length == 0) { Blank(); continue; }
                if (t[0] == ';' || t[0] == '#') { Comment(t.Substring(1)); continue; }
                if (t[0] == '[' && t.EndsWith("]")) { section = t.Substring(1, t.Length - 2).Trim(); Blank(); continue; }
                int sep = line.IndexOf('=');
                if (sep <= 0) { continue; }
                string key = line.Substring(0, sep).Trim();
                string rest = line.Substring(sep + 1);
                string prefix = line.Substring(0, sep + 1);

                // Palworld-style tuple: OptionSettings=(A=1,B="x",...)
                string rt = rest.Trim();
                if (rt.StartsWith("(") && rt.EndsWith(")") && rt.Contains('=') && TryParseTuple(rt, out var items))
                {
                    int lead = rest.Length - rest.TrimStart().Length;
                    var tuple = new Tuple { LinePrefix = prefix + rest.Substring(0, lead), LineSuffix = rest.Substring(lead + rt.Length) };
                    tuple.Items.AddRange(items);
                    string? comment = TakeComment(null);
                    for (int k = 0; k < items.Count; k++)
                    {
                        var slot = new Slot { Id = $"{i}.{k}", Line = i, Prefix = "", Suffix = "", Value = items[k].Value, Tuple = tuple, TupleIndex = k };
                        _slots.Add(slot);
                        Entries.Add(new ConfigEntry(slot.Id, section == null ? key : $"{section} › {key}", items[k].Key, items[k].Value, ValueTypes.Infer(items[k].Value), k == 0 ? comment : null));
                    }
                    continue;
                }

                if (!SplitValue(rest, null, allowSemicolon: false, out string value, out char? quote, out string suffix, out _)) { continue; }
                int valueLead = rest.Length - rest.TrimStart().Length;
                Add(i, section, key, prefix + rest.Substring(0, valueLead) + (quote.HasValue ? quote.Value.ToString() : ""), value, suffix, quote, false, null);
            }
        }

        private static readonly Regex YamlLine = new(@"^(\s*)([A-Za-z0-9_.\-]+|""[^""]*""|'[^']*')(\s*:)(\s*)(.*)$", RegexOptions.Compiled);

        private void ParseYaml()
        {
            var stack = new List<(int Indent, string Key)>();
            for (int i = 0; i < _lines.Count; i++)
            {
                string line = _lines[i];
                string t = line.Trim();
                if (t.Length == 0) { Blank(); continue; }
                if (t[0] == '#') { Comment(t.Substring(1)); continue; }
                if (t.StartsWith("- ") || t == "-" || t == "---" || t == "...") { continue; } // lists: read-only, not shown
                var m = YamlLine.Match(line);
                if (!m.Success) { continue; }
                int indent = m.Groups[1].Value.Length;
                while (stack.Count > 0 && stack[^1].Indent >= indent) { stack.RemoveAt(stack.Count - 1); }
                string key = m.Groups[2].Value.Trim('"', '\'');
                string rest = m.Groups[5].Value;
                if (rest.Trim().Length == 0 || rest.TrimStart().StartsWith("#")) { stack.Add((indent, key)); Blank(); continue; } // a nested mapping
                if (rest.TrimStart().StartsWith("|") || rest.TrimStart().StartsWith(">") || rest.TrimStart().StartsWith("[") || rest.TrimStart().StartsWith("{")) { continue; }
                string prefix = m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value + m.Groups[4].Value;
                if (!SplitValue(rest, " #", allowSemicolon: false, out string value, out char? quote, out string suffix, out string? comment)) { continue; }
                string? section = stack.Count == 0 ? null : string.Join(" › ", stack.Select(s => s.Key));
                Add(i, section, key, prefix + (quote.HasValue ? quote.Value.ToString() : ""), value, suffix, quote, false, comment);
            }
        }

        /// <summary>
        /// Splits a raw value into value / closing-quote-and-rest. Quoted values end at the matching quote;
        /// unquoted ones end at the comment marker (and, for cfg '=' lines, a trailing ';').
        /// </summary>
        private static bool SplitValue(string raw, string? commentMarker, bool allowSemicolon, out string value, out char? quote, out string suffix, out string? comment)
        {
            value = string.Empty; quote = null; suffix = string.Empty; comment = null;
            string trimmedEnd = raw.TrimEnd();
            if (trimmedEnd.Length == 0) { return true; }
            char first = raw[0];
            if (first == '"' || first == '\'')
            {
                int close = raw.IndexOf(first, 1);
                if (close < 0) { value = raw.Substring(1); quote = first; suffix = string.Empty; return true; } // unterminated: keep as is
                quote = first;
                value = raw.Substring(1, close - 1);
                suffix = raw.Substring(close); // closing quote onwards
                if (commentMarker != null)
                {
                    int c = suffix.IndexOf(commentMarker, StringComparison.Ordinal);
                    if (c >= 0) { comment = suffix.Substring(c + commentMarker.Length).Trim().TrimStart('/', '#'); }
                }
                return true;
            }

            int end = raw.Length;
            if (commentMarker != null)
            {
                int c = raw.IndexOf(commentMarker, StringComparison.Ordinal);
                if (c >= 0) { end = c; comment = raw.Substring(c + commentMarker.Length).Trim().TrimStart('/', '#'); }
            }
            if (allowSemicolon)
            {
                int s = raw.LastIndexOf(';', end - 1 < 0 ? 0 : end - 1);
                if (s >= 0 && raw.Substring(s + 1, end - s - 1).Trim().Length == 0) { end = s; }
            }
            string v = raw.Substring(0, end);
            string vt = v.TrimEnd();
            value = vt;
            suffix = v.Substring(vt.Length) + raw.Substring(end);
            return true;
        }

        private static bool TryParseTuple(string text, out List<(string Key, string Value, bool Quoted)> items)
        {
            items = new();
            string inner = text.Substring(1, text.Length - 2);
            var parts = new List<string>();
            var sb = new StringBuilder();
            bool inQuote = false;
            int depth = 0;
            foreach (char c in inner)
            {
                if (c == '"') { inQuote = !inQuote; }
                if (!inQuote && c == '(') { depth++; }
                if (!inQuote && c == ')') { depth--; }
                if (c == ',' && !inQuote && depth == 0) { parts.Add(sb.ToString()); sb.Clear(); continue; }
                sb.Append(c);
            }
            if (sb.Length > 0) { parts.Add(sb.ToString()); }
            foreach (string p in parts)
            {
                int eq = p.IndexOf('=');
                if (eq <= 0) { return false; }
                string k = p.Substring(0, eq).Trim();
                string v = p.Substring(eq + 1).Trim();
                bool quoted = v.Length >= 2 && v[0] == '"' && v[^1] == '"';
                items.Add((k, quoted ? v.Substring(1, v.Length - 2) : v, quoted));
            }
            return items.Count > 0;
        }

        // ───────────────────────────── Saving ─────────────────────────────

        public string Apply(IEnumerable<ConfigChange> changes)
        {
            var byId = _slots.ToDictionary(s => s.Id);
            var touchedTuples = new HashSet<Tuple>();
            foreach (var change in changes)
            {
                if (!byId.TryGetValue(change.Id, out var slot)) { throw new ConfigException($"Setting '{change.Id}' isn't in this file any more — reload and try again."); }
                string v = change.Value ?? string.Empty;
                if (v.IndexOfAny(new[] { '\r', '\n' }) >= 0) { throw new ConfigException("Settings must be a single line."); }
                v = ValueTypes.MatchBoolCase(slot.Value, v);

                if (slot.Tuple != null)
                {
                    var item = slot.Tuple.Items[slot.TupleIndex];
                    if (item.Quoted && v.Contains('"')) { throw new ConfigException($"{item.Key}: quotes aren't allowed in this value."); }
                    slot.Tuple.Items[slot.TupleIndex] = (item.Key, v, item.Quoted);
                    touchedTuples.Add(slot.Tuple);
                    _lines[slot.Line] = slot.Tuple.LinePrefix + "(" + string.Join(",", slot.Tuple.Items.Select(x => $"{x.Key}={(x.Quoted ? "\"" + x.Value + "\"" : x.Value)}")) + ")" + slot.Tuple.LineSuffix;
                    continue;
                }

                string prefix = slot.Prefix, suffix = slot.Suffix;
                if (slot.Quote is char q)
                {
                    if (v.Contains(q)) { throw new ConfigException($"This value can't contain the {q} character."); }
                }
                else if (slot.SpaceSeparated && (v.Contains(' ') || v.Length == 0))
                {
                    if (v.Contains('"')) { throw new ConfigException("This value can't contain both spaces and \" characters."); }
                    prefix += "\"";
                    suffix = "\"" + suffix;
                }
                _lines[slot.Line] = prefix + v + suffix;
            }
            string text = string.Join(_newline, _lines);
            return _endsWithNewline ? text + _newline : text;
        }
    }
}
