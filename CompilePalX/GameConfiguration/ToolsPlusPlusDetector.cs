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
    /// Finds and prefers the Hammer++ compile tools ("tools++") over the stock Source SDK compilers, and
    /// reports which of the two is in use. Covers both generations: ficool2's builds bundled with
    /// Hammer++, and the later standalone rebuild.
    ///
    /// Two jobs:
    ///   1. Resolution - the game configuration usually points at whatever bin folder Hammer was set up
    ///      with, which is often the 32-bit one. Older tools++ were installed alongside Hammer++ in
    ///      bin/win64 or bin/x64, so the configured path can be stock while a better binary sits next to
    ///      it. Current builds are standalone and typically live outside any game folder entirely.
    ///      <see cref="ResolveBinary"/> checks the standalone install first, then the sibling bin
    ///      folders, and prefers a tools++ build over the configured one.
    ///   2. Detection - tools++ accept a large set of extra arguments (ambient occlusion, texture
    ///      shadows, custom limits, static prop formats, repack threading, ...) that the stock tools
    ///      reject or ignore. Those parameters stay hidden unless the binary actually in use is tools++.
    ///
    /// Detection recognises the file name where it is decisive, and otherwise scans the executable for
    /// the version banner the tools carry. Scan results are cached per file identity, so this costs at
    /// most one read per binary per session.
    /// </summary>
    public static class ToolsPlusPlusDetector
    {
        /// <summary>
        /// Per-tool banner markers, keyed by CompileProcess name.
        ///
        /// Two generations are covered. The Hammer++ era tools print "VBSP++" and friends; the current
        /// standalone rebuild prints "Breadworks - vbsp++ (date)" instead, so a build recognised by one
        /// generation's marker is invisible to the other.
        /// </summary>
        private static readonly Dictionary<string, string[]> ToolMarkers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["VBSP"] = ["VBSP++", "Breadworks - vbsp++"],
            ["VVIS"] = ["VVIS++", "Breadworks - vvis++"],
            ["VRAD"] = ["VRAD++", "Breadworks - vrad++"],
            ["BSPZIP"] = ["BSPZIP++", "Breadworks - bspzip++"],
        };

        /// <summary>
        /// Markers shared by the whole tools++ suite. Needed as well as the per-tool banners because not
        /// every tool carries one: the Hammer++ era bspzipplusplus.exe has no "BSPZIP++" string at all
        /// and is only identifiable by the author tag. Verified against tools++ builds dated 2026-08-02
        /// and the standalone builds dated 2026-09-04.
        /// </summary>
        private static readonly string[] SuiteMarkers = ["ficool2", "HammerPlusPlus", "Hammer++", "Breadworks - "];

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

        /// <summary>
        /// Subfolders searched inside a standalone tools++ folder. Mostly the folder itself - the
        /// archive unpacks flat - but a user who keeps it next to a copy of Hammer++, or who unpacked
        /// into a wrapper folder, ends up one level down.
        /// </summary>
        private static readonly string[] StandaloneSubfolders =
        [
            "",
            "bin",
            "win64",
            Path.Combine("bin", "win64"),
        ];

        /// <summary>
        /// Folder names <see cref="AutoDetectFolder"/> looks for. The download is a zip named after the
        /// tools, so the folder it becomes is almost always one of these.
        /// </summary>
        private static readonly string[] StandaloneFolderNames =
        [
            "Tools++",
            "tools_plusplus",
            "toolsplusplus",
            "ToolsPlusPlus",
            "Hammer++ Tools",
        ];

        private record CacheKey(string Path, long Length, DateTime LastWrite);

        private static readonly Dictionary<string, bool> DetectionCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, CacheKey> DetectionKeys = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// Resolution verdicts by process name. Null means "nothing better than the configured path",
        /// which is cached too - otherwise the lookup reruns for every parameter on every rebuild of the
        /// parameter list.
        /// </summary>
        private static readonly Dictionary<string, string?> ResolutionCache = new(StringComparer.OrdinalIgnoreCase);

        private static string? autoDetectedFolder;
        private static bool autoDetectRan;

        /// <summary>
        /// Returns the binary that should actually be run for the given process. Prefers a tools++ build
        /// from the standalone install or from next to the configured one; otherwise returns the
        /// configured path unchanged.
        ///
        /// An empty configured path is still worth resolving: a game configuration written before the
        /// tools existed has nothing for BSPZIP, and a standalone install supplies one anyway.
        /// </summary>
        public static string? ResolveBinary(string processName, string? configuredPath)
        {
            if (!ToolMarkers.ContainsKey(processName))
                return configuredPath;

            if (!ConfigurationManager.Settings.PreferToolsPlusPlusBinaries)
                return configuredPath;

            if (ResolutionCache.TryGetValue(processName, out var cached))
                return cached ?? configuredPath;

            string? resolved = FindPreferredBinary(processName, configuredPath);
            ResolutionCache[processName] = resolved;

            if (resolved is not null && !string.Equals(resolved, configuredPath, StringComparison.OrdinalIgnoreCase))
                CompilePalLogger.LogLine(string.IsNullOrEmpty(configuredPath)
                    ? $"Using tools++ {processName} at \"{resolved}\"; nothing is configured for it."
                    : $"Using tools++ {processName} at \"{resolved}\" instead of the configured \"{configuredPath}\".");

            return resolved ?? configuredPath;
        }

        /// <summary>
        /// Same lookup as <see cref="ResolveBinary"/>, but never writes the resolution cache. Safe to call
        /// speculatively for a live UI hint while a path is still being edited and not yet saved -
        /// <see cref="ResolveBinary"/>'s cache is keyed by process name alone, so caching a result for a
        /// path the user hasn't committed to would poison the binary actually used by the next compile.
        /// </summary>
        public static string? PreviewResolveBinary(string processName, string? configuredPath)
        {
            if (!ToolMarkers.ContainsKey(processName))
                return configuredPath;

            if (!ConfigurationManager.Settings.PreferToolsPlusPlusBinaries)
                return configuredPath;

            return FindPreferredBinary(processName, configuredPath) ?? configuredPath;
        }

        /// <summary>
        /// The tools++ binary to run instead of <paramref name="configuredPath"/>, or null when there
        /// isn't a better one than what is already configured.
        /// </summary>
        private static string? FindPreferredBinary(string processName, string? configuredPath)
        {
            string? configuredFileName = string.IsNullOrEmpty(configuredPath) ? null : Path.GetFileName(configuredPath);
            var fileNames = CandidateFileNames(processName, configuredFileName).ToList();

            // A standalone install wins over anything under the game. It is the copy the user pointed
            // Compile Pal at, and now that the tools no longer have to live in bin/ it is the one that
            // gets updated - a drop-in copy left in a game's bin folder is the stale one.
            string? standalone = FindInFolder(processName, EffectiveStandaloneFolder(), fileNames);
            if (standalone is not null)
                return standalone;

            // nothing configured and no standalone install: there is nowhere else to look
            if (string.IsNullOrEmpty(configuredPath))
                return null;

            // already tools++, nothing to do
            if (IsToolsPlusPlusBinary(processName, configuredPath))
                return configuredPath;

            string? binFolder = Path.GetDirectoryName(configuredPath);
            if (binFolder is null)
                return null;

            // the configured path may itself already be a bin subfolder, so search its parent too
            var roots = new List<string> { binFolder };
            string? parent = Path.GetDirectoryName(binFolder);
            if (parent is not null)
                roots.Add(parent);

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

            return null;
        }

        /// <summary>
        /// The standalone folder actually in use: what the user set, or what auto-detection found when
        /// they have set nothing.
        /// </summary>
        private static string? EffectiveStandaloneFolder()
        {
            string? configured = ConfigurationManager.Settings.ToolsPlusPlusFolder;
            return !string.IsNullOrWhiteSpace(configured) ? configured.Trim() : AutoDetectFolder();
        }

        /// <summary>
        /// The tools++ build of <paramref name="processName"/> inside <paramref name="folder"/>, or null
        /// if there isn't one. Takes no notice of the settings, so the settings window can ask about a
        /// folder the user is still choosing.
        /// </summary>
        private static string? FindInFolder(string processName, string? folder, IEnumerable<string> fileNames)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return null;

            var names = fileNames.ToList();

            foreach (var subfolder in StandaloneSubfolders)
            {
                foreach (var fileName in names)
                {
                    string candidate;
                    try
                    {
                        candidate = Path.Combine(folder, subfolder, fileName);
                    }
                    catch (ArgumentException)
                    {
                        // the folder is typed by hand in settings, so it can contain anything
                        return null;
                    }

                    if (File.Exists(candidate) && IsToolsPlusPlusBinary(processName, candidate))
                        return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// Which compilers a folder supplies, by CompileProcess name. Used by the settings window to say
        /// what a folder actually contains before it is saved, and by auto-detection to tell a real
        /// install from a folder that merely has the right name.
        /// </summary>
        public static IReadOnlyList<string> ToolsInFolder(string? folder)
        {
            var found = new List<string>();

            if (string.IsNullOrWhiteSpace(folder))
                return found;

            foreach (var processName in ToolMarkers.Keys)
            {
                var fileNames = CandidateFileNames(processName, configuredFileName: null);
                if (FindInFolder(processName, folder.Trim(), fileNames) is not null)
                    found.Add(processName);
            }

            return found;
        }

        /// <summary>
        /// A standalone install found without being told where to look, or null if there isn't one.
        ///
        /// Only consulted when the setting is empty. It exists because the tools became standalone
        /// between releases: an install that used to be found automatically inside a game's bin folder
        /// now sits somewhere else entirely, and the alternative to probing is every existing user
        /// silently losing tools++ support until they notice a settings field they never had before.
        ///
        /// Cached for the session - the probe is cheap, but it runs on the UI thread from the settings
        /// window and there is no reason to repeat it.
        /// </summary>
        public static string? AutoDetectFolder()
        {
            if (autoDetectRan)
                return autoDetectedFolder;

            autoDetectRan = true;
            autoDetectedFolder = ProbeForStandaloneFolder();

            if (autoDetectedFolder is not null)
                CompilePalLogger.LogLineDebug($"tools++ standalone install auto-detected at \"{autoDetectedFolder}\".");

            return autoDetectedFolder;
        }

        private static string? ProbeForStandaloneFolder()
        {
            foreach (var root in ProbeRoots())
            {
                foreach (var name in StandaloneFolderNames)
                {
                    string folder;
                    try
                    {
                        folder = Path.Combine(root, name);
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }

                    // the name alone proves nothing - an empty folder called Tools++ is not an install
                    if (Directory.Exists(folder) && ToolsInFolder(folder).Count > 0)
                        return folder;
                }
            }

            return null;
        }

        /// <summary>Folders a downloaded archive plausibly got unpacked into.</summary>
        private static IEnumerable<string> ProbeRoots()
        {
            var specials = new[]
            {
                Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.DesktopDirectory,
                Environment.SpecialFolder.UserProfile,
            };

            foreach (var special in specials)
            {
                string path = Environment.GetFolderPath(special);
                if (!string.IsNullOrEmpty(path))
                    yield return path;
            }

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile))
                yield return Path.Combine(profile, "Downloads");

            // a portable setup that keeps the tools next to Compile Pal itself
            string? appFolder = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(appFolder))
                yield return appFolder;
        }

        /// <summary>
        /// File names to look for, most tools++ specific first. The installer ships the tools under
        /// their own names (vbspplusplus.exe, vradplusplus.exe, bspzipplusplus.exe, ...) alongside the
        /// stock binaries rather than overwriting them, so searching only for the configured file name
        /// finds nothing even when tools++ is installed.
        /// </summary>
        private static IEnumerable<string> CandidateFileNames(string processName, string? configuredFileName)
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

        /// <summary>
        /// What the compiler that will actually run for <paramref name="processName"/> says it
        /// accepts, or null when it does not say - a stock tool, nothing configured, or a process that
        /// is not one of the four compilers. Honours the ForceOff setting, under which no binary is
        /// asked anything and the shipped parameter lists are the whole truth.
        /// </summary>
        public static ToolHelp? HelpFor(string? processName)
        {
            if (processName is null || !ToolMarkers.ContainsKey(processName))
                return null;

            if (ConfigurationManager.Settings.ToolsPlusPlusMode == ToolsPlusPlusMode.ForceOff)
                return null;

            string? path = ResolveBinary(processName, GetConfiguredPath(processName));
            return ToolHelpProbe.Probe(path);
        }

        /// <summary>
        /// Clears cached detection and resolution, e.g. after the game configuration or the settings
        /// change. Also forgets the auto-detected standalone folder, so an install added while Compile
        /// Pal was open is picked up without a restart.
        /// </summary>
        public static void Invalidate()
        {
            DetectionCache.Clear();
            DetectionKeys.Clear();
            ResolutionCache.Clear();
            ToolHelpProbe.Invalidate();
            autoDetectedFolder = null;
            autoDetectRan = false;
        }

        /// <summary>Logs which binary is in use for every known compiler and whether it is tools++.</summary>
        public static void LogDetectionResults()
        {
            if (ConfigurationManager.Settings.ToolsPlusPlusMode != ToolsPlusPlusMode.Auto)
                CompilePalLogger.LogLineDebug($"tools++ parameter visibility overridden by setting: {ConfigurationManager.Settings.ToolsPlusPlusMode}");

            string? standaloneFolder = EffectiveStandaloneFolder();
            if (standaloneFolder is not null)
            {
                var supplied = ToolsInFolder(standaloneFolder);
                CompilePalLogger.LogLineDebug(supplied.Count > 0
                    ? $"tools++ standalone folder \"{standaloneFolder}\" supplies {string.Join(", ", supplied)}."
                    : $"tools++ standalone folder \"{standaloneFolder}\" contains no recognised compilers.");
            }

            foreach (var processName in ToolMarkers.Keys)
            {
                string? configured = GetConfiguredPath(processName);
                if (configured is null)
                    continue;

                string? resolved = ResolveBinary(processName, configured);
                bool toolsPlusPlus = resolved is not null && IsToolsPlusPlusBinary(processName, resolved);
                var help = toolsPlusPlus ? ToolHelpProbe.Probe(resolved) : null;

                CompilePalLogger.LogLineDebug(
                    $"tools++ detection: {processName} -> \"{resolved}\" is {(toolsPlusPlus ? "tools++" : "stock")}" +
                    (help is not null ? $", {help.Label}, {help.Options.Count} options" : ""));
            }
        }

        private static bool IsToolsPlusPlusBinary(string processName, string path)
        {
            if (!File.Exists(path))
                return false;

            // The standalone bspzip++ carries no banner of any kind - not "BSPZIP++", not an author tag,
            // nothing - so scanning it can only ever come back negative. The name settles it instead:
            // the stock binary is bspzip.exe, and nothing but a tools++ build ships as bspzip++.exe or
            // bspzipplusplus.exe.
            if (HasToolsPlusPlusFileName(path))
                return true;

            // A binary that answers -help with an option table is tools++ by definition: nothing
            // else prints one. This is the detection that matters now; the banner scan below is kept
            // for a build that somehow cannot be run - a blocked executable, a broken dependency.
            if (ToolHelpProbe.Probe(path) is not null)
                return true;

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

        /// <summary>
        /// Whether the file name itself identifies a tools++ build - vbsp++.exe, bspzipplusplus.exe and
        /// so on. The suffix is only ever used by these tools; the stock SDK binaries are plain vbsp.exe.
        /// </summary>
        private static bool HasToolsPlusPlusFileName(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);

            return name.EndsWith("++", StringComparison.Ordinal)
                || name.EndsWith("plusplus", StringComparison.OrdinalIgnoreCase);
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
