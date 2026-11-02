using System;

namespace CompilePalX.Bake
{
    /// <summary>
    /// S3TC block compression, both ways, on the CPU.
    ///
    /// The preview hands DXT data to the GPU untouched; the bake samples textures itself, so it
    /// decodes them here (DXT1, DXT3, DXT5 to RGBA8), and it writes its pictures as DXT1, the
    /// format the game's own opaque textures use, at an eighth of the size of RGB.
    /// </summary>
    public static class Dxt
    {
        /// <summary>Decodes one mip level of <paramref name="format"/> ("dxt1", "dxt3", "dxt5") to RGBA8.</summary>
        public static byte[] Decode(string format, int width, int height, byte[] data)
        {
            var rgba = new byte[width * height * 4];
            int blockSize = format == "dxt1" ? 8 : 16;
            int blocksWide = Math.Max(1, (width + 3) / 4), blocksHigh = Math.Max(1, (height + 3) / 4);
            Span<byte> colours = stackalloc byte[16];
            Span<byte> alphas = stackalloc byte[16];
            for (int by = 0; by < blocksHigh; by++)
                for (int bx = 0; bx < blocksWide; bx++)
                {
                    int at = (by * blocksWide + bx) * blockSize;
                    if (at + blockSize > data.Length)
                        return rgba;
                    int colourAt = format == "dxt1" ? at : at + 8;
                    ushort c0 = BitConverter.ToUInt16(data, colourAt), c1 = BitConverter.ToUInt16(data, colourAt + 2);
                    Expand(c0, colours[..4]);
                    Expand(c1, colours.Slice(4, 4));
                    if (c0 > c1 || format != "dxt1")
                    {
                        for (int k = 0; k < 3; k++)
                        {
                            colours[8 + k] = (byte)((2 * colours[k] + colours[4 + k]) / 3);
                            colours[12 + k] = (byte)((colours[k] + 2 * colours[4 + k]) / 3);
                        }
                        colours[11] = colours[15] = 255;
                    }
                    else
                    {
                        for (int k = 0; k < 3; k++)
                            colours[8 + k] = (byte)((colours[k] + colours[4 + k]) / 2);
                        colours[11] = 255;
                        colours[12] = colours[13] = colours[14] = colours[15] = 0;
                    }
                    uint indices = BitConverter.ToUInt32(data, colourAt + 4);

                    if (format == "dxt3")
                        for (int p = 0; p < 16; p++)
                            alphas[p] = (byte)(((data[at + p / 2] >> (4 * (p & 1))) & 0xF) * 17);
                    else if (format == "dxt5")
                    {
                        byte a0 = data[at], a1 = data[at + 1];
                        ulong bits = 0;
                        for (int k = 0; k < 6; k++) bits |= (ulong)data[at + 2 + k] << (8 * k);
                        for (int p = 0; p < 16; p++)
                        {
                            int code = (int)((bits >> (3 * p)) & 7);
                            alphas[p] = code switch
                            {
                                0 => a0,
                                1 => a1,
                                _ when a0 > a1 => (byte)(((8 - code) * a0 + (code - 1) * a1) / 7),
                                6 => 0,
                                7 => 255,
                                _ => (byte)(((6 - code) * a0 + (code - 1) * a1) / 5),
                            };
                        }
                    }

                    for (int p = 0; p < 16; p++)
                    {
                        int x = bx * 4 + (p & 3), y = by * 4 + (p >> 2);
                        if (x >= width || y >= height)
                            continue;
                        int code = (int)((indices >> (2 * p)) & 3);
                        int o = (y * width + x) * 4;
                        rgba[o] = colours[code * 4];
                        rgba[o + 1] = colours[code * 4 + 1];
                        rgba[o + 2] = colours[code * 4 + 2];
                        rgba[o + 3] = format == "dxt1" ? colours[code * 4 + 3] : alphas[p];
                    }
                }
            return rgba;
        }

