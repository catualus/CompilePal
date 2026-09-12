using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace CompilePalX.Preview
{
    /// <summary>
    /// Reads the zip inside a BSP's pakfile lump.
    ///
    /// Not <see cref="ZipArchive"/>, because bspzip's <c>-compress</c> stores every packed file with
    /// LZMA (zip method 14), which .NET does not decode; a repacked map's pak would come back empty
    /// and every custom texture on it would be missing. This reads the central directory itself and
    /// handles stored, deflated and LZMA entries. An LZMA entry's data is a two-byte version, a
    /// two-byte property length, the properties, then a raw LZMA stream, the same decoder the lumps
    /// use.
    /// </summary>
    public sealed class PakArchive
    {
        private readonly record struct Entry(int Method, long Offset, uint CompressedSize, uint Size);

        private readonly byte[] bytes;
        private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);

        public int Count => entries.Count;

        /// <summary>Reads the directory of <paramref name="zip"/>. Throws when it is not a zip.</summary>
        public PakArchive(byte[] zip)
        {
            bytes = zip;

            // the end-of-central-directory record is within the last 64K + 22 bytes
            int eocd = -1;
            for (int i = bytes.Length - 22; i >= Math.Max(0, bytes.Length - 65557); i--)
            {
                if (bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 0x05 && bytes[i + 3] == 0x06)
                {
                    eocd = i;
                    break;
                }
            }

            if (eocd < 0)
                throw new InvalidDataException("Not a zip file.");

            int count = BitConverter.ToUInt16(bytes, eocd + 10);
            uint directoryOffset = BitConverter.ToUInt32(bytes, eocd + 16);

            long p = directoryOffset;
            for (int i = 0; i < count && p + 46 <= bytes.Length; i++)
            {
                if (BitConverter.ToUInt32(bytes, (int)p) != 0x02014b50)
                    break;

                int method = BitConverter.ToUInt16(bytes, (int)p + 10);
                uint compressedSize = BitConverter.ToUInt32(bytes, (int)p + 20);
                uint size = BitConverter.ToUInt32(bytes, (int)p + 24);
                int nameLength = BitConverter.ToUInt16(bytes, (int)p + 28);
                int extraLength = BitConverter.ToUInt16(bytes, (int)p + 30);
                int commentLength = BitConverter.ToUInt16(bytes, (int)p + 32);
                uint localOffset = BitConverter.ToUInt32(bytes, (int)p + 42);

                string name = Encoding.UTF8.GetString(bytes, (int)p + 46, nameLength).Replace('\\', '/');
                entries[name] = new Entry(method, localOffset, compressedSize, size);

                p += 46 + nameLength + extraLength + commentLength;
            }
        }

        public IEnumerable<string> Names => entries.Keys;

        public bool Contains(string path) => entries.ContainsKey(path.Replace('\\', '/').TrimStart('/'));

        /// <summary>The file's bytes, or null when the pak does not hold it or it cannot be decoded.</summary>
        public byte[]? Read(string path)
        {
            if (!entries.TryGetValue(path.Replace('\\', '/').TrimStart('/'), out var entry))
                return null;

            try
            {
                // skip the local header to the data
                int local = (int)entry.Offset;
                if (local + 30 > bytes.Length || BitConverter.ToUInt32(bytes, local) != 0x04034b50)
                    return null;

                int nameLength = BitConverter.ToUInt16(bytes, local + 26);
                int extraLength = BitConverter.ToUInt16(bytes, local + 28);
                int data = local + 30 + nameLength + extraLength;

                if (data + entry.CompressedSize > bytes.Length)
                    return null;

                switch (entry.Method)
                {
                    case 0:
                    {
                        var result = new byte[entry.CompressedSize];
                        Buffer.BlockCopy(bytes, data, result, 0, (int)entry.CompressedSize);
                        return result;
                    }

                    case 8:
                    {
                        using var input = new MemoryStream(bytes, data, (int)entry.CompressedSize, writable: false);
                        using var inflate = new DeflateStream(input, CompressionMode.Decompress);
                        using var output = new MemoryStream((int)entry.Size);
                        inflate.CopyTo(output);
                        return output.ToArray();
                    }

                    case 14:
                    {
                        // version (2), properties size (2), properties, raw stream
                        int propertiesSize = BitConverter.ToUInt16(bytes, data + 2);
                        var properties = new byte[propertiesSize];
                        Buffer.BlockCopy(bytes, data + 4, properties, 0, propertiesSize);

                        var decoder = new SevenZip.Compression.LZMA.Decoder();
                        decoder.SetDecoderProperties(properties);

                        int streamStart = data + 4 + propertiesSize;
                        long streamLength = entry.CompressedSize - 4 - propertiesSize;
                        using var input = new MemoryStream(bytes, streamStart, (int)streamLength, writable: false);
                        using var output = new MemoryStream((int)entry.Size);
                        decoder.Code(input, output, streamLength, entry.Size, null);
                        return output.ToArray();
                    }

                    default:
                        return null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
