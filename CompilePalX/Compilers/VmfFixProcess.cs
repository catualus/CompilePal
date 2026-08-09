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

            try
            {
                CompilePalLogger.LogLine("\nCompilePal - VMF Fixer", 900);

                if (!File.Exists(context.MapFile))
                {
                    CompilePalLogger.LogCompileError($"Could not find map file: {context.MapFile}\n",
                        new Error($"Could not find map file: {context.MapFile}", ErrorSeverity.Error));
                    return;
                }

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
                        fixes += Apply(VmfFixes.FixStaticProps(vmf, contentDirs),
                            n => $"Converted {n} prop_static entities that vbsp would have deleted.");
                }

                if (cancellationToken.IsCancellationRequested) return;

                if (doMaterials)
                    MaterialChecks.Report(vmf, contentDirs);

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
