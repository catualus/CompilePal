using System;
using System.Collections.Generic;
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
    /// Interaction logic for PresetDialog.xaml
    /// </summary>
    public partial class PresetDialog
    {
        public bool Result = false;
        public bool IsMapSpecific { get { return IsMapSpecificCheckbox.IsChecked ?? false; } }
        /// <summary>The preset being edited, whose own name is not a clash. Null when adding or cloning.</summary>
        private readonly Preset? replacing;

        public PresetDialog(string title, Map? selectedMap, Preset? preset = null, Preset? replacing = null)
        {
            InitializeComponent();
            this.replacing = replacing;
            Title = title;

            string? mapRegex = null;
            if (selectedMap is not null)
            {
                mapRegex = $"{selectedMap.MapName}.*";
            }

            if (preset != null)
            {
                DataContext = preset;
                IsMapSpecificCheckbox.IsChecked = preset.Map != null;
            }
            else
            {
                DataContext = new Preset()
                {
                    Name = "",
                    Map = selectedMap?.MapName,
                    MapRegex = mapRegex
                };
            }
        }

        private void OKButton_OnClick(object sender, RoutedEventArgs e)
        {
            var preset = (Preset)DataContext;

            // clear map if not map specific
            if (!IsMapSpecific)
            {
                preset.MapRegex = null;
                preset.Map = null;
            }

            // Checked here so the user can fix it without losing what they typed. The same check runs
            // again where the preset is written, which is the one that actually protects the folder.
            if (ConfigurationManager.PresetProblem(preset, replacing) is { } problem)
            {
                ProblemText.Text = problem;
                ProblemText.Visibility = Visibility.Visible;
                return;
            }

            Result = true;
            Close();
        }

        private void CancelButton_OnClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
