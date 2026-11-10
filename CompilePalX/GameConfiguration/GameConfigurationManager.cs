using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json;
using CompilePalX.Compiling;

namespace CompilePalX
{
    static class GameConfigurationManager
    {
        public static GameConfiguration? GameConfiguration;
        public static GameConfiguration? GameConfigurationBackup;
        public static List<GameConfiguration> GameConfigurations;
        private static string GameConfigurationFolder = "./GameConfiguration";
        private static readonly string GameConfigurationsPath = Path.Combine(GameConfigurationFolder, "gameConfigs.json");

        public static string SubstituteValues(string text, string mapFile = "", bool quote = true, string? sourceMapFile = null)
        {
            // Before $vmfFile$ only for tidiness: neither name contains the other.
            text = text.Replace("$sourceVmf$", FormatValue(sourceMapFile ?? mapFile, quote));
            text = text.Replace("$vmfFile$", FormatValue(mapFile, quote));
            text = text.Replace("$map$", FormatValue(Path.GetFileNameWithoutExtension(mapFile), quote));
            text = text.Replace("$bsp$", FormatValue(Path.ChangeExtension(mapFile, "bsp"), quote));

            text = text.Replace("$mapCopyLocation$", FormatValue(Path.Combine(GameConfiguration.MapFolder, Path.ChangeExtension(Path.GetFileName(mapFile), "bsp")), quote));

            text = text.Replace("$game$", FormatValue(GameConfiguration.GameFolder, quote));
            // game configs often name a launcher that no longer exists (Garry's Mod hl2.exe -> gmod.exe)
            text = text.Replace("$gameEXE$", FormatValue(GameExeResolver.Resolve(GameConfiguration.GameEXE), quote));
            text = text.Replace("$binFolder$", FormatValue(GameConfiguration.BinFolder, quote));
            text = text.Replace("$mapFolder$", FormatValue(GameConfiguration.MapFolder, quote));
            text = text.Replace("$gameName$", FormatValue(GameConfiguration.Name, quote));
            text = text.Replace("$sdkFolder$", FormatValue(GameConfiguration.SDKMapFolder, quote));


            // resolve through the tools++ detector so a Hammer++ build sitting in bin/win64 is used in
            // preference to a stock binary the game configuration happens to point at
            text = text.Replace("$vbsp$", FormatValue(ToolsPlusPlusDetector.ResolveBinary("VBSP", GameConfiguration.VBSP) ?? GameConfiguration.VBSP, quote));
            text = text.Replace("$vvis$", FormatValue(ToolsPlusPlusDetector.ResolveBinary("VVIS", GameConfiguration.VVIS) ?? GameConfiguration.VVIS, quote));
            text = text.Replace("$vrad$", FormatValue(ToolsPlusPlusDetector.ResolveBinary("VRAD", GameConfiguration.VRAD) ?? GameConfiguration.VRAD, quote));


            text = text.Replace("$bspZip$", FormatValue(ToolsPlusPlusDetector.ResolveBinary("BSPZIP", GameConfiguration.BSPZip) ?? GameConfiguration.BSPZip, quote));
            text = text.Replace("$vbspInfo$", FormatValue(GameConfiguration.VBSPInfo, quote));


            return text;
        }

        private static string FormatValue(string text, bool quote)
        {
            if (quote)
                return $"\"{text}\"";
            return text;
        }

        /// <summary>
        /// Modifies the current context
        /// </summary>
        /// <param name="val">string in the form 'COMPILE_PAL_SET VARNAME VALUE'</param>
        public static void ModifyCurrentContext(string val)
        {
            val = val.Replace("COMPILE_PAL_SET ", "");
            val = Regex.Replace(val, @"\t|\r|\n", "");
            var firstSpace = val.IndexOf(" ");
            if (firstSpace < 0)
            {
                CompilePalLogger.LogLineColor($"Ignored COMPILE_PAL_SET with no value: '{val}'", 3);
                return;
            }
            var field = val.Substring(0, firstSpace);
            var value = val.Substring(firstSpace + 1).Replace("\"", "");

            switch (field)
            {
                case "vbsp_exe":
                    GameConfiguration.VBSP = value;
                    break;
                case "vvis_exe":
                    GameConfiguration.VVIS = value;
                    break;
                case "vrad_exe":
                    GameConfiguration.VRAD = value;
                    break;
                case "game_exe":
                    GameConfiguration.GameEXE = value;
                    break;
                case "bspzip_exe":
                    GameConfiguration.BSPZip = value;
                    break;
                case "vpk_exe":
                    GameConfiguration.VPK = value;
                    break;
                case "vbspinfo_exe":
                    GameConfiguration.VBSPInfo = value;
                    break;
                case "bspdir":
                    GameConfiguration.MapFolder = value;
                    break;
                case "sdkbspdir":
                    GameConfiguration.SDKMapFolder = value;
                    break;
                case "bindir":
                    GameConfiguration.BinFolder = value;
                    break;
                case "gamedir":
                    GameConfiguration.GameFolder = value;
                    break;
                case "file":
                    // The context for the map being compiled was built before its first step ran, so
                    // moving the live context is what lets a step ahead of VBSP hand it a different
                    // VMF. The redirect belongs to that one map and ends with it.
                    //
                    // This also used to write a static field that BuildContext preferred over the
                    // queued file. Only a map that finished cleared it, so after a cancelled or failed
                    // run the next compile - of any map - built the previous run's temp copy.
                    if (ActiveContext != null)
                        RedirectMapFile(ActiveContext, value);
                    else
                        CompilePalLogger.LogLineColor($"Ignored COMPILE_PAL_SET file \"{value}\": no map is being compiled.", 3);
                    break;
            }
        }

