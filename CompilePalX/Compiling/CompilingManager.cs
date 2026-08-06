using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using CompilePalX.Compilers;
using CompilePalX.Compiling;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents.Serialization;
using CompilePalX.Annotations;
using CompilePalX.Configuration;
using Newtonsoft.Json;

namespace CompilePalX
{
    internal delegate void CompileCleared();
    internal delegate void CompileStarted();
    internal delegate void CompileFinished();

    public class Map : INotifyPropertyChanged
    {
        private string file;

        public string File
        {
            get => file;
            set { file = value; OnPropertyChanged(nameof(File));  }
        }

        public string FullMapName => Path.GetFileNameWithoutExtension(file);
        /// <summary>
        /// Map name without version identifiers
        /// </summary>
        // try removing version identifier
        public string MapName  => Regex.Replace(FullMapName, @"((_[^_]+\d)|(_rc)|(_final))$", "");

        public bool IsBSP => Path.GetExtension(file) == ".bsp";

        private bool compile;
        public bool Compile 
        {
            get => compile;
            set { compile = value; OnPropertyChanged(nameof(Compile));  }
        }

        private Preset? preset;
        public Preset? Preset
        {
            get => preset;
            set { preset = value; OnPropertyChanged(nameof(Preset));  }
        }

        public Map(string file, bool compile = true, Preset? preset = null)
        {
            File = file;
            Compile = compile;
            Preset = preset;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    static class CompilingManager
    {
        static CompilingManager()
        {
            CompilePalLogger.OnErrorFound += CompilePalLogger_OnErrorFound;
        }
            
        private static void CompilePalLogger_OnErrorFound(Error e)
        {
            currentCompileProcess.CompileErrors.Add(e);

            if (e.Severity == 5 && IsCompiling)
            {
                //We're currently in the thread we would like to kill, so make sure we invoke from the window thread to do this.
                MainWindow.ActiveDispatcher.Invoke(() =>
                {
                    CompilePalLogger.LogLineColor("An error cancelled the compile.", Error.GetSeverityBrush(5));
                    CancelCompile();
                    ProgressManager.ErrorProgress();
                });
            }
        }

        public static event CompileCleared OnClear;
        public static event CompileFinished OnStart;
        public static event CompileFinished OnFinish;

        public static TrulyObservableCollection<Map> MapFiles = [];

        private static Stopwatch compileTimeStopwatch = new Stopwatch();

        public static bool IsCompiling { get; private set; }
        private static CancellationTokenSource cts;
        private static Task compileTask = Task.CompletedTask;

        public static void ToggleCompileState()
        {
            if (IsCompiling)
                CancelCompile();
            else
                StartCompile();
        }

        public static void StartCompile()
        {
            // Cancel used to only request cancellation and immediately report the compile as stopped,
            // without confirming the background task actually observed it - it could still be running
            // (nav visibility tracing has no cancellation check of its own inside a single pass). That
            // let this run concurrently with a freshly-started compile, both writing to the same log.
            // Now that cancellation is threaded down into every parallel pass, this should return almost
            // immediately unless a cancel just landed - but still worth waiting for rather than assuming.
            try { compileTask.Wait(); } catch { /* the previous run's own exception, already handled there */ }

            OnStart();

            // Tells windows to not go to sleep during compile
            NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED);

            AnalyticsManager.Compile();

            IsCompiling = true;

            compileTimeStopwatch.Start();

            OnClear();

            cts = new CancellationTokenSource();
            compileTask = Task.Run(() => CompileThreaded(cts.Token));
        }

        private static CompileProcess currentCompileProcess;

        /// <summary>
        /// Width of a step divider, in characters. Wide enough to read as a rule across the output at a
        /// typical window size without wrapping at a narrow one.
        /// </summary>
        private const int DividerWidth = 64;

        /// <summary>
        /// Writes a labelled rule before a compile step.
        ///
        /// Steps otherwise run straight into each other: vbsp, vvis and vrad all emit dense output of
        /// their own with no consistent header, so finding where one ended and the next began meant
        /// recognising the tools' own banners. The step number also makes it obvious at a glance how far
        /// through the compile the output is.
        /// </summary>
        private static void LogStepDivider(string name, int step, int total)
        {
            string label = $" {name} ({step}/{total}) ";
            int remaining = Math.Max(0, DividerWidth - label.Length);
            int left = remaining / 2;

            CompilePalLogger.LogLine();
            CompilePalLogger.LogLineColor(
                new string('─', left) + label + new string('─', remaining - left),
                Error.GetSeverityBrush(1));
        }

        private static void CompileThreaded(CancellationToken cancellationToken)
        {
            try
            {
                ProgressManager.SetProgress(0);

                var mapErrors = new List<MapErrors>();


                foreach (Map map in MapFiles)
                {
                    if (!map.Compile)
                    {
                        CompilePalLogger.LogDebug($"Skipping {map.File}");
                        continue;
                    }

                    string mapFile = map.File; 
                    string cleanMapName = Path.GetFileNameWithoutExtension(mapFile);
                    ConfigurationManager.CurrentPreset = map.Preset;

                    var compileErrors = new List<Error>();
                    CompilePalLogger.LogLine($"Starting a '{ConfigurationManager.CurrentPreset?.Name}' compile for {GameConfigurationManager.GameConfiguration.Name}.");
                    CompilePalLogger.LogLine($"Starting compilation of {cleanMapName}");
                    CompilePalLogger.LogLineDebug($"Map path: {mapFile}");

					//Update the grid so we have the most up to date order
	                OrderManager.UpdateOrder();

	                // Say so rather than reporting a successful zero-second compile, which is what this
	                // looked like before: no steps enabled is a configuration mistake, not a result.
	                if (OrderManager.CurrentOrder.Count == 0)
	                {
		                // Name both halves of the filter. "Nothing ran" has two quite different causes -
		                // no step ticked, or the preset not carrying the ticked steps - and they need
		                // opposite fixes.
		                var enabled = ConfigurationManager.CompileProcesses
			                .Where(c => c.Metadata.DoRun).Select(c => c.Name).ToList();
		                var inPreset = ConfigurationManager.CurrentPreset is null
			                ? new List<string>()
			                : ConfigurationManager.CompileProcesses
				                .Where(c => c.PresetDictionary.ContainsKey(ConfigurationManager.CurrentPreset))
				                .Select(c => c.Name).ToList();

		                CompilePalLogger.LogLineColor(
			                $"No compile steps will run for preset '{ConfigurationManager.CurrentPreset?.Name}'.",
			                Error.GetSeverityBrush(3));
		                CompilePalLogger.LogLine(
			                $"  enabled steps: {(enabled.Count == 0 ? "(none)" : string.Join(", ", enabled))}");
		                CompilePalLogger.LogLine(
			                $"  steps this preset knows about: {(inPreset.Count == 0 ? "(none)" : string.Join(", ", inPreset))}");
	                }

                    GameConfigurationManager.BackupCurrentContext();
                    var buildContext = GameConfigurationManager.BuildContext(map);
					int stepNumber = 0;
					foreach (var compileProcess in OrderManager.CurrentOrder)
					{
                        cancellationToken.ThrowIfCancellationRequested();
                        currentCompileProcess = compileProcess;

                        LogStepDivider(compileProcess.Name, ++stepNumber, OrderManager.CurrentOrder.Count);

                        // Hand the step its own slice of the bar, so one that can report its internal
                        // progress does not have to sit at whatever the previous step left behind.
                        CompileProcess.BeginStepProgress(ProgressManager.Progress, StepShare());

                        compileProcess.Run(buildContext, cancellationToken);

                        compileErrors.AddRange(currentCompileProcess.CompileErrors);

                        //Portal 2 cannot work with leaks, stop compiling if we do get a leak.
                        if (GameConfigurationManager.GameConfiguration.Name == "Portal 2")
                        {
                            if (currentCompileProcess.Name == "VBSP" && currentCompileProcess.CompileErrors.Count > 0)
                            {
                                //we have a VBSP error, aka a leak -> stop compiling;
                                break;
                            }
                        }

                        ProgressManager.Progress += StepShare();

                        // log empty line to make a space inbetween compile step logs
                        CompilePalLogger.LogLine();
                    }

                    mapErrors.Add(new MapErrors { MapName = cleanMapName, Errors = compileErrors });

                    CompilePalLogger.LogLineFileLocation($"Compiled Map: {buildContext.CopyLocation}\n", buildContext.CopyLocation);
                    GameConfigurationManager.RestoreCurrentContext();
                }

                if (!cancellationToken.IsCancellationRequested)
                    MainWindow.ActiveDispatcher.Invoke(() => postCompile(mapErrors));
            }
            catch (OperationCanceledException) { ProgressManager.ErrorProgress(); }
        }

        /// <summary>
        /// How much of the whole compile a single step accounts for.
        ///
        /// One definition, used both to advance the bar when a step finishes and to give a step the
        /// slice it may report inside. Guarded against zero because a preset with no steps enabled
        /// reaches here before the check that reports it.
        /// </summary>
        private static double StepShare()
        {
            int steps = ConfigurationManager.CompileProcesses.Count(c => c.Metadata.DoRun &&
                c.PresetDictionary.ContainsKey(ConfigurationManager.CurrentPreset));

            return 1d / Math.Max(1, steps) / Math.Max(1, MapFiles.Count);
        }

        private static void postCompile(List<MapErrors> errors, bool cancelled = false)
        {
            // Cancelling still ran this: it's the only place that resets IsCompiling/the progress bar and
            // fires OnFinish, so the UI can leave the "compiling" state. But it must not claim success -
            // this used to log a green "compile finished" line unconditionally, directly under "Compile
            // forcefully ended.", telling the user a killed compile had completed normally.
            if (cancelled)
            {
                CompilePalLogger.LogLineColor(
                    $"'{ConfigurationManager.CurrentPreset!.Name}' compile cancelled after {compileTimeStopwatch.Elapsed.ToString(@"hh\:mm\:ss")}. The map was not fully compiled.",
                    (Brush) Application.Current.TryFindResource("CompilePal.Brushes.Severity4"));
            }
            else
            {
                CompilePalLogger.LogLineColor(
                    $"'{ConfigurationManager.CurrentPreset!.Name}' compile finished in {compileTimeStopwatch.Elapsed.ToString(@"hh\:mm\:ss")}", (Brush) Application.Current.TryFindResource("CompilePal.Brushes.Success"));
            }

            if (errors != null && errors.Any())
            {
                int numErrors = errors.Sum(e => e.Errors.Count);
                int maxSeverity = errors.Max(e => e.Errors.Any() ? e.Errors.Max(e2 => e2.Severity) : 0);
                CompilePalLogger.LogLineColor("{0} errors/warnings logged:", Error.GetSeverityBrush(maxSeverity), numErrors);

                foreach (var map in errors)
                {
                    CompilePalLogger.Log("  ");

                    if (!map.Errors.Any())
                    {
                        CompilePalLogger.LogLineColor("No errors/warnings logged for {0}", Error.GetSeverityBrush(0), map.MapName);
                        continue;
                    }

                    int mapMaxSeverity = map.Errors.Max(e => e.Severity);
                    CompilePalLogger.LogLineColor("{0} errors/warnings logged for {1}:", Error.GetSeverityBrush(mapMaxSeverity), map.Errors.Count, map.MapName);

                    var distinctErrors = map.Errors.GroupBy(e => e.ID).OrderBy(e => e.First().Severity);
                    foreach (var errorList in distinctErrors)
                    {
                        var error = errorList.First();

                        string errorText = $"{errorList.Count()}x: {error.SeverityText}: {error.ShortDescription}";

                        CompilePalLogger.Log("    ● ");
                        CompilePalLogger.LogCompileError(errorText, error);
                        CompilePalLogger.LogLine();

                        if (error.Severity >= 3)
                            AnalyticsManager.CompileError();
                    }
                }
            }

            OnFinish();

            compileTimeStopwatch.Reset();

            IsCompiling = false;

            // Tells windows it's now okay to enter sleep
            NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);
        }

        public static void CancelCompile()
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
            }
            IsCompiling = false;

            ProgressManager.SetProgress(0);

            CompilePalLogger.LogLineColor("Compile forcefully ended.", (Brush) Application.Current.TryFindResource("CompilePal.Brushes.Severity4"));

            postCompile(null, cancelled: true);
        }

        public static Stopwatch GetTime()
        {
            return compileTimeStopwatch;
        }

        class MapErrors
        {
            public string MapName { get; set; }
            public List<Error> Errors { get; set; }
        }

        internal static class NativeMethods
        {
            // Import SetThreadExecutionState Win32 API and necessary flags
            [DllImport("kernel32.dll")]
            public static extern uint SetThreadExecutionState(uint esFlags);
            public const uint ES_CONTINUOUS = 0x80000000;
            public const uint ES_SYSTEM_REQUIRED = 0x00000001;
        }
    }
}
