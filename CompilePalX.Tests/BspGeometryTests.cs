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
        public void FullBrightnessIsWhite()
        {
            // 255 at exponent 0 is 1.0 in linear light, which is white however it is encoded
            Assert.Equal(((byte)255, (byte)255, (byte)255), BspGeometry.DecodeSample(255, 255, 255, 0));
        }

        [Fact]
        public void TheExponentScalesTheSample()
        {
            // 255 at exponent -1 is 0.5 linear, which gamma-encodes to about 186
            var half = BspGeometry.DecodeSample(255, 255, 255, -1);
            Assert.InRange(half.R, 184, 188);

            // a positive exponent overflows and clamps to white rather than wrapping
            Assert.Equal((byte)255, BspGeometry.DecodeSample(200, 200, 200, 3).R);

            // deep in the negatives is black
            Assert.Equal((byte)0, BspGeometry.DecodeSample(1, 1, 1, -20).R);
        }

        [Fact]
        public void ChannelsAreIndependent()
        {
            var sample = BspGeometry.DecodeSample(255, 0, 128, 0);
            Assert.Equal((byte)255, sample.R);
            Assert.Equal((byte)0, sample.G);
            Assert.InRange(sample.B, 185, 190);
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
