using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CompilePalX.Bake
{
    /// <summary>
    /// Puts files into a compiled map's pakfile without bspzip, which some Garry's Mod installs lack.
    ///
    /// The pakfile is lump 40, a zip whose offsets count from the lump's start. The zip is rebuilt -
    /// every entry already there copied byte for byte, whatever its compression, except those being
    /// replaced, then the new files stored uncompressed as the engine reads them - and written at the
    /// end of the BSP, with the lump header pointed at it. What was the old pakfile is left as unused
    /// bytes; a REPACK or BSPZIP step afterwards drops them.
    /// </summary>
    public static class PakWriter
    {
        private const int LumpPakfile = 40;

        /// <summary>Adds or replaces <paramref name="files"/> (path inside the pak to bytes) in <paramref name="bspPath"/>'s pakfile.</summary>
        public static void AddFiles(string bspPath, IReadOnlyDictionary<string, byte[]> files)
        {
            var bsp = File.ReadAllBytes(bspPath);
            if (bsp.Length < 1036 || BitConverter.ToInt32(bsp, 0) != 0x50534256)
                throw new InvalidDataException($"{bspPath} is not a Source BSP.");

            int header = 8 + LumpPakfile * 16;
            int offset = BitConverter.ToInt32(bsp, header), length = BitConverter.ToInt32(bsp, header + 4);
            var old = length > 0 && offset > 0 && offset + (long)length <= bsp.Length ? bsp.AsSpan(offset, length).ToArray() : [];

            var replaced = new HashSet<string>(files.Keys.Select(Normal), StringComparer.OrdinalIgnoreCase);
            var zip = Rebuild(old, replaced, files);

            // at the end of the file, four-byte aligned
            long at = (bsp.Length + 3) & ~3L;
            using var stream = new FileStream(bspPath, FileMode.Open, FileAccess.ReadWrite);
            stream.SetLength(at);
            stream.Position = at;
            stream.Write(zip);
            stream.Position = header;
            stream.Write(BitConverter.GetBytes((int)at));
            stream.Write(BitConverter.GetBytes(zip.Length));
            stream.Write(BitConverter.GetBytes(0));   // version
            stream.Write(BitConverter.GetBytes(0));   // uncompressed size: the lump itself is not LZMA
        }

        private static string Normal(string path) => path.Replace('\\', '/').TrimStart('/');

        /// <summary>A zip holding the entries of <paramref name="old"/> not in <paramref name="replaced"/>, then <paramref name="files"/>.</summary>
        internal static byte[] Rebuild(byte[] old, IReadOnlySet<string> replaced, IReadOnlyDictionary<string, byte[]> files)
        {
            using var output = new MemoryStream();
            var directory = new MemoryStream();
            int count = 0;

            foreach (var (name, central, localOffset) in Entries(old))
            {
                if (replaced.Contains(name))
                    continue;
                // the local record runs from its header to the end of its data
                int nameLength = BitConverter.ToUInt16(old, localOffset + 26), extraLength = BitConverter.ToUInt16(old, localOffset + 28);
                uint compressed = BitConverter.ToUInt32(central, 20);
                int size = 30 + nameLength + extraLength + (int)compressed;
                long newOffset = output.Position;
                output.Write(old, localOffset, size);
                BitConverter.GetBytes((uint)newOffset).CopyTo(central, 42);
                directory.Write(central);
                count++;
            }

            foreach (var (path, data) in files)
            {
                var name = Encoding.UTF8.GetBytes(Normal(path));
                uint crc = Crc32(data);
                long localOffset = output.Position;
                var local = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
                local.Write(0x04034b50u); local.Write((ushort)10); local.Write((ushort)0); local.Write((ushort)0);
                local.Write((ushort)0); local.Write((ushort)0);           // time, date
                local.Write(crc); local.Write((uint)data.Length); local.Write((uint)data.Length);
                local.Write((ushort)name.Length); local.Write((ushort)0);
                local.Write(name); local.Write(data);
                local.Flush();

                var dir = new BinaryWriter(directory, Encoding.UTF8, leaveOpen: true);
                dir.Write(0x02014b50u); dir.Write((ushort)20); dir.Write((ushort)10); dir.Write((ushort)0); dir.Write((ushort)0);
                dir.Write((ushort)0); dir.Write((ushort)0);
                dir.Write(crc); dir.Write((uint)data.Length); dir.Write((uint)data.Length);
                dir.Write((ushort)name.Length); dir.Write((ushort)0); dir.Write((ushort)0);
                dir.Write((ushort)0); dir.Write((ushort)0); dir.Write(0u);
                dir.Write((uint)localOffset);
                dir.Write(name);
                dir.Flush();
                count++;
            }

            long directoryOffset = output.Position;
            directory.Position = 0;
            directory.CopyTo(output);
            var end = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
            end.Write(0x06054b50u); end.Write((ushort)0); end.Write((ushort)0);
            end.Write((ushort)count); end.Write((ushort)count);
            end.Write((uint)directory.Length); end.Write((uint)directoryOffset);
            end.Write((ushort)0);
            end.Flush();
            return output.ToArray();
        }

        /// <summary>Each entry of a zip: its name, a copy of its central directory record, and where its local record is.</summary>
        private static IEnumerable<(string Name, byte[] Central, int LocalOffset)> Entries(byte[] zip)
        {
            int eocd = -1;
            for (int i = zip.Length - 22; i >= Math.Max(0, zip.Length - 65557); i--)
                if (BitConverter.ToUInt32(zip, i) == 0x06054b50)
                {
                    eocd = i;
                    break;
                }
            if (eocd < 0)
                yield break;
            int count = BitConverter.ToUInt16(zip, eocd + 10);
            long p = BitConverter.ToUInt32(zip, eocd + 16);
            for (int i = 0; i < count && p + 46 <= zip.Length; i++)
            {
                if (BitConverter.ToUInt32(zip, (int)p) != 0x02014b50)
                    yield break;
                int nameLength = BitConverter.ToUInt16(zip, (int)p + 28), extraLength = BitConverter.ToUInt16(zip, (int)p + 30),
                    commentLength = BitConverter.ToUInt16(zip, (int)p + 32);
                int size = 46 + nameLength + extraLength + commentLength;
                var central = zip.AsSpan((int)p, size).ToArray();
                string name = Encoding.UTF8.GetString(zip, (int)p + 46, nameLength).Replace('\\', '/');
                yield return (name, central, (int)BitConverter.ToUInt32(zip, (int)p + 42));
                p += size;
            }
        }

        private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
        {
            uint c = (uint)n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();

        internal static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data)
                crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
