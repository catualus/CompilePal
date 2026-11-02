using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using CompilePalX.Preview;

namespace CompilePalX.Bake
{
    /// <summary>
    /// Bakes what lies behind each of Viswright's shut windows into the picture the window shows.
    ///
    /// Viswright gives every func_areaportalwindow a stand-in: a slab across the opening drawn while
    /// the window is shut, past its fade distance, when what is behind is no longer drawn. With
    /// window pictures on, each face of the slab gets its own material, <c>viswright/&lt;map&gt;/...</c>,
    /// whose texture spans the face exactly. This finds those faces in the compiled map, draws what
    /// each one looks out on - from straight out in front, as far away as a window is shut, starting
    /// at the window's own plane so nothing on the near side gets in - and writes the picture as
    /// that material's texture: a DXT1 VTF with mips and an UnlitGeneric VMT, since its light is
    /// already in it.
    /// </summary>
    public static class WindowBaker
    {
        public const string MaterialPrefix = "viswright/";

        /// <summary>How far out from a window its picture is taken from: past where windows shut.</summary>
        public const float ViewDistance = 1500;

        public sealed record Result(int Faces, IReadOnlyDictionary<string, byte[]> Files, IReadOnlyList<string> Notes);

        /// <summary>Whether a material is a window stand-in's face.</summary>
        public static bool IsStandIn(string material) => material.Replace('\\', '/').StartsWith(MaterialPrefix, StringComparison.OrdinalIgnoreCase);