        private static void Expand(ushort c, Span<byte> into)
        {
            int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
            into[0] = (byte)((r << 3) | (r >> 2));
            into[1] = (byte)((g << 2) | (g >> 4));
            into[2] = (byte)((b << 3) | (b >> 2));
            into[3] = 255;
        }

        private static ushort Pack(int r, int g, int b) =>
            (ushort)(((Math.Clamp(r, 0, 255) * 31 + 127) / 255 << 11) | ((Math.Clamp(g, 0, 255) * 63 + 127) / 255 << 5) | ((Math.Clamp(b, 0, 255) * 31 + 127) / 255));

        /// <summary>
        /// Encodes RGBA8 as DXT1 with one-bit alpha: a block with a pixel under half alpha uses three
        /// colours and a fourth that is transparent, as the format allows; an opaque block is
        /// encoded as <see cref="EncodeDxt1"/> does.
        /// </summary>
        public static byte[] EncodeDxt1Alpha(byte[] rgba, int width, int height)
        {
            var output = EncodeDxt1(rgba, width, height);
            int blocksWide = Math.Max(1, (width + 3) / 4), blocksHigh = Math.Max(1, (height + 3) / 4);
            for (int by = 0; by < blocksHigh; by++)
                for (int bx = 0; bx < blocksWide; bx++)
                {
                    bool any = false;
                    int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0, opaque = 0;
                    for (int p = 0; p < 16; p++)
                    {
                        int x = Math.Min(bx * 4 + (p & 3), width - 1), y = Math.Min(by * 4 + (p >> 2), height - 1);
                        int o = (y * width + x) * 4;
                        if (rgba[o + 3] < 128) { any = true; continue; }
                        opaque++;
                        minR = Math.Min(minR, rgba[o]); minG = Math.Min(minG, rgba[o + 1]); minB = Math.Min(minB, rgba[o + 2]);
                        maxR = Math.Max(maxR, rgba[o]); maxG = Math.Max(maxG, rgba[o + 1]); maxB = Math.Max(maxB, rgba[o + 2]);
                    }
                    if (!any)
                        continue;
                    // three-colour mode needs the first end colour no greater than the second
                    ushort c0 = opaque > 0 ? Pack(minR, minG, minB) : (ushort)0, c1 = opaque > 0 ? Pack(maxR, maxG, maxB) : (ushort)0;
                    if (c0 > c1) (c0, c1) = (c1, c0);
                    Span<byte> palette = stackalloc byte[12];
                    Expand(c0, palette[..4]);
                    Expand(c1, palette.Slice(4, 4));
                    for (int k = 0; k < 3; k++)
                        palette[8 + k] = (byte)((palette[k] + palette[4 + k]) / 2);
                    uint indices = 0;
                    for (int p = 0; p < 16; p++)
                    {
                        int x = Math.Min(bx * 4 + (p & 3), width - 1), y = Math.Min(by * 4 + (p >> 2), height - 1);
                        int o = (y * width + x) * 4;
                        int best = 3;
                        if (rgba[o + 3] >= 128)
                        {
                            int bestDistance = int.MaxValue;
                            for (int c = 0; c < 3; c++)
                            {
                                int dr = rgba[o] - palette[c * 4], dg = rgba[o + 1] - palette[c * 4 + 1], db = rgba[o + 2] - palette[c * 4 + 2];
                                int d = dr * dr + dg * dg + db * db;
                                if (d < bestDistance) (best, bestDistance) = (c, d);
                            }
                        }
                        indices |= (uint)best << (2 * p);
                    }
                    int at = (by * blocksWide + bx) * 8;
                    BitConverter.GetBytes(c0).CopyTo(output, at);
                    BitConverter.GetBytes(c1).CopyTo(output, at + 2);
                    BitConverter.GetBytes(indices).CopyTo(output, at + 4);
                }
            return output;
        }

