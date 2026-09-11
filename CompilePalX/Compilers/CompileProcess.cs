using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CompilePalX.Annotations;
using CompilePalX.Compiling;
using Newtonsoft.Json;

namespace CompilePalX
{
    class CompileProcess : INotifyPropertyChanged
    {
        public string ParameterFolder = "./Parameters";
	    public bool Draggable = true; // set to false if we ever want to disable reordering non custom compile steps
        public List<Error> CompileErrors;

        public CompileProcess(string name, string? parameterFolder = null)
        {
            if (parameterFolder is not null)
                this.ParameterFolder = parameterFolder;

            string jsonMetadata = Path.Combine(ParameterFolder, name, "meta.json");

            if (File.Exists(jsonMetadata))
            {
                Metadata = JsonConvert.DeserializeObject<CompileMetadata>(File.ReadAllText(jsonMetadata));

                CompilePalLogger.LogLine("Loaded JSON metadata {0} from {1} at order {2}", Metadata.Name, jsonMetadata, Metadata.Order);
            }
            else
            {
                string legacyMetadata = Path.Combine(ParameterFolder, name + ".meta");

                if (File.Exists(legacyMetadata))
                {
                    Metadata = LoadLegacyData(legacyMetadata);

                    Directory.CreateDirectory(Path.Combine(ParameterFolder, name));

                    File.WriteAllText(jsonMetadata, JsonConvert.SerializeObject(Metadata, Formatting.Indented));

                    CompilePalLogger.LogLine("Loaded CSV metadata {0} from {1} at order {2}, converted to JSON successfully.", Metadata.Name, legacyMetadata, Metadata.Order);
                }
                else
                {
                    throw new FileNotFoundException("The metadata file for " + name + " could not be found.");
                }

            }

            ParameterList = ConfigurationManager.GetParameters(Metadata.Name, Metadata.IsExternal, this.ParameterFolder, CompilerName);
        }

        public static CompileMetadata LoadLegacyData(string csvFile)
        {
            CompileMetadata metadata = new CompileMetadata();

            var lines = File.ReadAllLines(csvFile);

            metadata.Name = lines[0];
            metadata.Path = lines[1];
            metadata.BasisString = lines[3];
            metadata.Order = float.Parse(lines[4], CultureInfo.InvariantCulture);
            metadata.DoRun = bool.Parse(lines[5]);
            metadata.ReadOutput = bool.Parse(lines[6]);
            if (lines.Count() > 7)
                metadata.Warning = lines[7];
            if (lines.Count() > 8)
                metadata.Description = lines[8];

            return metadata;
        }

        public CompileMetadata Metadata;

        public string PresetFile { get { return Metadata.Name + ".csv"; } }

        public double Ordering { get { return Metadata.Order; } }
        public bool DoRun { get { return Metadata.DoRun; } set { Metadata.DoRun = value; } }
        public string Name { get { return Metadata.Name; } }
        public string Description { get { return Metadata.Description; } }
        public string Warning { get { return Metadata.Warning; } }
		public bool IsDraggable { get { return Draggable; } }
		[UsedImplicitly] public bool SupportsBSP => Metadata.SupportsBSP;

        /// <summary>Whether this step brings its own setup window. Shows or hides the button.</summary>
        [UsedImplicitly] public bool HasConfigure => !string.IsNullOrWhiteSpace(Metadata.Configure);

        [UsedImplicitly] public string ConfigureLabel =>
            string.IsNullOrWhiteSpace(Metadata.ConfigureLabel) ? "Configure" : Metadata.ConfigureLabel!;

        [UsedImplicitly]
        public bool IsCompatible
        {
            get
            {
                // current game configuration has no SteamAppID
                if (GameConfigurationManager.GameConfiguration != null && GameConfigurationManager.GameConfiguration.SteamAppID == null)
                    return true;

                int currentAppID = (int)GameConfigurationManager.GameConfiguration!.SteamAppID!;

                // supported game ID list should take precedence. If defined, check that current GameConfiguration SteamID is in whitelist
                if (Metadata.CompatibleGames != null)
                    return Metadata.CompatibleGames.Contains(currentAppID);

                // If defined, check that current GameConfiguration SteamID is not in blacklist
                if (Metadata.IncompatibleGames != null)
                    return !Metadata.IncompatibleGames.Contains(currentAppID);

                // process does not define which games are supported
                return true;
            }
        }

