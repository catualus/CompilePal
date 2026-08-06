using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CompilePalX.Compiling;
using CompilePalX.Configuration;

namespace CompilePalX
{
    /// <summary>
    /// Finds and prefers ficool2's Hammer++ compile tools ("tools++") over the stock Source SDK
    /// compilers, and reports which of the two is in use.
    ///
    /// Two jobs:
    ///   1. Resolution - the game configuration usually points at whatever bin folder Hammer was set up
    ///      with, which is often the 32-bit one. tools++ are commonly installed alongside Hammer++ in
    ///      bin/win64 or bin/x64, so the configured path can be stock while a better binary sits next to
    ///      it. <see cref="ResolveBinary"/> looks in the sibling bin folders and prefers a tools++ build.
    ///   2. Detection - tools++ accept a large set of extra arguments (ambient occlusion, texture
    ///      shadows, custom limits, static prop formats, repack threading, ...) that the stock tools
    ///      reject or ignore. Those parameters stay hidden unless the binary actually in use is tools++.
    ///
    /// Detection scans the executable for the version banner the tools carry. Results are cached per
    /// file identity, so this costs one read per binary per session.
    /// </summary>
    public static class ToolsPlusPlusDetector
    {
        /// <summary>Per-tool banner markers, keyed by CompileProcess name.</summary>
        private static readonly Dictionary<string, string[]> ToolMarkers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["VBSP"] = ["VBSP++"],
            ["VVIS"] = ["VVIS++"],
            ["VRAD"] = ["VRAD++"],
            ["BSPZIP"] = ["BSPZIP++"],
        };

        /// <summary>
        /// Markers shared by the whole tools++ suite. Needed as well as the per-tool banners because not
        /// every tool carries one: bspzipplusplus.exe has no "BSPZIP++" string at all, and is only
        /// identifiable by the author tag. Verified against tools++ builds dated 2026-08-02.
        /// </summary>
        private static readonly string[] SuiteMarkers = ["ficool2", "HammerPlusPlus", "Hammer++"];

        /// <summary>
        /// Bin subfolders searched for a tools++ build, relative to the configured bin folder.
        /// tools_plusplus.zip is normally extracted over bin/win64 or bin/x64, but Hammer++ also keeps
        /// binaries under its own hammerplusplus/bin folder, so check there too.
        /// </summary>
        private static readonly string[] CandidateSubfolders =
        [
            "win64",
            "x64",
            Path.Combine("win64", "hammerplusplus", "bin"),
            Path.Combine("x64", "hammerplusplus", "bin"),
            Path.Combine("hammerplusplus", "bin"),
            "",
        ];

        private record CacheKey(string Path, long Length, DateTime LastWrite);

        private static readonly Dictionary<string, bool> DetectionCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, CacheKey> DetectionKeys = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> ResolutionCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns the binary that should actually be run for the given process. Prefers a tools++ build
        /// found next to the configured one; otherwise returns the configured path unchanged.
        /// </summary>
        public static string? ResolveBinary(string processName, string? configuredPath)
        {
            if (string.IsNullOrEmpty(configuredPath) || !ToolMarkers.ContainsKey(processName))
                return configuredPath;

            if (!ConfigurationManager.Settings.PreferToolsPlusPlusBinaries)
                return configuredPath;

            if (ResolutionCache.TryGetValue(processName, out var cached))
                return cached;

            string resolved = FindPreferredBinary(processName, configuredPath);
            ResolutionCache[processName] = resolved;

            if (!string.Equals(resolved, configuredPath, StringComparison.OrdinalIgnoreCase))
                CompilePalLogger.LogLine($"Using tools++ {processName} at \"{resolved}\" instead of the configured \"{configuredPath}\".");

            return resolved;
        }

        /// <summary>
        /// Same lookup as <see cref="ResolveBinary"/>, but never writes the resolution cache. Safe to call
        /// speculatively for a live UI hint while a path is still being edited and not yet saved -
        /// <see cref="ResolveBinary"/>'s cache is keyed by process name alone, so caching a result for a
        /// path the user hasn't committed to would poison the binary actually used by the next compile.
        /// </summary>
        public static string? PreviewResolveBinary(string processName, string? configuredPath)
        {
            if (string.IsNullOrEmpty(configuredPath) || !ToolMarkers.ContainsKey(processName))
                return configuredPath;

            if (!ConfigurationManager.Settings.PreferToolsPlusPlusBinaries)
                return configuredPath;

            return FindPreferredBinary(processName, configuredPath);
        }