        /// <summary>
        /// Encodes RGBA8 as DXT5: alpha in eight steps between each block's least and most, colour as
        /// <see cref="EncodeDxt1"/> does from the pixels that are not wholly clear, so the colour of
        /// glass is not pulled toward the black of the clear pixels beside it.
        /// </summary>
        public static byte[] EncodeDxt5(byte[] rgba, int width, int height)
        {
            int blocksWide = Math.Max(1, (width + 3) / 4), blocksHigh = Math.Max(1, (height + 3) / 4);
            var colourSource = (byte[])rgba.Clone();
            var output = new byte[blocksWide * blocksHigh * 16];
            Span<byte> alphas = stackalloc byte[16];
            for (int by = 0; by < blocksHigh; by++)
                for (int bx = 0; bx < blocksWide; bx++)
                {
                    int lo = 255, hi = 0, seen = 0;
                    double mr = 0, mg = 0, mb = 0;
                    for (int p = 0; p < 16; p++)
                    {
                        int x = Math.Min(bx * 4 + (p & 3), width - 1), y = Math.Min(by * 4 + (p >> 2), height - 1);
                        int o = (y * width + x) * 4;
                        alphas[p] = rgba[o + 3];
                        lo = Math.Min(lo, alphas[p]); hi = Math.Max(hi, alphas[p]);
                        if (alphas[p] > 0) { mr += rgba[o]; mg += rgba[o + 1]; mb += rgba[o + 2]; seen++; }
                    }
                    // clear pixels take the block's colour, so they do not stretch its end colours
                    for (int p = 0; p < 16; p++)
                    {
                        int x = Math.Min(bx * 4 + (p & 3), width - 1), y = Math.Min(by * 4 + (p >> 2), height - 1);
                        int o = (y * width + x) * 4;
                        if (rgba[o + 3] == 0 && seen > 0)
                        {
                            colourSource[o] = (byte)(mr / seen); colourSource[o + 1] = (byte)(mg / seen); colourSource[o + 2] = (byte)(mb / seen);
                        }
                    }

                    // eight steps from the most to the least: a0 > a1 picks that mode
                    int at = (by * blocksWide + bx) * 16;
                    output[at] = (byte)hi;
                    output[at + 1] = (byte)lo;
                    ulong indices = 0;
                    if (hi > lo)
                        for (int p = 0; p < 16; p++)
                        {
                            // step 0 is a0, 1 is a1, 2..7 run from a0 toward a1
                            int best = 0, bestDistance = int.MaxValue;
                            for (int k = 0; k < 8; k++)
                            {
                                int value = k == 0 ? hi : k == 1 ? lo : ((8 - k) * hi + (k - 1) * lo) / 7;
                                int d = Math.Abs(value - alphas[p]);
                                if (d < bestDistance) (best, bestDistance) = (k, d);
                            }
                            indices |= (ulong)best << (3 * p);
                        }
                    for (int k = 0; k < 6; k++)
                        output[at + 2 + k] = (byte)(indices >> (8 * k));
                }
            var colour = EncodeDxt1(colourSource, width, height);
            for (int b = 0; b < blocksWide * blocksHigh; b++)
                Array.Copy(colour, b * 8, output, b * 16 + 8, 8);
            return output;
        }

