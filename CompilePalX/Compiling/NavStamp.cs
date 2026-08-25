using System;
using System.IO;

namespace CompilePalX.Compiling
{
    /// <summary>
    /// Keeps a nav mesh's record of its map's size in step with the map.
    ///
    /// A .nav stores the size of the .bsp it was built for, and the engine compares the two when it
    /// loads a map, printing "Warning! .nav file is out of date!" when they disagree. The mesh is not
    /// stale when that happens here: it was built for this map, and then the map changed size.
    ///
    /// It changes because of what comes after the mesh is built. Packing content into the BSP grows
    /// it, repacking rewrites it, and moving the entity lump out shrinks it. None of those can run
    /// before the mesh is built, because a mesh has to be read from a BSP that still has its entities.
    /// So the stamp is always written before the last thing that invalidates it, and nothing that
    /// generates a mesh can predict what the size will end up being.
    ///
    /// Which is why this is not the mesh generator's job. The tools that change the BSP are the ones
    /// that know it changed, and they all live here, so the fix belongs at the end of a compile rather
    /// than in a step of its own that someone has to know to add.
    /// </summary>
    static class NavStamp
    {
        /// <summary>Where the size sits: magic, version and subversion each take four bytes ahead of it.</summary>
        private const int SizeOffset = 12;

        private const uint Magic = 0xFEEDFACE;

        /// <summary>Below this the header has no subversion field, so the size is not where we expect it.</summary>
        private const uint MinimumVersion = 10;

        /// <summary>
        /// Rewrites the size in the mesh beside <paramref name="bspPath"/>, if there is one and it
        /// disagrees.
        ///
        /// Quiet when there is nothing to do, which is the common case: most compiles have no mesh
        /// beside the map, and a compile that did not change the BSP after building one leaves the
        /// stamp already correct.
        /// </summary>
        public static void Refresh(string bspPath)
        {
            try
            {
                if (!File.Exists(bspPath))
                    return;

                string navPath = Path.ChangeExtension(bspPath, ".nav");
                if (!File.Exists(navPath))
                    return;

                long size = new FileInfo(bspPath).Length;
                if (size > uint.MaxValue)
                    return;

                using var nav = new FileStream(navPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

                Span<byte> header = stackalloc byte[SizeOffset + 4];
                if (nav.Read(header) != header.Length)
                    return;

                // Only a file this recognises, and only where the field is where it thinks. Anything
                // else is left alone rather than guessed at: writing four bytes into the middle of a
                // file that is not what we assume would corrupt somebody's mesh.
                if (BitConverter.ToUInt32(header[..4]) != Magic)
                    return;

                if (BitConverter.ToUInt32(header[4..8]) < MinimumVersion)
                    return;

                uint stamped = BitConverter.ToUInt32(header[SizeOffset..]);
                if (stamped == (uint)size)
                    return;

                nav.Seek(SizeOffset, SeekOrigin.Begin);
                nav.Write(BitConverter.GetBytes((uint)size));

                CompilePalLogger.LogLineColor(
                    $"Nav mesh: re-stamped for the final BSP ({stamped:N0} -> {size:N0} bytes)",
                    Error.GetSeverityBrush(1));
            }
            catch (Exception e)
            {
                // Never worth failing a finished compile over. The mesh still loads either way; all
                // that is lost is the warning going away.
                CompilePalLogger.LogLineDebug($"Could not re-stamp the nav mesh: {e.Message}");
            }
        }
    }
}
