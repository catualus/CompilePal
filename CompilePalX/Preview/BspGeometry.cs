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
        public bool Compressed { get; init; }
        public int FaceCount { get; init; }
        public int DrawnFaces { get; init; }
        public int DrawnDisplacements { get; init; }
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
    /// a renderer needs are touched: vertices, edges, surfedges, faces, planes, texinfo, texdata,
    /// displacements and the lighting lump, plus the entity text for a spawn point. Static props are
    /// not read; they live in the game lump and need the models, which are not in the BSP.
    ///
    /// Lumps that bspzip compressed with <c>-compress</c> are inflated on the way in, so a repacked
    /// map previews the same as the one it was made from.
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
        private const int LumpDispInfo = 26;
        private const int LumpDispVerts = 33;
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

        private const int MaxAtlas = 8192;

        private readonly record struct Lump(int Offset, int Length, int Version);

        private readonly record struct Face(
            ushort PlaneNum, int FirstEdge, short NumEdges, short TexInfo, short DispInfo,
            int LightOfs, int LightMinS, int LightMinT, int LightSizeS, int LightSizeT);

        private readonly record struct TexInfo(float[] TextureVecs, float[] LightmapVecs, int Flags, int TexData);

        /// <summary>One displacement: which face it replaces, where its grid starts, how fine it is.</summary>
        private readonly record struct DispInfo(float[] StartPosition, int VertStart, int Power, int MapFace);

        /// <summary>Reads <paramref name="path"/>. Throws on a file that is not a BSP.</summary>
        public static PreviewScene Read(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

            int ident = reader.ReadInt32();
            if (ident != 0x50534256) // "VBSP"
                throw new InvalidDataException("Not a Source BSP file.");

            int version = reader.ReadInt32();
            var lumps = ReadLumpTable(reader, version);

            bool compressed = false;
            byte[] Data(int index)
            {
                var raw = RawLump(stream, reader, lumps[index]);
                if (!IsLzma(raw))
                    return raw;

                compressed = true;
                return DecodeLzmaLump(raw);
            }

            var vertices = Floats(Data(LumpVertices));
            var edges = UShorts(Data(LumpEdges));
            var surfedges = Ints(Data(LumpSurfedges));
            var planes = ReadPlanes(Data(LumpPlanes));
            var texinfos = ReadTexInfos(Data(LumpTexinfo));
            var materials = ReadMaterialNames(Data(LumpTexdata), Data(LumpTexdataStringTable), Data(LumpTexdataStringData));

            // HDR when the map has it: a -hdr compile leaves the LDR lump empty, and a -both compile's
            // HDR lump is the better of the two.
            byte[] lighting;
            Face[] faces;
            string lightingMode;

            if (lumps[LumpLightingHdr].Length > 0 && lumps[LumpFacesHdr].Length > 0)
            {
                lighting = Data(LumpLightingHdr);
                faces = ReadFaces(Data(LumpFacesHdr));
                lightingMode = "hdr";
            }
            else
            {
                faces = ReadFaces(Data(LumpFaces));
                lighting = lumps[LumpLighting].Length > 0 ? Data(LumpLighting) : [];
                lightingMode = lighting.Length > 0 ? "ldr" : "none";
            }

            var dispInfos = ReadDispInfos(Data(LumpDispInfo));
            var dispVerts = Floats(Data(LumpDispVerts)); // 5 floats each: vector, distance, alpha

            float[]? spawn = FindSpawn(Encoding.ASCII.GetString(Data(LumpEntities)))
                             ?? FindSpawnInLumpFile(path);

            return Build(version, compressed, vertices, edges, surfedges, planes, texinfos, materials, faces, lighting, lightingMode, spawn, dispInfos, dispVerts);
        }

        private static PreviewScene Build(
            int version, bool compressed, float[] vertices, ushort[] edges, int[] surfedges, float[] planes,
            TexInfo[] texinfos, string[] materials, Face[] faces, byte[] lighting, string lightingMode, float[]? spawn,
            DispInfo[] dispInfos, float[] dispVerts)
        {
            // pass 1: decide what draws and reserve lightmap space
            var drawn = new List<int>(faces.Length);
            var drawnDisps = new List<int>(dispInfos.Length);
            var rects = new List<AtlasRect>();
            int skippedDisp = 0, skippedTool = 0, noLightmap = 0;

            for (int i = 0; i < faces.Length; i++)
            {
                var face = faces[i];
                if (face.NumEdges < 3 || face.DispInfo != -1)
                    continue;

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

            for (int d = 0; d < dispInfos.Length; d++)
            {
                var disp = dispInfos[d];
                int side = (1 << disp.Power) + 1;

                bool usable = disp.MapFace >= 0 && disp.MapFace < faces.Length
                              && faces[disp.MapFace].NumEdges == 4
                              && disp.Power is >= 0 and <= 4
                              && disp.VertStart >= 0
                              && (disp.VertStart + side * side) * 5 <= dispVerts.Length;

                if (!usable)
                {
                    skippedDisp++;
                    continue;
                }

                drawnDisps.Add(d);

                var face = faces[disp.MapFace];
                if (HasLightmap(face, lighting))
                    rects.Add(new AtlasRect(disp.MapFace, face.LightSizeS + 1, face.LightSizeT + 1));
                else
                    noLightmap++;
            }

            var atlas = Atlas.Pack(rects, MaxAtlas);
            byte[] lightmap = atlas.Width > 0 ? new byte[atlas.Width * atlas.Height * 4] : [];
            var placed = atlas.Placements;

            // pass 2: emit geometry
            var outVerts = new List<float>((drawn.Count * 4 + drawnDisps.Count * 81) * PreviewScene.VertexStride);
            var outIndices = new List<uint>(drawn.Count * 6 + drawnDisps.Count * 384);
            var mins = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            var maxs = new[] { float.MinValue, float.MinValue, float.MinValue };

            List<float[]> Corners(Face face)
            {
                var corners = new List<float[]>(face.NumEdges);
                for (int e = 0; e < face.NumEdges; e++)
                {
                    int se = surfedges[face.FirstEdge + e];
                    int edge = Math.Abs(se);
                    int v = se >= 0 ? edges[edge * 2] : edges[edge * 2 + 1];
                    corners.Add([vertices[v * 3], vertices[v * 3 + 1], vertices[v * 3 + 2]]);
                }
                return corners;
            }

            void Emit(float[] p, float[] normal, float u, float v, float[] colour)
            {
                for (int k = 0; k < 3; k++)
                {
                    if (p[k] < mins[k]) mins[k] = p[k];
                    if (p[k] > maxs[k]) maxs[k] = p[k];
                }

                outVerts.Add(p[0]); outVerts.Add(p[1]); outVerts.Add(p[2]);
                outVerts.Add(normal[0]); outVerts.Add(normal[1]); outVerts.Add(normal[2]);
                outVerts.Add(u); outVerts.Add(v);
                outVerts.Add(colour[0]); outVerts.Add(colour[1]); outVerts.Add(colour[2]);
            }

            foreach (int fi in drawn)
            {
                var face = faces[fi];
                var texinfo = texinfos[face.TexInfo];
                var colour = ColourFor(MaterialOf(texinfo, materials));

                bool lit = placed.TryGetValue(fi, out var place);
                if (lit)
                    BlitLightmap(lighting, face, place, atlas.Width, lightmap);

                var corners = Corners(face);
                var normal = FaceNormal(corners) ?? PlaneNormal(planes, face.PlaneNum);
                uint baseIndex = (uint)(outVerts.Count / PreviewScene.VertexStride);

                foreach (var p in corners)
                {
                    var (u, v) = lit
                        ? LightmapUv(p, texinfo.LightmapVecs, face.LightMinS, face.LightMinT, place, atlas.Width, atlas.Height)
                        : (-1f, -1f);
                    Emit(p, normal, u, v, colour);
                }

                foreach (uint index in FanIndices(corners.Count))
                    outIndices.Add(baseIndex + index);
            }

            foreach (int d in drawnDisps)
            {
                var disp = dispInfos[d];
                var face = faces[disp.MapFace];
                var texinfo = face.TexInfo >= 0 && face.TexInfo < texinfos.Length ? texinfos[face.TexInfo] : default;
                var colour = ColourFor(texinfo.TextureVecs is null ? "displacement" : MaterialOf(texinfo, materials));

                bool lit = placed.TryGetValue(disp.MapFace, out var place);
                if (lit)
                    BlitLightmap(lighting, face, place, atlas.Width, lightmap);

                var corners = Corners(face);
                var grid = BuildDisplacement(corners, disp.StartPosition, disp.Power, dispVerts, disp.VertStart);
                int side = (1 << disp.Power) + 1;
                uint baseIndex = (uint)(outVerts.Count / PreviewScene.VertexStride);

                for (int i = 0; i < side; i++)
                    for (int j = 0; j < side; j++)
                    {
                        int n = i * side + j;
                        float u = -1, v = -1;
                        if (lit)
                        {
                            // VRAD samples a displacement on its own grid: luxel s runs along the
                            // columns and t along the rows, over the face's whole lightmap.
                            u = (place.X + 1 + (float)j / (side - 1) * face.LightSizeS + 0.5f) / atlas.Width;
                            v = (place.Y + 1 + (float)i / (side - 1) * face.LightSizeT + 0.5f) / atlas.Height;
                        }
                        Emit(grid.Positions[n], grid.Normals[n], u, v, colour);
                    }

                foreach (uint index in grid.Indices)
                    outIndices.Add(baseIndex + index);
            }

            if (outVerts.Count == 0)
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
                Compressed = compressed,
                FaceCount = faces.Length,
                DrawnFaces = drawn.Count,
                DrawnDisplacements = drawnDisps.Count,
                SkippedDisplacements = skippedDisp,
                SkippedToolFaces = skippedTool,
                FacesWithoutLightmap = noLightmap + (drawn.Count + drawnDisps.Count - noLightmap - placed.Count),
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

        private static byte[] RawLump(Stream stream, BinaryReader reader, Lump lump)
        {
            if (lump.Length <= 0 || lump.Offset < 0 || lump.Offset + lump.Length > stream.Length)
                return [];

            stream.Seek(lump.Offset, SeekOrigin.Begin);
            return reader.ReadBytes(lump.Length);
        }

        private static bool IsLzma(byte[] raw) =>
            raw.Length >= 17 && raw[0] == 'L' && raw[1] == 'Z' && raw[2] == 'M' && raw[3] == 'A';

        /// <summary>
        /// Inflates a lump that bspzip compressed.
        ///
        /// A compressed lump starts with a 17-byte header - the letters LZMA, the inflated size, the
        /// compressed size, and the five LZMA property bytes - followed by a raw LZMA stream with no
        /// container of its own. The decoder is the reference one from the 7-Zip SDK.
        /// </summary>
        public static byte[] DecodeLzmaLump(byte[] raw)
        {
            if (!IsLzma(raw))
                throw new InvalidDataException("Not an LZMA lump.");

            uint actualSize = BitConverter.ToUInt32(raw, 4);
            uint lzmaSize = BitConverter.ToUInt32(raw, 8);
            var properties = raw.AsSpan(12, 5).ToArray();

            if (17 + lzmaSize > raw.Length)
                throw new InvalidDataException("The compressed lump is shorter than its header says.");

            var decoder = new SevenZip.Compression.LZMA.Decoder();
            decoder.SetDecoderProperties(properties);

            using var input = new MemoryStream(raw, 17, (int)lzmaSize, writable: false);
            using var output = new MemoryStream((int)actualSize);
            decoder.Code(input, output, lzmaSize, actualSize, null);

            return output.ToArray();
        }

        private static float[] Floats(byte[] data)
        {
            var result = new float[data.Length / 4];
            Buffer.BlockCopy(data, 0, result, 0, result.Length * 4);
            return result;
        }

        private static ushort[] UShorts(byte[] data)
        {
            var result = new ushort[data.Length / 2];
            Buffer.BlockCopy(data, 0, result, 0, result.Length * 2);
            return result;
        }

        private static int[] Ints(byte[] data)
        {
            var result = new int[data.Length / 4];
            Buffer.BlockCopy(data, 0, result, 0, result.Length * 4);
            return result;
        }

        /// <summary>Plane normals only, three floats each; the distance and type are not needed.</summary>
        private static float[] ReadPlanes(byte[] data)
        {
            int count = data.Length / 20;
            var result = new float[count * 3];
            for (int i = 0; i < count; i++)
            {
                result[i * 3] = BitConverter.ToSingle(data, i * 20);
                result[i * 3 + 1] = BitConverter.ToSingle(data, i * 20 + 4);
                result[i * 3 + 2] = BitConverter.ToSingle(data, i * 20 + 8);
            }
            return result;
        }

        private static TexInfo[] ReadTexInfos(byte[] data)
        {
            int count = data.Length / 72;
            var result = new TexInfo[count];
            for (int i = 0; i < count; i++)
            {
                int o = i * 72;
                var texture = new float[8];
                var lightmap = new float[8];
                for (int k = 0; k < 8; k++) texture[k] = BitConverter.ToSingle(data, o + k * 4);
                for (int k = 0; k < 8; k++) lightmap[k] = BitConverter.ToSingle(data, o + 32 + k * 4);
                result[i] = new TexInfo(texture, lightmap, BitConverter.ToInt32(data, o + 64), BitConverter.ToInt32(data, o + 68));
            }
            return result;
        }

        /// <summary>Material name per texdata entry, lower-cased.</summary>
        private static string[] ReadMaterialNames(byte[] texdata, byte[] stringTable, byte[] stringData)
        {
            int count = texdata.Length / 32;
            var offsets = Ints(stringTable);
            var names = new string[count];

            for (int i = 0; i < count; i++)
            {
                int id = BitConverter.ToInt32(texdata, i * 32 + 12);
                if (id < 0 || id >= offsets.Length || offsets[id] < 0 || offsets[id] >= stringData.Length)
                {
                    names[i] = "";
                    continue;
                }

                int start = offsets[id];
                int end = start;
                while (end < stringData.Length && stringData[end] != 0)
                    end++;

                names[i] = Encoding.ASCII.GetString(stringData, start, end - start).ToLowerInvariant();
            }

            return names;
        }

        private static Face[] ReadFaces(byte[] data)
        {
            const int size = 56;
            int count = data.Length / size;
            var result = new Face[count];

            for (int i = 0; i < count; i++)
            {
                int o = i * size;
                result[i] = new Face(
                    PlaneNum: BitConverter.ToUInt16(data, o),
                    FirstEdge: BitConverter.ToInt32(data, o + 4),
                    NumEdges: BitConverter.ToInt16(data, o + 8),
                    TexInfo: BitConverter.ToInt16(data, o + 10),
                    DispInfo: BitConverter.ToInt16(data, o + 12),
                    LightOfs: BitConverter.ToInt32(data, o + 20),
                    LightMinS: BitConverter.ToInt32(data, o + 28),
                    LightMinT: BitConverter.ToInt32(data, o + 32),
                    LightSizeS: BitConverter.ToInt32(data, o + 36),
                    LightSizeT: BitConverter.ToInt32(data, o + 40));
            }

            return result;
        }

        private static DispInfo[] ReadDispInfos(byte[] data)
        {
            const int size = 176;
            int count = data.Length / size;
            var result = new DispInfo[count];

            for (int i = 0; i < count; i++)
            {
                int o = i * size;
                result[i] = new DispInfo(
                    StartPosition: [BitConverter.ToSingle(data, o), BitConverter.ToSingle(data, o + 4), BitConverter.ToSingle(data, o + 8)],
                    VertStart: BitConverter.ToInt32(data, o + 12),
                    Power: BitConverter.ToInt32(data, o + 20),
                    MapFace: BitConverter.ToUInt16(data, o + 36));
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

        private static float[]? FaceNormal(IReadOnlyList<float[]> corners)
        {
            // the first non-degenerate triangle decides
            for (int i = 1; i + 1 < corners.Count; i++)
            {
                var n = Cross(Sub(corners[i], corners[0]), Sub(corners[i + 1], corners[0]));
                float length = Length(n);
                if (length > 1e-6f)
                    return [n[0] / length, n[1] / length, n[2] / length];
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

        /// <summary>A displacement's mesh: one position and normal per grid point, and the triangles over them.</summary>
        public sealed record DisplacementMesh(float[][] Positions, float[][] Normals, uint[] Indices);

        /// <summary>
        /// Builds a displacement surface the way the engine does.
        ///
        /// The four corners of the face are rotated so the one nearest the displacement's start
        /// position comes first. Row i runs from corner 0 towards corner 1 on the left and from
        /// corner 3 towards corner 2 on the right; column j interpolates between the two. Each grid
        /// point is that base point moved along its stored vector by its stored distance. Triangles
        /// alternate their diagonal from quad to quad, as the engine's do.
        /// </summary>
        public static DisplacementMesh BuildDisplacement(IReadOnlyList<float[]> faceCorners, float[] startPosition, int power, float[] dispVerts, int vertStart)
        {
            if (faceCorners.Count != 4)
                throw new ArgumentException("A displacement sits on a four-sided face.", nameof(faceCorners));

            // rotate so the corner nearest the start position is first
            int start = 0;
            float best = float.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                float d = Length(Sub(faceCorners[k], startPosition));
                if (d < best)
                {
                    best = d;
                    start = k;
                }
            }

            var p = new float[4][];
            for (int k = 0; k < 4; k++)
                p[k] = faceCorners[(start + k) % 4];

            int side = (1 << power) + 1;
            var positions = new float[side * side][];

            for (int i = 0; i < side; i++)
            {
                float ti = (float)i / (side - 1);
                var left = Lerp(p[0], p[1], ti);
                var right = Lerp(p[3], p[2], ti);

                for (int j = 0; j < side; j++)
                {
                    float tj = (float)j / (side - 1);
                    var basePoint = Lerp(left, right, tj);

                    int v = (vertStart + i * side + j) * 5;
                    float dist = dispVerts[v + 3];
                    positions[i * side + j] =
                    [
                        basePoint[0] + dispVerts[v] * dist,
                        basePoint[1] + dispVerts[v + 1] * dist,
                        basePoint[2] + dispVerts[v + 2] * dist,
                    ];
                }
            }

            // smooth normals from the grid's own tangents, facing the same way as the base face
            var faceNormal = FaceNormal(faceCorners) ?? new float[] { 0, 0, 1 };
            var normals = new float[side * side][];
            for (int i = 0; i < side; i++)
                for (int j = 0; j < side; j++)
                {
                    var alongI = Sub(positions[Math.Min(i + 1, side - 1) * side + j], positions[Math.Max(i - 1, 0) * side + j]);
                    var alongJ = Sub(positions[i * side + Math.Min(j + 1, side - 1)], positions[i * side + Math.Max(j - 1, 0)]);
                    var n = Cross(alongJ, alongI);
                    float length = Length(n);
                    if (length < 1e-6f)
                        n = faceNormal;
                    else
                        n = [n[0] / length, n[1] / length, n[2] / length];

                    if (n[0] * faceNormal[0] + n[1] * faceNormal[1] + n[2] * faceNormal[2] < 0)
                        n = [-n[0], -n[1], -n[2]];

                    normals[i * side + j] = n;
                }

            var indices = new List<uint>((side - 1) * (side - 1) * 6);
            for (int i = 0; i + 1 < side; i++)
                for (int j = 0; j + 1 < side; j++)
                {
                    uint a = (uint)(i * side + j);
                    uint b = (uint)(i * side + j + 1);
                    uint c = (uint)((i + 1) * side + j + 1);
                    uint d = (uint)((i + 1) * side + j);

                    if (((i + j) & 1) == 0)
                    {
                        indices.AddRange([a, b, c, a, c, d]);
                    }
                    else
                    {
                        indices.AddRange([a, b, d, b, c, d]);
                    }
                }

            return new DisplacementMesh(positions, normals, indices.ToArray());
        }

        private static float[] Sub(float[] a, float[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
        private static float[] Lerp(float[] a, float[] b, float t) => [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t];
        private static float[] Cross(float[] a, float[] b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
        private static float Length(float[] v) => MathF.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);

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
        /// the one-texel border around it. A bump-mapped face stores four lightmaps per style with the
        /// ordinary one first, which is the one wanted here, so the flag needs no special handling.
        /// </summary>
        private static void BlitLightmap(byte[] lighting, Face face, AtlasPlacement place, int atlasWidth, byte[] atlas)
        {
            int width = face.LightSizeS + 1;
            int height = face.LightSizeT + 1;

            for (int y = -1; y <= height; y++)
            {
                int sy = Math.Clamp(y, 0, height - 1);
                for (int x = -1; x <= width; x++)
                {
                    int sx = Math.Clamp(x, 0, width - 1);
                    int sample = face.LightOfs + (sy * width + sx) * 4;

                    var (r, g, b) = DecodeSample(lighting[sample], lighting[sample + 1], lighting[sample + 2], (sbyte)lighting[sample + 3]);

                    int o = ((place.Y + 1 + y) * atlasWidth + place.X + 1 + x) * 4;
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

        /// <summary>
        /// The spawn point from the entity lump file beside the map, for a map whose entities the
        /// ENTLUMP step moved out of the BSP. A .lmp is a 20-byte header - lump offset, id, version,
        /// length and map revision - followed by the lump's bytes, which for the entity lump is text.
        /// </summary>
        public static float[]? FindSpawnInLumpFile(string bspPath)
        {
            string lumpFile = Path.Combine(Path.GetDirectoryName(bspPath) ?? "", Path.GetFileNameWithoutExtension(bspPath) + "_l_0.lmp");

            try
            {
                if (!File.Exists(lumpFile))
                    return null;

                var bytes = File.ReadAllBytes(lumpFile);
                if (bytes.Length <= 20)
                    return null;

                int offset = BitConverter.ToInt32(bytes, 0);
                int length = BitConverter.ToInt32(bytes, 12);
                if (offset < 20 || offset > bytes.Length)
                    offset = 20;
                if (length <= 0 || offset + length > bytes.Length)
                    length = bytes.Length - offset;

                return FindSpawn(Encoding.ASCII.GetString(bytes, offset, length));
            }
            catch (IOException)
            {
                return null;
            }
        }

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
