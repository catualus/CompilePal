using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using CompilePalX.Compiling;
// CompilePalX.Compilers.BSPPack is both a namespace and the class inside it, so the bare name
// resolves to the namespace from here.
using BSPPackProcess = CompilePalX.Compilers.BSPPack.BSPPack;

namespace CompilePalX.Compilers
{
    /// <summary>
    /// Repairs known-fixable defects in the VMF before vbsp reads it.
    ///
    /// There is no compiler flag for any of this. vbsp and vrad only read the map and complain;
    /// nothing you pass them makes a reversed light falloff correct or a non-static model usable as
    /// a prop_static. The repair has to happen to the source, which means before the compile - so
    /// this runs at order 0.5, ahead of VBSP.
    ///
    /// Everything here is limited to defects with a single unambiguous correct answer. Anything
    /// needing a judgement call is reported and left alone.
    /// </summary>
    class VmfFixProcess : CompileProcess
    {
        public VmfFixProcess() : base("VMFFIX") { }

        private const string DryRunArg = "-dryrun";
        private const string NoBackupArg = "-nobackup";
        private const string SkipLightsArg = "-nolights";
        private const string SkipStaticPropsArg = "-nostaticprops";
        private const string CheckMaterialsArg = "-materials";
        private const string SkipFadesArg = "-nofades";
        private const string SkipSkyNameArg = "-noskyname";
        private const string SkipEmptyBrushEntitiesArg = "-noemptybrushents";
        private const string SkipDisplacementsArg = "-nodisplacements";
        private const string SkipAreaportalsArg = "-noareaportals";
        private const string SkipOverlaysArg = "-nooverlays";
        private const string SkipPhysicsPropsArg = "-nophysicsprops";
        private const string SkipReportArg = "-noreport";

        /// <summary>
        /// Converting this many prop_static entities means adding that many edicts, and the engine's
        /// budget is 8192 for everything in the map. Worth saying out loud rather than reporting a
        /// large number as a success.
        /// </summary>
        private const int EdictWarningThreshold = 200;