        public Process? Process;

        public virtual bool CanRun(CompileContext context)
        {
            if (context.Map.IsBSP && !SupportsBSP)
            {
                CompilePalLogger.LogLineDebug($"Map is BSP, skipping process {Name}");
                return false;
            }
            return true;
        }
        public virtual void Run(CompileContext context, CancellationToken cancellationToken)
        {

        }

        /*
         * A per-step progress hook lived here: BeginStepProgress stored where the running step
         * started and how much of the bar it owned, and ReportStepProgress let a step report its
         * own internal progress against that.
         *
         * Nothing ever called ReportStepProgress. The scaffolding was in place, the arithmetic was
         * right, and no compile step ever reported anything - so the bar only ever moved when a
         * step ended, and stood still through the whole of VVIS and VRAD.
         *
         * The footer now interpolates between step boundaries from how long the step has taken on
         * previous runs (see MainWindow.UpdateEstimates), which needs nothing from the steps
         * themselves. If a step is ever taught to report real progress - the Source tools do print
         * "0...1...2..." lines that could drive it - it should override that interpolation rather
         * than sit alongside it, and this is where that would go.
         */

        public virtual void Cancel()
        {
            if (Process is null || Process.Id == 0 || Process.HasExited)
                return;

            Process.Kill();
            CompilePalLogger.LogLineColor("\nKilled {0}.", (Brush) Application.Current.TryFindResource("CompilePal.Brushes.Severity4"), this.Metadata.Name);
        }

        public ObservableCollection<ConfigItem> ParameterList = [];
        public bool SupportsCustomParameters { get => ParameterList.Any(i => i.Name == "Command Line Argument"); }
        public ObservableDictionary<Preset, ObservableCollection<ConfigItem>> PresetDictionary = [];

        #region Stepper bindings

        /// <summary>
        /// This step's parameters under the preset being edited, or null when the preset does not carry
        /// this step at all.
        ///
        /// The SETUP list gives every step its own parameter grid, expanded underneath the step it
        /// belongs to, rather than one shared grid in a third column whose connection to the middle
        /// column had to be inferred.
        /// </summary>
        public ObservableCollection<ConfigItem>? CurrentPresetParameters =>
            ConfigurationManager.CurrentPreset is { } preset && PresetDictionary.ContainsKey(preset)
                ? PresetDictionary[preset]
                : null;

        /// <summary>
        /// The step's arguments on one line, for the collapsed row.
        ///
        /// Lets the whole preset be read at a glance without opening every step in turn - which was the
        /// only way to answer "how does Best differ from Best (tools++)".
        /// </summary>
        public string ArgumentSummary
        {
            get
            {
                try
                {
                    string summary = GetParameterString().Trim();

                    // GetParameterString leads with the program's own base arguments, which are the same
                    // for every preset and so say nothing about this one.
                    string baseArguments = (Metadata?.Arguments ?? "").Trim();
                    if (baseArguments.Length != 0 && summary.StartsWith(baseArguments, StringComparison.Ordinal))
                        summary = summary[baseArguments.Length..].Trim();

                    return summary;
                }
                catch
                {
                    // Only ever decoration on a row; a step whose arguments cannot be resolved yet (no
                    // preset selected, a parameter mid-edit) should render blank, not throw into layout.
                    return "";
                }
            }
        }

        /// <summary>Whether this step has any parameters set, so the row can say "no parameters".</summary>
        public bool HasParameters => CurrentPresetParameters is { Count: > 0 };

        /// <summary>
        /// Which of the four compilers this step runs, by the placeholder its path names, or null for
        /// every other step. REPACK and BSPZIP both run bspzip; STATS runs vbspinfo, which is not one
        /// of the tools that gets replaced or asked anything.
        /// </summary>
        public string? CompilerName => CompilerFor(Metadata?.Path);

        /// <summary>
        /// The compiler a step's path placeholder names, or null when it is not one of the four.
        /// Looked up by placeholder rather than step name because two steps can run the same tool:
        /// REPACK and BSPZIP are both bspzip, and a parameter marked as needing tools++ under REPACK
        /// was never offered at all while the lookup went by the step's own name.
        /// </summary>
        public static string? CompilerFor(string? path) => (path ?? "").Trim() switch
        {
            "$vbsp$" => "VBSP",
            "$vvis$" => "VVIS",
            "$vrad$" => "VRAD",
            "$bspZip$" => "BSPZIP",
            _ => null,
        };

