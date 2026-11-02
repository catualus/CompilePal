using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using CompilePalX.Bake;
using CompilePalX.Preview;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// The pieces the window bake stands on: S3TC both ways, the VTF it writes, and putting files
    /// into a map's pakfile without disturbing what was there.
    /// </summary>
    public class WindowBakeTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "CompilePalBake_" + Guid.NewGuid().ToString("N"));

        public WindowBakeTests() => Directory.CreateDirectory(root);

        public void Dispose()
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }

        private static byte[] Gradient(int w, int h)
        {
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    rgba[o] = (byte)(x * 255 / (w - 1));
                    rgba[o + 1] = (byte)(y * 255 / (h - 1));
                    rgba[o + 2] = 80;
                    rgba[o + 3] = 255;
                }
            return rgba;
        }

        [Fact]
        public void Dxt1KeepsAPictureCloseToWhatItWas()
        {
            var rgba = Gradient(16, 8);
            var back = Dxt.Decode("dxt1", 16, 8, Dxt.EncodeDxt1(rgba, 16, 8));

            // red runs across and green down, so a block's colours do not lie on one line, which is
            // all DXT1 can store: some error is the format's, not the encoder's
            var errors = Enumerable.Range(0, rgba.Length).Where(i => i % 4 != 3).Select(i => Math.Abs(rgba[i] - back[i])).ToList();
            Assert.True(errors.Average() <= 8, $"mean channel error {errors.Average():0.0}");
            Assert.True(errors.Max() <= 40, $"worst channel error {errors.Max()}");
            Assert.All(Enumerable.Range(0, 16 * 8), p => Assert.Equal(255, back[p * 4 + 3]));
        }

        [Fact]
        public void AFlatBlockEncodesToItsOwnColour()
        {
            var rgba = Enumerable.Repeat(new byte[] { 40, 80, 120, 255 }, 16).SelectMany(b => b).ToArray();
            var back = Dxt.Decode("dxt1", 4, 4, Dxt.EncodeDxt1(rgba, 4, 4));
            Assert.InRange(back[0], 36, 44);
            Assert.InRange(back[1], 76, 84);
            Assert.InRange(back[2], 116, 124);
        }

        [Fact]
        public void ACutOutKeepsItsHolesAndItsColours()
        {
            // the left half see-through, the right half the gradient
            var rgba = Gradient(16, 8);
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 6; x++)
                    rgba[(y * 16 + x) * 4 + 3] = 0;
            var back = Dxt.Decode("dxt1", 16, 8, Dxt.EncodeDxt1Alpha(rgba, 16, 8));

            for (int p = 0; p < 16 * 8; p++)
            {
                bool hole = rgba[p * 4 + 3] == 0;
                Assert.Equal(hole ? 0 : 255, back[p * 4 + 3]);
                if (!hole)
                    for (int c = 0; c < 3; c++)
                        Assert.True(Math.Abs(rgba[p * 4 + c] - back[p * 4 + c]) <= 40, $"pixel {p} channel {c}");
            }
        }

        [Fact]
        public void GlassKeepsItsAlphaByDegrees()
        {
            // alpha running left to right, clear in the first column
            var rgba = Gradient(16, 8);
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 16; x++)
                    rgba[(y * 16 + x) * 4 + 3] = (byte)(x * 17);
            var back = Dxt.Decode("dxt5", 16, 8, Dxt.EncodeDxt5(rgba, 16, 8));

            for (int p = 0; p < 16 * 8; p++)
            {
                Assert.True(Math.Abs(rgba[p * 4 + 3] - back[p * 4 + 3]) <= 6, $"pixel {p} alpha {back[p * 4 + 3]} for {rgba[p * 4 + 3]}");
                if (rgba[p * 4 + 3] > 0)
                    for (int c = 0; c < 3; c++)
                        Assert.True(Math.Abs(rgba[p * 4 + c] - back[p * 4 + c]) <= 40, $"pixel {p} channel {c}");
            }

            var vtf = VtfWriter.Write(Gradient(32, 32), 32, 32, smooth: true);
            Assert.Equal(15, BitConverter.ToInt32(vtf, 52));
            Assert.NotEqual(0u, BitConverter.ToUInt32(vtf, 20) & 0x2000);
            Assert.Equal("dxt5", Vtf.Read(vtf)!.Format);
            Assert.Contains("\"$translucent\" \"1\"", WindowBaker.Vmt("viswright/map/w_1_2_3_a_front", translucent: true));
        }

        [Fact]
        public void AnOpaqueCutOutIsPlainDxt1()
        {
            var rgba = Gradient(16, 8);
            Assert.Equal(Dxt.EncodeDxt1(rgba, 16, 8), Dxt.EncodeDxt1Alpha(rgba, 16, 8));
        }

        [Fact]
        public void ACutOutVtfIsMarkedOneBitAlpha()
        {
            var rgba = Gradient(32, 32);
            rgba[3] = 0;
            var vtf = VtfWriter.Write(rgba, 32, 32, alpha: true);
            Assert.Equal(20, BitConverter.ToInt32(vtf, 52));
            Assert.NotEqual(0u, BitConverter.ToUInt32(vtf, 20) & 0x1000);
            Assert.Equal("dxt1", Vtf.Read(vtf)!.Format);

            Assert.Contains("\"$alphatest\" \"1\"", WindowBaker.Vmt("viswright/map/w_1_2_3_a_slice0", alpha: true));
            Assert.DoesNotContain("$alphatest", WindowBaker.Vmt("viswright/map/w_1_2_3_a_back"));
        }

        [Fact]
        public void TheBakedVtfReadsBackWithEveryMipLevel()
        {
            var vtf = VtfWriter.Write(Gradient(64, 32), 64, 32);
            var texture = Vtf.Read(vtf)!;

            Assert.Equal("dxt1", texture.Format);
            Assert.Equal((64, 32), (texture.Width, texture.Height));
            Assert.Equal(Math.Min(7, texture.Levels.Count), texture.Levels.Count);
            Assert.Equal((1, 1), (texture.Levels[^1].Width, texture.Levels[^1].Height));
        }

        [Fact]
        public void FilesGoIntoThePakfileBesideWhatWasThere()
        {
            // A BSP of a header and a pakfile lump holding one file.
            var old = PakWriter.Rebuild([], new HashSet<string>(), new Dictionary<string, byte[]> { ["materials/old.vmt"] = Encoding.ASCII.GetBytes("old") });
            string bsp = Path.Combine(root, "map.bsp");
            var bytes = new byte[1036 + old.Length];
            BitConverter.GetBytes(0x50534256).CopyTo(bytes, 0);
            BitConverter.GetBytes(20).CopyTo(bytes, 4);
            BitConverter.GetBytes(1036).CopyTo(bytes, 8 + 40 * 16);
            BitConverter.GetBytes(old.Length).CopyTo(bytes, 8 + 40 * 16 + 4);
            old.CopyTo(bytes, 1036);
            File.WriteAllBytes(bsp, bytes);

            PakWriter.AddFiles(bsp, new Dictionary<string, byte[]>
            {
                ["materials/viswright/x/w_a.vtf"] = [1, 2, 3],
                ["materials/old.vmt"] = Encoding.ASCII.GetBytes("new"),
            });

            var after = File.ReadAllBytes(bsp);
            int offset = BitConverter.ToInt32(after, 8 + 40 * 16), length = BitConverter.ToInt32(after, 8 + 40 * 16 + 4);
            Assert.True(offset >= 1036 + old.Length, "the new pakfile goes after the old one");
            var pak = new PakArchive(after.AsSpan(offset, length).ToArray());
            Assert.Equal(2, pak.Count);
            Assert.Equal([1, 2, 3], pak.Read("materials/viswright/x/w_a.vtf"));
            Assert.Equal("new", Encoding.ASCII.GetString(pak.Read("materials/old.vmt")!));
        }

        [Fact]
        public void ACrcMatchesTheZipStandard()
        {
            Assert.Equal(0xCBF43926u, PakWriter.Crc32(Encoding.ASCII.GetBytes("123456789")));
        }

        [Fact]
        public void TheBakedMaterialIsUnlitAndNamesItsPicture()
        {
            string vmt = WindowBaker.Vmt("viswright/map/w_1_2_3_a");
            Assert.StartsWith("\"UnlitGeneric\"", vmt);
            Assert.Contains("\"$basetexture\" \"viswright/map/w_1_2_3_a\"", vmt);
            Assert.True(WindowBaker.IsStandIn("VISWRIGHT/MAP/W_1_2_3_A"));
            Assert.False(WindowBaker.IsStandIn("tools/toolsblack"));
        }
    }
}
