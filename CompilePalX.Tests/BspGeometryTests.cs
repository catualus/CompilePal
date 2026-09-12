using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CompilePalX.Preview;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// Turning a compiled map into something drawable: lightmap samples to pixels, faces to
    /// triangles, lightmaps into an atlas. The format is fixed by the engine, so most of this is
    /// checking arithmetic against what the engine does with the same numbers.
    /// </summary>
    public class BspGeometryTests
    {
        [Fact]
        public void FullBrightnessIsOneInLinearLight()
        {
            // 255 at exponent 0 is 1.0; the exponent scales by powers of two
            Assert.Equal((1f, 1f, 1f), BspGeometry.LinearSample(255, 255, 255, 0));
            Assert.Equal(0.5f, BspGeometry.LinearSample(255, 255, 255, -1).R, 4);
            Assert.Equal(0f, BspGeometry.LinearSample(0, 0, 0, 5).R);
        }

        [Fact]
        public void TheAtlasKeepsHeadroomAboveOne()
        {
            // 1.0 linear is a quarter of the atlas range, gamma-encoded: about 136 of 255
            var one = BspGeometry.EncodeSample(255, 255, 255, 0);
            Assert.InRange(one.R, 134, 138);

            // four times that fills the range; more clamps rather than wraps
            Assert.Equal((byte)255, BspGeometry.EncodeSample(255, 255, 255, 2).R);
            Assert.Equal((byte)255, BspGeometry.EncodeSample(200, 200, 200, 4).R);

            // deep in the negatives is black
            Assert.Equal((byte)0, BspGeometry.EncodeSample(1, 1, 1, -20).R);

            // channels are independent
            var sample = BspGeometry.EncodeSample(255, 0, 128, 0);
            Assert.Equal((byte)0, sample.G);
            Assert.True(sample.B < sample.R);
        }

        [Fact]
        public void TextureUvRepeatsOncePerTextureSize()
        {
            // texture S axis is world X at one texel per unit, on a 64-wide texture
            float[] vecs = [1, 0, 0, 0, 0, 1, 0, 0];
            Assert.Equal((0f, 0f), BspGeometry.TextureUv([0, 0, 0], vecs, 64, 32));
            Assert.Equal((1f, 0.5f), BspGeometry.TextureUv([64, 16, 0], vecs, 64, 32));
        }

        [Fact]
        public void AnglesRotateTheEngineWay()
        {
            // yaw turns about Z: +X becomes +Y
            var yawed = BspGeometry.Rotate([1, 0, 0], [0, 90, 0]);
            Assert.Equal(0f, yawed[0], 4);
            Assert.Equal(1f, yawed[1], 4);
            Assert.Equal(0f, yawed[2], 4);

            // pitch tips the nose down about Y: +X becomes -Z
            var pitched = BspGeometry.Rotate([1, 0, 0], [90, 0, 0]);
            Assert.Equal(-1f, pitched[2], 4);

            // roll leaves X alone
            Assert.Equal(new float[] { 1, 0, 0 }, BspGeometry.Rotate([1, 0, 0], [0, 0, 90]));
        }

        [Fact]
        public void BrushEntitiesAreReadWithTheirPlacement()
        {
            var entities = BspGeometry.ParseEntities("""
                { "classname" "worldspawn" "skyname" "sky_day01_01" }
                { "classname" "func_door" "model" "*3" "origin" "512 -64 128" }
                { "classname" "func_brush" "model" "*4" "angles" "0 45 0" }
                { "classname" "func_detail_like" "model" "*5" "origin" "0 0 0" }
                { "classname" "prop_dynamic" "model" "models/props/thing.mdl" "origin" "1 2 3" }
                """);

            Assert.Equal(5, entities.Count);
            Assert.Equal("sky_day01_01", entities[0]["skyname"]);

            var placements = BspGeometry.BrushPlacements(entities);

            Assert.Equal(new float[] { 512, -64, 128 }, placements[3].Origin);
            Assert.Equal(new float[] { 0, 45, 0 }, placements[4].Angles);
            Assert.False(placements.ContainsKey(5), "an entity sitting at the origin needs no placement");
            Assert.Equal(2, placements.Count);
        }

        [Fact]
        public void AConvexFaceFansIntoTriangles()
        {
            Assert.Equal(new uint[] { 0, 1, 2 }, BspGeometry.FanIndices(3));
            Assert.Equal(new uint[] { 0, 1, 2, 0, 2, 3, 0, 3, 4 }, BspGeometry.FanIndices(5));
            Assert.Empty(BspGeometry.FanIndices(2));
        }

        [Fact]
        public void LightmapUvLandsOnTheSampleCentre()
        {
            // lightmap S axis is world X, one luxel per 16 units, face starts at luxel 4
            float[] vecs = [1f / 16, 0, 0, 0, 0, 1f / 16, 0, 0];
            var place = new AtlasPlacement(10, 20);

            // the face's first luxel: s = 0, so the texel at place.X + border + 0.5
            var (u, v) = BspGeometry.LightmapUv([64, 80, 0], vecs, 4, 5, place, 256, 256);
            Assert.Equal((10 + 1 + 0.5f) / 256, u, 5);
            Assert.Equal((20 + 1 + 0.5f) / 256, v, 5);

            // two luxels along S
            (u, _) = BspGeometry.LightmapUv([96, 80, 0], vecs, 4, 5, place, 256, 256);
            Assert.Equal((10 + 1 + 2.5f) / 256, u, 5);
        }

        [Fact]
        public void TheAtlasPacksWithoutOverlap()
        {
            var rects = Enumerable.Range(0, 200)
                .Select(i => new AtlasRect(i, 1 + i % 17, 1 + i % 11))
                .ToList();

            var atlas = Atlas.Pack(rects, 8192);

            Assert.Equal(rects.Count, atlas.Placements.Count);
            Assert.True(atlas.Width >= 256 && atlas.Width <= 8192);

            var occupied = new HashSet<(int, int)>();
            foreach (var rect in rects)
            {
                var place = atlas.Placements[rect.Face];
                for (int y = 0; y < rect.Height + 2; y++)
                    for (int x = 0; x < rect.Width + 2; x++)
                    {
                        Assert.True(place.X + x < atlas.Width && place.Y + y < atlas.Height, "cell runs off the atlas");
                        Assert.True(occupied.Add((place.X + x, place.Y + y)), "two lightmaps share a texel");
                    }
            }
        }

        [Fact]
        public void AnAtlasThatCannotHoldEverythingKeepsWhatFits()
        {
            var rects = Enumerable.Range(0, 64).Select(i => new AtlasRect(i, 100, 100)).ToList();

            // 64 cells of 102x102 need more than 512x512
            var atlas = Atlas.Pack(rects, 512);

            Assert.Equal(512, atlas.Width);
            Assert.True(atlas.Placements.Count < rects.Count);
            Assert.True(atlas.Placements.Count > 0);
        }

        [Fact]
        public void NothingToPackIsNoAtlas()
        {
            var atlas = Atlas.Pack([], 8192);
            Assert.Equal(0, atlas.Width);
            Assert.Empty(atlas.Placements);
        }

        [Fact]
        public void TheSpawnPointComesFromTheEntityLump()
        {
            const string entities = """
                {
                "world_maxs" "1024 1024 256"
                "classname" "worldspawn"
                }
                {
                "origin" "-64 128.5 32"
                "angles" "0 90 0"
                "classname" "info_player_start"
                }
                """;

            Assert.Equal(new float[] { -64, 128.5f, 32 }, BspGeometry.FindSpawn(entities));
            Assert.Null(BspGeometry.FindSpawn("{\n\"classname\" \"worldspawn\"\n}"));
        }

        [Fact]
        public void AFlatDisplacementIsABilinearGrid()
        {
            // a 64x64 quad, power 1 (3x3 grid), every vertex offset zero
            float[][] corners = [[0, 0, 0], [64, 0, 0], [64, 64, 0], [0, 64, 0]];
            var verts = new float[9 * 5];

            var mesh = BspGeometry.BuildDisplacement(corners, [0, 0, 0], 1, verts, 0);

            Assert.Equal(9, mesh.Positions.Length);
            Assert.Equal(8 * 3, mesh.Indices.Length);

            // row i runs corner 0 -> corner 1, column j runs towards the opposite edge
            Assert.Equal(new float[] { 0, 0, 0 }, mesh.Positions[0]);
            Assert.Equal(new float[] { 32, 0, 0 }, mesh.Positions[1 * 3 + 0]);
            Assert.Equal(new float[] { 0, 32, 0 }, mesh.Positions[0 * 3 + 1]);
            Assert.Equal(new float[] { 64, 64, 0 }, mesh.Positions[2 * 3 + 2]);
            Assert.All(mesh.Normals, n => Assert.Equal(1f, MathF.Abs(n[2]), 3));
            Assert.All(mesh.Indices, i => Assert.True(i < 9));
        }

        [Fact]
        public void TheGridStartsAtTheCornerNearestTheStartPosition()
        {
            float[][] corners = [[0, 0, 0], [64, 0, 0], [64, 64, 0], [0, 64, 0]];
            var verts = new float[9 * 5];

            // start position at corner 2: the grid origin moves there
            var mesh = BspGeometry.BuildDisplacement(corners, [63, 65, 0], 1, verts, 0);

            Assert.Equal(new float[] { 64, 64, 0 }, mesh.Positions[0]);
        }

        [Fact]
        public void DisplacementVerticesMoveAlongTheirVector()
        {
            float[][] corners = [[0, 0, 0], [64, 0, 0], [64, 64, 0], [0, 64, 0]];
            var verts = new float[9 * 5];

            // the centre vertex (1,1) is pushed 10 units up
            int centre = (1 * 3 + 1) * 5;
            verts[centre + 2] = 1; // vector z
            verts[centre + 3] = 10; // distance

            var mesh = BspGeometry.BuildDisplacement(corners, [0, 0, 0], 1, verts, 0);

            Assert.Equal(new float[] { 32, 32, 10 }, mesh.Positions[4]);
            // the normal next to it tilts away from straight up; the far corner, whose neighbours are
            // all flat, does not
            Assert.True(mesh.Normals[1][2] < 0.999f);
            Assert.Equal(1f, mesh.Normals[0][2], 3);
        }

        [Fact]
        public void ACompressedLumpInflatesBackToItself()
        {
            var original = Enumerable.Range(0, 20000).Select(i => (byte)(i % 37 + (i / 500))).ToArray();

            // the way bspzip -compress writes a lump: "LZMA", inflated size, packed size, the five
            // property bytes, then a raw LZMA stream
            var encoder = new SevenZip.Compression.LZMA.Encoder();
            using var packed = new MemoryStream();
            using (var input = new MemoryStream(original))
                encoder.Code(input, packed, original.Length, -1, null);
            using var properties = new MemoryStream();
            encoder.WriteCoderProperties(properties);

            using var lump = new MemoryStream();
            lump.Write("LZMA"u8);
            lump.Write(BitConverter.GetBytes((uint)original.Length));
            lump.Write(BitConverter.GetBytes((uint)packed.Length));
            lump.Write(properties.ToArray());
            lump.Write(packed.ToArray());

            Assert.Equal(original, BspGeometry.DecodeLzmaLump(lump.ToArray()));
        }

        [Fact]
        public void MaterialColoursAreStableAndDistinct()
        {
            var a = BspGeometry.ColourFor("concrete/concretewall001");
            var b = BspGeometry.ColourFor("concrete/concretewall001");
            var c = BspGeometry.ColourFor("metal/metalwall001");

            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
            Assert.All(a, channel => Assert.InRange(channel, 0f, 1f));
        }

        /// <summary>
        /// A real map, when one is to hand. Garry's Mod's gm_construct is a stock file on any machine
        /// with the game installed and exercises everything above at once; on a machine without it
        /// this test simply has nothing to say.
        /// </summary>
        [Fact]
        public void AStockMapReadsEndToEnd()
        {
            string path = @"C:\Program Files (x86)\Steam\steamapps\common\GarrysMod\garrysmod\maps\gm_construct.bsp";
            if (!File.Exists(path))
                return;

            var scene = BspGeometry.Read(path);

            Assert.True(scene.DrawnFaces > 1000, "gm_construct has thousands of faces");
            Assert.True(scene.DrawnDisplacements > 0, "gm_construct's ground is displacements");
            Assert.Equal(0, scene.SkippedDisplacements);
            Assert.True(scene.Batches.Count > 10, "one batch per material");
            Assert.Equal(scene.Indices.Length, scene.Batches.Sum(b => b.Count));
            Assert.True(scene.MaterialNames.Count > 10);
            Assert.False(string.IsNullOrEmpty(scene.SkyName));
            Assert.True(scene.PakLump.Length > 0, "gm_construct packs content");
            Assert.Equal(scene.Indices.Length / 3, scene.TriangleCount);
            Assert.True(scene.Indices.All(i => i < scene.VertexCount), "an index points past the vertices");
            Assert.NotEqual("none", scene.LightingMode);
            Assert.True(scene.LightmapWidth >= 256);
            Assert.Equal(scene.LightmapWidth * scene.LightmapHeight * 4, scene.Lightmap.Length);
            Assert.NotNull(scene.Spawn);

            // the map is a few thousand units across and sits around the origin
            Assert.True(scene.Maxs[0] - scene.Mins[0] > 1000);
            Assert.All(scene.Vertices, f => Assert.False(float.IsNaN(f)));

            // the lightmap is not all black: sunlight reaches most of gm_construct
            long brightness = 0;
            for (int i = 0; i < scene.Lightmap.Length; i += 4)
                brightness += scene.Lightmap[i];
            Assert.True(brightness / (scene.Lightmap.Length / 4) > 5, "the atlas is black");
        }
    }
}
