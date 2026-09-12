using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CompilePalX.Compiling;

namespace CompilePalX.Preview
{
    /// <summary>A material as the viewer sees it: its textures by id, and how to draw it.</summary>
    public sealed record ResolvedMaterial(
        int Index, string Name, string Shader, int? Texture, int? Texture2,
        bool Translucent, bool AlphaTest, bool NoCull, bool Unlit, float[] Color, bool Hidden,
        bool Water = false, float[]? WaterColor = null, bool Modulate = false);

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
                    material.Water, material.WaterColor, material.Modulate));
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
