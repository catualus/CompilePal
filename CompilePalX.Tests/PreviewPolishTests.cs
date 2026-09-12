using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CompilePalX.Preview;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// The pieces that make a preview look like the game rather than a lightmap dump: fog, the
    /// 3D skybox camera, props placed by entities, and the leaf tree's areas. Entity text is
    /// built here; the leaf walk uses a real map when one is installed.
    /// </summary>
    public class PreviewPolishTests
    {
        private const string Game = @"C:\Program Files (x86)\Steam\steamapps\common\GarrysMod\garrysmod";

        [Fact]
        public void FogComesFromAnEnabledController()
        {
            var entities = BspGeometry.ParseEntities("{ \"classname\" \"env_fog_controller\" \"fogenable\" \"1\" \"fogcolor\" \"255 0 0\" \"fogstart\" \"100\" \"fogend\" \"2000\" \"fogmaxdensity\" \"0.6\" }");
            var fog = BspGeometry.ReadFog(entities);

            Assert.NotNull(fog);
            Assert.Equal(1f, fog!.Color[0]);
            Assert.Equal(0f, fog.Color[1]);
            Assert.Equal(100f, fog.Start);
            Assert.Equal(2000f, fog.End);
            Assert.Equal(0.6f, fog.MaxDensity);
        }

        [Fact]
        public void DisabledOrDegenerateFogIsNoFog()
        {
            Assert.Null(BspGeometry.ReadFog(BspGeometry.ParseEntities("{ \"classname\" \"env_fog_controller\" \"fogenable\" \"0\" }")));
            Assert.Null(BspGeometry.ReadFog(BspGeometry.ParseEntities("{ \"classname\" \"env_fog_controller\" \"fogenable\" \"1\" \"fogstart\" \"500\" \"fogend\" \"500\" }")));
            Assert.Null(BspGeometry.ReadFog(BspGeometry.ParseEntities("{ \"classname\" \"worldspawn\" }")));
        }

        [Fact]
        public void SkyCameraReadsOriginAndScale()
        {
            var camera = BspGeometry.ReadSkyCamera(BspGeometry.ParseEntities("{ \"classname\" \"sky_camera\" \"origin\" \"10 20 30\" \"scale\" \"8\" }"));
            Assert.NotNull(camera);
            Assert.Equal(new float[] { 10, 20, 30 }, camera!.Origin);
            Assert.Equal(8f, camera.Scale);

            // no scale key means the engine's default
            Assert.Equal(16f, BspGeometry.ReadSkyCamera(BspGeometry.ParseEntities("{ \"classname\" \"sky_camera\" \"origin\" \"0 0 0\" }"))!.Scale);
            Assert.Null(BspGeometry.ReadSkyCamera(BspGeometry.ParseEntities("{ \"classname\" \"info_player_start\" }")));
        }

        [Fact]
        public void EntityPropsComeFromDoorsAndDynamicProps()
        {
            var props = BspGeometry.EntityProps(BspGeometry.ParseEntities(
                "{ \"classname\" \"prop_door_rotating\" \"model\" \"models/props_c17/door01_left.mdl\" \"origin\" \"1 2 3\" \"angles\" \"0 90 0\" \"skin\" \"2\" }" +
                "{ \"classname\" \"prop_dynamic\" \"model\" \"models\\Props\\Box.MDL\" \"origin\" \"0 0 0\" \"modelscale\" \"2\" }" +
                "{ \"classname\" \"prop_dynamic\" \"model\" \"models/props/hidden.mdl\" \"rendermode\" \"10\" }" +
                "{ \"classname\" \"prop_dynamic\" \"model\" \"*12\" }" +
                "{ \"classname\" \"light\" \"origin\" \"0 0 0\" }"));

            Assert.Equal(2, props.Count);
            Assert.All(props, p => Assert.Equal(-1, p.Index));
            Assert.Equal("models/props_c17/door01_left.mdl", props[0].Model);
            Assert.Equal(2, props[0].Skin);
            Assert.Equal(90f, props[0].Angles[1]);
            Assert.Equal("models/props/box.mdl", props[1].Model);
            Assert.Equal(2f, props[1].Scale);
        }

        [Fact]
        public void LeafAreasAndFacesReadFromTheLumps()
        {
            // one plane, one node splitting two leaves (children -1 and -2), leaves in areas 0 and 1
            var planes = new byte[20];
            BitConverter.GetBytes(1f).CopyTo(planes, 0);           // normal +X, dist 0
            var nodes = new byte[32];
            BitConverter.GetBytes(0).CopyTo(nodes, 0);
            BitConverter.GetBytes(-1).CopyTo(nodes, 4);            // front: leaf 0
            BitConverter.GetBytes(-2).CopyTo(nodes, 8);            // back: leaf 1
            var leaves = new byte[64];
            BitConverter.GetBytes((short)0).CopyTo(leaves, 6);
            BitConverter.GetBytes((ushort)0).CopyTo(leaves, 20);   // leaf 0: faces [0, 2)
            BitConverter.GetBytes((ushort)2).CopyTo(leaves, 22);
            BitConverter.GetBytes((short)1).CopyTo(leaves, 32 + 6);
            BitConverter.GetBytes((ushort)2).CopyTo(leaves, 32 + 20); // leaf 1: faces [2, 3)
            BitConverter.GetBytes((ushort)1).CopyTo(leaves, 32 + 22);
            var leafFaces = new byte[6];
            BitConverter.GetBytes((ushort)7).CopyTo(leafFaces, 0);
            BitConverter.GetBytes((ushort)8).CopyTo(leafFaces, 2);
            BitConverter.GetBytes((ushort)9).CopyTo(leafFaces, 4);

            var ambient = new LeafAmbient(planes, nodes, leaves, 1, [], [], leafFaces);

            Assert.Equal(2, ambient.LeafCount);
            Assert.Equal(0, ambient.LeafAt([5, 0, 0]));
            Assert.Equal(1, ambient.LeafAt([-5, 0, 0]));
            Assert.Equal(0, ambient.AreaOf(0));
            Assert.Equal(1, ambient.AreaOf(1));
            Assert.Equal(-1, ambient.AreaOf(9));
            Assert.Equal(new HashSet<int> { 7, 8 }, ambient.FacesInArea(0));
            Assert.Equal(new HashSet<int> { 9 }, ambient.FacesInArea(1));
            Assert.Empty(ambient.FacesInArea(3));
        }

        [Fact]
        public void AmbientSamplesDecodeOnTheirOwnScale()
        {
            // one leaf, one sample: +X face 189 with exponent -11 (as VRAD writes an indoor cube)
            var planes = new byte[20]; BitConverter.GetBytes(1f).CopyTo(planes, 0);
            var nodes = new byte[32]; BitConverter.GetBytes(-1).CopyTo(nodes, 4); BitConverter.GetBytes(-1).CopyTo(nodes, 8);
            var leaves = new byte[32];
            BitConverter.GetBytes((short)-64).CopyTo(leaves, 8); BitConverter.GetBytes((short)-64).CopyTo(leaves, 10); BitConverter.GetBytes((short)-64).CopyTo(leaves, 12);
            BitConverter.GetBytes((short)64).CopyTo(leaves, 14); BitConverter.GetBytes((short)64).CopyTo(leaves, 16); BitConverter.GetBytes((short)64).CopyTo(leaves, 18);
            var index = new byte[4]; BitConverter.GetBytes((ushort)1).CopyTo(index, 0);
            var sample = new byte[28]; sample[0] = 189; sample[3] = unchecked((byte)(sbyte)-11);

            var ambient = new LeafAmbient(planes, nodes, leaves, 1, index, sample);
            var cube = ambient.CubeAt([0, 0, 0]);

            Assert.Equal(189 * MathF.Pow(2, -11), cube[0], 5);
            Assert.Equal(0f, cube[1]);
            Assert.Equal(0f, cube[3]);
        }

        [Fact]
        public void AStockMapWithASkyboxSplitsItsFacesOut()
        {
            string map = Path.Combine(Game, "maps", "gm_flatgrass.bsp");
            if (!File.Exists(map))
                return;

            var scene = BspGeometry.Read(map);
            if (scene.Sky3D is null)
                return;

            Assert.True(scene.SkyboxArea >= 0);
            Assert.True(scene.SkyboxFaces > 0, "the skybox area has faces");
            Assert.Contains(scene.Batches, b => b.Skybox);
            Assert.Contains(scene.Batches, b => !b.Skybox);
            Assert.True(scene.SkyboxFaces < scene.FaceCount / 2, "the skybox is the smaller part of the map");
        }
    }
}
