using System;
using System.Collections.Generic;
using System.IO;

namespace CompilePalX.Preview
{
    /// <summary>
    /// One mip level of a texture, ready for the GPU: either raw DXT blocks or RGBA8 pixels.
    /// </summary>
    public sealed record TextureLevel(int Width, int Height, byte[] Data);

    /// <summary>
    /// A texture read out of a VTF: its pixel format and the mip levels kept, largest first.
    /// </summary>
    public sealed class Texture
    {
        /// <summary>"dxt1", "dxt3", "dxt5" or "rgba8".</summary>
        public string Format { get; init; } = "rgba8";
        public IReadOnlyList<TextureLevel> Levels { get; init; } = [];
        public int Width => Levels.Count > 0 ? Levels[0].Width : 0;
        public int Height => Levels.Count > 0 ? Levels[0].Height : 0;
        public bool HasAlpha { get; init; }
    }

    /// <summary>
    /// Reads Valve Texture Format files, versions 7.0 to 7.5.
    ///
    /// Format: https://developer.valvesoftware.com/wiki/VTF_(Valve_Texture_Format). The image data
    /// is stored smallest mip first, and for 7.3 and later its offset comes from a resource entry
    /// rather than following the header directly. Only the top few mips are kept, capped at a size
    /// the preview can afford, and DXT data is passed through untouched for the GPU to decode. Other
    /// formats are converted to RGBA8; the exotic ones are approximated rather than refused.
    /// </summary>
    public static class Vtf
    {
        private const int FlagEnvmap = 0x4000;

        /// <summary>Largest edge kept. A map with hundreds of 2048² textures would not fit in a browser.</summary>
        public const int MaxSize = 1024;

        /// <summary>Reads a texture. Throws on a file that is not a VTF; returns null for cubemaps and volumes.</summary>
        public static Texture? Read(byte[] bytes)
        {
            if (bytes.Length < 64 || bytes[0] != 'V' || bytes[1] != 'T' || bytes[2] != 'F' || bytes[3] != 0)
                throw new InvalidDataException("Not a VTF file.");

            uint major = BitConverter.ToUInt32(bytes, 4);
            uint minor = BitConverter.ToUInt32(bytes, 8);
            uint headerSize = BitConverter.ToUInt32(bytes, 12);
            int width = BitConverter.ToUInt16(bytes, 16);
            int height = BitConverter.ToUInt16(bytes, 18);
            uint flags = BitConverter.ToUInt32(bytes, 20);
            int frames = BitConverter.ToUInt16(bytes, 24);
            int format = BitConverter.ToInt32(bytes, 52);
            int mipCount = bytes[56];
            int lowResFormat = BitConverter.ToInt32(bytes, 57);
            int lowResWidth = bytes[61];
            int lowResHeight = bytes[62];
            int depth = major > 7 || minor >= 2 ? BitConverter.ToUInt16(bytes, 63) : 1;

            if (major != 7)
                throw new InvalidDataException($"VTF version {major}.{minor} is not supported.");

            if ((flags & FlagEnvmap) != 0 || depth > 1)
                return null;

            if (width <= 0 || height <= 0 || mipCount <= 0 || frames <= 0)
                return null;

            // where the high-resolution image data begins
            long dataOffset;
            if (minor >= 3)
            {
                int resourceCount = BitConverter.ToInt32(bytes, 68);
                dataOffset = -1;
                for (int i = 0; i < resourceCount; i++)
                {
                    int o = 80 + i * 8;
                    if (o + 8 > bytes.Length)
                        break;
                    // tag 0x30 0x00 0x00 is the high-res image; the fourth byte is flags
                    if (bytes[o] == 0x30 && bytes[o + 1] == 0 && bytes[o + 2] == 0)
                    {
                        dataOffset = BitConverter.ToUInt32(bytes, o + 4);
                        break;
                    }
                }
                if (dataOffset < 0)
                    return null;
            }
            else
            {
                dataOffset = headerSize + (lowResFormat >= 0 ? LevelSize(lowResFormat, lowResWidth, lowResHeight) : 0);
            }

            // Mips are stored smallest first. Pick the largest we are willing to keep, and everything
            // below it, so the GPU can filter at distance.
            int keepFrom = 0;
            while ((width >> keepFrom) > MaxSize || (height >> keepFrom) > MaxSize)
                keepFrom++;
            if (keepFrom >= mipCount)
                keepFrom = mipCount - 1;

            var levels = new List<TextureLevel>();
            long offset = dataOffset;

            for (int mip = mipCount - 1; mip >= 0; mip--)
            {
                int w = Math.Max(1, width >> mip);
                int h = Math.Max(1, height >> mip);
                int size = LevelSize(format, w, h);

                // one frame of this mip, the first face and slice only
                if (mip >= keepFrom)
                {
                    if (offset + size > bytes.Length)
                        break;

                    var data = new byte[size];
                    Buffer.BlockCopy(bytes, (int)offset, data, 0, size);
                    levels.Insert(0, new TextureLevel(w, h, Convert(format, w, h, data)));
                }

                offset += (long)size * frames;
            }

            if (levels.Count == 0)
                return null;

            return new Texture
            {
                Format = format switch { 13 or 20 => "dxt1", 14 => "dxt3", 15 => "dxt5", _ => "rgba8" },
                Levels = levels,
                HasAlpha = format is 15 or 14 or 20 or 0 or 1 or 11 or 12 or 6 or 8 or 19 or 21,
            };
        }