        /// <summary>
        /// The compiler this step will actually run, as a few words for the row: "vrad++ (Sep 10 2026)
        /// · GPU", "stock vrad.exe", "not configured". Null for steps that run no compiler.
        ///
        /// This is the question the whole tools++ machinery exists to answer, and until now the only
        /// place it was answered was the debug log. Whether the compile about to start uses tools++,
        /// and whether VRAD is about to light on the GPU, should not need a log file.
        /// </summary>
        public string? CompilerBadge
        {
            get
            {
                try
                {
                    if (ToolsPlusPlusDetector.Describe(CompilerName) is not { } info)
                        return null;

                    if (info.Path is null)
                        return "not configured";

                    string label = info.Help?.Label ?? (info.ToolsPlusPlus ? "tools++" : "stock " + Path.GetFileName(info.Path));

                    // vrad++ builds that know -cpu light on the GPU unless told otherwise
                    if (CompilerName == "VRAD" && info.Help is { } help && help.Has("-cpu"))
                        label += UsesFlag("-cpu") ? " · CPU" : " · GPU";

                    return label;
                }
                catch
                {
                    // decoration on a row, never a reason for the row to fail to render
                    return null;
                }
            }
        }

        /// <summary>The badge's tooltip: the full path, and how much the compiler said about itself.</summary>
        public string? CompilerBadgeDetail
        {
            get
            {
                try
                {
                    if (ToolsPlusPlusDetector.Describe(CompilerName) is not { } info)
                        return null;

                    if (info.Path is null)
                        return "No compiler is configured for this step in the game configuration.";

                    string detail = info.Path;
                    if (info.Help is { } help)
                        detail += $"{Environment.NewLine}{help.Banner}{Environment.NewLine}Reports {help.Options.Count} options; the ones Compile Pal's own list does not describe are offered under their flag.";
                    else if (info.ToolsPlusPlus)
                        detail += $"{Environment.NewLine}A tools++ build, not yet asked what it accepts.";
                    else
                        detail += $"{Environment.NewLine}A stock compiler. It does not list its options, so the shipped parameter list is used.";

                    return detail;
                }
                catch
                {
                    return null;
                }
            }
        }

        public bool HasCompilerBadge => CompilerBadge is not null;

        /// <summary>Whether the previewed preset hands this step <paramref name="flag"/> and the compiler would take it.</summary>
        private bool UsesFlag(string flag) =>
            ConfigurationManager.PreviewPreset is { } preset
            && PresetDictionary.TryGetValue(preset, out var parameters)
            && parameters.Any(p => string.Equals(p.Flag, flag, StringComparison.OrdinalIgnoreCase) && p.IsCompatible);

