#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace CDS.ProwlFork.United;

/// <summary>A <c>CGPROGRAM</c> / <c>CGINCLUDE</c> / <c>HLSLPROGRAM</c> block found in a ShaderLab shader.</summary>
internal sealed class ParsedCodeBlock
{
    public string Kind { get; init; } = "";
    public string Location { get; init; } = "";
    public string Code { get; init; } = "";
}

/// <summary>Minimal, engine-agnostic parse result for a ShaderLab shader.</summary>
internal sealed class ParsedShader
{
    public string Name { get; init; } = "";
    public List<ShaderProperty> Properties { get; } = new();
    public List<ParsedCodeBlock> CodeBlocks { get; } = new();
}

/// <summary>
/// Lightweight structural parser for the ShaderLab subset that matters to a
/// transpiler: the outer <c>Shader "Name" { ... }</c> declaration, its
/// <c>Properties</c> block and the CG/HLSL code blocks nested inside
/// <c>SubShader</c> / <c>Pass</c> sections.
/// </summary>
/// <remarks>
/// Braces inside strings and comments are masked before matching so that
/// <c>Tags { "RenderType"="Opaque" }</c> and similar constructs do not confuse
/// the block scanner. The parser is intentionally pragmatic: it does not
/// validate ShaderLab semantics, it only finds the pieces that can be
/// translated and reports anything it cannot handle through the warnings list
/// passed to <see cref="TryParse"/>.
/// </remarks>
internal static class ShaderLabParser
{
    private static readonly Regex ShaderKeyword = new(
        @"(?m)^[ \t]*Shader\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ShaderHeader = new(
        @"Shader\s+""(?<name>[^""]*)""\s*\{",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex StructureKeyword = new(
        @"(?m)^[ \t]*(?<kw>Properties|SubShader|Pass|Tags|CGPROGRAM|CGINCLUDE|HLSLPROGRAM|HLSLINCLUDE|ENDCG|ENDHLSL)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EndMarker = new(
        @"(?m)^[ \t]*END(?:CG|HLSL)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PropertyStatement = new(
        @"^\s*(?<attrs>(?:\[[^\]]*\]\s*)*)(?<name>[A-Za-z_]\w*)\s*\(\s*""(?<display>[^""]*)""\s*,\s*(?<type>[A-Za-z0-9_]+(?:\s*\([^)]*\))?)\s*\)\s*(?:=\s*(?<default>.*))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex TypeName = new(
        @"^[A-Za-z0-9_]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Attempts to parse a ShaderLab shader.
    /// </summary>
    /// <param name="text">Normalized (LF) shader source.</param>
    /// <param name="warnings">List to which structural warnings are appended.</param>
    /// <returns>The parsed shader, or <see langword="null"/> when the input is not ShaderLab.</returns>
    public static ParsedShader? TryParse(string text, List<string> warnings)
    {
        string clean = MaskStringsAndComments(text);
        Match keyword = ShaderKeyword.Match(clean);
        if (!keyword.Success)
            return null;

        // The keyword was found in the masked text; read the quoted name from
        // the original text at exactly the same offset.
        Match header = ShaderHeader.Match(text, keyword.Index);
        if (!header.Success || header.Index != keyword.Index)
            return null;

        string name = header.Groups["name"].Value;
        int open = header.Index + header.Length - 1; // index of '{'
        int close = FindMatchingBrace(clean, open);
        if (close < 0)
        {
            warnings.Add("ShaderLab: unbalanced braces after 'Shader \"...\"'; parsing to end of input.");
            close = text.Length - 1;
        }

        var shader = new ParsedShader { Name = name };
        string body = text.Substring(open + 1, Math.Max(0, close - open - 1));
        string cleanBody = MaskStringsAndComments(body);

        int subShaderCount = 0;
        int passCount = 0;

        Match match = StructureKeyword.Match(cleanBody);
        while (match.Success)
        {
            string kw = match.Groups["kw"].Value;

            if (kw == "SubShader")
            {
                subShaderCount++;
                match = match.NextMatch();
                continue;
            }

            if (kw == "Pass")
            {
                passCount++;
                match = match.NextMatch();
                continue;
            }

            if (kw is "ENDCG" or "ENDHLSL")
            {
                match = match.NextMatch();
                continue;
            }

            if (kw == "Tags")
            {
                // Tags only carry metadata; skip the whole block so that any
                // keywords inside quoted tag values cannot be misread.
                int tagsOpen = cleanBody.IndexOf('{', match.Index + match.Length - 1);
                int after = tagsOpen < 0 ? match.Index + match.Length : FindMatchingBrace(cleanBody, tagsOpen) + 1;
                match = StructureKeyword.Match(cleanBody, Math.Max(match.Index + match.Length, after));
                continue;
            }

            if (kw == "Properties")
            {
                int propsOpen = cleanBody.IndexOf('{', match.Index + match.Length - 1);
                if (propsOpen < 0)
                {
                    warnings.Add("ShaderLab: 'Properties' block has no opening brace.");
                    match = match.NextMatch();
                    continue;
                }

                int propsClose = FindMatchingBrace(cleanBody, propsOpen);
                if (propsClose < 0)
                {
                    warnings.Add("ShaderLab: unbalanced braces in 'Properties' block; parsing to end of shader.");
                    propsClose = body.Length - 1;
                }

                ParseProperties(
                    body.Substring(propsOpen + 1, Math.Max(0, propsClose - propsOpen - 1)),
                    shader.Properties,
                    warnings);

                match = StructureKeyword.Match(cleanBody, propsClose + 1);
                continue;
            }

            // CGPROGRAM / CGINCLUDE / HLSLPROGRAM / HLSLINCLUDE.
            int codeStart = match.Index + match.Length;
            Match endMarker = EndMarker.Match(cleanBody, codeStart);
            int codeEnd = endMarker.Success ? endMarker.Index : cleanBody.Length;
            if (!endMarker.Success)
                warnings.Add($"ShaderLab: unterminated {kw} block; translating to end of shader.");

            string location = $"SubShader {Math.Max(1, subShaderCount)}, Pass {Math.Max(1, passCount)}";
            shader.CodeBlocks.Add(new ParsedCodeBlock
            {
                Kind = kw,
                Location = location,
                Code = body.Substring(codeStart, Math.Max(0, codeEnd - codeStart))
            });

            // Resume after the end marker; code may itself contain lines that
            // look like ShaderLab keywords and must not be scanned again.
            match = StructureKeyword.Match(cleanBody, endMarker.Success ? endMarker.Index + endMarker.Length : codeEnd);
        }

        return shader;
    }

    private static void ParseProperties(string body, List<ShaderProperty> properties, List<string> warnings)
    {
        foreach (string statement in SplitStatements(body))
        {
            string text = statement.Trim();
            if (text.Length == 0 || text.StartsWith("//", StringComparison.Ordinal))
                continue;

            Match match = PropertyStatement.Match(text);
            if (!match.Success)
            {
                warnings.Add($"ShaderLab: could not parse property declaration '{text}'; it was skipped.");
                continue;
            }

            string rawType = match.Groups["type"].Value.Trim();
            Match typeMatch = TypeName.Match(rawType);
            string type = typeMatch.Success ? typeMatch.Value : rawType;
            type = NormalizeType(type);

            string name = match.Groups["name"].Value;
            string attributes = match.Groups["attrs"].Value.Trim();
            string? defaultValue = match.Groups["default"].Success
                ? match.Groups["default"].Value.Trim()
                : null;

            properties.Add(new ShaderProperty(
                name,
                type,
                defaultValue,
                attributes.Length == 0 ? null : attributes));

            if (defaultValue is null)
                warnings.Add($"ShaderLab property '{name}' has no default value; it must be bound explicitly.");
            if (!IsKnownType(type))
                warnings.Add($"ShaderLab property '{name}' uses unsupported type '{rawType}'; it is emitted as a float.");
        }
    }

    private static string NormalizeType(string type)
    {
        if (type.Equals("Float", StringComparison.OrdinalIgnoreCase)) return "Float";
        if (type.Equals("Int", StringComparison.OrdinalIgnoreCase)) return "Int";
        if (type.Equals("Range", StringComparison.OrdinalIgnoreCase)) return "Range";
        if (type.Equals("Color", StringComparison.OrdinalIgnoreCase)) return "Color";
        if (type.Equals("Vector", StringComparison.OrdinalIgnoreCase)) return "Vector";
        if (type.Equals("2D", StringComparison.Ordinal)) return "2D";
        if (type.Equals("Cube", StringComparison.OrdinalIgnoreCase)) return "Cube";
        if (type.Equals("3D", StringComparison.Ordinal)) return "3D";
        return type;
    }

    private static bool IsKnownType(string type) =>
        type is "Float" or "Int" or "Range" or "Color" or "Vector" or "2D" or "Cube" or "3D";

    private static List<string> SplitStatements(string body)
    {
        var result = new List<string>();
        var current = new StringBuilder();

        foreach (string raw in body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) ||
                line.StartsWith("/*", StringComparison.Ordinal))
                continue;

            if (current.Length > 0)
                current.Append(' ');
            current.Append(line);

            if (IsBalanced(current.ToString()))
            {
                result.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }

    private static bool IsBalanced(string text)
    {
        int parentheses = 0, braces = 0, brackets = 0;
        char quote = '\0';
        bool escaped = false;

        foreach (char c in text)
        {
            if (quote != '\0')
            {
                if (escaped) escaped = false;
                else if (c == '\\' && quote == '"') escaped = true;
                else if (c == quote) quote = '\0';
                continue;
            }

            switch (c)
            {
                case '"':
                case '\'':
                    quote = c;
                    break;
                case '(': parentheses++; break;
                case ')': parentheses--; break;
                case '{': braces++; break;
                case '}': braces--; break;
                case '[': brackets++; break;
                case ']': brackets--; break;
            }
        }

        return parentheses == 0 && braces == 0 && brackets == 0;
    }

    /// <summary>Returns the index of the brace matching the one at <paramref name="open"/>.</summary>
    private static int FindMatchingBrace(string clean, int open)
    {
        if (open < 0 || open >= clean.Length || clean[open] != '{')
            return -1;

        int depth = 0;
        for (int i = open; i < clean.Length; i++)
        {
            if (clean[i] == '{') depth++;
            else if (clean[i] == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Replaces string and comment contents with spaces while preserving every
    /// newline and every structural character (braces, directives, keywords).
    /// The masked string has the same length and indices as the input.
    /// </summary>
    private static string MaskStringsAndComments(string text)
    {
        var result = new StringBuilder(text.Length);
        bool lineComment = false;
        bool blockComment = false;
        char quote = '\0';
        bool escaped = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (lineComment)
            {
                if (c == '\n') { lineComment = false; result.Append(c); }
                else result.Append(' ');
                continue;
            }

            if (blockComment)
            {
                if (c == '*' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    result.Append("  ");
                    i++;
                    blockComment = false;
                }
                else
                {
                    result.Append(c == '\n' ? '\n' : ' ');
                }
                continue;
            }

            if (quote != '\0')
            {
                if (escaped) escaped = false;
                else if (c == '\\' && quote == '"') escaped = true;
                else if (c == quote) quote = '\0';
                result.Append(c == '\n' ? '\n' : ' ');
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                result.Append("  ");
                i++;
                lineComment = true;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                result.Append("  ");
                i++;
                blockComment = true;
                continue;
            }

            if (c == '"' || c == '\'')
            {
                quote = c;
                result.Append(' ');
                continue;
            }

            result.Append(c);
        }

        return result.ToString();
    }
}
