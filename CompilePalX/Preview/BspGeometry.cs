using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CompilePalX.Preview
{
    /// <summary>
    /// A compiled map reduced to what a viewer needs: triangles, a lightmap atlas, and where to stand.
    /// Built by <see cref="BspGeometry.Read"/> from the BSP alone - no game content, no VTFs, no models.
    /// </summary>
    public sealed class PreviewScene
    {
        /// <summary>Floats per vertex in <see cref="Vertices"/>: position, normal, lightmap uv, colour.</summary>
        public const int VertexStride = 11;

        public float[] Vertices { get; init; } = [];
        public uint[] Indices { get; init; } = [];

        /// <summary>RGBA8, <see cref="LightmapWidth"/> by <see cref="LightmapHeight"/>. Empty when the map has no lighting.</summary>
        public byte[] Lightmap { get; init; } = [];
        public int LightmapWidth { get; init; }
        public int LightmapHeight { get; init; }

        /// <summary>"hdr", "ldr" or "none" - which lighting lump the atlas came from.</summary>
        public string LightingMode { get; init; } = "none";

        public float[] Mins { get; init; } = [0, 0, 0];
        public float[] Maxs { get; init; } = [0, 0, 0];

        /// <summary>An info_player_start, if the map has one; a good place to open the camera.</summary>
        public float[]? Spawn { get; init; }

        public int BspVersion { get; init; }
        public int FaceCount { get; init; }
        public int DrawnFaces { get; init; }
        public int SkippedDisplacements { get; init; }
        public int SkippedToolFaces { get; init; }
        public int FacesWithoutLightmap { get; init; }

        public int VertexCount => Vertices.Length / VertexStride;
        public int TriangleCount => Indices.Length / 3;
    }

    /// <summary>
    /// Reads the parts of a Source BSP that describe what it looks like.
    ///
    /// The format is https://developer.valvesoftware.com/wiki/Source_BSP_File_Format. Only the lumps
    /// a renderer needs are touched: vertices, edges, surfedges, faces, planes, texinfo, texdata and
    /// the lighting lump, plus the entity text for a spawn point. Displacements and static props are
    /// not read - they are the two biggest pieces a fuller viewer would add, and both are counted so
    /// the viewer can say they are missing.
    /// </summary>
    public static class BspGeometry
    {
        private const int LumpEntities = 0;
        private const int LumpPlanes = 1;
        private const int LumpTexdata = 2;
        private const int LumpVertices = 3;
        private const int LumpTexinfo = 6;
        private const int LumpFaces = 7;
        private const int LumpLighting = 8;
        private const int LumpEdges = 12;
        private const int LumpSurfedges = 13;
        private const int LumpTexdataStringData = 43;
        private const int LumpTexdataStringTable = 44;
        private const int LumpLightingHdr = 53;
        private const int LumpFacesHdr = 58;

        // texinfo flags: surfaces that never draw in the engine either
        private const int SurfSky2D = 0x2;
        private const int SurfSky = 0x4;
        private const int SurfTrigger = 0x40;
        private const int SurfNoDraw = 0x80;
        private const int SurfHint = 0x100;
        private const int SurfSkip = 0x200;
        private const int SurfBumpLight = 0x800;

        private const int MaxAtlas = 8192;

        private readonly record struct Lump(int Offset, int Length, int Version);

        private readonly record struct Face(
            ushort PlaneNum, int FirstEdge, short NumEdges, short TexInfo, short DispInfo,
            int LightOfs, int LightMinS, int LightMinT, int LightSizeS, int LightSizeT);

        private readonly record struct TexInfo(float[] TextureVecs, float[] LightmapVecs, int Flags, int TexData);

        /// <summary>Reads <paramref name="path"/>. Throws on a file that is not a BSP or is LZMA-compressed.</summary>
        public static PreviewScene Read(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

            int ident = reader.ReadInt32();
            if (ident != 0x50534256) // "VBSP"
                throw new InvalidDataException("Not a Source BSP file.");

            int version = reader.ReadInt32();
            var lumps = ReadLumpTable(reader, version);

            if (LumpBytes(stream, reader, lumps[LumpEntities], 4) is { Length: 4 } head
                && head[0] == 'L' && head[1] == 'Z' && head[2] == 'M' && head[3] == 'A')
                throw new InvalidDataException("The BSP is LZMA-compressed. Preview the map before repacking it, or repack without -compress.");

            var vertices = ReadVectors(stream, reader, lumps[LumpVertices]);
            var edges = ReadEdges(stream, reader, lumps[LumpEdges]);
            var surfedges = ReadInts(stream, reader, lumps[LumpSurfedges]);
            var planes = ReadPlanes(stream, reader, lumps[LumpPlanes]);
            var texinfos = ReadTexInfos(stream, reader, lumps[LumpTexinfo]);
            var materials = ReadMaterialNames(stream, reader, lumps[LumpTexdata], lumps[LumpTexdataStringTable], lumps[LumpTexdataStringData]);

            // HDR when the map has it: a -hdr compile leaves the LDR lump empty, and a -both compile's
            // HDR lump is the better of the two.
            byte[] lighting;
            Face[] faces;
            string lightingMode;

            if (lumps[LumpLightingHdr].Length > 0 && lumps[LumpFacesHdr].Length > 0)
            {
                lighting = LumpBytes(stream, reader, lumps[LumpLightingHdr]);
                faces = ReadFaces(stream, reader, lumps[LumpFacesHdr]);
                lightingMode = "hdr";
            }
            else
            {
                faces = ReadFaces(stream, reader, lumps[LumpFaces]);
                lighting = lumps[LumpLighting].Length > 0 ? LumpBytes(stream, reader, lumps[LumpLighting]) : [];
                lightingMode = lighting.Length > 0 ? "ldr" : "none";
            }

            float[]? spawn = FindSpawn(Encoding.ASCII.GetString(LumpBytes(stream, reader, lumps[LumpEntities])));

            return Build(version, vertices, edges, surfedges, planes, texinfos, materials, faces, lighting, lightingMode, spawn);
        }

        private static PreviewScene Build(
            int version, float[] vertices, ushort[] edges, int[] surfedges, float[] planes,
            TexInfo[] texinfos, string[] materials, Face[] faces, byte[] lighting, string lightingMode, float[]? spawn)
        {
            // pass 1: decide what draws and reserve lightmap space
            var drawn = new List<int>(faces.Length);
            var rects = new List<AtlasRect>();
            int skippedDisp = 0, skippedTool = 0, noLightmap = 0;

            for (int i = 0; i < faces.Length; i++)
            {
                var face = faces[i];
                if (face.NumEdges < 3)
                    continue;

                if (face.DispInfo != -1)
                {
                    skippedDisp++;
                    continue;
                }

                if (face.TexInfo < 0 || face.TexInfo >= texinfos.Length)
                    continue;

                var texinfo = texinfos[face.TexInfo];
                string material = MaterialOf(texinfo, materials);

                if ((texinfo.Flags & (SurfSky | SurfSky2D | SurfNoDraw | SurfTrigger | SurfHint | SurfSkip)) != 0
                    || (material.StartsWith("tools/", StringComparison.OrdinalIgnoreCase) && !material.Contains("black", StringComparison.OrdinalIgnoreCase)))
                {
                    skippedTool++;
                    continue;
                }

                drawn.Add(i);

                if (HasLightmap(face, lighting))
                    rects.Add(new AtlasRect(i, face.LightSizeS + 1, face.LightSizeT + 1));
                else
                    noLightmap++;
            }

            var atlas = Atlas.Pack(rects, MaxAtlas);
            byte[] lightmap = atlas.Width > 0 ? new byte[atlas.Width * atlas.Height * 4] : [];
            var placed = atlas.Placements;

            // pass 2: emit geometry
            var outVerts = new List<float>(drawn.Count * 4 * PreviewScene.VertexStride);
            var outIndices = new List<uint>(drawn.Count * 6);
            var mins = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            var maxs = new[] { float.MinValue, float.MinValue, float.MinValue };

            foreach (int fi in drawn)
            {
                var face = faces[fi];
                var texinfo = texinfos[face.TexInfo];
                string material = MaterialOf(texinfo, materials);
                var colour = ColourFor(material);

                bool lit = placed.TryGetValue(fi, out var place);
                if (lit)
                    BlitLightmap(lighting, face, texinfo, place, atlas.Width, lightmap);

                // the face's corners, in order, as the engine walks them
                var corners = new List<float[]>(face.NumEdges);
                for (int e = 0; e < face.NumEdges; e++)
                {
                    int se = surfedges[face.FirstEdge + e];
                    int edge = Math.Abs(se);
                    int v = se >= 0 ? edges[edge * 2] : edges[edge * 2 + 1];
                    corners.Add([vertices[v * 3], vertices[v * 3 + 1], vertices[v * 3 + 2]]);
                }

                var normal = FaceNormal(corners) ?? PlaneNormal(planes, face.PlaneNum);

                uint baseIndex = (uint)(outVerts.Count / PreviewScene.VertexStride);

                foreach (var p in corners)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        if (p[k] < mins[k]) mins[k] = p[k];
                        if (p[k] > maxs[k]) maxs[k] = p[k];
                    }

                    outVerts.Add(p[0]); outVerts.Add(p[1]); outVerts.Add(p[2]);
                    outVerts.Add(normal[0]); outVerts.Add(normal[1]); outVerts.Add(normal[2]);

                    if (lit)
                    {
                        var (u, v) = LightmapUv(p, texinfo.LightmapVecs, face.LightMinS, face.LightMinT, place, atlas.Width, atlas.Height);
                        outVerts.Add(u); outVerts.Add(v);
                    }
                    else
                    {
                        outVerts.Add(-1); outVerts.Add(-1);
                    }

                    outVerts.Add(colour[0]); outVerts.Add(colour[1]); outVerts.Add(colour[2]);
                }

                foreach (uint index in FanIndices(corners.Count))
                    outIndices.Add(baseIndex + index);
            }

            if (drawn.Count == 0)
            {
                mins = [0, 0, 0];
                maxs = [0, 0, 0];
            }

            return new PreviewScene
            {
                Vertices = outVerts.ToArray(),
                Indices = outIndices.ToArray(),
                Lightmap = lightmap,
                LightmapWidth = atlas.Width,
                LightmapHeight = atlas.Height,
                LightingMode = lightmap.Length == 0 ? "none" : lightingMode,
                Mins = mins,
                Maxs = maxs,
                Spawn = spawn,
                BspVersion = version,
                FaceCount = faces.Length,
                DrawnFaces = drawn.Count,
                SkippedDisplacements = skippedDisp,
                SkippedToolFaces = skippedTool,
                FacesWithoutLightmap = noLightmap + (drawn.Count - noLightmap - placed.Count),
            };
        }

        #region Lump readers

        private static Lump[] ReadLumpTable(BinaryReader reader, int version)
        {
            var lumps = new Lump[64];

            // Left 4 Dead 2 stores the version first in each lump entry; nothing else does. Told apart
            // the same way the packer does: a v21 file whose first int is zero cannot be a valid offset.
            long tableStart = reader.BaseStream.Position;
            bool l4d2 = version == 21 && reader.ReadInt32() == 0;
            reader.BaseStream.Seek(tableStart, SeekOrigin.Begin);

            for (int i = 0; i < 64; i++)
            {
                if (l4d2)
                {
                    int lumpVersion = reader.ReadInt32();
                    int offset = reader.ReadInt32();
                    int length = reader.ReadInt32();
                    reader.ReadInt32(); // fourCC
                    lumps[i] = new Lump(offset, length, lumpVersion);
                }
                else
                {
                    int offset = reader.ReadInt32();
                    int length = reader.ReadInt32();
                    int lumpVersion = reader.ReadInt32();
                    reader.ReadInt32(); // fourCC
                    lumps[i] = new Lump(offset, length, lumpVersion);
                }
            }

            return lumps;
        }

        private static byte[] LumpBytes(Stream stream, BinaryReader reader, Lump lump, int? limit = null)
        {
            if (lump.Length <= 0)
                return [];

            stream.Seek(lump.Offset, SeekOrigin.Begin);
            return reader.ReadBytes(limit is { } n ? Math.Min(n, lump.Length) : lump.Length);
        }

        private static float[] ReadVectors(Stream stream, BinaryReader reader, Lump lump)
        {
            int count = lump.Length / 12;
            var result = new float[count * 3];
            stream.Seek(lump.Offset, SeekOrigin.Begin);
            for (int i = 0; i < result.Length; i++)
                result[i] = reader.ReadSingle();
            return result;
        }

        private static ushort[] ReadEdges(Stream stream, BinaryReader reader, Lump lump)
        {
            int count = lump.Length / 4;
            var result = new ushort[count * 2];
            stream.Seek(lump.Offset, SeekOrigin.Begin);
            for (int i = 0; i < result.Length; i++)
                result[i] = reader.ReadUInt16();
            return result;
        }

        private static int[] ReadInts(Stream stream, BinaryReader reader, Lump lump)
        {
            int count = lump.Length / 4;
            var result = new int[count];
            stream.Seek(lump.Offset, SeekOrigin.Begin);
            for (int i = 0; i < count; i++)
                result[i] = reader.ReadInt32();
            return result;
        }

        /// <summary>Plane normals only, three floats each; the distance and type are not needed.</summary>
        private static float[] ReadPlanes(Stream stream, BinaryReader reader, Lump lump)
        {
            int count = lump.Length / 20;
            var result = new float[count * 3];
            stream.Seek(lump.Offset, SeekOrigin.Begin);
            for (int i = 0; i < count; i++)
            {
                result[i * 3] = reader.ReadSingle();
                result[i * 3 + 1] = reader.ReadSingle();
                result[i * 3 + 2] = reader.ReadSingle();
                reader.ReadSingle(); // dist
                reader.ReadInt32();  // type
            }
            return result;
        }

        private static TexInfo[] ReadTexInfos(Stream stream, BinaryReader reader, Lump lump)
        {
            int count = lump.Length / 72;
            var result = new TexInfo[count];
            stream.Seek(lump.Offset, SeekOrigin.Begin);
            for (int i = 0; i < count; i++)
            {
                var texture = new float[8];
                var lightmap = new float[8];
                for (int k = 0; k < 8; k++) texture[k] = reader.ReadSingle();
                for (int k = 0; k < 8; k++) lightmap[k] = reader.ReadSingle();
                int flags = reader.ReadInt32();
                int texdata = reader.ReadInt32();
                result[i] = new TexInfo(texture, lightmap, flags, texdata);
            }
            return result;
        }

        /// <summary>Material name per texdata entry, lower-cased.</summary>
        private static string[] ReadMaterialNames(Stream stream, BinaryReader reader, Lump texdata, Lump stringTable, Lump stringData)
        {
            int count = texdata.Length / 32;
            var nameIds = new int[count];
            stream.Seek(texdata.Offset, SeekOrigin.Begin);
            for (int i = 0; i < count; i++)
            {
                reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle(); // reflectivity
                nameIds[i] = reader.ReadInt32();
                reader.ReadInt32(); reader.ReadInt32(); reader.ReadInt32(); reader.ReadInt32(); // sizes
            }

            var offsets = ReadInts(stream, reader, stringTable);
            var data = LumpBytes(stream, reader, stringData);

            var names = new string[count];
            for (int i = 0; i < count; i++)
            {
                int id = nameIds[i];
                if (id < 0 || id >= offsets.Length || offsets[id] < 0 || offsets[id] >= data.Length)
                {
                    names[i] = "";
                    continue;
                }

                int start = offsets[id];
                int end = start;
                while (end < data.Length && data[end] != 0)
                    end++;

                names[i] = Encoding.ASCII.GetString(data, start, end - start).ToLowerInvariant();
            }

            return names;
        }

        private static Face[] ReadFaces(Stream stream, BinaryReader reader, Lump lump)
        {
            const int size = 56;
            int count = lump.Length / size;
            var result = new Face[count];
            stream.Seek(lump.Offset, SeekOrigin.Begin);

            for (int i = 0; i < count; i++)
            {
                long start = stream.Position;

                ushort planeNum = reader.ReadUInt16();
                reader.ReadByte(); // side
                reader.ReadByte(); // onNode
                int firstEdge = reader.ReadInt32();
                short numEdges = reader.ReadInt16();
                short texInfo = reader.ReadInt16();
                short dispInfo = reader.ReadInt16();
                reader.ReadInt16(); // surfaceFogVolumeID
                reader.ReadBytes(4); // styles
                int lightOfs = reader.ReadInt32();
                reader.ReadSingle(); // area
                int minS = reader.ReadInt32();
                int minT = reader.ReadInt32();
                int sizeS = reader.ReadInt32();
                int sizeT = reader.ReadInt32();

                result[i] = new Face(planeNum, firstEdge, numEdges, texInfo, dispInfo, lightOfs, minS, minT, sizeS, sizeT);

                stream.Seek(start + size, SeekOrigin.Begin);
            }

            return result;
        }

        #endregion

        #region Geometry helpers

        private static string MaterialOf(TexInfo texinfo, string[] materials) =>
            texinfo.TexData >= 0 && texinfo.TexData < materials.Length ? materials[texinfo.TexData] : "";

        private static bool HasLightmap(Face face, byte[] lighting)
        {
            if (lighting.Length == 0 || face.LightOfs < 0)
                return false;

            long luxels = (long)(face.LightSizeS + 1) * (face.LightSizeT + 1);
            return luxels > 0 && luxels < 1 << 22 && face.LightOfs + luxels * 4 <= lighting.Length;
        }

        private static float[]? FaceNormal(List<float[]> corners)
        {
            // the first non-degenerate triangle decides
            for (int i = 1; i + 1 < corners.Count; i++)
            {
                var a = corners[0];
                var b = corners[i];
                var c = corners[i + 1];

                float ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2];
                float vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];

                float nx = uy * vz - uz * vy;
                float ny = uz * vx - ux * vz;
                float nz = ux * vy - uy * vx;
                float length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);

                if (length > 1e-6f)
                    return [nx / length, ny / length, nz / length];
            }

            return null;
        }

        private static float[] PlaneNormal(float[] planes, int planeNum) =>
            planeNum * 3 + 2 < planes.Length
                ? [planes[planeNum * 3], planes[planeNum * 3 + 1], planes[planeNum * 3 + 2]]
                : [0, 0, 1];

        /// <summary>Triangle fan over a convex polygon of <paramref name="corners"/> vertices.</summary>
        public static IEnumerable<uint> FanIndices(int corners)
        {
            for (uint i = 1; i + 1 < corners; i++)
            {
                yield return 0;
                yield return i;
                yield return i + 1;
            }
        }

        /// <summary>
        /// Where <paramref name="position"/> lands in the atlas.
        ///
        /// The engine's own mapping: the luxel coordinate of a point is the lightmap vector dotted with
        /// the position, plus the vector's offset, minus the face's luxel origin. A face's lightmap has
        /// one more sample than its size in each direction, and samples sit at texel centres, hence the
        /// half. The placement adds a one-texel border so filtering never bleeds a neighbour in.
        /// </summary>
        public static (float U, float V) LightmapUv(float[] position, float[] lightmapVecs, int minS, int minT, AtlasPlacement place, int atlasWidth, int atlasHeight)
        {
            float s = lightmapVecs[0] * position[0] + lightmapVecs[1] * position[1] + lightmapVecs[2] * position[2] + lightmapVecs[3] - minS;
            float t = lightmapVecs[4] * position[0] + lightmapVecs[5] * position[1] + lightmapVecs[6] * position[2] + lightmapVecs[7] - minT;

            return ((place.X + 1 + s + 0.5f) / atlasWidth, (place.Y + 1 + t + 0.5f) / atlasHeight);
        }

        /// <summary>
        /// Copies a face's lightmap into the atlas, decoding each sample, and replicates its edge into
        /// the one-texel border around it.
        /// </summary>
        private static void BlitLightmap(byte[] lighting, Face face, TexInfo texinfo, AtlasPlacement place, int atlasWidth, byte[] atlas)
        {
            int width = face.LightSizeS + 1;
            int height = face.LightSizeT + 1;

            // A bump-mapped face stores four lightmaps per style: the ordinary one first, then one
            // per bump basis. The ordinary one is what an unbumped preview wants, and it comes first
            // either way, so the flag changes nothing here; it is checked so the intent is on record.
            _ = texinfo.Flags & SurfBumpLight;

            for (int y = -1; y <= height; y++)
            {
                int sy = Math.Clamp(y, 0, height - 1);
                for (int x = -1; x <= width; x++)
                {
                    int sx = Math.Clamp(x, 0, width - 1);
                    int sample = face.LightOfs + (sy * width + sx) * 4;

                    var (r, g, b) = DecodeSample(lighting[sample], lighting[sample + 1], lighting[sample + 2], (sbyte)lighting[sample + 3]);

                    int ax = place.X + 1 + x;
                    int ay = place.Y + 1 + y;
                    int o = (ay * atlasWidth + ax) * 4;

                    atlas[o] = r;
                    atlas[o + 1] = g;
                    atlas[o + 2] = b;
                    atlas[o + 3] = 255;
                }
            }
        }

        /// <summary>
        /// A ColorRGBExp32 sample as a display value.
        ///
        /// VRAD writes light as a byte per channel with a shared power-of-two exponent, in linear
        /// units where 255 at exponent 0 is full brightness. Clamped and gamma-encoded here, because
        /// the atlas is 8-bit; the viewer applies a gain on top for maps that are darker than that.
        /// </summary>
        public static (byte R, byte G, byte B) DecodeSample(byte r, byte g, byte b, sbyte exponent)
        {
            float scale = MathF.Pow(2, exponent) / 255f;
            return (ToDisplay(r * scale), ToDisplay(g * scale), ToDisplay(b * scale));
        }

        private static byte ToDisplay(float linear)
        {
            float clamped = Math.Clamp(linear, 0f, 1f);
            return (byte)Math.Round(MathF.Pow(clamped, 1f / 2.2f) * 255f);
        }

        /// <summary>
        /// A colour for a material, from its name. Without textures every surface would be the same
        /// grey; a stable hue per material keeps walls, floors and trims tellable apart, and the same
        /// material the same colour from one compile to the next.
        /// </summary>
        public static float[] ColourFor(string material)
        {
            uint hash = 2166136261;
            foreach (char c in material)
                hash = (hash ^ c) * 16777619;

            float hue = (hash % 360) / 360f;
            float saturation = 0.30f + (hash >> 8) % 20 / 100f;
            float value = 0.80f + (hash >> 16) % 15 / 100f;

            return HsvToRgb(hue, saturation, value);
        }

        private static float[] HsvToRgb(float h, float s, float v)
        {
            float c = v * s;
            float x = c * (1 - MathF.Abs(h * 6 % 2 - 1));
            float m = v - c;

            float r, g, b;
            switch ((int)(h * 6) % 6)
            {
                case 0: r = c; g = x; b = 0; break;
                case 1: r = x; g = c; b = 0; break;
                case 2: r = 0; g = c; b = x; break;
                case 3: r = 0; g = x; b = c; break;
                case 4: r = x; g = 0; b = c; break;
                default: r = c; g = 0; b = x; break;
            }

            return [r + m, g + m, b + m];
        }

        private static readonly Regex SpawnPattern = new(
            @"\{[^}]*""classname""\s*""info_player_start""[^}]*\}",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex OriginPattern = new(
            @"""origin""\s*""\s*(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s*""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>The first info_player_start's origin, or null.</summary>
        public static float[]? FindSpawn(string entities)
        {
            var block = SpawnPattern.Match(entities);
            if (!block.Success)
                return null;

            var origin = OriginPattern.Match(block.Value);
            if (!origin.Success)
                return null;

            try
            {
                return
                [
                    float.Parse(origin.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(origin.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(origin.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
                ];
            }
            catch (FormatException)
            {
                return null;
            }
        }

        #endregion
    }

    /// <summary>A face's lightmap, waiting for a spot in the atlas: its face index and size including the border.</summary>
    public readonly record struct AtlasRect(int Face, int Width, int Height);

    /// <summary>Where a face's lightmap went: the top-left of its bordered cell.</summary>
    public readonly record struct AtlasPlacement(int X, int Y);

    /// <summary>
    /// Packs lightmaps into one square texture, shelf by shelf.
    ///
    /// Nothing clever: rectangles sorted by height, placed left to right along a shelf, a new shelf
    /// when the current one is full. Lightmaps are small and similar in size, so this wastes little,
    /// and it is deterministic. The texture grows in powers of two up to the given maximum; faces
    /// that do not fit after that are left unlit rather than failing the whole preview.
    /// </summary>
    public sealed class Atlas
    {
        public int Width { get; private init; }
        public int Height { get; private init; }
        public IReadOnlyDictionary<int, AtlasPlacement> Placements { get; private init; } = new Dictionary<int, AtlasPlacement>();

        public static Atlas Pack(IReadOnlyList<AtlasRect> rects, int maxSize)
        {
            if (rects.Count == 0)
                return new Atlas { Width = 0, Height = 0 };

            // each cell carries a one-texel border on every side
            long area = rects.Sum(r => (long)(r.Width + 2) * (r.Height + 2));

            int size = 256;
            while ((long)size * size < area * 1.15 && size < maxSize)
                size *= 2;

            for (; size <= maxSize; size *= 2)
            {
                if (TryPack(rects, size, out var placements))
                    return new Atlas { Width = size, Height = size, Placements = placements };
            }

            // the largest allowed, holding whatever fit
            TryPack(rects, maxSize, out var partial);
            return new Atlas { Width = maxSize, Height = maxSize, Placements = partial };
        }

        private static bool TryPack(IReadOnlyList<AtlasRect> rects, int size, out Dictionary<int, AtlasPlacement> placements)
        {
            placements = new Dictionary<int, AtlasPlacement>(rects.Count);

            int shelfY = 0, shelfHeight = 0, x = 0;
            bool all = true;

            foreach (var rect in rects.OrderByDescending(r => r.Height).ThenByDescending(r => r.Width))
            {
                int w = rect.Width + 2;
                int h = rect.Height + 2;

                if (w > size || h > size)
                {
                    all = false;
                    continue;
                }

                if (x + w > size)
                {
                    shelfY += shelfHeight;
                    shelfHeight = 0;
                    x = 0;
                }

                if (shelfY + h > size)
                {
                    all = false;
                    continue;
                }

                placements[rect.Face] = new AtlasPlacement(x, shelfY);
                x += w;
                if (h > shelfHeight)
                    shelfHeight = h;
            }

            return all;
        }
    }
}
