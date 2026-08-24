using System;
using System.IO;
using CompilePalX.Compiling;

namespace CompilePalX.Compilers
{
    /// <summary>
    /// Works out where a finished BSP actually ended up.
    ///
    /// There are two candidates and which one is right depends on the preset:
    ///
    ///   beside the source   VBSP always writes the BSP next to the VMF it compiled.
    ///
    ///   the maps folder     The COPY step, which is off by default, copies it into the game's own
    ///                       maps folder. When that has run, that copy is the one the game loads,
    ///                       so it is the more useful answer.
    ///
    /// Existence is not enough to choose the copy. A BSP left in the maps folder by a compile from
    /// last week is still a file, and pointing at it after a run that did not copy would show the
    /// user a stale map while telling them it is the one they just built. So the copy only wins if
    /// it is at least as new as the one beside the source.
    /// </summary>
    public static class CompiledMapLocator
    {
        /// <summary>
        /// The BSP for a compiled map, or null if neither candidate exists.
        ///
        /// Null is the normal answer for a compile that failed before VBSP wrote anything, so it is
        /// a result rather than an error.
        /// </summary>
        /// <param name="mapFile">The queued file: a VMF, or a BSP if one was queued directly.</param>
        /// <param name="isBsp">True when the queued file is already a BSP.</param>
        /// <param name="gameMapFolder">The game's maps folder, or null when no game is configured.</param>
        public static string? ResolveBsp(string mapFile, bool isBsp, string? gameMapFolder)
        {
            if (string.IsNullOrWhiteSpace(mapFile))
                return null;

            try
            {
                // A BSP queued directly is already the artifact; there is nothing to change.
                string beside = isBsp ? mapFile : Path.ChangeExtension(mapFile, "bsp");

                if (!string.IsNullOrWhiteSpace(gameMapFolder))
                {
                    string copied = Path.Combine(gameMapFolder, Path.GetFileName(beside));

                    if (File.Exists(copied) && CopyIsCurrent(copied, beside))
                        return copied;
                }

                return File.Exists(beside) ? beside : null;
            }
            catch (Exception ex)
            {
                // A malformed path, or a folder that cannot be read. Neither is worth failing a
                // button click over.
                CompilePalLogger.LogLineDebug($"Could not locate the compiled map: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Whether the maps-folder copy is from this compile rather than an older one.
        ///
        /// With no BSP beside the source there is nothing to compare against, so the copy is taken
        /// on trust - that is the case where COPY ran and something later cleaned up the source
        /// folder, and the copy is then the only artifact there is.
        /// </summary>
        private static bool CopyIsCurrent(string copied, string beside)
        {
            if (!File.Exists(beside))
                return true;

            return File.GetLastWriteTimeUtc(copied) >= File.GetLastWriteTimeUtc(beside);
        }
    }
}
