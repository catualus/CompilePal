using System;
using System.IO;
using CompilePalX.Compiling;
using Newtonsoft.Json;

namespace CompilePalX.Preview
{
    /// <summary>
    /// Writes a <see cref="PreviewScene"/> where the viewer page can fetch it.
    ///
    /// Three files in one folder: <c>scene.json</c> with the numbers and the camera, <c>geometry.bin</c>
    /// with the vertices and indices, <c>lightmap.bin</c> with the raw atlas. The viewer page itself
    /// is copied in alongside, so one virtual host over one folder serves the lot and the page never
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
        public sealed record Export(string BspPath, PreviewScene Scene, DateTime WrittenAt, long Stamp);

        /// <summary>Reads <paramref name="bspPath"/> and writes the preview folder. Throws on an unreadable map.</summary>
        public static Export Write(string bspPath)
        {
            var scene = BspGeometry.Read(bspPath);
            Directory.CreateDirectory(Folder);
            InstallViewer();

            long stamp = DateTime.UtcNow.Ticks;

            using (var geometry = new BinaryWriter(File.Create(Path.Combine(Folder, "geometry.bin"))))
            {
                geometry.Write(scene.VertexCount);
                geometry.Write(scene.Indices.Length);
                foreach (float f in scene.Vertices) geometry.Write(f);
                foreach (uint i in scene.Indices) geometry.Write(i);
            }

            File.WriteAllBytes(Path.Combine(Folder, "lightmap.bin"), scene.Lightmap);

            var header = new
            {
                map = Path.GetFileNameWithoutExtension(bspPath),
                source = bspPath,
                stamp,
                writtenAt = DateTime.Now.ToString("s"),
                bspVersion = scene.BspVersion,
                compressed = scene.Compressed,
                vertexStride = PreviewScene.VertexStride,
                vertexCount = scene.VertexCount,
                indexCount = scene.Indices.Length,
                lightmap = new { width = scene.LightmapWidth, height = scene.LightmapHeight, mode = scene.LightingMode },
                bounds = new { mins = scene.Mins, maxs = scene.Maxs },
                spawn = scene.Spawn,
                faces = new
                {
                    total = scene.FaceCount,
                    drawn = scene.DrawnFaces,
                    displacements = scene.DrawnDisplacements,
                    displacementsSkipped = scene.SkippedDisplacements,
                    tool = scene.SkippedToolFaces,
                    unlit = scene.FacesWithoutLightmap,
                },
            };

            ConfigurationManager.WriteFileAtomic(Path.Combine(Folder, "scene.json"), JsonConvert.SerializeObject(header, Formatting.Indented));

            CompilePalLogger.LogLineDebug(
                $"Preview written for {header.map}: {scene.DrawnFaces} of {scene.FaceCount} faces, {scene.TriangleCount} triangles, " +
                $"{scene.LightingMode} lighting in a {scene.LightmapWidth}x{scene.LightmapHeight} atlas, " +
                $"{scene.DrawnDisplacements} displacements{(scene.Compressed ? ", inflated from a compressed BSP" : "")}.");

            return new Export(bspPath, scene, DateTime.Now, stamp);
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