        /// <summary>
        /// Encodes RGBA8 as opaque DXT1. Each block's two end colours are its extremes along the
        /// direction its colours spread most, which is what better encoders start from; it is
        /// plenty for a picture seen from a thousand units away.
        /// </summary>
        public static byte[] EncodeDxt1(byte[] rgba, int width, int height)
        {
            int blocksWide = Math.Max(1, (width + 3) / 4), blocksHigh = Math.Max(1, (height + 3) / 4);
            var output = new byte[blocksWide * blocksHigh * 8];
            Span<int> px = stackalloc int[48];
            for (int by = 0; by < blocksHigh; by++)
                for (int bx = 0; bx < blocksWide; bx++)
                {
                    // the block's pixels, repeating the edge for blocks past a small texture's size
                    double mr = 0, mg = 0, mb = 0;
                    for (int p = 0; p < 16; p++)
                    {
                        int x = Math.Min(bx * 4 + (p & 3), width - 1), y = Math.Min(by * 4 + (p >> 2), height - 1);
                        int o = (y * width + x) * 4;
                        px[p * 3] = rgba[o]; px[p * 3 + 1] = rgba[o + 1]; px[p * 3 + 2] = rgba[o + 2];
                        mr += rgba[o]; mg += rgba[o + 1]; mb += rgba[o + 2];
                    }
                    mr /= 16; mg /= 16; mb /= 16;

                    // the main axis of the colours, by a few rounds of power iteration on their covariance
                    double crr = 0, crg = 0, crb = 0, cgg = 0, cgb = 0, cbb = 0;
                    for (int p = 0; p < 16; p++)
                    {
                        double r = px[p * 3] - mr, g = px[p * 3 + 1] - mg, b = px[p * 3 + 2] - mb;
                        crr += r * r; crg += r * g; crb += r * b; cgg += g * g; cgb += g * b; cbb += b * b;
                    }
                    double ar = 1, ag = 1, ab = 1;
                    for (int k = 0; k < 4; k++)
                    {
                        double nr = crr * ar + crg * ag + crb * ab, ng = crg * ar + cgg * ag + cgb * ab, nb = crb * ar + cgb * ag + cbb * ab;
                        double len = Math.Sqrt(nr * nr + ng * ng + nb * nb);
                        if (len < 1e-9) break;
                        ar = nr / len; ag = ng / len; ab = nb / len;
                    }
                    double lo = double.MaxValue, hi = double.MinValue;
                    for (int p = 0; p < 16; p++)
                    {
                        double t = (px[p * 3] - mr) * ar + (px[p * 3 + 1] - mg) * ag + (px[p * 3 + 2] - mb) * ab;
                        lo = Math.Min(lo, t); hi = Math.Max(hi, t);
                    }
                    ushort c0 = Pack((int)Math.Round(mr + ar * hi), (int)Math.Round(mg + ag * hi), (int)Math.Round(mb + ab * hi));
                    ushort c1 = Pack((int)Math.Round(mr + ar * lo), (int)Math.Round(mg + ag * lo), (int)Math.Round(mb + ab * lo));
                    if (c0 < c1) (c0, c1) = (c1, c0);

                    // four-colour mode needs c0 > c1; a flat block uses colour 0 throughout
                    Span<byte> palette = stackalloc byte[16];
                    Expand(c0, palette[..4]);
                    Expand(c1, palette.Slice(4, 4));
                    for (int k = 0; k < 3; k++)
                    {
                        palette[8 + k] = (byte)((2 * palette[k] + palette[4 + k]) / 3);
                        palette[12 + k] = (byte)((palette[k] + 2 * palette[4 + k]) / 3);
                    }
                    uint indices = 0;
                    if (c0 != c1)
                        for (int p = 0; p < 16; p++)
                        {
                            int best = 0, bestDistance = int.MaxValue;
                            for (int c = 0; c < 4; c++)
                            {
                                int dr = px[p * 3] - palette[c * 4], dg = px[p * 3 + 1] - palette[c * 4 + 1], db = px[p * 3 + 2] - palette[c * 4 + 2];
                                int d = dr * dr + dg * dg + db * db;
                                if (d < bestDistance) (best, bestDistance) = (c, d);
                            }
                            indices |= (uint)best << (2 * p);
                        }

                    int at = (by * blocksWide + bx) * 8;
                    BitConverter.GetBytes(c0).CopyTo(output, at);
                    BitConverter.GetBytes(c1).CopyTo(output, at + 2);
                    BitConverter.GetBytes(indices).CopyTo(output, at + 4);
                }
            return output;
        }
    }
}
