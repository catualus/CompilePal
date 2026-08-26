using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace CompilePalX.Configuration
{
    /// <summary>
    /// The last look before a compile that will do something irreversible.
    ///
    /// WHY A DIALOG AT ALL
    ///
    /// Nothing else in Compile Pal asks before running, and it should not: compiling a map is
    /// reversible by compiling it again. What is not reversible is a step that publishes the result
    /// somewhere - to a Workshop, a server, a release - and a step cannot ask anything once the run
    /// has started, because there is nobody watching a compile at the point it matters.
    ///
    /// So a step's status may ask to be confirmed, and one may say the run must not start at all.
    /// This window is both: a list of what will happen, and, when something is blocking, a refusal
    /// with the reason next to the map it belongs to.
    ///
    /// It is shown only when a step asks for it. A queue with nothing to confirm never sees it.
    /// </summary>
    public partial class PreCompileWindow
    {
        /// <summary>Whether the user chose to go ahead.</summary>
        public bool Proceed { get; private set; }

        public PreCompileWindow(IReadOnlyList<PluginMapStatus> statuses)
        {
            InitializeComponent();

            // Blocking first, then warnings: the reason someone cannot start should not be below the
            // fold of a list of things that are merely worth knowing.
            var ordered = statuses
                .OrderByDescending(s => s.Severity)
                .ThenBy(s => s.MapName)
                .ToList();

            StatusList.ItemsSource = ordered;

            var blocking = ordered.Where(s => s.Severity == StatusSeverity.Blocking).ToList();

            if (blocking.Count > 0)
            {
                Title = "Cannot start this compile";
                HeadlineText.Text = blocking.Count == 1
                    ? $"{blocking[0].MapName} is not ready to compile."
                    : $"{blocking.Count} maps are not ready to compile.";

                SubText.Text = "Deal with the reason below, or take the map out of the queue.";
                FootnoteText.Text = "Nothing has been compiled.";

                ProceedButton.IsEnabled = false;
                ProceedButton.Content = "Compile";
                return;
            }

            int maps = ordered.Select(s => s.MapName).Distinct().Count();

            Title = "Before compiling";
            HeadlineText.Text = maps == 1
                ? "This compile will do something that cannot be undone."
                : $"This compile will do something that cannot be undone, on {maps} maps.";

            SubText.Text = "Everything a step wants confirmed is listed below.";
            FootnoteText.Text = "Cancel changes nothing.";
        }

        private void ProceedButton_Click(object sender, RoutedEventArgs e)
        {
            Proceed = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Proceed = false;
            Close();
        }
    }
}
