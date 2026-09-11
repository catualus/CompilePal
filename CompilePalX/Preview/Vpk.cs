using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CompilePalX.Preview
{
    /// <summary>
    /// Reads Valve's VPK archives: the directory tree from <c>name_dir.vpk</c>, file bytes from the
    /// numbered parts beside it. Versions 1 and 2, which is every Source game.
    ///
    /// Format: https://developer.valvesoftware.com/wiki/VPK_(file_format). The tree is nested by
    /// extension, then folder, then file name, each level a run of null-terminated strings ended by
    /// an empty one. Each file entry says which part holds it and where; a part index of 0x7fff means
    /// the bytes follow the tree in the directory file itself. A few leading "preload" bytes can sit
    /// inline after the entry and precede the rest.
    /// </summary>
    public sealed class Vpk
    {
        private readonly record struct Entry(ushort Archive, uint Offset, uint Length, byte[] Preload);

        private readonly string directoryPath;
        private readonly string archiveStem;
        private readonly long dataStart;
        private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);

        public string Path => directoryPath;
        public int Count => entries.Count;

        private Vpk(string directoryPath, long dataStart)
        {
            this.directoryPath = directoryPath;
            this.dataStart = dataStart;

            string name = System.IO.Path.GetFileNameWithoutExtension(directoryPath);
            archiveStem = name.EndsWith("_dir", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        }

        /// <summary>Opens a directory VPK and reads its tree. Throws on a file that is not one.</summary>
        public static Vpk Open(string directoryPath)
        {
            using var stream = new FileStream(directoryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            uint signature = reader.ReadUInt32();
            if (signature != 0x55aa1234)
                throw new InvalidDataException("Not a VPK directory file.");

            uint version = reader.ReadUInt32();
            uint treeSize = reader.ReadUInt32();

            long headerSize = version switch
            {
                1 => 12,
                2 => 28,
                _ => throw new InvalidDataException($"VPK version {version} is not supported."),
            };

            stream.Seek(headerSize, SeekOrigin.Begin);
            var vpk = new Vpk(directoryPath, headerSize + treeSize);
            vpk.ReadTree(reader, headerSize + treeSize);
            return vpk;
        }

        private void ReadTree(BinaryReader reader, long treeEnd)
        {
            while (reader.BaseStream.Position < treeEnd)
            {
                string extension = ReadString(reader);
                if (extension.Length == 0)
                    break;

                while (true)
                {
                    string folder = ReadString(reader);
                    if (folder.Length == 0)
                        break;

                    while (true)
                    {
                        string file = ReadString(reader);
                        if (file.Length == 0)
                            break;

                        reader.ReadUInt32(); // crc
                        ushort preloadBytes = reader.ReadUInt16();
                        ushort archive = reader.ReadUInt16();
                        uint offset = reader.ReadUInt32();
                        uint length = reader.ReadUInt32();
                        ushort terminator = reader.ReadUInt16();
                        if (terminator != 0xffff)
                            throw new InvalidDataException("VPK tree entry is malformed.");

                        byte[] preload = preloadBytes > 0 ? reader.ReadBytes(preloadBytes) : [];

                        // a file at the root has folder " " (a single space)
                        string path = folder == " " ? $"{file}.{extension}" : $"{folder}/{file}.{extension}";
                        entries[path] = new Entry(archive, offset, length, preload);
                    }
                }
            }
        }

        private static string ReadString(BinaryReader reader)
        {
            var bytes = new List<byte>();
            byte b;
            while ((b = reader.ReadByte()) != 0)
                bytes.Add(b);
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        /// <summary>Whether the archive holds <paramref name="path"/>, forward slashes, any case.</summary>
        public bool Contains(string path) => entries.ContainsKey(Normalise(path));

        /// <summary>The file's bytes, or null when the archive does not hold it or its part is missing.</summary>
        public byte[]? Read(string path)
        {
            if (!entries.TryGetValue(Normalise(path), out var entry))
                return null;

            var result = new byte[entry.Preload.Length + entry.Length];
            entry.Preload.CopyTo(result, 0);

            if (entry.Length == 0)
                return result;

            string partPath = entry.Archive == 0x7fff
                ? directoryPath
                : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(directoryPath) ?? "", $"{archiveStem}_{entry.Archive:D3}.vpk");

            long offset = entry.Archive == 0x7fff ? dataStart + entry.Offset : entry.Offset;

            try
            {
                using var stream = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                stream.Seek(offset, SeekOrigin.Begin);
                int read = 0;
                while (read < entry.Length)
                {
                    int n = stream.Read(result, entry.Preload.Length + read, (int)entry.Length - read);
                    if (n <= 0)
                        return null;
                    read += n;
                }
            }
            catch (IOException)
            {
                return null;
            }

            return result;
        }

        private static string Normalise(string path) => path.Replace('\\', '/').TrimStart('/');
    }
}
