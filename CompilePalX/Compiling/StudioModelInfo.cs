using System;
using System.Collections.Generic;
using System.IO;

namespace CompilePalX.Compiling
{
    /// <summary>
    /// Reads just enough of an MDL header to answer "was this compiled with $staticprop?".
    ///
    /// vbsp deletes any prop_static whose model lacks the flag ("Error! To use model X as static
    /// prop, it must be compiled with $staticprop! Deleted."), so knowing this before the compile
    /// is what lets the entity be converted instead of silently disappearing from the map.
    /// </summary>
    public static class StudioModelInfo
    {
        // studiohdr_t: id(4) version(4) checksum(4) name(64) length(4) eyeposition(12)
        // illumposition(12) hull_min(12) hull_max(12) view_bbmin(12) view_bbmax(12) flags(4)
        private const int FlagsOffset = 152;
        private const int StudioHdrFlagsStaticProp = 1 << 4;
        private const int IdstMagic = 0x54534449; // "IDST" little-endian

        public enum StaticPropSupport
        {
            /// <summary>The model file could not be found in any content path.</summary>
            Unknown,
            Supported,
            NotSupported,
        }

        private static readonly Dictionary<string, StaticPropSupport> cache =
            new(StringComparer.OrdinalIgnoreCase);

        public static void ClearCache()
        {
            lock (cache) cache.Clear();
        }

        /// <summary>
        /// Resolves <paramref name="modelPath"/> (as written in the VMF, e.g.
        /// "models/props_c17/door01_left.mdl") against the content directories and reads its flags.
        ///
        /// Results are cached: a map typically places one model thousands of times, and the answer
        /// cannot change during a compile.
        /// </summary>
        public static StaticPropSupport SupportsStaticProp(string modelPath, IEnumerable<string> contentDirectories)
        {
            string key = modelPath.Replace('\\', '/').ToLowerInvariant();

            lock (cache)
            {
                if (cache.TryGetValue(key, out var cached))
                    return cached;
            }

            var result = Probe(key, contentDirectories);

            lock (cache) cache[key] = result;
            return result;
        }

        private static StaticPropSupport Probe(string modelPath, IEnumerable<string> contentDirectories)
        {
            foreach (string dir in contentDirectories)
            {
                string full;
                try { full = Path.Combine(dir, modelPath); }
                catch (ArgumentException) { continue; }   // invalid characters in the VMF value

                if (!File.Exists(full))
                    continue;

                try
                {
                    using var stream = File.OpenRead(full);
                    using var reader = new BinaryReader(stream);

                    if (stream.Length < FlagsOffset + 4)
                        return StaticPropSupport.Unknown;

                    if (reader.ReadInt32() != IdstMagic)
                        return StaticPropSupport.Unknown;   // not an MDL, or a compressed variant

                    stream.Seek(FlagsOffset, SeekOrigin.Begin);
                    int flags = reader.ReadInt32();

                    return (flags & StudioHdrFlagsStaticProp) != 0
                        ? StaticPropSupport.Supported
                        : StaticPropSupport.NotSupported;
                }
                catch (IOException)
                {
                    return StaticPropSupport.Unknown;
                }
                catch (UnauthorizedAccessException)
                {
                    return StaticPropSupport.Unknown;
                }
            }

            // Not on disk anywhere we can see. It may still live inside a VPK, in which case vbsp
            // will find it and we simply cannot judge - so say nothing rather than guess.
            return StaticPropSupport.Unknown;
        }
    }
}
