using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CompilePalX.Preview
{
    /// <summary>A run of the index buffer drawn with one material. Skybox runs are drawn first, scaled, and behind everything.</summary>
    public sealed record DrawBatch(int Material, int First, int Count, bool Skybox = false);

    /// <summary>An overlay: four corners in world space, their texture coordinates, its material and which way it faces.</summary>
    public sealed record Overlay(float[][] Corners, float[][] TexCoords, int Material, float[] Normal, bool Skybox);

    /// <summary>
    /// A compiled map reduced to what a viewer needs: triangles grouped by material, a lightmap
    /// atlas, the material names to go and find textures for, and where to stand. Built by
    /// <see cref="BspGeometry.Read"/> from the BSP alone.
    /// </summary>
    public sealed class PreviewScene
    {
        /// <summary>Floats per vertex: position, normal, lightmap uv, texture uv, blend, colour.</summary>
        public const int VertexStride = 14;

        public float[] Vertices { get; init; } = [];
        public uint[] Indices { get; init; } = [];
        public IReadOnlyList<DrawBatch> Batches { get; init; } = [];

        /// <summary>Material names by texdata index, which is what batches refer to.</summary>
        public IReadOnlyList<string> MaterialNames { get; init; } = [];

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

        /// <summary>worldspawn's skyname, for the 2D skybox.</summary>
        public string? SkyName { get; init; }

        /// <summary>Garry's Mod's procedural sky, from env_skypaint, when the map has one.</summary>
        public BspGeometry.SkyPaint? SkyPaint { get; init; }

        /// <summary>The pakfile lump as stored: a zip of the content packed into the map.</summary>
        public byte[] PakLump { get; init; } = [];

        /// <summary>Every static prop the map places, from the game lump.</summary>
        public IReadOnlyList<StaticProp> StaticProps { get; init; } = [];

        /// <summary>The map's ambient light by position, for props with no baked vertex lighting.</summary>
        public LeafAmbient? Ambient { get; init; }

        /// <summary>Models placed by entities - doors, dynamic and physics props - drawn where they start.</summary>
        public IReadOnlyList<StaticProp> EntityProps { get; init; } = [];

        /// <summary>The 3D skybox's camera and scale, when the map has one.</summary>
        public BspGeometry.SkyCamera? Sky3D { get; init; }

        /// <summary>Distance fog from env_fog_controller, when enabled.</summary>
        public BspGeometry.Fog? Fog { get; init; }

        /// <summary>Overlays - decals placed in Hammer - as quads with the material they show.</summary>
        public IReadOnlyList<Overlay> Overlays { get; init; } = [];

        public int SkyboxFaces { get; init; }

        /// <summary>The map area holding the 3D skybox, or -1.</summary>
        public int SkyboxArea { get; init; } = -1;

        public int BspVersion { get; init; }
        public bool Compressed { get; init; }
        public int FaceCount { get; init; }
        public int DrawnFaces { get; init; }
        public int DrawnDisplacements { get; init; }
        public int SkippedDisplacements { get; init; }
        public int SkippedToolFaces { get; init; }
        public int FacesWithoutLightmap { get; init; }
        public int PlacedBrushEntities { get; init; }

        public int VertexCount => Vertices.Length / VertexStride;
        public int TriangleCount => Indices.Length / 3;
    }

    /// <summary>
    /// Reads the parts of a Source BSP that describe what it looks like.
    ///
    /// The format is https://developer.valvesoftware.com/wiki/Source_BSP_File_Format. Only the lumps
    /// a renderer needs are touched: vertices, edges, surfedges, faces, planes, texinfo, texdata,
    /// models, displacements and the lighting lump, plus the entity text for a spawn point, the sky
    /// name and where brush entities sit. Static props are not read; they live in the game lump
    /// and need the models, which are not in the BSP.
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
        private const int LumpModels = 14;
        private const int LumpDispInfo = 26;
        private const int LumpDispVerts = 33;
        private const int LumpPakfile = 40;
        private const int LumpTexdataStringData = 43;
        private const int LumpTexdataStringTable = 44;
        private const int LumpLightingHdr = 53;
        private const int LumpFacesHdr = 58;
        private const int LumpNodes = 5;
        private const int LumpLeafs = 10;
        private const int LumpGameLump = 35;
        private const int LumpLeafAmbientIndexHdr = 51;
        private const int LumpLeafAmbientIndex = 52;
        private const int LumpLeafAmbientLightingHdr = 55;
        private const int LumpLeafAmbientLighting = 56;
        private const int LumpLeafFaces = 16;
        private const int LumpOverlays = 45;

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

        private readonly record struct TexData(string Name, int Width, int Height);

        /// <summary>One displacement: which face it replaces, where its grid starts, how fine it is.</summary>
        private readonly record struct DispInfo(float[] StartPosition, int VertStart, int Power, int MapFace);

        /// <summary>A brush model: the faces it owns. Model 0 is the world.</summary>
        private readonly record struct Model(int FirstFace, int NumFaces);

        /// <summary>Where a brush entity put its model: a translation and a rotation.</summary>
        public readonly record struct Placement(float[] Origin, float[] Angles);

        /// <summary>sky_camera: where the 3D skybox is built, and how much smaller than the world it is.</summary>
        public sealed record SkyCamera(float[] Origin, float Scale);

        /// <summary>env_fog_controller, as the engine reads it: linear colour, start and end distances.</summary>
        public sealed record Fog(float[] Color, float Start, float End, float MaxDensity);

        /// <summary>The colours env_skypaint paints the sky with. Linear 0..1, as the entity stores them.</summary>
        public sealed record SkyPaint(float[] TopColor, float[] BottomColor, float FadeBias, float[] SunColor, float[] SunNormal, float SunSize);

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
            var texdata = ReadTexData(Data(LumpTexdata), Data(LumpTexdataStringTable), Data(LumpTexdataStringData));
            var models = ReadModels(Data(LumpModels));

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

            // ENTLUMP moves the entities out of the BSP into a file beside it; when that file exists
            // it is the map's entity list and the lump inside is empty or a stub
            string entityText = ReadEntityLumpFile(path) ?? Encoding.ASCII.GetString(Data(LumpEntities));
            var entities = ParseEntities(entityText);

            float[]? spawn = FindSpawn(entityText);
            string? skyName = entities.FirstOrDefault(e => e.GetValueOrDefault("classname") == "worldspawn")?.GetValueOrDefault("skyname");

            var placements = BrushPlacements(entities);
            var skyPaint = ReadSkyPaint(entities);

            // the pak lump is a zip and is read by the content locator later; kept as stored
            var pak = RawLump(stream, reader, lumps[LumpPakfile]);

            // static props, whose sub-lump offsets are absolute in the file
            List<StaticProp> staticProps;
            try
            {
                staticProps = StaticPropLump.Read(RawLump(stream, reader, lumps[LumpGameLump]), lumps[LumpGameLump].Offset,
                    (offset, length) => RawLump(stream, reader, new Lump(offset, length, 0)));
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
                staticProps = [];
            }

            // ambient light per leaf, HDR when the map has it
            bool hdrAmbient = lightingMode == "hdr" && lumps[LumpLeafAmbientLightingHdr].Length > 0;
            var ambient = new LeafAmbient(
                Data(LumpPlanes), Data(LumpNodes), Data(LumpLeafs), lumps[LumpLeafs].Version,
                Data(hdrAmbient ? LumpLeafAmbientIndexHdr : LumpLeafAmbientIndex),
                Data(hdrAmbient ? LumpLeafAmbientLightingHdr : LumpLeafAmbientLighting),
                Data(LumpLeafFaces));

            var sky3D = ReadSkyCamera(entities);
            var fog = ReadFog(entities);
            var entityProps = EntityProps(entities);
            var overlayData = Data(LumpOverlays);

            return Build(version, compressed, vertices, edges, surfedges, planes, texinfos, texdata, models, placements,
                faces, lighting, lightingMode, spawn, skyName, skyPaint, pak, staticProps, entityProps, ambient, sky3D, fog, overlayData, dispInfos, dispVerts);
        }

        private static PreviewScene Build(
            int version, bool compressed, float[] vertices, ushort[] edges, int[] surfedges, float[] planes,
            TexInfo[] texinfos, TexData[] texdata, Model[] models, Dictionary<int, Placement> placements,
            Face[] faces, byte[] lighting, string lightingMode, float[]? spawn, string? skyName, SkyPaint? skyPaint, byte[] pak,
            List<StaticProp> staticProps, List<StaticProp> entityProps, LeafAmbient ambient, SkyCamera? sky3D, Fog? fog, byte[] overlayData,
            DispInfo[] dispInfos, float[] dispVerts)
        {
            // the 3D skybox is the area the sky_camera sits in; its faces are drawn scaled up around it
            var skyboxFaces = new HashSet<int>();
            int skyArea = -1;
            if (sky3D is not null)
            {
                skyArea = ambient.AreaOf(ambient.LeafAt(sky3D.Origin));
                if (skyArea > 0)
                    skyboxFaces = ambient.FacesInArea(skyArea);
                else
                    sky3D = null;
            }

            float[] ToWorld(float[] p, bool skybox) =>
                skybox && sky3D is not null
                    ? [(p[0] - sky3D.Origin[0]) * sky3D.Scale, (p[1] - sky3D.Origin[1]) * sky3D.Scale, (p[2] - sky3D.Origin[2]) * sky3D.Scale]
                    : p;

            // which brush model each face belongs to, for the entities that moved theirs
            var faceModel = new int[faces.Length];
            for (int m = 1; m < models.Length; m++)
                for (int f = models[m].FirstFace; f < models[m].FirstFace + models[m].NumFaces && f < faces.Length; f++)
                    if (f >= 0)
                        faceModel[f] = m;

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
                string material = MaterialOf(texinfo, texdata);

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

            // pass 2: emit geometry, one material at a time so the viewer draws each in one call
            var outVerts = new List<float>((drawn.Count * 4 + drawnDisps.Count * 81) * PreviewScene.VertexStride);
            var outIndices = new List<uint>(drawn.Count * 6 + drawnDisps.Count * 384);
            var batches = new List<DrawBatch>();
            var mins = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            var maxs = new[] { float.MinValue, float.MinValue, float.MinValue };
            var placedEntities = new HashSet<int>();

            int TexDataOf(Face face) => face.TexInfo >= 0 && face.TexInfo < texinfos.Length ? texinfos[face.TexInfo].TexData : -1;

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

            void Emit(float[] p, float[] normal, float lu, float lv, float tu, float tv, float blend, float[] colour)
            {
                for (int k = 0; k < 3; k++)
                {
                    if (p[k] < mins[k]) mins[k] = p[k];
                    if (p[k] > maxs[k]) maxs[k] = p[k];
                }

                outVerts.Add(p[0]); outVerts.Add(p[1]); outVerts.Add(p[2]);
                outVerts.Add(normal[0]); outVerts.Add(normal[1]); outVerts.Add(normal[2]);
                outVerts.Add(lu); outVerts.Add(lv);
                outVerts.Add(tu); outVerts.Add(tv);
                outVerts.Add(blend);
                outVerts.Add(colour[0]); outVerts.Add(colour[1]); outVerts.Add(colour[2]);
            }

            // faces and displacements grouped by material, so each group is one draw
            var byMaterial = drawn.Select(f => (Face: f, Disp: -1))
                .Concat(drawnDisps.Select(d => (Face: dispInfos[d].MapFace, Disp: d)))
                .GroupBy(x => (Material: TexDataOf(faces[x.Face]), Skybox: skyboxFaces.Contains(x.Face)))
                .OrderBy(g => g.Key.Skybox ? 0 : 1).ThenBy(g => g.Key.Material);

            int skyboxFaceCount = 0;
            foreach (var group in byMaterial)
            {
                int batchStart = outIndices.Count;
                bool inSkybox = group.Key.Skybox;
                var colour = ColourFor(group.Key.Material >= 0 && group.Key.Material < texdata.Length ? texdata[group.Key.Material].Name : "");

                foreach (var (fi, di) in group)
                {
                    var face = faces[fi];
                    var texinfo = texinfos[face.TexInfo];
                    var tex = texinfo.TexData >= 0 && texinfo.TexData < texdata.Length ? texdata[texinfo.TexData] : new TexData("", 64, 64);

                    bool lit = placed.TryGetValue(fi, out var place);
                    if (lit)
                        BlitLightmap(lighting, face, place, atlas.Width, lightmap);

                    placements.TryGetValue(faceModel[fi], out var placement);
                    bool moved = faceModel[fi] > 0 && placements.ContainsKey(faceModel[fi]);
                    if (moved)
                        placedEntities.Add(faceModel[fi]);

                    var corners = Corners(face);
                    uint baseIndex = (uint)(outVerts.Count / PreviewScene.VertexStride);

                    if (di < 0)
                    {
                        var normal = FaceNormal(corners) ?? PlaneNormal(planes, face.PlaneNum);
                        var worldNormal = moved ? Rotate(normal, placement.Angles) : normal;

                        foreach (var p in corners)
                        {
                            // lightmap and texture coordinates belong to the model's own space
                            var (lu, lv) = lit
                                ? LightmapUv(p, texinfo.LightmapVecs, face.LightMinS, face.LightMinT, place, atlas.Width, atlas.Height)
                                : (-1f, -1f);
                            var (tu, tv) = TextureUv(p, texinfo.TextureVecs, tex.Width, tex.Height);
                            var world = ToWorld(moved ? Place(p, placement) : p, inSkybox);
                            Emit(world, worldNormal, lu, lv, tu, tv, 0, colour);
                        }

                        foreach (uint index in FanIndices(corners.Count))
                            outIndices.Add(baseIndex + index);
                    }
                    else
                    {
                        var disp = dispInfos[di];
                        var grid = BuildDisplacement(corners, disp.StartPosition, disp.Power, dispVerts, disp.VertStart);
                        int side = (1 << disp.Power) + 1;

                        for (int i = 0; i < side; i++)
                            for (int j = 0; j < side; j++)
                            {
                                int n = i * side + j;
                                float lu = -1, lv = -1;
                                if (lit)
                                {
                                    // VRAD samples a displacement on its own grid: luxel s runs along
                                    // the columns and t along the rows, over the face's whole lightmap.
                                    lu = (place.X + 1 + (float)j / (side - 1) * face.LightSizeS + 0.5f) / atlas.Width;
                                    lv = (place.Y + 1 + (float)i / (side - 1) * face.LightSizeT + 0.5f) / atlas.Height;
                                }
                                var (tu, tv) = TextureUv(grid.Positions[n], texinfo.TextureVecs, tex.Width, tex.Height);
                                float alpha = dispVerts[(disp.VertStart + n) * 5 + 4] / 255f;
                                Emit(ToWorld(grid.Positions[n], inSkybox), grid.Normals[n], lu, lv, tu, tv, alpha, colour);
                            }

                        foreach (uint index in grid.Indices)
                            outIndices.Add(baseIndex + index);
                    }
                }

                if (outIndices.Count > batchStart)
                    batches.Add(new DrawBatch(group.Key.Material, batchStart, outIndices.Count - batchStart, inSkybox));
                if (inSkybox)
                    skyboxFaceCount += group.Count();
            }

            var overlays = ReadOverlays(overlayData, faces, texinfos, skyboxFaces, ToWorld);

            if (outVerts.Count == 0)
            {
                mins = [0, 0, 0];
                maxs = [0, 0, 0];
            }

            return new PreviewScene
            {
                Vertices = outVerts.ToArray(),
                Indices = outIndices.ToArray(),
                Batches = batches,
                MaterialNames = texdata.Select(t => t.Name).ToList(),
                Lightmap = lightmap,
                LightmapWidth = atlas.Width,
                LightmapHeight = atlas.Height,
                LightingMode = lightmap.Length == 0 ? "none" : lightingMode,
                Mins = mins,
                Maxs = maxs,
                Spawn = spawn,
                SkyName = skyName,
                SkyPaint = skyPaint,
                PakLump = pak,
                StaticProps = staticProps,
                EntityProps = entityProps,
                Ambient = ambient,
                Sky3D = sky3D,
                Fog = fog,
                Overlays = overlays,
                SkyboxFaces = skyboxFaceCount,
                SkyboxArea = sky3D is null ? -1 : skyArea,
                BspVersion = version,
                Compressed = compressed,
                FaceCount = faces.Length,
                DrawnFaces = drawn.Count,
                DrawnDisplacements = drawnDisps.Count,
                SkippedDisplacements = skippedDisp,
                SkippedToolFaces = skippedTool,
                FacesWithoutLightmap = noLightmap + (drawn.Count + drawnDisps.Count - noLightmap - placed.Count),
                PlacedBrushEntities = placedEntities.Count,
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

        /// <summary>Material name and texture size per texdata entry, names lower-cased.</summary>
        private static TexData[] ReadTexData(byte[] texdata, byte[] stringTable, byte[] stringData)
        {
            int count = texdata.Length / 32;
            var offsets = Ints(stringTable);
            var result = new TexData[count];

            for (int i = 0; i < count; i++)
            {
                int o = i * 32;
                int id = BitConverter.ToInt32(texdata, o + 12);
                int width = Math.Max(1, BitConverter.ToInt32(texdata, o + 16));
                int height = Math.Max(1, BitConverter.ToInt32(texdata, o + 20));

                string name = "";
                if (id >= 0 && id < offsets.Length && offsets[id] >= 0 && offsets[id] < stringData.Length)
                {
                    int start = offsets[id];
                    int end = start;
                    while (end < stringData.Length && stringData[end] != 0)
                        end++;
                    name = Encoding.ASCII.GetString(stringData, start, end - start).ToLowerInvariant().Replace('\\', '/');
                }

                result[i] = new TexData(name, width, height);
            }

            return result;
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

        private static Model[] ReadModels(byte[] data)
        {
            const int size = 48;
            int count = data.Length / size;
            var result = new Model[count];
            for (int i = 0; i < count; i++)
                result[i] = new Model(BitConverter.ToInt32(data, i * size + 40), BitConverter.ToInt32(data, i * size + 44));
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

        #region Entities

        private static readonly Regex EntityBlock = new(@"\{([^{}]*)\}", RegexOptions.Compiled);
        private static readonly Regex EntityPair = new(@"""([^""]*)""\s*""([^""]*)""", RegexOptions.Compiled);

        /// <summary>The entity lump as a list of key-value maps, one per entity, keys lower-cased.</summary>
        public static List<Dictionary<string, string>> ParseEntities(string text)
        {
            var result = new List<Dictionary<string, string>>();
            foreach (Match block in EntityBlock.Matches(text))
            {
                var entity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match pair in EntityPair.Matches(block.Groups[1].Value))
                    entity.TryAdd(pair.Groups[1].Value.ToLowerInvariant(), pair.Groups[2].Value);
                if (entity.Count > 0)
                    result.Add(entity);
            }
            return result;
        }

        /// <summary>
        /// Where each brush entity put its model.
        ///
        /// A brush entity's geometry is stored around its own origin, and the engine places it with the
        /// entity's "origin" and "angles" keys - a func_door at 512 0 0 has its faces stored near 0 0 0.
        /// Drawing the faces as stored puts every moved brush entity in the wrong place.
        /// </summary>
        public static Dictionary<int, Placement> BrushPlacements(IEnumerable<Dictionary<string, string>> entities)
        {
            var result = new Dictionary<int, Placement>();

            foreach (var entity in entities)
            {
                if (!entity.TryGetValue("model", out var model) || !model.StartsWith('*'))
                    continue;
                if (!int.TryParse(model.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || index <= 0)
                    continue;

                var origin = ParseVector(entity.GetValueOrDefault("origin"));
                var angles = ParseVector(entity.GetValueOrDefault("angles"));

                if (origin.All(v => v == 0) && angles.All(v => v == 0))
                    continue;

                result[index] = new Placement(origin, angles);
            }

            return result;
        }

        /// <summary>
        /// Garry's Mod paints its default sky from an env_skypaint entity rather than textures: a
        /// gradient between two colours, biased, with a sun. The stock maps all use it.
        /// </summary>
        public static SkyPaint? ReadSkyPaint(IEnumerable<Dictionary<string, string>> entities)
        {
            var paint = entities.FirstOrDefault(e => string.Equals(e.GetValueOrDefault("classname"), "env_skypaint", StringComparison.OrdinalIgnoreCase));
            if (paint is null)
                return null;

            float Number(string key, float fallback) =>
                paint.TryGetValue(key, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : fallback;

            float[] Colour(string key, float[] fallback) => paint.ContainsKey(key) ? ParseVector(paint[key]) : fallback;

            return new SkyPaint(
                Colour("topcolor", [0.2f, 0.5f, 1f]),
                Colour("bottomcolor", [0.8f, 1f, 1f]),
                Number("fadebias", 1f),
                Colour("suncolor", [0.2f, 0.1f, 0f]),
                Colour("sunnormal", [0.4f, 0f, 1f]),
                Number("sunsize", 2f));
        }

        /// <summary>The sky_camera, or null. Its scale is how many world units one skybox unit stands for; 16 when unsaid.</summary>
        public static SkyCamera? ReadSkyCamera(IEnumerable<Dictionary<string, string>> entities)
        {
            var camera = entities.FirstOrDefault(e => string.Equals(e.GetValueOrDefault("classname"), "sky_camera", StringComparison.OrdinalIgnoreCase));
            if (camera is null)
                return null;

            float scale = camera.TryGetValue("scale", out var s) && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && f > 0 ? f : 16f;
            return new SkyCamera(ParseVector(camera.GetValueOrDefault("origin")), scale);
        }

        /// <summary>The map's fog, when an env_fog_controller has it enabled. Colour is "r g b" in bytes.</summary>
        public static Fog? ReadFog(IEnumerable<Dictionary<string, string>> entities)
        {
            var controller = entities.FirstOrDefault(e => string.Equals(e.GetValueOrDefault("classname"), "env_fog_controller", StringComparison.OrdinalIgnoreCase));
            if (controller is null || controller.GetValueOrDefault("fogenable") != "1")
                return null;

            float Number(string key, float fallback) =>
                controller.TryGetValue(key, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : fallback;

            var bytes = ParseVector(controller.GetValueOrDefault("fogcolor") ?? "255 255 255");
            var colour = bytes.Select(b => MathF.Pow(Math.Clamp(b / 255f, 0f, 1f), 2.2f)).ToArray();

            float start = Number("fogstart", 0f), end = Number("fogend", 4000f);
            if (end <= start)
                return null;

            return new Fog(colour, start, end, Math.Clamp(Number("fogmaxdensity", 1f), 0f, 1f));
        }

        private static readonly HashSet<string> PropClasses = new(StringComparer.OrdinalIgnoreCase)
        {
            "prop_dynamic", "prop_dynamic_override", "prop_physics", "prop_physics_override", "prop_physics_multiplayer",
            "prop_door_rotating", "prop_dynamic_ornament", "prop_vehicle_jeep", "prop_vehicle_airboat", "prop_vehicle_prisoner_pod",
            "cycler", "monster_generic", "prop_ragdoll",
        };

        /// <summary>
        /// Models placed by entities: doors, dynamic and physics props, drawn where the map starts
        /// them. An RP map keeps most of its doors and furniture this way rather than as static props.
        /// Lit from the ambient cubes, as the engine lights them before anything moves.
        /// </summary>
        public static List<StaticProp> EntityProps(IEnumerable<Dictionary<string, string>> entities)
        {
            var result = new List<StaticProp>();
            foreach (var entity in entities)
            {
                if (!PropClasses.Contains(entity.GetValueOrDefault("classname") ?? ""))
                    continue;
                if (!entity.TryGetValue("model", out var model) || !model.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                    continue;
                // rendermode 10 is "don't render"; an invisible prop is not part of the picture
                if (entity.GetValueOrDefault("rendermode") == "10")
                    continue;

                int skin = int.TryParse(entity.GetValueOrDefault("skin"), out int s) ? s : 0;
                float scale = float.TryParse(entity.GetValueOrDefault("modelscale"), NumberStyles.Float, CultureInfo.InvariantCulture, out float ms) && ms > 0 ? ms : 1f;
                var origin = ParseVector(entity.GetValueOrDefault("origin"));

                result.Add(new StaticProp(-1, model.Replace('\\', '/').ToLowerInvariant(), origin, ParseVector(entity.GetValueOrDefault("angles")), skin, scale, origin));
            }
            return result;
        }

        /// <summary>
        /// Overlays as quads. Each has an origin, a normal, and four corners given in a basis the
        /// engine builds from the texture axis of the first face it sits on and that normal (the
        /// overlay's own texinfo is a placeholder VBSP writes only for the material), with texture
        /// coordinates from its U and V ranges. Lifted a little off the surface so they draw on top
        /// of it. Layout: https://developer.valvesoftware.com/wiki/Source_BSP_File_Format#Overlay.
        /// </summary>
        private static List<Overlay> ReadOverlays(byte[] data, Face[] faces, TexInfo[] texinfos, HashSet<int> skyboxFaces, Func<float[], bool, float[]> toWorld)
        {
            const int size = 352;
            var result = new List<Overlay>();

            for (int i = 0; i + size <= data.Length; i += size)
            {
                int texinfo = BitConverter.ToInt16(data, i + 4);
                if (texinfo < 0 || texinfo >= texinfos.Length)
                    continue;

                int faceCount = BitConverter.ToUInt16(data, i + 6) & 0x3fff;
                int firstFace = faceCount > 0 ? BitConverter.ToInt32(data, i + 8) : -1;
                if (firstFace < 0 || firstFace >= faces.Length)
                    continue;
                int faceTexinfo = faces[firstFace].TexInfo;
                if (faceTexinfo < 0 || faceTexinfo >= texinfos.Length)
                    continue;
                bool skybox = skyboxFaces.Contains(firstFace);

                float u0 = BitConverter.ToSingle(data, i + 264), u1 = BitConverter.ToSingle(data, i + 268);
                float v0 = BitConverter.ToSingle(data, i + 272), v1 = BitConverter.ToSingle(data, i + 276);

                var points = new float[4][];
                for (int k = 0; k < 4; k++)
                    points[k] = [BitConverter.ToSingle(data, i + 280 + k * 12), BitConverter.ToSingle(data, i + 284 + k * 12), BitConverter.ToSingle(data, i + 288 + k * 12)];

                float[] origin = [BitConverter.ToSingle(data, i + 328), BitConverter.ToSingle(data, i + 332), BitConverter.ToSingle(data, i + 336)];
                float[] normal = [BitConverter.ToSingle(data, i + 340), BitConverter.ToSingle(data, i + 344), BitConverter.ToSingle(data, i + 348)];

                // basis: the face texture's S axis projected off the normal, the normal, and their cross
                var s = texinfos[faceTexinfo].TextureVecs;
                float[] axis = [s[0], s[1], s[2]];
                float along = axis[0] * normal[0] + axis[1] * normal[1] + axis[2] * normal[2];
                axis = [axis[0] - normal[0] * along, axis[1] - normal[1] * along, axis[2] - normal[2] * along];
                float length = Length(axis);
                if (length < 1e-6f)
                    continue;
                axis = [axis[0] / length, axis[1] / length, axis[2] / length];
                var cross = Cross(normal, axis);

                var corners = new float[4][];
                for (int k = 0; k < 4; k++)
                {
                    var p = points[k];
                    corners[k] = toWorld(
                    [
                        origin[0] + axis[0] * p[0] + cross[0] * p[1] + normal[0] * (p[2] + 0.5f),
                        origin[1] + axis[1] * p[0] + cross[1] * p[1] + normal[1] * (p[2] + 0.5f),
                        origin[2] + axis[2] * p[0] + cross[2] * p[1] + normal[2] * (p[2] + 0.5f),
                    ], skybox);
                }

                float[][] uv = [[u0, v0], [u0, v1], [u1, v1], [u1, v0]];
                result.Add(new Overlay(corners, uv, texinfos[texinfo].TexData, normal, skybox));
            }

            return result;
        }

        /// <summary>"x y z" as three floats; zeros when absent or malformed.</summary>
        public static float[] ParseVector(string? text)
        {
            var result = new float[3];
            if (string.IsNullOrWhiteSpace(text))
                return result;

            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < 3 && i < parts.Length; i++)
                if (float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                    result[i] = f;

            return result;
        }

        /// <summary>
        /// Rotates by the engine's angle convention: pitch about Y, yaw about Z, roll about X,
        /// applied roll first, then pitch, then yaw - the matrix AngleMatrix builds.
        /// </summary>
        public static float[] Rotate(float[] v, float[] angles)
        {
            if (angles[0] == 0 && angles[1] == 0 && angles[2] == 0)
                return v;

            double pitch = angles[0] * Math.PI / 180, yaw = angles[1] * Math.PI / 180, roll = angles[2] * Math.PI / 180;
            double sp = Math.Sin(pitch), cp = Math.Cos(pitch);
            double sy = Math.Sin(yaw), cy = Math.Cos(yaw);
            double sr = Math.Sin(roll), cr = Math.Cos(roll);

            double m00 = cp * cy, m01 = sr * sp * cy - cr * sy, m02 = cr * sp * cy + sr * sy;
            double m10 = cp * sy, m11 = sr * sp * sy + cr * cy, m12 = cr * sp * sy - sr * cy;
            double m20 = -sp, m21 = sr * cp, m22 = cr * cp;

            return
            [
                (float)(m00 * v[0] + m01 * v[1] + m02 * v[2]),
                (float)(m10 * v[0] + m11 * v[1] + m12 * v[2]),
                (float)(m20 * v[0] + m21 * v[1] + m22 * v[2]),
            ];
        }

        public static float[] Place(float[] v, Placement placement)
        {
            var r = Rotate(v, placement.Angles);
            return [r[0] + placement.Origin[0], r[1] + placement.Origin[1], r[2] + placement.Origin[2]];
        }

        #endregion

        #region Geometry helpers

        private static string MaterialOf(TexInfo texinfo, TexData[] texdata) =>
            texinfo.TexData >= 0 && texinfo.TexData < texdata.Length ? texdata[texinfo.TexData].Name : "";

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
                        indices.AddRange([a, b, c, a, c, d]);
                    else
                        indices.AddRange([a, b, d, b, c, d]);
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
        /// Texture coordinates from the face's texture axes: the engine's texel position divided by
        /// the texture's size, so the result repeats once per texture width.
        /// </summary>
        public static (float U, float V) TextureUv(float[] position, float[] textureVecs, int width, int height)
        {
            float u = textureVecs[0] * position[0] + textureVecs[1] * position[1] + textureVecs[2] * position[2] + textureVecs[3];
            float v = textureVecs[4] * position[0] + textureVecs[5] * position[1] + textureVecs[6] * position[2] + textureVecs[7];
            return (u / width, v / height);
        }

        /// <summary>
        /// Copies a face's lightmap into the atlas, encoding each sample, and replicates its edge into
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

                    var (r, g, b) = EncodeSample(lighting[sample], lighting[sample + 1], lighting[sample + 2], (sbyte)lighting[sample + 3]);

                    int o = ((place.Y + 1 + y) * atlasWidth + place.X + 1 + x) * 4;
                    atlas[o] = r;
                    atlas[o + 1] = g;
                    atlas[o + 2] = b;
                    atlas[o + 3] = 255;
                }
            }
        }

        /// <summary>
        /// Linear light a ColorRGBExp32 sample stands for: a byte per channel with a shared
        /// power-of-two exponent, where 255 at exponent 0 is 1.0.
        /// </summary>
        public static (float R, float G, float B) LinearSample(byte r, byte g, byte b, sbyte exponent)
        {
            float scale = MathF.Pow(2, exponent) / 255f;
            return (r * scale, g * scale, b * scale);
        }

        /// <summary>
        /// The range of linear light one atlas texel can hold. VRAD's output goes past 1.0 wherever
        /// the sun hits, and the engine keeps that headroom; clamping at 1.0 flattens every sunlit
        /// surface to the same white.
        /// </summary>
        public const float LightmapRange = 4f;

        /// <summary>
        /// A sample as stored in the atlas: linear light divided by <see cref="LightmapRange"/>, then
        /// gamma-encoded so the 8 bits are spent where the eye can tell. The viewer inverts both.
        /// </summary>
        public static (byte R, byte G, byte B) EncodeSample(byte r, byte g, byte b, sbyte exponent)
        {
            var (lr, lg, lb) = LinearSample(r, g, b, exponent);
            return (Encode(lr), Encode(lg), Encode(lb));
        }

        private static byte Encode(float linear)
        {
            float scaled = Math.Clamp(linear / LightmapRange, 0f, 1f);
            return (byte)Math.Round(MathF.Pow(scaled, 1f / 2.2f) * 255f);
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
        /// The entity text from the lump file beside the map, for a map whose entities the ENTLUMP
        /// step moved out of the BSP, or null when there is no such file. A .lmp is a 20-byte header -
        /// lump offset, id, version, length and map revision - followed by the lump's bytes, which
        /// for the entity lump is text.
        /// </summary>
        public static string? ReadEntityLumpFile(string bspPath)
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

                string text = Encoding.ASCII.GetString(bytes, offset, length);
                return text.Contains("classname", StringComparison.OrdinalIgnoreCase) ? text : null;
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
                    float.Parse(origin.Groups[1].Value, CultureInfo.InvariantCulture),
                    float.Parse(origin.Groups[2].Value, CultureInfo.InvariantCulture),
                    float.Parse(origin.Groups[3].Value, CultureInfo.InvariantCulture),
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
