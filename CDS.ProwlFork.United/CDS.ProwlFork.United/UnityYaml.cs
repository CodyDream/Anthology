#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CDS.ProwlFork.United;

/// <summary>
/// Engine-agnostic reader for the subset of YAML emitted by Unity serialisation.
/// </summary>
/// <remarks>
/// Supports document markers (<c>--- !u!21 &amp;2100000</c>), indentation-based maps and
/// lists (including Unity's indentless list style), flow maps/lists such as
/// <c>{fileID: 1, guid: abc, type: 3}</c> spanning multiple lines, quoted strings and
/// colons inside scalar values. Unity <c>.meta</c> files, which have no document
/// marker, are parsed as a single implicit document with class/file id zero.
/// Binary inputs (and JSON ShaderGraph files) produce an empty result rather than
/// throwing.
/// </remarks>
public static class UnityYaml
{
    private static readonly Regex DocumentHeader = new(
        @"^---\s+!u!(-?\d+)\s+&(-?\d+)(?:\s+.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses Unity YAML text into documents.
    /// </summary>
    /// <param name="text">Contents of a Unity YAML asset, prefab, scene or meta file.</param>
    /// <returns>One entry per <c>--- !u!...</c> document, or a single implicit entry for meta files.</returns>
    public static IReadOnlyList<UnityDocument> Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<UnityDocument>();

        string content = text[0] == '\uFEFF' ? text.Substring(1) : text;
        List<Line> lines = Tokenize(content);
        if (lines.Count == 0)
            return Array.Empty<UnityDocument>();

        bool hasYamlHeader = lines.Exists(static line =>
            line.Indent == 0 && line.Text.StartsWith("%YAML", StringComparison.Ordinal));
        int firstMarker = lines.FindIndex(static line => IsDocumentMarker(line));

        if (firstMarker < 0)
        {
            // No marker: this is a .meta file (or a non-Unity file). Only accept a
            // leading YAML mapping key so random binary/JSON input is skipped.
            if (!hasYamlHeader && !LooksLikeKey(lines[0].Text))
                return Array.Empty<UnityDocument>();

            var parser = new BlockParser(lines);
            return parser.ParseBlock(0) is MapNode root
                ? [new UnityDocument { Fields = root.Fields }]
                : Array.Empty<UnityDocument>();
        }

        var documents = new List<UnityDocument>();
        for (int i = 0; i < lines.Count;)
        {
            if (!IsDocumentMarker(lines[i]))
            {
                i++;
                continue;
            }

            long classId = 0;
            long fileId = 0;
            Match header = DocumentHeader.Match(lines[i].Text);
            if (header.Success)
            {
                long.TryParse(header.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out classId);
                long.TryParse(header.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out fileId);
            }

            int end = i + 1;
            while (end < lines.Count && !IsDocumentMarker(lines[end]))
                end++;

            var body = new List<Line>();
            for (int j = i + 1; j < end; j++)
            {
                if (lines[j].Text == "..." || lines[j].Text.StartsWith("...", StringComparison.Ordinal))
                    continue;
                body.Add(lines[j]);
            }

            if (body.Count > 0)
            {
                var parser = new BlockParser(body);
                if (parser.ParseBlock(body[0].Indent) is MapNode root)
                {
                    // Unity wraps each object in a single class key ("Material:",
                    // "MonoBehaviour:", ...). Unwrap it so Fields holds the object's
                    // actual properties.
                    IReadOnlyDictionary<string, UnityYamlNode> fields = root.Fields;
                    if (fields.Count == 1)
                    {
                        foreach (var pair in fields)
                        {
                            if (pair.Value is MapNode inner)
                                fields = inner.Fields;
                        }
                    }

                    documents.Add(new UnityDocument
                    {
                        ClassId = classId,
                        FileId = fileId,
                        Fields = fields
                    });
                }
            }

            i = end;
        }

        return documents;
    }

