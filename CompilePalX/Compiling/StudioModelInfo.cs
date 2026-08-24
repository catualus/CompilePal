using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

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

        /*
         * ... numlocalposeparameters(300) localposeparamindex(304) surfacepropindex(308)
         *     keyvalueindex(312) keyvaluesize(316)
         *
         * The block those two describe is the model's mdlkeyvalue text, which is where prop_data
         * lives. Verified against every loose .mdl in a Garry's Mod install: of 467 files containing
         * a prop_data block, this offset pair located it in 467.
         */
        private const int KeyValueIndexOffset = 312;

        public enum StaticPropSupport
        {
            /// <summary>The model file could not be found in any content path.</summary>
            Unknown,
            Supported,
            NotSupported,
        }

        private static readonly Dictionary<string, StaticPropSupport> cache =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, bool?> propDataCache =
            new(StringComparer.OrdinalIgnoreCase);

        public static void ClearCache()
        {
            lock (cache) cache.Clear();
            lock (propDataCache) propDataCache.Clear();
        }

        /// <summary>
        /// Whether the model carries a prop_data block, or null if the file could not be read.
        ///
        /// A prop_physics needs one: without it the model has no mass, no health and no break
        /// behaviour, and vbsp refuses to create the entity ("uses model X, which has no propdata
        /// which means the model will not be able to be created"). Its absence is decidable from the
        /// model alone, which is what makes it safe to act on.
        ///
        /// The reverse question - whether a model that HAS prop_data may still be used as a
        /// prop_static - is deliberately not answered here. That depends on "allowstatic", which is
        /// usually inherited through the block's "base" from scripts/propdata.txt, a file that ships
        /// inside a VPK. Guessing it would convert scenery into physics props on a false positive.
        /// </summary>
        public static bool? HasPropData(string modelPath, IEnumerable<string> contentDirectories)
        {
            string key = modelPath.Replace('\\', '/').ToLowerInvariant();

            lock (propDataCache)
            {
                if (propDataCache.TryGetValue(key, out var cached))
                    return cached;
            }

            var result = ProbePropData(key, contentDirectories);

            lock (propDataCache) propDataCache[key] = result;
            return result;
        }

        private static bool? ProbePropData(string modelPath, IEnumerable<string> contentDirectories)
        {
            foreach (string dir in contentDirectories)
            {
                string full;
                try { full = Path.Combine(dir, modelPath); }
                catch (ArgumentException) { continue; }

                if (!File.Exists(full))
                    continue;

                try
                {
                    byte[] header = new byte[KeyValueIndexOffset + 8];

                    using var stream = File.OpenRead(full);
                    if (stream.Read(header, 0, header.Length) < header.Length)
                        return null;

                    if (BitConverter.ToInt32(header, 0) != IdstMagic)
                        return null;

                    int index = BitConverter.ToInt32(header, KeyValueIndexOffset);
                    int size = BitConverter.ToInt32(header, KeyValueIndexOffset + 4);

                    // No keyvalue block at all is a definite answer: no prop_data.
                    if (size <= 0 || index <= 0)
                        return false;

                    if (index + (long)size > stream.Length)
                        return null;   // header disagrees with the file; do not guess from it

                    stream.Seek(index, SeekOrigin.Begin);
                    byte[] text = new byte[size];
                    if (stream.Read(text, 0, size) < size)
                        return null;

                    return Encoding.ASCII.GetString(text)
                        .Contains("prop_data", StringComparison.OrdinalIgnoreCase);
                }
                catch (IOException) { return null; }
                catch (UnauthorizedAccessException) { return null; }
            }

            // Possibly inside a VPK, where vbsp will find it and we cannot look.
            return null;
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
