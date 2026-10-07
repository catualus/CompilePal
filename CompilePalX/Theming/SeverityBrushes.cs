using System.Windows;
using System.Windows.Media;

namespace CompilePalX.Theming
{
    /// <summary>
    /// The colour each error severity is drawn in.
    ///
    /// Lived on Error as GetSeverityBrush, which meant the compile code chose WPF brushes for its own
    /// log lines. It now passes the severity, 0 to 5, and the window turns that into a colour here -
    /// the compile code no longer needs to know what a brush is. See docs/native-port.md.
    /// </summary>
    public static class SeverityBrushes
    {
        /// <summary>The theme's brush for a severity. Reads application resources, so UI thread only.</summary>
        public static Brush For(int severity) => (Brush)Application.Current.TryFindResource(severity switch
        {
            Compiling.CompilePalLogger.Success => "CompilePal.Brushes.Success",
            2 => "CompilePal.Brushes.Severity2",
            3 => "CompilePal.Brushes.Severity3",
            4 => "CompilePal.Brushes.Severity4",
            5 => "CompilePal.Brushes.Severity5",
            _ => "CompilePal.Brushes.Severity1",
        });
    }
}
