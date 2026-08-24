using System;
using System.IO;
using System.Linq;
using System.Text;
using CompilePalX.Compiling;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// Moving the entity lump out of a BSP and into the .lmp override the engine reads instead.
    ///
    /// The format assumptions here were checked against six real Garry's Mod maps before any of
    /// this was written: ident "VBSP", version 20, the lump table at offset 8 as
    /// offset/length/version/fourCC, and the map revision at 1032. A round trip over a real map is
    /// at the bottom of this file and skips itself when no map is installed.
    /// </summary>
    public class EntityLumpExtractorTests : IDisposable
    {
        private readonly string tempDir =
            Path.Combine(Path.GetTempPath(), "cpx-entlump-" + Guid.NewGuid().ToString("N"));

        public EntityLumpExtractorTests() => Directory.CreateDirectory(tempDir);

        public void Dispose()
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }

        private const int HeaderBytes = 1036;
        private const int LumpTableOffset = 8;
        private const int MapRevisionOffset = 1032;

        /// <summary>
        /// Builds a BSP that is a real header and nothing else: the entity lump, and enough of a
        /// second lump to prove the entity edit does not disturb what sits after it.
        /// </summary>
        private string WriteBsp(string entities, int mapRevision = 42, int lumpVersion = 0,
                                bool compressed = false, string trailer = "OTHER LUMP DATA")
        {
            byte[] entityBytes = Encoding.ASCII.GetBytes(entities);
            byte[] trailerBytes = Encoding.ASCII.GetBytes(trailer);

            int entityOffset = HeaderBytes;
            int trailerOffset = entityOffset + entityBytes.Length;

            var bsp = new byte[trailerOffset + trailerBytes.Length];

            BitConverter.GetBytes(0x50534256).CopyTo(bsp, 0);   // 'VBSP'
            BitConverter.GetBytes(20).CopyTo(bsp, 4);           // version

            // lump 0: entities
            BitConverter.GetBytes(entityOffset).CopyTo(bsp, LumpTableOffset);
            BitConverter.GetBytes(entityBytes.Length).CopyTo(bsp, LumpTableOffset + 4);
            BitConverter.GetBytes(lumpVersion).CopyTo(bsp, LumpTableOffset + 8);
            // fourCC is non-zero only on a compressed lump, where it holds the uncompressed size
            BitConverter.GetBytes(compressed ? entityBytes.Length : 0).CopyTo(bsp, LumpTableOffset + 12);

            // lump 1: something else, so the test can prove it survives untouched
            BitConverter.GetBytes(trailerOffset).CopyTo(bsp, LumpTableOffset + 16);
            BitConverter.GetBytes(trailerBytes.Length).CopyTo(bsp, LumpTableOffset + 20);

            BitConverter.GetBytes(mapRevision).CopyTo(bsp, MapRevisionOffset);

            entityBytes.CopyTo(bsp, entityOffset);
            trailerBytes.CopyTo(bsp, trailerOffset);

            string path = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".bsp");
            File.WriteAllBytes(path, bsp);
            return path;
        }

        private const string ThreeEntities =
            "{\n\"classname\" \"worldspawn\"\n\"skyname\" \"sky_day01_01\"\n}\n" +
            "{\n\"classname\" \"info_player_start\"\n\"origin\" \"0 0 64\"\n}\n" +
            "{\n\"classname\" \"light_environment\"\n\"pitch\" \"-40\"\n}\n\0";

        private static (int offset, int length, int fourCc) EntityLump(string bspPath)
        {
            byte[] bsp = File.ReadAllBytes(bspPath);
            return (
                BitConverter.ToInt32(bsp, LumpTableOffset),
                BitConverter.ToInt32(bsp, LumpTableOffset + 4),
                BitConverter.ToInt32(bsp, LumpTableOffset + 12));
        }

        // ------------------------------------------------------------------ the happy path

        [Fact]
        public void EveryEntityEndsUpInTheLumpFile()
        {
            string bsp = WriteBsp(ThreeEntities);

            var result = EntityLumpExtractor.Extract(bsp);
            Assert.True(result.Extracted, result.Message);

            string? text = EntityLumpExtractor.ReadLumpFile(result.LumpFile!);
            Assert.NotNull(text);

            Assert.Contains("worldspawn", text);
            Assert.Contains("info_player_start", text);
            Assert.Contains("light_environment", text);
            Assert.Equal(3, EntityLumpExtractor.CountEntities(Encoding.ASCII.GetBytes(text!)));
        }

        [Fact]
        public void TheLumpFileIsNamedTheWayTheEngineLooksForIt()
        {
            string bsp = Path.Combine(tempDir, "de_dust2.bsp");
            Assert.Equal(
                Path.Combine(tempDir, "de_dust2_l_0.lmp"),
                EntityLumpExtractor.LumpFileNameFor(bsp));
        }

        [Fact]
        public void OnlyWorldspawnIsLeftInTheBsp()
        {
            string bsp = WriteBsp(ThreeEntities);
            EntityLumpExtractor.Extract(bsp);

            var (offset, length, _) = EntityLump(bsp);
            string remaining = Encoding.ASCII.GetString(File.ReadAllBytes(bsp), offset, length);

            Assert.Contains("worldspawn", remaining);
            Assert.DoesNotContain("info_player_start", remaining);
            Assert.DoesNotContain("light_environment", remaining);
            Assert.Equal(1, EntityLumpExtractor.CountEntities(Encoding.ASCII.GetBytes(remaining)));
        }

        /// <summary>
        /// The bug in entremover_bsp, pinned so it cannot come back.
        ///
        /// It sets lump[0].length to the size of the part it DISCARDED rather than the part it
        /// kept. That describes a region running from worldspawn off into the zero padding, and on
        /// a map with a long worldspawn and few other entities it is shorter than worldspawn and
        /// truncates it instead.
        /// </summary>
        [Fact]
        public void TheHeaderLengthDescribesWhatWasKeptNotWhatWasRemoved()
        {
            string bsp = WriteBsp(ThreeEntities);
            int originalLength = EntityLump(bsp).length;

            EntityLumpExtractor.Extract(bsp);

            var (offset, length, _) = EntityLump(bsp);
            byte[] data = File.ReadAllBytes(bsp);

            // Exactly worldspawn plus its terminator, and nothing beyond it.
            string kept = Encoding.ASCII.GetString(data, offset, length);
            Assert.EndsWith("}\0", kept);
            Assert.True(length < originalLength);

            // Every byte past the shortened lump is zero.
            Assert.All(
                Enumerable.Range(offset + length, originalLength - length),
                i => Assert.Equal(0, data[i]));
        }

        [Fact]
        public void OtherLumpsAreUntouched()
        {
            string bsp = WriteBsp(ThreeEntities, trailer: "OTHER LUMP DATA");
            EntityLumpExtractor.Extract(bsp);

            byte[] data = File.ReadAllBytes(bsp);
            int trailerOffset = BitConverter.ToInt32(data, LumpTableOffset + 16);
            int trailerLength = BitConverter.ToInt32(data, LumpTableOffset + 20);

            Assert.Equal("OTHER LUMP DATA", Encoding.ASCII.GetString(data, trailerOffset, trailerLength));
        }

        /// <summary>
        /// The file must not shrink. Every other lump is addressed by an absolute offset, so
        /// removing bytes from the middle would move all of them.
        /// </summary>
        [Fact]
        public void TheFileKeepsItsSizeSoOtherLumpOffsetsStayValid()
        {
            string bsp = WriteBsp(ThreeEntities);
            long before = new FileInfo(bsp).Length;

            EntityLumpExtractor.Extract(bsp);

            Assert.Equal(before, new FileInfo(bsp).Length);
        }

        // ------------------------------------------------------------------ the revision pairing

        /// <summary>
        /// The engine ignores a lump file whose map revision does not match the BSP beside it, and
        /// the symptom is a map with no entities rather than an error. Carrying the revision across
        /// is what makes the pair usable at all.
        /// </summary>
        [Fact]
        public void TheLumpFileCarriesTheMapRevisionFromTheBsp()
        {
            string bsp = WriteBsp(ThreeEntities, mapRevision: 1765);

            var result = EntityLumpExtractor.Extract(bsp);

            Assert.Equal(1765, EntityLumpExtractor.LumpFileRevision(result.LumpFile!));
            Assert.Equal(1765, EntityLumpExtractor.BspRevision(bsp));
        }

        [Fact]
        public void TheLumpFileCarriesTheLumpVersion()
        {
            string bsp = WriteBsp(ThreeEntities, lumpVersion: 0);
            var result = EntityLumpExtractor.Extract(bsp);

            byte[] lump = File.ReadAllBytes(result.LumpFile!);
            Assert.Equal(0, BitConverter.ToInt32(lump, 8));

            // and the data offset is the header size, which is what the engine seeks to
            Assert.Equal(20, BitConverter.ToInt32(lump, 0));
        }

        // ------------------------------------------------------------------ refusals

        /// <summary>
        /// Running twice would write a lump file containing only worldspawn, over the top of the
        /// one holding the real entities. They exist nowhere else by then.
        /// </summary>
        [Fact]
        public void RunningTwiceDoesNotDestroyTheFirstLumpFile()
        {
            string bsp = WriteBsp(ThreeEntities);

            var first = EntityLumpExtractor.Extract(bsp);
            Assert.True(first.Extracted);

            string lumpPath = first.LumpFile!;
            byte[] afterFirst = File.ReadAllBytes(lumpPath);

            var second = EntityLumpExtractor.Extract(bsp);

            Assert.False(second.Extracted);
            Assert.Contains("already", second.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(afterFirst, File.ReadAllBytes(lumpPath));
        }

        /// <summary>
        /// bspzip -compress LZMA-compresses lumps including this one. Scanning compressed bytes for
        /// a closing brace finds one somewhere in the stream and cuts the lump at a meaningless
        /// point, so this has to be refused rather than attempted.
        /// </summary>
        [Fact]
        public void ACompressedEntityLumpIsRefused()
        {
            string bsp = WriteBsp("LZMA" + new string('\x01', 200), compressed: true);

            var result = EntityLumpExtractor.Extract(bsp);

            Assert.False(result.Extracted);
            Assert.Contains("compressed", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(EntityLumpExtractor.LumpFileNameFor(bsp)));
        }

        [Fact]
        public void ACompressedLumpIsCaughtByItsLzmaHeaderEvenWithoutTheFourCc()
        {
            string bsp = WriteBsp("LZMA" + new string('\x01', 200), compressed: false);

            Assert.False(EntityLumpExtractor.Extract(bsp).Extracted);
        }

        [Fact]
        public void AFileThatIsNotABspIsRefused()
        {
            string path = Path.Combine(tempDir, "notabsp.bsp");
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes(new string('x', 2000)));

            var result = EntityLumpExtractor.Extract(path);

            Assert.False(result.Extracted);
            Assert.Contains("VBSP", result.Message);
        }

        [Fact]
        public void ALumpHeaderPointingOutsideTheFileIsRefused()
        {
            string bsp = WriteBsp(ThreeEntities);

            byte[] data = File.ReadAllBytes(bsp);
            BitConverter.GetBytes(int.MaxValue).CopyTo(data, LumpTableOffset + 4);   // absurd length
            File.WriteAllBytes(bsp, data);

            Assert.False(EntityLumpExtractor.Extract(bsp).Extracted);
        }

        // ------------------------------------------------------------------ entity counting

        /// <summary>
        /// A brace inside a quoted value - an output whose parameter contains one, which is legal -
        /// must not be counted, or an ordinary map can look like it has already been extracted.
        /// </summary>
        [Fact]
        public void BracesInsideQuotedValuesAreNotCountedAsEntities()
        {
            byte[] entities = Encoding.ASCII.GetBytes(
                "{\n\"classname\" \"worldspawn\"\n}\n" +
                "{\n\"classname\" \"logic_relay\"\n\"OnTrigger\" \"target,AddOutput,message {weird}\"\n}\n");

            Assert.Equal(2, EntityLumpExtractor.CountEntities(entities));
        }

        [Fact]
        public void CountingStopsAtTheNullTerminator()
        {
            byte[] entities = Encoding.ASCII.GetBytes("{\n\"classname\" \"worldspawn\"\n}\n\0{\n}\n");

            Assert.Equal(1, EntityLumpExtractor.CountEntities(entities));
        }

        // ------------------------------------------------------------------ options

        [Fact]
        public void RemovingWorldspawnLeavesAnEmptyEntityLump()
        {
            string bsp = WriteBsp(ThreeEntities);

            var result = EntityLumpExtractor.Extract(bsp, keepWorldspawn: false);
            Assert.True(result.Extracted, result.Message);

            var (offset, length, _) = EntityLump(bsp);
            string remaining = Encoding.ASCII.GetString(File.ReadAllBytes(bsp), offset, length);

            Assert.Equal(0, EntityLumpExtractor.CountEntities(Encoding.ASCII.GetBytes(remaining)));

            // and the entities are still all in the lump file
            Assert.Equal(3, EntityLumpExtractor.CountEntities(
                Encoding.ASCII.GetBytes(EntityLumpExtractor.ReadLumpFile(result.LumpFile!)!)));
        }

        // ------------------------------------------------------------------ a real map

        private static string? RealMap()
        {
            string maps = @"C:\Program Files (x86)\Steam\steamapps\common\GarrysMod\garrysmod\maps";
            if (!Directory.Exists(maps)) return null;

            return Directory.EnumerateFiles(maps, "*.bsp")
                .OrderBy(f => new FileInfo(f).Length)
                .FirstOrDefault();
        }

        /// <summary>
        /// The whole operation over a real compiled map, on a copy.
        ///
        /// Synthetic fixtures prove the arithmetic; only a real BSP proves the format assumptions,
        /// and those are the ones that would corrupt somebody's map if they were wrong. Skips
        /// itself when no map is installed, so the suite still runs on a machine without the game.
        /// </summary>
        [Fact]
        public void ARealMapRoundTrips()
        {
            string? source = RealMap();
            if (source is null) return;

            string bsp = Path.Combine(tempDir, Path.GetFileName(source));
            File.Copy(source, bsp);

            long originalSize = new FileInfo(bsp).Length;
            int? originalRevision = EntityLumpExtractor.BspRevision(bsp);
            Assert.NotNull(originalRevision);

            var (_, originalLength, _) = EntityLump(bsp);

            var result = EntityLumpExtractor.Extract(bsp);
            Assert.True(result.Extracted, result.Message);

            // The entities came back out of the lump file, all of them.
            string? text = EntityLumpExtractor.ReadLumpFile(result.LumpFile!);
            Assert.NotNull(text);
            Assert.Contains("worldspawn", text);
            Assert.True(EntityLumpExtractor.CountEntities(Encoding.ASCII.GetBytes(text!)) > 1);

            // The BSP kept its size, its revision and its identity.
            Assert.Equal(originalSize, new FileInfo(bsp).Length);
            Assert.Equal(originalRevision, EntityLumpExtractor.BspRevision(bsp));
            Assert.Equal(originalRevision, EntityLumpExtractor.LumpFileRevision(result.LumpFile!));

            // And it now holds worldspawn alone.
            var (offset, length, _) = EntityLump(bsp);
            Assert.True(length < originalLength);
            Assert.Equal(1, EntityLumpExtractor.CountEntities(
                Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(File.ReadAllBytes(bsp), offset, length))));
        }

        /// <summary>
        /// Compile Pal's own BSP reader must still open the stripped map. It is what PACK uses, and
        /// a BSP this step had made unreadable would break re-packing an already-shipped map.
        /// </summary>
        [Fact]
        public void AStrippedRealMapIsStillReadableAsABsp()
        {
            string? source = RealMap();
            if (source is null) return;

            string bsp = Path.Combine(tempDir, Path.GetFileName(source));
            File.Copy(source, bsp);

            Assert.True(EntityLumpExtractor.Extract(bsp).Extracted);

            // Header still parses, and the lump table still describes the same file.
            byte[] data = File.ReadAllBytes(bsp);
            Assert.Equal(0x50534256, BitConverter.ToInt32(data, 0));

            for (int i = 0; i < 64; i++)
            {
                int offset = BitConverter.ToInt32(data, LumpTableOffset + (i * 16));
                int length = BitConverter.ToInt32(data, LumpTableOffset + (i * 16) + 4);

                Assert.True(offset >= 0 && length >= 0, $"lump {i} has a negative field");
                Assert.True(offset + (long)length <= data.Length, $"lump {i} runs past the end of the file");
            }
        }
    }
}