    /// <summary>
    /// Converts a parsed node into a Unity object reference.
    /// </summary>
    /// <param name="node">Usually a flow map with <c>fileID</c>, <c>guid</c> and <c>type</c>.</param>
    /// <returns>The reference, or a null reference when <paramref name="node"/> is not a map.</returns>
    public static UnityRef ParseRef(UnityYamlNode? node)
    {
        if (node is not MapNode map)
            return new UnityRef(0, "", 0);

        long fileId = ReadLong(map, "fileID");
        string guid = ReadString(map, "guid") ?? "";
        int type = (int)ReadLong(map, "type");
        return new UnityRef(fileId, guid, type);
    }

    /// <summary>
    /// Navigates a document through a chain of map keys.
    /// </summary>
    /// <param name="doc">Document to navigate.</param>
    /// <param name="path">Keys to follow in order.</param>
    /// <returns>The node at the final key, or <see langword="null"/> if any key is missing or non-map.</returns>
    public static UnityYamlNode? Get(this UnityDocument doc, params string[] path)
    {
        if (doc is null || path is null || path.Length == 0)
            return null;

        IReadOnlyDictionary<string, UnityYamlNode> current = doc.Fields;
        for (int i = 0; i < path.Length; i++)
        {
            if (!current.TryGetValue(path[i], out UnityYamlNode? node))
                return null;
            if (i == path.Length - 1)
                return node;
            if (node is not MapNode map)
                return null;
            current = map.Fields;
        }

        return null;
    }

    private static string? ReadString(MapNode map, string key) =>
        map.Fields.TryGetValue(key, out UnityYamlNode? node) && node is ScalarNode scalar
            ? scalar.Value
            : null;

