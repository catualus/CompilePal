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

    /// <summary>
    /// Which step of which map a compile is currently on, reported so the footer can say something
    /// more useful than a percentage.
    /// </summary>
    internal delegate void CompileStepChanged(CompileStepInfo info);

    /// <summary>What the footer shows while a compile runs.</summary>
    internal class CompileStepInfo
    {
        public string StepName { get; init; } = "";
        public int StepNumber { get; init; }
        public int StepCount { get; init; }
        public int MapNumber { get; init; }
        public int MapCount { get; init; }

        /// <summary>Names of every step of this map, in order, for the segmented progress bar.</summary>
        public IReadOnlyList<string> StepNames { get; init; } = [];

        /// <summary>Each step's share of this map's compile, in the same order as <see cref="StepNames"/>.</summary>
        public IReadOnlyList<double> StepWeights { get; init; } = [];

        /// <summary>Best guess at how much longer the whole run will take, or null with no history.</summary>
        public TimeSpan? Remaining { get; init; }
    }

    /// <summary>How a map's most recent compile turned out. Shown as a chip on its queue card.</summary>
    public enum MapCompileState
    {
        /// <summary>Never compiled this session, or the queue was changed since.</summary>
        None,
        Queued,
        Running,
        Succeeded,
        Failed,
        Cancelled,
    }

    public class Map : INotifyPropertyChanged
    {
        private string file;

        public string File
        {
            get => file;
            set
            {
                file = value;
                OnPropertyChanged(nameof(File));
                // Both are computed from File, so they do not raise changes of their own.
                OnPropertyChanged(nameof(FileName));
                OnPropertyChanged(nameof(DirectoryDisplay));
            }
        }

        /// <summary>File name with extension - what the map list leads with.</summary>
        public string FileName => Path.GetFileName(file);

        /// <summary>
        /// The map's folder, shortened for display.
        ///
        /// The list used to show the whole absolute path, which meant the map name - the only part
        /// anyone actually reads - sat at the far right and was the first thing to be trimmed away when
        /// the window narrowed. Keeping the last two segments is enough to tell two same-named maps in
        /// different folders apart; the full path is still on the row's tooltip.
        /// </summary>
        public string DirectoryDisplay
        {
            get
            {
                var directory = Path.GetDirectoryName(file);
                if (string.IsNullOrEmpty(directory))
                    return string.Empty;

                var segments = directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Where(s => s.Length != 0).ToList();

                if (segments.Count <= 2)
                    return directory;

                return "…" + Path.DirectorySeparatorChar +
                       string.Join(Path.DirectorySeparatorChar, segments.Skip(segments.Count - 2));
            }
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

        #region Last compile result

        // Deliberately not serialized: the queue file is written on every change, and a result from a
        // previous session says nothing about the file as it stands now.

        private MapCompileState state = MapCompileState.None;

        /// <summary>
        /// How this map's last compile went.
        ///
        /// Before this the queue looked identical before and after a run, so "which of the eight maps
        /// failed" could only be answered by reading back through the log.
        /// </summary>
        [JsonIgnore]
        public MapCompileState State
        {
            get => state;
            set { state = value; OnPropertyChanged(nameof(State)); OnPropertyChanged(nameof(StatusLine)); }
        }

        private TimeSpan? lastDuration;
        [JsonIgnore]
        public TimeSpan? LastDuration
        {
            get => lastDuration;
            set { lastDuration = value; OnPropertyChanged(nameof(LastDuration)); OnPropertyChanged(nameof(StatusLine)); }
        }

        private int lastWarningCount;
        [JsonIgnore]
        public int LastWarningCount
        {
            get => lastWarningCount;
            set { lastWarningCount = value; OnPropertyChanged(nameof(LastWarningCount)); OnPropertyChanged(nameof(StatusLine)); }
        }

        private int lastErrorCount;
        [JsonIgnore]
        public int LastErrorCount
        {
            get => lastErrorCount;
            set { lastErrorCount = value; OnPropertyChanged(nameof(LastErrorCount)); OnPropertyChanged(nameof(StatusLine)); }
        }

        /// <summary>
        /// One line summarising the last run, or empty when there has not been one.
        ///
        /// Built here rather than in a converter so the card template stays a single binding, and so the
        /// wording is in one place.
        /// </summary>
        [JsonIgnore]
        public string StatusLine
        {
            get
            {
                switch (State)
                {
                    case MapCompileState.Queued:
                        return "queued";
                    case MapCompileState.Running:
                        return "compiling…";
                    case MapCompileState.None:
                        return "";
                }

                var parts = new List<string>();

                if (LastDuration is { } duration)
                    parts.Add(duration.ToString(duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss"));

                if (LastErrorCount > 0)
                    parts.Add($"{LastErrorCount} error{(LastErrorCount == 1 ? "" : "s")}");

                if (LastWarningCount > 0)
                    parts.Add($"{LastWarningCount} warning{(LastWarningCount == 1 ? "" : "s")}");

                if (State == MapCompileState.Cancelled)
                    parts.Insert(0, "cancelled");
                else if (State == MapCompileState.Failed && LastErrorCount == 0)
                    parts.Insert(0, "failed");

                return string.Join(" · ", parts);
            }
        }

        #endregion

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
            // Null before the first step of a run starts, and between runs. Errors can be logged there -
            // a failure building the compile context, or the summary that postCompile prints - and this
            // used to throw a NullReferenceException out of a logging call.
            currentCompileProcess?.CompileErrors.Add(e);

            if (e.Severity == 5 && IsCompiling)
            {
                //We're currently in the thread we would like to kill, so make sure we invoke from the window thread to do this.
                MainWindow.ActiveDispatcher.Invoke(() =>
                {
                    CompilePalLogger.LogLineColor("An error cancelled the compile.", Error.GetSeverityBrush(5));
                    CancelCompile();
                });
            }
        }

        public static event CompileCleared OnClear;
        public static event CompileFinished OnStart;
        public static event CompileFinished OnFinish;

        /// <summary>Raised as each step begins, so the footer can name what is running.</summary>
        internal static event CompileStepChanged? OnStepChanged;

        public static TrulyObservableCollection<Map> MapFiles = [];

        private static Stopwatch compileTimeStopwatch = new Stopwatch();

        public static bool IsCompiling { get; private set; }
        private static CancellationTokenSource cts;
        private static Task compileTask = Task.CompletedTask;

        public static async Task ToggleCompileState()
        {
            if (IsCompiling)
                CancelCompile();
            else
                await StartCompile();
        }

        public static async Task StartCompile()
        {
            // Cancel used to only request cancellation and immediately report the compile as stopped,
            // without confirming the background task actually observed it - it could still be running
            // (nav visibility tracing has no cancellation check of its own inside a single pass). That
            // let this run concurrently with a freshly-started compile, both writing to the same log.
            // Now that cancellation is threaded down into every parallel pass, this should return almost
            // immediately unless a cancel just landed - but still worth waiting for rather than assuming.
            //
            // Awaited rather than blocked on: this runs on the UI thread (straight off the button click),
            // and a synchronous .Wait() here froze the window for however long the previous run's
            // external process took to die. Awaiting yields the thread instead while still guaranteeing
            // the two runs never overlap.
            try { await compileTask; } catch { /* the previous run's own exception, already handled there */ }

            OnStart();

            // Tells windows to not go to sleep during compile
            NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED);

            TelemetryManager.Compile();

            IsCompiling = true;

            compileTimeStopwatch.Start();

            OnClear();

            // After OnClear, which empties the output document, and before the compile thread can write
            // a single line. The logger holds the tail of the previous run in process-wide buffers -
            // clearing the document alone left those in place, so the new output opened with whatever
            // the last compile was midway through printing when it ended. See ResetOutputState.
            CompilePalLogger.ResetOutputState();

            // Stale between runs otherwise: it still names the last step of the previous compile, and
            // an error recognised before the first step of this one starts would be filed against it.
            currentCompileProcess = null;

            cts = new CancellationTokenSource();
            compileTask = Task.Run(() => CompileThreaded(cts.Token));
        }

        /// <summary>
        /// Applies a change to a queued map on the UI thread.
        ///
        /// MapFiles is a TrulyObservableCollection bound to the queue, so setting any property on a map
        /// raises a collection change - and WPF refuses one raised from a thread other than the one that
        /// owns the view. Doing it directly from the compile thread threw NotSupportedException out of
        /// the first statement of CompileThreaded, before a single line had been logged, and the Task's
        /// exception went unobserved: the compile looked like it had started - the button read Cancel and
        /// the elapsed timer ran - while producing no output whatsoever.
        ///
        /// Unlike OrderManager.CurrentOrder this collection cannot simply be registered with
        /// EnableCollectionSynchronization, because PersistenceManager replaces the instance when it
        /// restores a saved queue, so any registration made at startup would be against a dead object.
        /// </summary>
        private static void UpdateMapOnUiThread(Map map, Action<Map> change)
        {
            var dispatcher = MainWindow.ActiveDispatcher;

            if (dispatcher == null || dispatcher.CheckAccess())
                change(map);
            else
                dispatcher.Invoke(() => change(map));
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

                // Snapshot, for the same reason as the step order below: this loop runs for the whole
                // compile, and anything that adds to or removes from the queue meanwhile would
                // invalidate its enumerator and abort the run. It also fixes what a run means - the maps
                // that were queued when Compile was pressed, not whatever the list holds by the end.
                var maps = MapFiles.ToList();

                // Everything the queue shows about a previous run is about to be replaced, and leaving
                // the old chips up while the new run is under way reads as though those are its results.
                var queued = maps.Where(m => m.Compile).ToList();
                foreach (var m in maps)
                    UpdateMapOnUiThread(m, x => x.State = x.Compile ? MapCompileState.Queued : MapCompileState.None);

                int mapNumber = 0;

                foreach (Map map in maps)
                {
                    if (!map.Compile)
                    {
                        CompilePalLogger.LogDebug($"Skipping {map.File}");
                        continue;
                    }

                    mapNumber++;

                    string mapFile = map.File;
                    string cleanMapName = Path.GetFileNameWithoutExtension(mapFile);
                    ConfigurationManager.CurrentPreset = map.Preset;

                    UpdateMapOnUiThread(map, x => x.State = MapCompileState.Running);
                    var mapStopwatch = Stopwatch.StartNew();

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

                    // Worked out once per map: the weights come from how long each step took on previous
                    // runs, so they only change when a run finishes, not while one is in progress.
                    var stepShares = StepShares(map, maps);

                    // The footer's segmented bar draws one segment per step at its own width, so it needs
                    // the order as a list rather than the name-keyed shares (a name can repeat).
                    // Snapshot. OrderManager.UpdateOrder clears and refills CurrentOrder, and anything
                    // that calls it - selecting the ORDER tab, for one - would otherwise invalidate the
                    // enumerator of the loop below and abort the compile partway through with
                    // "collection was modified".
                    var order = OrderManager.CurrentOrder.ToList();

                    var stepNames = order.Select(c => c.Name).ToList();
                    var stepWeights = stepNames
                        .Select(n => stepShares.TryGetValue(n, out var w) ? w : 1d / Math.Max(1, stepNames.Count))
                        .ToList();

					int stepNumber = 0;
					foreach (var compileProcess in order)
					{
                        cancellationToken.ThrowIfCancellationRequested();
                        currentCompileProcess = compileProcess;

                        LogStepDivider(compileProcess.Name, ++stepNumber, order.Count);

                        double share = stepShares.TryGetValue(compileProcess.Name, out var s)
                            ? s
                            : 1d / Math.Max(1, order.Count) / Math.Max(1, queued.Count);

                        OnStepChanged?.Invoke(new CompileStepInfo
                        {
                            StepName = compileProcess.Name,
                            StepNumber = stepNumber,
                            StepCount = order.Count,
                            MapNumber = mapNumber,
                            MapCount = queued.Count,
                            StepNames = stepNames,
                            StepWeights = stepWeights,
                            Remaining = EstimateRemaining(cleanMapName, stepNames, stepNumber, queued, mapNumber),
                        });

                        // Hand the step its own slice of the bar, so one that can report its internal
                        // progress does not have to sit at whatever the previous step left behind.
                        CompileProcess.BeginStepProgress(ProgressManager.Progress, share);

                        var stepStopwatch = Stopwatch.StartNew();
                        compileProcess.Run(buildContext, cancellationToken);
                        stepStopwatch.Stop();

                        CompileTimings.Record(cleanMapName, compileProcess.Name, stepStopwatch.Elapsed);

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

                        ProgressManager.Progress += share;

                        // log empty line to make a space inbetween compile step logs
                        CompilePalLogger.LogLine();
                    }

                    mapErrors.Add(new MapErrors { MapName = cleanMapName, Errors = compileErrors });

                    // Severity 4 and 5 are the levels the log calls errors; anything below is a warning.
                    // The card shows the two separately because they mean different things to the user:
                    // warnings are worth reading afterwards, an error usually means redoing the compile.
                    mapStopwatch.Stop();
                    var elapsed = mapStopwatch.Elapsed;
                    int errorCount = compileErrors.Count(e => e.Severity >= 4);
                    int warningCount = compileErrors.Count(e => e.Severity is > 0 and < 4);

                    UpdateMapOnUiThread(map, x =>
                    {
                        x.LastDuration = elapsed;
                        x.LastErrorCount = errorCount;
                        x.LastWarningCount = warningCount;
                        x.State = errorCount > 0 ? MapCompileState.Failed : MapCompileState.Succeeded;
                    });

                    // Counted per map rather than per run: "how often does a compile come out
                    // clean" is the question worth answering, and a run of five maps where one
                    // failed is not one failure.
                    if (errorCount > 0)
                        TelemetryManager.CompileFailed();
                    else
                        TelemetryManager.CompileSucceeded();

                    CompilePalLogger.LogLineFileLocation($"Compiled Map: {buildContext.CopyLocation}\n", buildContext.CopyLocation);
                    GameConfigurationManager.RestoreCurrentContext();
                }

                if (!cancellationToken.IsCancellationRequested)
                    MainWindow.ActiveDispatcher.Invoke(() => postCompile(mapErrors));
            }
            // cts.Cancel() is only ever called from CancelCompile(), which already updates the
            // taskbar/progress state itself (and does so before this can even be reached) - reporting
            // it again here just raced the same state onto the taskbar a second time.
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                // Anything else is a bug - in a compile step, or in Compile Pal. This used to have no
                // handler at all, so the exception was left on an unobserved Task and then swallowed
                // whole by the `try { await compileTask; } catch { }` at the top of the next
                // StartCompile. A compile that died on its first statement was indistinguishable from
                // one running quietly: the button read Cancel, the elapsed timer ran, and the output
                // stayed empty with nothing anywhere to say why.
                CompilePalLogger.LogLineColor(
                    $"The compile stopped with an unhandled error: {e.Message}", Error.GetSeverityBrush(5));
                CompilePalLogger.LogLineDebug(e.ToString());
                ExceptionHandler.LogException(e, false);

                ProgressManager.ErrorProgress();
                MainWindow.ActiveDispatcher.Invoke(() => postCompile(null, cancelled: true));
            }
        }

        /// <summary>
        /// Roughly how much of the run is left, or null when there is no history to base it on.
        ///
        /// Counts the steps of the current map still to come plus a whole map's worth for each map after
        /// it. Deliberately coarse: the point is to tell "another minute" from "another half hour", not
        /// to be accurate to the second, and a figure that visibly jitters would be trusted less than no
        /// figure at all.
        /// </summary>
        private static TimeSpan? EstimateRemaining(string mapName, IReadOnlyList<string> stepNames,
            int stepNumber, IReadOnlyList<Map> queuedMaps, int mapNumber)
        {
            double thisMap = 0;
            bool anyKnown = false;

            // stepNumber is 1-based and names the step about to start, so it is included.
            for (int i = stepNumber - 1; i < stepNames.Count; i++)
            {
                if (CompileTimings.Median(mapName, stepNames[i]) is { } seconds)
                {
                    thisMap += seconds;
                    anyKnown = true;
                }
            }

            if (!anyKnown)
                return null;

            double total = thisMap;

            foreach (var later in queuedMaps.Skip(mapNumber))
            {
                string laterName = Path.GetFileNameWithoutExtension(later.File);
                foreach (var step in stepNames)
                {
                    // Falls back to this map's figure for a map never compiled before, which is a better
                    // guess than leaving it out and claiming the run ends sooner than it will.
                    total += CompileTimings.Median(laterName, step) ?? CompileTimings.Median(mapName, step) ?? 0;
                }
            }

            return TimeSpan.FromSeconds(total);
        }

        /// <summary>
        /// Maps this run will actually build.
        ///
        /// Not MapFiles.Count, which the progress maths used to divide by: that counts maps whose
        /// checkbox is clear too, so compiling one of three queued maps could only ever fill a third of
        /// the bar and reported 33% as "finished".
        /// </summary>
        private static int CompilingMapCount(IReadOnlyList<Map> maps) => Math.Max(1, maps.Count(m => m.Compile));

        /// <summary>
        /// How much of the whole run each step of this map accounts for, keyed by step name.
        ///
        /// Weighted by how long the steps have taken before rather than split evenly, so the bar moves
        /// at something like a constant rate instead of stalling through VVIS and then leaping. Scaled
        /// down by the number of maps so the shares across the whole run still sum to 1.
        /// </summary>
        private static Dictionary<string, double> StepShares(Map map, IReadOnlyList<Map> maps)
        {
            var stepNames = OrderManager.CurrentOrder.Select(c => c.Name).ToList();
            var shares = CompileTimings.Shares(map.MapName, stepNames);

            int mapCount = CompilingMapCount(maps);
            foreach (var name in shares.Keys.ToList())
                shares[name] /= mapCount;

            return shares;
        }

        private static void postCompile(List<MapErrors> errors, bool cancelled = false)
        {
            // Saved even for a cancelled run: the steps that did finish before the cancel took their
            // real time, and that is exactly as useful for weighting the next bar.
            CompileTimings.Save();

            // A map still marked Running or Queued when the run ends did not finish - either the cancel
            // caught it mid-step, or the loop never reached it. Leaving it showing "compiling…" forever
            // would be the worst of the three states to be wrong about.
            foreach (var map in MapFiles)
            {
                if (map.State is MapCompileState.Running or MapCompileState.Queued)
                    map.State = cancelled ? MapCompileState.Cancelled : MapCompileState.None;
            }

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
                            TelemetryManager.CompileError();
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

            // ErrorProgress(), not SetProgress(0): this is the only place that sets the taskbar's final
            // state after a cancel, so it needs to land on that state directly - going through the empty
            // state first just made the taskbar icon flash empty then red a moment later.
            ProgressManager.ErrorProgress();

            CompilePalLogger.LogLineColor("Compile forcefully ended.", (Brush) Application.Current.TryFindResource("CompilePal.Brushes.Severity4"));

            TelemetryManager.CompileCancelled();

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
