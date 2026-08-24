using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CompilePalX.Compiling;
using ValveKeyValue;

namespace CompilePalX {
    class GameConfigurationParser
    {
        private static KVSerializer KVSerializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);
        public static List<GameConfiguration> Parse(string binFolder)
        {
            // prioritize hammer++ configs, fallback to hammer if it doesn't exist
            string filename = Path.Combine(binFolder, "hammerplusplus", "hammerplusplus_gameconfig.txt");
            if (!File.Exists(filename))
                filename = Path.Combine(binFolder, "GameConfig.txt");

            var gameInfos = new List<GameConfiguration>();

            CompilePalLogger.LogLineDebug($"Reading Game Config: {filename}");
            using (var gameConfigFile = File.OpenRead(filename))
            {
                var data = KVSerializer.Deserialize(gameConfigFile);

                foreach (var gamedb in (IEnumerable<KVObject>)data["Games"])
                {
                    try
                    {
                        var hdb = gamedb["Hammer"];
                        if (hdb is null)
                        {
                            CompilePalLogger.LogLineDebug($"GameInfo block is missing Hammer section: {gamedb}");
                            continue;
                        }

                        CompilePalLogger.LogLineDebug($"Gamedb: {gamedb}");

                        // use vbsp as a backup path for finding other compile executables if they are in a non standard location
                        var vbsp = GetFullPath(Required(name => hdb[name], gamedb.Name, "BSP", "bsp"), binFolder);
                        var vbspPath = Path.GetDirectoryName(vbsp) ?? binFolder;

                        var bspzip = FindPath("bspzip.exe", binFolder, vbspPath);
                        var vbspinfo = FindPath("vbspinfo.exe", binFolder, vbspPath);
                        var vpk = FindPath("vpk.exe", binFolder, vbspPath);

                        // Only follow the compilers to their real location when we actually found
                        // bspzip and can name its folder. GetDirectoryName returns null for a bare
                        // filename, and assigning that to binFolder made every later Path.Combine
                        // throw ArgumentNullException instead of reporting the missing tool.
                        if (Path.GetDirectoryName(bspzip) is { Length: > 0 } compilerFolder
                            && compilerFolder != binFolder)
                        {
                            CompilePalLogger.LogLineDebug($"Bin folder \"{binFolder}\" differs from compiler location \"{compilerFolder}\"");
                            binFolder = compilerFolder;
                        }

                        GameConfiguration game = new GameConfiguration
                        {
                            Name = gamedb.Name.Replace("\"", ""),
                            BinFolder = binFolder,
                            GameFolder = GetFullPath(Required(name => gamedb[name], gamedb.Name, "GameDir", "gamedir"), binFolder),
                            GameEXE = GetFullPath(Required(name => hdb[name], gamedb.Name, "GameExe", "gamexe"), binFolder),
                            SDKMapFolder = GetFullPath(Required(name => hdb[name], gamedb.Name, "MapDir", "mapdir"), binFolder),
                            VBSP = vbsp,
                            VVIS = GetFullPath(Required(name => hdb[name], gamedb.Name, "Vis", "vis"), binFolder),
                            VRAD = GetFullPath(Required(name => hdb[name], gamedb.Name, "Light", "light"), binFolder),
                            MapFolder = GetFullPath(Required(name => hdb[name], gamedb.Name, "BSPDir", "bspdir"), binFolder),
                            BSPZip = bspzip,
                            VBSPInfo = vbspinfo,
                            VPK = vpk,
                        };

                        var cpdb = gamedb["CompilePal"];
                        if (cpdb is not null)
                        {
                            CompilePalLogger.LogLineDebug($"Found CompilePal GameInfo block");
                            // Optional: a CompilePal block may exist without naming a plugin folder.
                            var pluginFolder = cpdb["Plugins"]?.ToString();
                            if (!string.IsNullOrEmpty(pluginFolder))
                            {
                                game.PluginFolder = pluginFolder;
                            }
                        }

                        game.SteamAppID = GetSteamAppID(game);
                        gameInfos.Add(game);
                    }
                    catch (InvalidDataException ex)
                    {
                        // Raised by Required() and already says which game and which key. A
                        // gameconfig written for a mod, or hand-edited, routinely omits keys the
                        // stock one has - so this is a normal thing to hit and deserves a sentence
                        // rather than a stack trace.
                        CompilePalLogger.LogLineColor(ex.Message, Error.GetSeverityBrush(3));
                    }
                    catch (Exception ex)
                    {
                        CompilePalLogger.LogLine($"Failed to parse game configuration: {ex}");
                    }
                }
            }

            return gameInfos;
        }

        /// <summary>
        /// Reads a key that the configuration must contain, trying each spelling in turn.
        ///
        /// These blocks are written by Hammer, by Hammer++, by mod authors and by hand, and the
        /// casing varies between all of them - hence the alternatives. What they also do is omit
        /// keys: a gameconfig for a mod frequently has no BSPDir, and a hand-trimmed one can be
        /// missing almost anything.
        ///
        /// The previous form was `(block["Vis"] ?? block["vis"]).ToString()`, which throws a
        /// NullReferenceException when neither spelling is present. That was caught by the loop's
        /// handler and logged as a stack trace, so the visible symptom was a game quietly absent
        /// from the list and a log entry that did not say which key caused it. Naming the game and
        /// the key turns that into something a user can act on.
        /// </summary>
        private static string Required(Func<string, KVValue?> read, string gameName, params string[] names)
        {
            foreach (var name in names)
            {
                if (read(name) is { } value)
                    return value.ToString() ?? "";
            }

            throw new InvalidDataException(
                $"Game configuration for '{gameName}' is missing '{names[0]}', so it cannot be used. " +
                "Check the Hammer block in your gameconfig.");
        }

        private static string GetFullPath(string line, string gameInfoDir)
        {
            // Only relative paths need resolving. The second half of this condition used to read
            // `|| !line.StartsWith("")`, which is dead: every string starts with the empty string,
            // so that operand was always false and contributed nothing. Removed rather than
            // "fixed", because the behaviour it produced is the behaviour that is wanted.
            if (!line.StartsWith(".."))
                return line;

            return Path.GetFullPath(Path.Combine(gameInfoDir, line));
        }

        private static int? GetSteamAppID(GameConfiguration config)
        {
            if (!File.Exists(config.GameInfoPath)) return null;

            using (var gameInfoFile = File.OpenRead(config.GameInfoPath))
            {
                var gameInfo = KVSerializer.Deserialize(gameInfoFile);
                var appIDValue = gameInfo["FileSystem"]?["SteamAppId"];
                if (appIDValue is null)
                    return null;

                Int32.TryParse(appIDValue.ToString(), out int appID);
                return appID;
            }
        }

        
        private static string? FindPath(string program, string binFolder, string backupBinFolder)
        {
            var path = Path.Combine(binFolder, program);
            if (File.Exists(path))
            {
                return path;
            }

            // program does not exist in standard bin folder, fallback to trying to locate it by using a known executable
            CompilePalLogger.LogLineDebug($"{program} does not exist at \"{path}\", using known compiler location {backupBinFolder}");

            path = Path.Combine(backupBinFolder, program);
            return File.Exists(path) ? path : null;

        }
    }
}
