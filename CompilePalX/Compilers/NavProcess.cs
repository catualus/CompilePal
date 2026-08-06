using CompilePalX.Compiling;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CompilePalX.Compilers
{
    class NavProcess : CompileProcess
    {
        public NavProcess() : base("NAV") { }

        static string mapname;
        static string mapnav;
        static string mapcfg;
        static string mapCFGBackup;
        static string listenServerCfg;
        static string listenServerCfgBackup;
        static string? navLuaPath;
        private string mapLogPath;
        private string consoleLogPath;

        bool hidden;

        /// <summary>
        /// Commands the game runs once the map is loaded.
        ///
        /// Deliberately no nav_save: nav_generate runs asynchronously over many frames, so a nav_save on
        /// the following line would execute immediately and write nothing useful. Generation saves the
        /// mesh itself on completion, which is the "'...nav' saved." line this step waits for.
        /// </summary>
        private static string BuildNavCfg(string mapLog) =>
            $"con_logfile {mapLog}\n{NavSetup}\nnav_generate\n";

        /// <summary>
        /// nav_draw_limit 1 is a crash guard: drawing the mesh while it is being built is a known
        /// source of crashes during generation, and nothing is watching the screen during a compile.
        /// </summary>
        private const string NavSetup = NavQuickSave + "\nnav_draw_limit 1";

        /// <summary>
        /// Forced on because it is the single largest cost in generation. With nav_quicksave 0 the
        /// engine also runs the full analysis pass (hiding spots, approach areas, encounter spots),
        /// which dwarfs the sampling pass. It defaults to 1, but a user's config.cfg can leave it at 0,
        /// and there is no reason for a compile step to pay for analysis data.
        /// </summary>
        private const string NavQuickSave = "nav_quicksave 1";

        /// <summary>
        /// Garry's Mod does not reliably execute cfg/&lt;mapname&gt;.cfg or cfg/listenserver.cfg for a
        /// listen server, which leaves the game idling in the map having never run nav_generate. Lua
        /// autorun always runs, so on GMod this drives generation instead. Removed again in CleanUp.
        /// </summary>
        private static string BuildNavLua(string mapLog) =>
            "-- Temporary file written by Compile Pal to trigger nav mesh generation. Safe to delete.\n" +
            "hook.Add(\"InitPostEntity\", \"CompilePalNavGenerate\", function()\n" +
            "    timer.Simple(10, function()\n" +
            $"        game.ConsoleCommand(\"con_logfile {mapLog}\\n\")\n" +
            $"        game.ConsoleCommand(\"{NavQuickSave}\\n\")\n" +
            "        game.ConsoleCommand(\"nav_draw_limit 1\\n\")\n" +
            "        game.ConsoleCommand(\"nav_generate\\n\")\n" +
            "    end)\n" +
            "end)\n";

        public override void Run(CompileContext context, CancellationToken cancellationToken)
        {
            CompileErrors = [];

            if (!CanRun(context)) return;

            try
            {
                CompilePalLogger.LogLine("\nCompilePal - Nav Generator", 900);

                if (!File.Exists(context.CopyLocation))
                {
                    throw new FileNotFoundException();
                }

                mapname = System.IO.Path.GetFileName(context.CopyLocation).Replace(".bsp", "");
                mapnav = context.CopyLocation.Replace(".bsp", ".nav");
                mapcfg = context.Configuration.GameFolder + "/cfg/" + mapname + ".cfg";
                mapCFGBackup = context.Configuration.GameFolder + "/cfg/" + mapname + "_cpalbackup.cfg";
                listenServerCfg = context.Configuration.GameFolder + "/cfg/listenserver.cfg";
                listenServerCfgBackup = context.Configuration.GameFolder + "/cfg/listenserver_cpalbackup.cfg";
                string mapLog = mapname + "_nav.log";
                mapLogPath = Path.Combine(context.Configuration.GameFolder, mapLog);
                // -condebug writes the console to this unconditionally, so detection does not depend on
                // con_logfile having been reached
                consoleLogPath = Path.Combine(context.Configuration.GameFolder, "console.log");

                if (cancellationToken.IsCancellationRequested) return;
                DeleteNav(mapname, context.Configuration.GameFolder);

                hidden = GetParameterString().Contains("-hidden");

                var addtionalParameters = Regex.Replace(GetParameterString(), "\b-hidden\b", "");

                // -condebug mirrors the whole console to console.log regardless of whether our cfg ran
                string args =
                    $"-steam -condebug -game \"{context.Configuration.GameFolder}\" -windowed -insecure -novid +log 0 +sv_logflush 1 +sv_cheats 1 +map {mapname} {addtionalParameters}";

                if (hidden)
                    args += " -noborder -x 4000 -y 2000";

                string gameExe = GameExeResolver.Resolve(context.Configuration.GameEXE);

                CompilePalLogger.LogLine("Generating...");

                string navCfg = BuildNavCfg(mapLog);

                // Write the commands to both hooks. Garry's Mod does not reliably execute
                // cfg/<mapname>.cfg on a listen server, but it does execute cfg/listenserver.cfg, so
                // relying on the map cfg alone leaves the game sitting in the map doing nothing.
                WriteCfgWithBackup(mapcfg, mapCFGBackup, navCfg);
                WriteCfgWithBackup(listenServerCfg, listenServerCfgBackup, navCfg);

                // Garry's Mod ignores both cfg hooks for listen servers, so drive generation from Lua
                // autorun there instead. Written into the game's own lua folder and removed in CleanUp.
                navLuaPath = null;
                string luaAutorunFolder = Path.Combine(context.Configuration.GameFolder, "lua", "autorun", "server");
                if (Directory.Exists(Path.Combine(context.Configuration.GameFolder, "lua")))
                {
                    try
                    {
                        Directory.CreateDirectory(luaAutorunFolder);
                        navLuaPath = Path.Combine(luaAutorunFolder, "compilepal_navgen.lua");
                        File.WriteAllText(navLuaPath, BuildNavLua(mapLog));
                        CompilePalLogger.LogLineDebug($"Wrote nav generation autorun script to {navLuaPath}");
                    }
                    catch (Exception ex)
                    {
                        CompilePalLogger.LogLineDebug($"Could not write nav autorun script: {ex.Message}");
                        navLuaPath = null;
                    }
                }

                if (File.Exists(mapLogPath))
                    File.Delete(mapLogPath);
                if (File.Exists(consoleLogPath))
                    TryDelete(consoleLogPath);

                bool navSaved = false;

                using (TextReader tr = new StreamReader(File.Open(mapLogPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite)))
                {
                    // Launch through GameLauncher: with a cold Steam the process we start exits almost
                    // immediately while Steam re-spawns the real game, which used to end this loop early
                    // via HasExited and report success with no nav mesh built.
                    Process = GameLauncher.Launch(gameExe, args, cancellationToken);

                    if (Process is null)
                    {
                        CleanUp();
                        return;
                    }

                    // read from -condebug's console.log too, so we still see output when the game never
                    // reached our con_logfile command
                    using var consoleReader = OpenConsoleLog();

                    bool failed = false;
                    do
                    {
                        Thread.Sleep(100);

                        foreach (var line in ReadAvailable(tr).Concat(ReadAvailable(consoleReader)))
                        {
                            if (line.Contains("Cannot generate Navigation Mesh."))
                            {
                                CompilePalLogger.LogLineCompileError($"Failed to build nav: {line}", new Error($"Failed to build nav: {line}", ErrorSeverity.Error));
                                failed = true;
                                break;
                            }

                            if (line.Contains(".nav' saved.") || line.Contains(".nav' saved"))
                            {
                                navSaved = true;
                                break;
                            }
                        }
                    } while (!navSaved && !failed && !cancellationToken.IsCancellationRequested && !HasExited(Process));
                }

                ExitClient();

                if (cancellationToken.IsCancellationRequested) return;

                // A running game is not proof of a built nav mesh, so confirm the file is really there
                // rather than reporting success off the back of the game having exited.
                if (!navSaved && !File.Exists(mapnav))
                {
                    CompilePalLogger.LogCompileError($"Nav generation finished but no nav file was produced at {mapnav}. The game may have closed before generation completed.\n",
                        new Error($"No nav file produced at {mapnav}", "Nav failed", ErrorSeverity.Error));
                    return;
                }

                CompilePalLogger.LogLine("nav file complete!");
            }
            catch (FileNotFoundException)
            {
                CompilePalLogger.LogCompileError($"Could not find {context.CopyLocation}\n", new Error($"Could not find {context.CopyLocation}", "Nav failed", ErrorSeverity.Error));
            }
            catch (Exception exception)
            {
                CompilePalLogger.LogLine("Something broke:");
                CompilePalLogger.LogCompileError($"{exception}\n", new Error(exception.ToString(), "CompilePal Internal Error", ErrorSeverity.FatalError));
            }
        }

        private static void DeleteNav(string mapname, string gamefolder)
        {
            List<string> navdirs = BSPPack.BSPPack.GetSourceDirectories(gamefolder, false);
            foreach (string source in navdirs)
            {
                string externalPath = source + "/maps/" + mapname + ".nav";

                if (File.Exists(externalPath))
                {
                    CompilePalLogger.LogLine("Deleting existing nav file.");
                    File.Delete(externalPath);
                }
            }
        }

        /// <summary>Exiting processes we adopted rather than started can throw, so treat failures as exited.</summary>
        private static bool HasExited(Process process)
        {
            try { return process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }

        private void ExitClient()
        {
            if (Process != null && !HasExited(Process))
            {
                try
                {
                    this.Process.Kill();
                    // the game holds the console log open; give Windows time to release the handle so
                    // CleanUp does not fail deleting it (upstream issue #266 on Garry's Mod x86-64)
                    Process.WaitForExit(5000);
                }
                catch (Win32Exception) { }
                catch (InvalidOperationException) { }
            }

            CleanUp();
        }
        /// <summary>
        /// Writes a cfg, preserving any pre-existing one. Skips the backup when the file already holds
        /// our generated content: an earlier run whose cleanup failed would otherwise get "backed up"
        /// over the user's real cfg, and restoring it would leave our commands behind permanently.
        /// </summary>
        private static void WriteCfgWithBackup(string path, string backupPath, string contents)
        {
            if (File.Exists(path))
            {
                bool isOurs;
                try { isOurs = File.ReadAllText(path).Contains("nav_generate"); }
                catch (Exception) { isOurs = false; }

                if (!isOurs)
                {
                    if (File.Exists(backupPath))
                        File.Delete(backupPath);
                    File.Move(path, backupPath);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        /// <summary>Restores a cfg from its backup, or removes ours if there was nothing to restore.</summary>
        private static void RestoreCfg(string path, string backupPath)
        {
            if (File.Exists(path))
                File.Delete(path);
            if (File.Exists(backupPath))
                File.Move(backupPath, path);
        }

        private StreamReader? OpenConsoleLog()
        {
            try
            {
                return new StreamReader(File.Open(consoleLogPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite));
            }
            catch (Exception ex)
            {
                CompilePalLogger.LogLineDebug($"Could not open {consoleLogPath}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Drains whatever has been appended to a log since the last read.</summary>
        private static IEnumerable<string> ReadAvailable(TextReader? reader)
        {
            if (reader is null)
                yield break;

            while (reader.ReadLine() is { } line)
                yield return line;
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (Exception) { /* the game may still hold it open; not worth failing the step */ }
        }

        private void CleanUp()
        {
            // give time for process to release file handles
            Thread.Sleep(500);
            try
            {
                RestoreCfg(mapcfg, mapCFGBackup);
                RestoreCfg(listenServerCfg, listenServerCfgBackup);

                if (navLuaPath is not null && File.Exists(navLuaPath))
                    TryDelete(navLuaPath);

                if (File.Exists(mapLogPath))
                    TryDelete(mapLogPath);
            }
            catch (Exception e)
            {
                CompilePalLogger.LogCompileError($"Failed to cleanup temporary file: {e}\n", new Error($"Failed to cleanup temporary file: {e}\n", "CompilePal Internal Error", ErrorSeverity.Info));
            }
        }

        public override void Cancel()
        {
            ExitClient();
        }
    }
}
