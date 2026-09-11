using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CompilePalX.Compiling;
using Newtonsoft.Json;

namespace CompilePalX.Preview
{
    /// <summary>
    /// Writes a <see cref="PreviewScene"/> and its textures where the viewer page can fetch them.
    ///
    /// One folder: <c>scene.json</c> with the numbers, the materials, the batches and the camera;
    /// <c>geometry.bin</c> with the vertices and indices; <c>lightmap.bin</c> with the raw atlas;
    /// <c>textures/tN.bin</c> with each texture's mip levels back to back. The viewer page itself is
    /// copied in alongside, so one virtual host over one folder serves the lot and the page never
    /// has to cross an origin to load its data.
    /// </summary>
    public static class PreviewExporter
    {
        public const string HostName = "preview.compilepal";

        /// <summary>Per-user, per-process-independent: previews are scratch and never need keeping.</summary>
        public static string Folder { get; } =
            Path.Combine(Path.GetTempPath(), "CompilePal", "preview");

        private static readonly string ViewerSource =
            Path.Combine(AppContext.BaseDirectory, "Preview", "viewer.html");

        /// <summary>What was last written, for the status line and for tests.</summary>
        public sealed record Export(string BspPath, PreviewScene Scene, DateTime WrittenAt, long Stamp,
            int MaterialsFound, int MaterialsMissing, int TexturesWritten, int TexturesMissing, bool SkyWritten,
            int PropsPlaced, int PropsTotal, int PropsMissing);

