using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CompilePalX.Preview
{
    /// <summary>
    /// What the preview needs to know about a material: which shader, which textures, how it blends.
    /// </summary>
    public sealed record Material
    {
        public string Name { get; init; } = "";
        public string Shader { get; init; } = "";
        public string? BaseTexture { get; init; }
        /// <summary>The second texture of a WorldVertexTransition, blended by displacement alpha.</summary>
        public string? BaseTexture2 { get; init; }
        public bool Translucent { get; init; }
        public bool AlphaTest { get; init; }
        public bool NoCull { get; init; }
        /// <summary>UnlitGeneric and friends: drawn at full brightness, no lightmap.</summary>
        public bool Unlit { get; init; }
        /// <summary>$color, a linear tint. White when unset.</summary>
        public float[] Color { get; init; } = [1, 1, 1];
        /// <summary>The material is a compile-time tool or a decal the preview has no way to draw.</summary>
        public bool Hidden { get; init; }

        /// <summary>The Water shader, drawn as a tinted translucent sheet since refraction is out of reach.</summary>
        public bool Water { get; init; }

        /// <summary>The water's colour, from $fogcolor or $refracttint, linear.</summary>
        public float[] WaterColor { get; init; } = [0.3f, 0.5f, 0.6f];

        /// <summary>DecalModulate: multiplied onto what is under it rather than drawn over it.</summary>
        public bool Modulate { get; init; }
    }

    /// <summary>
    /// Reads Valve Material files.
    ///
    /// A VMT is a KeyValues block named after its shader, whose keys start with a dollar sign. The
    /// syntax is loose enough - unquoted values, DirectX-level conditionals in square brackets,
    /// comments, occasional missing braces - that a strict KeyValues parser refuses a fair number of
    /// real files, so this reads what it needs with a tolerant scanner rather than a full parser.
    ///
    /// <c>patch</c> materials include another file and override some of its keys; those are
    /// followed through <paramref name="load"/>, which is how every other lookup is done too.
    /// </summary>
    public static class Vmt
    {
        private static readonly Regex KeyValuePattern = new(
            @"^\s*""?(?<key>[%$\w]+)""?\s+(?:""(?<quoted>[^""]*)""|(?<bare>[^\s""{}]+))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ShaderPattern = new(
            @"^\s*""?(?<shader>[A-Za-z_][\w]*)""?\s*$",
            RegexOptions.Compiled);

        /// <summary>
        /// Parses <paramref name="text"/>. <paramref name="load"/> resolves an included file by
        /// its relative path (<c>materials/…</c>) for patch materials; may return null.
        /// </summary>
        public static Material Parse(string name, string text, Func<string, string?> load, int depth = 0)
        {
            var (shader, keys) = Scan(text);

            if (string.Equals(shader, "patch", StringComparison.OrdinalIgnoreCase) && depth < 4)
            {
                // "include" names the base; anything else set here overrides it
                if (keys.TryGetValue("include", out var include))
                {
                    string included = load(include.Replace('\\', '/')) ?? "";
                    var baseMaterial = Parse(name, included, load, depth + 1);
                    return Apply(baseMaterial, keys);
                }
            }

            return Build(name, shader, keys);
        }

        /// <summary>The shader name and the dollar keys, first occurrence wins.</summary>
        internal static (string Shader, Dictionary<string, string> Keys) Scan(string text)
        {
            var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var tokens = Tokenise(text);

            string shader = "";
            int depth = 0;
            int i = 0;

            // the first word is the shader; braces may come on the same line or the next
            while (i < tokens.Count && tokens[i] is "{" or "}")
                i++;
            if (i < tokens.Count)
                shader = tokens[i++].Trim('"');

            while (i < tokens.Count)
            {
                string token = tokens[i++];

                if (token == "{") { depth++; continue; }
                if (token == "}") { depth--; continue; }

                // a conditional like [$dx90] or [!$hdr] tags the value before it; not a key
                if (token.StartsWith('['))
                    continue;

                string key = token.Trim('"').ToLowerInvariant();

                // the value is the next token unless a block opens instead
                if (i >= tokens.Count || tokens[i] == "{")
                    continue;

                string value = tokens[i++].Trim('"');
                if (value == "}")
                {
                    depth--;
                    continue;
                }

                // keys inside nested blocks (Proxies, >=DX90 …) are taken too, but the shallower
                // value keeps precedence, being read first
                if (depth <= 2 && !keys.ContainsKey(key))
                    keys[key] = value;
            }

            return (shader, keys);
        }

        /// <summary>
        /// Splits KeyValues text into quoted strings, bare words and braces, dropping comments.
        /// Quotes are kept on quoted tokens so a quoted brace is not mistaken for a block.
        /// </summary>
        internal static List<string> Tokenise(string text)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            int i = 0;

            void Flush()
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }

            while (i < text.Length)
            {
                char c = text[i];

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    Flush();
                    while (i < text.Length && text[i] != '\n')
                        i++;
                    continue;
                }

                if (c == '"')
                {
                    Flush();
                    int end = text.IndexOf('"', i + 1);
                    if (end < 0)
                        end = text.Length;
                    tokens.Add(text.Substring(i, Math.Min(end + 1, text.Length) - i));
                    i = end + 1;
                    continue;
                }

                if (c is '{' or '}')
                {
                    Flush();
                    tokens.Add(c.ToString());
                    i++;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    Flush();
                    i++;
                    continue;
                }

                current.Append(c);
                i++;
            }

            Flush();
            return tokens;
        }

        private static Material Build(string name, string shader, Dictionary<string, string> keys)
        {
            string lowerShader = shader.ToLowerInvariant();
            string? baseTexture = keys.TryGetValue("$basetexture", out var bt) ? bt : null;
            string? baseTexture2 = keys.TryGetValue("$basetexture2", out var bt2) ? bt2 : null;

            // sky materials in HDR ship only the compressed HDR texture in some games
            if (baseTexture is null && keys.TryGetValue("$hdrbasetexture", out var hdr))
                baseTexture = hdr;

            bool modulate = lowerShader is "decalmodulate" or "modulate";
            bool hidden = lowerShader is "sprite" or "spritecard" or "cable" or "splinerope"
                          || name.StartsWith("tools/", StringComparison.OrdinalIgnoreCase) && !name.Contains("black", StringComparison.OrdinalIgnoreCase)
                          || Flag(keys, "%compilenodraw") || Flag(keys, "%compilesky") || Flag(keys, "%compile2dsky")
                          || Flag(keys, "%compiletrigger") || Flag(keys, "%compilehint") || Flag(keys, "%compileskip")
                          || Flag(keys, "%compileclip") || Flag(keys, "%compileplayerclip") || Flag(keys, "%compilenpcclip");

            return new Material
            {
                Name = name,
                Shader = shader,
                BaseTexture = baseTexture,
                BaseTexture2 = baseTexture2,
                Translucent = Flag(keys, "$translucent") || lowerShader is "water" or "refract" or "unlittwotexture",
                AlphaTest = Flag(keys, "$alphatest"),
                NoCull = Flag(keys, "$nocull"),
                Unlit = lowerShader is "unlitgeneric" or "unlittwotexture" or "sky" or "monitorscreen",
                // $color2 is a model-shader tint; LightmappedGeneric and friends ignore it
                Color = ParseColor(keys.TryGetValue("$color", out var c) ? c : lowerShader is "vertexlitgeneric" && keys.TryGetValue("$color2", out var c2) ? c2 : null),
                Hidden = hidden,
                Water = lowerShader is "water",
                WaterColor = ParseColor(keys.TryGetValue("$fogcolor", out var fog) ? fog : keys.TryGetValue("$refracttint", out var tint) ? tint : "{77 128 153}"),
                Modulate = modulate,
            };
        }

        private static Material Apply(Material baseMaterial, Dictionary<string, string> overrides)
        {
            // a patch's own keys are in the same dictionary as "include"; merge them over the base
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["$basetexture"] = baseMaterial.BaseTexture ?? "",
                ["$basetexture2"] = baseMaterial.BaseTexture2 ?? "",
            };
            if (baseMaterial.Translucent) merged["$translucent"] = "1";
            if (baseMaterial.AlphaTest) merged["$alphatest"] = "1";
            if (baseMaterial.NoCull) merged["$nocull"] = "1";
            // formatted invariantly: these strings go back through ParseColor, which reads "." decimals
            merged["$color"] = FormattableString.Invariant($"{{{baseMaterial.Color[0] * 255} {baseMaterial.Color[1] * 255} {baseMaterial.Color[2] * 255}}}");

            foreach (var (key, value) in overrides)
                if (key != "include")
                    merged[key] = value;

            if (merged["$basetexture"].Length == 0) merged.Remove("$basetexture");
            if (merged["$basetexture2"].Length == 0) merged.Remove("$basetexture2");

            if (baseMaterial.Water) merged.TryAdd("$fogcolor", FormattableString.Invariant($"[{baseMaterial.WaterColor[0]} {baseMaterial.WaterColor[1]} {baseMaterial.WaterColor[2]}]"));
            var patched = Build(baseMaterial.Name, baseMaterial.Shader, merged);
            return baseMaterial.Hidden ? patched with { Hidden = true } : patched;
        }

        private static bool Flag(Dictionary<string, string> keys, string key) =>
            keys.TryGetValue(key, out var v) && v.Trim() is not ("0" or "" or "0.0");

        /// <summary>$color as "{255 128 0}", "[1 .5 0]" or a single number; white when absent or odd.</summary>
        public static float[] ParseColor(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return [1, 1, 1];

            string trimmed = value.Trim();
            bool bytes = trimmed.StartsWith('{');
            var parts = trimmed.Trim('{', '}', '[', ']').Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var result = new float[3];
            for (int i = 0; i < 3; i++)
            {
                string part = parts.Length > i ? parts[i] : parts.Length > 0 ? parts[0] : "1";
                if (!float.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f))
                    return [1, 1, 1];
                result[i] = bytes ? f / 255f : f;
            }

            return result;
        }
    }

}
