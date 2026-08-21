using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace CompilePalX
{
    /// <summary>
    /// Interaction logic for ProcessAdder.xaml
    /// </summary>
    public partial class ProcessAdder
    {
        /// <summary>The step to add, or null if the dialog was cancelled.</summary>
        /// <remarks>internal, not public: CompileProcess itself is internal.</remarks>
        internal CompileProcess? ChosenProcess;

        private readonly ICollectionView processView;

        public ProcessAdder()
        {
            InitializeComponent();

            // A private view, not the default one for ConfigurationManager.CompileProcesses: that
            // collection is shared with the main window, so grouping and filtering it here would leak
            // out of this dialog.
            processView = new CollectionViewSource { Source = ConfigurationManager.CompileProcesses }.View;
            using (processView.DeferRefresh())
            {
                processView.GroupDescriptions.Clear();
                processView.GroupDescriptions.Add(new IsCompatiblePropertyGroup());
                processView.Filter = MatchesSearch;
            }

            ProcessDataGrid.ItemsSource = processView;

            Loaded += (_, _) => SearchBox.Focus();
        }

        private bool MatchesSearch(object item)
        {
            var query = SearchBox?.Text;
            if (string.IsNullOrWhiteSpace(query))
                return true;

            if (item is not CompileProcess process)
                return false;

            return Contains(process.Name, query) || Contains(process.Description, query);
        }

        private static bool Contains(string? haystack, string needle) =>
            haystack != null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

        private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e) => processView.Refresh();

        private void ConfigDataGrid_MouseUp(object sender, MouseButtonEventArgs e)
        {
            // walk up dependency tree to make sure click source was not a group header
            DependencyObject? dep = e.OriginalSource as DependencyObject;
            while ((dep != null) && !(dep is GroupItem) && !(dep is DataGridRow))
            {
                dep = VisualTreeHelper.GetParent(dep);
            }

            // ignore if double click came from group item
            if (dep is GroupItem)
                return;

            Commit();
        }

        private void AddButton_OnClick(object sender, RoutedEventArgs e) => Commit();

        private void CancelButton_OnClick(object sender, RoutedEventArgs e) => Close();

        private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            e.Handled = true;

            if (ProcessDataGrid.SelectedItem == null)
            {
                if (ProcessDataGrid.Items.Count == 0)
                    return;

                // See ParameterAdder: Enter commits an unambiguous match, and otherwise selects the
                // first row and hands focus to the grid rather than guessing.
                ProcessDataGrid.SelectedIndex = 0;

                if (ProcessDataGrid.Items.Count > 1)
                {
                    ProcessDataGrid.Focus();
                    return;
                }
            }

            Commit();
        }

        /// <summary>
        /// Takes the selection as the result and closes.
        ///
        /// The caller used to read the grid's SelectedItem after the dialog closed, which meant closing
        /// the window with its X - having clicked a row on the way past - still added that step. The
        /// result is now only set by an explicit commit.
        /// </summary>
        private void Commit()
        {
            if (ProcessDataGrid.SelectedItem is not CompileProcess selected)
                return;

            ChosenProcess = selected;
            Close();
        }
    }
}
