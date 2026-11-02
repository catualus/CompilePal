using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CompilePalX.Preview;

namespace CompilePalX.Bake
{
    /// <summary>
    /// A view through a rectangle: the eye, the way it looks, and the rectangle - <see cref="Distance"/>
    /// ahead, centred <see cref="CentreX"/> right and <see cref="CentreY"/> up of straight ahead, with its
    /// half extents. The picture is what lies along each ray from the eye through the rectangle, but
    /// only beyond the clip plane (points p with Clip·p ≥ ClipDistance): where the room starts, which
    /// need not be the rectangle. A <see cref="Square"/> view looks straight along <see cref="Forward"/>
    /// instead, every ray parallel, so what lies behind each point of the rectangle is what it shows.
    /// With <see cref="FarDistance"/>, only what lies before it (Clip·p ≤ FarDistance) is drawn, and a
    /// <see cref="SeeThrough"/> view leaves where nothing is drawn transparent rather than showing sky.
    /// </summary>
    public readonly record struct BakeCamera(Vector3 Eye, Vector3 Forward, Vector3 Right, Vector3 Up, float Distance,
        float CentreX, float CentreY, float HalfWidth, float HalfHeight, Vector3 Clip, float ClipDistance, bool Square = false,
        float? FarDistance = null, bool SeeThrough = false);

    /// <summary>
    /// Draws a compiled map on the CPU the way the preview draws it on the GPU: the same triangles,
    /// lightmap atlas, textures and baked prop light, shaded by the same sums - texture times light,
    /// fog, a soft clip near white, display gamma - so a picture baked here matches the preview, and
    /// the preview has been checked against the game.
    ///
    /// The 3D skybox is drawn first into its own depth, then the world and props, then translucent
    /// surfaces and overlays from far to near, blended. Each triangle is first tested against the
    /// view, so a view through one window only rasterises what lies beyond it.
    /// </summary>
    public sealed class SceneRenderer
    {
        private readonly record struct Tri(int A, int B, int C, int Material, bool Prop, bool Skybox, bool Overlay, Vector3 Min, Vector3 Max);

        private readonly PreviewScene scene;
        private readonly float[] vertices;
        private readonly Tri[] triangles;
        private readonly IReadOnlyList<ResolvedMaterial> materials;
        private readonly IReadOnlyList<(string Path, Texture Texture)> textures;
        private readonly Dictionary<string, int>? sky;
        private readonly ConcurrentDictionary<(int Texture, int Level), byte[]?> decoded = new();
        private readonly float[] lightmap;

        private const int Stride = PreviewScene.VertexStride;

        /// <param name="skip">Materials not drawn at all, by index: the window stand-ins being baked, say.</param>
        public SceneRenderer(PreviewScene scene, PropGeometry props, PreviewMaterials materials, Dictionary<string, int>? sky, Func<int, bool> skip)
        {
            this.scene = scene;
            this.materials = materials.Resolved;
            textures = materials.Textures;
            this.sky = sky;
            vertices = new float[scene.Vertices.Length + props.Vertices.Length];
            scene.Vertices.CopyTo(vertices, 0);
            props.Vertices.CopyTo(vertices, scene.Vertices.Length);
            uint propBase = (uint)scene.VertexCount;

            var list = new List<Tri>();
            void Add(DrawBatch b, uint[] indices, uint offset, bool prop)
            {
                if (b.Material < 0 || b.Material >= this.materials.Count || this.materials[b.Material].Hidden || skip(b.Material))
                    return;
                for (int i = b.First; i + 2 < b.First + b.Count; i += 3)
                {
                    int a = (int)(indices[i] + offset), c1 = (int)(indices[i + 1] + offset), c2 = (int)(indices[i + 2] + offset);
                    var pa = Position(a); var pb = Position(c1); var pc = Position(c2);
                    list.Add(new Tri(a, c1, c2, b.Material, prop, b.Skybox, b.Overlay, Vector3.Min(pa, Vector3.Min(pb, pc)), Vector3.Max(pa, Vector3.Max(pb, pc))));
                }
            }
            foreach (var b in scene.Batches) Add(b, scene.Indices, 0, false);
            foreach (var b in props.Batches) Add(b, props.Indices, propBase, true);
            triangles = list.ToArray();

            // the atlas as linear light, as the shader reads it
            lightmap = new float[scene.Lightmap.Length / 4 * 3];
            for (int i = 0, o = 0; i + 3 < scene.Lightmap.Length; i += 4, o += 3)
                for (int c = 0; c < 3; c++)
                    lightmap[o + c] = MathF.Pow(scene.Lightmap[i + c] / 255f, 2.2f) * BspGeometry.LightmapRange;
        }