        /// <summary>Bytes one mip level of <paramref name="format"/> occupies.</summary>
        public static int LevelSize(int format, int width, int height)
        {
            switch (format)
            {
                case 13: case 20: // DXT1
                    return Math.Max(1, width / 4) * Math.Max(1, height / 4) * 8;
                case 14: case 15: // DXT3, DXT5
                    return Math.Max(1, width / 4) * Math.Max(1, height / 4) * 16;
                default:
                    return width * height * BytesPerPixel(format);
            }
        }

        private static int BytesPerPixel(int format) => format switch
        {
            0 or 1 or 11 or 12 or 16 or 23 or 26 => 4, // RGBA8888, ABGR8888, ARGB8888, BGRA8888, BGRX8888, UVWQ8888, UVLX8888
            2 or 3 or 9 or 10 => 3,                    // RGB888, BGR888 and their bluescreen variants
            4 or 17 or 18 or 19 or 21 or 6 or 22 => 2, // 565, 5551, 4444, IA88, UV88
            5 or 7 or 8 => 1,                          // I8, P8, A8
            24 or 25 => 8,                             // RGBA16161616F, RGBA16161616
            _ => 4,
        };

        /// <summary>DXT stays DXT; everything else becomes RGBA8.</summary>
        private static byte[] Convert(int format, int width, int height, byte[] data)
        {
            int pixels = width * height;
            switch (format)
            {
                case 13: case 14: case 15: case 20:
                    return data;

                case 0: // RGBA8888
                    return data;

                case 12: // BGRA8888
                case 16: // BGRX8888
                {
                    var o = new byte[pixels * 4];
                    for (int i = 0; i < pixels; i++)
                    {
                        o[i * 4] = data[i * 4 + 2];
                        o[i * 4 + 1] = data[i * 4 + 1];
                        o[i * 4 + 2] = data[i * 4];
                        o[i * 4 + 3] = format == 16 ? (byte)255 : data[i * 4 + 3];
                    }
                    return o;
                }

                case 1: // ABGR8888
                {
                    var o = new byte[pixels * 4];
                    for (int i = 0; i < pixels; i++)
                    {
                        o[i * 4] = data[i * 4 + 3];
                        o[i * 4 + 1] = data[i * 4 + 2];
                        o[i * 4 + 2] = data[i * 4 + 1];
                        o[i * 4 + 3] = data[i * 4];
                    }
                    return o;
                }

                case 11: // ARGB8888
                {
                    var o = new byte[pixels * 4];
                    for (int i = 0; i < pixels; i++)
                    {
                        o[i * 4] = data[i * 4 + 1];
                        o[i * 4 + 1] = data[i * 4 + 2];
                        o[i * 4 + 2] = data[i * 4 + 3];
                        o[i * 4 + 3] = data[i * 4];
                    }
                    return o;
                }

                case 2: case 9: // RGB888
                case 3: case 10: // BGR888
                {
                    bool bgr = format is 3 or 10;
                    var o = new byte[pixels * 4];
                    for (int i = 0; i < pixels; i++)
                    {
                        o[i * 4] = data[i * 3 + (bgr ? 2 : 0)];
                        o[i * 4 + 1] = data[i * 3 + 1];
                        o[i * 4 + 2] = data[i * 3 + (bgr ? 0 : 2)];
                        o[i * 4 + 3] = 255;
                    }
                    return o;
                }

                case 5: // I8
                {
                    var o = new byte[pixels * 4];
                    for (int i = 0; i < pixels; i++)
                    {
                        o[i * 4] = o[i * 4 + 1] = o[i * 4 + 2] = data[i];
                        o[i * 4 + 3] = 255;
                    }
                    return o;
                }

                case 6: // IA88
                {
                    var o = new byte[pixels * 4];
                    for (int i = 0; i < pixels; i++)
                    {
                        o[i * 4] = o[i * 4 + 1] = o[i * 4 + 2] = data[i * 2];
                        o[i * 4 + 3] = data[i * 2 + 1];
                    }
                    return o;
                }

                case 8: // A8
                {
                    var o = new byte[pixels * 4];
                    for (int i = 0; i < pixels; i++)
                    {
                        o[i * 4] = o[i * 4 + 1] = o[i * 4 + 2] = 255;
                        o[i * 4 + 3] = data[i];
                    }
                    return o;
                }

                case 4: case 17: // RGB565 / BGR565
                {
                    var o = new byte[pixels * 4];
                    for (int i = 0; i < pixels; i++)
                    {
                        ushort p = BitConverter.ToUInt16(data, i * 2);
                        int r5 = (p >> 11) & 31, g6 = (p >> 5) & 63, b5 = p & 31;
                        if (format == 17) (r5, b5) = (b5, r5);
                        o[i * 4] = (byte)(r5 * 255 / 31);
                        o[i * 4 + 1] = (byte)(g6 * 255 / 63);
                        o[i * 4 + 2] = (byte)(b5 * 255 / 31);
                        o[i * 4 + 3] = 255;
                    }
                    return o;
                }

                case 24: // RGBA16161616F
                {
                    var o = new byte[pixels * 4];
                    for (int i = 0; i < pixels; i++)
                        for (int c = 0; c < 4; c++)
                        {
                            float v = (float)BitConverter.ToHalf(data, (i * 4 + c) * 2);
                            o[i * 4 + c] = (byte)Math.Clamp(Math.Round(Math.Pow(Math.Clamp(v, 0, 1), 1 / 2.2) * 255), 0, 255);
                        }
                    return o;
                }

                default:
                {
                    // something we do not decode: a flat mid-grey rather than nothing
                    var o = new byte[pixels * 4];
                    for (int i = 0; i < pixels; i++)
                    {
                        o[i * 4] = o[i * 4 + 1] = o[i * 4 + 2] = 128;
                        o[i * 4 + 3] = 255;
                    }
                    return o;
                }
            }
        }
    }
}
