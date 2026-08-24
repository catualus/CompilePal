using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CompilePalX.Compiling
{
    /// <summary>What an extraction did, or why it did nothing.</summary>
    public sealed record EntityLumpResult(
        bool Extracted,
        string Message,
        int EntitiesMoved = 0,
        int LumpBytes = 0,
        string? LumpFile = null);

    /// <summary>
    /// Moves a compiled map's entity lump out of the BSP and into the lump override file the engine
    /// reads instead.
    ///
    /// Source loads <c>maps/&lt;name&gt;_l_&lt;index&gt;.lmp</c> in place of the BSP's own lump when one is
    /// present and its map revision matches, which is how Valve shipped entity-only patches without
    /// reshipping a map. The same mechanism means a BSP can be distributed with its entity lump
    /// almost empty: the server loads the map correctly from the .lmp beside it, while anything
    /// reading only the BSP finds no entities to decompile.
    ///
    /// This is obfuscation, not protection, and the header comment should say so plainly: the .lmp
    /// has to be readable by the engine, so it has to be readable by anyone who has it. What it
    /// defeats is the common case of someone running a decompiler over a downloaded BSP.
    ///
    /// Modelled on meepen's entremover_bsp, reimplemented rather than shelled out to so there is no
    /// second binary to ship and the failure cases below can be handled. Two differences from it:
    ///
    ///   - It writes the wrong length back into the BSP header. It sets lump[0].length to the size
    ///     of the part it DISCARDED rather than the part it kept, so the header describes a run of
    ///     bytes that starts at worldspawn and ends at an arbitrary point in the zero padding. The
    ///     value is usually large enough that nothing notices, but on a map whose worldspawn is
    ///     long and whose remaining entities are few it truncates worldspawn itself.
    ///   - It has no guard against being run twice. A second run over an already-stripped BSP
    ///     writes a .lmp containing nothing but worldspawn, destroying the real one.
    /// </summary>
    public static class EntityLumpExtractor
    {
        /// <summary>'VBSP' as a little-endian int, which is what sits at offset 0 of every BSP.</summary>
        private const int VbspIdent = 0x50534256;

        private const int LumpCount = 64;
        private const int LumpEntryBytes = 16;
        private const int LumpTableOffset = 8;
        private const int MapRevisionOffset = LumpTableOffset + (LumpCount * LumpEntryBytes);   // 1032
        private const int HeaderBytes = MapRevisionOffset + 4;                                  // 1036

        /// <summary>Entities are lump 0, which is also the only lump this deals with.</summary>
        private const int EntityLumpIndex = 0;

        /// <summary>
        /// lumpfileheader_t: lumpOffset, lumpID, lumpVersion, lumpLength, mapRevision.
        ///
        /// mapRevision matters. The engine refuses a .lmp whose revision does not match the BSP it
        /// sits beside, which is the mechanism that stops a stale override being applied to a
        /// recompiled map - and the reason this cannot simply be written once and reused.
        /// </summary>
        private const int LumpFileHeaderBytes = 20;

        /// <summary>The name the engine looks for beside a map: mapname_l_0.lmp.</summary>
        public static string LumpFileNameFor(string bspPath) =>
            Path.Combine(
                Path.GetDirectoryName(bspPath) ?? ".",
                $"{Path.GetFileNameWithoutExtension(bspPath)}_l_{EntityLumpIndex}.lmp");

        /// <summary>
        /// Rewrites <paramref name="bspPath"/> in place and writes the lump file beside it.
        ///
        /// Nothing is written unless the whole operation can be completed, so a failure part way
        /// through leaves a working map rather than a BSP with no entities and no override.
        /// </summary>
        public static EntityLumpResult Extract(string bspPath, bool keepWorldspawn = true)
        {
            byte[] bsp;
            try
            {
                bsp = File.ReadAllBytes(bspPath);
            }
            catch (IOException e)
            {
                return new EntityLumpResult(false, $"Could not read {Path.GetFileName(bspPath)}: {e.Message}");
            }

            if (bsp.Length < HeaderBytes)
                return new EntityLumpResult(false, "File is too small to be a BSP.");

            if (BitConverter.ToInt32(bsp, 0) != VbspIdent)
                return new EntityLumpResult(false, "Not a VBSP file.");

            int bspVersion = BitConverter.ToInt32(bsp, 4);

            /*
             * Left 4 Dead 2 reorders the fields inside each lump entry to version/offset/length
             * rather than offset/length/version. Detected the same way BSP.cs does it: on that
             * branch the first field of lump 0 is the lump version, which is 0.
             */
            bool isL4D2 = bspVersion == 21 && BitConverter.ToInt32(bsp, LumpTableOffset) == 0;

            int entry = LumpTableOffset + (EntityLumpIndex * LumpEntryBytes);
            int offsetField = isL4D2 ? entry + 4 : entry;
            int lengthField = isL4D2 ? entry + 8 : entry + 4;
            int versionField = isL4D2 ? entry : entry + 8;
            int fourCcField = entry + 12;

            int lumpOffset = BitConverter.ToInt32(bsp, offsetField);
            int lumpLength = BitConverter.ToInt32(bsp, lengthField);
            int lumpVersion = BitConverter.ToInt32(bsp, versionField);
            int fourCc = BitConverter.ToInt32(bsp, fourCcField);
            int mapRevision = BitConverter.ToInt32(bsp, MapRevisionOffset);

            if (lumpOffset < HeaderBytes || lumpLength <= 0 || (long)lumpOffset + lumpLength > bsp.Length)
                return new EntityLumpResult(false, "The entity lump is missing or its header is out of range.");

            /*
             * A compressed lump cannot be edited as text.
             *
             * bspzip's -compress stores the uncompressed size in fourCC and prefixes the data with
             * an LZMA header, so the bytes here are not entity text at all. Scanning them for a
             * closing brace would find one somewhere in the compressed stream and cut the lump at a
             * meaningless point.
             *
             * Refused rather than decompressed, and the compile step is ordered to run BEFORE
             * REPACK so this should not normally be reachable. If it is, the order has been changed.
             */
            if (fourCc != 0 || (lumpLength >= 4 && Encoding.ASCII.GetString(bsp, lumpOffset, 4) == "LZMA"))
            {
                return new EntityLumpResult(false,
                    "The entity lump is LZMA compressed, so it cannot be extracted. Run this step " +
                    "before REPACK rather than after it.");
            }

            var entities = new byte[lumpLength];
            Buffer.BlockCopy(bsp, lumpOffset, entities, 0, lumpLength);

            int entityCount = CountEntities(entities);

            /*
             * Idempotence guard, and the reason it matters.
             *
             * A BSP that has already been through this holds worldspawn and nothing else. Running
             * again would write a .lmp containing only worldspawn, overwriting the real one - and
             * the entities it replaced are in that file and nowhere else. Compiling a .bsp directly,
             * or re-running the tail of a compile, makes that a plausible accident rather than a
             * theoretical one.
             */
            if (entityCount <= 1)
            {
                return new EntityLumpResult(false,
                    "The entity lump holds one entity or none, so it looks extracted already. " +
                    "Left alone, because overwriting the lump file would lose the entities in it.");
            }

            /*
             * What stays behind.
             *
             * Worldspawn is kept by default because it carries the map's own properties - skyname,
             * detail material, world bounds - and tools that read a BSP without loading it expect
             * to find it. Everything after the first closing brace goes.
             */
            int keep = 0;
            if (keepWorldspawn)
            {
                int brace = Array.IndexOf(entities, (byte)'}');
                if (brace < 0)
                    return new EntityLumpResult(false, "The entity lump has no complete entity in it.");

                keep = brace + 1;
            }

            // The lump is a null-terminated string. Keeping the terminator costs one byte and means
            // the shortened lump is still the shape every reader expects.
            int keptLength = keep + 1;

            if (keptLength > lumpLength)
                return new EntityLumpResult(false, "The entity lump is too small to shorten.");

            /*
             * The bytes are blanked where they are and the lump is shortened in the header, rather
             * than the file being rebuilt without them.
             *
             * Every one of the other 63 lumps is addressed by an absolute offset into this file.
             * Removing bytes from the middle would move all of them and mean rewriting the whole
             * lump table, for no gain: a later REPACK rebuilds the file and drops the padding
             * anyway, and until then the padding is zeroes.
             */
            Array.Clear(bsp, lumpOffset + keptLength, lumpLength - keptLength);
            bsp[lumpOffset + keep] = 0;

            // The length of what was KEPT. entremover_bsp writes the length of what it discarded,
            // which describes a region running off the end of worldspawn into the padding.
            BitConverter.GetBytes(keptLength).CopyTo(bsp, lengthField);

            byte[] lumpFile = BuildLumpFile(entities, lumpVersion, mapRevision);
            string lumpPath = LumpFileNameFor(bspPath);

            /*
             * The lump file first, and to a temporary name.
             *
             * If writing it fails, the BSP has not been touched and the map still works. If the BSP
             * write fails afterwards, the map still works and there is a stray .lmp, which is inert
             * because the BSP it names still contains its own entities.
             */
            try
            {
                string temp = lumpPath + ".tmp";
                File.WriteAllBytes(temp, lumpFile);
                File.Move(temp, lumpPath, overwrite: true);
            }
            catch (IOException e)
            {
                return new EntityLumpResult(false, $"Could not write {Path.GetFileName(lumpPath)}: {e.Message}");
            }

            try
            {
                File.WriteAllBytes(bspPath, bsp);
            }
            catch (IOException e)
            {
                return new EntityLumpResult(false, $"Could not write {Path.GetFileName(bspPath)}: {e.Message}");
            }

            return new EntityLumpResult(
                true,
                $"Moved {entityCount - (keepWorldspawn ? 1 : 0)} entities out of the BSP.",
                entityCount - (keepWorldspawn ? 1 : 0),
                lumpLength,
                lumpPath);
        }

        /// <summary>Builds the .lmp: a 20 byte header, then the entity text unchanged.</summary>
        private static byte[] BuildLumpFile(byte[] entities, int lumpVersion, int mapRevision)
        {
            var file = new byte[LumpFileHeaderBytes + entities.Length];

            BitConverter.GetBytes(LumpFileHeaderBytes).CopyTo(file, 0);   // data starts after the header
            BitConverter.GetBytes(EntityLumpIndex).CopyTo(file, 4);
            BitConverter.GetBytes(lumpVersion).CopyTo(file, 8);
            BitConverter.GetBytes(entities.Length).CopyTo(file, 12);
            BitConverter.GetBytes(mapRevision).CopyTo(file, 16);

            entities.CopyTo(file, LumpFileHeaderBytes);
            return file;
        }

        /// <summary>
        /// Counts top-level entities by counting opening braces at depth zero.
        ///
        /// Braces inside a quoted value - an output whose parameter contains one, say - are skipped,
        /// because counting them would put the depth out and make an ordinary map look already
        /// extracted.
        /// </summary>
        public static int CountEntities(byte[] entities)
        {
            int count = 0;
            int depth = 0;
            bool inQuotes = false;

            foreach (byte b in entities)
            {
                if (b == 0) break;

                if (b == '"') { inQuotes = !inQuotes; continue; }
                if (inQuotes) continue;

                if (b == '{')
                {
                    if (depth == 0) count++;
                    depth++;
                }
                else if (b == '}' && depth > 0)
                {
                    depth--;
                }
            }

            return count;
        }

        /// <summary>
        /// Reads back the entity text from a lump file, for verification and for tests.
        ///
        /// Returns null if the file is not a lump file of the expected shape.
        /// </summary>
        public static string? ReadLumpFile(string path)
        {
            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch (IOException) { return null; }

            if (data.Length < LumpFileHeaderBytes) return null;

            int dataOffset = BitConverter.ToInt32(data, 0);
            int lumpId = BitConverter.ToInt32(data, 4);
            int length = BitConverter.ToInt32(data, 12);

            if (lumpId != EntityLumpIndex) return null;
            if (dataOffset < LumpFileHeaderBytes || length < 0) return null;
            if ((long)dataOffset + length > data.Length) return null;

            return Encoding.ASCII.GetString(data, dataOffset, length).TrimEnd('\0');
        }

        /// <summary>The map revision a lump file was built for, or null if it cannot be read.</summary>
        public static int? LumpFileRevision(string path)
        {
            try
            {
                byte[] data = File.ReadAllBytes(path);
                return data.Length < LumpFileHeaderBytes ? null : BitConverter.ToInt32(data, 16);
            }
            catch (IOException) { return null; }
        }

        /// <summary>The map revision recorded in a BSP header, or null if it cannot be read.</summary>
        public static int? BspRevision(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                if (stream.Length < HeaderBytes) return null;

                var header = new byte[HeaderBytes];
                stream.ReadExactly(header, 0, HeaderBytes);

                return BitConverter.ToInt32(header, 0) != VbspIdent
                    ? null
                    : BitConverter.ToInt32(header, MapRevisionOffset);
            }
            catch (IOException) { return null; }
        }
    }
}
