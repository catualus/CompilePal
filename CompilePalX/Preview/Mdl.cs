using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CompilePalX.Preview
{
    /// <summary>One mesh of a model: its triangles and the material they use, at LOD 0.</summary>
    public sealed record ModelMesh(
        string Material,
        float[] Positions,   // 3 per vertex
        float[] Normals,     // 3 per vertex
        float[] TexCoords,   // 2 per vertex
        int[] VvdIndices,    // which VVD vertex each mesh vertex came from
        int VertexStart,     // the mesh's first VVD vertex
        uint[] Indices);

    /// <summary>
    /// A Source model reduced to what a viewer needs: LOD 0 triangles per mesh, and the material
    /// each mesh uses under each skin.
    /// </summary>
    public sealed class StudioModel
    {
        public string Path { get; init; } = "";
        public IReadOnlyList<ModelMesh> Meshes { get; init; } = [];
        /// <summary>Material names per skin family; index with the prop's skin, then the mesh's material slot.</summary>
        public IReadOnlyList<IReadOnlyList<string>> Skins { get; init; } = [];
        public int TriangleCount => Meshes.Sum(m => m.Indices.Length / 3);

        private float[]? centre;
        /// <summary>The mean of every vertex, in model space: where the model's body is, as opposed to its origin.</summary>
        public float[] Centre
        {
            get
            {
                if (centre is null)
                {
                    var sum = new float[3];
                    int n = 0;
                    foreach (var mesh in Meshes)
                        for (int i = 0; i + 2 < mesh.Positions.Length; i += 3, n++)
                            for (int k = 0; k < 3; k++)
                                sum[k] += mesh.Positions[i + k];
                    centre = n == 0 ? [0, 0, 0] : [sum[0] / n, sum[1] / n, sum[2] / n];
                }
                return centre;
            }
        }
        public int VertexCount => Meshes.Sum(m => m.Positions.Length / 3);

        /// <summary>The material a mesh uses under a skin, or its default when the skin is out of range.</summary>
        public string MaterialFor(ModelMesh mesh, int skin)
        {
            int slot = Meshes.ToList().IndexOf(mesh);
            if (Skins.Count == 0)
                return mesh.Material;

            var family = Skins[Math.Clamp(skin, 0, Skins.Count - 1)];
            return family.Count > 0 ? family[Math.Clamp(materialSlots[slot], 0, family.Count - 1)] : mesh.Material;
        }

        internal int[] materialSlots = [];
    }

    /// <summary>
    /// Reads a model from its three files.
    ///
    /// A Source model is an MDL (the skeleton, materials, skins and the mesh list), a VVD (the
    /// vertices) and a VTX (the triangle strips, per hardware level). Only what a static prop
    /// needs is read: LOD 0 of the first model of each body part, with materials resolved through
    /// the model's texture directories. Formats: https://developer.valvesoftware.com/wiki/MDL,
    /// https://developer.valvesoftware.com/wiki/VVD, https://developer.valvesoftware.com/wiki/VTX.
    /// </summary>
    public static class Mdl
    {
        private const int MdlIdent = 0x54534449; // "IDST"
        private const int VvdIdent = 0x56534449; // "IDSV"

        /// <summary>
        /// Loads <paramref name="mdlPath"/> (<c>models/props/thing.mdl</c>) through <paramref name="read"/>,
        /// which supplies file bytes by relative path or null. Null when any of the three files is
        /// missing or unreadable.
        /// </summary>
        public static StudioModel? Load(string mdlPath, Func<string, byte[]?> read)
        {
            string stem = mdlPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) ? mdlPath[..^4] : mdlPath;

            var mdl = read(stem + ".mdl");
            if (mdl is null || mdl.Length < 244 || BitConverter.ToInt32(mdl, 0) != MdlIdent)
                return null;

            var vvd = read(stem + ".vvd");
            if (vvd is null || vvd.Length < 64 || BitConverter.ToInt32(vvd, 0) != VvdIdent)
                return null;

            var vtx = read(stem + ".dx90.vtx") ?? read(stem + ".dx80.vtx") ?? read(stem + ".sw.vtx");
            if (vtx is null || vtx.Length < 36)
                return null;

            try
            {
                return Parse(mdlPath, mdl, vvd, vtx, read);
            }
            catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or ArgumentException or InvalidDataException or IOException)
            {
                // a truncated or unusual file; better no prop than a crash
                return null;
            }
        }

        private static StudioModel Parse(string path, byte[] mdl, byte[] vvd, byte[] vtx, Func<string, byte[]?> read)
        {
            int version = BitConverter.ToInt32(mdl, 4);
            int numTextures = BitConverter.ToInt32(mdl, 204);
            int textureIndex = BitConverter.ToInt32(mdl, 208);
            int numCdTextures = BitConverter.ToInt32(mdl, 212);
            int cdTextureIndex = BitConverter.ToInt32(mdl, 216);
            int numSkinRef = BitConverter.ToInt32(mdl, 220);
            int numSkinFamilies = BitConverter.ToInt32(mdl, 224);
            int skinIndex = BitConverter.ToInt32(mdl, 228);
            int numBodyParts = BitConverter.ToInt32(mdl, 232);
            int bodyPartIndex = BitConverter.ToInt32(mdl, 236);

            // material names, each joined with the first of the model's texture directories that has it
            var directories = new List<string>();
            for (int i = 0; i < numCdTextures; i++)
            {
                int offset = BitConverter.ToInt32(mdl, cdTextureIndex + i * 4);
                directories.Add(CString(mdl, offset).Replace('\\', '/').Trim('/'));
            }
            if (directories.Count == 0)
                directories.Add("");
            var qualified = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string Qualify(string texture)
            {
                if (!qualified.TryGetValue(texture, out var name))
                    qualified[texture] = name = QualifyIn(texture, directories, read);
                return name;
            }

            var textures = new List<string>();
            for (int i = 0; i < numTextures; i++)
            {
                int o = textureIndex + i * 64;
                int nameOffset = BitConverter.ToInt32(mdl, o);
                string name = CString(mdl, o + nameOffset).Replace('\\', '/').Trim('/');
                textures.Add(name);
            }

            // skin families: a table of texture indices, one row per skin
            var skins = new List<IReadOnlyList<string>>();
            for (int f = 0; f < numSkinFamilies; f++)
            {
                var family = new List<string>();
                for (int r = 0; r < numSkinRef; r++)
                {
                    int t = BitConverter.ToInt16(mdl, skinIndex + (f * numSkinRef + r) * 2);
                    family.Add(t >= 0 && t < textures.Count ? Qualify(textures[t]) : "");
                }
                skins.Add(family);
            }

            // vertices, with the LOD fixups applied
            var vertices = ReadVvd(vvd);

            // VTX layout
            int vtxVersion = BitConverter.ToInt32(vtx, 0);
            if (vtxVersion != 7)
                throw new InvalidDataException($"VTX version {vtxVersion} is not supported.");
            int vtxBodyParts = BitConverter.ToInt32(vtx, 28);
            int vtxBodyPartOffset = BitConverter.ToInt32(vtx, 32);
            bool extended = version >= 49; // strip and strip group headers grew by 8 bytes
            int stripGroupSize = extended ? 33 : 25;
            int stripSize = extended ? 35 : 27;

            var meshes = new List<ModelMesh>();
            var slots = new List<int>();

            for (int bp = 0; bp < Math.Min(numBodyParts, vtxBodyParts); bp++)
            {
                int bodyPart = bodyPartIndex + bp * 16;
                int numModels = BitConverter.ToInt32(mdl, bodyPart + 4);
                int modelIndex = BitConverter.ToInt32(mdl, bodyPart + 12);
                if (numModels <= 0)
                    continue;

                int vtxBodyPart = vtxBodyPartOffset + bp * 8;
                int vtxModelOffset = BitConverter.ToInt32(vtx, vtxBodyPart + 4);

                // the first model of the body part: static props do not switch body groups
                int model = bodyPart + modelIndex;
                int numMeshes = BitConverter.ToInt32(mdl, model + 72);
                int meshIndex = BitConverter.ToInt32(mdl, model + 76);
                int modelVertexIndex = BitConverter.ToInt32(mdl, model + 84) / 48;

                int vtxModel = vtxBodyPart + vtxModelOffset;
                int numLods = BitConverter.ToInt32(vtx, vtxModel);
                int lodOffset = BitConverter.ToInt32(vtx, vtxModel + 4);
                if (numLods <= 0)
                    continue;

                int lod = vtxModel + lodOffset; // LOD 0
                int lodMeshes = BitConverter.ToInt32(vtx, lod);
                int lodMeshOffset = BitConverter.ToInt32(vtx, lod + 4);

                for (int m = 0; m < Math.Min(numMeshes, lodMeshes); m++)
                {
                    int mesh = model + meshIndex + m * 116;
                    int materialSlot = BitConverter.ToInt32(mdl, mesh);
                    int meshVertexOffset = BitConverter.ToInt32(mdl, mesh + 12);

                    int vtxMesh = lod + lodMeshOffset + m * 9;
                    int numStripGroups = BitConverter.ToInt32(vtx, vtxMesh);
                    int stripGroupOffset = BitConverter.ToInt32(vtx, vtxMesh + 4);

                    var positions = new List<float>();
                    var normals = new List<float>();
                    var texcoords = new List<float>();
                    var vvdIndices = new List<int>();
                    var indices = new List<uint>();

                    for (int sg = 0; sg < numStripGroups; sg++)
                    {
                        int group = vtxMesh + stripGroupOffset + sg * stripGroupSize;
                        int numVerts = BitConverter.ToInt32(vtx, group);
                        int vertOffset = BitConverter.ToInt32(vtx, group + 4);
                        int numIndices = BitConverter.ToInt32(vtx, group + 8);
                        int indexOffset = BitConverter.ToInt32(vtx, group + 12);
                        int numStrips = BitConverter.ToInt32(vtx, group + 16);
                        int stripOffset = BitConverter.ToInt32(vtx, group + 20);

                        uint baseIndex = (uint)(positions.Count / 3);

                        for (int v = 0; v < numVerts; v++)
                        {
                            int vert = group + vertOffset + v * 9;
                            int origMeshVertId = BitConverter.ToUInt16(vtx, vert + 4);
                            int vvdIndex = modelVertexIndex + meshVertexOffset + origMeshVertId;

                            if (vvdIndex < 0 || vvdIndex >= vertices.Count)
                                vvdIndex = 0;

                            var (p, n, t) = vertices.Count > 0 ? vertices[vvdIndex] : (new float[3], new float[] { 0, 0, 1 }, new float[2]);
                            positions.AddRange(p);
                            normals.AddRange(n);
                            texcoords.AddRange(t);
                            vvdIndices.Add(vvdIndex);
                        }

                        for (int s = 0; s < numStrips; s++)
                        {
                            int strip = group + stripOffset + s * stripSize;
                            int stripIndices = BitConverter.ToInt32(vtx, strip);
                            int stripIndexOffset = BitConverter.ToInt32(vtx, strip + 4);
                            byte flags = vtx[strip + 18];

                            var list = new List<uint>(stripIndices);
                            for (int i = 0; i < stripIndices; i++)
                                list.Add(BitConverter.ToUInt16(vtx, group + indexOffset + (stripIndexOffset + i) * 2));

                            if ((flags & 2) != 0)
                            {
                                // a triangle strip: every window of three is a triangle, alternating winding
                                for (int i = 0; i + 2 < list.Count; i++)
                                {
                                    if ((i & 1) == 0)
                                        indices.AddRange([baseIndex + list[i], baseIndex + list[i + 1], baseIndex + list[i + 2]]);
                                    else
                                        indices.AddRange([baseIndex + list[i + 1], baseIndex + list[i], baseIndex + list[i + 2]]);
                                }
                            }
                            else
                            {
                                foreach (uint i in list)
                                    indices.Add(baseIndex + i);
                            }
                        }
                    }

                    if (indices.Count == 0)
                        continue;

                    string material = skins.Count > 0 && materialSlot >= 0 && materialSlot < skins[0].Count
                        ? skins[0][materialSlot]
                        : materialSlot >= 0 && materialSlot < textures.Count ? Qualify(textures[materialSlot]) : "";

                    meshes.Add(new ModelMesh(material, positions.ToArray(), normals.ToArray(), texcoords.ToArray(), vvdIndices.ToArray(), modelVertexIndex + meshVertexOffset, indices.ToArray()));
                    slots.Add(materialSlot);
                }
            }

            return new StudioModel { Path = path, Meshes = meshes, Skins = skins, materialSlots = slots.ToArray() };
        }

        /// <summary>
        /// The LOD 0 vertices of a VVD: position, normal, texture coordinate. Fixups reorder the
        /// stored vertices per LOD; for LOD 0 every fixup applies, in order.
        /// </summary>
        private static List<(float[] Position, float[] Normal, float[] TexCoord)> ReadVvd(byte[] vvd)
        {
            int numFixups = BitConverter.ToInt32(vvd, 48);
            int fixupStart = BitConverter.ToInt32(vvd, 52);
            int vertexStart = BitConverter.ToInt32(vvd, 56);
            int numLod0 = BitConverter.ToInt32(vvd, 16);

            (float[], float[], float[]) Vertex(int index)
            {
                int o = vertexStart + index * 48;
                if (o + 48 > vvd.Length)
                    return (new float[3], new float[] { 0, 0, 1 }, new float[2]);
                return (
                    [BitConverter.ToSingle(vvd, o + 16), BitConverter.ToSingle(vvd, o + 20), BitConverter.ToSingle(vvd, o + 24)],
                    [BitConverter.ToSingle(vvd, o + 28), BitConverter.ToSingle(vvd, o + 32), BitConverter.ToSingle(vvd, o + 36)],
                    [BitConverter.ToSingle(vvd, o + 40), BitConverter.ToSingle(vvd, o + 44)]);
            }

            var result = new List<(float[], float[], float[])>();

            if (numFixups <= 0)
            {
                for (int i = 0; i < numLod0; i++)
                    result.Add(Vertex(i));
                return result;
            }

            for (int f = 0; f < numFixups; f++)
            {
                int o = fixupStart + f * 12;
                int lod = BitConverter.ToInt32(vvd, o);
                int source = BitConverter.ToInt32(vvd, o + 4);
                int count = BitConverter.ToInt32(vvd, o + 8);
                if (lod < 0)
                    continue;
                for (int i = 0; i < count; i++)
                    result.Add(Vertex(source + i));
            }

            return result;
        }

        /// <summary>
        /// A material name as a path under materials/: the engine tries each $cdmaterials directory
        /// in order and takes the first that has the VMT, so this does the same, settling for the
        /// first directory when none has it.
        /// </summary>
        private static string QualifyIn(string texture, List<string> directories, Func<string, byte[]?> read)
        {
            string? first = null;
            foreach (var dir in directories)
            {
                string name = dir.Length == 0 ? texture : $"{dir}/{texture}";
                first ??= name;
                if (read($"materials/{name}.vmt") is not null)
                    return name;
            }
            return first ?? texture;
        }

        private static string CString(byte[] bytes, int offset)
        {
            if (offset < 0 || offset >= bytes.Length)
                return "";
            int end = offset;
            while (end < bytes.Length && bytes[end] != 0)
                end++;
            return Encoding.ASCII.GetString(bytes, offset, end - offset);
        }
    }
}
