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
        private static string? mapFile = null;
        public static GameConfiguration? GameConfiguration;
        public static GameConfiguration GameConfigurationBackup;
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
                    mapFile = value;
                    // The context for the map being compiled was built before its first step ran, so
                    // setting the field alone reached nothing until the next map. Moving the live
                    // context is what lets a step ahead of VBSP hand it a different VMF.
                    if (ActiveContext != null)
                        RedirectMapFile(ActiveContext, value);
                    break;
            }
        }

        /// <summary>The context of the map being compiled right now, or null between maps.</summary>
        public static CompileContext? ActiveContext { get; private set; }

        public static void BackupCurrentContext()
        {
            GameConfigurationBackup = GameConfiguration;
        }

        public static void RestoreCurrentContext()
        {
            GameConfiguration = GameConfigurationBackup;
            mapFile = null;
            ActiveContext = null;
        }

        public static CompileContext BuildContext(Map map)
        {
            var context = new CompileContext
            {
                Configuration = GameConfiguration,
                MapFile = GameConfigurationManager.mapFile ?? map.File,
                SourceMapFile = map.File,
                Map = map,
                BSPFile = Path.ChangeExtension(GameConfigurationManager.mapFile ?? map.File, "bsp"),
                CopyLocation = map.IsBSP ? map.File : Path.Combine(GameConfiguration.MapFolder, Path.ChangeExtension(Path.GetFileName(GameConfigurationManager.mapFile ?? map.File), "bsp"))
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
        public static void MirrorRedirectedOutputs(CompileContext context)
        {
            if (context.Map.IsBSP || context.RedirectedAt is not { } redirectedAt || !context.IsRedirected)
                return;

            string compiledBsp = Path.ChangeExtension(context.MapFile, "bsp");
            if (!File.Exists(compiledBsp))
                return;

            var written = File.GetLastWriteTimeUtc(compiledBsp);
            // Older than the redirect: a leftover from an earlier run in the same folder, not this one's.
            if (written < redirectedAt || written == context.MirroredBspWriteTime)
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
                string jsonLoadText = File.ReadAllText(GameConfigurationsPath);
                configs.AddRange(JsonConvert.DeserializeObject<List<GameConfiguration>>(jsonLoadText) ?? []);
            }

            //try loading from registry
            if (rk != null)
            {
                string binFolder = (string)rk.GetValue("Directory")!;

                try
                {
                    configs.AddRange(GameConfigurationParser.Parse(binFolder));
                }
                catch (Exception e)
                {
                    ExceptionHandler.LogException(e);
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
            
            SaveGameConfigurations();
        }

        public static void SaveGameConfigurations()
        {
            string jsonSaveText = JsonConvert.SerializeObject(GameConfigurations, Formatting.Indented);
            File.WriteAllText(GameConfigurationsPath, jsonSaveText);
        }
    }
}
