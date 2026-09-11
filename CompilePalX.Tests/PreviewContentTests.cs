using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CompilePalX.Preview;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// Finding and reading the content a map's materials name: VPK archives, VMT materials, VTF
    /// textures, and the locator that ties them together. The formats are Valve's and fixed; these
    /// pin the reading against small files built here, and one real install when there is one.
    /// </summary>
    public class PreviewContentTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "CompilePalContent_" + Guid.NewGuid().ToString("N"));

        public PreviewContentTests() => Directory.CreateDirectory(root);

        public void Dispose()
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }

        #region VPK

        /// <summary>Writes a version 1 directory VPK holding the given files inline.</summary>
        private static byte[] BuildVpk(IReadOnlyList<(string Path, byte[] Data)> files)
        {
            // group as the format wants: extension -> folder -> name
            var tree = new MemoryStream();
            var data = new MemoryStream();
            var writer = new BinaryWriter(tree);

            var byExt = files.GroupBy(f => Path.GetExtension(f.Path).TrimStart('.'));
            foreach (var extGroup in byExt)
            {
                Write(writer, extGroup.Key);
                foreach (var folderGroup in extGroup.GroupBy(f => Path.GetDirectoryName(f.Path)!.Replace('\\', '/')))
                {
                    Write(writer, folderGroup.Key.Length == 0 ? " " : folderGroup.Key);
                    foreach (var (path, bytes) in folderGroup)
                    {
                        Write(writer, Path.GetFileNameWithoutExtension(path));
                        writer.Write(0u);                       // crc
                        writer.Write((ushort)0);                // preload bytes
                        writer.Write((ushort)0x7fff);           // inline in the directory file
                        writer.Write((uint)data.Position);      // offset after the tree
                        writer.Write((uint)bytes.Length);
                        writer.Write((ushort)0xffff);
                        data.Write(bytes);
                    }
                    writer.Write((byte)0);
                }
                writer.Write((byte)0);
            }
            writer.Write((byte)0);

            var file = new MemoryStream();
            var header = new BinaryWriter(file);
            header.Write(0x55aa1234u);
            header.Write(1u);
            header.Write((uint)tree.Length);
            file.Write(tree.ToArray());
            file.Write(data.ToArray());
            return file.ToArray();

            static void Write(BinaryWriter w, string s)
            {
                w.Write(Encoding.UTF8.GetBytes(s));
                w.Write((byte)0);
            }
        }

        [Fact]
        public void AVpkListsAndReadsItsFiles()
        {
            string path = Path.Combine(root, "test_dir.vpk");
            File.WriteAllBytes(path, BuildVpk(
            [
                ("materials/concrete/wall.vmt", Encoding.ASCII.GetBytes("LightmappedGeneric { $basetexture concrete/wall }")),
                ("materials/concrete/wall.vtf", [1, 2, 3, 4, 5]),
                ("readme.txt", Encoding.ASCII.GetBytes("hello")),
            ]));

            var vpk = Vpk.Open(path);

            Assert.Equal(3, vpk.Count);
            Assert.True(vpk.Contains("materials/concrete/wall.vmt"));
            Assert.True(vpk.Contains("MATERIALS\\Concrete\\WALL.VTF"), "lookups ignore case and slash direction");
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, vpk.Read("materials/concrete/wall.vtf"));
            Assert.Equal("hello", Encoding.ASCII.GetString(vpk.Read("readme.txt")!));
            Assert.Null(vpk.Read("materials/missing.vmt"));
        }

        [Fact]
        public void NotAVpkIsRefused()
        {
            string path = Path.Combine(root, "nope_dir.vpk");
            File.WriteAllBytes(path, new byte[64]);
            Assert.Throws<InvalidDataException>(() => Vpk.Open(path));
        }

        #endregion

        #region VMT

        [Fact]
        public void AMaterialNamesItsTexturesAndFlags()
        {
            const string vmt = """
                "LightmappedGeneric"
                {
                    "$basetexture" "concrete/concretefloor001a"
                    $surfaceprop concrete
                    "$translucent" 1
                    "$color" "{255 128 0}"
                    // a comment
                    ">=DX90"
                    {
                        "$bumpmap" "concrete/concretefloor001a_normal"
                    }
                }
                """;

            var m = Vmt.Parse("concrete/concretefloor001a", vmt, _ => null);

            Assert.Equal("LightmappedGeneric", m.Shader);
            Assert.Equal("concrete/concretefloor001a", m.BaseTexture);
            Assert.True(m.Translucent);
            Assert.False(m.AlphaTest);
            Assert.False(m.Unlit);
            Assert.False(m.Hidden);
            Assert.Equal(1f, m.Color[0], 3);
            Assert.Equal(128f / 255, m.Color[1], 3);
            Assert.Equal(0f, m.Color[2], 3);
        }

        [Fact]
        public void ABlendMaterialHasTwoTextures()
        {
            var m = Vmt.Parse("nature/blend", "WorldVertexTransition { $basetexture nature/grass $basetexture2 nature/dirt }", _ => null);

            Assert.Equal("nature/grass", m.BaseTexture);
            Assert.Equal("nature/dirt", m.BaseTexture2);
        }

        [Fact]
        public void APatchIncludesItsBaseAndOverridesIt()
        {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["materials/base/wall.vmt"] = "LightmappedGeneric { $basetexture base/wall $alphatest 1 }",
            };

            var m = Vmt.Parse("patched", """
                patch
                {
                    include "materials/base/wall.vmt"
                    insert
                    {
                        $basetexture "custom/wall"
                    }
                }
                """, path => files.GetValueOrDefault(path));

            Assert.Equal("custom/wall", m.BaseTexture);
            Assert.True(m.AlphaTest, "a flag the base set survives the patch");
            Assert.Equal("LightmappedGeneric", m.Shader);
        }

        [Fact]
        public void ToolAndDecalMaterialsAreHidden()
        {
            Assert.True(Vmt.Parse("tools/toolsnodraw", "LightmappedGeneric { $basetexture tools/toolsnodraw %compilenodraw 1 }", _ => null).Hidden);
            Assert.True(Vmt.Parse("decals/blood1", "DecalModulate { $basetexture decals/blood1 }", _ => null).Hidden);
            Assert.False(Vmt.Parse("tools/toolsblack", "LightmappedGeneric { $basetexture tools/toolsblack }", _ => null).Hidden);
            Assert.True(Vmt.Parse("signs/screen", "UnlitGeneric { $basetexture signs/screen }", _ => null).Unlit);
        }

        [Fact]
        public void ColoursComeInThreeSpellings()
        {
            Assert.Equal(new float[] { 1, 0.5f, 0 }, Vmt.ParseColor("[1 .5 0]"));
            Assert.Equal(0.5f, Vmt.ParseColor("{255 128 0}")[1], 2);
            Assert.Equal(new float[] { 0.5f, 0.5f, 0.5f }, Vmt.ParseColor("0.5"));
            Assert.Equal(new float[] { 1, 1, 1 }, Vmt.ParseColor(null));
            Assert.Equal(new float[] { 1, 1, 1 }, Vmt.ParseColor("not a colour"));
        }

        #endregion

        #region VTF

        /// <summary>A 7.2 VTF of the given uncompressed format with one mip level.</summary>
        private static byte[] BuildVtf(int format, int width, int height, byte[] pixels, int mips = 1)
        {
            var file = new MemoryStream();
            var w = new BinaryWriter(file);
            w.Write(Encoding.ASCII.GetBytes("VTF\0"));
            w.Write(7u); w.Write(2u);          // version
            w.Write(80u);                      // header size
            w.Write((ushort)width); w.Write((ushort)height);
            w.Write(0u);                       // flags
            w.Write((ushort)1); w.Write((ushort)0); // frames, first frame
            w.Write(new byte[4]);              // padding
            w.Write(0f); w.Write(0f); w.Write(0f); // reflectivity
            w.Write(new byte[4]);
            w.Write(1f);                       // bump scale
            w.Write(format);
            w.Write((byte)mips);
            w.Write(-1);                       // no low-res image
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1);                // depth
            while (file.Length < 80) w.Write((byte)0);
            // mips smallest first: for mips > 1 write the smaller levels as zeros
            for (int mip = mips - 1; mip >= 1; mip--)
                w.Write(new byte[Vtf.LevelSize(format, Math.Max(1, width >> mip), Math.Max(1, height >> mip))]);
            w.Write(pixels);
            return file.ToArray();
        }

        [Fact]
        public void AnUncompressedTextureBecomesRgba()
        {
            // 2x1 BGRA8888: pure red, then half-transparent green
            var bytes = BuildVtf(12, 2, 1, [0, 0, 255, 255, 0, 255, 0, 128]);

            var texture = Vtf.Read(bytes)!;

            Assert.Equal("rgba8", texture.Format);
            Assert.Equal(2, texture.Width);
            Assert.Single(texture.Levels);
            Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 128 }, texture.Levels[0].Data);
            Assert.True(texture.HasAlpha);
        }

        [Fact]
        public void DxtDataIsPassedThroughLargestMipFirst()
        {
            // 8x4 DXT1 with two mips: the top level is two 8-byte blocks
            var top = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
            var bytes = BuildVtf(13, 8, 4, top, mips: 2);

            var texture = Vtf.Read(bytes)!;

            Assert.Equal("dxt1", texture.Format);
            Assert.Equal(2, texture.Levels.Count);
            Assert.Equal(8, texture.Levels[0].Width);
            Assert.Equal(top, texture.Levels[0].Data);
            Assert.Equal(4, texture.Levels[1].Width);
            Assert.Equal(8, texture.Levels[1].Data.Length);
        }

        [Fact]
        public void ATextureLargerThanTheCapDropsItsTopMips()
        {
            int size = Vtf.MaxSize * 2;
            var bytes = BuildVtf(13, size, size, new byte[Vtf.LevelSize(13, size, size)], mips: 3);

            var texture = Vtf.Read(bytes)!;

            Assert.Equal(Vtf.MaxSize, texture.Width);
            Assert.Equal(2, texture.Levels.Count);
        }

        [Fact]
        public void NotAVtfIsRefused()
        {
            Assert.Throws<InvalidDataException>(() => Vtf.Read(new byte[100]));
        }

        #endregion

        #region Locator

        [Fact]
        public void TheLocatorPrefersThePakThenFoldersThenVpks()
        {
            // the same material in all three places, each saying where it came from
            string game = Path.Combine(root, "game");
            Directory.CreateDirectory(Path.Combine(game, "materials"));
            File.WriteAllText(Path.Combine(game, "gameinfo.txt"), "GameInfo { game test FileSystem { SearchPaths { game |gameinfo_path|. } } }");
            File.WriteAllText(Path.Combine(game, "materials", "only_loose.vmt"), "loose");
            File.WriteAllText(Path.Combine(game, "materials", "shared.vmt"), "loose");
            File.WriteAllBytes(Path.Combine(game, "test_dir.vpk"), BuildVpk(
            [
                ("materials/only_vpk.vmt", Encoding.ASCII.GetBytes("vpk")),
                ("materials/shared.vmt", Encoding.ASCII.GetBytes("vpk")),
            ]));

            using var pakStream = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(pakStream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                using var entry = zip.CreateEntry("materials/shared.vmt").Open();
                entry.Write(Encoding.ASCII.GetBytes("pak"));
            }

            using var locator = new ContentLocator(pakStream.ToArray(), game);

            Assert.True(locator.HasGameContent);
            Assert.Equal("pak", Encoding.ASCII.GetString(locator.Read("materials/shared.vmt")!));
            Assert.Equal("loose", Encoding.ASCII.GetString(locator.Read("materials/only_loose.vmt")!));
            Assert.Equal("vpk", Encoding.ASCII.GetString(locator.Read("materials/only_vpk.vmt")!));
            Assert.Null(locator.Read("materials/nowhere.vmt"));
            Assert.Equal(1, locator.PakHits);
            Assert.Equal(1, locator.FolderHits);
            Assert.Equal(1, locator.VpkHits);
            Assert.Equal(1, locator.Misses);
        }

        [Fact]
        public void WithNoGameFolderOnlyThePakIsSearched()
        {
            using var locator = new ContentLocator([], null);
            Assert.False(locator.HasGameContent);
            Assert.Null(locator.Read("materials/anything.vmt"));
        }

        /// <summary>
        /// The whole pipeline against Garry's Mod, when it is installed: gm_construct's materials come
        /// from its own pak, the game's VPKs and the mounted HL2 content.
        /// </summary>
        [Fact]
        public void AStockMapsMaterialsAreFound()
        {
            string game = @"C:\Program Files (x86)\Steam\steamapps\common\GarrysMod\garrysmod";
            string map = Path.Combine(game, "maps", "gm_construct.bsp");
            if (!File.Exists(map))
                return;

            var scene = BspGeometry.Read(map);
            using var content = new ContentLocator(scene.PakLump, game);
            var materials = new PreviewMaterials(content);

            var resolved = materials.Resolve(scene.MaterialNames);
            var sky = materials.ResolveSky(scene.SkyName);

            Assert.Equal(scene.MaterialNames.Count, resolved.Count);
            Assert.True(materials.MaterialsFound > materials.MaterialsMissing, $"found {materials.MaterialsFound}, missing {materials.MaterialsMissing}");
            Assert.True(materials.Textures.Count > 20, $"only {materials.Textures.Count} textures");
            Assert.Contains(materials.Textures, t => t.Texture.Format.StartsWith("dxt"));
            // gm_construct uses Garry's Mod's painted sky, which has no textures to find
            Assert.Null(sky);
            Assert.NotNull(scene.SkyPaint);
            Assert.Equal(3, scene.SkyPaint!.TopColor.Length);
        }

        #endregion
    }
}
