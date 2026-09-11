using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CompilePalX.Compiling;

namespace CompilePalX.Preview
{
    /// <summary>
    /// Finds game content the way the engine does, in the engine's order: the map's own packed
    /// files first, then every search path the game configuration mounts, loose folders and VPKs
    /// alike, then the engine's shared VPKs.
    ///
    /// Everything is keyed by the relative path a material or model names - <c>materials/x.vmt</c>,
    /// forward slashes, any case. Packing already works out the search paths, mounted games and
    /// mount.cfg for Garry's Mod; this reuses that and adds the reading.
    /// </summary>
    public sealed class ContentLocator : IDisposable
    {
        private readonly PakArchive? pak;
        private readonly List<string> folders = [];
        private readonly List<Vpk> vpks = [];
        private readonly Dictionary<string, byte[]?> cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Where a lookup was answered from, for the log.</summary>
        public int PakHits { get; private set; }
        public int FolderHits { get; private set; }
        public int VpkHits { get; private set; }
        public int Misses { get; private set; }

        public IReadOnlyList<string> Folders => folders;
        public IReadOnlyList<Vpk> Vpks => vpks;

        /// <summary>
        /// Builds a locator for a map. <paramref name="pakLump"/> is the BSP's pakfile lump (a zip), or
        /// empty; <paramref name="gameFolder"/> the folder holding gameinfo.txt, or null when nothing is
        /// configured, in which case only the pak is searched.
        /// </summary>
        public ContentLocator(byte[] pakLump, string? gameFolder)
        {
            if (pakLump.Length > 0)
            {
                try
                {
                    pak = new PakArchive(pakLump);
                }
                catch (Exception e)
                {
                    CompilePalLogger.LogLineDebug($"The map's pakfile could not be read: {e.Message}");
                    pak = null;
                }
            }

            if (gameFolder is null || !Directory.Exists(gameFolder))
                return;

            List<string> searchPaths;
            try
            {
                searchPaths = Compilers.BSPPack.BSPPack.GetSourceDirectories(gameFolder, verbose: false);
            }
            catch (Exception e)
            {
                CompilePalLogger.LogLineDebug($"Could not resolve the game's search paths: {e.Message}");
                searchPaths = [gameFolder];
            }

            if (!searchPaths.Any(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(gameFolder), StringComparison.OrdinalIgnoreCase)))
                searchPaths.Insert(0, gameFolder);

            var seenVpks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddVpk(string path)
            {
                if (!seenVpks.Add(Path.GetFullPath(path)))
                    return;
                try
                {
                    vpks.Add(Vpk.Open(path));
                }
                catch (Exception e)
                {
                    CompilePalLogger.LogLineDebug($"Skipping VPK \"{path}\": {e.Message}");
                }
            }

            foreach (var searchPath in searchPaths)
            {
                if (!Directory.Exists(searchPath))
                    continue;

                folders.Add(searchPath);

                // every archive the folder mounts, its own and the engine's shared ones
                foreach (var vpk in Directory.EnumerateFiles(searchPath, "*_dir.vpk", SearchOption.TopDirectoryOnly))
                    AddVpk(vpk);
            }

            foreach (var vpk in Compilers.BSPPack.BSPPack.FindGameVpks(gameFolder))
                AddVpk(vpk);
        }

        /// <summary>How many files the map packs, for the log.</summary>
        public int PackedFiles => pak?.Count ?? 0;

        /// <summary>Whether anything beyond the map's own pak is searched.</summary>
        public bool HasGameContent => folders.Count > 0 || vpks.Count > 0;

        /// <summary>The file's bytes, or null if nothing has it. Results are remembered, misses included.</summary>
        public byte[]? Read(string relativePath)
        {
            string key = Normalise(relativePath);

            if (cache.TryGetValue(key, out var cached))
                return cached;

            var bytes = Find(key);
            cache[key] = bytes;
            return bytes;
        }

        private byte[]? Find(string key)
        {
            if (pak?.Read(key) is { } packed)
            {
                PakHits++;
                return packed;
            }

            foreach (var folder in folders)
            {
                string path = Path.Combine(folder, key.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path))
                {
                    try
                    {
                        FolderHits++;
                        return File.ReadAllBytes(path);
                    }
                    catch (IOException)
                    {
                        // fall through to the next place it might be
                    }
                }
            }

            foreach (var vpk in vpks)
            {
                if (vpk.Read(key) is { } bytes)
                {
                    VpkHits++;
                    return bytes;
                }
            }

            Misses++;
            return null;
        }

        private static string Normalise(string path) => path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

        public void Dispose() { }
    }
}
