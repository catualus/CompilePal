using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CompilePalX.Preview;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// Static props: the lump that places them, the model files that shape them, and the two ways
    /// they get lit. The formats are fixed by the engine; the small files are built here and the
    /// real ones come from a Garry's Mod install when there is one.
    /// </summary>
    public class StaticPropTests
    {
        private const string Game = @"C:\Program Files (x86)\Steam\steamapps\common\GarrysMod\garrysmod";

        [Fact]
        public void AVertexLightingFileDecodesToLinearColours()
        {
            // two meshes at LOD 0, one at LOD 1 which is ignored; colours stored BGRA
            var file = new MemoryStream();
            var w = new BinaryWriter(file);
            w.Write(2); w.Write(0u); w.Write(0u); w.Write(4u); w.Write(3u); w.Write(3); w.Write(new byte[16]);
            int dataStart = 40 + 3 * 28;
            w.Write(0u); w.Write(2u); w.Write((uint)dataStart); w.Write(new byte[16]);
            w.Write(1u); w.Write(1u); w.Write((uint)(dataStart + 8)); w.Write(new byte[16]);
            w.Write(0u); w.Write(1u); w.Write((uint)(dataStart + 12)); w.Write(new byte[16]);
            w.Write(new byte[] { 0, 0, 255, 255,  255, 255, 255, 255 }); // red, white
            w.Write(new byte[] { 9, 9, 9, 255 });                        // lod 1
            w.Write(new byte[] { 255, 0, 0, 255 });                      // blue

            var meshes = VertexLighting.Read(file.ToArray())!;

            Assert.Equal(2, meshes.Count);
            Assert.Equal(VertexLighting.Overbright, meshes[0][0], 3);   // red channel of the first vertex
            Assert.Equal(0f, meshes[0][1], 3);
            Assert.Equal(VertexLighting.Overbright, meshes[0][4], 3);   // white
            Assert.Equal(VertexLighting.Overbright, meshes[1][2], 3);   // blue
            Assert.Equal(0f, meshes[1][0], 3);
        }

        [Fact]
        public void NotAVertexLightingFileIsRefused()
        {
            Assert.Null(VertexLighting.Read(new byte[100]));
        }

        [Fact]
        public void AnAmbientCubeLightsBySquaredNormal()
        {
            // bright from above only
            var cube = new float[18];
            cube[12] = cube[13] = cube[14] = 1f; // +Z

            Assert.Equal(new float[] { 1, 1, 1 }, LeafAmbient.Light(cube, [0, 0, 1]));
            Assert.Equal(new float[] { 0, 0, 0 }, LeafAmbient.Light(cube, [0, 0, -1]));
            Assert.Equal(0.5f, LeafAmbient.Light(cube, [0.7071f, 0, 0.7071f])[0], 3);
        }

        [Fact]
        public void AnEmptyMapHasNoAmbient()
        {
            var ambient = new LeafAmbient([], [], [], 1, [], []);
            Assert.True(ambient.IsEmpty);
            Assert.Equal(-1, ambient.LeafAt([0, 0, 0]));
            Assert.All(ambient.CubeAt([0, 0, 0]), c => Assert.Equal(0.15f, c));
        }

        [Fact]
        public void AStockMapPlacesItsProps()
        {
            string map = Path.Combine(Game, "maps", "gm_construct.bsp");
            if (!File.Exists(map))
                return;

            var scene = BspGeometry.Read(map);

            Assert.True(scene.StaticProps.Count > 100, $"gm_construct has many props, found {scene.StaticProps.Count}");
            Assert.All(scene.StaticProps, p => Assert.StartsWith("models/", p.Model));
            Assert.Contains(scene.StaticProps, p => p.Model.Contains("light_industrialbell"));
            Assert.All(scene.StaticProps, p => Assert.Equal(1f, p.Scale));

            // the ambient lookup finds a leaf for every prop, and a real cube for most of them - a
            // prop sunk into a wall can sit in a leaf VRAD gave no samples
            Assert.NotNull(scene.Ambient);
            Assert.All(scene.StaticProps, p => Assert.True(scene.Ambient!.LeafAt(p.Origin) >= 0));
            int lit = scene.StaticProps.Count(p => scene.Ambient!.CubeAt(p.LightingOrigin).Any(c => c != 0.15f));
            Assert.True(lit > scene.StaticProps.Count / 2, $"only {lit} of {scene.StaticProps.Count} props found an ambient cube");
        }

        [Fact]
        public void AStockModelReadsAsTriangles()
        {
            string map = Path.Combine(Game, "maps", "gm_construct.bsp");
            if (!File.Exists(map))
                return;

            var scene = BspGeometry.Read(map);
            using var content = new ContentLocator(scene.PakLump, Game);

            var model = Mdl.Load("models/props_c17/oildrum001.mdl", content.Read);

            Assert.NotNull(model);
            Assert.True(model!.TriangleCount > 50, $"an oil drum has more than {model.TriangleCount} triangles");
            Assert.All(model.Meshes, m => Assert.Equal(m.Positions.Length / 3, m.TexCoords.Length / 2));
            Assert.All(model.Meshes, m => Assert.All(m.Indices, i => Assert.True(i < m.Positions.Length / 3)));
            Assert.True(model.Skins.Count >= 1);
            Assert.Contains("props_c17/oil_drum", model.MaterialFor(model.Meshes[0], 0).ToLowerInvariant());

            // the material it names is findable through the same content
            var materials = new PreviewMaterials(content);
            int index = materials.IndexOf(model.MaterialFor(model.Meshes[0], 0));
            Assert.NotNull(materials.Resolved[index].Texture);

            // an oil drum is about 40 units tall and centred at its base
            var zs = model.Meshes.SelectMany(m => Enumerable.Range(0, m.Positions.Length / 3).Select(i => m.Positions[i * 3 + 2])).ToList();
            Assert.InRange(zs.Max() - zs.Min(), 30, 60);
        }

        [Fact]
        public void AStockMapsPropsBuildIntoGeometry()
        {
            string map = Path.Combine(Game, "maps", "gm_construct.bsp");
            if (!File.Exists(map))
                return;

            var scene = BspGeometry.Read(map);
            using var content = new ContentLocator(scene.PakLump, Game);
            var materials = new PreviewMaterials(content);
            materials.Resolve(scene.MaterialNames);
            int worldMaterials = materials.Resolved.Count;

            var props = PropBuilder.Build(scene, content, materials, scene.LightingMode == "hdr");

            Assert.True(props.PropsPlaced > scene.StaticProps.Count / 2, $"placed {props.PropsPlaced} of {scene.StaticProps.Count}");
            Assert.True(props.Triangles > 1000);
            Assert.Equal(props.Indices.Length, props.Batches.Sum(b => b.Count));
            Assert.All(props.Indices, i => Assert.True(i < props.Vertices.Length / PreviewScene.VertexStride));
            Assert.True(materials.Resolved.Count > worldMaterials, "prop materials were added after the world's");
            Assert.All(props.Batches, b => Assert.True(b.Material < materials.Resolved.Count));

            // every prop vertex carries light in its colour slot, and none of it is NaN
            for (int v = 0; v < props.Vertices.Length; v += PreviewScene.VertexStride)
            {
                Assert.Equal(-1f, props.Vertices[v + 6]);
                Assert.False(float.IsNaN(props.Vertices[v + 11]));
            }
        }
    }
}
