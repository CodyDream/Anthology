#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CDS.ProwlFork.United;

/// <summary>
/// One ShaderLab property entry (for example <c>_MainTex ("Albedo", 2D) = "white" {}</c>).
/// </summary>
/// <param name="Name">Unity property name, including its leading underscore when present.</param>
/// <param name="Type">Unity property type: Float, Range, Color, Vector, 2D, Cube or 3D.</param>
/// <param name="DefaultValue">Unity default value text, or <see langword="null"/> if absent.</param>
/// <param name="Attributes">Raw leading attribute list such as <c>[HDR] [Gamma]</c>, or <see langword="null"/>.</param>
public sealed record ShaderProperty(string Name, string Type, string? DefaultValue, string? Attributes);

/// <summary>
/// Result of a ShaderLab/HLSL to Slang translation.
/// </summary>
/// <param name="Source">Generated Slang source, including comments for everything that needs manual work.</param>
/// <param name="Properties">ShaderLab properties found in the source, in declaration order.</param>
/// <param name="Warnings">Constructs that could not be translated safely.</param>
public sealed record TranspiledShader(
    string Source,
    IReadOnlyList<ShaderProperty> Properties,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Engine-agnostic, pragmatic ShaderLab + HLSL to Slang transpiler.
/// </summary>
/// <remarks>
/// The transpiler handles the structural parts of a Unity shader (properties,
/// tags, subshaders, passes and CG/HLSL code blocks) and translates the safe
/// subset of HLSL/Cg. Anything Unity-specific that cannot be mapped reliably is
/// commented out or left in place and reported through
/// <see cref="TranspiledShader.Warnings"/>, together with the original source.
/// ShaderGraph JSON is deliberately not parsed: it is not ShaderLab source.
/// </remarks>
public static class UnityShaderTranspiler
{
    /// <summary>
    /// Platform/variant symbols that must always have a concrete value in the
    /// generated Slang, otherwise <c>#if SYMBOL</c> blocks fail to compile.
    /// </summary>
    private static readonly (string Name, string Value)[] PreprocessorShims =
    {
        ("UNITY_REVERSED_Z", "0"),
        ("UNITY_UV_STARTS_AT_TOP", "1"),
        ("UNITY_SINGLE_PASS_STEREO", "0"),
        ("STEREO_CUBEMAP_RENDER_ON", "0"),
        ("SHADER_API_GLES", "0")
    };

    private static readonly Regex PreprocessorSymbol = new(
        @"\b(?:UNITY_[A-Z0-9_]+|STEREO_[A-Z0-9_]+|SHADER_API_[A-Z0-9_]+)\b",
        RegexOptions.CultureInvariant);

    private static readonly Regex DefinedSymbol = new(
        @"^#define\s+([A-Za-z_]\w*)",
        RegexOptions.CultureInvariant);

    /// <summary>Translates a ShaderLab shader to a Slang module.</summary>
    /// <param name="source">Contents of a Unity <c>.shader</c> file.</param>
    /// <param name="name">Optional name override for the generated header comment.</param>
    /// <returns>The generated Slang source, extracted properties and warnings.</returns>
    public static TranspiledShader Transpile(string source, string? name = null) =>
        TranspileCore(source, name, compute: false);

    /// <summary>
    /// Translates a compute shader to a Slang module. Both ShaderLab shaders
    /// containing <c>#pragma kernel</c> and raw <c>.compute</c> HLSL are accepted.
    /// </summary>
    /// <param name="source">Contents of a Unity <c>.compute</c> file or a ShaderLab shader.</param>
    /// <param name="name">Optional name override for the generated header comment.</param>
    /// <returns>The generated Slang source, extracted properties and warnings.</returns>
    public static TranspiledShader TranspileCompute(string source, string? name = null) =>
        TranspileCore(source, name, compute: true);

    private static TranspiledShader TranspileCore(string source, string? name, bool compute)
    {
        string text = source ?? "";
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text.Substring(1);

        var warnings = new List<string>();
        ParsedShader? parsed = ShaderLabParser.TryParse(text, warnings);

        if (parsed is null)
        {
            return compute
                ? TranspileRawCompute(text, name, warnings)
                : NonShaderLabResult(text, warnings);
        }

        if (text.Contains("ShaderGraph", StringComparison.Ordinal) ||
            text.Contains("m_SerializedShaderGraph", StringComparison.Ordinal))
        {
            warnings.Add(
                "ShaderGraph-only nodes were detected; node data is not parsed. Only the ShaderLab structure and its CG/HLSL blocks were translated.");
        }

        string shaderName = !string.IsNullOrEmpty(name) ? name! : parsed.Name;
        if (shaderName.Length == 0)
            shaderName = "(unnamed)";

        var sections = new List<(ParsedCodeBlock Block, HlslTranslation Translation)>();
        foreach (ParsedCodeBlock block in parsed.CodeBlocks)
        {
            HlslTranslation translation = ShaderHlslTranslator.Translate(block.Code, parsed.Properties, shaderName);
            sections.Add((block, translation));
            warnings.AddRange(translation.Warnings);
        }

        return BuildResult(shaderName, parsed.Properties, sections, warnings, text);
    }

    private static TranspiledShader TranspileRawCompute(string text, string? name, List<string> warnings)
    {
        warnings.Add("Input is not ShaderLab; treating the whole input as a raw HLSL compute shader.");

        HlslTranslation translation = ShaderHlslTranslator.Translate(text, Array.Empty<ShaderProperty>(), name);
        warnings.AddRange(translation.Warnings);

        var block = new ParsedCodeBlock
        {
            Kind = "HLSL (raw compute source)",
            Location = "compute shader",
            Code = text
        };
        var sections = new List<(ParsedCodeBlock, HlslTranslation)> { (block, translation) };

        return BuildResult(
            !string.IsNullOrEmpty(name) ? name! : "(unnamed compute shader)",
            Array.Empty<ShaderProperty>(),
            sections,
            warnings,
            text);
    }

    private static TranspiledShader NonShaderLabResult(string text, List<string> warnings)
    {
        string trimmed = text.TrimStart();
        bool looksLikeShaderGraph =
            text.Contains("ShaderGraph", StringComparison.Ordinal) ||
            text.Contains("\"nodes\"", StringComparison.Ordinal) ||
            text.Contains("m_SerializedShaderGraph", StringComparison.Ordinal) ||
            trimmed.StartsWith("{", StringComparison.Ordinal);

        warnings.Add(looksLikeShaderGraph
            ? "Input looks like ShaderGraph JSON; ShaderGraph assets are not parsed. Export ShaderLab/HLSL source before translating."
            : "Input is not ShaderLab source (expected 'Shader \"Name\" { ... }'); no translation was performed.");

        var sb = new StringBuilder();
        AppendLine(sb, "// ---------------------------------------------------------------------------");
        AppendLine(sb, "// UnityShaderTranspiler: no translation performed.");
        AppendLine(sb, "// ---------------------------------------------------------------------------");
        AppendWarnings(sb, warnings);
        AppendOriginalSource(sb, text, "Original input (verbatim, line-commented):");

        return new TranspiledShader(
            sb.ToString(),
            Array.Empty<ShaderProperty>(),
            warnings.AsReadOnly());
    }

    private static TranspiledShader BuildResult(
        string shaderName,
        IReadOnlyList<ShaderProperty> properties,
        List<(ParsedCodeBlock Block, HlslTranslation Translation)> sections,
        List<string> warnings,
        string originalSource)
    {
        // Collect all diagnostics before rendering the warning header so the
        // in-source warning list matches the returned Warnings collection.
        CollectPropertyWarnings(properties, warnings);
        List<string> shimLines = BuildPreprocessorShims(originalSource, warnings);

        var sb = new StringBuilder();
        AppendLine(sb, "// ---------------------------------------------------------------------------");
        AppendLine(sb, $"// Slang translation of Unity shader \"{shaderName}\"");
        AppendLine(sb, "// Generated by UnityShaderTranspiler. Review the warnings before use.");
        AppendLine(sb, "// ---------------------------------------------------------------------------");
        AppendLine(sb, "");

        AppendWarnings(sb, warnings);
        if (shimLines.Count > 0)
        {
            foreach (string line in shimLines)
                AppendLine(sb, line);
            AppendLine(sb, "");
        }

        AppendPropertyBlock(sb, properties);
        AppendGeneratedDeclarations(sb, sections);
        AppendCode(sb, sections);
        AppendOriginalSource(sb, originalSource, "Original ShaderLab/HLSL source (verbatim, line-commented):");

        return new TranspiledShader(
            sb.ToString(),
            properties is List<ShaderProperty> list ? list.AsReadOnly() : properties,
            warnings.AsReadOnly());
    }

    private static void AppendWarnings(StringBuilder sb, IReadOnlyList<string> warnings)
    {
        AppendLine(sb, $"// Warnings ({warnings.Count}):");
        if (warnings.Count == 0)
        {
            AppendLine(sb, "//   none - every construct was translated safely.");
        }
        else
        {
            for (int i = 0; i < warnings.Count; i++)
                AppendLine(sb, $"//  {i + 1}. {warnings[i]}");
        }
        AppendLine(sb, "");
    }

    /// <summary>
    /// Builds the <c>#define</c> prefix that gives Unity platform/variant
    /// conditionals a concrete value in Slang. Symbols the source defines
    /// itself are left untouched.
    /// </summary>
    private static List<string> BuildPreprocessorShims(string originalSource, List<string> warnings)
    {
        var defined = new HashSet<string>(StringComparer.Ordinal);
        var used = new List<string>();
        var usedSet = new HashSet<string>(StringComparer.Ordinal);

        foreach (string rawLine in originalSource.Split('\n'))
        {
            string line = rawLine.Trim();
            Match define = DefinedSymbol.Match(line);
            if (define.Success)
            {
                defined.Add(define.Groups[1].Value);
                continue;
            }

            if (!line.StartsWith("#", StringComparison.Ordinal))
                continue;

            foreach (Match match in PreprocessorSymbol.Matches(line))
            {
                string symbol = match.Value;
                if (!defined.Contains(symbol) && usedSet.Add(symbol))
                    used.Add(symbol);
            }
        }

        var shims = new List<(string Name, string Value)>();
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var shim in PreprocessorShims)
        {
            if (defined.Contains(shim.Name) || !emitted.Add(shim.Name))
                continue;
            shims.Add(shim);
        }

        foreach (string symbol in used)
        {
            if (defined.Contains(symbol) || !emitted.Add(symbol))
                continue;
            shims.Add((symbol, "0"));
        }

        if (shims.Count == 0)
            return new List<string>();

        warnings.Add(
            "Unity platform/variant preprocessor symbols were flattened to fixed #define shims (" +
            string.Join(", ", shims.Select(s => $"{s.Name}={s.Value}")) +
            "); only one variant is represented. Review #if/#ifdef blocks manually.");

        var lines = new List<string>
        {
            "// ---------------------------------------------------------------------------",
            "// Unity preprocessor shims - platform variants are flattened to these values.",
            "// ---------------------------------------------------------------------------"
        };
        foreach (var shim in shims)
            lines.Add($"#define {shim.Name} {shim.Value}");
        return lines;
    }

    private static void CollectPropertyWarnings(
        IReadOnlyList<ShaderProperty> properties,
        List<string> warnings)
    {
        foreach (ShaderProperty property in properties)
        {
            if (property.Attributes is not null)
            {
                warnings.Add(
                    $"ShaderLab attributes '{property.Attributes}' on property '{property.Name}' are metadata only; there is no Slang equivalent.");
            }

            if (property.Type == "Range")
            {
                warnings.Add(
                    $"Unity Range property '{property.Name}': the original min/max limits are not represented in the emitted float; clamp manually if needed.");
            }

            if (property.Type is "2D" or "Cube" or "3D" && property.DefaultValue is not null)
            {
                warnings.Add(
                    $"Texture property '{property.Name}' has Unity default '{property.DefaultValue}'; bind a real texture (and its sampler) in the target engine.");
            }
        }
    }

    private static void AppendPropertyBlock(
        StringBuilder sb,
        IReadOnlyList<ShaderProperty> properties)
    {
        AppendLine(sb, "// ---------------------------------------------------------------------------");
        AppendLine(sb, "// Unity ShaderLab properties - bind every one of these BY NAME.");
        AppendLine(sb, "// Texture properties also need a SamplerState named '<Property>Sampler'.");
        AppendLine(sb, "// ---------------------------------------------------------------------------");

        if (properties.Count == 0)
        {
            AppendLine(sb, "//   (none)");
            AppendLine(sb, "");
            return;
        }

        foreach (ShaderProperty property in properties)
        {
            string attributes = property.Attributes is null ? "" : property.Attributes + " ";
            AppendLine(sb, $"//   {property.Type,-8} {attributes}{property.Name}   default = {property.DefaultValue ?? "(none)"}");
        }

        AppendLine(sb, "// ---------------------------------------------------------------------------");

        foreach (ShaderProperty property in properties)
        {
            // Every property is emitted exactly once here. Duplicate HLSL
            // declarations of these names are commented out by
            // ShaderHlslTranslator while the code blocks are copied.
            string[] declarations = DeclareProperty(property).Split('\n');
            for (int i = 0; i < declarations.Length; i++)
            {
                string comment = i == declarations.Length - 1
                    ? $"   // Unity {property.Type} property; default = {property.DefaultValue ?? "unset"}"
                    : "";
                AppendLine(sb, declarations[i] + comment);
            }
        }

        AppendLine(sb, "");
    }

    private static string DeclareProperty(ShaderProperty property) => property.Type switch
    {
        "Float" => $"uniform float {property.Name};",
        "Int" => $"uniform int {property.Name};",
        "Range" => $"uniform float {property.Name};",
        "Color" => $"uniform float4 {property.Name};",
        "Vector" => $"uniform float4 {property.Name};",
        "2D" => $"uniform Texture2D {property.Name};\nuniform SamplerState {property.Name}Sampler;",
        "Cube" => $"uniform TextureCube {property.Name};\nuniform SamplerState {property.Name}Sampler;",
        "3D" => $"uniform Texture3D {property.Name};\nuniform SamplerState {property.Name}Sampler;",
        _ => $"uniform float {property.Name};"
    };

    private static void AppendGeneratedDeclarations(
        StringBuilder sb,
        List<(ParsedCodeBlock Block, HlslTranslation Translation)> sections)
    {
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var any = false;

        foreach (var section in sections)
        {
            foreach (string declaration in section.Translation.GeneratedDeclarations)
            {
                if (!emitted.Add(declaration))
                    continue;

                if (!any)
                {
                    AppendLine(sb, "// Generated helpers and Unity built-in substitutes (bind by name):");
                    any = true;
                }

                foreach (string line in declaration.Split('\n'))
                    AppendLine(sb, line);
            }
        }

        if (any)
            AppendLine(sb, "");
    }

    private static void AppendCode(
        StringBuilder sb,
        List<(ParsedCodeBlock Block, HlslTranslation Translation)> sections)
    {
        if (sections.Count == 0)
        {
            AppendLine(sb, "// No CGPROGRAM / CGINCLUDE / HLSLPROGRAM / HLSLINCLUDE blocks were found.");
            AppendLine(sb, "");
            return;
        }

        foreach (var section in sections)
        {
            AppendLine(sb, $"// ==== {section.Block.Kind} ({section.Block.Location}) ====");
            foreach (string line in section.Translation.Source.Split('\n'))
                AppendLine(sb, line);
            AppendLine(sb, "");
        }
    }

    private static void AppendOriginalSource(StringBuilder sb, string text, string header)
    {
        AppendLine(sb, "// ==== " + header + " ====");
        foreach (string line in text.Split('\n'))
            AppendLine(sb, "// " + line);
    }

    private static void AppendLine(StringBuilder sb, string line) => sb.Append(line).Append('\n');
}
