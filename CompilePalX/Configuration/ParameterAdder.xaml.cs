using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
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
    /// Interaction logic for ParameterAdder.xaml
    /// </summary>
    public partial class ParameterAdder
    {
        /// <summary>The parameter to add, or null if the dialog was cancelled.</summary>
        public ConfigItem? ChosenItem;

        private readonly ICollectionView paramView;

        public ParameterAdder(ObservableCollection<ConfigItem> configItems)
        {
            InitializeComponent();

            // A private view rather than CollectionViewSource.GetDefaultView(configItems): the default
            // view is shared with everything else bound to the same collection, so the grouping - and
            // now the search filter - would outlive this dialog and apply wherever else it is shown.
            paramView = new CollectionViewSource { Source = configItems }.View;
            using (paramView.DeferRefresh())
            {
                paramView.GroupDescriptions.Clear();
                paramView.GroupDescriptions.Add(new IsCompatiblePropertyGroup());
                paramView.Filter = MatchesSearch;
            }

            ConfigDataGrid.ItemsSource = paramView;

            Loaded += (_, _) => SearchBox.Focus();
        }

        private bool MatchesSearch(object item)
        {
            var query = SearchBox?.Text;
            if (string.IsNullOrWhiteSpace(query))
                return true;

            if (item is not ConfigItem config)
                return false;

            // Description included on purpose: parameters are frequently looked for by what they do
            // ("hdr", "leak") rather than by the name they happen to carry.
            return Contains(config.Name, query)
                   || Contains(config.Parameter, query)
                   || Contains(config.Description, query);
        }

        private static bool Contains(string? haystack, string needle) =>
            haystack != null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

        private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e) => paramView.Refresh();

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
            // Enter from the search box too, so typing a query and pressing Enter adds the single
            // remaining match without reaching for the mouse.
            if (e.Key != Key.Enter)
                return;

            e.Handled = true;

            if (ConfigDataGrid.SelectedItem == null)
            {
                if (ConfigDataGrid.Items.Count == 0)
                    return;

                // With nothing chosen yet, Enter selects rather than adds: one unambiguous match can be
                // committed straight away, but where the query still matches several, silently adding
                // the top one would be a guess. Highlighting it instead shows what a second Enter takes.
                ConfigDataGrid.SelectedIndex = 0;

                if (ConfigDataGrid.Items.Count > 1)
                {
                    ConfigDataGrid.Focus();
                    return;
                }
            }

            Commit();
        }

        /// <summary>Takes the selection as the result and closes. Does nothing without a selection.</summary>
        private void Commit()
        {
            if (ConfigDataGrid.SelectedItem is not ConfigItem selected)
                return;

            ChosenItem = selected;
            Close();
        }
    }
}