        private static string FindPreferredBinary(string processName, string configuredPath)
        {
            // already tools++, nothing to do
            if (IsToolsPlusPlusBinary(processName, configuredPath))
                return configuredPath;

            string? binFolder = Path.GetDirectoryName(configuredPath);
            if (binFolder is null)
                return configuredPath;

            // the configured path may itself already be a bin subfolder, so search its parent too
            var roots = new List<string> { binFolder };
            string? parent = Path.GetDirectoryName(binFolder);
            if (parent is not null)
                roots.Add(parent);

            var fileNames = CandidateFileNames(processName, Path.GetFileName(configuredPath));

            foreach (var root in roots)
            {
                foreach (var subfolder in CandidateSubfolders)
                {
                    foreach (var fileName in fileNames)
                    {
                        string candidate = Path.Combine(root, subfolder, fileName);

                        if (string.Equals(candidate, configuredPath, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!File.Exists(candidate))
                            continue;
                        if (IsToolsPlusPlusBinary(processName, candidate))
                            return candidate;
                    }
                }
            }

            return configuredPath;
        }

        /// <summary>
        /// File names to look for, most tools++ specific first. The installer ships the tools under
        /// their own names (vbspplusplus.exe, vradplusplus.exe, bspzipplusplus.exe, ...) alongside the
        /// stock binaries rather than overwriting them, so searching only for the configured file name
        /// finds nothing even when tools++ is installed.
        /// </summary>
        private static IEnumerable<string> CandidateFileNames(string processName, string configuredFileName)
        {
            string baseName = processName.ToLowerInvariant();

            yield return $"{baseName}plusplus.exe";
            yield return $"{baseName}++.exe";

            // an install that overwrote the stock binaries in place
            if (!string.IsNullOrEmpty(configuredFileName))
                yield return configuredFileName;

            yield return $"{baseName}.exe";
        }

        /// <summary>
        /// Whether tools++ specific parameters should be offered for the given process. Honours the
        /// user's ToolsPlusPlusMode setting, falling back to inspecting the binary that will be run.
        /// </summary>
        public static bool IsEnabledFor(string? processName)
        {
            switch (ConfigurationManager.Settings.ToolsPlusPlusMode)
            {
                case ToolsPlusPlusMode.ForceOn:
                    return true;
                case ToolsPlusPlusMode.ForceOff:
                    return false;
            }

            if (processName is null)
                return false;

            string? path = ResolveBinary(processName, GetConfiguredPath(processName));
            return path is not null && IsToolsPlusPlusBinary(processName, path);
        }

        /// <summary>Clears cached detection and resolution, e.g. after the game configuration changes.</summary>
        public static void Invalidate()
        {
            DetectionCache.Clear();
            DetectionKeys.Clear();
            ResolutionCache.Clear();
        }

        /// <summary>Logs which binary is in use for every known compiler and whether it is tools++.</summary>
        public static void LogDetectionResults()
        {
            if (ConfigurationManager.Settings.ToolsPlusPlusMode != ToolsPlusPlusMode.Auto)
                CompilePalLogger.LogLineDebug($"tools++ parameter visibility overridden by setting: {ConfigurationManager.Settings.ToolsPlusPlusMode}");

            foreach (var processName in ToolMarkers.Keys)
            {
                string? configured = GetConfiguredPath(processName);
                if (configured is null)
                    continue;

                string? resolved = ResolveBinary(processName, configured);
                CompilePalLogger.LogLineDebug($"tools++ detection: {processName} -> \"{resolved}\" is {(resolved is not null && IsToolsPlusPlusBinary(processName, resolved) ? "tools++" : "stock")}");
            }
        }

        private static bool IsToolsPlusPlusBinary(string processName, string path)
        {
            if (!File.Exists(path))
                return false;

            var info = new FileInfo(path);
            var key = new CacheKey(path, info.Length, info.LastWriteTimeUtc);

            // reuse the cached verdict unless the binary was replaced since we last looked
            if (DetectionKeys.TryGetValue(path, out var cachedKey) && cachedKey == key
                && DetectionCache.TryGetValue(path, out bool cachedResult))
                return cachedResult;

            bool result;
            try
            {
                var markers = ToolMarkers.TryGetValue(processName, out var toolMarkers)
                    ? toolMarkers.Concat(SuiteMarkers).ToArray()
                    : SuiteMarkers;

                result = ScanForMarkers(path, markers);
            }
            catch (Exception ex)
            {
                CompilePalLogger.LogLineDebug($"tools++ detection failed for \"{path}\": {ex.Message}");
                result = false;
            }

            DetectionKeys[path] = key;
            DetectionCache[path] = result;
            return result;
        }

        private static string? GetConfiguredPath(string processName)
        {
            var config = GameConfigurationManager.GameConfiguration;
            if (config is null)
                return null;

            return processName.ToUpperInvariant() switch
            {
                "VBSP" => config.VBSP,
                "VVIS" => config.VVIS,
                "VRAD" => config.VRAD,
                "BSPZIP" => config.BSPZip,
                _ => null,
            };
        }

        /// <summary>
        /// Streams the file looking for any of the given ASCII markers. Chunks overlap by the longest
        /// marker length so a marker straddling a chunk boundary is still found.
        /// </summary>
        private static bool ScanForMarkers(string path, string[] markers)
        {
            var needles = markers.Select(Encoding.ASCII.GetBytes).ToArray();
            int overlap = needles.Max(n => n.Length) - 1;

            const int chunkSize = 1 << 20; // 1 MiB
            var buffer = new byte[chunkSize + overlap];

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            int carried = 0;
            while (true)
            {
                int read = stream.Read(buffer, carried, chunkSize);
                if (read == 0)
                    return false;

                int available = carried + read;
                var window = buffer.AsSpan(0, available);

                foreach (var needle in needles)
                {
                    if (window.IndexOf(needle) >= 0)
                        return true;
                }

                if (available <= overlap)
                    return false;

                // carry the tail forward so markers spanning the boundary are not missed
                buffer.AsSpan(available - overlap, overlap).CopyTo(buffer);
                carried = overlap;
            }
        }
    }
}