    private static long ReadLong(MapNode map, string key)
    {
        string? value = ReadString(map, key);
        return value is not null &&
               long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result)
            ? result
            : 0;
    }

    private readonly record struct Line(int Indent, string Text);

    private static bool IsDocumentMarker(Line line) =>
        line.Indent == 0 && line.Text.Length >= 3 &&
        line.Text.StartsWith("---", StringComparison.Ordinal) &&
        (line.Text.Length == 3 || char.IsWhiteSpace(line.Text[3]));

    private static bool LooksLikeKey(string text) =>
        text.Length > 0 && text[0] != '{' && text[0] != '[' && text[0] != '%' &&
        TrySplitKey(text, out _, out _);

    private static List<Line> Tokenize(string text)
    {
        var result = new List<Line>();
        foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            // Unity sometimes embeds binary mesh data after the YAML text. Such lines
            // are not YAML and are simply ignored.
            bool binary = false;
            foreach (char c in raw)
            {
                if (c < 0x20 && c != '\t')
                {
                    binary = true;
                    break;
                }
            }
            if (binary)
                continue;

            int indent = 0;
            while (indent < raw.Length && raw[indent] == ' ')
                indent++;

            string content = raw.Substring(indent).TrimEnd();
            if (content.Length == 0 || content[0] == '#')
                continue;

            result.Add(new Line(indent, content));
        }

        return result;
    }

    private static bool IsSequenceItem(string text) =>
        text.Length > 0 && text[0] == '-' &&
        (text.Length == 1 || char.IsWhiteSpace(text[1]));

    /// <summary>Splits "key: value" at the first colon that is followed by whitespace or end of line.</summary>
    private static bool TrySplitKey(string text, out string key, out string inline)
    {
        key = "";
        inline = text;

        char quote = '\0';
        bool escaped = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (escaped)
                    escaped = false;
                else if (c == '\\' && quote == '"')
                    escaped = true;
                else if (c == quote)
                    quote = '\0';
                continue;
            }

            if (c == '"' || c == '\'')
            {
                quote = c;
                continue;
            }

            if (c != ':' || (i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1])))
                continue;

            key = Unquote(text.Substring(0, i).Trim());
            inline = text.Substring(i + 1).TrimStart();
            return key.Length > 0;
        }

        return false;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            if (value[0] == '"' && value[value.Length - 1] == '"')
                return Unescape(value.Substring(1, value.Length - 2));
            if (value[0] == '\'' && value[value.Length - 1] == '\'')
                return value.Substring(1, value.Length - 2).Replace("''", "'");
        }

        return value;
    }

    private static string Unescape(string value)
    {
        if (value.IndexOf('\\') < 0)
            return value;

        var builder = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 == value.Length)
            {
                builder.Append(value[i]);
                continue;
            }

            char next = value[++i];
            builder.Append(next switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                _ => next
            });
        }

        return builder.ToString();
    }

    private static int FlowBalance(string text)
    {
        int depth = 0;
        char quote = '\0';
        bool escaped = false;
        foreach (char c in text)
        {
            if (quote != '\0')
            {
                if (escaped)
                    escaped = false;
                else if (c == '\\' && quote == '"')
                    escaped = true;
                else if (c == quote)
                    quote = '\0';
                continue;
            }

            switch (c)
            {
                case '"':
                case '\'':
                    quote = c;
                    break;
                case '{':
                case '[':
                    depth++;
                    break;
                case '}':
                case ']':
                    depth--;
                    break;
            }
        }

        return depth;
    }

    private static void SkipWhite(string text, ref int pos)
    {
        while (pos < text.Length && char.IsWhiteSpace(text[pos]))
            pos++;
    }

    private static string ReadUntil(string text, ref int pos, params char[] delimiters)
    {
        int start = pos;
        char quote = '\0';
        bool escaped = false;
        while (pos < text.Length)
        {
            char c = text[pos];
            if (quote != '\0')
            {
                if (escaped)
                    escaped = false;
                else if (c == '\\' && quote == '"')
                    escaped = true;
                else if (c == quote)
                    quote = '\0';
                pos++;
                continue;
            }

            if (c == '"' || c == '\'')
            {
                quote = c;
                pos++;
                continue;
            }

            if (Array.IndexOf(delimiters, c) >= 0)
                break;

            pos++;
        }

        return text.Substring(start, pos - start).Trim();
    }

    private static UnityYamlNode ParseFlow(string text, ref int pos)
    {
        SkipWhite(text, ref pos);
        if (pos >= text.Length)
            return new ScalarNode("");

        char open = text[pos];
        if (open != '{' && open != '[')
            return new ScalarNode(Unquote(ReadUntil(text, ref pos, ',', '}', ']')));

        bool isMap = open == '{';
        char close = isMap ? '}' : ']';
        pos++;

        var fields = new Dictionary<string, UnityYamlNode>(StringComparer.Ordinal);
        var items = new List<UnityYamlNode>();

        while (true)
        {
            SkipWhite(text, ref pos);
            if (pos >= text.Length)
                break;
            if (text[pos] == close)
            {
                pos++;
                break;
            }

            if (isMap)
            {
                string key = Unquote(ReadUntil(text, ref pos, ':'));
                SkipWhite(text, ref pos);
                if (pos < text.Length && text[pos] == ':')
                    pos++;
                fields[key] = ParseFlow(text, ref pos);
            }
            else
            {
                items.Add(ParseFlow(text, ref pos));
            }

            SkipWhite(text, ref pos);
            if (pos < text.Length && text[pos] == ',')
            {
                pos++;
                continue;
            }
            if (pos < text.Length && text[pos] == close)
            {
                pos++;
            }
            break;
        }

        return isMap
            ? new MapNode(new ReadOnlyDictionary<string, UnityYamlNode>(fields))
            : new ListNode(new ReadOnlyCollection<UnityYamlNode>(items));
    }

    /// <summary>Recursive-descent parser over pre-tokenised, indentation-based lines.</summary>
    private sealed class BlockParser
    {
        private readonly List<Line> _lines;
        private int _index;

        public BlockParser(List<Line> lines) => _lines = lines;

        public UnityYamlNode ParseBlock(int indent) =>
            IsSequenceItem(_lines[_index].Text) ? ParseSequence(indent) : ParseMap(indent);

        private UnityYamlNode ParseMap(int indent)
        {
            var fields = new Dictionary<string, UnityYamlNode>(StringComparer.Ordinal);
            while (_index < _lines.Count)
            {
                Line line = _lines[_index];
                if (line.Indent < indent)
                    break;
                if (line.Indent > indent || IsSequenceItem(line.Text))
                {
                    _index++;
                    continue;
                }

                if (!TrySplitKey(line.Text, out string key, out string inline))
                {
                    _index++;
                    continue;
                }

                _index++;
                fields[key] = ParseValue(inline, indent);
            }

            return new MapNode(new ReadOnlyDictionary<string, UnityYamlNode>(fields));
        }

        private UnityYamlNode ParseSequence(int indent)
        {
            var items = new List<UnityYamlNode>();
            while (_index < _lines.Count)
            {
                Line line = _lines[_index];
                if (line.Indent != indent || !IsSequenceItem(line.Text))
                    break;

                string rest = line.Text.Length == 1 ? "" : line.Text.Substring(1).TrimStart();
                _index++;

                if (rest.Length == 0)
                {
                    items.Add(_index < _lines.Count && _lines[_index].Indent > indent
                        ? ParseBlock(_lines[_index].Indent)
                        : new ScalarNode(""));
                    continue;
                }

                if (rest[0] == '{' || rest[0] == '[')
                {
                    items.Add(ParseFlowFromCurrentLine(rest));
                    continue;
                }

                if (TrySplitKey(rest, out string key, out string inline))
                {
                    var fields = new Dictionary<string, UnityYamlNode>(StringComparer.Ordinal);
                    fields[key] = ParseValue(inline, indent);

                    // Remaining properties of a map-valued list item are indented
                    // relative to the dash, e.g. "- time: 0" / "  value: 1".
                    if (_index < _lines.Count &&
                        _lines[_index].Indent > indent &&
                        !IsSequenceItem(_lines[_index].Text) &&
                        ParseMap(_lines[_index].Indent) is MapNode continuation)
                    {
                        foreach (var pair in continuation.Fields)
                            fields[pair.Key] = pair.Value;
                    }

                    items.Add(new MapNode(new ReadOnlyDictionary<string, UnityYamlNode>(fields)));
                    continue;
                }

                items.Add(new ScalarNode(Unquote(rest)));
            }

            return new ListNode(new ReadOnlyCollection<UnityYamlNode>(items));
        }

        private UnityYamlNode ParseValue(string inline, int keyIndent)
        {
            if (inline.Length == 0)
                return ParseNestedOrEmpty(keyIndent);
            if (inline[0] == '{' || inline[0] == '[')
                return ParseFlowFromCurrentLine(inline);

            // Unity wraps long plain scalars in meta files across lines; fold them
            // back together until a deeper non-scalar line is reached.
            var parts = new List<string> { Unquote(inline) };
            while (_index < _lines.Count &&
                   _lines[_index].Indent > keyIndent &&
                   !IsSequenceItem(_lines[_index].Text) &&
                   !TrySplitKey(_lines[_index].Text, out _, out _))
            {
                parts.Add(_lines[_index].Text.Trim());
                _index++;
            }

            return new ScalarNode(string.Join(" ", parts));
        }

        private UnityYamlNode ParseNestedOrEmpty(int keyIndent)
        {
            if (_index < _lines.Count)
            {
                Line next = _lines[_index];
                // Unity writes list items at the same indentation as their key:
                //   m_Component:
                //   - component: {fileID: 1}
                if (next.Indent > keyIndent ||
                    (next.Indent == keyIndent && IsSequenceItem(next.Text)))
                {
                    return ParseBlock(next.Indent);
                }
            }

            return new ScalarNode("");
        }

        private UnityYamlNode ParseFlowFromCurrentLine(string first)
        {
            var text = new StringBuilder(first);
            int balance = FlowBalance(text.ToString());
            while (balance > 0 && _index < _lines.Count)
            {
                text.Append('\n');
                text.Append(_lines[_index].Text);
                _index++;
                balance = FlowBalance(text.ToString());
            }

            int pos = 0;
            return ParseFlow(text.ToString(), ref pos);
        }
    }
}
