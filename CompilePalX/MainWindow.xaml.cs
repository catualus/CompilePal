using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using CompilePalX.Compiling;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Windows.Media.TextFormatting;
using CompilePalX.Compilers;
using CompilePalX.Configuration;
using Path = System.IO.Path;
using System.Runtime.InteropServices;

namespace CompilePalX
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Raises a change for the named property.
        ///
        /// This used to ignore <paramref name="name"/> and always announce
        /// AddCustomParameterButtonEnabled, which happened to be harmless while that was the only bound
        /// property - every caller passed it - but meant any second binding would silently never
        /// update.
        /// </summary>
        private void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public static Dispatcher ActiveDispatcher;
        private ObservableCollection<CompileProcess> CompileProcessesSubList = [];

        // processModeEnabled is gone with the two overlaid parameter grids it used to switch between.
        // The CUSTOM step now renders its own program columns inside its own row of the stepper, so
        // nothing global has to know which kind of step is selected.

        public bool PresetFilterEnabled { get; set; } = true;

        private DispatcherTimer elapsedTimeDispatcherTimer;

        private readonly List<Hyperlink> outputErrorLinks = [];
        private int currentErrorIndex = -1;

        private List<TextRange> outputSearchMatches = [];
        private int currentSearchMatchIndex = -1;
        private string? lastSearchQuery;

        // Created once the XAML document exists; see the constructor.
        private OutputSearch outputSearch = null!;

        #region Output buffering

        /// <summary>
        /// Log lines written but not yet put into the document.
        ///
        /// Compile tools emit output a line at a time and a verbose VRAD run emits a great many of them.
        /// Adding each one to the live FlowDocument individually - and calling ScrollToEnd after each,
        /// which forces the whole document to be measured - meant the UI thread spent the busiest part
        /// of a compile re-laying out text rather than staying responsive. Runs are still created
        /// immediately, because callers rely on getting one back (see CompilePalLogger.LogProgressive,
        /// which blanks them again to redraw a progress line), but the document only changes on a tick.
        /// </summary>
        private readonly List<Inline> pendingOutputInlines = [];

        /// <summary>
        /// Priority matters here, and the default is wrong.
        ///
        /// DispatcherTimer defaults to <see cref="DispatcherPriority.Background"/>, which is *below* the
        /// Normal priority of the Dispatcher.Invoke each log line arrives on. While a compile is writing
        /// output the Normal queue never empties, so a Background tick never gets a turn: the buffer
        /// filled and the OUTPUT tab stayed blank until the compile stopped writing, which made pressing
        /// Cancel look like the thing that produced the log.
        /// </summary>
        private readonly DispatcherTimer outputFlushTimer =
            new(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(100) };

        /// <summary>
        /// Lines to buffer before flushing regardless of the timer.
        ///
        /// Belt and braces for the starvation above: a verbose step can emit thousands of lines a second
        /// and the output must appear as it happens, not whenever the dispatcher next draws breath.
        /// Also caps how much is held back if the timer is ever delayed again.
        /// </summary>
        private const int MaxPendingOutputInlines = 200;

        /// <summary>Adds an inline to the next flush. Must be called on the UI thread.</summary>
        private void QueueOutput(Inline inline)
        {
            pendingOutputInlines.Add(inline);

            if (pendingOutputInlines.Count >= MaxPendingOutputInlines)
            {
                FlushOutput();
                return;
            }

            if (!outputFlushTimer.IsEnabled)
                outputFlushTimer.Start();
        }

        /// <summary>
        /// Puts everything written since the last flush into the document.
        ///
        /// Anything that reads, searches or saves the output has to call this first, or it works against
        /// a document missing the most recent lines.
        /// </summary>
        private void FlushOutput()
        {
            if (pendingOutputInlines.Count == 0)
            {
                outputFlushTimer.Stop();
                return;
            }

            // Sampled before the insert: afterwards ExtentHeight describes a document the user has not
            // been shown yet, so "were they already at the bottom" is no longer answerable.
            bool wasAtBottom = CompileOutputTextbox.VerticalOffset + CompileOutputTextbox.ViewportHeight
                               >= CompileOutputTextbox.ExtentHeight - 1.0;

            OutputParagraph.Inlines.AddRange(pendingOutputInlines);
            pendingOutputInlines.Clear();

            InvalidateOutputSearchIndex();

            if (wasAtBottom)
                CompileOutputTextbox.ScrollToEnd();
        }

        #endregion

        /// <summary>
        /// The document changed, so the search index and any painted highlights are stale.
        /// Called from every place that appends to or rewrites OutputParagraph.
        /// </summary>
        private void InvalidateOutputSearchIndex() => outputSearch?.Invalidate();

        #region Output navigation

        /// <summary>Every error and warning recognised in the current log, newest last.</summary>
        public ObservableCollection<LoggedIssue> Issues { get; } = [];

        private ICollectionView? issuesView;

        /// <summary>Filtered view behind the issues list. Its own view, so nothing else is affected.</summary>
        public ICollectionView IssuesView =>
            issuesView ??= CreateIssuesView();

        private ICollectionView CreateIssuesView()
        {
            var view = new CollectionViewSource { Source = Issues }.View;
            view.Filter = o => o is LoggedIssue issue && (!showErrorsOnly || issue.IsError);
            return view;
        }

        private bool showErrorsOnly;

        /// <summary>
        /// Narrows the issues list to errors only.
        ///
        /// Filtering the log document itself is not an option - a FlowDocument's blocks have no
        /// visibility to toggle, so hiding lines would mean rebuilding the document from a model on
        /// every change to the busiest code path in the app. Listing the recognised issues separately
        /// gives the same "show me only what went wrong" without touching the log.
        /// </summary>
        public bool ShowErrorsOnly
        {
            get => showErrorsOnly;
            set
            {
                if (showErrorsOnly == value)
                    return;

                showErrorsOnly = value;
                IssuesView.Refresh();
                OnPropertyChanged(nameof(ShowErrorsOnly));
                OnPropertyChanged(nameof(IssuesHeading));
            }
        }

        public string IssuesHeading
        {
            get
            {
                int errors = Issues.Count(i => i.IsError);
                int warnings = Issues.Count - errors;

                if (Issues.Count == 0)
                    return "No issues found";

                return $"{errors} error{(errors == 1 ? "" : "s")}, {warnings} warning{(warnings == 1 ? "" : "s")}";
            }
        }

        /// <summary>
        /// Where each compile step's output begins, for the jump-to-step list.
        ///
        /// Anchored to the inline the step's divider was written as, not to a TextPointer taken from
        /// the paragraph. Paragraph.ContentEnd is not a snapshot of where the end happened to be - it
        /// denotes the end of the content, wherever that currently is - so every anchor captured that
        /// way resolved to the bottom of the log and all the entries jumped to the same place.
        /// </summary>
        public sealed record StepAnchor(string Label, Inline Anchor)
        {
            public override string ToString() => Label;
        }

        public ObservableCollection<StepAnchor> StepAnchors { get; } = [];

        /// <summary>Step being logged right now, recorded against each issue as it arrives.</summary>
        private string currentStepName = "";

        private void ClearOutputNavigation()
        {
            Issues.Clear();
            StepAnchors.Clear();
            currentStepName = "";
            OnPropertyChanged(nameof(IssuesHeading));
        }

        /// <summary>Scrolls the log to an issue and highlights the line it was logged on.</summary>
        private void IssuesList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IssuesList.SelectedItem is not LoggedIssue { Link: { } link })
                return;

            MainTabControl.SelectedItem = OutputTab;
            FlushOutput();

            outputSearch.HighlightSingle(new TextRange(link.ContentStart, link.ContentEnd));
            ScrollRangeIntoView(link.ContentStart);
        }

        private void StepJumpBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (StepJumpBox.SelectedItem is not StepAnchor anchor)
                return;

            FlushOutput();

            // An anchor from a previous run points into a document that has since been cleared.
            var position = anchor.Anchor.ContentStart;
            if (position.IsInSameDocument(CompileOutputTextbox.Document.ContentStart))
                ScrollRangeIntoView(position);
        }

        #endregion

        #region Compile history

        public ObservableCollection<CompileRun> History { get; } = [];

        private void RefreshHistory()
        {
            History.Clear();
            foreach (var run in CompileHistory.Load())
                History.Add(run);

            OnPropertyChanged(nameof(HistoryIsEmpty));
        }

        public bool HistoryIsEmpty => History.Count == 0;

        /// <summary>
        /// Shows a past compile's transcript in a plain read-only viewer.
        ///
        /// Deliberately not loaded into the live OUTPUT document: that one belongs to the compile that
        /// produced it, and its error links and step anchors point into it. Replacing it with an old
        /// log would leave both pointing at text they no longer describe.
        /// </summary>
        private void HistoryList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (HistoryList.SelectedItem is not CompileRun run)
                return;

            string? log = CompileHistory.ReadLog(run);
            if (log == null)
            {
                CompilePalLogger.LogLineColor($"The log for this run is no longer on disk ({run.LogFile}).",
                    Error.GetSeverityBrush(3));
                return;
            }

            new LogViewerWindow(run, log) { Owner = this }.Show();
        }

        private void HistoryOpenFolder_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                string full = System.IO.Path.GetFullPath(CompileHistory.LogDirectory);
                Directory.CreateDirectory(full);
                Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                CompilePalLogger.LogLineDebug($"Could not open the log folder: {ex.Message}");
            }
        }

        #endregion

        #region Compile footer

        /// <summary>
        /// The game every tool path and compatibility check depends on.
        ///
        /// It used to appear only as the tail of the window title ("Compile Pal 29.1X Garry's Mod"),
        /// with the way to change it hidden behind an unlabelled hamburger glyph.
        /// </summary>
        public string GameName => GameConfigurationManager.GameConfiguration?.Name ?? "No game selected";

        /// <summary>
        /// "Queue", or "Queue · 2 of 5" once some maps are unticked.
        ///
        /// Only says "n of m" when they differ: on the common case of everything ticked, a count adds
        /// nothing but noise.
        /// </summary>
        public string QueueHeading
        {
            get
            {
                int total = CompilingManager.MapFiles.Count;
                int enabled = CompilingManager.MapFiles.Count(m => m.Compile);

                if (total == 0)
                    return "Queue";

                return enabled == total ? $"Queue · {total}" : $"Queue · {enabled} of {total}";
            }
        }

        private string compileStatusLine = "";
        /// <summary>"Meshwright · step 3 of 5 · map 1 of 2", or empty when idle.</summary>
        public string CompileStatusLine
        {
            get => compileStatusLine;
            private set { compileStatusLine = value; OnPropertyChanged(nameof(CompileStatusLine)); }
        }

        private string compileRemainingText = "";
        public string CompileRemainingText
        {
            get => compileRemainingText;
            private set { compileRemainingText = value; OnPropertyChanged(nameof(CompileRemainingText)); }
        }

        private int liveWarningCount;
        public int LiveWarningCount
        {
            get => liveWarningCount;
            private set { liveWarningCount = value; OnPropertyChanged(nameof(LiveWarningCount)); }
        }

        private int liveErrorCount;
        public int LiveErrorCount
        {
            get => liveErrorCount;
            private set { liveErrorCount = value; OnPropertyChanged(nameof(LiveErrorCount)); }
        }

        /// <summary>Error and warning navigation, copy and save only ever act on the OUTPUT tab.</summary>
        public Visibility OutputToolsVisibility =>
            MainTabControl?.SelectedItem == OutputTab ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// Where each step of the current map starts and ends on the bar, as fractions of the whole run.
        /// Used to turn the overall progress figure back into "how far through the current step".
        /// </summary>
        private double currentStepStart;
        private double currentStepEnd;

        /// <summary>When the running step began, for interpolating between step boundaries.</summary>
        private DateTime currentStepStartedAt;

        /// <summary>How long the running step is expected to take, or null if never timed.</summary>
        private TimeSpan? currentStepExpected;

        /// <summary>The whole-run estimate as it stood when the running step began.</summary>
        private TimeSpan? remainingAtStepStart;

        private List<Border> progressSegments = [];
        private int currentSegmentIndex = -1;

        /// <summary>
        /// Lays out one segment per compile step, each as wide as that step's share of the run.
        ///
        /// A single bar cannot show that VVIS is most of the compile and COPY is none of it, which is
        /// why a uniform bar sat at 40% for twenty minutes. Segment widths come from recorded durations
        /// (see CompileTimings), so the bar advances at roughly a constant rate.
        /// </summary>
        private void BuildProgressSegments(IReadOnlyList<string> stepNames, IReadOnlyList<double> weights)
        {
            CompileProgressSegments.ColumnDefinitions.Clear();
            CompileProgressSegments.Children.Clear();
            progressSegments = [];

            double total = weights.Sum();
            if (total <= 0)
                total = Math.Max(1, stepNames.Count);

            for (int i = 0; i < stepNames.Count; i++)
            {
                double weight = i < weights.Count ? weights[i] : total / stepNames.Count;

                CompileProgressSegments.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = new GridLength(Math.Max(weight, 0.0001), GridUnitType.Star) });

                var segment = new Border
                {
                    // A hairline gap so adjoining segments read as separate steps rather than one bar.
                    Margin = new Thickness(i == 0 ? 0 : 1, 0, 0, 0),
                    CornerRadius = new CornerRadius(2),
                    Background = (Brush)FindResource("ControlFillColorDefaultBrush"),
                    ToolTip = stepNames[i],
                };

                Grid.SetColumn(segment, i);
                CompileProgressSegments.Children.Add(segment);
                progressSegments.Add(segment);
            }

            currentSegmentIndex = -1;
        }

        /// <summary>Paints segments: finished behind the current one, pending ahead of it.</summary>
        private void PaintProgressSegments(int currentIndex)
        {
            currentSegmentIndex = currentIndex;

            var done = (Brush)FindResource("CompilePal.Brushes.Success");
            var pending = (Brush)FindResource("ControlFillColorDefaultBrush");

            for (int i = 0; i < progressSegments.Count; i++)
                progressSegments[i].Background = i < currentIndex ? done : pending;
        }

        private void CompilingManager_OnStepChanged(CompileStepInfo info)
        {
            Dispatcher.Invoke(() =>
            {
                if (info.StepNumber == 1 || progressSegments.Count != info.StepNames.Count)
                    BuildProgressSegments(info.StepNames, info.StepWeights);

                currentStepName = info.StepName;

                // Flushed first so the divider is in the document: it is logged immediately before this
                // event is raised, and the last inline is therefore the divider itself - which is what
                // the jump should land on.
                FlushOutput();

                if (OutputParagraph.Inlines.LastInline is { } divider)
                {
                    string label = info.MapCount > 1
                        ? $"{info.StepName} ({info.StepNumber}/{info.StepCount}) · map {info.MapNumber}"
                        : $"{info.StepName} ({info.StepNumber}/{info.StepCount})";

                    StepAnchors.Add(new StepAnchor(label, divider));
                }

                PaintProgressSegments(info.StepNumber - 1);

                // Cumulative weights either side of the running step, so ProgressManager's single overall
                // figure can be turned back into a fill fraction for that step's own segment.
                double before = info.StepWeights.Take(info.StepNumber - 1).Sum();
                double share = info.StepNumber - 1 < info.StepWeights.Count
                    ? info.StepWeights[info.StepNumber - 1]
                    : 0;

                // Weights are per map; earlier maps have already filled their share of the bar.
                double mapsBefore = info.MapCount <= 1 ? 0 : (double)(info.MapNumber - 1) / info.MapCount;
                currentStepStart = mapsBefore + before;
                currentStepEnd = currentStepStart + share;

                currentStepStartedAt = DateTime.UtcNow;
                currentStepExpected = info.Expected;
                remainingAtStepStart = info.Remaining;

                CompileStatusLine = info.MapCount > 1
                    ? $"{info.StepName} · step {info.StepNumber} of {info.StepCount} · map {info.MapNumber} of {info.MapCount}"
                    : $"{info.StepName} · step {info.StepNumber} of {info.StepCount}";

                UpdateEstimates();
            });
        }

        private static string FormatDuration(TimeSpan span) =>
            span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");

        /// <summary>Fills the running step's own segment from the overall progress figure.</summary>
        private void UpdateCurrentSegmentFill(double overallPercent)
        {
            if (currentSegmentIndex < 0 || currentSegmentIndex >= progressSegments.Count)
                return;

            double span = currentStepEnd - currentStepStart;
            double fraction = span <= 0 ? 0 : Math.Clamp((overallPercent / 100d - currentStepStart) / span, 0, 1);

            // A left-anchored gradient rather than a nested element: the segment is a couple of pixels
            // tall and only a few wide, so an extra child to size and lay out is not worth it.
            var accent = (Color)FindResource("SystemAccentColorSecondary");
            var idle = ((SolidColorBrush)FindResource("ControlFillColorDefaultBrush")).Color;

            progressSegments[currentSegmentIndex].Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
                GradientStops =
                [
                    new GradientStop(accent, 0),
                    new GradientStop(accent, fraction),
                    new GradientStop(idle, fraction),
                    new GradientStop(idle, 1),
                ],
            };
        }

        private void ResetCompileFooter()
        {
            CompileStatusLine = "";
            CompileRemainingText = "";
            LiveWarningCount = 0;
            LiveErrorCount = 0;
            currentSegmentIndex = -1;

            CompileProgressSegments.ColumnDefinitions.Clear();
            CompileProgressSegments.Children.Clear();
            progressSegments = [];
        }

        #endregion

		public static MainWindow? Instance { get; private set; }

        private ICollectionView? allPresetsView;

        /// <summary>
        /// Every preset, for the per-map dropdown on a queue card.
        ///
        /// Its own view, not KnownPresets directly: binding ItemsSource to the collection would make the
        /// dropdown use that collection's *default* view, which the preset panel groups by map and
        /// filters to the selected map. A card whose preset was filtered out would then show an empty
        /// selection and silently offer to change it.
        /// </summary>
        public ICollectionView AllPresets =>
            allPresetsView ??= new CollectionViewSource { Source = ConfigurationManager.KnownPresets }.View;

        private int SelectedMapIndex
        {
            get => selectedMapIndex;
            set => selectedMapIndex = value >= 0 ? value : 0; // prevent negative values
        }

        private int selectedMapIndex = 0;

        private bool _isCompiling = false;
        public bool IsCompiling { get => _isCompiling;
            set {
                if (value == _isCompiling)
                    return;

                _isCompiling = value;
                OnPropertyChanged(nameof(IsCompiling));
                OnPropertyChanged(nameof(IsNotCompiling));
            }
        }

        /// <summary>
        /// Everything that must not be touched while a compile is running binds its IsEnabled to this.
        ///
        /// This replaces two mirrored lists of eleven <c>SomeControl.IsEnabled = false/true</c>
        /// assignments in CompilingManager_OnStart/OnFinish. Any control left out of one of them stayed
        /// live for the whole compile, and the pair had to be kept in step by hand forever after.
        /// </summary>
        public bool IsNotCompiling => !IsCompiling;

        // The parameter grids now bind IsNotCompiling like everything else: there is one grid per step
        // rather than two overlaid ones, so no property has to combine compile state with which kind of
        // step is showing. Add Custom Parameter is likewise per row, bound to that step's own
        // SupportsCustomParameters.

		public MainWindow()
        {
	        Instance = this;

			Application.Current.DispatcherUnhandledException += Current_DispatcherUnhandledException;

            InitializeComponent();

            ActiveDispatcher = Dispatcher;

            // After InitializeComponent so the XAML-declared FlowDocument exists.
            outputSearch = new OutputSearch(CompileOutputTextbox.Document);

            outputFlushTimer.Tick += (_, _) => FlushOutput();

            CompilePalLogger.OnWrite += Logger_OnWrite;
            CompilePalLogger.OnBacktrack += Logger_OnBacktrack;
            CompilePalLogger.OnErrorLog += CompilePalLogger_OnError;
            CompilePalLogger.OnWriteURL += CompilePalLogger_OnWriteFileLocation;

            UpdateManager.OnUpdateFound += UpdateManager_OnUpdateFound;
            UpdateManager.CheckVersion();

            TelemetryManager.Launch();
            PersistenceManager.Init();
            CompileTimings.Init();
            RefreshHistory();
            ErrorFinder.Init();

            // settings must load first: AssembleParameters reads ToolsPlusPlusMode when building parameter
            // lists and LastPreset when choosing the initially selected preset
            ConfigurationManager.LoadSettings();
            ApplyOutputFontSettings();
            ConfigurationManager.OnSettingsSaved += ApplyOutputFontSettings;
            ConfigurationManager.AssembleParameters();
            ToolsPlusPlusDetector.LogDetectionResults();
            GameExeResolver.LogResolution();

            ProgressManager.TitleChange += ProgressManager_TitleChange;
            ProgressManager.ProgressChange += ProgressManager_ProgressChange;
            ProgressManager.Init(TaskbarItemInfo);


            SetSources();

            CompileProcessesListBox.Items.SortDescriptions.Add(new System.ComponentModel.SortDescription("Ordering", System.ComponentModel.ListSortDirection.Ascending));


            CompileProcessesListBox.SelectedIndex = 0;

            // restore the preset from last session, falling back to the first one
            if (ConfigurationManager.CurrentPreset != null && ConfigurationManager.KnownPresets.Contains(ConfigurationManager.CurrentPreset))
                PresetConfigListBox.SelectedItem = ConfigurationManager.CurrentPreset;
            else
                PresetConfigListBox.SelectedIndex = 0;

            MapListBox.SelectedIndex = 0;

            UpdateConfigGrid();

            CompilingManager.OnClear += CompilingManager_OnClear;

            // The heading counts maps and how many are ticked, so it follows both the collection and
            // each map's own Compile flag - TrulyObservableCollection raises for the latter too.
            CompilingManager.MapFiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(QueueHeading));

            // Once explicitly: PersistenceManager loads the queue by replacing the whole collection, so
            // restoring a saved queue raises no change event at all and the heading would sit on the
            // count it was bound with - zero.
            OnPropertyChanged(nameof(QueueHeading));

            CompilingManager.OnStart += CompilingManager_OnStart;
            CompilingManager.OnFinish += CompilingManager_OnFinish;
            CompilingManager.OnStepChanged += CompilingManager_OnStepChanged;

            // Counted live so the footer can show a running total, rather than the user having to wait
            // for the summary at the end to learn the compile has been logging errors for ten minutes.
            CompilePalLogger.OnErrorFound += CompilePalLogger_OnErrorCounted;

			RowDragHelper.RowSwitched += RowDragHelperOnRowSwitched;

            elapsedTimeDispatcherTimer = new DispatcherTimer(new TimeSpan(0, 0, 0, 1), DispatcherPriority.Background,
                TickElapsedTimer, Dispatcher.CurrentDispatcher)
            {
                IsEnabled = false
            };

            HandleArgs();

            if (compileOnStartup)
            {
                // Queued rather than called: parameters, presets and the process order are still being
                // assembled at this point, and a compile started here would run against half of them.
                Dispatcher.BeginInvoke(new Action(StartCompileFromCommandLine), DispatcherPriority.ApplicationIdle);
            }


            // check to see if running on unsupported platform
            if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            {
                UnsupportedPlatformButton.Visibility = Visibility.Visible;
                // show unsupported message on startup only once
                if (!Convert.ToBoolean(RegistryManager.Read<string>("UnsupportedDialogShown")))
                {
                    ShowUnsupportedModal();
                    RegistryManager.Write("UnsupportedDialogShown", true);
                }
            }
        }

        public Task ShowModal(string title, string message)
		{
			return Theming.AppDialog.ShowAsync(title, message);
		}

	    private static void HandleArgs(bool ignoreWipeArg = false)
        {
            //Handle command line args
            string[] commandLineArgs = Environment.GetCommandLineArgs();
            for (int i = 0; i < commandLineArgs.Length; i++)
            {
	            var arg = commandLineArgs[i];
                try
                {
                    if (!ignoreWipeArg)
                    {
                        // wipes the map list
                        if (arg == "--wipe")
                        {
                            CompilingManager.MapFiles.Clear();
                            // recursive so that wipe doesn't clear maps added through the command line
                            HandleArgs(true);
                            break;
                        }
                    }

                    // adds map
                    if (arg == "--add")
                    {
                        if (i + 1 > commandLineArgs.Length)
	                        break;

                        var argPath = commandLineArgs[i + 1];

                        // Same preset choice the Add Map button makes. Without it the map carries no
                        // preset at all, and a --compile then runs with a null CurrentPreset.
                        if (File.Exists(argPath) && IsAddableMap(argPath))
                            CompilingManager.MapFiles.Add(new Map(argPath, preset: PresetForMap(argPath)));
                    }

                    // starts compiling as soon as the window is ready
                    if (arg == "--compile")
                        compileOnStartup = true;
                }
                catch (ArgumentOutOfRangeException)
                {
                    //Ignore error
                }
            }
        }

        /// <summary>
        /// The preset a newly added map should start on: the current one when it is valid for that map,
        /// otherwise the first that is. Map-specific presets exist precisely so a .bsp does not land on
        /// a preset full of steps that need a .vmf.
        /// </summary>
        private static Preset? PresetForMap(string path)
        {
            if (ConfigurationManager.CurrentPreset != null && ConfigurationManager.CurrentPreset.IsValidMap(path))
                return ConfigurationManager.CurrentPreset;

            return ConfigurationManager.KnownPresets.FirstOrDefault(p => p.IsValidMap(path))
                   ?? ConfigurationManager.KnownPresets.FirstOrDefault();
        }

        /// <summary>Map types the list accepts: sources Compile Pal can build, plus an already-built BSP.</summary>
        private static bool IsAddableMap(string path)
        {
            foreach (string extension in new[] { ".vmf", ".vmm", ".vmx", ".bsp" })
            {
                if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>Set by --compile; acted on once the window has finished loading.</summary>
        private static bool compileOnStartup;

        void CompilePalLogger_OnError(string errorText, Error e)
        {
            Dispatcher.Invoke(() =>
            {
                Hyperlink errorLink = new Hyperlink();

                Run text = new Run(errorText)
                {
                    Foreground = e.ErrorColor
                };

                errorLink.Inlines.Add(text);
                if (e.ID >= 0)
                {
                    errorLink.DataContext = e;
                    errorLink.Click += errorLink_Click;

                    outputErrorLinks.Add(errorLink);
                    UpdateErrorNavLabel();

                    // Also collected as a listable issue, so the log does not have to be read to find
                    // out what went wrong.
                    Issues.Add(new LoggedIssue
                    {
                        Error = e,
                        Text = errorText.Trim(),
                        Step = currentStepName,
                        Link = errorLink,
                    });
                    OnPropertyChanged(nameof(IssuesHeading));

                    // Opened on the first issue of a run, not on every one: an empty panel is 320px of
                    // log the user could have been reading, but once there is something in it they
                    // should not have to go looking. Only the first, so closing it stays closed.
                    if (Issues.Count == 1)
                        IssuesToggle.IsChecked = true;
                }

                var underline = new TextDecoration
                {
                    Location = TextDecorationLocation.Underline,
                    Pen = new Pen(e.ErrorColor, 1),
                    PenThicknessUnit = TextDecorationUnit.FontRecommended
                };

                errorLink.TextDecorations = new TextDecorationCollection([underline]);

                QueueOutput(errorLink);

            });
        }

        static void errorLink_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var link = (Hyperlink)sender;
            Error error = (Error)link.DataContext;

            ErrorFinder.ShowErrorDialog(error);
        }
        

        Run? Logger_OnWrite(string s, Brush? b = null, int? fontWeight = null)
        {
            return Dispatcher.Invoke(() =>
            {
                if (string.IsNullOrEmpty(s))
                    return null;

                Run textRun = new Run(s);

                if (b != null)
                    textRun.Foreground = b;

                if (fontWeight != null)
                    textRun.FontWeight = FontWeight.FromOpenTypeWeight((int)fontWeight);

                QueueOutput(textRun);

                return textRun;
            });
        }

        void Logger_OnBacktrack(List<Run> removals)
        {
            Dispatcher.Invoke(() =>
            {
                foreach (var run in removals)
                {
                    run.Text = "";
                }

                InvalidateOutputSearchIndex();
            });
        }

        private Run? CompilePalLogger_OnWriteFileLocation(string s, string url, int? fontWeight = null)
        {
            return Dispatcher.Invoke(() =>
            {
                if (string.IsNullOrEmpty(s))
                    return null;

                Hyperlink link = new Hyperlink
                {
                    NavigateUri = new Uri(url)
                };
                link.RequestNavigate += Link_RequestNavigate;

                Run textRun = new Run(s)
                {
                    Foreground = FindResource("CompilePal.Brushes.Link") as Brush
                };
                if (fontWeight != null)
                {
                    textRun.FontWeight = FontWeight.FromOpenTypeWeight((int)fontWeight);
                }
                link.Inlines.Add(textRun);

                QueueOutput(link);

                return textRun;
            });
        }

        private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            // The path here comes out of the compile: CompilingManager logs the copy location as a
            // clickable link. Interpolating it into a single argument string, as this did, means a
            // path containing a double quote closes the quoted argument and everything after it
            // becomes further arguments to explorer.
            //
            // ArgumentList hands each argument to CreateProcess separately, so quotes in a path are
            // just characters. The existence check keeps a link whose target has since been deleted
            // from opening explorer on nothing.
            RevealInExplorer(e.Uri.IsFile ? e.Uri.LocalPath : e.Uri.ToString());
            e.Handled = true;
        }

        /// <summary>
        /// Selects a file in Explorer, or opens its folder if the file is gone.
        ///
        /// One place rather than three, and it takes the path as a real argument rather than as text
        /// spliced into a command line. Refuses anything that is not an existing local path: this is
        /// reached from a link built out of compile output, and "open whatever this string names" is
        /// not a capability that belongs on that path.
        /// </summary>
        private static void RevealInExplorer(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                    info.ArgumentList.Add("/select,");
                    info.ArgumentList.Add(Path.GetFullPath(path));
                    Process.Start(info);
                    return;
                }

                if (Path.GetDirectoryName(path) is { Length: > 0 } directory && Directory.Exists(directory))
                {
                    var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                    info.ArgumentList.Add(Path.GetFullPath(directory));
                    Process.Start(info);
                }
            }
            catch (Exception ex)
            {
                CompilePalLogger.LogLineDebug($"Could not reveal {path}: {ex.Message}");
            }
        }

        /// <summary>
        /// Opens a link in the user's browser.
        ///
        /// Every caller passes a literal from this assembly, and this check is here so that stays
        /// true. ProcessStartInfo with UseShellExecute resolves whatever the string names - a
        /// protocol handler, a UNC path, an executable - so a URL that ever came from a file or the
        /// network would be a way to launch arbitrary things.
        /// </summary>
        private static void OpenLink(string url)
        {
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                CompilePalLogger.LogLineDebug($"Refusing to open a non-https link: {url}");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                CompilePalLogger.LogLineDebug($"Could not open {url}: {ex.Message}");
            }
        }

        void UpdateManager_OnUpdateFound()
        {
            UpdateHyperLink.Inlines.Add(
	            $"An update is available. Current version is {UpdateManager.CurrentVersion}, latest version is {UpdateManager.LatestVersion}.");
            UpdateHyperLink.NavigateUri = UpdateManager.UpdateURL;
            UpdateLabel.Visibility = Visibility.Visible;
        }


        void Current_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            ExceptionHandler.LogException(e.Exception);
        }

        Map? GetCurrentMap()
        {
            return MapListBox.SelectedItem as Map;
        }

        void SetSources()
        {
            CompileProcessesListBox.ItemsSource = CompileProcessesSubList;

            // group presets by map
            ICollectionView presetView = CollectionViewSource.GetDefaultView(ConfigurationManager.KnownPresets);
            using (presetView.DeferRefresh())
            {
                presetView.GroupDescriptions.Clear();
                presetView.SortDescriptions.Clear();
                presetView.GroupDescriptions.Add(new PropertyGroupDescription("Map"));
                presetView.SortDescriptions.Add(new SortDescription("Map", ListSortDirection.Descending));
                presetView.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
                // filter out maps that don't match the currently selected map (presets with null maps are global
                if (PresetFilterEnabled)
                {
                    presetView.Filter = (o) =>
                    {
                        if (o is not Preset preset) return false;;

                        var map = GetCurrentMap();

                        // if no map is selected, show only global presets
                        if (map == null)
                            return preset.MapRegex == null;

                        // The map's own preset is always listed, even when the filter would exclude it.
                        // Otherwise selecting that map leaves the preset picker with nothing selected,
                        // and the "nothing selected, fall back to the first one" repair below wrote that
                        // first preset onto the map - silently replacing the preset the map was saved
                        // with, which is exactly what the per-map preset is meant to preserve.
                        if (preset.Equals(map.Preset))
                            return true;

                        return preset.IsValidMap(map.File);
                    };
                }
                else
                {
                    presetView.Filter = null;
                }
            }
            PresetConfigListBox.ItemsSource = presetView;

            MapListBox.ItemsSource = CompilingManager.MapFiles;

			OrderManager.Init();
	        OrderManager.UpdateOrder();

			
			//BindingOperations.EnableCollectionSynchronization(CurrentOrder, lockObj);
		}

        public void LoadGameConfiguration(GameConfiguration gameConfiguration)
        {
            Title = ProgressManager.WindowTitle(gameConfiguration.Name);
            OnPropertyChanged(nameof(GameName));

            PresetConfigListBox.Items.Refresh();
            // ConfigDataGrid is gone; every step row rebinds itself below.
            CompileProcessesListBox.Items.Refresh();

            // reload parameters incase new game config has a plugin folder
            ConfigurationManager.AssembleParameters();
            TelemetryManager.SelectGameConfiguration(gameConfiguration.Name);
        }

        void ProgressManager_ProgressChange(double progress)
        {
            UpdateCurrentSegmentFill(progress);

            if (progress < 0 || progress >= 100)
                CompileStartStopButton.Content = "Compile";
        }

        private void CompilePalLogger_OnErrorCounted(Error e)
        {
            Dispatcher.Invoke(() =>
            {
                // Same split the queue chips use: 4 and 5 are what the log calls errors.
                if (e.Severity >= 4)
                    LiveErrorCount++;
                else if (e.Severity > 0)
                    LiveWarningCount++;
            });
        }

        void ProgressManager_TitleChange(string title)
        {
            Title = title;
        }


        void CompilingManager_OnClear()
        {
            Dispatcher.Invoke(() =>
            {
                // Before clearing the document, not after: anything still queued belongs to the compile
                // that just ended and would otherwise be flushed into the new run's empty output.
                pendingOutputInlines.Clear();
                outputFlushTimer.Stop();

                OutputParagraph.Inlines.Clear();

                outputErrorLinks.Clear();
                currentErrorIndex = -1;
                UpdateErrorNavLabel();

                // The issues and step anchors point into the document being cleared.
                ClearOutputNavigation();

                // The ranges point into a document that no longer exists, so drop them without
                // trying to un-paint them.
                outputSearch.Reset();
                outputSearchMatches.Clear();
                currentSearchMatchIndex = -1;
                lastSearchQuery = null;
            });

        }

        private void CompilingManager_OnStart()
        {
            // Every control that has to lock during a compile binds IsEnabled to IsNotCompiling (or, for
            // parameter grids included, so setting this one
            // property is the whole of it.
            IsCompiling = true;

            ResetCompileFooter();

            CompileStartStopButton.Content = "Cancel";

            // hide update link so elapsed time can be shown
            UpdateLabel.Visibility = Visibility.Collapsed;
            TimeElapsedLabel.Visibility = Visibility.Visible;
            // Tick elapsed timer to display the default string
            TickElapsedTimer(null, null);

            elapsedTimeDispatcherTimer.IsEnabled = true;
        }

        private void CompilingManager_OnFinish()
        {
            IsCompiling = false;

            // The step line and time-remaining guess describe a run that is over; the error and warning
            // totals are the result of it, so those stay up until the next compile clears them.
            CompileStatusLine = "";
            CompileRemainingText = "";

            TimeElapsedLabel.Visibility = Visibility.Collapsed;
            elapsedTimeDispatcherTimer.IsEnabled = false;

            // The saved log is read straight off the document, so anything still sitting in the append
            // buffer has to land first or the transcript loses its last fraction of a second - which is
            // exactly where a failing compile's last words are.
            FlushOutput();

            string logName = DateTime.Now.ToString("s").Replace(":", "-") + ".txt";
            string textLog = new TextRange(CompileOutputTextbox.Document.ContentStart, CompileOutputTextbox.Document.ContentEnd).Text;

            if (!Directory.Exists(CompileHistory.LogDirectory))
                Directory.CreateDirectory(CompileHistory.LogDirectory);

            File.WriteAllText(System.IO.Path.Combine(CompileHistory.LogDirectory, logName), textLog);

            // The transcript has always been written here and never read back. Recording what the run
            // was makes it findable afterwards instead of being a folder of timestamps.
            var compiled = CompilingManager.MapFiles.Where(m => m.Compile).ToList();
            CompileHistory.Add(new CompileRun
            {
                Finished = DateTime.Now,
                LogFile = logName,
                Maps = compiled.Count == 1
                    ? compiled[0].FullMapName
                    : string.Join(", ", compiled.Select(m => m.FullMapName)),
                Preset = ConfigurationManager.CurrentPreset?.Name ?? "",
                Game = GameConfigurationManager.GameConfiguration?.Name ?? "",
                Duration = CompilingManager.GetTime().Elapsed,
                Errors = LiveErrorCount,
                Warnings = LiveWarningCount,
                Cancelled = compiled.Any(m => m.State == MapCompileState.Cancelled),
            });

            RefreshHistory();

            CompileStartStopButton.Content = "Compile";

            // A segment is only marked done when the *next* step starts, so the last one never was and
            // a finished compile ended on a bar that still had a step's worth left to go. A run that was
            // cancelled keeps its partial bar, which is the honest picture of how far it actually got.
            bool cancelled = CompilingManager.MapFiles.Any(m => m.State == MapCompileState.Cancelled);
            if (!cancelled && progressSegments.Count != 0)
                PaintProgressSegments(progressSegments.Count);

            ProgressManager.SetProgress(1);
        }

        private void ComparePresetsButton_OnClick(object sender, RoutedEventArgs e)
        {
            new PresetDiffWindow(ConfigurationManager.KnownPresets, ConfigurationManager.CurrentPreset,
                ConfigurationManager.CompileProcesses) { Owner = this }.Show();
        }

        /// <summary>Opens the preset action menu, which the kebab button owns as its context menu.</summary>
        private void PresetMenuButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { ContextMenu: { } menu } button)
                return;

            // A context menu opened by left click needs its placement set explicitly, or it appears at
            // the mouse rather than under the button.
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }

        /// <summary>
        /// Keeps one step open at a time, and makes opening a step select it.
        ///
        /// Several steps expanded at once turns the list back into a wall of grids with no sense of
        /// where you are; and selection still drives which step the preset-level actions apply to.
        /// </summary>
        private void StepExpander_OnExpanded(object sender, RoutedEventArgs e)
        {
            if (sender is not Expander expander)
                return;

            if (expander.DataContext is CompileProcess step)
                CompileProcessesListBox.SelectedItem = step;

            foreach (var other in FindVisualChildren<Expander>(CompileProcessesListBox))
            {
                if (!ReferenceEquals(other, expander))
                    other.IsExpanded = false;
            }
        }

        /// <summary>
        /// The step a control inside a SETUP row belongs to.
        ///
        /// Each step in the stepper carries its own parameter grid and its own add/remove buttons, so
        /// "which step is this for" is answered by where the control sits, not by a separate selection.
        /// </summary>
        private static CompileProcess? StepFor(object sender) =>
            (sender as FrameworkElement)?.DataContext as CompileProcess;

        /// <summary>Finds the parameter grid belonging to the same step as <paramref name="origin"/>.</summary>
        private static DataGrid? StepGridFor(object origin)
        {
            // Up to the row's card, then down to whichever of the two grids is the visible one - the
            // CUSTOM step swaps in a different set of columns.
            DependencyObject? node = origin as DependencyObject;
            while (node != null && node is not ListBoxItem)
                node = VisualTreeHelper.GetParent(node);

            return node == null ? null : FindVisualChildren<DataGrid>(node).FirstOrDefault(g => g.IsVisible);
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is T typed)
                    yield return typed;

                foreach (var descendant in FindVisualChildren<T>(child))
                    yield return descendant;
            }
        }

        private void OnConfigChanged(object sender, RoutedEventArgs e)
        {
            StepFor(sender)?.NotifyParametersChanged();
            ConfigurationManager.MarkDirty(ConfigurationManager.CurrentPreset);
        }

        private void AddParameterButton_Click(object sender, RoutedEventArgs e)
        {
            var step = StepFor(sender) ?? selectedProcess;

            if (step != null && ConfigurationManager.CurrentPreset is { } currentPreset
                             && step.PresetDictionary.ContainsKey(currentPreset))
            {
                var selectedProcess = step;
				//Skip Paramater Adder for Custom Process
	            if (selectedProcess.Name == "CUSTOM")
	            {
					selectedProcess.PresetDictionary[ConfigurationManager.CurrentPreset].Add((ConfigItem)selectedProcess.ParameterList[0].Clone());
	            }
	            else
	            {
					ParameterAdder c = new ParameterAdder(selectedProcess.ParameterList);
					c.ShowDialog();

					if (c.ChosenItem != null)
					{
						if (c.ChosenItem.CanBeUsedMoreThanOnce)
						{
							// .clone() removes problems with parameters sometimes becoming linked
							selectedProcess.PresetDictionary[ConfigurationManager.CurrentPreset].Add((ConfigItem)c.ChosenItem.Clone());
						} 
						else if (!selectedProcess.PresetDictionary[ConfigurationManager.CurrentPreset].Contains(c.ChosenItem))
						{
							selectedProcess.PresetDictionary[ConfigurationManager.CurrentPreset].Add(c.ChosenItem);
						}
					}
	            }

                TelemetryManager.ModifyPreset();

                step.NotifyParametersChanged();
            }
        }

        private void RemoveParameterButton_OnClickParameterButton_Click(object sender, RoutedEventArgs e)
        {
            var step = StepFor(sender);
            if (step == null || ConfigurationManager.CurrentPreset is not { } currentPreset
                             || !step.PresetDictionary.ContainsKey(currentPreset))
                return;

            // The selection in this step's own grid. There is no single "the" parameter grid any more,
            // so the row the button sits in decides which one is meant.
            if (StepGridFor(sender)?.SelectedItem is ConfigItem selectedItem)
                step.PresetDictionary[currentPreset].Remove(selectedItem);

            step.NotifyParametersChanged();
            ConfigurationManager.MarkDirty(currentPreset);
        }
        private void AddCustomParameterButton_Click(object sender, RoutedEventArgs e)
        {
            var step = StepFor(sender);

            if (step != null && ConfigurationManager.CurrentPreset is { } currentPreset
                             && step.PresetDictionary.ContainsKey(currentPreset))
            {
                var selectedProcess = step;
                var customArgumentItem = selectedProcess.ParameterList.FirstOrDefault(i => i.Name == "Command Line Argument");
                if (customArgumentItem == null)
                    return;

                selectedProcess.PresetDictionary[ConfigurationManager.CurrentPreset].Add((ConfigItem)customArgumentItem.Clone());
            }
            TelemetryManager.ModifyPreset();

            StepFor(sender)?.NotifyParametersChanged();
        }

        private void AddProcessButton_Click(object sender, RoutedEventArgs e)
        {
            ProcessAdder c = new ProcessAdder();
            c.ShowDialog();

            if (c.ChosenProcess != null)
            {
                CompileProcess chosenProcess = c.ChosenProcess;
                chosenProcess.Metadata.DoRun = true;
                if (!chosenProcess.PresetDictionary.ContainsKey(ConfigurationManager.CurrentPreset))
                {
                    ObservableCollection<ConfigItem> parameters = [];
                    chosenProcess.PresetDictionary.Add(ConfigurationManager.CurrentPreset, parameters);
                    // newly created lists aren't covered by the subscriptions set up during load
                    ConfigurationManager.TrackForAutosave(ConfigurationManager.CurrentPreset, parameters);
                }
                ConfigurationManager.MarkProcessesDirty();
            }

            TelemetryManager.ModifyPreset();
            ConfigurationManager.MarkDirty(ConfigurationManager.CurrentPreset);

            UpdateProcessList();
            OrderManager.UpdateOrder();
		}

        private void RemoveProcessButton_Click(object sender, RoutedEventArgs e)
        {
            if (CompileProcessesListBox.SelectedItem != null)
            {
                CompileProcess removed = (CompileProcess)CompileProcessesListBox.SelectedItem;
                removed.PresetDictionary.Remove(ConfigurationManager.CurrentPreset);
                ConfigurationManager.RemoveProcess(CompileProcessesListBox.SelectedItem.ToString());
            }
            UpdateProcessList();
            CompileProcessesListBox.SelectedIndex = 0;
		}

        private void AddPresetButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new PresetDialog("Add Preset", MapListBox.SelectedItem as Map);
            dialog.ShowDialog();

            if (!dialog.Result)
            {
                return;
            }
            var presetInfo = (Preset)dialog.DataContext;
            var preset = ConfigurationManager.NewPreset(presetInfo);

            TelemetryManager.NewPreset();

            SetSources();
            CompileProcessesListBox.SelectedIndex = 0;
            PresetConfigListBox.SelectedItem = preset;
        }
        private void ClonePresetButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (ConfigurationManager.CurrentPreset == null)
            {
                return;
            }

            var dialog = new PresetDialog("Clone Preset", MapListBox.SelectedItem as Map);
            dialog.ShowDialog();

            if (!dialog.Result)
            {
                return;
            }
            var presetInfo = (Preset)dialog.DataContext;
            var preset = ConfigurationManager.ClonePreset(presetInfo);

            TelemetryManager.NewPreset();

            SetSources();
            CompileProcessesListBox.SelectedIndex = 0;
            PresetConfigListBox.SelectedItem = preset;
        }

        private void EditPresetButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (PresetConfigListBox.SelectedItem is not Preset selectedPreset)
            {
                return;
            }
            
            var dialog = new PresetDialog("Edit Preset", MapListBox.SelectedItem as Map, (Preset)selectedPreset.Clone());
            dialog.ShowDialog();

            if (!dialog.Result)
            {
                return;
            }
            var presetInfo = (Preset)dialog.DataContext;
            var preset = ConfigurationManager.EditPreset(presetInfo);

            SetSources();
            CompileProcessesListBox.SelectedIndex = 0;
            PresetConfigListBox.SelectedItem = preset;

            // update all maps referencing the unedited preset to be the new one
            for (int i = 0; i < MapListBox.Items.Count; i++)
            {
                var map = MapListBox.Items[i] as Map;
                if (map.Preset != null && map.Preset.Equals(selectedPreset))
                    map.Preset = preset;
            }

        }

        private async void RemovePresetButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (PresetConfigListBox.SelectedItem is not Preset selectedPreset)
            {
                return;
            }

            bool confirmed = await Theming.AppDialog.ConfirmAsync("Delete Preset",
                $"Are you sure you want to delete preset {selectedPreset.Name}{(selectedPreset.Map != null ? $" ({selectedPreset.Map})" : "")}?",
                affirmativeText: "Delete");

            if (!confirmed)
                return;

            ConfigurationManager.RemovePreset(selectedPreset);

            SetSources();
            CompileProcessesListBox.SelectedIndex = 0;
            PresetConfigListBox.SelectedIndex = 0;

            // update all maps referencing the deleted preset to be default
            for (int i = 0; i < MapListBox.Items.Count; i++)
            {
                var map = MapListBox.Items[i] as Map;
                if (map.Preset != null && map.Preset.Equals(selectedPreset))
                    map.Preset = (Preset) PresetConfigListBox.SelectedItem;
            }
        }

        private void MetroWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // prevent users from accidentally closing during a compile
            if (CompilingManager.IsCompiling)
            {
                MessageBoxResult cancelBoxResult = MessageBox.Show("Compile in progress, are you sure you want to cancel?", "Cancel Confirmation", System.Windows.MessageBoxButton.YesNo);
                if (cancelBoxResult != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }

            ConfigurationManager.SavePresets();
            ConfigurationManager.SaveProcesses();

            // prevent closing if launch window is open
            if (LaunchWindow.Instance == null)
                Environment.Exit(0);//hack because wpf is weird
            Instance = null;
        }

        private void PresetConfigListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateConfigGrid();
            UpdateProcessList();
            OrderManager.UpdateOrder();

            // ignore if nothing is selected
            if (MapListBox.SelectedItem is not Map selectedMap)
            {
                // if the only map is removed and the preset becomes deselected because it is map specific, select the first preset
                if (MapListBox.Items.Count == 0 && PresetConfigListBox.SelectedItem == null)
                    PresetConfigListBox.SelectedIndex = 0;
                return;
            }

            // preset is already selected. This event gets raised when we manually change selection of the preset box when the user selects a map, this prevents a bug that deselects the map
            if (selectedMap.Preset != null && selectedMap.Preset.Equals(PresetConfigListBox.SelectedItem as Preset))
                return;

            // update map's selected preset
            if (PresetConfigListBox.SelectedItem is Preset preset)
            {
                selectedMap.Preset = preset;
            }
            // A cleared selection is not a request to change the map's preset. This used to select the
            // first preset instead, which then fell through to the assignment above and overwrote what
            // the map was actually set to.
            else if (selectedMap.Preset == null)
            {
                PresetConfigListBox.SelectedIndex = 0;
            }
        }
        private void CompileProcessesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateConfigGrid();
        }


        private CompileProcess? _selectedProcess;
        private CompileProcess? selectedProcess
        {
            get => _selectedProcess;
            set {
                if (value == selectedProcess)
                    return;
                _selectedProcess = value;
            }
        }

        /// <summary>
        /// Points the editor at the selected preset and step.
        ///
        /// Almost all of what this did is gone. It used to swap two overlaid grids, retarget their
        /// ItemsSource, toggle visibilities and rewrite a shared command-line box, because one parameter
        /// panel had to serve whichever step happened to be selected. In the stepper each step renders
        /// its own parameters from its own bindings, so the only shared state left is which preset is
        /// being edited.
        /// </summary>
        private void UpdateConfigGrid()
        {
            ConfigurationManager.CurrentPreset = PresetConfigListBox.SelectedItem as Preset;

            selectedProcess = CompileProcessesListBox.SelectedItem as CompileProcess;

            // The preset decides what every row resolves its parameters and summary from.
            RefreshStepSummaries();
        }

        private void UpdateProcessList()
        {
            CompileProcessesListBox.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(50))));


            int currentIndex = CompileProcessesListBox.SelectedIndex;

            CompileProcessesSubList.Clear();

            CompileProcessesListBox.Items.SortDescriptions.Add(new SortDescription("Ordering", ListSortDirection.Ascending));

            foreach (CompileProcess p in ConfigurationManager.CompileProcesses)
            {
                if (ConfigurationManager.CurrentPreset != null)
                    if (p.PresetDictionary.ContainsKey(ConfigurationManager.CurrentPreset))
                        CompileProcessesSubList.Add(p);
            }

            if (currentIndex < CompileProcessesListBox.Items.Count && currentIndex >= 0)
                CompileProcessesListBox.SelectedIndex = currentIndex;
        }

        /// <summary>
        /// Re-reads the argument summary and command line on every step row.
        ///
        /// These used to be one shared read-only textbox showing whichever step was selected; each step
        /// now shows its own, so a change has to be announced to all of them.
        /// </summary>
        void RefreshStepSummaries()
        {
            foreach (var process in CompileProcessesSubList)
                process.NotifyParametersChanged();
        }

        private void MetroWindow_Activated(object sender, EventArgs e)
        {
            ProgressManager.PingProgress();
        }

        private void AddMapButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog();

            if (GameConfigurationManager.GameConfiguration.SDKMapFolder != null)
                dialog.InitialDirectory = GameConfigurationManager.GameConfiguration.SDKMapFolder;

            dialog.Multiselect = true;
            dialog.Filter = "Map Files (*.vmf;*.vmm;*.bsp)|*.vmf;*.vmm;*.bsp|All Files (*.*)|*.*";

            try
            {
                dialog.ShowDialog();
            }
            catch
            {
                CompilePalLogger.LogDebug($"AddMapButton dialog failed to open, falling back to {Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}");
				// if dialog fails to open it's possible its initial directory is in a non existant folder or something
	            dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
	            dialog.ShowDialog();
            }

            AddMaps(dialog.FileNames);
        }

        /// <summary>
        /// Queues maps, skipping any already in the list.
        ///
        /// Shared by the Add Map button and by dropping files on the window so the two agree on preset
        /// choice and on de-duplication; queueing the same file twice only ever compiled it twice.
        /// </summary>
        private static void AddMaps(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                if (CompilingManager.MapFiles.Any(m => string.Equals(m.File, path, StringComparison.OrdinalIgnoreCase)))
                    continue;

                CompilingManager.MapFiles.Add(new Map(path, preset: PresetForMap(path)));
            }
        }

        /// <summary>Map files being dragged over the window, ignoring anything Compile Pal cannot build.</summary>
        private static List<string> DraggedMaps(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return [];

            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
                return [];

            return paths.Where(p => File.Exists(p) && IsAddableMap(p)).ToList();
        }

        private void CompileWindow_OnDragOver(object sender, DragEventArgs e)
        {
            // Refusing the drop while compiling rather than silently ignoring it: the cursor says up
            // front that the window will not take the file, instead of accepting it to no effect.
            e.Effects = !IsCompiling && DraggedMaps(e).Count != 0 ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void CompileWindow_OnDrop(object sender, DragEventArgs e)
        {
            // CompileThreaded iterates MapFiles directly, so adding to it mid-compile would both change
            // what still gets compiled and risk invalidating that enumeration.
            if (IsCompiling)
                return;

            AddMaps(DraggedMaps(e));
            e.Handled = true;
        }

        private void RemoveMapButton_Click(object sender, RoutedEventArgs e)
        {
            if (MapListBox.SelectedItem is Map selectedMap)
                CompilingManager.MapFiles.Remove(selectedMap);
        }

        /// <summary>
        /// Keeps the editor in step when a map's preset is changed from its own card.
        ///
        /// The binding has already written the new preset onto the map. What still has to happen is what
        /// selecting the map in the list does: point the preset panel and the step list at it, so the
        /// SETUP tab is editing the preset the card now says this map uses.
        /// </summary>
        private void MapPresetCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox { DataContext: Map map } combo || combo.SelectedItem is not Preset preset)
                return;

            // Only for the map being edited. Changing another card's preset should not yank the panel
            // over to it - the user is labelling the queue, not switching what they are looking at.
            if (!ReferenceEquals(MapListBox.SelectedItem, map))
                return;

            if (ReferenceEquals(ConfigurationManager.CurrentPreset, preset))
                return;

            ConfigurationManager.CurrentPreset = preset;
            PresetConfigListBox.SelectedItem = preset;
            UpdateProcessList();
            UpdateConfigGrid();
        }

        /// <summary>The map a map-list context menu item was opened on.</summary>
        private static Map? MapFromMenuItem(object sender) => (sender as FrameworkElement)?.DataContext as Map;

        private void MapOpenFolder_OnClick(object sender, RoutedEventArgs e)
        {
            if (MapFromMenuItem(sender) is not { } map)
                return;

            // /select, highlights the map inside its folder rather than just opening the folder. Falls
            // back to the folder alone when the file has since been moved or deleted. See
            // RevealInExplorer for why the path is not spliced into a command line.
            RevealInExplorer(map.File);
        }

        private void MapCopyPath_OnClick(object sender, RoutedEventArgs e)
        {
            if (MapFromMenuItem(sender) is not { } map)
                return;

            try
            {
                Clipboard.SetText(map.File);
            }
            catch (Exception ex)
            {
                // Another process can hold the clipboard open, which makes SetText throw. Losing a
                // copied path is not worth taking the window down for.
                CompilePalLogger.LogDebug($"Could not copy map path to the clipboard: {ex.Message}");
            }
        }

        private void MapRemove_OnClick(object sender, RoutedEventArgs e)
        {
            // The Add/Remove buttons bind IsEnabled to IsNotCompiling, but a ContextMenu is not in the
            // visual tree so it cannot reach that binding by ElementName - and removing a map while the
            // compile is walking the queue is exactly the kind of change that used to abort the run.
            if (IsCompiling)
                return;

            // Deliberately the right-clicked map, not the selected one - a context menu acts on what it
            // was opened on, and right-clicking a ListBox row does not select it.
            if (MapFromMenuItem(sender) is { } map)
                CompilingManager.MapFiles.Remove(map);
        }


        /// <summary>
        /// Begins a compile requested with --compile. Mirrors the button rather than reimplementing it,
        /// so a scripted run and a clicked one take exactly the same path.
        /// </summary>
        private void StartCompileFromCommandLine()
        {
            if (CompilingManager.MapFiles.Count == 0)
            {
                CompilePalLogger.LogLineColor("--compile was given but no maps are queued.",
                    Error.GetSeverityBrush(2));
                return;
            }

            CompilePalLogger.LogLine($"Starting compile from the command line ({CompilingManager.MapFiles.Count} map(s)).");
            CompileStartStopButton_OnClick(CompileStartStopButton, new RoutedEventArgs());
        }

        private async void CompileStartStopButton_OnClick(object sender, RoutedEventArgs e)
        {
            // never compile against edits that are still sitting in the debounce window
            ConfigurationManager.Flush();

            // Button label is driven by CompilingManager_OnStart/OnFinish, which fire for every
            // transition (including an error auto-cancelling the compile, which never reaches this
            // click handler) - toggling here too raced with them and could leave the label backwards.
            await CompilingManager.ToggleCompileState();

            OutputTab.Focus();
        }

        private void UpdateLabel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
			// This fork's releases, not upstream's - and https, which the old link was not.
			OpenLink("https://github.com/catualus/CompilePal/releases/latest");
        }

	    /// <summary>
	    /// Rebuilds the compile order when the ORDER tab is opened.
	    ///
	    /// The order is derived from the current preset and which steps are ticked, both of which can
	    /// change while another tab is showing, so it is recomputed on the way in rather than kept live.
	    /// </summary>
	    private void MainTabControl_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
	    {
		    // SelectionChanged bubbles from every Selector inside the tabs (the preset and process
		    // lists, the parameter grids), so ignore anything that did not come from this TabControl.
		    if (!ReferenceEquals(e.Source, MainTabControl))
			    return;

		    // Never while a compile is running. UpdateOrder clears and refills the collection the compile
		    // is stepping through, so opening this tab mid-run aborted the compile outright with
		    // "collection was modified" - after VRAD had already spent four minutes on it. The order on
		    // screen during a run is the one being executed anyway, so there is nothing to rebuild.
		    if (MainTabControl.SelectedItem == OrderTab && !IsCompiling)
			    OrderManager.UpdateOrder();

		    // Error navigation, copy and save only mean anything against the log, so they appear with it
		    // instead of sitting in the footer looking available on every other tab.
		    OnPropertyChanged(nameof(OutputToolsVisibility));
	    }

        private void MapListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // clear config datagrid so no stale data is shown
            // Step rows rebind from the newly selected map's preset via UpdateConfigGrid below.

            // no maps selected, default to last selected index. When we update any bound item in the MapBox datasource it will deselect all items, this reselects it after it has been deselected
            if (MapListBox.SelectedItem is not Map selectedMap)
            {
                // a map got deleted, make sure selected map index is valid
                if (MapListBox.Items.Count - 1 < SelectedMapIndex)
                    SelectedMapIndex = MapListBox.Items.Count - 1;

                MapListBox.SelectedIndex = SelectedMapIndex;
            } else
            {
                // select the preset of the map
                ConfigurationManager.CurrentPreset = selectedMap.Preset;
                PresetConfigListBox.SelectedItem = ConfigurationManager.CurrentPreset;
                SelectedMapIndex = MapListBox.SelectedIndex;
            }

            // refresh preset config listbox to filter the presets
            CollectionViewSource.GetDefaultView(ConfigurationManager.KnownPresets).Refresh();
            UpdateConfigGrid();
        }

	    private void DoRun_OnClick(object sender, RoutedEventArgs e)
	    {
		    // Unconditional now that ORDER is its own tab. It used to only refresh while the CUSTOM
		    // process was selected, because that was the only time the order grid could be on screen.
			OrderManager.UpdateOrder();

			ConfigurationManager.MarkProcessesDirty();
		}

	    private void DataGridCell_OnEnter(object sender, MouseEventArgs e)
	    {
			//Only show drag cursor if row is draggable
		    if (sender is DataGridRow { Item: CompileProcess process } && process.IsDraggable)
			    Cursor = Cursors.SizeAll;
	    }

	    private void DataGridCell_OnExit(object sender, MouseEventArgs e)
	    {
		    if (sender is DataGridRow { Item: CompileProcess process } && process.IsDraggable)
			    Cursor = Cursors.Arrow;
	    }

	    public void UpdateOrderGridSource<T>(ObservableCollection<T> newSrc)
	    {
			//Use dispatcher so this can be called from seperate thread
			Dispatcher.Invoke(() =>
			{
				//TODO order grid doesnt seem to want to update, so have to do it manually by resetting the source
				//Update ordergrid by resetting collection
				OrderGrid.ItemsSource = newSrc;
			});
		}

		private void RowDragHelperOnRowSwitched(object sender, RowSwitchEventArgs e)
		{
			var primaryItem = OrderGrid.Items[e.PrimaryRowIndex] as CustomProgram;
			var displacedItem = OrderGrid.Items[e.DisplacedRowIndex] as CustomProgram;

			SetOrder(primaryItem, e.PrimaryRowIndex);
			SetOrder(displacedItem, e.DisplacedRowIndex);
		}

	    public void SetOrder<T>(T target, int newOrder)
	    {
			//Generic T is workaround for CustomProgram being
		    //less accessible than this method.
            if (target is not CustomProgram program)
			    return;
            CompilePalLogger.LogDebug($"Setting order of target: {target} to {newOrder}");
			var programConfig = GetConfigFromCustomProgram(program);

			if (programConfig == null)
				return;

			program.CustomOrder = newOrder;
			programConfig.Warning = newOrder.ToString();
		}


		/// <summary>
		/// Finds the ConfigItem backing a custom program, so reordering can write its new position.
		///
		/// Reads the CUSTOM step's parameters for the current preset directly. It used to walk the
		/// ItemsSource of the dedicated program grid, which no longer exists - and which also meant this
		/// only worked while that grid happened to be the one on screen.
		/// </summary>
		private ConfigItem? GetConfigFromCustomProgram(CustomProgram program)
	    {
		    if (ConfigurationManager.CurrentPreset is not { } preset)
			    return null;

		    var customStep = ConfigurationManager.CompileProcesses
			    .FirstOrDefault(c => c.Name == "CUSTOM" && c.PresetDictionary.ContainsKey(preset));

		    if (customStep == null)
			    return null;

		    foreach (var item in customStep.PresetDictionary[preset])
		    {
			    if (program.Equals(item))
				    return item;
		    }

			//Return null on failure
		    return null;
	    }

		private void UpdateHyperLink_OnRequestNavigate(object sender, RequestNavigateEventArgs e)
		{
			OpenLink(e.Uri.AbsoluteUri);
			e.Handled = true;
		}

		private void Settings_OnClick(object sender, RoutedEventArgs e)
		{
			throw new NotImplementedException();
		}

		private void ConfigBack_OnClick(object sender, RoutedEventArgs e)
		{
			if (LaunchWindow.Instance == null)
				new LaunchWindow().Show();
			else
				LaunchWindow.Instance.Focus();
		}

        private void BugReportButton_OnClick(object sender, RoutedEventArgs e)
        {
			// Bug reports belong on the fork that produced the build.
			OpenLink("https://github.com/catualus/CompilePal/issues/");
            e.Handled = true;
        }

        private void ShowUnsupportedModal()
        {
            ShowModal("Unsupported Platform", $"{RuntimeInformation.OSDescription} is no longer officially supported\nSome features may not work as exepcted\n\nKnown Issues:\nUnable to automatically check for updates");
        }
        private void UnsupportedPlatformButton_OnClick(object sender, RoutedEventArgs e)
        {
            ShowUnsupportedModal();
        }

        private void TickElapsedTimer(object sender, EventArgs e)
        {
            var time = CompilingManager.GetTime().Elapsed;
            TimeElapsedLabel.Content = $"Time Elapsed: {(int) time.TotalHours:00}:{time:mm}:{time:ss}";

            UpdateEstimates();
        }

        /// <summary>
        /// Advances the running step's segment and the "time remaining" text between step boundaries.
        ///
        /// Both used to be written once, when a step began, and then left alone until the next one -
        /// so on a real compile the bar and the estimate stood still for the whole of VVIS and VRAD,
        /// which is where nearly all the time goes. The bar looked stuck and the estimate looked
        /// wrong, and neither was: they simply were not being updated.
        ///
        /// The interpolation is against how long the step has taken before, which is the same figure
        /// the estimate is built from. Where a step has no history there is nothing honest to
        /// interpolate against, so the segment is left at its start rather than invented - an
        /// unknown step is the one case where a still bar is the truthful answer.
        /// </summary>
        private void UpdateEstimates()
        {
            if (!IsCompiling)
                return;

            var inStep = DateTime.UtcNow - currentStepStartedAt;
            if (inStep < TimeSpan.Zero)
                inStep = TimeSpan.Zero;

            if (currentStepExpected is { TotalSeconds: > 0 } expected)
            {
                /*
                 * Held below 1 deliberately. A step that runs longer than its median would otherwise
                 * fill its segment completely and sit there looking finished while it is still
                 * working - and the next segment is not ours to start filling.
                 */
                double fraction = Math.Min(inStep.TotalSeconds / expected.TotalSeconds, 0.99);
                double overall = currentStepStart + fraction * (currentStepEnd - currentStepStart);

                UpdateCurrentSegmentFill(overall * 100d);
            }

            if (remainingAtStepStart is { } remaining)
            {
                // Counted down from what was estimated when the step began. An overrunning step
                // reaches zero and stops there rather than going negative, which is the honest
                // presentation of "longer than expected" without pretending to a new estimate the
                // recorded timings cannot support.
                var left = remaining - inStep;

                CompileRemainingText = left.TotalSeconds >= 1
                    ? $"~{FormatDuration(left)} left"
                    : "";
            }
            else
            {
                CompileRemainingText = "";
            }
        }

        /// <summary>
        /// Applies the configured output font.
        ///
        /// The FontFamily/FontSize MUST be assigned to the FlowDocument, not only to the hosting
        /// RichTextBox. A FlowDocument declared inline in XAML as RichTextBox.Document does not
        /// inherit text properties from that RichTextBox - it falls back to WPF's own document
        /// defaults, which are Georgia at 12. That is why the OUTPUT tab rendered in a proportional
        /// serif despite the monospace family declared in XAML, and why the "Output Font Size"
        /// setting appeared to do nothing: both were being set on a control the text did not
        /// inherit from.
        ///
        /// Confirmed by querying the running app through UI Automation's TextPattern
        /// (FontNameAttribute reported "Georgia" / size 12 while the RichTextBox was set to
        /// Consolas / 20). Setting only the control is NOT sufficient here, despite an isolated
        /// RichTextBox built in code appearing to inherit correctly.
        /// </summary>
        private void ApplyOutputFontSettings()
        {
            Dispatcher.Invoke(() =>
            {
                var settings = ConfigurationManager.Settings;
                var document = CompileOutputTextbox.Document;

                if (!string.IsNullOrWhiteSpace(settings.OutputFontFamily))
                {
                    FontFamily family;
                    try
                    {
                        family = new FontFamily(settings.OutputFontFamily);
                    }
                    catch (Exception)
                    {
                        // The family string is user-typed and free-form, so keep a bad value from
                        // taking down the settings save.
                        family = new FontFamily("Consolas, Courier New");
                    }

                    CompileOutputTextbox.FontFamily = family;
                    if (document != null)
                        document.FontFamily = family;
                }

                if (settings.OutputFontSize > 0)
                {
                    CompileOutputTextbox.FontSize = settings.OutputFontSize;
                    if (document != null)
                        document.FontSize = settings.OutputFontSize;
                }
            });
        }

        private void CopyButton_OnClick(object sender, RoutedEventArgs e)
        {
            // Everything below reads the document rather than the buffer, so land the buffer first.
            FlushOutput();
            Clipboard.SetText(new TextRange(CompileOutputTextbox.Document.ContentStart, CompileOutputTextbox.Document.ContentEnd).Text);
        }

        private void SaveLogButton_OnClick(object sender, RoutedEventArgs e)
        {
            FlushOutput();

            var dialog = new SaveFileDialog
            {
                Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*",
                FileName = $"CompilePal-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt"
            };

            if (dialog.ShowDialog() == true)
            {
                string text = new TextRange(CompileOutputTextbox.Document.ContentStart, CompileOutputTextbox.Document.ContentEnd).Text;
                File.WriteAllText(dialog.FileName, text);
            }
        }

        #region Error navigation

        private void UpdateErrorNavLabel()
        {
            ErrorNavLabel.Content = outputErrorLinks.Count == 0
                ? ""
                : $"{(currentErrorIndex >= 0 ? currentErrorIndex + 1 : 0)}/{outputErrorLinks.Count}";
        }

        private void PrevErrorButton_OnClick(object sender, RoutedEventArgs e) => StepError(-1);
        private void NextErrorButton_OnClick(object sender, RoutedEventArgs e) => StepError(1);

        private void StepError(int direction)
        {
            // An error queued but not yet in the document has no valid position to scroll to.
            FlushOutput();

            if (outputErrorLinks.Count == 0)
                return;

            currentErrorIndex = currentErrorIndex < 0
                ? (direction > 0 ? 0 : outputErrorLinks.Count - 1)
                : (currentErrorIndex + direction + outputErrorLinks.Count) % outputErrorLinks.Count;

            var link = outputErrorLinks[currentErrorIndex];

            // Same reason search stopped using the selection: focus is on the nav button, so a
            // selection renders "inactive" and is effectively invisible. Stepping through errors
            // used to scroll with nothing to show which one you had landed on.
            outputSearch.HighlightSingle(new TextRange(link.ContentStart, link.ContentEnd));
            ScrollRangeIntoView(link.ContentStart);
            UpdateErrorNavLabel();
        }

        #endregion

        #region Output search

        private void CompileWindow_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // F9 rather than Esc for cancelling. Esc is the obvious pair to "start", but it is also the
            // key people hit to dismiss things, and using it here means one stray press throws away a
            // compile that may be half an hour in. F9 is what Hammer binds "run map" to, so it is
            // already the right muscle memory, and toggling on the same key makes an accidental cancel
            // take a deliberate second press.
            if (e.Key == Key.F9 && CompileStartStopButton.IsEnabled)
            {
                CompileStartStopButton_OnClick(CompileStartStopButton, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control && AddMapButton.IsEnabled)
            {
                AddMapButton_Click(AddMapButton, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && MainTabControl.SelectedItem == OutputTab)
            {
                ShowOutputSearchBar();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape && OutputSearchBar.Visibility == Visibility.Visible)
            {
                HideOutputSearchBar();
                e.Handled = true;
            }
        }

        private void ShowOutputSearchBar()
        {
            OutputSearchBar.Visibility = Visibility.Visible;
            OutputSearchBox.Focus();
            OutputSearchBox.SelectAll();
        }

        private void HideOutputSearchBar()
        {
            outputSearch.ClearHighlights();
            outputSearchMatches.Clear();
            currentSearchMatchIndex = -1;
            lastSearchQuery = null;

            OutputSearchBar.Visibility = Visibility.Collapsed;
            CompileOutputTextbox.Focus();
        }

        private void OutputSearchCloseButton_OnClick(object sender, RoutedEventArgs e) => HideOutputSearchBar();

        private void OutputSearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            // search live as the query changes, rather than only on Enter/arrow click - otherwise the
            // count label reflects the *previous* query's results (or a blank match list) until the
            // user explicitly navigates, which reads as a false "no matches".
            lastSearchQuery = null;
            currentSearchMatchIndex = -1;
            PerformSearch(forward: true);
        }

        private void OutputSearchBox_OnKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    PerformSearch(forward: Keyboard.Modifiers != ModifierKeys.Shift);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    HideOutputSearchBar();
                    e.Handled = true;
                    break;
            }
        }

        private void OutputSearchNextButton_OnClick(object sender, RoutedEventArgs e) => PerformSearch(forward: true);
        private void OutputSearchPrevButton_OnClick(object sender, RoutedEventArgs e) => PerformSearch(forward: false);

        private void UpdateSearchCountLabel()
        {
            OutputSearchCountLabel.Content = outputSearchMatches.Count == 0
                ? (string.IsNullOrEmpty(OutputSearchBox.Text) ? "" : "No matches")
                : $"{currentSearchMatchIndex + 1}/{outputSearchMatches.Count}";
        }

        private void PerformSearch(bool forward)
        {
            // Search the whole log, including lines still sitting in the append buffer.
            FlushOutput();

            string query = OutputSearchBox.Text;

            if (string.IsNullOrEmpty(query))
            {
                outputSearch.ClearHighlights();
                outputSearchMatches.Clear();
                currentSearchMatchIndex = -1;
                UpdateSearchCountLabel();
                return;
            }

            if (query != lastSearchQuery)
            {
                // Drop the old highlights before re-indexing: they split runs, and the new match
                // list has to be built against a document that is no longer fragmented by them.
                outputSearch.ClearHighlights();
                outputSearchMatches = outputSearch.FindAll(query);
                lastSearchQuery = query;
                currentSearchMatchIndex = -1;
            }

            if (outputSearchMatches.Count == 0)
            {
                outputSearch.ClearHighlights();
                UpdateSearchCountLabel();
                return;
            }

            currentSearchMatchIndex = currentSearchMatchIndex < 0
                ? (forward ? 0 : outputSearchMatches.Count - 1)
                : (currentSearchMatchIndex + (forward ? 1 : -1) + outputSearchMatches.Count) % outputSearchMatches.Count;

            outputSearch.Highlight(outputSearchMatches, currentSearchMatchIndex);

            ScrollRangeIntoView(outputSearchMatches[currentSearchMatchIndex].Start);
            UpdateSearchCountLabel();
        }

        private void ScrollRangeIntoView(TextPointer start)
        {
            Rect rect = start.GetCharacterRect(LogicalDirection.Forward);
            double target = CompileOutputTextbox.VerticalOffset + rect.Top - CompileOutputTextbox.ViewportHeight / 2;
            CompileOutputTextbox.ScrollToVerticalOffset(Math.Max(0, target));
        }

        #endregion
        private void PresetActionButton_OnContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            // block right click context menus
            e.Handled = true;
        }
        private void PresetActionButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.ContextMenu == null)
                return;

            // set placement of context menu to button instead of default behaviour of mouse position
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.IsOpen = true;
            e.Handled = true;
        }
        private void FilterPresetButton_OnChecked(object sender, RoutedEventArgs e)
        {
            var filterChecked = ((bool)(sender as ToggleButton)!.IsChecked)!;
            // prevent unnecessary updates if state didnt change
            if (filterChecked != PresetFilterEnabled)
            {
                PresetFilterEnabled = filterChecked;
                // update filters on sources
                SetSources();
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            new SettingsWindow().Show();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            // load settings on window opening
            try
            {
                // The queue is a left rail now, so the remembered size is its width. Deliberately read
                // from the same setting: a value saved as a row height is a plausible column width, and
                // silently reusing it beats resetting everyone's layout for the sake of a key name.
                var converter = new GridLengthConverter();
                if (ConfigurationManager.Settings.MapListHeight is not null)
                    this.QueueColumn.Width = (GridLength)converter.ConvertFromString(ConfigurationManager.Settings.MapListHeight);
            }
            catch (Exception ex)
            {
                // fail silently, worst case scenario is we use the default height of the list box
                CompilePalLogger.LogLineDebug($"Failed to load settings on startup: {ex}");
            }

            RestoreWindowPlacement();

            base.OnSourceInitialized(e);
        }

        /// <summary>
        /// Puts the window back where it was last closed, if that is still somewhere it can be seen.
        ///
        /// The check is the whole point of this method. A window saved on a second monitor that has
        /// since been unplugged, or on a display whose resolution has dropped, restores to
        /// coordinates that no longer exist - and an off-screen window cannot be dragged back,
        /// because there is nothing to grab. The remedy is a settings file the user has to find and
        /// delete, which is a worse experience than never having remembered the position.
        ///
        /// So the saved rectangle has to overlap the desktop as it is right now. Overlap rather than
        /// containment: a window hanging slightly off the right edge is normal and worth restoring.
        /// </summary>
        private void RestoreWindowPlacement()
        {
            try
            {
                var settings = ConfigurationManager.Settings;

                if (settings.WindowLeft is not { } left || settings.WindowTop is not { } top
                    || settings.WindowWidth is not { } width || settings.WindowHeight is not { } height)
                    return;

                // Nonsense values, from a hand-edited file or a minimised window saved by an older
                // build. MinWidth/MinHeight from XAML are the floor worth honouring.
                if (double.IsNaN(width) || double.IsNaN(height) || width < MinWidth || height < MinHeight)
                    return;

                var saved = new Rect(left, top, width, height);

                var desktop = new Rect(
                    SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

                if (!desktop.IntersectsWith(saved))
                {
                    CompilePalLogger.LogLineDebug(
                        $"Saved window position {saved} is not on any current display; using the default.");
                    return;
                }

                // Manual placement, so WindowStartupLocation does not overwrite it afterwards.
                WindowStartupLocation = WindowStartupLocation.Manual;

                Left = left;
                Top = top;
                Width = width;
                Height = height;

                if (settings.WindowMaximised)
                    WindowState = WindowState.Maximized;
            }
            catch (Exception ex)
            {
                // Never fatal. A window that will not open is a far worse bug than one that opens at
                // the wrong size, and this runs before anything is on screen to report it with.
                CompilePalLogger.LogLineDebug($"Failed to restore the window position: {ex}");
            }
        }

        /// <summary>
        /// Records where the window is, for the next launch.
        ///
        /// RestoreBounds rather than Left/Top/Width/Height, because those describe the maximised
        /// rectangle while the window is maximised. Saving those would mean unmaximising after a
        /// restart snapped the window to full screen size in a restored state, which is not where
        /// the user left it.
        /// </summary>
        private void SaveWindowPlacement()
        {
            var settings = ConfigurationManager.Settings;

            // Minimised has no useful geometry of its own, and RestoreBounds covers both the other
            // states, so it is read in every case.
            var bounds = RestoreBounds;

            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;

            settings.WindowLeft = bounds.Left;
            settings.WindowTop = bounds.Top;
            settings.WindowWidth = bounds.Width;
            settings.WindowHeight = bounds.Height;

            // A window closed while minimised should reopen the way it was before it was minimised,
            // not minimised, so only maximised is worth carrying across.
            settings.WindowMaximised = WindowState == WindowState.Maximized;
        }

        /// <summary>How long the closing path will wait for the usage report to go out.</summary>
        private static readonly TimeSpan TelemetryFlushTimeout = TimeSpan.FromSeconds(3);

        protected override void OnClosing(CancelEventArgs e)
        {
            // save size of map list box on window closing
            try
            {
                var converter = new GridLengthConverter();
                ConfigurationManager.Settings.MapListHeight = converter.ConvertToString(this.QueueColumn.Width);

                // remember which preset was selected so the next launch reopens on it
                ConfigurationManager.Settings.LastPreset = ConfigurationManager.CurrentPreset?.Name;

                SaveWindowPlacement();

                ConfigurationManager.SaveSettings();
            }
            catch (Exception ex)
            {
                // fail silently, worst case scenario is the height of the list box doesnt save
                CompilePalLogger.LogLineDebug($"Failed while saving settings on shutdown: {ex}");
            }

            /*
             * The one and only telemetry send, if the user turned it on.
             *
             * Here rather than spread across the session on purpose: a single summary at the end
             * carries the same totals as a stream of events without also describing when the user
             * was working. See TelemetryManager.
             *
             * Started on the thread pool, then waited on with a bounded Wait.
             *
             * This previously called GetAwaiter().GetResult() directly on the task, on this
             * thread, which is the UI thread. That deadlocks, and it did: FlushAsync awaits the
             * POST, and the continuation after that await is posted back to the dispatcher this
             * line has just blocked. The request goes out, the response arrives, and the
             * continuation waits for a thread that is waiting for the continuation. The 3 second
             * timeout inside FlushAsync cannot save it, because cancelling the request only makes
             * the same continuation runnable on the same blocked thread.
             *
             * The observed symptom was an application that hung on close, having successfully sent
             * the submission first - which is why the endpoint had already logged the request when
             * the window would not shut.
             *
             * Task.Run moves the whole thing to a pool thread with no captured context, so nothing
             * needs the dispatcher; ConfigureAwait(false) inside FlushAsync makes that true even if
             * this is ever called back on the UI thread. The Wait then has a ceiling of its own, so
             * even a flush that ignores its own timeout costs a moment and no more.
             *
             * Dropping the submission is always preferable to not closing.
             */
            try
            {
                Task.Run(() => TelemetryManager.FlushAsync(TelemetryFlushTimeout))
                    .Wait(TelemetryFlushTimeout + TimeSpan.FromSeconds(1));
            }
            catch (Exception ex)
            {
                CompilePalLogger.LogLineDebug($"Telemetry flush failed on shutdown: {ex.Message}");
            }

            base.OnClosing(e);
        }
    }

    public static class ObservableCollectionExtension
	{
		public static ObservableCollection<T> AddRange<T>(this ObservableCollection<T> collection, IEnumerable<T> range)
		{
			foreach (var element in range)
				collection.Add(element);

			return collection;
		}

		public static ObservableCollection<T> RemoveRange<T>(this ObservableCollection<T> collection, IEnumerable<T> range)
		{
			foreach (var element in range)
				collection.Remove(element);

			return collection;
		}
	}
}
