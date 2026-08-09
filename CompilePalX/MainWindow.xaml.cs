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
        private void OnPropertyChanged(string name)
        {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AddCustomParameterButtonEnabled)));
        }

        public static Dispatcher ActiveDispatcher;
        private ObservableCollection<CompileProcess> CompileProcessesSubList = [];
	    private bool _processModeEnabled;
	    private bool processModeEnabled
        {
            get => _processModeEnabled;
            set {
                if (value == _processModeEnabled)
                    return;

                _processModeEnabled = value;
                OnPropertyChanged(nameof(AddCustomParameterButtonEnabled));
            }
        }

        public bool PresetFilterEnabled { get; set; } = true;

        private DispatcherTimer elapsedTimeDispatcherTimer;

        private readonly List<Hyperlink> outputErrorLinks = [];
        private int currentErrorIndex = -1;

        private List<TextRange> outputSearchMatches = [];
        private int currentSearchMatchIndex = -1;
        private string? lastSearchQuery;

        // Created once the XAML document exists; see the constructor.
        private OutputSearch outputSearch = null!;

        /// <summary>
        /// The document changed, so the search index and any painted highlights are stale.
        /// Called from every place that appends to or rewrites OutputParagraph.
        /// </summary>
        private void InvalidateOutputSearchIndex() => outputSearch?.Invalidate();

		public static MainWindow? Instance { get; private set; }
        public ObservableCollection<Preset> Presets;

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
                OnPropertyChanged(nameof(AddCustomParameterButtonEnabled));
            }
        }

        public bool AddCustomParameterButtonEnabled { get => !IsCompiling && !processModeEnabled && selectedProcess != null && selectedProcess.SupportsCustomParameters; }

		public MainWindow()
        {
	        Instance = this;

			Application.Current.DispatcherUnhandledException += Current_DispatcherUnhandledException;

            InitializeComponent();

            ActiveDispatcher = Dispatcher;

            // After InitializeComponent so the XAML-declared FlowDocument exists.
            outputSearch = new OutputSearch(CompileOutputTextbox.Document);

            CompilePalLogger.OnWrite += Logger_OnWrite;
            CompilePalLogger.OnBacktrack += Logger_OnBacktrack;
            CompilePalLogger.OnErrorLog += CompilePalLogger_OnError;
            CompilePalLogger.OnWriteURL += CompilePalLogger_OnWriteFileLocation;

            UpdateManager.OnUpdateFound += UpdateManager_OnUpdateFound;
            UpdateManager.CheckVersion();

            AnalyticsManager.Launch();
            PersistenceManager.Init();
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

            CompilingManager.OnStart += CompilingManager_OnStart;
            CompilingManager.OnFinish += CompilingManager_OnFinish;

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
                }

                var underline = new TextDecoration
                {
                    Location = TextDecorationLocation.Underline,
                    Pen = new Pen(e.ErrorColor, 1),
                    PenThicknessUnit = TextDecorationUnit.FontRecommended
                };

                errorLink.TextDecorations = new TextDecorationCollection([underline]);

                OutputParagraph.Inlines.Add(errorLink);
                InvalidateOutputSearchIndex();
                CompileOutputTextbox.ScrollToEnd();

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

                OutputParagraph.Inlines.Add(textRun);
                InvalidateOutputSearchIndex();

                // scroll to end only if already scrolled to the bottom. 1.0 is an epsilon value for double comparison
                if (CompileOutputTextbox.VerticalOffset + CompileOutputTextbox.ViewportHeight >= CompileOutputTextbox.ExtentHeight - 1.0)
                    CompileOutputTextbox.ScrollToEnd();

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

                OutputParagraph.Inlines.Add(link);
                InvalidateOutputSearchIndex();

                // scroll to end only if already scrolled to the bottom. 1.0 is an epsilon value for double comparison
                if (CompileOutputTextbox.VerticalOffset + CompileOutputTextbox.ViewportHeight >= CompileOutputTextbox.ExtentHeight - 1.0)
                    CompileOutputTextbox.ScrollToEnd();

                return textRun;
            });
        }

        private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start("explorer", $"/select, \"{e.Uri}\"");
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
            Title = $"Compile Pal {UpdateManager.CurrentVersion}X {gameConfiguration.Name}";

            PresetConfigListBox.Items.Refresh();
            ConfigDataGrid.Items.Refresh();
            CompileProcessesListBox.Items.Refresh();

            // reload parameters incase new game config has a plugin folder
            ConfigurationManager.AssembleParameters();
            AnalyticsManager.SelectGameConfiguration(gameConfiguration.Name);
        }

        void ProgressManager_ProgressChange(double progress)
        {
            CompileProgressBar.Value = progress;

            if (progress < 0 || progress >= 100)
                CompileStartStopButton.Content = "Compile";
        }

        void ProgressManager_TitleChange(string title)
        {
            Title = title;
        }


        void CompilingManager_OnClear()
        {
            Dispatcher.Invoke(() =>
            {
                OutputParagraph.Inlines.Clear();

                outputErrorLinks.Clear();
                currentErrorIndex = -1;
                UpdateErrorNavLabel();

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
            IsCompiling = true;

            ConfigDataGrid.IsEnabled = false;
            ProcessDataGrid.IsEnabled = false;
	        OrderGrid.IsEnabled = false;

            AddParameterButton.IsEnabled = false;
            RemoveParameterButton.IsEnabled = false;

            AddProcessesButton.IsEnabled = false;
            RemoveProcessesButton.IsEnabled = false;
            CompileProcessesListBox.IsEnabled = false;

            AddPresetButton.IsEnabled = false;
            FilterPresetButton.IsEnabled = false;
            PresetConfigListBox.IsEnabled = false;

            AddMapButton.IsEnabled = false;
            RemoveMapButton.IsEnabled = false;

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

			//If process grid is enabled, disable config grid
            ConfigDataGrid.IsEnabled = !processModeEnabled;
            ProcessDataGrid.IsEnabled = processModeEnabled;
	        OrderGrid.IsEnabled = true;

            AddParameterButton.IsEnabled = true;
            RemoveParameterButton.IsEnabled = true;

            AddProcessesButton.IsEnabled = true;
            RemoveProcessesButton.IsEnabled = true;
            CompileProcessesListBox.IsEnabled = true;

            AddPresetButton.IsEnabled = true;
            FilterPresetButton.IsEnabled = true;
            PresetConfigListBox.IsEnabled = true;

            AddMapButton.IsEnabled = true;
            RemoveMapButton.IsEnabled = true;

            TimeElapsedLabel.Visibility = Visibility.Collapsed;
            elapsedTimeDispatcherTimer.IsEnabled = false;

            string logName = DateTime.Now.ToString("s").Replace(":", "-") + ".txt";
            string textLog = new TextRange(CompileOutputTextbox.Document.ContentStart, CompileOutputTextbox.Document.ContentEnd).Text;

            if (!Directory.Exists("CompileLogs"))
                Directory.CreateDirectory("CompileLogs");

            File.WriteAllText(System.IO.Path.Combine("CompileLogs", logName), textLog);

            CompileStartStopButton.Content = "Compile";

            ProgressManager.SetProgress(1);
        }

        private void OnConfigChanged(object sender, RoutedEventArgs e)
        {
            UpdateParameterTextBox();
            ConfigurationManager.MarkDirty(ConfigurationManager.CurrentPreset);
        }

        private void AddParameterButton_Click(object sender, RoutedEventArgs e)
        {
            if (selectedProcess != null)
            {
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

                AnalyticsManager.ModifyPreset();

                UpdateParameterTextBox();
            }
        }

        private void RemoveParameterButton_OnClickParameterButton_Click(object sender, RoutedEventArgs e)
        {
	        ConfigItem selectedItem;
	        if (processModeEnabled)
		        selectedItem = (ConfigItem) ProcessDataGrid.SelectedItem;
	        else
				selectedItem = (ConfigItem) ConfigDataGrid.SelectedItem;
            
            if (selectedItem != null)
                selectedProcess.PresetDictionary[ConfigurationManager.CurrentPreset].Remove(selectedItem);

            UpdateParameterTextBox();
        }
        private void AddCustomParameterButton_Click(object sender, RoutedEventArgs e)
        {
            if (selectedProcess != null)
            {
                var customArgumentItem = selectedProcess.ParameterList.FirstOrDefault(i => i.Name == "Command Line Argument");
                selectedProcess.PresetDictionary[ConfigurationManager.CurrentPreset].Add((ConfigItem)customArgumentItem.Clone());
            }
            AnalyticsManager.ModifyPreset();

            UpdateParameterTextBox();
        }

        private void AddProcessButton_Click(object sender, RoutedEventArgs e)
        {
            ProcessAdder c = new ProcessAdder();
            c.ShowDialog();

            if (c.ProcessDataGrid.SelectedItem != null)
            {
                CompileProcess chosenProcess = (CompileProcess)c.ProcessDataGrid.SelectedItem;
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

            AnalyticsManager.ModifyPreset();
            ConfigurationManager.MarkDirty(ConfigurationManager.CurrentPreset);

            UpdateParameterTextBox();
            UpdateProcessList();

			if (processModeEnabled)
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

            AnalyticsManager.NewPreset();

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

            AnalyticsManager.NewPreset();

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

			if (processModeEnabled)
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
            if (selectedMap.Preset != null && selectedMap.Preset.Equals((Preset)PresetConfigListBox.SelectedItem))
                return;

            // update map's selected preset
            if (PresetConfigListBox.SelectedItem is Preset preset)
                selectedMap.Preset = preset;
            else
                PresetConfigListBox.SelectedIndex = 0;
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
                OnPropertyChanged(nameof(AddCustomParameterButtonEnabled));
            }
        }

        private void UpdateConfigGrid()
        {
            ConfigurationManager.CurrentPreset = (Preset)PresetConfigListBox.SelectedItem;

            selectedProcess = (CompileProcess)CompileProcessesListBox.SelectedItem;

            if (selectedProcess != null && ConfigurationManager.CurrentPreset != null && selectedProcess.PresetDictionary.ContainsKey(ConfigurationManager.CurrentPreset))
            {
                //Switch to the process grid for custom program screen
                if (selectedProcess.Name == "CUSTOM")
                {
                    ProcessDataGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(50))));
                    processModeEnabled = true;

                    ProcessDataGrid.ItemsSource = selectedProcess.PresetDictionary[ConfigurationManager.CurrentPreset];

                    ConfigDataGrid.IsEnabled = false;
                    ConfigDataGrid.Visibility = Visibility.Hidden;
                    ParametersTextBox.Visibility = Visibility.Hidden;

                    ProcessDataGrid.IsEnabled = true;
                    ProcessDataGrid.Visibility = Visibility.Visible;

                    ProcessTab.IsEnabled = true;
                    ProcessTab.Visibility = Visibility.Visible;

                    //Hide parameter buttons if ORDER is the current tab
                    if ((string)(ProcessTab.SelectedItem as TabItem)?.Header == "ORDER")
                    {
                        AddParameterButton.Visibility = Visibility.Hidden;
                        AddParameterButton.IsEnabled = false;

                        RemoveParameterButton.Visibility = Visibility.Hidden;
                        RemoveParameterButton.IsEnabled = false;

                        AddCustomParameterButton.Visibility = Visibility.Hidden;
                    }
                }
                else
                {
                    ConfigDataGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(50))));
                    processModeEnabled = false;

                    ConfigDataGrid.IsEnabled = true;
                    ConfigDataGrid.Visibility = Visibility.Visible;
                    ParametersTextBox.Visibility = Visibility.Visible;

                    ProcessDataGrid.IsEnabled = false;
                    ProcessDataGrid.Visibility = Visibility.Hidden;

                    ProcessTab.IsEnabled = false;
                    ProcessTab.Visibility = Visibility.Hidden;

                    ConfigDataGrid.ItemsSource = selectedProcess.PresetDictionary[ConfigurationManager.CurrentPreset];

                    //Make buttons visible if they were disabled
                    if (!AddParameterButton.IsEnabled)
                    {
                        AddParameterButton.Visibility = Visibility.Visible;
                        AddParameterButton.IsEnabled = true;

                        RemoveParameterButton.Visibility = Visibility.Visible;
                        RemoveParameterButton.IsEnabled = true;

                        AddCustomParameterButton.Visibility = Visibility.Visible;
                    }

                    UpdateParameterTextBox();
                }


            }
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

        void UpdateParameterTextBox()
        {
            if (selectedProcess != null)
                ParametersTextBox.Text = selectedProcess.GetParameterString();
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

            foreach (var file in dialog.FileNames)
            {
                // use current preset if it matches the map, otherwise default to first
                CompilingManager.MapFiles.Add(new Map(file, preset: ConfigurationManager.CurrentPreset != null && ConfigurationManager.CurrentPreset.IsValidMap(file) ? ConfigurationManager.CurrentPreset : ConfigurationManager.KnownPresets.FirstOrDefault()));
            }
        }

        private void RemoveMapButton_Click(object sender, RoutedEventArgs e)
        {
            if (MapListBox.SelectedItem is Map selectedMap)
                CompilingManager.MapFiles.Remove(selectedMap);
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
			Process.Start(new ProcessStartInfo("http://www.github.com/ruarai/CompilePal/releases/latest") { UseShellExecute = true });
        }

	    private void ProcessTab_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
	    {
			if (e.Source is TabControl)
				OrderManager.UpdateOrder();

			if (OrderTab.IsSelected)
		    {
				AddParameterButton.Visibility = Visibility.Hidden;
				AddParameterButton.IsEnabled = false;

				RemoveParameterButton.Visibility = Visibility.Hidden;
				RemoveParameterButton.IsEnabled = false;

                AddCustomParameterButton.Visibility = Visibility.Hidden;
            }
		    else
		    {
				AddParameterButton.Visibility = Visibility.Visible;
				AddParameterButton.IsEnabled = true;

				RemoveParameterButton.Visibility = Visibility.Visible;
				RemoveParameterButton.IsEnabled = true;

                AddCustomParameterButton.Visibility = Visibility.Visible;
            }
		}

        private void MapListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // clear config datagrid so no stale data is shown
            ConfigDataGrid.ItemsSource = null;

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
			if (processModeEnabled)
				OrderManager.UpdateOrder();

			ConfigurationManager.MarkProcessesDirty();
		}

	    private void OrderGrid_OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
	    {
			if (processModeEnabled)
				OrderManager.UpdateOrder();
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


		//Search through ProcDataGrid to find corresponding ConfigItem
		private ConfigItem? GetConfigFromCustomProgram(CustomProgram program)
	    {
            if (ProcessDataGrid.ItemsSource is null)
                return null;

			foreach (var procSourceItem in ProcessDataGrid.ItemsSource)
			{
				if (program.Equals(procSourceItem))
				{
					return procSourceItem as ConfigItem;
				}
			}

			//Return null on failure
		    return null;
	    }

		private void UpdateHyperLink_OnRequestNavigate(object sender, RequestNavigateEventArgs e)
		{
			Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
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
			Process.Start(new ProcessStartInfo("https://github.com/ruarai/CompilePal/issues/") { UseShellExecute = true });
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
            Clipboard.SetText(new TextRange(CompileOutputTextbox.Document.ContentStart, CompileOutputTextbox.Document.ContentEnd).Text);
        }

        private void SaveLogButton_OnClick(object sender, RoutedEventArgs e)
        {
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
                var converter = new GridLengthConverter();
                if (ConfigurationManager.Settings.MapListHeight is not null)
                    this.MapListBoxRow.Height = (GridLength)converter.ConvertFromString(ConfigurationManager.Settings.MapListHeight);
            }
            catch (Exception ex)
            {
                // fail silently, worst case scenario is we use the default height of the list box
                CompilePalLogger.LogLineDebug($"Failed to load settings on startup: {ex}");
            }

            base.OnSourceInitialized(e);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // save size of map list box on window closing
            try
            {
                var converter = new GridLengthConverter();
                ConfigurationManager.Settings.MapListHeight = converter.ConvertToString(this.MapListBoxRow.Height);

                // remember which preset was selected so the next launch reopens on it
                ConfigurationManager.Settings.LastPreset = ConfigurationManager.CurrentPreset?.Name;

                ConfigurationManager.SaveSettings();
            }
            catch (Exception ex)
            {
                // fail silently, worst case scenario is the height of the list box doesnt save
                CompilePalLogger.LogLineDebug($"Failed while saving settings on shutdown: {ex}");
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
