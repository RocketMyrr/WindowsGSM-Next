#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace WindowsGSM.Engine.GameConfig
{
    /// <summary>
    /// XML config. Two common shapes: <c>&lt;property name="X" value="Y"/&gt;</c> lists (7 Days to Die) and plain
    /// leaf elements <c>&lt;ServerName&gt;Y&lt;/ServerName&gt;</c> (Space Engineers, Stormworks, Barotrauma).
    ///
    /// Saving never re-serialises the document: it finds each changed value in the original text (from the
    /// parser's line/column information) and replaces just those characters, so the file stays byte-for-byte
    /// identical apart from the new values — the game's own formatting, comments and quirks included.
    /// </summary>
    internal sealed class XmlConfig
    {
        private readonly string _text;
        private readonly int[] _lineStarts;
        private readonly Dictionary<string, Func<string, (int Start, int Length, string Replacement)>> _editors = new();
        public List<ConfigEntry> Entries { get; } = new();

        private XmlConfig(string text)
        {
            _text = text;
            var starts = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++) { if (text[i] == '\n') { starts.Add(i + 1); } }
            _lineStarts = starts.ToArray();
        }

        public static XmlConfig Parse(string text)
        {
            XDocument doc;
            try { doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo); }
            catch (XmlException ex) { throw new ConfigException($"This file isn't valid XML (line {ex.LineNumber}): {ex.Message}"); }
            var cfg = new XmlConfig(text);
            if (doc.Root != null) { cfg.Walk(doc.Root, null); }
            return cfg;
        }

        private void Walk(XElement el, string? section)
        {
            int index = 0;
            foreach (var child in el.Elements())
            {
                index++;
                string id = $"{Path(el)}/{child.Name.LocalName}[{index}]";
                var nameAttr = child.Attribute("name") ?? child.Attribute("Name");
                var valueAttr = child.Attribute("value") ?? child.Attribute("Value");
                if (nameAttr != null && valueAttr != null && !child.HasElements)
                {
                    var attr = valueAttr;
                    _editors[id] = v => AttributeEdit(attr, v);
                    Entries.Add(new ConfigEntry(id, section, nameAttr.Value, valueAttr.Value, ValueTypes.Infer(valueAttr.Value), CommentNear(child)));
                }
                else if (!child.HasElements && !child.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name.LocalName != "type" && !a.Name.NamespaceName.Contains("XMLSchema-instance")))
                {
                    var leaf = child;
                    _editors[id] = v => ElementEdit(leaf, v);
                    Entries.Add(new ConfigEntry(id, section, child.Name.LocalName, child.Value, ValueTypes.Infer(child.Value), CommentNear(child)));
                }
                else if (child.HasElements)
                {
                    Walk(child, section == null ? child.Name.LocalName : $"{section} › {child.Name.LocalName}");
                }
            }
        }

        private static string Path(XElement el) => el.Parent == null ? el.Name.LocalName : $"{Path(el.Parent)}/{el.Name.LocalName}[{el.ElementsBeforeSelf().Count() + 1}]";

        /// <summary>The comment just above the element, or on the same line right after it (7 Days to Die style).</summary>
        private static string? CommentNear(XElement el)
        {
            var next = el.NextNode;
            if (next is XText nt && !nt.Value.Contains('\n')) { next = next.NextNode; }
            if (next is XComment after) { return after.Value.Trim(); }
            var node = el.PreviousNode;
            if (node is XText t && string.IsNullOrWhiteSpace(t.Value) && t.Value.Count(c => c == '\n') <= 1) { node = node.PreviousNode; }
            return node is XComment before ? before.Value.Trim() : null;
        }

        private int Offset(IXmlLineInfo info) => _lineStarts[info.LineNumber - 1] + info.LinePosition - 1;

        private (int, int, string) AttributeEdit(XAttribute attr, string value)
        {
            int pos = Offset(attr);                           // at the attribute's name
            pos = _text.IndexOf('=', pos) + 1;
            while (char.IsWhiteSpace(_text[pos])) { pos++; }
            char quote = _text[pos];
            int end = _text.IndexOf(quote, pos + 1);
            string escaped = value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(quote.ToString(), quote == '"' ? "&quot;" : "&apos;");
            return (pos + 1, end - pos - 1, escaped);
        }

        private (int, int, string) ElementEdit(XElement el, string value)
        {
            int nameAt = Offset(el);                          // just after '<'
            int close = FindTagEnd(nameAt);                   // index of the start tag's '>'
            string escaped = value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            if (_text[close - 1] == '/')
            {
                // <Name/> or <Name /> → <Name>value</Name>
                int nameEnd = nameAt;
                while (nameEnd < _text.Length && !char.IsWhiteSpace(_text[nameEnd]) && _text[nameEnd] != '/' && _text[nameEnd] != '>') { nameEnd++; }
                string name = _text.Substring(nameAt, nameEnd - nameAt);
                int slash = close - 1;
                while (slash > nameEnd && char.IsWhiteSpace(_text[slash - 1])) { slash--; }
                return (slash, close - slash + 1, $">{escaped}</{name}>");
            }
            int contentEnd = _text.IndexOf("</", close + 1, StringComparison.Ordinal);
            return (close + 1, contentEnd - close - 1, escaped);
        }

        /// <summary>The '>' that ends a start tag, skipping any '>' inside quoted attribute values.</summary>
        private int FindTagEnd(int from)
        {
            char quote = '\0';
            for (int i = from; i < _text.Length; i++)
            {
                char c = _text[i];
                if (quote != '\0') { if (c == quote) { quote = '\0'; } continue; }
                if (c == '"' || c == '\'') { quote = c; continue; }
                if (c == '>') { return i; }
            }
            throw new ConfigException("This XML file ends in the middle of a tag.");
        }

        public string Apply(IEnumerable<ConfigChange> changes)
        {
            var edits = new List<(int Start, int Length, string Replacement)>();
            foreach (var change in changes)
            {
                if (!_editors.TryGetValue(change.Id, out var edit)) { throw new ConfigException($"Setting '{change.Id}' isn't in this file any more — reload and try again."); }
                var entry = Entries.First(e => e.Id == change.Id);
                edits.Add(edit(ValueTypes.MatchBoolCase(entry.Value, change.Value ?? string.Empty)));
            }
            var sb = new StringBuilder(_text);
            foreach (var (start, length, replacement) in edits.OrderByDescending(e => e.Start))
            {
                sb.Remove(start, length).Insert(start, replacement);
            }
            return sb.ToString();
        }
    }
}
