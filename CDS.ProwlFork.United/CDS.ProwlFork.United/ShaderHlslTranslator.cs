#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace CDS.ProwlFork.United;

/// <summary>Result of translating one CG/HLSL code block to Slang.</summary>
internal sealed class HlslTranslation
{
    /// <summary>Translated Slang source (LF line endings).</summary>
    public string Source { get; init; } = "";

    /// <summary>Constructs that need manual attention.</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>
    /// Extra globals that the translation needs (Unity built-ins, generated
    /// samplers, explicit appdata structs). Each entry may span several lines.
    /// </summary>
    public List<string> GeneratedDeclarations { get; } = new();

    /// <summary>Identifiers explicitly declared in the translated code.</summary>
    public HashSet<string> DeclaredNames { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Pragmatic, engine-agnostic HLSL/Cg to Slang translation. The goal is to
/// produce a useful starting point, not a guaranteed-compilable module:
/// every construct that cannot be mapped safely is preserved and reported in
/// <see cref="HlslTranslation.Warnings"/> instead of being silently dropped.
/// </summary>
internal static class ShaderHlslTranslator
{
    private static readonly Regex FunctionDefinition = new(
        @"^\s*(?<prefix>(?:(?:[A-Za-z_]\w*|static|inline|uniform)\s+)+)(?<name>[A-Za-z_]\w*)\s*\((?<params>[^;{}]*)\)\s*(?::\s*[A-Za-z_]\w*)?\s*(?:\{.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SamplerDeclaration = new(
        @"^(?:uniform\s+)?(?<type>sampler2D|samplerCUBE|sampler3D)\s+(?<name>[A-Za-z_]\w*)\s*(?::\s*register\s*\([^)]*\))?\s*;$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DeclaredName = new(
        @"\b(?:float|float2|float3|float4|int|int2|int3|int4|half|half2|half3|half4|bool|Texture2D|TextureCube|Texture3D)\s+(_\w+)\s*(?:[;=])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AppDataReference = new(
        @"\bappdata_[A-Za-z0-9_]*\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UnityIdentifier = new(
        @"\b(?:UNITY_[A-Z0-9_]+|unity_[A-Za-z0-9_]+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FixedType = new(
        @"\bfixed([234]?)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IdentifierAtStart = new(
        @"^[A-Za-z_]\w*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Matches a (possibly comma-separated) variable declaration such as
    /// <c>float4 _SunDiskColor, _SunCoreColor, _SunHaloColor;</c>.
    /// </summary>
    private static readonly Regex VariableDeclaration = new(
        @"^(?<indent>\s*)(?<prefix>(?:(?:uniform|static|const|extern|shared|groupshared)\s+)*)(?<type>(?:float|int|uint|half|double|bool|fixed)\d?|Texture2D|TextureCube|Texture3D|SamplerState)\b\s+(?<declarators>[^;{}]+);(?<trailing>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Unity-only macros that are not valid Slang and must not be copied into
    /// the generated module. Their equivalents are emitted as uniform globals
    /// by <see cref="UnityShaderTranspiler"/>.
    /// </summary>
    private static readonly string[] UnityMacroPrefixes =
    {
        "CBUFFER_START(", "CBUFFER_END", "TEXTURE2D(", "TEXTURECUBE(",
        "TEXTURE3D(", "TEXTURE2D_ARRAY(", "SAMPLER(", "SAMPLER_CMP("
    };

    private static readonly Dictionary<string, string> BuiltinTypes = new(StringComparer.Ordinal)
    {
        ["UNITY_MATRIX_MVP"] = "float4x4",
        ["UNITY_MATRIX_MV"] = "float4x4",
        ["UNITY_MATRIX_V"] = "float4x4",
        ["UNITY_MATRIX_P"] = "float4x4",
        ["UNITY_MATRIX_VP"] = "float4x4",
        ["UNITY_MATRIX_I_M"] = "float4x4",
        ["UNITY_MATRIX_IT_MV"] = "float4x4",
        ["unity_ObjectToWorld"] = "float4x4",
        ["unity_WorldToObject"] = "float4x4",
        ["_WorldSpaceCameraPos"] = "float3",
        ["_Time"] = "float4",
        ["_SinTime"] = "float4",
        ["_CosTime"] = "float4",
        ["unity_DeltaTime"] = "float4",
        ["_ProjectionParams"] = "float4",
        ["_ScreenParams"] = "float4"
    };

    private static readonly HashSet<string> HandledMacros = new(StringComparer.Ordinal)
    {
        "TRANSFORM_TEX",
        "UNITY_INITIALIZE_OUTPUT"
    };

    private static readonly string[] ManualIntrinsics =
    {
        "tex2Dlod", "tex2Dgrad", "tex2Dbias", "tex2Dproj",
        "tex3Dlod", "texCUBElod", "texCUBEproj", "texCUBEbias"
    };

    private static readonly Dictionary<string, string> AppDataStructs = new(StringComparer.Ordinal)
    {
        ["appdata_base"] =
            "float4 vertex : POSITION;\nfloat3 normal : NORMAL;\nfloat4 texcoord : TEXCOORD0;",
        ["appdata_normal"] =
            "float4 vertex : POSITION;\nfloat3 normal : NORMAL;\nfloat4 texcoord : TEXCOORD0;",
        ["appdata_tan"] =
            "float4 vertex : POSITION;\nfloat3 normal : NORMAL;\nfloat4 tangent : TANGENT;\nfloat4 texcoord : TEXCOORD0;",
        ["appdata_full"] =
            "float4 vertex : POSITION;\nfloat3 normal : NORMAL;\nfloat4 tangent : TANGENT;\nfloat4 texcoord : TEXCOORD0;\nfloat4 texcoord1 : TEXCOORD1;\nfloat4 color : COLOR;",
        ["appdata_img"] =
            "float4 vertex : POSITION;\nfloat4 texcoord : TEXCOORD0;"
    };

    /// <summary>Translates a single CG/HLSL block.</summary>
    /// <param name="code">Raw block contents.</param>
    /// <param name="properties">ShaderLab properties, used for sampler naming.</param>
    /// <param name="name">Shader name, used in diagnostics only.</param>
    public static HlslTranslation Translate(string code, IReadOnlyList<ShaderProperty> properties, string? name)
    {
        _ = name;
        string normalized = code.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = normalized.Split('\n');

        var output = new List<string>();
        var warnings = new List<string>();
        var generated = new List<string>();
        var generatedSet = new HashSet<string>(StringComparer.Ordinal);
        var warnedOnce = new HashSet<string>(StringComparer.Ordinal);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var samplersEmitted = new HashSet<string>(StringComparer.Ordinal);
        var sampledTextures = new List<string>();
        var builtins = new HashSet<string>(StringComparer.Ordinal);
        var usedAppData = new List<string>();
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);

        // Names for which UnityShaderTranspiler emits a global (uniform) in
        // the generated Slang. Any HLSL declaration of one of these names is
        // a duplicate and must be removed from the copied source.
        var emittedGlobals = new HashSet<string>(StringComparer.Ordinal);
        foreach (ShaderProperty property in properties)
        {
            emittedGlobals.Add(property.Name);
            if (property.Type is "2D" or "Cube" or "3D")
            {
                samplersEmitted.Add(property.Name + "Sampler");
                emittedGlobals.Add(property.Name + "Sampler");
            }
        }

        void WarnOnce(string key, string message)
        {
            if (warnedOnce.Add(key))
                warnings.Add(message);
        }

        void Generate(string declaration)
        {
            if (generatedSet.Add(declaration))
                generated.Add(declaration);
        }

        void AddBuiltin(string identifier, string type)
        {
            if (!builtins.Add(identifier))
                return;

            Generate($"uniform {type} {identifier};");
            warnings.Add(
                $"Unity built-in '{identifier}' was emitted as a uniform; bind it by name in the target engine.");
        }

        string Rewrite(string line)
        {
            string result = line;

            if (FixedType.IsMatch(result))
            {
                WarnOnce("fixed", "Cg 'fixed' precision types were mapped to 'float'; verify precision requirements.");
                result = FixedType.Replace(result, "float$1");
            }

            result = RewriteCalls(result, "tex2D", args =>
            {
                if (args.Length != 2)
                {
                    WarnOnce("tex2D-arity", "A tex2D call has an unexpected argument count; it was left unchanged.");
                    return null;
                }

                string texture = args[0].Trim();
                string uv = args[1].Trim();
                if (!sampledTextures.Contains(texture))
                    sampledTextures.Add(texture);
                return $"{texture}.Sample({texture}Sampler, {uv})";
            });

            result = RewriteCalls(result, "texCUBE", args =>
            {
                if (args.Length != 2)
                {
                    WarnOnce("texCUBE-arity", "A texCUBE call has an unexpected argument count; it was left unchanged.");
                    return null;
                }

                string texture = args[0].Trim();
                string direction = args[1].Trim();
                if (!sampledTextures.Contains(texture))
                    sampledTextures.Add(texture);
                return $"{texture}.Sample({texture}Sampler, {direction})";
            });

            result = RewriteCalls(result, "UnityObjectToClipPos", args =>
            {
                if (args.Length != 1) return null;
                AddBuiltin("UNITY_MATRIX_MVP", "float4x4");
                return $"mul(UNITY_MATRIX_MVP, {args[0].Trim()})";
            });

            result = RewriteCalls(result, "UnityWorldToClipPos", args =>
            {
                if (args.Length != 1) return null;
                AddBuiltin("UNITY_MATRIX_VP", "float4x4");
                return $"mul(UNITY_MATRIX_VP, {args[0].Trim()})";
            });

            result = RewriteCalls(result, "UnityObjectToWorldPos", args =>
            {
                if (args.Length != 1) return null;
                AddBuiltin("UNITY_MATRIX_M", "float4x4");
                return $"mul(UNITY_MATRIX_M, {args[0].Trim()})";
            });

            result = RewriteCalls(result, "UnityObjectToWorldNormal", args =>
            {
                if (args.Length != 1) return null;
                AddBuiltin("UNITY_MATRIX_M", "float4x4");
                WarnOnce(
                    "UnityObjectToWorldNormal",
                    "UnityObjectToWorldNormal was approximated with UNITY_MATRIX_M; it is only exact for uniform scaling.");
                return $"normalize(mul((float3x3)UNITY_MATRIX_M, {args[0].Trim()}))";
            });

            result = RewriteCalls(result, "TRANSFORM_TEX", args =>
            {
                if (args.Length != 2) return null;
                string texture = args[1].Trim();
                AddBuiltin(texture + "_ST", "float4");
                WarnOnce(
                    "TRANSFORM_TEX",
                    "TRANSFORM_TEX was expanded to a '_ST' scale/offset multiply; bind each '<Texture>_ST' uniform by name.");
                return $"({args[0].Trim()} * {texture}_ST.xy + {texture}_ST.zw)";
            });

            result = RewriteCalls(result, "UNITY_INITIALIZE_OUTPUT", args =>
            {
                if (args.Length != 2) return null;
                string type = args[0].Trim();
                string variable = args[1].Trim();
                WarnOnce(
                    "UNITY_INITIALIZE_OUTPUT",
                    "UNITY_INITIALIZE_OUTPUT was expanded to a zero-initialisation; verify the target type is zero-initialisable.");
                return $"{type} {variable} = ({type})0";
            });

            foreach (string intrinsic in ManualIntrinsics)
            {
                if (!Regex.IsMatch(result, @"\b" + intrinsic + @"\s*\("))
                    continue;

                WarnOnce(
                    "intrinsic:" + intrinsic,
                    $"HLSL intrinsic '{intrinsic}(...)' needs manual translation to a Slang Sample/SampleLevel/SampleBias call; the original call is preserved.");
                result += "  // TODO(Slang): " + intrinsic + " requires manual translation";
            }

            foreach (Match match in AppDataReference.Matches(result))
            {
                string structName = match.Value;
                if (AppDataStructs.ContainsKey(structName) && !usedAppData.Contains(structName))
                    usedAppData.Add(structName);
            }

            foreach (Match match in UnityIdentifier.Matches(result))
            {
                string identifier = match.Value;
                if (BuiltinTypes.TryGetValue(identifier, out string? type))
                {
                    AddBuiltin(identifier, type);
                }
                else if (!HandledMacros.Contains(identifier))
                {
                    WarnOnce(
                        "macro:" + identifier,
                        $"Unity macro '{identifier}' cannot be translated safely; manual work is required (the original text is preserved).");
                }
            }

            return result;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                output.Add("");
                continue;
            }

            if (trimmed.StartsWith("#pragma", StringComparison.Ordinal))
            {
                HandlePragma(trimmed, output, warnings, entries);
                continue;
            }

            if (trimmed.StartsWith("#include", StringComparison.Ordinal))
            {
                HandleInclude(trimmed, output, warnings);
                continue;
            }

            if (trimmed.StartsWith("#define", StringComparison.Ordinal) ||
                trimmed.StartsWith("#if", StringComparison.Ordinal) ||
                trimmed.StartsWith("#else", StringComparison.Ordinal) ||
                trimmed.StartsWith("#elif", StringComparison.Ordinal) ||
                trimmed.StartsWith("#endif", StringComparison.Ordinal) ||
                trimmed.StartsWith("#undef", StringComparison.Ordinal) ||
                trimmed.StartsWith("#line", StringComparison.Ordinal))
            {
                // Slang keeps the standard preprocessor; these directives are
                // valid in both languages.
                output.Add(line);
                continue;
            }

            string? unityMacro = FindUnityMacro(trimmed);
            if (unityMacro is not null)
            {
                WarnOnce(
                    "unity-macro:" + unityMacro,
                    $"Unity macro '{unityMacro}' has no Slang equivalent; the line was commented out (its uniforms and textures are emitted separately).");
                output.Add("// [untranslated] " + line);
                continue;
            }

            if (TryStripDuplicateDeclarations(line, emittedGlobals, out string deduplicated, out List<string> removed))
            {
                string action = deduplicated.StartsWith("// [deduplicated]", StringComparison.Ordinal)
                    ? "was commented out"
                    : "was rewritten";
                warnings.Add(
                    $"HLSL declaration '{line.Trim()}' {action} because {string.Join(", ", removed)} " +
                    "are already emitted as generated ShaderLab uniform globals; the generated 'uniform' declarations are the single binding point.");
                output.Add(deduplicated);
                continue;
            }

            Match sampler = SamplerDeclaration.Match(trimmed);
            if (sampler.Success)
            {
                string type = sampler.Groups["type"].Value;
                string samplerName = sampler.Groups["name"].Value;
                string slangType = type switch
                {
                    "sampler2D" => "Texture2D",
                    "samplerCUBE" => "TextureCube",
                    _ => "Texture3D"
                };

                declared.Add(samplerName);
                declared.Add(samplerName + "Sampler");
                samplersEmitted.Add(samplerName + "Sampler");

                output.Add($"{slangType} {samplerName};");
                output.Add($"SamplerState {samplerName}Sampler;");
                output.Add($"// [translated] {type} {samplerName} -> {slangType} + SamplerState; bind both by name.");
                warnings.Add(
                    $"Sampler '{samplerName}' was split into {slangType} '{samplerName}' and SamplerState '{samplerName}Sampler'; bind both by name.");
                continue;
            }

            foreach (Match declaration in DeclaredName.Matches(line))
                declared.Add(declaration.Groups[1].Value);

            Match function = FunctionDefinition.Match(line);
            if (function.Success &&
                entries.TryGetValue(function.Groups["name"].Value, out string? shaderStage))
            {
                output.Add($"[shader(\"{shaderStage}\")]");
            }

            output.Add(Rewrite(line));
        }

        foreach (string texture in sampledTextures)
        {
            string samplerName = texture + "Sampler";
            if (samplersEmitted.Contains(samplerName))
                continue;

            samplersEmitted.Add(samplerName);
            Generate($"uniform SamplerState {samplerName};");
            WarnOnce(
                "sampler:" + samplerName,
                $"Generated SamplerState '{samplerName}' for texture '{texture}'; bind it by name in the target engine.");
        }

        foreach (string structName in usedAppData)
        {
            if (Regex.IsMatch(normalized, @"\bstruct\s+" + structName + @"\b"))
                continue;

            Generate(
                $"// Explicit vertex input struct replacing UnityCG.cginc's {structName}.\n" +
                $"struct {structName}\n{{\n{AppDataStructs[structName]}\n}};");
            WarnOnce(
                "appdata:" + structName,
                $"Generated an explicit vertex input struct for UnityCG type '{structName}'; verify its fields and semantics.");
        }

        foreach (var entry in entries)
        {
            if (entry.Value == "compute" &&
                !Regex.IsMatch(normalized, @"\[\s*numthreads\s*\("))
            {
                WarnOnce(
                    "numthreads:" + entry.Key,
                    $"Compute kernel '{entry.Key}' has no [numthreads(...)] attribute; Slang requires one on the entry point.");
            }
        }

        var translation = new HlslTranslation
        {
            Source = string.Join("\n", output).TrimEnd('\n', ' ', '\t')
        };
        translation.GeneratedDeclarations.AddRange(generated);
        translation.DeclaredNames.UnionWith(declared);
        translation.Warnings.AddRange(warnings);
        return translation;
    }

    /// <summary>Returns the macro name when a line starts with a Unity-only macro.</summary>
    private static string? FindUnityMacro(string trimmed)
    {
        foreach (string prefix in UnityMacroPrefixes)
        {
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            return prefix.EndsWith("(", StringComparison.Ordinal)
                ? prefix.Substring(0, prefix.Length - 1)
                : prefix;
        }

        return null;
    }

    /// <summary>
    /// Removes every declarator that is already emitted as a Slang uniform
    /// global. Handles comma-separated declarations; a line whose declarators
    /// were all duplicates is turned into a comment.
    /// </summary>
    private static bool TryStripDuplicateDeclarations(
        string line,
        HashSet<string> emittedGlobals,
        out string result,
        out List<string> removed)
    {
        result = line;
        removed = new List<string>();

        Match declaration = VariableDeclaration.Match(line);
        if (!declaration.Success)
            return false;

        var kept = new List<string>();
        foreach (string part in SplitArguments(declaration.Groups["declarators"].Value))
        {
            string declarator = part.Trim();
            Match identifier = IdentifierAtStart.Match(declarator);
            if (identifier.Success && emittedGlobals.Contains(identifier.Value))
                removed.Add(identifier.Value);
            else
                kept.Add(declarator);
        }

        if (removed.Count == 0)
            return false;

        if (kept.Count == 0)
        {
            result = "// [deduplicated] " + line;
        }
        else
        {
            result = declaration.Groups["indent"].Value +
                     declaration.Groups["prefix"].Value +
                     declaration.Groups["type"].Value + " " +
                     string.Join(", ", kept) + ";" +
                     declaration.Groups["trailing"].Value +
                     "  // [deduplicated] " + string.Join(", ", removed);
        }

        return true;
    }

    private static void HandlePragma(
        string trimmed,
        List<string> output,
        List<string> warnings,
        Dictionary<string, string> entries)
    {
        string[] parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string directive = parts.Length >= 2 ? parts[1] : "";

        if (directive is "vertex" or "fragment" or "kernel")
        {
            if (parts.Length >= 3)
            {
                string stage = directive == "kernel" ? "compute" : directive;
                entries[parts[2]] = stage;
                output.Add($"// {trimmed}  ->  [shader(\"{stage}\")] on {parts[2]}");
            }
            else
            {
                warnings.Add($"HLSL: malformed '#pragma {directive}' directive; no entry function was identified.");
                output.Add("// [untranslated] " + trimmed);
            }
            return;
        }

        string message = directive switch
        {
            "target" => $"HLSL '#pragma target {string.Join(" ", parts, 2, Math.Max(0, parts.Length - 2))}' has no Slang equivalent; choose a compilation target/profile at build time.",
            "multi_compile" or "multi_compile_local" or "shader_feature" or "shader_feature_local" =>
                "Unity shader variant pragmas are not supported by Slang; variant selection must be implemented manually.",
            "only_renderers" or "exclude_renderers" or "hardware_tier_variants" =>
                "Unity renderer/tier pragmas are not supported by Slang; restrict targets in the build pipeline instead.",
            "instancing_options" or "skip_variants" or "fragmentoption" =>
                $"Unity-specific '#pragma {directive}' has no Slang equivalent; manual work is required.",
            _ => $"HLSL directive '{trimmed}' was not translated; manual work may be required."
        };
        warnings.Add(message);
        output.Add("// [untranslated] " + trimmed);
    }

    private static void HandleInclude(string trimmed, List<string> output, List<string> warnings)
    {
        string fileName = "";
        int firstQuote = trimmed.IndexOf('"');
        if (firstQuote >= 0)
        {
            int secondQuote = trimmed.IndexOf('"', firstQuote + 1);
            if (secondQuote > firstQuote)
                fileName = trimmed.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
        }
        if (fileName.Length == 0)
        {
            int firstAngle = trimmed.IndexOf('<');
            int lastAngle = trimmed.LastIndexOf('>');
            if (firstAngle >= 0 && lastAngle > firstAngle)
                fileName = trimmed.Substring(firstAngle + 1, lastAngle - firstAngle - 1);
        }
        if (fileName.Length == 0)
            fileName = trimmed.Substring("#include".Length).Trim();

        bool unitySpecific =
            fileName.Contains("Unity", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("HLSLSupport", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Lighting", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("AutoLight", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".cginc", StringComparison.OrdinalIgnoreCase);

        string message = unitySpecific
            ? $"Unity-specific include '{fileName}' cannot be translated; replace it with equivalent Slang code or your engine's shader library."
            : $"#include '{fileName}' cannot be resolved by the transpiler; include the equivalent Slang module manually.";
        warnings.Add(message);
        output.Add("// [untranslated] " + trimmed);
    }

    /// <summary>Replaces every call to <paramref name="name"/> using <paramref name="rewrite"/>.</summary>
    private static string RewriteCalls(string line, string name, Func<string[], string?> rewrite)
    {
        var result = new StringBuilder(line.Length + 32);
        int position = 0;

        while (position < line.Length)
        {
            int index = FindIdentifier(line, name, position);
            if (index < 0)
            {
                result.Append(line, position, line.Length - position);
                break;
            }

            int afterName = index + name.Length;
            int open = afterName;
            while (open < line.Length && char.IsWhiteSpace(line[open]))
                open++;

            if (open >= line.Length || line[open] != '(')
            {
                result.Append(line, position, afterName - position);
                position = afterName;
                continue;
            }

            int close = FindClosingParen(line, open);
            if (close < 0)
            {
                result.Append(line, position, afterName - position);
                position = afterName;
                continue;
            }

            string[] arguments = SplitArguments(line.Substring(open + 1, close - open - 1));
            string? replacement = rewrite(arguments);

            result.Append(line, position, index - position);
            result.Append(replacement ?? line.Substring(index, close - index + 1));
            position = close + 1;
        }

        return result.ToString();
    }

    private static int FindIdentifier(string text, string name, int start)
    {
        for (int i = text.IndexOf(name, start, StringComparison.Ordinal);
             i >= 0;
             i = text.IndexOf(name, i + 1, StringComparison.Ordinal))
        {
            if (i > 0 && IsWordChar(text[i - 1]))
                continue;
            int after = i + name.Length;
            if (after < text.Length && IsWordChar(text[after]))
                continue;
            return i;
        }

        return -1;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int FindClosingParen(string text, int open)
    {
        int depth = 0;
        char quote = '\0';
        bool escaped = false;

        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (escaped) escaped = false;
                else if (c == '\\' && quote == '"') escaped = true;
                else if (c == quote) quote = '\0';
                continue;
            }

            if (c == '"' || c == '\'')
            {
                quote = c;
                continue;
            }

            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }

        return -1;
    }

    private static string[] SplitArguments(string text)
    {
        if (text.Trim().Length == 0)
            return Array.Empty<string>();

        var arguments = new List<string>();
        int depth = 0;
        int start = 0;
        char quote = '\0';
        bool escaped = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (escaped) escaped = false;
                else if (c == '\\' && quote == '"') escaped = true;
                else if (c == quote) quote = '\0';
                continue;
            }

            if (c == '"' || c == '\'') { quote = c; continue; }
            if (c == '(' || c == '[' || c == '{') depth++;
            else if (c == ')' || c == ']' || c == '}') depth--;
            else if (c == ',' && depth == 0)
            {
                arguments.Add(text.Substring(start, i - start).Trim());
                start = i + 1;
            }
        }

        arguments.Add(text.Substring(start).Trim());
        return arguments.ToArray();
    }
}