        /// <summary>How many triangles the renderer holds.</summary>
        public int TriangleCount => triangles.Length;

        private Vector3 Position(int v) => new(vertices[v * Stride], vertices[v * Stride + 1], vertices[v * Stride + 2]);

        /// <summary>
        /// Draws <paramref name="camera"/>'s view at <paramref name="width"/> by <paramref name="height"/>,
        /// each pixel the average of <paramref name="samples"/>² sub-pixels. Returns RGBA8 in display gamma, rows top first.
        /// </summary>
        public byte[] Render(BakeCamera camera, int width, int height, int samples = 2)
        {
            int w = width * samples, h = height * samples;
            var target = new Target(w, h);

            // the background: the sky in every direction, or nothing at all
            if (!camera.SeeThrough)
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        var d = Direction(camera, (x + 0.5f) / w, (y + 0.5f) / h);
                        target.Set(x, y, SkyColour(Vector3.Normalize(d)), 1);
                    }

            var planes = Planes(camera);
            var visible = triangles.Where(t => Inside(planes, t.Min, t.Max)).ToList();

            // the 3D skybox behind everything, then the depth cleared for the world
            Draw(target, camera, visible.Where(t => t.Skybox && !Translucent(t)), blend: false);
            Draw(target, camera, Sorted(camera, visible.Where(t => t.Skybox && Translucent(t))), blend: true);
            Array.Fill(target.Depth, 0f);
            Draw(target, camera, visible.Where(t => !t.Skybox && !Translucent(t)), blend: false);
            // A see-through picture is cut out where it is not covered: clear glass would cover a whole
            // window and hide what it is cut out to show, so a translucent surface is kept only where it
            // is mostly opaque - the bars of a grate printed on glass, a frame.
            if (camera.SeeThrough)
                Draw(target, camera, visible.Where(t => !t.Skybox && Translucent(t)), blend: false, cutout: true);
            else
                Draw(target, camera, Sorted(camera, visible.Where(t => !t.Skybox && Translucent(t))), blend: true);