        /// <summary>
        /// Reads <paramref name="bspPath"/>, finds its materials under <paramref name="gameFolder"/>
        /// and inside the map's own pak, and writes the preview folder. Throws on an unreadable map.
        /// </summary>
        public static Export Write(string bspPath, string? gameFolder)
        {
            var scene = BspGeometry.Read(bspPath);
            Directory.CreateDirectory(Folder);
            InstallViewer();

            long stamp = DateTime.UtcNow.Ticks;

            File.WriteAllBytes(Path.Combine(Folder, "lightmap.bin"), scene.Lightmap);

            // materials and textures
            using var content = new ContentLocator(scene.PakLump, gameFolder);
            var materials = new PreviewMaterials(content);
            materials.Resolve(scene.MaterialNames);
            var sky = materials.ResolveSky(scene.SkyName);

            // static props, after the world so their indices follow the world's
            var props = PropBuilder.Build(scene, content, materials, scene.LightingMode == "hdr");
            var resolved = materials.Resolved;

            uint worldVertices = (uint)scene.VertexCount;
            using (var geometry = new BinaryWriter(File.Create(Path.Combine(Folder, "geometry.bin"))))
            {
                geometry.Write(scene.VertexCount + props.Vertices.Length / PreviewScene.VertexStride);
                geometry.Write(scene.Indices.Length + props.Indices.Length);
                foreach (float f in scene.Vertices) geometry.Write(f);
                foreach (float f in props.Vertices) geometry.Write(f);
                foreach (uint i in scene.Indices) geometry.Write(i);
                foreach (uint i in props.Indices) geometry.Write(i + worldVertices);
            }

            string textureFolder = Path.Combine(Folder, "textures");
            if (Directory.Exists(textureFolder))
                foreach (var old in Directory.EnumerateFiles(textureFolder))
                    File.Delete(old);
            Directory.CreateDirectory(textureFolder);

            var textureManifest = new List<object>();
            for (int id = 0; id < materials.Textures.Count; id++)
            {
                var (path, texture) = materials.Textures[id];
                var levels = new List<object>();
                using (var file = File.Create(Path.Combine(textureFolder, $"t{id}.bin")))
                {
                    long offset = 0;
                    foreach (var level in texture.Levels)
                    {
                        file.Write(level.Data);
                        levels.Add(new { w = level.Width, h = level.Height, offset, size = level.Data.Length });
                        offset += level.Data.Length;
                    }
                }

                textureManifest.Add(new
                {
                    id,
                    path,
                    format = texture.Format,
                    width = texture.Width,
                    height = texture.Height,
                    hasAlpha = texture.HasAlpha,
                    levels,
                });
            }

            var header = new
            {
                map = Path.GetFileNameWithoutExtension(bspPath),
                source = bspPath,
                stamp,
                writtenAt = DateTime.Now.ToString("s"),
                bspVersion = scene.BspVersion,
                compressed = scene.Compressed,
                vertexStride = PreviewScene.VertexStride,
                vertexCount = scene.VertexCount + props.Vertices.Length / PreviewScene.VertexStride,
                indexCount = scene.Indices.Length + props.Indices.Length,
                lightmap = new
                {
                    width = scene.LightmapWidth,
                    height = scene.LightmapHeight,
                    mode = scene.LightingMode,
                    range = BspGeometry.LightmapRange,
                },
                bounds = new { mins = scene.Mins, maxs = scene.Maxs },
                spawn = scene.Spawn,
                sky = sky is not null
                    ? new { name = scene.SkyName, faces = (object?)sky, painted = (object?)null }
                    : scene.SkyPaint is { } paint
                        ? new
                        {
                            name = scene.SkyName,
                            faces = (object?)null,
                            painted = (object?)new
                            {
                                top = paint.TopColor, bottom = paint.BottomColor, fadeBias = paint.FadeBias,
                                sunColor = paint.SunColor, sunNormal = paint.SunNormal, sunSize = paint.SunSize,
                            },
                        }
                        : null,
                materials = resolved.Select(m => new
                {
                    name = m.Name,
                    shader = m.Shader,
                    texture = m.Texture,
                    texture2 = m.Texture2,
                    translucent = m.Translucent,
                    alphaTest = m.AlphaTest,
                    noCull = m.NoCull,
                    unlit = m.Unlit,
                    color = m.Color,
                    hidden = m.Hidden,
                }).ToList(),
                batches = scene.Batches.Select(b => new { material = b.Material, first = b.First, count = b.Count, prop = false })
                    .Concat(props.Batches.Select(b => new { material = b.Material, first = b.First + scene.Indices.Length, count = b.Count, prop = true }))
                    .ToList(),
                props = new
                {
                    total = scene.StaticProps.Count,
                    placed = props.PropsPlaced,
                    missing = props.PropsMissing,
                    skipped = props.PropsSkipped,
                    models = props.ModelsLoaded,
                    baked = props.PropsWithBakedLight,
                    triangles = props.Triangles,
                },
                textures = textureManifest,
                content = new
                {
                    gameContent = content.HasGameContent,
                    folders = content.Folders.Count,
                    vpks = content.Vpks.Count,
                    materialsFound = materials.MaterialsFound,
                    materialsMissing = materials.MaterialsMissing,
                    texturesMissing = materials.TexturesMissing,
                },
                faces = new
                {
                    total = scene.FaceCount,
                    drawn = scene.DrawnFaces,
                    displacements = scene.DrawnDisplacements,
                    displacementsSkipped = scene.SkippedDisplacements,
                    tool = scene.SkippedToolFaces,
                    unlit = scene.FacesWithoutLightmap,
                    brushEntities = scene.PlacedBrushEntities,
                },
            };

            ConfigurationManager.WriteFileAtomic(Path.Combine(Folder, "scene.json"), JsonConvert.SerializeObject(header, Formatting.Indented));

            CompilePalLogger.LogLineDebug(
                $"Preview written for {header.map}: {scene.DrawnFaces} of {scene.FaceCount} faces, {scene.TriangleCount} triangles, " +
                $"{scene.LightingMode} lighting in a {scene.LightmapWidth}x{scene.LightmapHeight} atlas, " +
                $"{scene.DrawnDisplacements} displacements, {scene.PlacedBrushEntities} brush entities placed, " +
                $"{props.PropsPlaced} of {scene.StaticProps.Count} static props ({props.ModelsLoaded} models, {props.Triangles} triangles, {props.PropsWithBakedLight} with baked light, {props.PropsMissing} models missing, {props.PropsSkipped} over budget), " +
                $"{materials.MaterialsFound} of {scene.MaterialNames.Count} materials found ({content.PakHits} of {content.PackedFiles} packed, {content.FolderHits} loose, {content.VpkHits} in VPKs), " +
                $"{materials.Textures.Count} textures, sky {(sky is not null ? scene.SkyName : scene.SkyPaint is not null ? "painted" : "not found")}" +
                $"{(scene.Compressed ? ", inflated from a compressed BSP" : "")}.");

            return new Export(bspPath, scene, DateTime.Now, stamp,
                materials.MaterialsFound, materials.MaterialsMissing, materials.Textures.Count, materials.TexturesMissing, sky is not null || scene.SkyPaint is not null,
                props.PropsPlaced, scene.StaticProps.Count, props.PropsMissing);
        }

        /// <summary>Copies the viewer page into the folder when it is missing or older than the shipped one.</summary>
        public static void InstallViewer()
        {
            string target = Path.Combine(Folder, "viewer.html");

            if (!File.Exists(ViewerSource))
                throw new FileNotFoundException("The preview viewer page is missing from the Compile Pal folder.", ViewerSource);

            if (!File.Exists(target) || File.GetLastWriteTimeUtc(target) < File.GetLastWriteTimeUtc(ViewerSource))
                File.Copy(ViewerSource, target, overwrite: true);
        }
    }
}
