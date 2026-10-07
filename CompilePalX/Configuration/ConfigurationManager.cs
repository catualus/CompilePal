using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CompilePalX.Compilers;
using CompilePalX.Compilers.BSPPack;
using CompilePalX.Compilers.UtilityProcess;
using CompilePalX.Compiling;
using CompilePalX.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CompilePalX
{
    [JsonObject(ItemNullValueHandling = NullValueHandling.Ignore)]
    public class PresetProcessParameter
    {
        public string Name { get; set; }
        public string? Value { get; set; }
        public string? Value2 { get; set; }
        public bool ReadOutput { get; set; }
        public bool WaitForExit { get; set; }
        public string? Order { get; set; }

        public PresetProcessParameter(ConfigItem config)
        {
            Name = config.Name;
            Value = config.Value;
            Value2 = config.Value2;
            ReadOutput = config.ReadOutput;
            WaitForExit = config.WaitForExit;
            Order = config.Warning; // HACK: old model uses Warning to store the order for CUSTOM PROGRAM step
        }

        [Newtonsoft.Json.JsonConstructor]
        public PresetProcessParameter(string name, string? value, string? value2, bool readOutput, bool waitForExit, string? order)
        {
            Name = name;
            Value = value;
            Value2 = value2;
            ReadOutput = readOutput;
            WaitForExit = waitForExit;
            Order = order;
        }
    }
    /// <remarks>
    /// WARNING: Preset is used as the key of <see cref="CompileProcess.PresetDictionary"/> while
    /// <see cref="GetHashCode"/> derives from the mutable Name/Map/MapRegex. Mutating any of those on a
    /// preset that is already a key orphans its entry. Rename via
    /// <see cref="ConfigurationManager.EditPreset"/>, which rebuilds the dictionaries from disk.
    /// </remarks>
    public class Preset : IEquatable<Preset>, ICloneable
    {
        public required string Name { get; set; }
        public string? Map { get; set; }
        public string? MapRegex { get; set; }
        public Dictionary<string, List<PresetProcessParameter>> Processes { get; set; } = [];

        public bool Equals(Preset? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Name == other.Name && MapRegex == other.MapRegex && Map == other.Map;
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((Preset)obj);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Name, MapRegex, Map);
        }
        public object Clone()
        {
            var clone = (Preset)this.MemberwiseClone();
            // MemberwiseClone is shallow: without this the clone shares the original's parameter lists,
            // so editing one preset silently rewrites the other.
            clone.Processes = CopyProcesses();
            return clone;
        }

        /// <summary>
        /// Deep copies the process -> parameter map so two presets never share parameter list instances.
        /// </summary>
        public Dictionary<string, List<PresetProcessParameter>> CopyProcesses()
        {
            return Processes.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        }

        /// <summary>
        /// Returns whether a map can use the preset
        /// </summary>
        /// <param name="mapName"></param>
        /// <returns></returns>
        public bool IsValidMap(string mapName)
        {
            // presets with no map are global
            return this.MapRegex == null || Regex.IsMatch(mapName, MapRegex);
        }
    }

    static class ConfigurationManager
    {
        public static ObservableCollection<CompileProcess> CompileProcesses = [];
        public static ObservableCollection<Preset> KnownPresets = [];
        public static Settings Settings = new Settings();

        public static Preset? CurrentPreset = null;

        /// <summary>
        /// The preset of the map being compiled right now, set by the compile loop for each map and
        /// cleared when the run ends. Null whenever no compile is running.
        ///
        /// Separate from <see cref="CurrentPreset"/> because that one belongs to the window: it is
        /// whatever the user is looking at, and the map queue stays clickable during a compile.
        /// The compile loop used to write each map's preset into CurrentPreset and every step read its
        /// arguments back out of it when it started - so clicking another queued map while VBSP ran
        /// handed that map's arguments to VVIS and VRAD. It also left CurrentPreset on the last map's
        /// preset after the run, while the preset box still showed the selected map's, and Edit
        /// Preset then deleted the preset that had been compiled last rather than the one on screen.
        /// </summary>
        public static volatile Preset? CompilingPreset = null;

        /// <summary>
        /// The preset a step takes its arguments from: the compiling map's during a compile, the one
        /// being edited otherwise.
        /// </summary>
        public static Preset? ActivePreset => CompilingPreset ?? CurrentPreset;

        /// <summary>
        /// The map selected in the queue, for the command each step shows in its expanded row. Null
        /// when nothing is selected, in which case the rows show the template with its placeholders.
        ///
        /// The map rather than its path, because the preview also needs the map's own preset: the
        /// compile loop sets <see cref="CurrentPreset"/> to each queued map's preset in turn and
        /// leaves it on the last one, so after a multi-map run CurrentPreset can belong to a map other
        /// than the one selected, and a preview built from it would show one map's path with another
        /// map's arguments.
        /// </summary>
        public static Map? PreviewMap = null;

        /// <summary>The preset the selected map compiles with, or the one being edited when no map is selected.</summary>
        public static Preset? PreviewPreset => PreviewMap?.Preset ?? CurrentPreset;

        private static readonly string ParametersFolder = "./Parameters";
        private static readonly string PresetsFolder = "./Presets";
        private static readonly string PluginFolder = "./Plugins";
        private static readonly string SettingsFile = "./Settings.json";

        #region Autosave

        // Presets used to be written only from MainWindow's Closing handler, so anything short of a clean
        // shutdown (crash, force kill, Environment.Exit) discarded every edit made that session. Edits are
        // now tracked and flushed shortly after they happen.
        private static readonly HashSet<Preset> DirtyPresets = [];
        private static Timer? autosaveTimer;
        private static bool processesDirty;

        /// <summary>
        /// Marks a preset as having unsaved changes and (re)starts the debounce timer.
        /// Safe to call on every keystroke.
        /// </summary>
        public static void MarkDirty(Preset? preset)
        {
            if (preset is null)
                return;

            lock (DirtyPresets)
            {
                DirtyPresets.Add(preset);
            }

            ScheduleFlush();
        }

        /// <summary>Marks compile process metadata (step order, DoRun, ...) as needing a write.</summary>
        public static void MarkProcessesDirty()
        {
            processesDirty = true;
            ScheduleFlush();
        }

        private static readonly object autosaveGate = new();

        /// <summary>
        /// A plain timer whose callback hands the write to the UI thread, where Flush has always run.
        /// This was a WPF DispatcherTimer, which put a WPF type in the configuration code for nothing
        /// more than a delay; see docs/native-port.md.
        /// </summary>
        private static void ScheduleFlush()
        {
            if (!UiThread.IsCaptured)
            {
                // no UI thread (unit tests, headless): write through immediately
                Flush();
                return;
            }

            var delay = TimeSpan.FromMilliseconds(Math.Max(50, Settings.AutosaveDelayMilliseconds));

            // restart the countdown so a burst of edits results in a single write
            lock (autosaveGate)
            {
                autosaveTimer ??= new Timer(_ => UiThread.Post(Flush));
                autosaveTimer.Change(delay, Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>
        /// Writes every pending change to disk immediately. Called by the debounce timer, before a compile
        /// starts, on shutdown, and from the crash handler.
        /// </summary>
        public static void Flush()
        {
            Preset[] pending;
            lock (DirtyPresets)
            {
                pending = DirtyPresets.ToArray();
                DirtyPresets.Clear();
            }

            foreach (var preset in pending)
            {
                try
                {
                    SavePreset(preset);
                }
                catch (Exception ex)
                {
                    CompilePalLogger.LogLineDebug($"Failed to autosave preset \"{preset.Name}\": {ex}");
                }
            }

            if (processesDirty)
            {
                processesDirty = false;
                try
                {
                    SaveProcesses();
                }
                catch (Exception ex)
                {
                    CompilePalLogger.LogLineDebug($"Failed to autosave process metadata: {ex}");
                }
            }
        }

        /// <summary>
        /// Watches a preset's parameter list so any add, remove, or in-place edit schedules a save.
        /// </summary>
        public static void TrackForAutosave(Preset preset, ObservableCollection<ConfigItem> parameters)
        {
            foreach (var item in parameters)
                Subscribe(item);

            parameters.CollectionChanged += (_, args) =>
            {
                foreach (var item in args.OldItems?.OfType<ConfigItem>() ?? [])
                    item.PropertyChanged -= OnItemChanged;

                foreach (var item in args.NewItems?.OfType<ConfigItem>() ?? [])
                    Subscribe(item);

                MarkDirty(preset);
            };

            void Subscribe(ConfigItem item)
            {
                item.PropertyChanged -= OnItemChanged;
                item.PropertyChanged += OnItemChanged;
            }

            void OnItemChanged(object? sender, PropertyChangedEventArgs args)
            {
                // What the compiler said its default is comes from the binary, is never saved, and is
                // rewritten on every refresh - not a reason to write the preset to disk.
                if (args.PropertyName is nameof(ConfigItem.ToolDefault)
                    or nameof(ConfigItem.IsCompatible)
                    or nameof(ConfigItem.IncompatibilityReason))
                    return;

                MarkDirty(preset);
            }
        }

        /// <summary>
        /// Serialises to a temporary file then swaps it into place, so an interrupted write can never
        /// leave a half-written meta.json behind.
        /// </summary>
        internal static void WriteFileAtomic(string path, string contents)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, contents);
            File.Move(tempPath, path, overwrite: true);
        }

        #endregion


        /// <summary>
        /// Constructs one built-in compile step, reporting rather than throwing if it cannot.
        ///
        /// The name is passed separately because it is needed for the message when the constructor
        /// is the thing that failed and there is no object to ask.
        /// </summary>
        private static void AddBuiltIn(string name, Func<CompileProcess> create)
        {
            try
            {
                CompileProcesses.Add(create());
            }
            catch (Exception e)
            {
                CompilePalLogger.LogLineColor(
                    $"Could not load the {name} step: {e.Message}", 3);
                CompilePalLogger.LogLineDebug(e.ToString());
            }
        }

        public static void AssembleParameters()
        {
            CompileProcesses.Clear();

            /*
             * The built-in steps, each one allowed to fail on its own.
             *
             * These construct by reading their own metadata and parameter files from disk, exactly
             * as the discovered ones below do - and those have been wrapped in a try/catch since
             * forever, so an unreadable VRAD folder logs "Failed to load Compile Process" and the
             * application starts without it.
             *
             * These were not wrapped, so the same unreadable file threw out of AssembleParameters,
             * out of the MainWindow constructor, and killed the application on startup. That is
             * what 1.0.1 did when Windows denied a read on CUBEMAPS/parameters.json: one file the
             * application does not need in order to run took the whole thing down.
             *
             * A missing step is a degraded application. A step that cannot load is not a reason to
             * have no application at all.
             */
            AddBuiltIn("PACK", () => new BSPPack());
            AddBuiltIn("VMFFIX", () => new VmfFixProcess());
            AddBuiltIn("CUBEMAPS", () => new CubemapProcess());
            AddBuiltIn("NAV", () => new NavProcess());
            AddBuiltIn("ENTLUMP", () => new EntityLumpProcess());
            AddBuiltIn("SHUTDOWN", () => new ShutdownProcess());
            AddBuiltIn("UTILITY", () => new UtilityProcess());
            AddBuiltIn("CUSTOM", () => new CustomProcess());

            //collect new metadatas
            var metadatas = Directory.GetDirectories(ParametersFolder).Concat(Directory.GetDirectories(PluginFolder)).ToArray();

            // load autodiscovered plugins
            if (GameConfigurationManager.GameConfiguration is not null && GameConfigurationManager.GameConfiguration.PluginFolder is not null)
            {
                CompilePalLogger.LogLineDebug($"Loading additional plugins from: {GameConfigurationManager.GameConfiguration.PluginFolder}");
                metadatas = metadatas.Concat(Directory.GetDirectories(GameConfigurationManager.GameConfiguration.PluginFolder)).ToArray();
            }

            foreach (var metadata in metadatas)
            {
                string folderName = Path.GetFileName(metadata);

                if (CompileProcesses.Any(c => String.Equals(c.Metadata.Name, folderName, StringComparison.CurrentCultureIgnoreCase)))
                    continue;

                string? parameterFolder = null;
                if (!String.Equals(Path.GetDirectoryName(metadata), ParametersFolder, StringComparison.CurrentCultureIgnoreCase))
                {
                    parameterFolder = Path.GetDirectoryName(metadata);
                }

                try
                {
                    var compileProcess = new CompileExecutable(folderName, parameterFolder);
                    CompileProcesses.Add(compileProcess);
                }
                catch (Exception ex)
                {
                    CompilePalLogger.LogLine($"Failed to load Compile Process: {metadata}, {ex}");
                }
            }

            //collect legacy metadatas
            var csvMetaDatas = Directory.GetFiles(ParametersFolder + "\\", "*.meta");

            foreach (var metadata in csvMetaDatas)
            {
                string name = Path.GetFileName(metadata).Replace(".meta", "");

                if (CompileProcesses.Any(c => String.Equals(c.Metadata.Name, name, StringComparison.CurrentCultureIgnoreCase)))
                    continue;

                try
                {
                    var compileProcess = new CompileExecutable(name);
                    CompileProcesses.Add(compileProcess);
                }
                catch (Exception ex)
                {
                    CompilePalLogger.LogLine($"Failed to load Compile Process: {metadata}, {ex}");
                }
            }

            // Before sorting, since Order is one of the answers being restored.
            ApplyStepState();

            CompileProcesses = new ObservableCollection<CompileProcess>(CompileProcesses.OrderBy(c => c.Metadata.Order));

            // Before the presets, which look their parameters up by name in these lists and so need
            // the discovered ones to be there already.
            RefreshDiscoveredParameters();

            AssemblePresets();
        }

        /// <summary>
        /// Adds to each compiler step whatever its binary reports that the shipped parameter list does
        /// not know about. Run after the parameter lists are built, and again whenever the binary
        /// that will run may have changed: a different game, a different tools++ folder, the
        /// override setting.
        /// </summary>
        public static void RefreshDiscoveredParameters()
        {
            foreach (var process in CompileProcesses)
                process.RefreshDiscoveredParameters();
        }

        private static void AssemblePresets()
        {
            if (!Directory.Exists(PresetsFolder))
                Directory.CreateDirectory(PresetsFolder);

            //get a list of presets from the directories in the preset folder
            var presets = Directory.GetDirectories(PresetsFolder);

            //clear old lists
            KnownPresets.Clear();

            foreach (var process in CompileProcesses)
            {
                process.PresetDictionary.Clear();
            }

            foreach (string presetPath in presets)
            {
                string presetName = Path.GetFileName(presetPath);

                // try reading preset metadata
                string metadataFile = Path.Combine(presetPath, "meta.json");

                Preset preset;
                if (File.Exists(metadataFile))
                {
                    /*
                     * Guarded, for the reason LoadSettings is. This runs from the MainWindow
                     * constructor, so one truncated or hand-mangled preset file threw out of
                     * AssembleParameters and the application died on launch - over a preset it does
                     * not need in order to run. The preset is skipped and left on disk untouched, so
                     * nothing in it is lost and fixing the file brings it back.
                     */
                    try
                    {
                        preset = JsonConvert.DeserializeObject<Preset>(File.ReadAllText(metadataFile)) ?? new Preset() { Name = presetName };
                    }
                    catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
                    {
                        CompilePalLogger.LogLineColor(
                            $"Skipped preset \"{presetName}\": its meta.json could not be read ({e.Message}).",
                            3);
                        continue;
                    }

                    // "Processes": null in the file overrides the initialiser.
                    preset.Processes ??= [];
                }
                else
                    // legacy presets don't have metadata, use folder name as preset name
                    preset = new Preset() { Name = presetName };

                // handle legacy CSV presets
                if (preset.Processes.Count == 0)
                {
                    foreach (var process in CompileProcesses)
                    {
                        string file = Path.Combine(presetPath, process.PresetFile);
                        if (File.Exists(file))
                        {
                            process.PresetDictionary.Add(preset, []);
                            //read the list of preset parameters
                            var lines = File.ReadAllLines(file);

                            foreach (var line in lines)
                            {
                                var item = ParsePresetLine(line);

                                if (process.ParameterList.Any(c => c.Parameter == item.Parameter))
                                {
                                    //remove .clone if you are a masochist and wish to enter the object oriented version of hell
                                    var equivalentItem = (ConfigItem)process.ParameterList.FirstOrDefault(c => c.Parameter == item.Parameter).Clone();

                                    equivalentItem.Value = item.Value;

                                    //Copy extra information stored for custom programs
                                    if (item.Parameter == "program")
                                    {
                                        equivalentItem.Value2 = item.Value2;
                                        equivalentItem.WaitForExit= item.WaitForExit;
                                        equivalentItem.Warning = item.Warning;
                                    }
                                    
                                    process.PresetDictionary[preset].Add(equivalentItem);
                                }
                            }

                            // convert to new preset parameter model
                            preset.Processes[process.Name] = process.PresetDictionary[preset].Select(config => new PresetProcessParameter(config)).ToList();
                        }
                    }
                } 
                else
                // handle json presets
                {
                    foreach ((var processName, var parameters) in preset.Processes)
                    {
                        var process = CompileProcesses.FirstOrDefault(p => p.Name == processName);
                        if (process is null)
                        {
                            CompilePalLogger.LogLine($"Failed to find process \"{processName}\" while loading presets");
                            continue;
                        }

                        process.PresetDictionary[preset] = [];
                        foreach (var parameter in parameters)
                        {
                            var configItem = process.ParameterList.FirstOrDefault(c => c.Name == parameter.Name);
                            if (configItem is null)
                            {
                                // A parameter the preset took from a compiler's own -help, saved under
                                // its flag. The compiler that listed it may not be the one configured
                                // now, so it is not in the list - but the preset still means it, and
                                // dropping it here would quietly change what the preset does the next
                                // time that compiler is back. Kept as it was written; IsCompatible
                                // decides per compile whether it is handed over.
                                configItem = ConfigItem.FromPresetFlag(process.CompilerName ?? processName, parameter.Name, parameter.Value);
                            }

                            if (configItem is null)
                            {
                                CompilePalLogger.LogLine($"Failed to find parameter \"{parameter.Name}\" while loading preset \"{processName}\"");
                                continue;
                            }

                            // TODO: this should be improved at some point. I dont think we need to store extra info such as Warnings, description, etc, for presets
                            //remove .clone if you are a masochist and wish to enter the object oriented version of hell
                            var equivalentItem = (ConfigItem)configItem.Clone();

                            equivalentItem.Value = parameter.Value;
                            equivalentItem.Value2 = parameter.Value2;
                            equivalentItem.ReadOutput = parameter.ReadOutput;
                            equivalentItem.WaitForExit = parameter.WaitForExit;
                            if (processName == "CUSTOM")
                            {
                                equivalentItem.Warning = parameter.Order;
                            }

                            process.PresetDictionary[preset].Add(equivalentItem);
                        }
                    }
                }

                CompilePalLogger.LogLine($"Added preset {preset.Name} {(preset.Map != null ? $"for map {preset.Map} " : "")}for processes {string.Join(", ", CompileProcesses)}");
                KnownPresets.Add(preset);

                // watch every parameter list belonging to this preset so edits schedule a save
                foreach (var process in CompileProcesses)
                {
                    if (process.PresetDictionary.TryGetValue(preset, out var parameters))
                        TrackForAutosave(preset, parameters);
                }
            }

            // Restore the preset that was selected last session. Previously CurrentPreset was assigned
            // inside the loop above, so it always ended up as whichever preset happened to load last.
            CurrentPreset = KnownPresets.FirstOrDefault(p => p.Name == Settings.LastPreset)
                            ?? KnownPresets.FirstOrDefault();
        }

        /// <summary>
        /// Reads settings.json, and never lets a bad one stop the application starting.
        ///
        /// This used to call DeserializeObject with no guard. That handles a file containing "null",
        /// which is what the null check below is for, but it throws on anything malformed - and this
        /// runs during startup, before a window exists. The result was an application that died on
        /// launch with no way back except finding and deleting the file by hand.
        ///
        /// The file is rewritten in full on every close, so a crash, a power cut or a full disk
        /// part-way through that write leaves exactly the truncated JSON that triggers it. It is not
        /// a hypothetical.
        ///
        /// Same treatment as mapfiles.json in PersistenceManager: keep the unreadable file rather
        /// than discarding whatever was in it, say so, and carry on with defaults.
        /// </summary>
        public static void LoadSettings()
        {
            if (!File.Exists(SettingsFile))
            {
                CompilePalLogger.LogLine("No settings file found, falling back to default");
                return;
            }

            try
            {
                var settings = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(SettingsFile));
                if (settings is null)
                {
                    CompilePalLogger.LogLine("Failed to load settings, falling back to default");
                    return;
                }

                Settings = settings;
            }
            catch (Exception e)
            {
                string backup = SettingsFile + ".corrupt";
                bool moved = false;

                try
                {
                    File.Move(SettingsFile, backup, overwrite: true);
                    moved = true;
                }
                catch (Exception moveFailure)
                {
                    CompilePalLogger.LogLineDebug($"Could not rename the unreadable settings file: {moveFailure}");
                }

                /*
                 * Logged, not reported through ExceptionHandler.
                 *
                 * ExceptionHandler.LogException shows the crash dialog even when told the fault is
                 * not fatal, and "Compile Pal has stopped" is simply untrue here: the file was set
                 * aside, the defaults are in place and the application is about to start normally.
                 * Telling somebody their application crashed, and then having it not crash, teaches
                 * them to dismiss the dialog that matters.
                 */
                CompilePalLogger.LogLineDebug($"Could not read {SettingsFile}: {e}");

                // Only claim the rename happened if it did, so nobody goes looking for a file that
                // is not there while the real one still sits under its original name.
                CompilePalLogger.LogLineColor(
                    "Could not read your settings; starting with the defaults. " +
                    (moved
                        ? $"The unreadable file was kept as {backup}."
                        : $"The unreadable file could not be renamed and is still at {SettingsFile}."),
                    3);
            }
        }

        public static void SavePresets()
        {
            foreach (var knownPreset in KnownPresets)
            {
                SavePreset(knownPreset);
            }
        }

        public static void SavePreset(Preset preset)
        {
            // Rebuild from scratch rather than only refreshing processes still present in the dictionary.
            // Updating in place left entries for processes the user had removed, so deleted steps and
            // parameters came back on the next launch.
            var rebuilt = new Dictionary<string, List<PresetProcessParameter>>();
            foreach (var compileProcess in CompileProcesses)
            {
                if (compileProcess.PresetDictionary.TryGetValue(preset, out var parameters))
                    rebuilt[compileProcess.Name] = parameters.Select(config => new PresetProcessParameter(config)).ToList();
            }

            // keep entries for processes that failed to load this session so their config is not destroyed
            foreach (var (processName, parameters) in preset.Processes)
            {
                if (!rebuilt.ContainsKey(processName) && CompileProcesses.All(p => p.Name != processName))
                    rebuilt[processName] = parameters;
            }

            preset.Processes = rebuilt;

            // save preset metadata
            string presetFolder = GetPresetFolder(preset);
            string metadataPath = Path.Combine(presetFolder, "meta.json");
            string jsonSaveText = JsonConvert.SerializeObject(preset, Formatting.Indented);

            WriteFileAtomic(metadataPath, jsonSaveText);
        }

        /// <summary>One step's answers that belong to the user rather than to whoever wrote the step.</summary>
        public sealed record StepState(bool DoRun, float Order);

        /// <summary>
        /// Whether each step runs and where it runs in the order, written by Compile Pal into its own
        /// folder rather than into the step's.
        ///
        /// These used to be written back into each step's meta.json - a plugin's own file, in the
        /// plugin's own folder. Three things followed from that. Updating a plugin replaced the file
        /// and with it the user's tick, so the step switched itself off after every update. A plugin
        /// folder that is read-only - installed system-wide, or linked to a build output - could not
        /// hold the tick at all, and the failed write was only ever logged in debug output. And the
        /// path was rebuilt from the meta.json's Name rather than the folder the plugin was loaded
        /// from, so a plugin unpacked under any other folder name had its tick written into a new
        /// stray folder, never read back, and found at the next start as a broken plugin.
        ///
        /// A step's meta.json now only ever supplies the defaults: what a step does the first time it
        /// is seen. Existing users keep what they had, because the values already written into those
        /// files are read as the defaults until this file has an answer of its own.
        /// </summary>
        private static readonly string StepStateFile = "./StepState.json";

        private static Dictionary<string, StepState> LoadStepState()
        {
            if (!File.Exists(StepStateFile))
                return new(StringComparer.OrdinalIgnoreCase);

            try
            {
                var read = JsonConvert.DeserializeObject<Dictionary<string, StepState>>(File.ReadAllText(StepStateFile));
                return new(read ?? [], StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                // The defaults from each step's meta.json stand in, which is a working application with
                // some ticks reset - not a reason to fail to start.
                CompilePalLogger.LogLineColor(
                    $"Could not read {StepStateFile}, so steps start as their defaults: {e.Message}",
                    3);
                return new(StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>Applies the saved answers over each step's defaults. Run before the order is sorted.</summary>
        private static void ApplyStepState()
        {
            var saved = LoadStepState();

            foreach (var process in CompileProcesses)
            {
                if (process.Metadata?.Name is not { } name || !saved.TryGetValue(name, out var state))
                    continue;

                process.Metadata.DoRun = state.DoRun;
                process.Metadata.Order = state.Order;
            }
        }

        /// <summary>
        /// Writes whether each step runs and where it runs in the order. See <see cref="StepStateFile"/>
        /// for why that is Compile Pal's own file and not the step's meta.json.
        /// </summary>
        public static void SaveProcesses()
        {
            // Starts from what is on disk so a step that failed to load this session - a plugin that is
            // temporarily missing - keeps its answer rather than losing it.
            var state = LoadStepState();

            foreach (var process in CompileProcesses)
            {
                if (process.Metadata?.Name is { } name)
                    state[name] = new StepState(process.Metadata.DoRun, process.Metadata.Order);
            }

            WriteFileAtomic(StepStateFile, JsonConvert.SerializeObject(
                new SortedDictionary<string, StepState>(state, StringComparer.OrdinalIgnoreCase), Formatting.Indented));
        }

        /// <summary>Raised after a settings save so open windows can apply changes without a restart.</summary>
        public static event Action? OnSettingsSaved;

        public static void SaveSettings(Settings settings)
        {
            // Compared before Settings is replaced. The error catalogue only has to be re-fetched when
            // the source it comes from actually changed - and forcing it otherwise was expensive in a
            // way that was easy to miss: SaveSettings also runs on window close, to persist the map list
            // height and the last preset, so every shutdown spent one request against a source that
            // rate limits to about five of them. After a handful of launches the fetch only ever
            // returned 403 and error descriptions stopped resolving.
            bool sourceChanged = !string.Equals(Settings?.ErrorSourceURL, settings.ErrorSourceURL,
                StringComparison.Ordinal);

            WriteFileAtomic(SettingsFile, JsonConvert.SerializeObject(settings, Formatting.Indented));
            Settings = settings;

            if (sourceChanged)
                ErrorFinder.Init(true);

            OnSettingsSaved?.Invoke();
        }
        public static void SaveSettings()
        {
            SaveSettings(Settings);
        }

        public static Preset? NewPreset(Preset preset, bool initializeDefaultProcesses = true)
        {
            if (PresetProblem(preset) is { } problem)
            {
                CompilePalLogger.LogLineColor($"Preset not created: {problem}", 3);
                return null;
            }

            if (initializeDefaultProcesses)
            {
                string[] defaultProcesses = new string[] { "VBSP", "VVIS", "VRAD", "COPY", "GAME" };
                preset.Processes = defaultProcesses.ToDictionary(key => key, key => new List<PresetProcessParameter>());
            }

            // if map specific, append map to name so you can make map specific presets with the same name as global ones
            string folder = GetPresetFolder(preset);

            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);

                SavePreset(preset);
            }

            AssembleParameters();
            return preset;
        }
        /// <summary>
        /// Copies <paramref name="source"/>'s steps into the new <paramref name="preset"/>. Takes the
        /// source rather than reading <see cref="CurrentPreset"/>, for the reason EditPreset does.
        /// </summary>
        public static Preset? ClonePreset(Preset source, Preset preset)
        {
            if (PresetProblem(preset) is { } problem)
            {
                CompilePalLogger.LogLineColor($"Preset not cloned: {problem}", 3);
                return null;
            }

            // if map specific, append map to name so you can make map specific presets with the same name as global ones
            string newFolder = GetPresetFolder(preset);

            // deep copy: sharing the dictionary made edits to the clone rewrite the source preset
            preset.Processes = source.CopyProcesses();

            if (!Directory.Exists(newFolder))
            {
                Directory.CreateDirectory(newFolder);
                SavePreset(preset);
                AssembleParameters();
            }

            return preset;
        }

        /// <summary>
        /// Replaces <paramref name="original"/> with <paramref name="edited"/>.
        ///
        /// Takes the preset being edited rather than reading <see cref="CurrentPreset"/>. The dialog is
        /// opened on the preset in the preset box, and CurrentPreset is not guaranteed to be that one:
        /// when they disagreed, this deleted whichever preset CurrentPreset happened to hold and
        /// recreated it under the new name, leaving the one being edited untouched.
        ///
        /// Refuses a name that is already taken. Deleting first and then finding the new folder
        /// already there - which NewPreset treats as "nothing to save" - lost the preset outright.
        /// </summary>
        public static Preset? EditPreset(Preset original, Preset edited)
        {
            if (PresetProblem(edited, replacing: original) is { } problem)
            {
                CompilePalLogger.LogLineColor($"Preset not changed: {problem}", 3);
                return null;
            }

            // copy processes
            edited.Processes = original.CopyProcesses();

            // TODO: this can be improved, deleting and recreating isn't really neccessary now that all preset info is consolidated into one file
            // "Edit" preset by deleting the preset and adding a new one, then make it the currently selected preset
            RemovePreset(original);
            var newPreset = NewPreset(edited, false);

            CurrentPreset = newPreset;

            return newPreset;
        }

        private static string GetPresetFolder(Preset preset)
        {
            return preset.Map != null ? Path.Combine(PresetsFolder, $"{preset.Name}_{preset.Map}") : Path.Combine(PresetsFolder, preset.Name);
        }
        /// <summary>
        /// Whether a preset's folder is a folder directly inside Presets, as opposed to Presets itself
        /// or somewhere outside it.
        ///
        /// The folder is built from the preset's name, and the name comes from whatever is in its
        /// meta.json - including a shared one. A name of ".." resolved to the Compile Pal folder, and
        /// removing that preset deleted the whole installation recursively.
        /// </summary>
        private static bool IsOwnPresetFolder(string folder)
        {
            string root = Path.GetFullPath(PresetsFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// What is wrong with a preset's name, map or match pattern, or null if nothing is.
        ///
        /// A name becomes a folder, so it has to be one: not empty (that is Presets itself, which
        /// always exists, so the preset silently failed to save), no path characters, not "." or
        /// "..", and not a folder another preset already has. <paramref name="replacing"/> is the
        /// preset being edited, whose own folder does not count as taken. The match pattern is
        /// checked too, because an invalid one throws from IsValidMap every time the preset list is
        /// filtered.
        /// </summary>
        public static string? PresetProblem(Preset preset, Preset? replacing = null)
        {
            if (string.IsNullOrWhiteSpace(preset.Name))
                return "A preset needs a name.";

            var invalid = Path.GetInvalidFileNameChars();

            foreach (var (label, value) in new[] { ("name", preset.Name), ("map filter name", preset.Map) })
            {
                if (value is null)
                    continue;

                if (value.Trim() is "." or ".." || value.IndexOfAny(invalid) >= 0 || value != value.Trim())
                    return $"The {label} \"{value}\" cannot be used as a folder name.";
            }

            if (preset.MapRegex is not null)
            {
                try
                {
                    _ = new Regex(preset.MapRegex);
                }
                catch (ArgumentException e)
                {
                    return $"The match pattern is not a valid regular expression: {e.Message}";
                }
            }

            string folder = GetPresetFolder(preset);

            if (!IsOwnPresetFolder(folder))
                return "That name does not make a folder inside Presets.";

            bool isOwnFolder = replacing is not null &&
                               string.Equals(Path.GetFullPath(folder), Path.GetFullPath(GetPresetFolder(replacing)),
                                   StringComparison.OrdinalIgnoreCase);

            if (Directory.Exists(folder) && !isOwnFolder)
                return $"A preset called \"{preset.Name}\"{(preset.Map != null ? $" for {preset.Map}" : "")} already exists.";

            return null;
        }

        public static void RemovePreset(Preset preset)
        {
            string folder = GetPresetFolder(preset);

            if (!IsOwnPresetFolder(folder))
            {
                CompilePalLogger.LogLineColor(
                    $"Not deleting preset \"{preset.Name}\": its folder would be {Path.GetFullPath(folder)}, which is not inside Presets.",
                    4);
                return;
            }

            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }


            AssembleParameters();
        }
        public static void RemoveProcess(string name)
        {
            if (CurrentPreset is null)
                return;

            CurrentPreset.Processes.Remove(name);
            // make sure value is also removed from preset in master preset list
            KnownPresets.FirstOrDefault(p => p.Name == CurrentPreset.Name)?.Processes.Remove(name);
            SavePreset(CurrentPreset);
        }


        /// <param name="compilerName">
        /// Which of the four compilers the step runs, when it runs one - the name the parameters are
        /// checked against, which is not always the step's own: REPACK runs bspzip.
        /// </param>
        public static ObservableCollection<ConfigItem> GetParameters(string processName, bool external = false, string? parameterFolder = null, string? compilerName = null)
        {
            var list = new ObservableCollection<ConfigItem>();

            if ( parameterFolder is null)
            {
                parameterFolder = ParametersFolder;
            }

            string jsonParameters = Path.Combine(parameterFolder, processName, "parameters.json");

            if (File.Exists(jsonParameters))
            {
                ConfigItem[] items = JsonConvert.DeserializeObject<ConfigItem[]>(File.ReadAllText(jsonParameters));
                foreach (var configItem in items)
                {
                    // lets IsCompatible resolve which compiler binary to test for tools++ support
                    configItem.OwningProcess = compilerName ?? processName;
                    list.Add(configItem);
                }

                // add custom parameter to all external programs
                if (external)
                {
                    list.Add(new ConfigItem()
                    {
                        Name = "Command Line Argument",
                        CanHaveValue = true,
                        CanBeUsedMoreThanOnce = true,
                        Description = "Passes value as a command line argument",
                        OwningProcess = compilerName ?? processName,
                    });
                }
            }
            else
            {
                string csvParameters = Path.Combine(parameterFolder, processName + ".csv");

                if (File.Exists(csvParameters))
                {
                    var baselines = File.ReadAllLines(csvParameters);

                    for (int i = 2; i < baselines.Length; i++)
                    {
                        string baseline = baselines[i];

                        var item = ParseBaseLine(baseline);
                        item.OwningProcess = compilerName ?? processName;

                        list.Add(item);
                    }

                    ConfigItem[] items = list.ToArray();

                    File.WriteAllText(jsonParameters, JsonConvert.SerializeObject(items, Formatting.Indented));
                }
                else
                {
                    throw new FileNotFoundException("Parameter files could not be found for " + processName);
                }
            }


            return list;
        }

        private static ConfigItem ParsePresetLine(string line)
        {
            var item = new ConfigItem();

            // Split on commas unless they are escaped with a backslash
            var pieces = Regex.Split(line, "(?<!\\\\),");

            if (pieces.Any())
            {
                // Custom parameter stores name as first value instead of parameter, because it has no parameter
                if (pieces[0] == "Command Line Argument")
                    item.Name = pieces[0];
                else
                    item.Parameter = pieces[0];

                if (pieces.Count() >= 2)
                    item.Value = pieces[1].Replace("\\,", ",");
				//Handle extra information stored for custom programs
	            if (pieces.Count() >= 3)
		            item.Value2 = pieces[2].Replace("\\,", ",");
	            if (pieces.Length >= 4)
		            item.ReadOutput = Convert.ToBoolean(pieces[3]);
	            if (pieces.Length >= 5)
					item.WaitForExit= Convert.ToBoolean(pieces[4]);
	            if (pieces.Length >= 6)
		            item.Warning = pieces[5];
            }
            return item;
        }

		private static string WritePresetLine(ConfigItem item)
        {
			// Handle extra information stored for custom programs
	        if (item.Name == "Run Program")
		        return $"{item.Parameter},{item.Value.Replace(",", "\\,")},{item.Value2.Replace(",", "\\,")},{item.ReadOutput},{item.WaitForExit},{item.Warning}";
            else if (item.Name == "Command Line Argument") // Command line arguments have no parameter value
                return $"{item.Name},{item.Value.Replace(",", "\\,")}";
            return $"{item.Parameter},{item.Value?.Replace(",", "\\,")}";
        }

        private static ConfigItem ParseBaseLine(string line)
        {
            var item = new ConfigItem();

            var pieces = line.Split(';');

            if (pieces.Any())
            {
                item.Name = pieces[0];
                if (pieces.Count() >= 2)
                    item.Parameter = pieces[1];
                if (pieces.Count() >= 3)
                    item.CanHaveValue = bool.Parse(pieces[2]);
                if (pieces.Count() >= 4)
                    item.Description = pieces[3];
                if (pieces.Count() >= 5)
                    item.Warning = pieces[4];
            }
            return item;
        }

        private static void DirectoryCopy(string sourceDirName, string destDirName, bool copySubDirs)
        {
            // Get the subdirectories for the specified directory.
            DirectoryInfo dir = new DirectoryInfo(sourceDirName);
            DirectoryInfo[] dirs = dir.GetDirectories();

            if (!dir.Exists)
            {
                throw new DirectoryNotFoundException(
                    "Source directory does not exist or could not be found: "
                    + sourceDirName);
            }

            // If the destination directory doesn't exist, create it. 
            if (!Directory.Exists(destDirName))
            {
                Directory.CreateDirectory(destDirName);
            }

            // Get the files in the directory and copy them to the new location.
            FileInfo[] files = dir.GetFiles();
            foreach (FileInfo file in files)
            {
                string temppath = Path.Combine(destDirName, file.Name);
                file.CopyTo(temppath, false);
            }

            // If copying subdirectories, copy them and their contents to new location. 
            if (copySubDirs)
            {
                foreach (DirectoryInfo subdir in dirs)
                {
                    string temppath = Path.Combine(destDirName, subdir.Name);
                    DirectoryCopy(subdir.FullName, temppath, copySubDirs);
                }
            }
        }
    }
}
