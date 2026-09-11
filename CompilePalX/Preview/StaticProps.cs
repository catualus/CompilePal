using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CompilePalX.Preview
{
    /// <summary>One placed static prop, as the map stores it.</summary>
    public sealed record StaticProp(int Index, string Model, float[] Origin, float[] Angles, int Skin, float Scale, float[] LightingOrigin);

    /// <summary>
    /// Reads the static prop list out of the BSP's game lump.
    ///
    /// The game lump (35) is a table of sub-lumps; the one tagged <c>sprp</c> holds a dictionary of
    /// model names, a leaf list, and one entry per prop. The entry has grown a field or two with
    /// every version from 4 to 11, so its size is worked out from the space left rather than
    /// assumed, and only the fields every version shares are read from fixed offsets. Format:
    /// https://developer.valvesoftware.com/wiki/Source_BSP_File_Format#Static_props.
    /// </summary>
    public static class StaticPropLump
    {
        private const int Sprp = 0x73707270;
        private const int FlagUseLightingOrigin = 0x2;

        /// <summary>
        /// Parses the game lump. <paramref name="gameLump"/> is lump 35 as stored; <paramref name="lumpOffset"/>
        /// where it sits in the file, because sub-lump offsets are absolute; <paramref name="readFile"/>
        /// reads a span of the file, for sub-lumps that lie outside the lump's own bytes.
        /// </summary>
        public static List<StaticProp> Read(byte[] gameLump, int lumpOffset, Func<int, int, byte[]> readFile)
        {
            var props = new List<StaticProp>();
            if (gameLump.Length < 4)
                return props;

            int count = BitConverter.ToInt32(gameLump, 0);
            if (count <= 0 || 4 + count * 16 > gameLump.Length)
                return props;

            // sub-lumps, sorted by offset so a compressed one's length is the gap to the next
            var entries = new List<(int Id, int Flags, int Version, int Offset, int Length)>();
            for (int i = 0; i < count; i++)
            {
                int o = 4 + i * 16;
                entries.Add((BitConverter.ToInt32(gameLump, o), BitConverter.ToUInt16(gameLump, o + 4),
                    BitConverter.ToUInt16(gameLump, o + 6), BitConverter.ToInt32(gameLump, o + 8), BitConverter.ToInt32(gameLump, o + 12)));
            }

            var sprp = entries.FirstOrDefault(e => e.Id == Sprp);
            if (sprp.Id != Sprp || sprp.Length <= 0)
                return props;

            byte[] data;
            if ((sprp.Flags & 1) != 0)
            {
                // compressed: the stored bytes run to the next sub-lump (or the lump's end) and carry
                // the same LZMA header the whole-lump compression uses
                var sorted = entries.Where(e => e.Offset > 0).OrderBy(e => e.Offset).ToList();
                int index = sorted.FindIndex(e => e.Id == Sprp);
                int end = index + 1 < sorted.Count ? sorted[index + 1].Offset : lumpOffset + gameLump.Length;
                var raw = readFile(sprp.Offset, Math.Max(0, end - sprp.Offset));
                data = raw.Length >= 17 && raw[0] == 'L' && raw[1] == 'Z' ? BspGeometry.DecodeLzmaLump(raw) : raw;
            }
            else
            {
                data = readFile(sprp.Offset, sprp.Length);
            }

            if (data.Length < 4)
                return props;

            int p = 0;
            int dictEntries = BitConverter.ToInt32(data, p); p += 4;
            if (dictEntries < 0 || p + dictEntries * 128 > data.Length)
                return props;

            var models = new string[dictEntries];
            for (int i = 0; i < dictEntries; i++)
            {
                int end = p;
                while (end < p + 128 && data[end] != 0)
                    end++;
                models[i] = Encoding.ASCII.GetString(data, p, end - p).Replace('\\', '/').ToLowerInvariant();
                p += 128;
            }

            if (p + 4 > data.Length)
                return props;
            int leafEntries = BitConverter.ToInt32(data, p); p += 4 + Math.Max(0, leafEntries) * 2;

            if (p + 4 > data.Length)
                return props;
            int propEntries = BitConverter.ToInt32(data, p); p += 4;
            if (propEntries <= 0)
                return props;

            int entrySize = (data.Length - p) / propEntries;
            if (entrySize < 56)
                return props;

            for (int i = 0; i < propEntries; i++)
            {
                int o = p + i * entrySize;
                var origin = new[] { BitConverter.ToSingle(data, o), BitConverter.ToSingle(data, o + 4), BitConverter.ToSingle(data, o + 8) };
                var angles = new[] { BitConverter.ToSingle(data, o + 12), BitConverter.ToSingle(data, o + 16), BitConverter.ToSingle(data, o + 20) };
                int type = BitConverter.ToUInt16(data, o + 24);
                int flags = data[o + 31];
                int skin = BitConverter.ToInt32(data, o + 32);
                var lightingOrigin = new[] { BitConverter.ToSingle(data, o + 44), BitConverter.ToSingle(data, o + 48), BitConverter.ToSingle(data, o + 52) };
                float scale = entrySize >= 80 ? BitConverter.ToSingle(data, o + 76) : 1f;
                if (scale <= 0 || float.IsNaN(scale))
                    scale = 1f;

                if (type < 0 || type >= models.Length)
                    continue;

                props.Add(new StaticProp(i, models[type], origin, angles, skin, scale,
                    (flags & FlagUseLightingOrigin) != 0 ? lightingOrigin : origin));
            }

            return props;
        }
    }

    /// <summary>
    /// The per-vertex lighting VRAD bakes for a static prop with <c>-StaticPropLighting</c>, stored
    /// in the map's pak as <c>sp_N.vhv</c> (or <c>sp_hdr_N.vhv</c>).
    ///
    /// A 40-byte header - version, checksum, vertex flags, vertex size, vertex count, mesh count -
    /// then 28-byte mesh headers giving each mesh's LOD, vertex count and offset, then the colours,
    /// one BGRA byte quad per vertex in the mesh's own vertex order.
    /// </summary>
    public static class VertexLighting
    {
        /// <summary>The LOD 0 meshes' colours, in file order, as linear RGB; null when the file is not one.</summary>
        public static List<float[]>? Read(byte[] vhv)
        {
            if (vhv.Length < 40 || BitConverter.ToInt32(vhv, 0) != 2)
                return null;

            int vertexSize = BitConverter.ToInt32(vhv, 12);
            int meshCount = BitConverter.ToInt32(vhv, 20);
            if (vertexSize != 4 || meshCount <= 0 || 40 + meshCount * 28 > vhv.Length)
                return null;

            var result = new List<float[]>();
            for (int m = 0; m < meshCount; m++)
            {
                int h = 40 + m * 28;
                int lod = BitConverter.ToInt32(vhv, h);
                int vertexes = BitConverter.ToInt32(vhv, h + 4);
                int offset = BitConverter.ToInt32(vhv, h + 8);

                if (lod != 0)
                    continue;
                if (vertexes < 0 || offset < 0 || offset + vertexes * 4 > vhv.Length)
                    return null;

                var colours = new float[vertexes * 3];
                for (int v = 0; v < vertexes; v++)
                {
                    int o = offset + v * 4;
                    colours[v * 3] = Decode(vhv[o + 2]);
                    colours[v * 3 + 1] = Decode(vhv[o + 1]);
                    colours[v * 3 + 2] = Decode(vhv[o]);
                }
                result.Add(colours);
            }

            return result;
        }

        /// <summary>
        /// How much brighter a baked vertex colour is than its byte suggests. The engine stores
        /// vertex light with the same headroom as lightmaps and doubles it in the shader.
        /// </summary>
        public const float Overbright = 2f;

        private static float Decode(byte b) => MathF.Pow(b / 255f, 2.2f) * Overbright;
    }

    /// <summary>
    /// The map's ambient light, for a prop with no baked vertex lighting.
    ///
    /// VRAD stores a handful of ambient cubes per leaf - six colours, one per axis direction, at
    /// sample points inside the leaf. The engine lights a prop from the cube nearest its origin,
    /// weighting the six colours by the square of each normal component. Walking the BSP tree
    /// finds the leaf; the planes, nodes and leaves come from lumps 1, 5 and 10, the samples from
    /// 51/55 (HDR) or 52/56.
    /// </summary>
    public sealed class LeafAmbient
    {
        private readonly float[] planes;     // normal(3) + dist per plane
        private readonly int[] nodes;        // planenum, child0, child1 per node
        private readonly short[] leafBounds; // mins(3) maxs(3) per leaf
        private readonly ushort[] index;     // count, first per leaf
        private readonly byte[] samples;     // 28 bytes each

        public bool IsEmpty => nodes.Length == 0 || samples.Length == 0;

        public LeafAmbient(byte[] planeLump, byte[] nodeLump, byte[] leafLump, int leafVersion, byte[] indexLump, byte[] sampleLump)
        {
            int planeCount = planeLump.Length / 20;
            planes = new float[planeCount * 4];
            for (int i = 0; i < planeCount; i++)
                for (int k = 0; k < 4; k++)
                    planes[i * 4 + k] = BitConverter.ToSingle(planeLump, i * 20 + k * 4);

            int nodeCount = nodeLump.Length / 32;
            nodes = new int[nodeCount * 3];
            for (int i = 0; i < nodeCount; i++)
            {
                nodes[i * 3] = BitConverter.ToInt32(nodeLump, i * 32);
                nodes[i * 3 + 1] = BitConverter.ToInt32(nodeLump, i * 32 + 4);
                nodes[i * 3 + 2] = BitConverter.ToInt32(nodeLump, i * 32 + 8);
            }

            int leafSize = leafVersion == 0 ? 56 : 32;
            int leafCount = leafLump.Length / leafSize;
            leafBounds = new short[leafCount * 6];
            for (int i = 0; i < leafCount; i++)
                for (int k = 0; k < 6; k++)
                    leafBounds[i * 6 + k] = BitConverter.ToInt16(leafLump, i * leafSize + 8 + k * 2);

            int indexCount = indexLump.Length / 4;
            index = new ushort[indexCount * 2];
            for (int i = 0; i < indexCount; i++)
            {
                index[i * 2] = BitConverter.ToUInt16(indexLump, i * 4);
                index[i * 2 + 1] = BitConverter.ToUInt16(indexLump, i * 4 + 2);
            }

            samples = sampleLump;
        }

        /// <summary>The leaf containing <paramref name="point"/>, or -1 when the tree is empty.</summary>
        public int LeafAt(float[] point)
        {
            if (nodes.Length == 0)
                return -1;

            int node = 0;
            for (int guard = 0; guard < 4096; guard++)
            {
                int plane = nodes[node * 3];
                if (plane < 0 || plane * 4 + 3 >= planes.Length)
                    return -1;

                float d = planes[plane * 4] * point[0] + planes[plane * 4 + 1] * point[1] + planes[plane * 4 + 2] * point[2] - planes[plane * 4 + 3];
                int child = d >= 0 ? nodes[node * 3 + 1] : nodes[node * 3 + 2];

                if (child < 0)
                    return -1 - child;

                node = child;
                if (node * 3 + 2 >= nodes.Length)
                    return -1;
            }

            return -1;
        }

        /// <summary>
        /// The six ambient colours at <paramref name="point"/> as linear RGB, in the order +X, -X,
        /// +Y, -Y, +Z, -Z, from the nearest sample in its leaf. A dim grey when the map has none.
        /// </summary>
        public float[] CubeAt(float[] point)
        {
            var fallback = Enumerable.Repeat(0.15f, 18).ToArray();

            int leaf = LeafAt(point);
            if (leaf < 0 || leaf * 2 + 1 >= index.Length || leaf * 6 + 5 >= leafBounds.Length)
                return fallback;

            int count = index[leaf * 2];
            int first = index[leaf * 2 + 1];
            if (count == 0)
                return fallback;

            // sample positions are bytes across the leaf's bounds
            float[] mins = [leafBounds[leaf * 6], leafBounds[leaf * 6 + 1], leafBounds[leaf * 6 + 2]];
            float[] maxs = [leafBounds[leaf * 6 + 3], leafBounds[leaf * 6 + 4], leafBounds[leaf * 6 + 5]];

            int best = -1;
            float bestDistance = float.MaxValue;
            for (int s = 0; s < count; s++)
            {
                int o = (first + s) * 28;
                if (o + 28 > samples.Length)
                    break;

                float dx = mins[0] + (maxs[0] - mins[0]) * samples[o + 24] / 255f - point[0];
                float dy = mins[1] + (maxs[1] - mins[1]) * samples[o + 25] / 255f - point[1];
                float dz = mins[2] + (maxs[2] - mins[2]) * samples[o + 26] / 255f - point[2];
                float distance = dx * dx + dy * dy + dz * dz;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = o;
                }
            }

            if (best < 0)
                return fallback;

            var cube = new float[18];
            for (int face = 0; face < 6; face++)
            {
                int o = best + face * 4;
                var (r, g, b) = BspGeometry.LinearSample(samples[o], samples[o + 1], samples[o + 2], (sbyte)samples[o + 3]);
                cube[face * 3] = r;
                cube[face * 3 + 1] = g;
                cube[face * 3 + 2] = b;
            }
            return cube;
        }

        /// <summary>Light on a surface facing <paramref name="normal"/> under an ambient <paramref name="cube"/>.</summary>
        public static float[] Light(float[] cube, float[] normal)
        {
            var result = new float[3];
            for (int axis = 0; axis < 3; axis++)
            {
                float n = normal[axis];
                int face = (n >= 0 ? axis * 2 : axis * 2 + 1) * 3;
                float weight = n * n;
                result[0] += cube[face] * weight;
                result[1] += cube[face + 1] * weight;
                result[2] += cube[face + 2] * weight;
            }
            return result;
        }
    }
}
