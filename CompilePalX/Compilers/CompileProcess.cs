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
    class CompileProcess
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

        /// <summary>
        /// Where in the overall compile the running step starts, and how much of it the step accounts
        /// for. Set by <c>CompilingManager</c> before each step so a step that knows its own progress
        /// can report it without having to work out the arithmetic - or duplicate the definition of a
        /// step's share, which only has one correct answer.
        /// </summary>
        private static double stepBase;
        private static double stepShare;

        internal static void BeginStepProgress(double start, double share)
        {
            stepBase = start;
            stepShare = share;
        }

        /// <summary>
        /// Reports progress from inside a step, as a fraction of that step.
        ///
        /// Held just below 1 on purpose: <see cref="ProgressManager.SetProgress"/> treats reaching 1 as
        /// the compile finishing and plays the completion sound, which the last step of a compile would
        /// otherwise trigger while it was still working.
        /// </summary>
        protected static void ReportStepProgress(double fraction)
        {
            if (stepShare <= 0)
                return;

            ProgressManager.SetProgress(Math.Min(stepBase + stepShare * Math.Clamp(fraction, 0, 1), 0.999));
        }
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
                    if (!parameter.IsCompatible)
                    {
                        CompilePalLogger.LogLineColor(
                            $"Skipping '{parameter.Name}' ({parameter.Parameter.Trim()}): not supported by " +
                            $"{GameConfigurationManager.GameConfiguration?.Name ?? "this game"}" +
                            (parameter.RequiresToolsPlusPlus ? " with the configured (non-tools++) compiler." : "."),
                            Error.GetSeverityBrush(1));
                        continue;
                    }

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
