using CompilePalX.Compiling;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CompilePalX.Compilers
{
    class CubemapProcess : CompileProcess
    {
        public CubemapProcess() : base("CUBEMAPS") { }

        bool HDR = false;
        bool LDR = false;

        string vbspInfo;
        string bspFile;


        public override void Run(CompileContext context, CancellationToken cancellationToken)
        {
            CompileErrors = [];

            if (!CanRun(context)) return;

            vbspInfo = context.Configuration.VBSPInfo;
            bspFile = context.CopyLocation;

            // listen for cancellations
            cancellationToken.Register(() =>
            {
                try
                {
                    Cancel();
                }
                catch (InvalidOperationException) { }
                catch (Exception e) { ExceptionHandler.LogException(e); }
            });

            try
            {
                CompilePalLogger.LogLine("\nCompilePal - Cubemap Generator", 900);

                if (!File.Exists(context.CopyLocation))
                {
                    throw new FileNotFoundException();
                }

                var addtionalParameters = Regex.Replace(GetParameterString(), "-hidden", "");
                addtionalParameters = Regex.Replace(addtionalParameters, @"-iterations \w", "");
                bool hidden = GetParameterString().Contains("-hidden");

                string buildCubemapCommand = "-buildcubemaps";
                if (GetParameterString().Contains("-iterations"))
                {
                    try
                    {
                        int iterations = int.Parse(Regex.Match(GetParameterString(), @"-iterations (\w)").Groups[1].Value);
                        buildCubemapCommand = $"{buildCubemapCommand} {iterations}";
                    } catch
                    {
                        CompilePalLogger.LogCompileError("-iterations must be an int\n", new Error("-iterations must be an int", "CompilePal Internal Error", ErrorSeverity.FatalError));
                        return;
                    }
                }

                FetchHDRLevels();

                string gameExe = GameExeResolver.Resolve(context.Configuration.GameEXE);
                string mapname = System.IO.Path.GetFileName(context.CopyLocation).Replace(".bsp", "");

                string args =
                    $"-steam -game \"{context.Configuration.GameFolder}\" -windowed -insecure -novid +mat_specular 0 %HDRevel% +map {mapname} {buildCubemapCommand} {addtionalParameters}";

                if (hidden)
                    args += " -noborder -x 4000 -y 2000";

                if (HDR && LDR)
                {
                    CompilePalLogger.LogLine("Map requires two sets of cubemaps");

                    if (cancellationToken.IsCancellationRequested) return;
                    CompilePalLogger.LogLine("Compiling LDR cubemaps...");
                    RunCubemaps(gameExe, args.Replace("%HDRevel%", "+mat_hdr_level 0"), cancellationToken);

                    if (cancellationToken.IsCancellationRequested) return;
                    CompilePalLogger.LogLine("Compiling HDR cubemaps...");
                    RunCubemaps(gameExe, args.Replace("%HDRevel%", "+mat_hdr_level 2"), cancellationToken);
                }
                else
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    CompilePalLogger.LogLine("Map requires one set of cubemaps");
                    CompilePalLogger.LogLine("Compiling cubemaps...");
                    RunCubemaps(gameExe, args.Replace("%HDRevel%", ""), cancellationToken);
                }
                if (cancellationToken.IsCancellationRequested) return;
                CompilePalLogger.LogLine("Cubemaps compiled");
            }
            catch (FileNotFoundException)
            {
                CompilePalLogger.LogCompileError($"Could not find file: {context.CopyLocation}", new Error($"Could not find file: {context.CopyLocation}", ErrorSeverity.Error));
            }
            catch (Exception exception)
            {
                CompilePalLogger.LogLine("Something broke:");
                CompilePalLogger.LogLineCompileError($"{exception}", new Error(exception.ToString(), "CompilePal Internal Error", ErrorSeverity.FatalError));
            }

        }

        public void RunCubemaps(string gameEXE, string args, CancellationToken cancellationToken)
        {
            // Record the BSP's state up front. Building cubemaps rewrites the BSP, so comparing before
            // and after is what tells us the work actually happened - the old code just called
            // WaitForExit, which returns instantly when Steam re-spawns the game as another process,
            // making the step claim success having built nothing (upstream issues #256, #262).
            var before = SnapshotBsp();

            // GameLauncher finds the re-spawned process so we wait on the game rather than the launcher
            Process = GameLauncher.Launch(gameEXE, args, cancellationToken);

            if (Process is null)
                return;

            GameLauncher.WaitForExit(Process, cancellationToken);

            if (cancellationToken.IsCancellationRequested)
                return;

            // Recorded, not acted on. Source games return arbitrary exit codes - which is exactly why
            // this step stopped trusting them (upstream issues #256, #262) - so a non-zero one is worth
            // having in the log when diagnosing a failure but must not be turned back into an error.
            TryLogExitCode();

            var after = SnapshotBsp();

            if (after is null)
            {
                // The BSP could not be read afterwards at all, which is a worse outcome than an
                // unchanged one and previously went unreported: both branches below need a snapshot.
                CompilePalLogger.LogLineCompileError($"{Path.GetFileName(bspFile)} could not be read after the game exited, so it is not known whether cubemaps were built.",
                    new Error("Cubemaps could not be verified - BSP unreadable after the game exited", ErrorSeverity.Error));
            }
            else if (before is not null && before == after)
            {
                CompilePalLogger.LogLineCompileError($"The game closed without modifying {Path.GetFileName(bspFile)}, so no cubemaps were built. Check that the map loads and that Steam is running.",
                    new Error("Cubemaps were not built - BSP unchanged after the game exited", ErrorSeverity.Error));
            }
        }

        /// <summary>
        /// Notes the game's exit code in the debug log, if it can still be read.
        ///
        /// Reading ExitCode throws when the process was never started by us or has already been
        /// released, and a diagnostic line is never worth an exception.
        /// </summary>
        private void TryLogExitCode()
        {
            try
            {
                if (Process is { HasExited: true } process && process.ExitCode != 0)
                    CompilePalLogger.LogLineDebug($"The game exited with code {process.ExitCode} (Source exit codes are not reliable; the BSP check below decides the outcome).");
            }
            catch (Exception e)
            {
                CompilePalLogger.LogLineDebug($"Could not read the game's exit code: {e.Message}");
            }
        }

        /// <summary>Size and last-write time of the BSP, used to confirm cubemaps were actually written.</summary>
        private (long Length, DateTime LastWrite)? SnapshotBsp()
        {
            try
            {
                var info = new FileInfo(bspFile);
                return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
            }
            catch (Exception ex)
            {
                CompilePalLogger.LogLineDebug($"Could not inspect {bspFile}: {ex.Message}");
                return null;
            }
        }

        public void FetchHDRLevels()
        {
            CompilePalLogger.LogLine("Detecting HDR levels...");
            var startInfo = new ProcessStartInfo(vbspInfo, "\"" + bspFile + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            Process = new Process { StartInfo = startInfo };
            try
            {
                Process.Start();
            }
            catch (Exception e)
            {
                CompilePalLogger.LogDebug(e.ToString());
                CompilePalLogger.LogCompileError($"Failed to run executable: {Process.StartInfo.FileName}\n", new Error($"Failed to run executable: {Process.StartInfo.FileName}", ErrorSeverity.Warning));
                CompilePalLogger.LogLine("Could not read HDR levels, defaulting to one.");
                return;
            }

            string output = Process.StandardOutput.ReadToEnd();

            if (Process.ExitCode != 0)
                CompilePalLogger.LogLine("Could not read HDR levels, defaulting to one.");
            else{
                Regex re = new Regex(@"^LDR\sworldlights\s+.*", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                string LDRStats = re.Match(output).Value.Trim();
                re = new Regex(@"^HDR\sworldlights\s+.*", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                string HDRStats = re.Match(output).Value.Trim();
                LDR = !LDRStats.Contains(" 0/");
                HDR = !HDRStats.Contains(" 0/");
            }
        }
    }
}
