using System;
using System.IO;
using System.Linq;
using System.Text;
using CompilePalX.Preview;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// Edges the preview readers must get right: leaf lists that changed width in BSP 25, a
    /// prop's first leaf being an index into that list, partial DXT blocks, a lump file for the
    /// wrong map revision, and a map that names files outside its mounts.
    /// </summary>
    public class PreviewReviewTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "CompilePalReview_" + Guid.NewGuid().ToString("N"));

        public PreviewReviewTests() => Directory.CreateDirectory(root);

        public void Dispose()
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }

        /// <summary>A game lump with one sprp sub-lump: one model, the given leaf list, one prop at leaf entry 2.</summary>
        private static (byte[] GameLump, byte[] Sprp) BuildStaticProps(int[] leafList, int leafEntrySize)
        {
            var sprp = new MemoryStream();
            var w = new BinaryWriter(sprp);
            w.Write(1);
            var name = new byte[128];
            Encoding.ASCII.GetBytes("models/props/crate.mdl").CopyTo(name, 0);
            w.Write(name);
            w.Write(leafList.Length);
            foreach (int leaf in leafList)
                if (leafEntrySize == 4) w.Write(leaf); else w.Write((ushort)leaf);
            w.Write(1);
            // a version 5 entry: origin, angles, type, first leaf, leaf count, solid, flags, skin, fade min/max, lighting origin, forced fade scale
            w.Write(10f); w.Write(20f); w.Write(30f);
            w.Write(0f); w.Write(90f); w.Write(0f);
            w.Write((ushort)0); w.Write((ushort)2); w.Write((ushort)3);
            w.Write((byte)6); w.Write((byte)0);
            w.Write(1);
            w.Write(0f); w.Write(0f);
            w.Write(1f); w.Write(2f); w.Write(3f);
            w.Write(1f);

            var lump = new MemoryStream();
            var lw = new BinaryWriter(lump);
            lw.Write(1);
            lw.Write(1936749168); lw.Write((ushort)0); lw.Write((ushort)5); lw.Write(100); lw.Write((int)sprp.Length);
            return (lump.ToArray(), sprp.ToArray());
        }

        [Fact]
        public void APropsFirstLeafComesThroughTheLeafList()
        {
            var (lump, sprp) = BuildStaticProps([7, 9, 12], 2);
            var props = StaticPropLump.Read(lump, 0, (_, _) => sprp, bspVersion: 20);

            var prop = Assert.Single(props);
            Assert.Equal("models/props/crate.mdl", prop.Model);
            Assert.Equal(12, prop.FirstLeaf);
            Assert.Equal(90f, prop.Angles[1]);
            Assert.Equal(1, prop.Skin);
        }

        [Fact]
        public void Bsp25WidensTheLeafList()
        {
            var (lump, sprp) = BuildStaticProps([70000, 9, 12], 4);
            var props = StaticPropLump.Read(lump, 0, (_, _) => sprp, bspVersion: 25);

            var prop = Assert.Single(props);
            Assert.Equal(12, prop.FirstLeaf);
            Assert.Equal(new[] { 10f, 20f, 30f }, prop.Origin);

            // read as 16-bit entries the same bytes fall apart into nothing usable
            var misread = StaticPropLump.Read(lump, 0, (_, _) => sprp, bspVersion: 20);
            Assert.True(misread.Count == 0 || misread[0].FirstLeaf != 12);
        }

        [Fact]
        public void APartialDxtBlockIsAWholeBlock()
        {
            Assert.Equal(8, Vtf.LevelSize(13, 1, 1));
            Assert.Equal(8, Vtf.LevelSize(13, 4, 4));
            Assert.Equal(4 * 8, Vtf.LevelSize(13, 5, 5));
            Assert.Equal(4 * 16, Vtf.LevelSize(15, 5, 5));
            Assert.Equal(2 * 2 * 8, Vtf.LevelSize(13, 8, 8));
        }

        [Fact]
        public void ALumpFileForAnotherRevisionIsIgnored()
        {
            string bsp = Path.Combine(root, "map.bsp");
            File.WriteAllBytes(bsp, []);
            string text = "{ \"classname\" \"info_player_start\" \"origin\" \"1 2 3\" }";
            var file = new MemoryStream();
            var w = new BinaryWriter(file);
            w.Write(20); w.Write(0); w.Write(0); w.Write(text.Length); w.Write(7);
            w.Write(Encoding.ASCII.GetBytes(text));
            File.WriteAllBytes(Path.Combine(root, "map_l_0.lmp"), file.ToArray());

            Assert.NotNull(BspGeometry.ReadEntityLumpFile(bsp, 7));
            Assert.NotNull(BspGeometry.ReadEntityLumpFile(bsp));
            Assert.Null(BspGeometry.ReadEntityLumpFile(bsp, 8));
        }

        [Fact]
        public void TheLocatorStaysInsideItsMounts()
        {
            string game = Path.Combine(root, "game");
            Directory.CreateDirectory(Path.Combine(game, "materials"));
            File.WriteAllText(Path.Combine(game, "gameinfo.txt"), "GameInfo { game test FileSystem { SearchPaths { game |gameinfo_path|. } } }");
            File.WriteAllText(Path.Combine(game, "materials", "ok.vmt"), "ok");
            File.WriteAllText(Path.Combine(root, "secret.txt"), "secret");

            using var locator = new ContentLocator([], game);

            Assert.Equal("ok", Encoding.ASCII.GetString(locator.Read("materials/ok.vmt")!));
            Assert.Null(locator.Read("../secret.txt"));
            Assert.Null(locator.Read("materials/../../secret.txt"));
            Assert.Null(locator.Read(Path.Combine(root, "secret.txt")));
            Assert.Null(locator.Read("materials\\..\\..\\secret.txt"));
        }

        [Fact]
        public void TheSkyboxCanLiveInAreaZero()
        {
            // one plane, one node, two leaves both in area 0; the sky camera's leaf is area 0 and must count
            var planes = new byte[20]; BitConverter.GetBytes(1f).CopyTo(planes, 0);
            var nodes = new byte[32]; BitConverter.GetBytes(-1).CopyTo(nodes, 4); BitConverter.GetBytes(-2).CopyTo(nodes, 8);
            var leaves = new byte[64];
            BitConverter.GetBytes((ushort)0).CopyTo(leaves, 20); BitConverter.GetBytes((ushort)1).CopyTo(leaves, 22);
            var leafFaces = new byte[2]; BitConverter.GetBytes((ushort)5).CopyTo(leafFaces, 0);
            var ambient = new LeafAmbient(planes, nodes, leaves, 1, [], [], leafFaces);

            Assert.Equal(0, ambient.AreaOf(ambient.LeafAt([5, 0, 0])));
            Assert.Equal(new[] { 5 }, ambient.FacesInArea(0).ToArray());
        }
    }
}
