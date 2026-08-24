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

            ParameterList = ConfigurationManager.GetParameters(Metadata.Name, Metadata.IsExternal, this.ParameterFolder);
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

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Re-reads everything the SETUP row shows. Called when a parameter is added, removed or edited.
        /// </summary>
        public void NotifyParametersChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ArgumentSummary)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasParameters)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentPresetParameters)));
        }

        #endregion


        /// <summary>
        /// Parameters this preset carries that the configured compiler will not accept, with the
        /// reason. Empty when everything applies.
        /// </summary>
        public IEnumerable<(string Name, string Flag, bool ToolsPlusPlus)> IncompatibleParameters()
        {
            if (ConfigurationManager.CurrentPreset is not { } preset || !PresetDictionary.ContainsKey(preset))
                yield break;

            foreach (var parameter in PresetDictionary[preset])
                if (!parameter.IsCompatible)
                    yield return (parameter.Name, parameter.Parameter.Trim(), parameter.RequiresToolsPlusPlus);
        }

        public string GetParameterString()
        {
            string parameters = Metadata.Arguments;

            if (ConfigurationManager.CurrentPreset != null)
                foreach (var parameter in PresetDictionary[ConfigurationManager.CurrentPreset])
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
