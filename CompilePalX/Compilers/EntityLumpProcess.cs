using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CompilePalX.Compiling;

namespace CompilePalX.Compilers
{
    /// <summary>
    /// Moves the compiled map's entities out of the BSP and into the lump override file beside it.
    ///
    /// WHERE THIS SITS, AND WHY
    ///
    /// Order 11.5, which is after CUBEMAPS and before REPACK. Both halves of that matter.
    ///
    /// After PACK (10) and CUBEMAPS (11), because both read the entity lump and would come up empty
    /// without it. PACK walks the entities to find the models, materials and sounds a map depends
    /// on, so stripping first would produce a BSP packed with almost nothing. CUBEMAPS needs the
    /// env_cubemap entities to know where to build cubemaps from, and it writes its results back
    /// into the BSP afterwards.
    ///
    /// Before REPACK (12) and BSPZIP (12.1), for two reasons. The first is correctness: bspzip's
    /// -compress LZMA-compresses lumps, including this one, and a compressed entity lump cannot be
    /// read as text - so running afterwards would fail on exactly the maps most likely to want
    /// this. The second is that it makes the result better. REPACK rebuilds the BSP's lump layout
    /// from the header, so the blanked region left behind here is simply not carried over: the
    /// entity text is gone from the file rather than zeroed in place, and the file shrinks.
    ///
    /// Both orderings work when nothing is repacked. Only this one works when something is.
    ///
    /// WHAT IT OPERATES ON
    ///
    /// The BSP in the game's maps folder, which is what the engine loads and what every step from
    /// COPY (4) onwards has been editing. The lump file is written beside it, which is where the
    /// engine looks for it. A copy is also placed beside the BSP in the map source folder when the
    /// two are separate, so the pair travels together when the map is uploaded from there.
    /// </summary>
    class EntityLumpProcess : CompileProcess
    {
        public EntityLumpProcess() : base("ENTLUMP") { }

        private const string KeepEntitiesArg = "-keepentities";
        private const string NoWorldspawnArg = "-noworldspawn";
        private const string NoMirrorArg = "-nomirror";

        public override void Run(CompileContext context, CancellationToken cancellationToken)
        {
            CompileErrors = [];

            if (!CanRun(context)) return;

            string args = GetParameterString();
            bool dryRun = args.Contains(KeepEntitiesArg);
            bool keepWorldspawn = !args.Contains(NoWorldspawnArg);
            bool mirror = !args.Contains(NoMirrorArg);

            try
            {
                CompilePalLogger.LogLine("\nCompilePal - Entity Lump Extraction", 900);

                /*
                 * The maps-folder copy, not the one beside the VMF.
                 *
                 * That is the file the engine loads and the file PACK, CUBEMAPS and REPACK have all
                 * been working on. Falling back to the source BSP covers a compile with COPY turned
                 * off, where the maps-folder copy was never made.
                 */
                string bsp = File.Exists(context.CopyLocation) ? context.CopyLocation : context.BSPFile;

                if (!File.Exists(bsp))
                {
                    CompilePalLogger.LogCompileError($"Could not find a compiled BSP: {bsp}\n",
                        new Error($"Could not find a compiled BSP: {bsp}", ErrorSeverity.Error));
                    return;
                }

                if (cancellationToken.IsCancellationRequested) return;

                if (dryRun)
                {
                    ReportWithoutChanging(bsp);
                    return;
                }

                var result = EntityLumpExtractor.Extract(bsp, keepWorldspawn);

                if (!result.Extracted)
                {
                    // Not an error. The common reasons are "already extracted" and "nothing to do",
                    // and neither should stop a compile that has otherwise succeeded.
                    CompilePalLogger.LogLineColor(result.Message, Error.GetSeverityBrush(1));
                    return;
                }

                CompilePalLogger.LogLine(result.Message);
                CompilePalLogger.LogLine(
                    $"Wrote {Path.GetFileName(result.LumpFile!)} ({result.LumpBytes:N0} bytes) beside the map.");

                Verify(bsp, result.LumpFile!);

                if (mirror)
                    Mirror(context, bsp, result.LumpFile!);

                /*
                 * Said every time, because the consequence of not knowing it is a map that loads
                 * with no entities at all and no error to explain why.
                 */
                CompilePalLogger.LogLineColor(
                    "The .lmp must sit in the maps folder next to the .bsp. It cannot be packed " +
                    "inside the BSP, and a map shipped without it will load with no entities.",
                    Error.GetSeverityBrush(2));
            }
            catch (Exception e)
            {
                CompilePalLogger.LogLine("Something broke:");
                CompilePalLogger.LogLineCompileError($"{e}",
                    new Error(e.ToString(), "CompilePal Internal Error", ErrorSeverity.FatalError));
            }
        }