        /// <summary>
        /// The command this step would run for the map selected in the queue, with every placeholder
        /// filled in: the real compiler path, the real map, the real game folder. Falls back to the
        /// template when no map is selected or nothing is configured.
        ///
        /// The row's summary keeps the placeholders on purpose - it is the preset that is being read
        /// there, and a preset applies to any map. The expanded step is where someone goes to see
        /// what is about to happen, and "-game $game$ $vmfFile$" is not that.
        /// </summary>
        public string CommandPreview
        {
            get
            {
                try
                {
                    // The selected map's own preset, not the one being edited: the two can differ
                    // after a multi-map compile, and this is a statement about that map.
                    string template = GetParameterString(ConfigurationManager.PreviewPreset).Trim();
                    string? map = ConfigurationManager.PreviewMap?.File;

                    if (map is null || GameConfigurationManager.GameConfiguration is null)
                        return template;

                    string arguments = GameConfigurationManager.SubstituteValues(template, map).Trim();

                    if (Metadata is null || !Metadata.IsExternal || string.IsNullOrWhiteSpace(Metadata.Path))
                        return arguments;

                    string program = GameConfigurationManager.SubstituteValues(Metadata.Path, map, quote: false);

                    // always quoted, so a path with spaces and one without read the same way
                    if (!program.StartsWith('"'))
                        program = $"\"{program}\"";

                    return arguments.Length == 0 ? program : $"{program} {arguments}";
                }
                catch
                {
                    return "";
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Re-reads everything the SETUP row shows. Called when a parameter is added, removed or edited.
        /// </summary>
        public void NotifyParametersChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ArgumentSummary)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasParameters)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentPresetParameters)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompilerBadge)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompilerBadgeDetail)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCompilerBadge)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CommandPreview)));
        }

        #endregion


        /// <summary>
        /// Parameters this preset carries that the configured compiler will not accept, with the
        /// reason. Empty when everything applies.
        /// </summary>
        public IEnumerable<(string Name, string Flag, string Reason)> IncompatibleParameters()
        {
            if (ConfigurationManager.CurrentPreset is not { } preset || !PresetDictionary.ContainsKey(preset))
                yield break;

            foreach (var parameter in PresetDictionary[preset])
                if (parameter.IncompatibilityReason is { } reason)
                    yield return (parameter.Name, parameter.Parameter.Trim(), reason);
        }

        /// <summary>
        /// Brings the parameter list in line with what the compiler for this step reports.
        ///
        /// The shipped parameters.json is the curated layer: a readable name, a warning, a fixed set
        /// of values, a file picker. The compiler's own <c>-help</c> is the complete one. Anything the
        /// compiler lists that the file does not is added here under its flag, so a new option is
        /// available the day the tools ship it rather than the day someone edits a JSON file - and
        /// anything the file lists that the compiler does not is hidden by <see cref="ConfigItem.IsCompatible"/>.
        ///
        /// Also fills in <see cref="ConfigItem.ToolDefault"/> on the curated entries, since the
        /// default is the thing most likely to differ between builds.
        ///
        /// Steps that are not one of the four compilers, or whose compiler prints no table, are left
        /// exactly as loaded.
        /// </summary>
        public void RefreshDiscoveredParameters()
        {
            foreach (var stale in ParameterList.Where(p => p.FromToolHelp).ToList())
                ParameterList.Remove(stale);

            var help = ToolsPlusPlusDetector.HelpFor(CompilerName);

            string? DefaultFor(ConfigItem parameter) =>
                help is not null && help.TryGet(parameter.Flag, out var known) && known.TakesValue ? known.Default : null;

            foreach (var parameter in ParameterList)
                parameter.ToolDefault = DefaultFor(parameter);

            // The rows on screen are the presets' clones, taken before the compiler answered - so
            // they have to be told too, or the default appears on the picker's copy and never on the
            // row it was wanted for.
            foreach (var parameters in PresetDictionary.Values)
                foreach (var parameter in parameters)
                {
                    parameter.ToolDefault = DefaultFor(parameter);
                    parameter.NotifyAvailabilityChanged();
                }

            if (help is null)
                return;

            var curated = new HashSet<string>(
                ParameterList.Select(p => p.Flag).Where(f => f.Length > 0),
                StringComparer.OrdinalIgnoreCase);

            int added = 0;
            foreach (var option in help.Options)
            {
                if (!curated.Add(option.Flag))
                    continue;

                if (NeverOffered.Contains(option.Flag))
                    continue;

                // bspzip's -extract, -dir, -addfile and the rest are commands that name the BSP
                // themselves; the step already supplies the file, and they are not options to
                // combine with -repack. An option whose default mentions the bsp file is one of these.
                if (option.Default.Contains("<bspfile>", StringComparison.OrdinalIgnoreCase))
                    continue;

                ParameterList.Add(ConfigItem.FromToolOption(CompilerName!, option));
                added++;
            }

            if (added > 0)
                CompilePalLogger.LogLineDebug($"{Name}: {added} parameter(s) added from {help.Label} that the shipped list does not describe.");
        }

        /// <summary>
        /// Options a compiler lists that make no sense as a preset parameter: the ones Compile Pal
        /// already supplies from the game configuration, the ones that only mean something at a
        /// keyboard, and the one-letter alias of a switch that is already offered in full.
        /// </summary>
        private static readonly HashSet<string> NeverOffered = new(StringComparer.OrdinalIgnoreCase)
        {
            "-game", "-vproject", "-basedir", "-StopOnExit", "-v",
        };

        public string GetParameterString() => GetParameterString(ConfigurationManager.CurrentPreset);

        /// <summary>
        /// The arguments this step would be given under one particular preset.
        ///
        /// The parameterless version reads whichever preset is being edited, which is the right
        /// answer for the row the user is looking at and the wrong one for a queue where each map
        /// carries its own preset. Anything asking "what will happen to that map" has to name the
        /// preset rather than inherit it.
        /// </summary>
        public string GetParameterString(Preset? preset)
        {
            string parameters = Metadata.Arguments;

            if (preset != null && PresetDictionary.ContainsKey(preset))
                foreach (var parameter in PresetDictionary[preset])
                {
                    // A preset can outlive the game it was built for, and the parameter adder is the
                    // only thing that was consulting IsCompatible - so a preset saved against one game
                    // handed every one of its arguments to whatever compiler ran next. That is how a
                    // Garry's Mod compile ended up passing -StaticPropLightingFinal and
                    // -StaticPropBounce, both marked CS:GO-only, and having ficool2's VRAD reject them.
                    //
                    // Reported by ReportIncompatibleParameters, NOT from here. This method is a pure
                    // query on a hot path - ArgumentSummary binds it to a row, and BSPPack calls it
                    // twenty times in a row to test for individual flags - so logging here produced one
                    // line per WPF binding refresh. A single skipped parameter filled the output with
                    // dozens of identical warnings before the compile had even started.
                    if (!parameter.IsCompatible)
                        continue;

                    parameters += parameter.Parameter;

                    if (parameter.CanHaveValue && !string.IsNullOrEmpty(parameter.Value))
                    {
                        //Handle additional parameters in CUSTOM process
                        if (parameter.Name == "Run Program")
                        {
                            //Add args
                            parameters += " " + parameter.Value;

                            //Read Ouput
                            if (parameter.ReadOutput)
                                parameters += " " + parameter.ReadOutput;
                        }
                        else
                            // protect filepaths in quotes, since they can contain -
                        if (parameter.ValueIsFile || parameter.Value2IsFile)
                            parameters += $" \"{parameter.Value}\"";
                        else
                            parameters += " " + parameter.Value;
                    }
                }

            parameters += Metadata.BasisString;

            return parameters;
        }

        public override string ToString()
        {
            return Metadata.Name;
        }
    }

    class CompileMetadata
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public bool IsExternal { get => Path != "builtin"; }
        public string Arguments { get; set; } = String.Empty;
        public float Order { get; set; }

        public bool DoRun { get; set; }
        public bool ReadOutput { get; set; }

        public string Description { get; set; }
        public string Warning { get; set; }
        public bool PresetDefault { get; set; } = false;
        public bool CheckExitCode { get; set; } = true;
        public string BasisString { get; set; }
        public bool SupportsBSP { get; set; } = false;
        public HashSet<int>? IncompatibleGames { get; set; }
        public HashSet<int>? CompatibleGames { get; set; }
        public string? WorkingDirectory { get; set; }

        /// <summary>
        /// A program this step can open to be set up, run from a button on the step's row.
        ///
        /// Some steps need more than a list of flags. A step that publishes to a Workshop needs to be
        /// told which item, and the honest way to choose one is a window that lists them - not a
        /// number typed into a parameter, where a mistyped digit is a different person's map.
        ///
        /// Compile Pal knows nothing about what the program does. It runs it, waits, and re-reads
        /// whatever the step reads at compile time, which keeps everything the plugin understands
        /// inside the plugin. Templated the same way <see cref="Path"/> is, so it can be handed the
        /// map and the game's folders.
        /// </summary>
        public string? Configure { get; set; }

        /// <summary>What the button says. "Configure" if the step does not care.</summary>
        public string? ConfigureLabel { get; set; }

        /// <summary>
        /// A program that says what this step makes of one queued map, run before any compile starts.
        ///
        /// Templated like <see cref="Path"/> and given the map. It must print one line of JSON -
        /// label, detail, severity, confirm - and nothing else. Compile Pal shows the label on the
        /// map's card, offers the detail before a run the step asks to confirm, and refuses to start
        /// at all on a severity of "blocking".
        ///
        /// The point is timing. A step only speaks once it is running, which is after everything it
        /// might have wanted to warn about has already been compiled.
        /// </summary>
        public string? MapStatus { get; set; }
    }

    class CompileContext
    {
        public string MapFile;
        public Map Map;
        public GameConfiguration Configuration;
        public string BSPFile;
        public string CopyLocation;
    }
}