        public override void Run(CompileContext context, CancellationToken cancellationToken)
        {
            CompileErrors = [];

            if (!CanRun(context)) return;

            string args = GetParameterString();
            bool dryRun = args.Contains(DryRunArg);
            bool backup = !args.Contains(NoBackupArg);
            bool doLights = !args.Contains(SkipLightsArg);
            bool doStaticProps = !args.Contains(SkipStaticPropsArg);
            bool doMaterials = args.Contains(CheckMaterialsArg);
            bool doFades = !args.Contains(SkipFadesArg);
            bool doSkyName = !args.Contains(SkipSkyNameArg);
            bool doEmptyBrushEntities = !args.Contains(SkipEmptyBrushEntitiesArg);
            bool doDisplacements = !args.Contains(SkipDisplacementsArg);
            bool doAreaportals = !args.Contains(SkipAreaportalsArg);
            bool doOverlays = !args.Contains(SkipOverlaysArg);
            bool doPhysicsProps = !args.Contains(SkipPhysicsPropsArg);
            bool doReport = !args.Contains(SkipReportArg);

            try
            {
                CompilePalLogger.LogLine("\nCompilePal - VMF Fixer", 900);

                if (!File.Exists(context.MapFile))
                {
                    CompilePalLogger.LogCompileError($"Could not find map file: {context.MapFile}\n",
                        new Error($"Could not find map file: {context.MapFile}", ErrorSeverity.Error));
                    return;
                }

                // Models can be recompiled between map compiles without restarting Compile Pal, and a
                // cached "not compiled with $staticprop" would then keep converting props the mapper
                // has since fixed. ClearCache existed for this and was never called.
                StudioModelInfo.ClearCache();

                var vmf = VmfDocument.Load(context.MapFile);
                if (cancellationToken.IsCancellationRequested) return;

                var contentDirs = ResolveContentDirectories(context);

                int fixes = 0;

                if (doLights)
                    fixes += Apply(VmfFixes.FixLightFalloff(vmf),
                        n => $"Fixed {n} light(s) with reversed falloff distances.");

                if (cancellationToken.IsCancellationRequested) return;

                if (doStaticProps)
                {
                    if (contentDirs.Count == 0)
                        CompilePalLogger.LogLineDebug("No content directories resolved; skipping prop_static checks.");
                    else
                    {
                        var staticProps = VmfFixes.FixStaticProps(vmf, contentDirs);
                        fixes += Apply(staticProps,
                            n => $"Converted {n} prop_static entities that vbsp would have deleted.");

                        // Each conversion turns a baked, edict-free static prop into a networked entity.
                        // A few is nothing; a few thousand is a map that will not load, and reporting
                        // that as a plain success would be actively misleading.
                        if (staticProps.Count >= EdictWarningThreshold)
                            CompilePalLogger.LogLineColor(
                                $"{staticProps.Count} props were converted to prop_dynamic_override. Each one costs an " +
                                "edict (the engine's limit is 8192 for the whole map) and is lit by the ambient cube " +
                                "rather than baked lighting. Recompiling those models with $staticprop is the real fix.",
                                Error.GetSeverityBrush(3));
                    }
                }

                if (cancellationToken.IsCancellationRequested) return;

                if (doFades)
                    fixes += Apply(VmfFixes.FixPropFadeDistances(vmf),
                        n => $"Fixed {n} prop(s) with reversed fade distances.");

                if (doSkyName)
                    fixes += Apply(VmfFixes.FixSkyName(vmf),
                        n => $"Corrected {n} skybox name(s) written as a file path.");

                if (doOverlays)
                    fixes += Apply(VmfFixes.FixOverlayRenderOrder(vmf),
                        n => $"Clamped {n} overlay render order(s) into the valid 0-3 range.");

                if (doPhysicsProps)
                {
                    if (contentDirs.Count != 0)
                        fixes += Apply(VmfFixes.FixPhysicsPropsWithoutPropData(vmf, contentDirs),
                            n => $"Converted {n} prop_physics entities whose models have no propdata.");
                }

                if (cancellationToken.IsCancellationRequested) return;

                /*
                 * The structural fixers run before RemoveEmptyBrushEntities, and that order matters:
                 * moving a displacement out of a func_detail is itself a way of emptying one, and the
                 * result would be a map that traded a displacement error for a "has no head node".
                 *
                 * MoveDisplacementsToWorld removes an entity it empties completely, so the two only
                 * overlap on entities that were already empty - but the ordering keeps that true if
                 * either changes later.
                 */
                if (doDisplacements)
                    fixes += Apply(VmfFixes.MoveDisplacementsToWorld(vmf),
                        n => $"Moved {n} displacement brush(es) out of brush entities and into the world.");

                if (doAreaportals)
                    fixes += Apply(VmfFixes.SplitMultiBrushAreaportals(vmf),
                        n => $"Split multi-brush areaportals into {n} additional single-brush areaportal(s).");

                if (doEmptyBrushEntities)
                    fixes += Apply(VmfFixes.RemoveEmptyBrushEntities(vmf),
                        n => $"Removed {n} brush entit(ies) that had no brushes and would have stopped vbsp.");

                if (cancellationToken.IsCancellationRequested) return;

                if (doMaterials)
                    MaterialChecks.Report(vmf, contentDirs);

                /*
                 * Faults that are real, are visible here, and are deliberately NOT repaired - each has
                 * more than one reasonable answer, so choosing one would be changing the map on a guess.
                 *
                 * Reported anyway because this step runs before vbsp: hearing about an origin brush in
                 * the world now beats hearing about it twenty minutes into a compile that then has to
                 * be thrown away. They do not count toward the fix total, since nothing was fixed.
                 */
                if (doReport)
                {
                    var unfixable = VmfFixes.ReportUnfixableFaults(vmf);
                    foreach (string line in unfixable.Descriptions)
                        CompilePalLogger.LogLineColor($"  {line}", Error.GetSeverityBrush(3));
                }

                if (fixes == 0)
                {
                    CompilePalLogger.LogLine("No fixable issues found.");
                    return;
                }

                if (dryRun)
                {
                    CompilePalLogger.LogLineColor(
                        $"{fixes} issue(s) found. Dry run is enabled, so the VMF was not modified.",
                        Error.GetSeverityBrush(1));
                    return;
                }

                if (backup)
                {
                    string backupPath = BackupPath(context.MapFile);
                    File.Copy(context.MapFile, backupPath, overwrite: true);
                    CompilePalLogger.LogLine($"Backed up original to {Path.GetFileName(backupPath)}");
                }

                vmf.Save(context.MapFile);
                CompilePalLogger.LogLine($"Applied {fixes} fix(es) to {Path.GetFileName(context.MapFile)}");
            }
            catch (Exception e)
            {
                CompilePalLogger.LogLine("Something broke:");
                CompilePalLogger.LogLineCompileError($"{e}",
                    new Error(e.ToString(), "CompilePal Internal Error", ErrorSeverity.FatalError));
            }
        }

        /// <summary>Logs a fixer's result and returns how many entities it changed.</summary>
        private static int Apply(VmfFixResult result, Func<int, string> summary)
        {
            foreach (string line in result.Descriptions)
                CompilePalLogger.LogLine($"  {line}");

            if (result.Count > 0)
                CompilePalLogger.LogLine(summary(result.Count));

            return result.Count;
        }

        /// <summary>
        /// Timestamped so a second compile cannot overwrite the last good copy of the map, which is
        /// the one thing that must not happen when a tool edits someone's source file.
        /// </summary>
        private static string BackupPath(string mapFile)
        {
            string dir = Path.GetDirectoryName(mapFile) ?? ".";
            string name = Path.GetFileNameWithoutExtension(mapFile);
            return Path.Combine(dir, $"{name}.{DateTime.Now:yyyyMMdd-HHmmss}.vmf.bak");
        }

        private static List<string> ResolveContentDirectories(CompileContext context)
        {
            try
            {
                return BSPPackProcess.GetSourceDirectories(context.Configuration.GameFolder, verbose: false);
            }
            catch (Exception e)
            {
                CompilePalLogger.LogLineDebug($"Could not resolve content directories: {e.Message}");
                return [];
            }
        }
    }
}
