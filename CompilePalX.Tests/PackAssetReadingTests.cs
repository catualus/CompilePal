using System;
using System.IO;
using System.Text;
using CompilePalX.Compilers.BSPPack;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// The readers PACK uses to find what a map depends on, on small files built here: models with
    /// several $includemodel entries, multi-solid and read-only .phy files, particle manifests and
    /// soundscapes with comments, Garry's Mod .gma headers, and pak paths with "..".
    /// </summary>
    public class PackAssetReadingTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "cp-packread-" + Guid.NewGuid().ToString("N"));

        public PackAssetReadingTests() => Directory.CreateDirectory(root);

        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }

        private string Write(string name, byte[] bytes)
        {
            string path = Path.Combine(root, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private string Write(string name, string text) => Write(name, Encoding.UTF8.GetBytes(text));

        /// <summary>
        /// Each include's name offset counts from its own entry. Every include after the first used to
        /// be read from the first entry's position, so only the first was ever packed.
        /// </summary>
        [Theory]
        [InlineData(44)]
        [InlineData(48)]
        public void EveryIncludedModelIsFound(int version)
        {
            var mdl = new byte[1024];
            void Int(int at, int value) => BitConverter.GetBytes(value).CopyTo(mdl, at);
            void Str(int at, string value) => Encoding.ASCII.GetBytes(value + "\0").CopyTo(mdl, at);

            Int(4, version);
            Int(336, 3);    // includemodel count
            Int(340, 400);  // includemodel index
            string[] names = ["models/a_anims.mdl", "models/b_anims.mdl", "models/c_anims.mdl"];
            for (int i = 0; i < 3; i++)
            {
                int entry = 400 + i * 8, name = 500 + i * 40;
                Int(entry + 4, name - entry);
                Str(name, names[i]);
            }

            var (_, models) = AssetUtils.FindMdlMaterialsAndModels(Write("m.mdl", mdl), vtxVmts: []);

            Assert.Equal(names, models);
        }

        private static byte[] Phy(int solids, string text)
        {
            using var stream = new MemoryStream();
            using var w = new BinaryWriter(stream);
            w.Write(16); w.Write(0); w.Write(solids); w.Write(0);
            for (int s = 0; s < solids; s++)
            {
                w.Write(8 + s);
                w.Write(new byte[8 + s]);
            }
            w.Write(Encoding.ASCII.GetBytes(text + "\0"));
            return stream.ToArray();
        }

        /// <summary>
        /// The text section comes after every solid. Skipping only the first read binary as text for
        /// multi-solid breakables, so their gibs were never packed.
        /// </summary>
        [Fact]
        public void GibsAreFoundAfterEverySolid()
        {
            string path = Write("break.phy", Phy(3, "solid {\"index\" \"0\"} break {\"model\" \"props/gib1\" \"health\" \"5\"}"));

            Assert.Equal(["models\\props/gib1.mdl"], AssetUtils.FindPhyGibs(path));
        }

        [Fact]
        public void AReadOnlyPhyIsStillRead()
        {
            string path = Write("ro.phy", Phy(1, "break {\"model\" \"props/gib2.mdl\"}"));
            File.SetAttributes(path, FileAttributes.ReadOnly);

            Assert.Equal(["models\\props/gib2.mdl"], AssetUtils.FindPhyGibs(path));
        }

        [Fact]
        public void ATruncatedPhyDoesNotThrow()
        {
            string path = Write("cut.phy", Phy(1, "break {}")[..30]);

            Assert.Empty(AssetUtils.FindPhyGibs(path));
        }

        /// <summary>
        /// A comment mentioning "file" used to throw out of the line-based reader and cancel the compile.
        /// </summary>
        [Fact]
        public void AManifestCommentIsNotAnEntry()
        {
            string path = Write("m_particles.txt", """
                // custom particle files
                particles_manifest
                {
                    "file" "!particles/custom.pcf"
                    // "file" "particles/old.pcf"
                    "file"   "particles/other.pcf"
                }
                """);

            Assert.Equal(["particles/custom.pcf", "particles/other.pcf"], AssetUtils.FindManifestPcfs(path));
        }

        [Fact]
        public void ASoundscapeWaveKeepsItsPathWithoutTheComment()
        {
            string path = Write("soundscapes_m.txt", """
                "m.street"
                {
                    "playlooping"
                    {
                        "wave" "ambient/city/traffic_loop.wav" // cars
                        "volume" "0.5"
                    }
                    "rndwave" { "wave" "*ambient/birds/bird1.wav" }
                }
                """);

            Assert.Equal(["sound/ambient/city/traffic_loop.wav", "sound/ambient/birds/bird1.wav"], AssetUtils.FindSoundscapeSounds(path));
        }

        [Theory]
        [InlineData("materials/models/props/../shared/x.vmt", "materials/models/shared/x.vmt")]
        [InlineData("materials\\vgui\\..\\hud\\logos\\spray.vmt", "materials/hud/logos/spray.vmt")]
        [InlineData("materials/./a//b.vmt", "materials/a/b.vmt")]
        public void PakPathsResolveDotDotSegmentBySegment(string path, string expected)
        {
            Assert.Equal(expected, PakFile.NormaliseInternalPath(path));
        }

        [Fact]
        public void APakPathClimbingAboveItsRootIsRefused()
        {
            Assert.Null(PakFile.NormaliseInternalPath("materials/../../secret.txt"));
        }

        [Theory]
        [InlineData("materials/brick/wall.vmt", true)]
        [InlineData("materials/brick/wall.vtf", true)]
        [InlineData("models/props/chair.mdl", true)]
        [InlineData("sound/ambient/x.wav", true)]
        [InlineData("particles/fire.pcf", true)]
        [InlineData("models/props/chair.vvd", false)]
        [InlineData("materials/brick/wall.hdr.vtf", false)]
        [InlineData("materials/maps/rp_x/c0_0_0.vmt", false)]
        [InlineData("materials/_rt_fullframefb.vmt", false)]
        [InlineData("sound/Streetwar.d3_c17_13_horn", false)]
        [InlineData("materials/0.vmt", false)]
        [InlineData("materials/.vmt", false)]
        [InlineData("materials/skybox/sky34bk.vmt", true)]
        [InlineData("materials/skybox/sky34.vmt", false)]
        [InlineData("materials/skybox/sky34_hdrbk.vmt", false)]
        [InlineData("materials/vgui/maps/menu_photos_rp_x.vmt", false)]
        public void OnlyAssetsPlayersWouldMissAreReported(string path, bool reported)
        {
            Assert.Equal(reported, MissingAssets.IsReportable(path));
        }

        [Fact]
        public void AGmaHeaderListsItsFiles()
        {
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                void Str(string s) => w.Write(Encoding.UTF8.GetBytes(s + "\0"));
                w.Write(Encoding.ASCII.GetBytes("GMAD"));
                w.Write((byte)3);
                w.Write(76561198000000000UL);
                w.Write(1700000000UL);
                Str("");                  // no required content
                Str("Props"); Str("{}"); Str("author");
                w.Write(1);
                w.Write(1u); Str("materials/props/crate.vmt"); w.Write(120L); w.Write(0u);
                w.Write(2u); Str("models/props/crate.mdl"); w.Write(2048L); w.Write(0u);
                w.Write(0u);
            }

            var names = MissingAssets.GmaFileNames(Write("props.gma", stream.ToArray()));

            Assert.Equal(["materials/props/crate.vmt", "models/props/crate.mdl"], names);
        }
    }
}