        /// <summary>The context of the map being compiled right now, or null between maps.</summary>
        public static CompileContext? ActiveContext { get; private set; }

        /// <summary>
        /// Starts a map's compile on a copy of the game configuration, so what a step changes with
        /// COMPILE_PAL_SET (vbsp_exe, bspdir, ...) lasts for that map only. Kept by reference, as it
        /// was, those changes were made to the configuration itself and outlived the map: every
        /// later map and run, and gameConfigs.json, kept them.
        /// </summary>
        public static void BackupCurrentContext()
        {
            GameConfigurationBackup = GameConfiguration;
            if (GameConfiguration != null)
                GameConfiguration = (GameConfiguration)GameConfiguration.Clone();
        }

        /// <summary>
        /// Ends the map's compile, however it ended: puts back the configuration the user chose and
        /// drops the map's context. Runs from a finally in the compile loop, so a cancelled or failed
        /// map is cleaned up as well as a finished one.
        /// </summary>
        public static void RestoreCurrentContext()
        {
            if (GameConfigurationBackup != null)
                GameConfiguration = GameConfigurationBackup;
            GameConfigurationBackup = null;
            ActiveContext = null;
        }

        public static CompileContext BuildContext(Map map)
        {
            var context = new CompileContext
            {
                Configuration = GameConfiguration,
                MapFile = map.File,
                SourceMapFile = map.File,
                Map = map,
                BSPFile = Path.ChangeExtension(map.File, "bsp"),
                CopyLocation = map.IsBSP ? map.File : Path.Combine(GameConfiguration.MapFolder, Path.ChangeExtension(Path.GetFileName(map.File), "bsp"))
            };
            ActiveContext = context;
            return context;
        }

        /// <summary>
        /// Points every later step of this map's compile at <paramref name="vmf"/> in place of the
        /// queued map. The copy should keep the queued map's file name, so the BSP it produces lands in
        /// the game's maps folder under the name the map has always had.
        /// </summary>
        public static void RedirectMapFile(CompileContext context, string vmf)
        {
            context.MapFile = vmf;
            context.BSPFile = Path.ChangeExtension(vmf, "bsp");
            if (!context.Map.IsBSP)
                context.CopyLocation = Path.Combine(context.Configuration.MapFolder, Path.ChangeExtension(Path.GetFileName(vmf), "bsp"));
            context.RedirectedAt = DateTime.UtcNow;
            context.MirroredBspWriteTime = null;
            CompilePalLogger.LogLineDebug($"Compiling {vmf} in place of {context.SourceMapFile}");
        }

        /// <summary>
        /// Brings the files VBSP writes beside the VMF it compiled back beside the queued map, once a
        /// redirected compile has produced them: the BSP, which the preview and anyone opening the map
        /// expect to find there, and the portal and leak files, which Hammer loads from the VMF's own
        /// folder.
        ///
        /// The .prt and .lin are made to match exactly, deletion included. VBSP deletes both before it
        /// compiles, so a pointfile left beside the queued map from an earlier leak would otherwise go
        /// on pointing at a hole the map no longer has.
        /// </summary>
        private static readonly TimeSpan MirrorClockSlack = TimeSpan.FromSeconds(2);

