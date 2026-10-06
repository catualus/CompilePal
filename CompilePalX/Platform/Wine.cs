using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace CompilePalX.Platform
{
    /// <summary>
    /// Whether Compile Pal is running under Wine, and the parts of the Linux side a Windows program
    /// can reach from there.
    ///
    /// Compile Pal is a Windows application, and on Linux it runs the way Hammer and the compile tools
    /// already do: under Wine or Proton. It works there, but a few things assume a Windows machine -
    /// finding Steam through the registry, a step that shuts the computer down - and those need to
    /// know which one they are on. Nothing else should branch on this.
    /// </summary>
    internal static class Wine
    {
        /// <summary>
        /// True under Wine or Proton. Detected the way Wine documents: its ntdll exports
        /// <c>wine_get_version</c>, which no Windows ntdll has.
        /// </summary>
        public static bool IsRunning { get; } = Detect();

        private static bool Detect()
        {
            try
            {
                return NativeLibrary.TryLoad("ntdll.dll", out var ntdll)
                       && NativeLibrary.TryGetExport(ntdll, "wine_get_version", out _);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The Linux home folder as a Windows path (<c>Z:\home\you</c>), or null when not under Wine.
        ///
        /// From WINEHOMEDIR, which Wine sets for every process it starts. HOME is not passed through -
        /// Wine replaces the Unix environment's idea of home with USERPROFILE, which is a folder inside
        /// the prefix rather than the user's real one.
        /// </summary>
        public static string? HomeFolder => IsRunning ? FromNtPath(Environment.GetEnvironmentVariable("WINEHOMEDIR")) : null;

        /// <summary>Strips the NT namespace prefix Wine writes WINEHOMEDIR with (<c>\??\Z:\home\you</c>).</summary>
        internal static string? FromNtPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            return path.StartsWith(@"\??\", StringComparison.Ordinal) ? path[4..] : path;
        }

        /// <summary>
        /// A Unix path as Windows programs see it under Wine, where the Unix root is drive Z:. Paths that
        /// are not Unix-absolute come back unchanged.
        /// </summary>
        public static string ToWindowsPath(string path) =>
            path.StartsWith('/') ? "Z:" + path.Replace('/', '\\') : path;

        /// <summary>
        /// Where Steam for Linux keeps itself, under a home folder: the native client, the Flatpak and
        /// the Snap. <c>.steam/steam</c> is a link the native client maintains to wherever it really
        /// lives, so it is tried first.
        /// </summary>
        internal static IEnumerable<string> LinuxSteamFolders(string home)
        {
            yield return Path.Combine(home, ".steam", "steam");
            yield return Path.Combine(home, ".local", "share", "Steam");
            yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
            yield return Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam");
        }
    }
}
