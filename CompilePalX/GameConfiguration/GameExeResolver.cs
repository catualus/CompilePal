using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CompilePalX.Compiling;

namespace CompilePalX
{
    /// <summary>
    /// Resolves the executable used to launch a game for the steps that need a running client
    /// (nav generation, cubemap building, the GAME step).
    ///
    /// Hammer game configurations routinely point at an executable that no longer exists. Garry's Mod
    /// replaced hl2.exe with gmod.exe but Hammer++ still writes "GameExe" ".../hl2.exe", and several
    /// Valve games moved their launcher during the 64-bit updates (see upstream issue #274 for
    /// Counter-Strike: Source). Launching a missing path fails the step for a reason that looks nothing
    /// like the real cause, so fall back to a launcher that actually exists in the same folder.
    /// </summary>
    public static class GameExeResolver
    {
        /// <summary>
        /// Known launcher names, most specific first. hl2.exe is last because it is the legacy default
        /// that game configs keep naming even when the game ships its own launcher.
        /// </summary>
        private static readonly string[] KnownLaunchers =
        [
            "gmod.exe",
            "csgo.exe",
            "left4dead2.exe",
            "portal2.exe",
            "swarm.exe",
            "hl2.exe",
        ];

        /// <summary>Executables in a game root that are never the launcher.</summary>
        private static readonly HashSet<string> NotLaunchers = new(StringComparer.OrdinalIgnoreCase)
        {
            "srcds.exe", "hammer.exe", "hammerplusplus.exe", "studiomdl.exe", "vbsp.exe",
            "vvis.exe", "vrad.exe", "bspzip.exe", "vpk.exe", "vbspinfo.exe", "vtex.exe",
            "hlmv.exe", "hlfaceposer.exe", "gmad.exe", "gmpublish.exe", "shadercompile.exe",
            "dmxconvert.exe", "mksheet.exe",
        };

        private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Clears cached resolutions, e.g. after the game configuration changes.</summary>
        public static void Invalidate() => Cache.Clear();

        /// <summary>
        /// Logs which launcher the game-driven steps will use, so a missing or substituted executable is
        /// visible in debug.log before a compile rather than only when nav or cubemaps fail.
        /// </summary>
        public static void LogResolution()
        {
            string? configured = GameConfigurationManager.GameConfiguration?.GameEXE;
            if (string.IsNullOrWhiteSpace(configured))
                return;

            string resolved = Resolve(configured);
            CompilePalLogger.LogLineDebug($"Game executable: configured \"{configured}\" -> using \"{resolved}\"");
        }

        /// <summary>
        /// Returns an executable that exists, preferring the configured one. Returns the configured path
        /// unchanged when nothing better is found, so callers still produce their usual error.
        /// </summary>
        public static string Resolve(string? configuredPath, string? gameExeDir = null)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
                return configuredPath ?? string.Empty;

            if (File.Exists(configuredPath))
                return configuredPath;

            if (Cache.TryGetValue(configuredPath, out var cached))
                return cached;

            string resolved = Find(configuredPath, gameExeDir) ?? configuredPath;
            Cache[configuredPath] = resolved;

            if (!string.Equals(resolved, configuredPath, StringComparison.OrdinalIgnoreCase))
                CompilePalLogger.LogLine($"Game executable \"{configuredPath}\" does not exist; using \"{resolved}\" instead.");
            else
                CompilePalLogger.LogCompileError($"Game executable \"{configuredPath}\" does not exist and no replacement was found. Set the correct path in the game configuration.\n",
                    new Error($"Game executable not found: {configuredPath}", "Game executable missing", ErrorSeverity.Error));

            return resolved;
        }

        private static string? Find(string configuredPath, string? gameExeDir)
        {
            // search the configured location first, then the directory the game config named
            var roots = new List<string>();

            string? configuredDir = Path.GetDirectoryName(configuredPath);
            if (!string.IsNullOrEmpty(configuredDir))
                roots.Add(configuredDir);

            if (!string.IsNullOrEmpty(gameExeDir) && !roots.Contains(gameExeDir, StringComparer.OrdinalIgnoreCase))
                roots.Add(gameExeDir);

            foreach (var root in roots)
            {
                if (!Directory.Exists(root))
                    continue;

                foreach (var launcher in KnownLaunchers)
                {
                    string candidate = Path.Combine(root, launcher);
                    if (File.Exists(candidate))
                        return candidate;
                }

                // Nothing known matched. If the folder holds exactly one plausible executable, that is
                // almost certainly the launcher - this is what catches renamed or new launchers.
                try
                {
                    var plausible = Directory.GetFiles(root, "*.exe")
                        .Where(f => !NotLaunchers.Contains(Path.GetFileName(f)))
                        .ToArray();

                    if (plausible.Length == 1)
                        return plausible[0];
                }
                catch (Exception ex)
                {
                    CompilePalLogger.LogLineDebug($"Failed to scan \"{root}\" for a game executable: {ex.Message}");
                }
            }

            return null;
        }
    }
}
