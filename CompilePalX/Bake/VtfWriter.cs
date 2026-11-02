using System;
using System.Collections.Generic;
using System.IO;

namespace CompilePalX.Bake
{
    /// <summary>
    /// Writes an opaque picture as a VTF 7.2: DXT1, with every mip level down to 1x1 so it stays
    /// clean far away, and no low-resolution thumbnail. The levels are averaged down in linear
    /// light, so a dark window does not brighten as it shrinks.
    /// </summary>
    public static class VtfWriter
    {
        private const int FormatDxt1 = 13, FormatDxt5 = 15, FormatDxt1Alpha = 20;
        private const uint FlagClampS = 0x4, FlagClampT = 0x8, FlagOneBitAlpha = 0x1000, FlagEightBitAlpha = 0x2000;

        /// <summary>
        /// The VTF bytes for <paramref name="rgba"/>, <paramref name="width"/> by <paramref name="height"/>,
        /// both powers of two; with <paramref name="alpha"/>, see-through where alpha is under half, and
        /// with <paramref name="smooth"/> as well, see-through by degrees (DXT5), as glass is.
        /// </summary>
        public static byte[] Write(byte[] rgba, int width, int height, bool alpha = false, bool smooth = false)
        {
            alpha |= smooth;
            var levels = new List<(int W, int H, byte[] Pixels)> { (width, height, rgba) };
            while (levels[^1].W > 1 || levels[^1].H > 1)
                levels.Add(Half(levels[^1]));

            using var stream = new MemoryStream();
            using var w = new BinaryWriter(stream);
            w.Write((byte)'V'); w.Write((byte)'T'); w.Write((byte)'F'); w.Write((byte)0);
            w.Write(7u); w.Write(2u);
            w.Write(80u);                       // header size
            w.Write((ushort)width); w.Write((ushort)height);
            w.Write(FlagClampS | FlagClampT | (smooth ? FlagEightBitAlpha : alpha ? FlagOneBitAlpha : 0));
            w.Write((ushort)1); w.Write((ushort)0);   // frames, first frame
            w.Write(0);                          // padding
            w.Write(0.2f); w.Write(0.2f); w.Write(0.2f);  // reflectivity, for VRAD's bounce
            w.Write(0);                          // padding
            w.Write(1f);                         // bump scale
            w.Write(smooth ? FormatDxt5 : alpha ? FormatDxt1Alpha : FormatDxt1);
            w.Write((byte)levels.Count);
            w.Write(-1);                         // no low-resolution image
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1);                  // depth
            while (stream.Length < 80) w.Write((byte)0);

            // smallest level first
            for (int i = levels.Count - 1; i >= 0; i--)
                w.Write(smooth ? Dxt.EncodeDxt5(levels[i].Pixels, levels[i].W, levels[i].H)
                    : alpha ? Dxt.EncodeDxt1Alpha(levels[i].Pixels, levels[i].W, levels[i].H) : Dxt.EncodeDxt1(levels[i].Pixels, levels[i].W, levels[i].H));
            w.Flush();
            return stream.ToArray();
        }

        private static (int W, int H, byte[] Pixels) Half((int W, int H, byte[] Pixels) level)
        {
            int w = Math.Max(1, level.W / 2), h = Math.Max(1, level.H / 2);
            var output = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        // colour weighted by alpha, so what is cut out does not darken the edges kept
                        double sum = 0, weight = 0;
                        for (int dy = 0; dy < 2; dy++)
                            for (int dx = 0; dx < 2; dx++)
                            {
                                int sx = Math.Min(x * 2 + dx, level.W - 1), sy = Math.Min(y * 2 + dy, level.H - 1);
                                int at = (sy * level.W + sx) * 4;
                                double v = level.Pixels[at + c] / 255.0, a = c == 3 ? 1 : level.Pixels[at + 3] / 255.0;
                                sum += (c == 3 ? v : Math.Pow(v, 2.2)) * a;
                                weight += a;
                            }
                        double mean = weight > 0 ? sum / weight : 0;
                        output[(y * w + x) * 4 + c] = (byte)Math.Round(255 * (c == 3 ? mean : Math.Pow(mean, 1 / 2.2)));
                    }
            return (w, h, output);
        }
    }
}
