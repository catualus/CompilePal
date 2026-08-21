using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace CompilePalX.Configuration
{
	/// <summary>
	/// Side-by-side comparison of two presets.
	/// </summary>
	public partial class PresetDiffWindow
	{
		private readonly List<CompileProcess> processes;

		internal PresetDiffWindow(IEnumerable<Preset> presets, Preset? initial, IEnumerable<CompileProcess> processes)
		{
			InitializeComponent();

			this.processes = processes.ToList();

			var all = presets.ToList();
			LeftBox.ItemsSource = all;
			RightBox.ItemsSource = all;

			LeftBox.SelectedItem = initial ?? all.FirstOrDefault();

			// Opens on a pair rather than on the same preset twice, which would only ever show an empty
			// list and leave the user to work out that they need to change one side.
			RightBox.SelectedItem = all.FirstOrDefault(p => !p.Equals(LeftBox.SelectedItem)) ?? all.FirstOrDefault();

			Refresh();
		}

		private void Selection_OnChanged(object sender, RoutedEventArgs e) => Refresh();

		private void Refresh()
		{
			// Called from SelectionChanged during InitializeComponent's own bindings too, before both
			// boxes have a value.
			if (DiffGrid == null || LeftBox.SelectedItem is not Preset left || RightBox.SelectedItem is not Preset right)
				return;

			var rows = PresetDiff.Compare(processes
				.OrderBy(p => p.Ordering)
				.Select(p => new PresetStepComparison
				{
					Step = p.Name,
					Left = p.PresetDictionary.ContainsKey(left) ? p.PresetDictionary[left] : null,
					Right = p.PresetDictionary.ContainsKey(right) ? p.PresetDictionary[right] : null,
				}));

			if (DifferencesOnly.IsChecked == true)
				rows = rows.Where(r => r.Differs).ToList();

			DiffGrid.ItemsSource = rows;

			bool empty = rows.Count == 0;
			EmptyMessage.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
			DiffGrid.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

			EmptyMessage.Text = left.Equals(right)
				? "Pick two different presets to compare."
				: "These two presets are identical.";
		}
	}
}