            // down to the asked size
            var output = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    // colour averaged over the sub-pixels something was drawn in; coverage is the alpha
                    Vector3 sum = Vector3.Zero;
                    float covered = 0;
                    for (int sy = 0; sy < samples; sy++)
                        for (int sx = 0; sx < samples; sx++)
                        {
                            int i = (y * samples + sy) * w + x * samples + sx;
                            sum += target.Colour[i] * target.Cover[i];
                            covered += target.Cover[i];
                        }
                    if (covered > 0)
                        sum /= covered;
                    int o = (y * width + x) * 4;
                    output[o] = (byte)Math.Clamp(sum.X * 255 + 0.5f, 0, 255);
                    output[o + 1] = (byte)Math.Clamp(sum.Y * 255 + 0.5f, 0, 255);
                    output[o + 2] = (byte)Math.Clamp(sum.Z * 255 + 0.5f, 0, 255);
                    output[o + 3] = (byte)Math.Clamp(covered / (samples * samples) * 255 + 0.5f, 0, 255);
                }
            return output;
        }

        private bool Translucent(Tri t)
        {
            var m = materials[t.Material];
            return t.Overlay || m.Translucent || m.Water;
        }

        private static IEnumerable<Tri> Sorted(BakeCamera camera, IEnumerable<Tri> tris) =>
            tris.OrderByDescending(t => Vector3.Dot((t.Min + t.Max) / 2 - camera.Eye, camera.Forward));

        private static Vector3 Direction(BakeCamera c, float u, float v) => c.Square ? c.Forward :
            c.Forward * c.Distance + c.Right * (c.CentreX + (u * 2 - 1) * c.HalfWidth) + c.Up * (c.CentreY + (1 - v * 2) * c.HalfHeight);

        // ---- the view's bounds -------------------------------------------------------------

        private readonly record struct Plane(Vector3 N, float D)
        {
            public float Distance(Vector3 p) => Vector3.Dot(N, p) - D;
        }

        private static Plane[] Planes(BakeCamera c)
        {
            var corners = new[] { Direction(c, 0, 0), Direction(c, 1, 0), Direction(c, 1, 1), Direction(c, 0, 1) };
            var middle = Direction(c, 0.5f, 0.5f);
            var planes = new List<Plane> { new(c.Clip, c.ClipDistance), new(c.Forward, Vector3.Dot(c.Forward, c.Eye) + 1) };
            if (c.FarDistance is { } far)
                planes.Add(new Plane(-c.Clip, -far));
            if (c.Square)
            {
                float x = Vector3.Dot(c.Right, c.Eye) + c.CentreX, y = Vector3.Dot(c.Up, c.Eye) + c.CentreY;
                planes.Add(new Plane(c.Right, x - c.HalfWidth));
                planes.Add(new Plane(-c.Right, -(x + c.HalfWidth)));
                planes.Add(new Plane(c.Up, y - c.HalfHeight));
                planes.Add(new Plane(-c.Up, -(y + c.HalfHeight)));
                return planes.ToArray();
            }
            for (int i = 0; i < 4; i++)
            {
                var n = Vector3.Normalize(Vector3.Cross(corners[i], corners[(i + 1) % 4]));
                if (Vector3.Dot(n, middle) < 0) n = -n;
                planes.Add(new Plane(n, Vector3.Dot(n, c.Eye)));
            }
            return planes.ToArray();
        }

        private static bool Inside(Plane[] planes, Vector3 min, Vector3 max)
        {
            foreach (var p in planes)
            {
                var far = new Vector3(p.N.X >= 0 ? max.X : min.X, p.N.Y >= 0 ? max.Y : min.Y, p.N.Z >= 0 ? max.Z : min.Z);
                if (p.Distance(far) < 0)
                    return false;
            }
            return true;
        }

        // ---- rasterising -------------------------------------------------------------------

        private sealed class Target(int width, int height)
        {
            public readonly int Width = width, Height = height;
            public readonly Vector3[] Colour = new Vector3[width * height];
            public readonly float[] Depth = new float[width * height];
            public readonly float[] Cover = new float[width * height];

            public void Set(int x, int y, Vector3 c, float a)
            {
                int i = y * Width + x;
                Colour[i] = a >= 1 || Cover[i] == 0 ? c : Vector3.Lerp(Colour[i], c, a);
                Cover[i] = MathF.Max(Cover[i], MathF.Min(1, a >= 1 ? 1 : Cover[i] + a));
            }
        }

        /// <summary>A vertex as the rasteriser needs it: where it is in the view, and what the shader reads from it.</summary>
        private struct Corner
        {
            public Vector3 View;      // x right, y up, z ahead
            public Vector3 World;
            public Vector2 Lm, Tex;
            public float Blend;
            public Vector3 Colour;

            public static Corner Lerp(Corner a, Corner b, float t) => new()
            {
                View = Vector3.Lerp(a.View, b.View, t), World = Vector3.Lerp(a.World, b.World, t),
                Lm = Vector2.Lerp(a.Lm, b.Lm, t), Tex = Vector2.Lerp(a.Tex, b.Tex, t),
                Blend = a.Blend + (b.Blend - a.Blend) * t, Colour = Vector3.Lerp(a.Colour, b.Colour, t),
            };
        }

        private Corner CornerOf(BakeCamera c, int v)
        {
            int o = v * Stride;
            var world = new Vector3(vertices[o], vertices[o + 1], vertices[o + 2]);
            var rel = world - c.Eye;
            return new Corner
            {
                View = new Vector3(Vector3.Dot(rel, c.Right), Vector3.Dot(rel, c.Up), Vector3.Dot(rel, c.Forward)),
                World = world,
                Lm = new Vector2(vertices[o + 6], vertices[o + 7]),
                Tex = new Vector2(vertices[o + 8], vertices[o + 9]),
                Blend = vertices[o + 10],
                Colour = new Vector3(vertices[o + 11], vertices[o + 12], vertices[o + 13]),
            };
        }

        private void Draw(Target target, BakeCamera camera, IEnumerable<Tri> tris, bool blend, bool cutout = false)
        {
            var polygon = new List<Corner>(6);
            foreach (var t in tris)
            {
                polygon.Clear();
                polygon.Add(CornerOf(camera, t.A));
                polygon.Add(CornerOf(camera, t.B));
                polygon.Add(CornerOf(camera, t.C));
                ClipNear(polygon, camera);
                for (int i = 1; i + 1 < polygon.Count; i++)
                    Rasterise(target, camera, t, polygon[0], polygon[i], polygon[i + 1], blend, cutout);
            }
        }

        /// <summary>Cuts a polygon down to what lies beyond the clip plane and in front of the eye.</summary>
        private static void ClipNear(List<Corner> polygon, BakeCamera camera)
        {
            Cut(polygon, k => Vector3.Dot(camera.Clip, k.World) - camera.ClipDistance);
            if (camera.FarDistance is { } far)
                Cut(polygon, k => far - Vector3.Dot(camera.Clip, k.World));
            Cut(polygon, k => k.View.Z - 1);
        }

        private static void Cut(List<Corner> polygon, Func<Corner, float> distance)
        {
            var output = new List<Corner>(polygon.Count + 2);
            for (int i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                float da = distance(a), db = distance(b);
                bool ina = da >= 0, inb = db >= 0;
                if (ina) output.Add(a);
                if (ina != inb)
                    output.Add(Corner.Lerp(a, b, da / (da - db)));
            }
            polygon.Clear();
            polygon.AddRange(output);
        }

        private void Rasterise(Target target, BakeCamera camera, Tri t, Corner a, Corner b, Corner c, bool blend, bool cutout = false)
        {
            // screen positions: the rectangle at the near plane fills the target
            float Project(float along, float z) => camera.Square ? along : along / z * camera.Distance;
            Vector2 Screen(Corner k) => new(
                ((Project(k.View.X, k.View.Z) - camera.CentreX) / camera.HalfWidth + 1) / 2 * target.Width,
                (1 - (Project(k.View.Y, k.View.Z) - camera.CentreY) / camera.HalfHeight) / 2 * target.Height);
            Vector2 sa = Screen(a), sb = Screen(b), sc = Screen(c);
            float area = (sb.X - sa.X) * (sc.Y - sa.Y) - (sb.Y - sa.Y) * (sc.X - sa.X);
            if (MathF.Abs(area) < 1e-6f)
                return;

            var material = materials[t.Material];
            bool overlay = t.Overlay;
            if (material.Modulate)
                return;
            Texture? texture = material.Texture is { } id && id < textures.Count ? textures[id].Texture : null;
            Texture? texture2 = material.Texture2 is { } id2 && id2 < textures.Count ? textures[id2].Texture : null;

            // the mip level from how many texels a pixel covers here
            int level = 0;
            if (texture != null)
            {
                var ta = a.Tex * new Vector2(texture.Width, texture.Height);
                var tb = b.Tex * new Vector2(texture.Width, texture.Height);
                var tc = c.Tex * new Vector2(texture.Width, texture.Height);
                float texArea = MathF.Abs((tb.X - ta.X) * (tc.Y - ta.Y) - (tb.Y - ta.Y) * (tc.X - ta.X));
                if (texArea > 0)
                    level = Math.Clamp((int)MathF.Floor(0.5f * MathF.Log2(texArea / MathF.Abs(area))), 0, texture.Levels.Count - 1);
            }

            float iza = 1 / a.View.Z, izb = 1 / b.View.Z, izc = 1 / c.View.Z;
            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(sa.X, MathF.Min(sb.X, sc.X))));
            int x1 = Math.Min(target.Width - 1, (int)MathF.Ceiling(MathF.Max(sa.X, MathF.Max(sb.X, sc.X))));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(sa.Y, MathF.Min(sb.Y, sc.Y))));
            int y1 = Math.Min(target.Height - 1, (int)MathF.Ceiling(MathF.Max(sa.Y, MathF.Max(sb.Y, sc.Y))));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    float wa = ((sb.X - p.X) * (sc.Y - p.Y) - (sb.Y - p.Y) * (sc.X - p.X)) / area;
                    float wb = ((sc.X - p.X) * (sa.Y - p.Y) - (sc.Y - p.Y) * (sa.X - p.X)) / area;
                    float wc = 1 - wa - wb;
                    if (wa < 0 || wb < 0 || wc < 0)
                        continue;
                    // square on, depth is linear on screen; in perspective its inverse is
                    float iz = camera.Square ? 1 / (wa * a.View.Z + wb * b.View.Z + wc * c.View.Z) : wa * iza + wb * izb + wc * izc;
                    int index = y * target.Width + x;
                    // overlays sit on their face; a little give lets them through the depth test
                    float depth = target.Depth[index];
                    if (overlay ? iz < depth * 0.999f : blend ? iz < depth : iz <= depth)
                        continue;

                    // perspective-correct weights
                    float pa = camera.Square ? wa : wa * iza / iz, pb = camera.Square ? wb : wb * izb / iz, pc = camera.Square ? wc : wc * izc / iz;
                    var shaded = Shade(material, texture, texture2, level, t, pa, pb, pc, a, b, c, camera, out float alpha, out bool discard);
                    if (discard || cutout && (alpha < 0.5f || material.Water))
                        continue;
                    if (blend)
                        target.Set(x, y, shaded, alpha);
                    else
                    {
                        target.Set(x, y, shaded, 1);
                        target.Depth[index] = iz;
                    }
                }
        }

        // ---- shading, as the preview's fragment shader ---------------------------------------

        private Vector3 Shade(ResolvedMaterial m, Texture? texture, Texture? texture2, int level, Tri t, float pa, float pb, float pc,
            Corner a, Corner b, Corner c, BakeCamera camera, out float alpha, out bool discard)
        {
            discard = false;
            alpha = 1;
            var tex = a.Tex * pa + b.Tex * pb + c.Tex * pc;
            var lm = a.Lm * pa + b.Lm * pb + c.Lm * pc;
            var colour = a.Colour * pa + b.Colour * pb + c.Colour * pc;
            float vBlend = a.Blend * pa + b.Blend * pb + c.Blend * pc;
            var tint = new Vector3(m.Color[0], m.Color[1], m.Color[2]);

            Vector3 albedo = t.Prop ? new Vector3(0.6f) : colour;
            bool textured = false;
            if (texture != null && Sample(m.Texture!.Value, texture, level, tex) is { } texel)
            {
                var s = texel;
                if (texture2 != null && Sample(m.Texture2!.Value, texture2, Math.Min(level, texture2.Levels.Count - 1), tex) is { } second)
                    s = Vector4.Lerp(s, second, vBlend);
                albedo = new Vector3(MathF.Pow(s.X, 2.2f), MathF.Pow(s.Y, 2.2f), MathF.Pow(s.Z, 2.2f)) * tint;
                alpha = s.W;
                textured = true;
                if (m.AlphaTest && alpha < 0.5f)
                {
                    discard = true;
                    return default;
                }
            }

            bool lit = t.Prop || (lightmap.Length > 0 && lm.X >= 0);
            Vector3 light = t.Prop ? colour : lit ? Lightmap(lm) : Vector3.One;

            Vector3 result;
            if (m.Water)
            {
                var water = m.WaterColor is { } wc ? new Vector3(wc[0], wc[1], wc[2]) : new Vector3(0.3f, 0.5f, 0.6f);
                result = water * (lit ? light : new Vector3(0.9f));
                alpha = 0.6f;
                textured = true;
            }
            else if (m.Unlit && textured)
                result = albedo;
            else
                result = albedo * light;

            // No fog: the game fogs the picture itself, by how far away its viewer is.

            result = new Vector3(SoftClip(result.X), SoftClip(result.Y), SoftClip(result.Z));
            if (!((m.Translucent || m.Water || t.Overlay) && textured))
                alpha = 1;
            return Display(result);
        }

        private static float SoftClip(float c) => c < 0.85f ? c : 0.85f + 0.15f * (1 - MathF.Exp(-(c - 0.85f) / 0.15f));

        private static Vector3 Display(Vector3 c) =>
            new(MathF.Pow(Math.Clamp(c.X, 0, 1), 1 / 2.2f), MathF.Pow(Math.Clamp(c.Y, 0, 1), 1 / 2.2f), MathF.Pow(Math.Clamp(c.Z, 0, 1), 1 / 2.2f));

        private Vector3 Lightmap(Vector2 uv)
        {
            int w = scene.LightmapWidth, h = scene.LightmapHeight;
            if (w == 0 || h == 0)
                return Vector3.One;
            float fx = Math.Clamp(uv.X * w - 0.5f, 0, w - 1), fy = Math.Clamp(uv.Y * h - 0.5f, 0, h - 1);
            int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
            float tx = fx - x0, ty = fy - y0;
            Vector3 L(int x, int y) { int o = (y * w + x) * 3; return new Vector3(lightmap[o], lightmap[o + 1], lightmap[o + 2]); }
            return Vector3.Lerp(Vector3.Lerp(L(x0, y0), L(x1, y0), tx), Vector3.Lerp(L(x0, y1), L(x1, y1), tx), ty);
        }

        /// <summary>A bilinear, repeating sample of one mip level, 0..1 per channel; null when the texture cannot be decoded.</summary>
        private Vector4? Sample(int id, Texture texture, int level, Vector2 uv)
        {
            var pixels = decoded.GetOrAdd((id, level), key =>
            {
                var l = texture.Levels[key.Level];
                return texture.Format == "rgba8" ? l.Data : Dxt.Decode(texture.Format, l.Width, l.Height, l.Data);
            });
            if (pixels == null)
                return null;
            var lv = texture.Levels[level];
            int w = lv.Width, h = lv.Height;
            float fx = (uv.X - MathF.Floor(uv.X)) * w - 0.5f, fy = (uv.Y - MathF.Floor(uv.Y)) * h - 0.5f;
            int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
            float tx = fx - x0, ty = fy - y0;
            Vector4 P(int x, int y)
            {
                x = ((x % w) + w) % w; y = ((y % h) + h) % h;
                int o = (y * w + x) * 4;
                return o + 3 < pixels.Length ? new Vector4(pixels[o], pixels[o + 1], pixels[o + 2], pixels[o + 3]) / 255f : Vector4.One;
            }
            return Vector4.Lerp(Vector4.Lerp(P(x0, y0), P(x0 + 1, y0), tx), Vector4.Lerp(P(x0, y0 + 1), P(x0 + 1, y0 + 1), tx), ty);
        }

        // ---- the sky -------------------------------------------------------------------------

        private static readonly (string Suffix, Vector3 Normal, Vector3 Up)[] SkyFaces =
        [
            ("rt", Vector3.UnitX, Vector3.UnitZ), ("lf", -Vector3.UnitX, Vector3.UnitZ),
            ("bk", Vector3.UnitY, Vector3.UnitZ), ("ft", -Vector3.UnitY, Vector3.UnitZ),
            ("up", Vector3.UnitZ, -Vector3.UnitX), ("dn", -Vector3.UnitZ, Vector3.UnitX),
        ];

        private Vector3 SkyColour(Vector3 d)
        {
            if (scene.SkyPaint is { } paint)
            {
                float t = MathF.Pow(Math.Clamp(d.Z * 0.5f + 0.5f, 0, 1), MathF.Max(paint.FadeBias, 0.01f));
                var c = Vector3.Lerp(V(paint.BottomColor), V(paint.TopColor), t);
                float sun = MathF.Max(Vector3.Dot(d, Vector3.Normalize(V(paint.SunNormal))), 0);
                c += V(paint.SunColor) * (MathF.Pow(sun, 256f / MathF.Max(paint.SunSize, 0.5f)) * 3 + MathF.Pow(sun, 8) * 0.4f);
                return Display(c);
            }
            if (sky != null)
            {
                var face = SkyFaces.OrderByDescending(f => Vector3.Dot(d, f.Normal)).First();
                if (sky.TryGetValue(face.Suffix, out int id) && id < textures.Count)
                {
                    var p = d / Vector3.Dot(d, face.Normal);
                    var right = Vector3.Cross(face.Normal, face.Up);
                    float u = (Vector3.Dot(p, right) + 1) / 2, v = (1 - Vector3.Dot(p, face.Up)) / 2;
                    var tex = textures[id].Texture;
                    if (Sample(id, tex, 0, new Vector2(Math.Clamp(u, 0.001f, 0.999f), Math.Clamp(v, 0.001f, 0.999f))) is { } s)
                        return new Vector3(s.X, s.Y, s.Z);
                }
            }
            return new Vector3(0.45f, 0.55f, 0.65f);
        }

        private static Vector3 V(float[] a) => new(a[0], a[1], a[2]);
    }
}
