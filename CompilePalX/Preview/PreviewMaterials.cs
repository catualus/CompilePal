using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CompilePalX.Compiling;

namespace CompilePalX.Preview
{
    /// <summary>A material as the viewer sees it: its textures by id, and how to draw it.</summary>
    /// <param name="Reflection">How strongly the material reflects its cubemap, per channel: $envmaptint
    /// times the average of its mask. Null when it reflects nothing.</param>
    public sealed record ResolvedMaterial(
        int Index, string Name, string Shader, int? Texture, int? Texture2,
        bool Translucent, bool AlphaTest, bool NoCull, bool Unlit, float[] Color, bool Hidden,
        bool Water = false, float[]? WaterColor = null, bool Modulate = false, float[]? Reflection = null, float ReflectionContrast = 0);

    /// <summary>
    /// Turns the material names in a BSP into textures the viewer can load.
    ///
    /// For each name: find the VMT, read which VTF it wants, find and decode that, and hand back an
    /// id. Textures are shared between materials that name the same file, which they often do. A
    /// material whose files cannot be found keeps its flat colour, and the counts say how many did.
    /// </summary>
    public sealed class PreviewMaterials
    {
        private readonly ContentLocator content;
        private readonly Dictionary<string, int> textureIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string Path, Texture Texture)> textures = [];

        public IReadOnlyList<(string Path, Texture Texture)> Textures => textures;
        public int MaterialsFound { get; private set; }
        public int MaterialsMissing { get; private set; }
        public int TexturesMissing { get; private set; }

        public PreviewMaterials(ContentLocator content)
        {
            this.content = content;
        }

        private readonly List<ResolvedMaterial> resolved = [];
        private readonly Dictionary<string, int> resolvedByName = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every material resolved so far, in index order; batches refer to these indices.</summary>
        public IReadOnlyList<ResolvedMaterial> Resolved => resolved;

        /// <summary>Resolves every name in <paramref name="materialNames"/>, in order, keeping indices.</summary>
        public List<ResolvedMaterial> Resolve(IReadOnlyList<string> materialNames)
        {
            foreach (var name in materialNames)
                Append(name);

            return resolved.ToList();
        }

        /// <summary>
        /// The index of <paramref name="name"/>, resolving it first if it is new. The world's
        /// materials come from the texdata lump in order; props name theirs from their models, and
        /// the same material on two props is resolved once.
        /// </summary>
        public int IndexOf(string name)
        {
            if (resolvedByName.TryGetValue(name, out int known))
                return known;

            return Append(name);
        }

        private int Append(string name)
        {
            int i = resolved.Count;
            var material = LoadMaterial(name);

            if (material is null)
            {
                MaterialsMissing++;
                resolved.Add(new ResolvedMaterial(i, name, "", null, null, false, false, false, false, [1, 1, 1], false));
            }
            else
            {
                MaterialsFound++;
                int? texture = material.Hidden ? null : LoadTexture(material.BaseTexture);
                int? texture2 = material.Hidden ? null : LoadTexture(material.BaseTexture2);

                resolved.Add(new ResolvedMaterial(i, name, material.Shader, texture, texture2,
                    material.Translucent, material.AlphaTest, material.NoCull, material.Unlit, material.Color, material.Hidden,
                    material.Water, material.WaterColor, material.Modulate, Reflection(material), material.EnvMapContrast));
            }

            // the world can list one name twice under different texdata entries; the first keeps the name
            resolvedByName.TryAdd(name, i);
            return i;
        }

        /// <summary>
        /// The six faces of the 2D skybox named <paramref name="skyName"/>, as texture ids keyed by
        /// suffix, or null when none of them can be found.
        /// </summary>
        public Dictionary<string, int>? ResolveSky(string? skyName)
        {
            if (string.IsNullOrWhiteSpace(skyName))
                return null;

            var faces = new Dictionary<string, int>();
            foreach (var suffix in new[] { "ft", "bk", "lf", "rt", "up", "dn" })
            {
                var material = LoadMaterial($"skybox/{skyName}{suffix}");
                int? id = material is null ? null : LoadTexture(material.BaseTexture);
                if (id is { } found)
                    faces[suffix] = found;
            }

            return faces.Count == 6 ? faces : null;
        }