        /// <summary>Says what would happen, and touches nothing.</summary>
        private static void ReportWithoutChanging(string bsp)
        {
            int? revision = EntityLumpExtractor.BspRevision(bsp);

            if (revision is null)
            {
                CompilePalLogger.LogLineColor($"{Path.GetFileName(bsp)} is not a readable BSP.",
                    Error.GetSeverityBrush(3));
                return;
            }

            CompilePalLogger.LogLine(
                $"Keep entities is set, so {Path.GetFileName(bsp)} was left alone. " +
                $"It would have written {Path.GetFileName(EntityLumpExtractor.LumpFileNameFor(bsp))}.");
        }

        /// <summary>
        /// Reads the pair back and checks they belong together.
        ///
        /// The engine silently ignores a lump file whose map revision does not match the BSP beside
        /// it, and the symptom is a map with no entities rather than an error - so a mismatch is
        /// worth catching here, while the compile output is still on screen.
        /// </summary>
        private static void Verify(string bsp, string lumpFile)
        {
            int? bspRevision = EntityLumpExtractor.BspRevision(bsp);
            int? lumpRevision = EntityLumpExtractor.LumpFileRevision(lumpFile);

            if (bspRevision is null || lumpRevision is null)
            {
                CompilePalLogger.LogLineColor("Could not read back the map revision to check the pair.",
                    Error.GetSeverityBrush(3));
                return;
            }

            if (bspRevision != lumpRevision)
            {
                string message =
                    $"The lump file records map revision {lumpRevision} but the BSP is {bspRevision}. " +
                    "The engine will ignore the lump file and the map will load with no entities.";

                CompilePalLogger.LogCompileError(message + "\n",
                    new Error(message, "Entity lump revision mismatch", ErrorSeverity.Error));
                return;
            }

            string? text = EntityLumpExtractor.ReadLumpFile(lumpFile);
            int recovered = text is null ? 0 : EntityLumpExtractor.CountEntities(System.Text.Encoding.ASCII.GetBytes(text));

            if (recovered == 0)
            {
                const string message = "The lump file was written but no entities could be read back from it.";
                CompilePalLogger.LogCompileError(message + "\n",
                    new Error(message, "Entity lump unreadable", ErrorSeverity.Error));
                return;
            }

            CompilePalLogger.LogLine($"Verified: {recovered:N0} entities readable, map revision {bspRevision}.");
        }

        /// <summary>
        /// Copies the lump file beside the other BSP, when the map source folder and the maps folder
        /// are different places.
        ///
        /// PACK already copies the packed BSP back to the map source folder. Without this the .lmp
        /// would exist only in the maps folder, so uploading the map from the source folder would
        /// ship a BSP whose entities are nowhere.
        /// </summary>
        private static void Mirror(CompileContext context, string source, string lumpFile)
        {
            string other = string.Equals(Path.GetFullPath(source), Path.GetFullPath(context.CopyLocation),
                               StringComparison.OrdinalIgnoreCase)
                ? context.BSPFile
                : context.CopyLocation;

            if (string.IsNullOrEmpty(other)) return;

            if (string.Equals(Path.GetFullPath(other), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
                return;

            if (!File.Exists(other))
            {
                CompilePalLogger.LogLineDebug($"No BSP at {other}, so no lump file copied there.");
                return;
            }

            /*
             * Only when the two BSPs are the same build. A map source folder holding an older BSP -
             * because REPACK writes only to the maps folder - would otherwise be given a lump file
             * from a different revision, which is the exact mismatch this is meant to avoid.
             */
            int? sourceRevision = EntityLumpExtractor.BspRevision(source);
            int? otherRevision = EntityLumpExtractor.BspRevision(other);

            if (sourceRevision is null || otherRevision is null || sourceRevision != otherRevision)
            {
                CompilePalLogger.LogLineColor(
                    $"Not copying the lump file to {Path.GetDirectoryName(other)}: the BSP there is a " +
                    "different revision, so the pair would not match.",
                    Error.GetSeverityBrush(2));
                return;
            }

            try
            {
                string destination = EntityLumpExtractor.LumpFileNameFor(other);
                File.Copy(lumpFile, destination, overwrite: true);
                CompilePalLogger.LogLine($"Copied {Path.GetFileName(destination)} to {Path.GetDirectoryName(other)}");
            }
            catch (IOException e)
            {
                CompilePalLogger.LogLineColor($"Could not copy the lump file: {e.Message}",
                    Error.GetSeverityBrush(3));
            }
        }
    }
}