        /// <summary>The pictures for every stand-in face in <paramref name="bspPath"/>, as files for the pakfile.</summary>
        public static Result Bake(string bspPath, string? gameFolder, Action<string>? progress = null)
        {
            var scene = BspGeometry.Read(bspPath);
            var standIns = Enumerable.Range(0, scene.MaterialNames.Count).Where(i => IsStandIn(scene.MaterialNames[i])).ToHashSet();
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var notes = new List<string>();
            if (standIns.Count == 0)
                return new Result(0, files, notes);

            using var content = new ContentLocator(scene.PakLump, gameFolder);
            var materials = new PreviewMaterials(content);
            materials.Resolve(scene.MaterialNames);
            var sky = materials.ResolveSky(scene.SkyName);
            var props = PropBuilder.Build(scene, content, materials, scene.LightingMode == "hdr");
            var renderer = new SceneRenderer(scene, props, materials, sky, standIns.Contains);
            progress?.Invoke($"{standIns.Count} window face(s) to bake; the scene has {renderer.TriangleCount:N0} triangles, {props.PropsPlaced:N0} props placed ({props.PropsMissing:N0} models missing)");
            if (scene.LightingMode == "none")
                notes.Add("the map has no lighting yet, so the pictures are drawn fullbright; run VRAD before baking");

            var frames = standIns.ToDictionary(i => scene.MaterialNames[i].Replace('\\', '/').ToLowerInvariant(), i => (Material: i, Face: Face(scene, i)));
            var views = Views(content, frames.Keys);
            int done = 0;
            var gate = new object();
            var baked = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            // each face is drawn on its own; the renderer only reads the scene
            System.Threading.Tasks.Parallel.ForEach(frames.OrderBy(kv => kv.Key, StringComparer.Ordinal), entry =>
            {
                var (name, (material, found)) = (entry.Key, entry.Value);
                if (found is not { } face)
                {
                    lock (gate) notes.Add($"{name}: no face of the map uses it");
                    return;
                }
                // Outward is away from the face on the slab's other side, named _a and _b: a face's
                // stored normal is not to be trusted to point either way.
                string other = name[..^1] + (name[^1] == 'a' ? 'b' : 'a');
                if (frames.TryGetValue(other, out var pair) && pair.Face is { } opposite)
                {
                    var away = face.Origin + (face.U + face.V) / 2 - (opposite.Origin + (opposite.U + opposite.V) / 2);
                    if (Vector3.Dot(away, face.Normal) < 0)
                        face = face with { Normal = -face.Normal };
                }
                var (width, height) = Size(materials, material, face);
                var f = face;
                var centre = f.Origin + f.U * 0.5f + f.V * 0.5f;
                // where Viswright says the face is seen from and the room starts; else straight out in front
                var (eye, clip, clipDistance, square, farDistance, alpha, skip, glass) = views.TryGetValue(name, out var view)
                    ? view
                    : new View(centre + f.Normal * ViewDistance, -f.Normal, Vector3.Dot(-f.Normal, centre), false, null, false, null, false);
                var right = Vector3.Normalize(f.U);
                var up = -Vector3.Normalize(f.V);
                var forward = Vector3.Normalize(Vector3.Cross(f.U, f.V));
                if (square)
                {
                    // straight on along the clip normal, from far enough back that everything is ahead
                    forward = Vector3.Dot(forward, clip) < 0 ? -forward : forward;
                    eye = centre - forward * 100_000;
                }
                else if (Vector3.Dot(centre - eye, forward) < 0)
                    forward = -forward;
                var toCentre = centre - eye;
                var camera = new BakeCamera(eye, forward, right, up, Vector3.Dot(toCentre, forward),
                    Vector3.Dot(toCentre, right), Vector3.Dot(toCentre, up), f.U.Length() / 2, f.V.Length() / 2, clip, clipDistance, square,
                    farDistance, SeeThrough: alpha || glass, Skip: skip, Glass: glass);
                var picture = renderer.Render(camera, width, height);
                baked[$"materials/{name}.vtf"] = VtfWriter.Write(picture, width, height, alpha, smooth: glass);
                baked[$"materials/{name}.vmt"] = Encoding.ASCII.GetBytes(Vmt(name, alpha, glass));
                lock (gate)
                {
                    done++;
                    progress?.Invoke($"  {name}: {width}x{height}");
                }
            });
            foreach (var (path, bytes) in baked.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                files[path] = bytes;
            return new Result(done, files, notes);
        }

        /// <summary>
        /// What Viswright's <c>bake.json</c> files say of each face: the eye it is seen from, and the
        /// plane where the room starts. One file per map folder, found through the game's content.
        /// </summary>
        /// <summary>How one face is drawn, as bake.json gives it.</summary>
        private sealed record View(Vector3 Eye, Vector3 Clip, float ClipDistance, bool Square, float? Far, bool Alpha,
            IReadOnlyList<(int Axis, float At)>? Skip, bool Glass);

        private static Dictionary<string, View> Views(ContentLocator content, IEnumerable<string> materials)
        {
            var views = new Dictionary<string, View>(StringComparer.OrdinalIgnoreCase);
            foreach (string folder in materials.Select(m => m[..m.LastIndexOf('/')]).Distinct())
            {
                var bytes = content.Read($"materials/{folder}/bake.json");
                if (bytes == null)
                    continue;
                using var json = System.Text.Json.JsonDocument.Parse(bytes);
                foreach (var face in json.RootElement.GetProperty("faces").EnumerateArray())
                {
                    var e = face.GetProperty("eye").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
                    var c = face.GetProperty("clip").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
                    bool square = face.TryGetProperty("square", out var s) && s.GetBoolean();
                    float? far = face.TryGetProperty("far", out var f) ? (float)f.GetDouble() : null;
                    bool alpha = face.TryGetProperty("alpha", out var al) && al.GetBoolean();
                    // planes another picture shows: [axis, where along it]
                    var skip = face.TryGetProperty("skip", out var sk)
                        ? sk.EnumerateArray().Select(p => ((int)p[0].GetDouble(), (float)p[1].GetDouble())).ToList()
                        : null;
                    // translucent by degrees, as the glass in a window's opening is
                    bool glass = face.TryGetProperty("translucent", out var tr) && tr.GetBoolean();
                    views[face.GetProperty("material").GetString()!] = new View(new Vector3(e[0], e[1], e[2]), new Vector3(c[0], c[1], c[2]), c[3], square, far, alpha, skip, glass);
                }
            }
            return views;
        }

        /// <summary>The material a baked face uses: its picture, unlit.</summary>
        public static string Vmt(string name, bool alpha = false, bool translucent = false) =>
            "\"UnlitGeneric\"\r\n{\r\n\t\"$basetexture\" \"" + name + "\"\r\n" +
            (translucent ? "\t\"$translucent\" \"1\"\r\n" : alpha ? "\t\"$alphatest\" \"1\"\r\n" : "") +
            "\t\"%keywords\" \"viswright\"\r\n}\r\n";

        /// <summary>
        /// A face as its texture lies on it: the point where the texture's corner falls, and how far
        /// across the face the texture's width and height each run, from its texture coordinates.
        /// </summary>
        private readonly record struct FaceFrame(Vector3 Origin, Vector3 U, Vector3 V, Vector3 Normal);

        private static FaceFrame? Face(PreviewScene scene, int material)
        {
            const int s = PreviewScene.VertexStride;
            Vector3 P(uint v) => new(scene.Vertices[v * s], scene.Vertices[v * s + 1], scene.Vertices[v * s + 2]);
            Vector2 T(uint v) => new(scene.Vertices[v * s + 8], scene.Vertices[v * s + 9]);
            // The triangle the mapping is read from: the largest in texture space. VBSP adds vertices along
            // a face's edges to mend T-junctions, so the first triangle of a fan can be a sliver whose
            // corners lie in a line, from which no mapping can be read.
            uint i0 = 0, i1 = 0, i2 = 0;
            float det = 0;
            foreach (var batch in scene.Batches.Where(b => b.Material == material))
                for (int k = batch.First; k + 2 < batch.First + batch.Count; k += 3)
                {
                    uint a = scene.Indices[k], b = scene.Indices[k + 1], c = scene.Indices[k + 2];
                    Vector2 ta = T(a), tb = T(b) - ta, tc = T(c) - ta;
                    float d = tb.X * tc.Y - tc.X * tb.Y;
                    if (MathF.Abs(d) > MathF.Abs(det))
                        (i0, i1, i2, det) = (a, b, c, d);
                }
            if (MathF.Abs(det) < 1e-9f)
                return null;
            Vector3 p0 = P(i0), e1 = P(i1) - p0, e2 = P(i2) - p0;
            Vector2 t0 = T(i0), d1 = T(i1) - t0, d2 = T(i2) - t0;
            // world distance per whole texture across: e = U du + V dv
            var u = (e1 * d2.Y - e2 * d1.Y) / det;
            var v = (e2 * d1.X - e1 * d2.X) / det;
            var origin = p0 - u * t0.X - v * t0.Y;
            var normal = Vector3.Normalize(new Vector3(scene.Vertices[i0 * s + 3], scene.Vertices[i0 * s + 4], scene.Vertices[i0 * s + 5]));
            return new FaceFrame(origin, u, v, normal);
        }

        /// <summary>
        /// The picture's size: the texture the material already names (Viswright's placeholder, sized
        /// for the face), so the face keeps the mapping VBSP gave it; else a texel a unit, to 512.
        /// </summary>
        private static (int Width, int Height) Size(PreviewMaterials materials, int material, FaceFrame face)
        {
            if (material < materials.Resolved.Count && materials.Resolved[material].Texture is { } id && id < materials.Textures.Count)
            {
                var t = materials.Textures[id].Texture;
                if (t.Width >= 4 && t.Height >= 4)
                    return (t.Width, t.Height);
            }
            return (PowerOfTwo(face.U.Length()), PowerOfTwo(face.V.Length()));
        }

        /// <summary>The texture edge for a face edge this many units long: a texel a unit, a power of two, 32 to 512.</summary>
        public static int PowerOfTwo(float units)
        {
            int size = 32;
            while (size < units && size < 512)
                size *= 2;
            return size;
        }
    }
}