        private Material? LoadMaterial(string name)
        {
            string text = ReadText($"materials/{name}.vmt") ?? "";
            if (text.Length == 0)
                return null;

            try
            {
                return Vmt.Parse(name, text, include => ReadText(include.StartsWith("materials/", StringComparison.OrdinalIgnoreCase) ? include : $"materials/{include}"));
            }
            catch (Exception e)
            {
                CompilePalLogger.LogLineDebug($"Material \"{name}\" could not be read: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// How strongly <paramref name="material"/> reflects, as one number per channel: the tint times
        /// the average of the mask the material names. A picture baked from one view cannot follow a
        /// reflection's detail, only its overall strength.
        /// </summary>
        private float[]? Reflection(Material material)
        {
            if (material.EnvMap is null || material.Hidden)
                return null;
            float mask = material.NormalMapAlphaEnvMapMask ? AverageOf(material.BumpMap, alpha: true)
                : material.BaseAlphaEnvMapMask ? AverageOf(material.BaseTexture, alpha: true)
                : material.EnvMapMask is { } m ? AverageOf(m, alpha: false)
                : 1;
            return [material.EnvMapTint[0] * mask, material.EnvMapTint[1] * mask, material.EnvMapTint[2] * mask];
        }

        /// <summary>The average alpha, or brightness, of a texture, from a small mip level; 1 when it cannot be read.</summary>
        private float AverageOf(string? name, bool alpha)
        {
            if (string.IsNullOrWhiteSpace(name))
                return 1;
            string path = name.Replace('\\', '/').TrimStart('/');
            if (!path.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase)) path += ".vtf";
            if (!path.StartsWith("materials/", StringComparison.OrdinalIgnoreCase)) path = "materials/" + path;
            try
            {
                if (content.Read(path) is not { } bytes || Vtf.Read(bytes) is not { } texture || texture.Levels.Count == 0)
                    return 1;
                var level = texture.Levels.LastOrDefault(l => l.Width >= 8 && l.Height >= 8) ?? texture.Levels[0];
                var rgba = texture.Format == "rgba8" ? level.Data : Bake.Dxt.Decode(texture.Format, level.Width, level.Height, level.Data);
                double sum = 0;
                int n = level.Width * level.Height;
                for (int p = 0; p < n && p * 4 + 3 < rgba.Length; p++)
                    sum += alpha ? rgba[p * 4 + 3] : (rgba[p * 4] + rgba[p * 4 + 1] + rgba[p * 4 + 2]) / 3.0;
                return (float)(sum / n / 255);
            }
            catch (Exception)
            {
                return 1;
            }
        }

        private int? LoadTexture(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            string path = name.Replace('\\', '/').TrimStart('/');
            if (!path.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase))
                path += ".vtf";
            if (!path.StartsWith("materials/", StringComparison.OrdinalIgnoreCase))
                path = "materials/" + path;

            if (textureIds.TryGetValue(path, out int known))
                return known < 0 ? null : known;

            int? id = null;
            var bytes = content.Read(path);
            if (bytes is not null)
            {
                try
                {
                    if (Vtf.Read(bytes) is { } texture)
                    {
                        id = textures.Count;
                        textures.Add((path, texture));
                    }
                }
                catch (Exception e)
                {
                    CompilePalLogger.LogLineDebug($"Texture \"{path}\" could not be read: {e.Message}");
                }
            }

            if (id is null)
                TexturesMissing++;

            textureIds[path] = id ?? -1;
            return id;
        }

        private string? ReadText(string path)
        {
            var bytes = content.Read(path);
            return bytes is null ? null : Encoding.UTF8.GetString(bytes);
        }
    }
}
