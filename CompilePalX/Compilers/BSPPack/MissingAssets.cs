using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CompilePalX.Compiling;
using CompilePalX.Preview;

namespace CompilePalX.Compilers.BSPPack
{
    /// <summary>
    /// Tells the mapper which assets the map uses that PACK could not pack, and why.
    ///
    /// PACK only ever packs loose files. Everything else it could not find used to leave one debug
    /// line, so a texture left out of an addon folder, or a prop borrowed from a game mounted through
    /// mount.cfg, shipped as purple checkers or an ERROR model with nothing in the log to say so -
    /// the failure PACK exists to prevent. Not-found assets are sorted into:
    ///   stock content in the game's own VPKs, which every player has: said nothing about;
    ///   content of a game mounted from another install: players need that game mounted;
    ///   content inside Workshop or local addon .gma files (Garry's Mod): players need those addons;
    ///   found nowhere: missing.
    /// </summary>
    internal static class MissingAssets
    {
        /// <summary>
        /// Whether a not-found path is worth reporting. Only assets a player would see or hear go
        /// missing: materials, textures, models, particles and sound files. Model sub-files are left
        /// to their .mdl, ".hdr.vtf" variants are optional, and render targets, cubemap placeholders
        /// and VBSP's per-map materials (materials/maps/) are never loose files.
        /// </summary>
        public static bool IsReportable(string internalPath)
        {
            string path = internalPath.Replace('\\', '/');

            if (path.Contains("_rt_", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("env_cubemap", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("materials/maps/", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".hdr.vtf", StringComparison.OrdinalIgnoreCase) ||
                // TF2-style loading-screen photos, packed when present and optional otherwise
                path.StartsWith("materials/vgui/maps/menu_photos_", StringComparison.OrdinalIgnoreCase))
                return false;

            string stem = Path.GetFileNameWithoutExtension(path);
            if (stem.Length == 0 || int.TryParse(stem, out _))
                return false;

            // A sky is looked for under its bare name and with _hdr faces too, and neither need
            // exist; only its six faces matter.
            if (path.StartsWith("materials/skybox/", StringComparison.OrdinalIgnoreCase))
            {
                bool face = SkyFaces.Any(f => stem.EndsWith(f, StringComparison.OrdinalIgnoreCase));
                if (!face || stem.Contains("_hdr", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            string extension = Path.GetExtension(path).ToLowerInvariant();
            return extension is ".vmt" or ".vtf" or ".mdl" or ".pcf" or ".wav" or ".mp3" or ".ogg";
        }

        private static readonly string[] SkyFaces = ["bk", "dn", "ft", "lf", "rt", "up"];

        /// <summary>
        /// Archive indexes kept for the life of the process, keyed by file and its write time: a
        /// batch compiles many maps against the same game, and opening every VPK and Workshop .gma
        /// again for each one took seconds per map.
        /// </summary>
        private static readonly Dictionary<(string Path, DateTime Written), Vpk?> vpkCache = [];
        private static readonly Dictionary<(string Path, DateTime Written), List<string>> gmaCache = [];
        private static readonly object cacheGate = new();

        /// <summary>
        /// Sorts <paramref name="notFound"/> (each entry the alternatives for one asset) and logs what
        /// players would be missing. Never throws: a report that fails to build must not fail PACK.
        /// </summary>
        public static void Report(IEnumerable<string[]> notFound, string gameFolder, IEnumerable<string> sourceDirectories)
        {
            try
            {
                var remaining = notFound.Where(a => a.Length > 0)
                    .GroupBy(a => a[0], StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();
                if (remaining.Count == 0)
                    return;

                var (stock, mounted) = Vpks(gameFolder, sourceDirectories);

                foreach (var vpk in stock)
                    remaining.RemoveAll(a => a.Any(vpk.Contains));

                var fromMounted = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (game, vpk) in mounted)
                {
                    foreach (var asset in remaining.Where(a => a.Any(vpk.Contains)).ToList())
                    {
                        if (!fromMounted.TryGetValue(game, out var list))
                            fromMounted[game] = list = [];
                        list.Add(asset[0]);
                        remaining.Remove(asset);
                    }
                }

                var fromAddons = new List<string>();
                if (remaining.Count > 0 && IsGarrysMod(gameFolder))
                {
                    var addonFiles = AddonFiles(gameFolder);
                    foreach (var asset in remaining.Where(a => a.Any(addonFiles.Contains)).ToList())
                    {
                        fromAddons.Add(asset[0]);
                        remaining.Remove(asset);
                    }
                }

                if (fromAddons.Count > 0)
                {
                    CompilePalLogger.LogLine(
                        $"{fromAddons.Count} asset(s) come from Workshop or local addons and are not packed. " +
                        "Players need the same addons, which a server's Workshop collection usually provides.");
                    foreach (string path in fromAddons.Order().Take(10))
                        CompilePalLogger.LogLine($"  {path}");
                    if (fromAddons.Count > 10)
                        CompilePalLogger.LogLine($"  and {fromAddons.Count - 10} more");
                }

                if (fromMounted.Count > 0)
                {
                    int total = fromMounted.Values.Sum(l => l.Count);
                    var text = new StringBuilder();
                    text.AppendLine($"{total} asset(s) come from other games mounted through mount.cfg and are not packed, " +
                                    "so players who do not have those games mounted will see missing textures or ERROR models:");
                    foreach (var (game, paths) in fromMounted)
                    {
                        text.AppendLine($"  {game} ({paths.Count}):");
                        foreach (string path in paths.Order().Take(15))
                            text.AppendLine($"    {path}");
                        if (paths.Count > 15)
                            text.AppendLine($"    and {paths.Count - 15} more");
                    }
                    CompilePalLogger.LogCompileError(text.ToString(),
                        new Error($"{total} asset(s) come from mounted games ({string.Join(", ", fromMounted.Keys)}), so players need those games mounted",
                            ErrorSeverity.Warning));
                }

                if (remaining.Count > 0)
                {
                    var missing = remaining.Select(a => a[0]).Order(StringComparer.OrdinalIgnoreCase).ToList();
                    var text = new StringBuilder();
                    text.AppendLine($"{missing.Count} asset(s) the map uses were not found in any content folder, the game's VPKs, " +
                                    "or mounted games, so they are not packed and players will see missing textures or ERROR models:");
                    foreach (string path in missing.Take(40))
                        text.AppendLine($"  {path}");
                    if (missing.Count > 40)
                        text.AppendLine($"  and {missing.Count - 40} more (the full list is in the debug log)");
                    CompilePalLogger.LogCompileError(text.ToString(),
                        new Error($"{missing.Count} asset(s) the map uses were not found, so they are not packed", ErrorSeverity.Warning));

                    foreach (string path in missing)
                        CompilePalLogger.LogLineDebug($"Missing asset: {path}");
                }
            }
            catch (Exception e)
            {
                CompilePalLogger.LogLineDebug($"Could not check assets PACK did not find: {e}");
            }
        }

        private static bool IsGarrysMod(string gameFolder)
            => Path.GetFileName(gameFolder.TrimEnd('\\', '/')).Equals("garrysmod", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The game's own VPKs, and those of games mounted from other installs. A content folder
        /// inside the game's install (TF2's hl2 beside tf, say) is the game's own; one outside it
        /// came from mount.cfg or mounts.kv, and its content is only there for players who mount it.
        /// </summary>
        private static (List<Vpk> Stock, List<(string Game, Vpk Vpk)> Mounted) Vpks(string gameFolder, IEnumerable<string> sourceDirectories)
        {
            var stock = new List<Vpk>();
            var mounted = new List<(string, Vpk)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string install = Path.GetFullPath(Directory.GetParent(gameFolder.TrimEnd('\\', '/'))?.FullName ?? gameFolder);

            foreach (string path in BSPPack.FindGameVpks(gameFolder))
                if (seen.Add(Path.GetFullPath(path)) && TryOpen(path) is { } vpk)
                    stock.Add(vpk);

            foreach (string directory in sourceDirectories.Where(Directory.Exists))
            {
                string full = Path.GetFullPath(directory);
                bool own = full.StartsWith(install + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

                foreach (string path in BSPPack.FindVpkSets(full))
                {
                    if (!seen.Add(Path.GetFullPath(path)) || TryOpen(path) is not { } vpk)
                        continue;

                    if (own)
                        stock.Add(vpk);
                    else
                        mounted.Add((Path.GetFileName(full), vpk));
                }
            }

            return (stock, mounted);
        }

        private static Vpk? TryOpen(string path)
        {
            var key = (Path.GetFullPath(path), File.GetLastWriteTimeUtc(path));
            lock (cacheGate)
            {
                if (vpkCache.TryGetValue(key, out var cached))
                    return cached;
            }

            Vpk? vpk;
            try
            {
                vpk = Vpk.Open(path);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                CompilePalLogger.LogLineDebug($"Could not read {path}: {e.Message}");
                vpk = null;
            }

            lock (cacheGate)
                vpkCache[key] = vpk;
            return vpk;
        }

        /// <summary>
        /// Every file inside Garry's Mod's addon .gma files: the Workshop downloads in steamapps, the
        /// ones a server has sent (cache/workshop) and legacy addons/*.gma.
        /// </summary>
        private static HashSet<string> AddonFiles(string gameFolder)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var gmas = new List<string>();

            void Collect(string folder, SearchOption depth)
            {
                if (Directory.Exists(folder))
                    gmas.AddRange(Directory.EnumerateFiles(folder, "*.gma", depth));
            }

            Collect(Path.Combine(gameFolder, "addons"), SearchOption.TopDirectoryOnly);
            Collect(Path.Combine(gameFolder, "cache", "workshop"), SearchOption.TopDirectoryOnly);

            // steamapps/common/GarrysMod/garrysmod -> steamapps/workshop/content/4000/<id>/*.gma
            var steamapps = Directory.GetParent(gameFolder.TrimEnd('\\', '/'))?.Parent?.Parent;
            if (steamapps is not null && steamapps.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
                Collect(Path.Combine(steamapps.FullName, "workshop", "content", "4000"), SearchOption.AllDirectories);

            foreach (string gma in gmas)
            {
                var key = (Path.GetFullPath(gma), File.GetLastWriteTimeUtc(gma));
                List<string>? names;
                lock (cacheGate)
                    gmaCache.TryGetValue(key, out names);

                if (names is null)
                {
                    try
                    {
                        names = GmaFileNames(gma);
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                        CompilePalLogger.LogLineDebug($"Could not read {gma}: {e.Message}");
                        names = [];
                    }

                    lock (cacheGate)
                        gmaCache[key] = names;
                }

                foreach (string name in names)
                    files.Add(name.Replace('\\', '/'));
            }

            return files;
        }

        /// <summary>
        /// The file names a .gma holds, read from its header alone: "GMAD", a version byte, the
        /// author's SteamID and a timestamp, the required-content list (from version 2), the name,
        /// description and author, the addon version, then one entry per file until a zero.
        /// </summary>
        internal static List<string> GmaFileNames(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            if (reader.ReadUInt32() != 0x44414D47) // "GMAD"
                throw new InvalidDataException("Not a GMA file.");

            byte version = reader.ReadByte();
            reader.ReadUInt64(); // SteamID
            reader.ReadUInt64(); // timestamp

            if (version > 1)
                while (ReadString(reader).Length > 0) { }

            ReadString(reader); // name
            ReadString(reader); // description
            ReadString(reader); // author
            reader.ReadInt32(); // addon version

            var names = new List<string>();
            while (reader.ReadUInt32() != 0)
            {
                names.Add(ReadString(reader));
                reader.ReadInt64(); // size
                reader.ReadUInt32(); // CRC
            }

            return names;
        }

        private static string ReadString(BinaryReader reader)
        {
            var bytes = new List<byte>();
            byte b;
            while ((b = reader.ReadByte()) != 0)
                bytes.Add(b);
            return Encoding.UTF8.GetString(bytes.ToArray());
        }
    }
}