        public static void MirrorRedirectedOutputs(CompileContext context)
        {
            if (context.Map.IsBSP || context.RedirectedAt is not { } redirectedAt || !context.IsRedirected)
                return;

            string compiledBsp = Path.ChangeExtension(context.MapFile, "bsp");
            if (!File.Exists(compiledBsp))
                return;

            var written = File.GetLastWriteTimeUtc(compiledBsp);
            // Older than the redirect: a leftover from an earlier run in the same folder, not this one's.
            // With some slack, because file times come from a clock that only ticks every few
            // milliseconds, so a file written just after the redirect can be stamped just before it.
            if (written < redirectedAt - MirrorClockSlack || written == context.MirroredBspWriteTime)
                return;

            string sourceBase = Path.ChangeExtension(context.SourceMapFile, null);
            try
            {
                File.Copy(compiledBsp, sourceBase + ".bsp", overwrite: true);
                foreach (var extension in new[] { ".prt", ".lin" })
                {
                    string produced = Path.ChangeExtension(context.MapFile, extension);
                    string beside = sourceBase + extension;
                    if (File.Exists(produced))
                        File.Copy(produced, beside, overwrite: true);
                    else if (File.Exists(beside))
                        File.Delete(beside);
                }
                context.MirroredBspWriteTime = written;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Not fatal: the compile itself is in the redirected folder and goes on from there.
                CompilePalLogger.LogLineColor($"Could not copy the compiled files back beside {Path.GetFileName(context.SourceMapFile)}: {e.Message}", 3);
            }
        }

        public static void LoadGameConfigurations()
        {
            if (!Directory.Exists(GameConfigurationFolder))
                Directory.CreateDirectory(GameConfigurationFolder);

            //Loading the last used configurations for hammer
            RegistryKey? rk = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Hammer\General");

            var configs = new List<GameConfiguration>();

            //try loading json
            if (File.Exists(GameConfigurationsPath))
            {
                /*
                 * Guarded, as settings.json and mapfiles.json are. This file is rewritten on every
                 * start, so a power cut during that write leaves truncated JSON, and reading it
                 * unguarded crashed Compile Pal on every launch until the file was found and deleted
                 * by hand. The games it listed are mostly found again below from Hammer and Steam.
                 */
                try
                {
                    string jsonLoadText = File.ReadAllText(GameConfigurationsPath);
                    configs.AddRange(JsonConvert.DeserializeObject<List<GameConfiguration>>(jsonLoadText) ?? []);
                }
                catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
                {
                    string backup = GameConfigurationsPath + ".corrupt";
                    try { File.Copy(GameConfigurationsPath, backup, overwrite: true); }
                    catch (Exception copyFailure) { CompilePalLogger.LogLineDebug($"Could not keep a copy of the unreadable game list: {copyFailure.Message}"); }

                    CompilePalLogger.LogLineColor($"The saved game list could not be read ({e.Message}). A copy was kept as {Path.GetFileName(backup)}; games are found again from Hammer and Steam.", 3);
                }
            }

            //try loading from registry
            if (rk?.GetValue("Directory") is string hammerBinFolder && !string.IsNullOrWhiteSpace(hammerBinFolder))
            {
                try
                {
                    configs.AddRange(GameConfigurationParser.Parse(hammerBinFolder));
                }
                catch (Exception e)
                {
                    // Hammer remembers the last bin folder it ran from, and that game may since have
                    // been moved or uninstalled. This was reported as a crash, which exited the
                    // application on every start; it is no more serious than an unreadable game
                    // found through Steam below, and handled the same way.
                    CompilePalLogger.LogLineDebug($"Could not parse the game configuration in Hammer's last folder {hammerBinFolder}: {e.Message}");
                }
            }

            // Then every Source game Steam has installed. The registry key above only ever names the
            // last bin folder Hammer was started from, so without this a game is invisible until Hammer
            // has been opened for it - and switching between two games means alternating Hammer
            // launches to make each one appear.
            foreach (var binFolder in SteamGameScanner.FindBinFolders())
            {
                try
                {
                    configs.AddRange(GameConfigurationParser.Parse(binFolder));
                }
                catch (Exception e)
                {
                    // One unreadable game configuration must not lose the others, and it is not worth a
                    // dialog - this runs on every refresh, including at startup.
                    CompilePalLogger.LogLineDebug($"Could not parse the game configuration in {binFolder}: {e.Message}");
                }
            }

            // remove duplicates
            GameConfigurations = configs.GroupBy(g => (g.Name, g.GameFolder)).Select(grp => grp.First()).ToList();

            // Saving the merged list is a convenience; failing to (a read-only install folder, a
            // file another program holds) is no reason not to start.
            try
            {
                SaveGameConfigurations();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                CompilePalLogger.LogLineDebug($"Could not save the game list: {e.Message}");
            }
        }

        public static void SaveGameConfigurations()
        {
            string jsonSaveText = JsonConvert.SerializeObject(GameConfigurations, Formatting.Indented);
            // Atomic: a write cut short must leave the old list, not a truncated one.
            ConfigurationManager.WriteFileAtomic(GameConfigurationsPath, jsonSaveText);
        }
    }
}
