using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows.Documents;
using CompilePalX.Compiling;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CompilePalX
{
    static class PersistenceManager
    {
        private static string mapFiles = "mapfiles.json";
        public static void Init()
        {
            if (File.Exists(mapFiles))
            {
                // A corrupt map list must not be fatal. This file is rewritten on every change, so a
                // crash or a full disk mid-write can leave it truncated - and because it is read during
                // startup, the failure took the whole application down before a window ever appeared,
                // with no way back except finding and deleting the file by hand.
                try
                {
                    var list = JsonConvert.DeserializeObject<List<object>>(File.ReadAllText(mapFiles))
                               ?? [];
                    var mapList = new List<Map>();

                    // make this backwards compatible by allowing plain string values in maplist array (old format)
                    foreach (var item in list)
                    {
                        if (item is string mapFile)
                            mapList.Add(new Map(mapFile));
                        else if (item is JObject obj)
                            mapList.Add(obj.ToObject<Map>());
                        else
                            CompilePalLogger.LogDebug($"Failed to load item from mapfiles: {item}");
                    }

                    CompilingManager.MapFiles = new TrulyObservableCollection<Map>(mapList);
                }
                catch (Exception e)
                {
                    // keep the bad file rather than silently discarding whatever was in it
                    string backup = mapFiles + ".corrupt";
                    try
                    {
                        File.Move(mapFiles, backup, overwrite: true);
                    }
                    catch (Exception moveFailure)
                    {
                        ExceptionHandler.LogException(moveFailure, false);
                    }

                    ExceptionHandler.LogException(e, false);
                    CompilePalLogger.LogLineColor(
                        $"Could not read the map list; starting with an empty one. The unreadable file " +
                        $"was kept as {backup}.", Error.GetSeverityBrush(3));
                }
            }

            CompilingManager.MapFiles.CollectionChanged +=
                delegate
                {
                    File.WriteAllText(mapFiles, JsonConvert.SerializeObject(CompilingManager.MapFiles,Formatting.Indented));
                };
        }
    }
}
