using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using CompilePalX.Compiling;
using CompilePalX.Configuration;

namespace CompilePalX
{
    /// <summary>
    /// Interaction logic for Settings.xaml
    /// </summary>
    public partial class SettingsWindow
    {
        public SettingsWindow()
        {
            this.DataContext = ConfigurationManager.Settings.Clone();
            InitializeComponent();
        }

        private static List<string>? monospaceFonts;

        /// <summary>
        /// Monospace families installed on this machine, for the output font picker.
        ///
        /// The setting used to be a free-text box wanting a comma-separated XAML FontFamily fallback
        /// list. Compiler output is column-aligned with spaces - VBSP's lump report and VVIS's progress
        /// bars - so a proportional font silently ruins it, which made a free-text box the wrong control
        /// for the job twice over.
        ///
        /// Enumerated once: this walks every installed family and measures glyphs, which is slow enough
        /// to be worth not repeating each time the window opens.
        /// </summary>
        public List<string> MonospaceFonts => monospaceFonts ??= FindMonospaceFonts();

        private static List<string> FindMonospaceFonts()
        {
            var names = new List<string>();

            foreach (var family in Fonts.SystemFontFamilies)
            {
                try
                {
                    if (!family.GetTypefaces().Any(IsMonospace))
                        continue;

                    string name = family.Source;
                    if (!string.IsNullOrWhiteSpace(name))
                        names.Add(name);
                }
                catch
                {
                    // A family whose typefaces cannot be read is one we cannot vouch for; skipping it
                    // is better than failing to offer any font at all.
                }
            }

            names.Sort(StringComparer.CurrentCultureIgnoreCase);
            return names;
        }

        /// <summary>
        /// A typeface is monospace when every glyph advances by the same width.
        ///
        /// Checked by measuring rather than by name: "Cascadia Mono" and "Consolas" would be easy to
        /// match on, but so would "Monotype Corsiva", which is not remotely fixed-width.
        /// </summary>
        private static bool IsMonospace(Typeface typeface)
        {
            if (!typeface.TryGetGlyphTypeface(out var glyphs))
                return false;

            // 'i' and 'W' are the widest-apart pair in a proportional face and identical in a fixed one.
            if (!glyphs.CharacterToGlyphMap.TryGetValue('i', out ushort narrow) ||
                !glyphs.CharacterToGlyphMap.TryGetValue('W', out ushort wide))
                return false;

            return Math.Abs(glyphs.AdvanceWidths[narrow] - glyphs.AdvanceWidths[wide]) < 0.0001;
        }

        /// <summary>
        /// Prints the exact submission that would be sent right now.
        ///
        /// Generated from the same state the send uses rather than written out by hand, so it
        /// cannot drift from the truth - which is the entire point of offering it.
        /// </summary>
        private async void ShowTelemetryPayload_Click(object sender, RoutedEventArgs e)
        {
            // Save first: the toggle and endpoint are bound to the DataContext copy, and the
            // payload is built from ConfigurationManager.Settings. Without this the dialog
            // describes the settings as they were when the window opened.
            var pending = (Settings)this.DataContext;
            ConfigurationManager.Settings.TelemetryEnabled = pending.TelemetryEnabled;

            // A build without an endpoint compiled in collects and then discards. Saying so is
            // better than showing a payload it will never send and letting the toggle imply
            // otherwise - this is what an unofficial build or a local clone will see.
            var note = TelemetryManager.CanReport
                ? ""
                : "This build has no reporting endpoint compiled in, so nothing is sent no " +
                  "matter what this setting says. Official releases do." + Environment.NewLine +
                  Environment.NewLine + "It would otherwise send:" + Environment.NewLine + Environment.NewLine;

            await Theming.AppDialog.ShowAsync(
                "What Compile Pal would send",
                note + TelemetryManager.DescribePayload(),
                closeText: "Close");
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var settings = (Settings)this.DataContext;

            // The font size box is bound to a double and clamped by the control, but a family typed by
            // hand into the editable combo can still be nonsense. An empty one would leave the output
            // rendering in WPF's document default, so fall back rather than accept it.
            if (string.IsNullOrWhiteSpace(settings.OutputFontFamily))
                settings.OutputFontFamily = "Cascadia Mono, Cascadia Code, Consolas, Courier New";

            // Read before the save replaces Settings, so "was it on a moment ago" is still answerable.
            bool wasEnabled = ConfigurationManager.Settings.TelemetryEnabled;

            ConfigurationManager.SaveSettings(settings);

            // Switching reporting off discards this session's counters rather than leaving them
            // in memory. They would not have been sent - the flush checks the setting too - but
            // holding onto them after the user has just declined is not the right answer.
            if (wasEnabled && !settings.TelemetryEnabled)
                TelemetryManager.Discard();

            // the tools++ override may have changed, so cached verdicts are no longer valid
            ToolsPlusPlusDetector.Invalidate();

            // appearance applies immediately rather than needing a restart
            Theming.ThemeBridge.Apply(ConfigurationManager.Settings.Theme);

            Close();
        }

        /// <summary>
        /// Closes without saving.
        ///
        /// The window had no way out but Save or the close button, and since it edits a clone the close
        /// button already discarded changes - it just never said so.
        /// </summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
