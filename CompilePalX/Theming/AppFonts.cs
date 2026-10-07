using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace CompilePalX.Theming
{
    /// <summary>
    /// The fonts the application draws with, each ending in one that ships inside it.
    ///
    /// WPF does not survive having no font at all. When every family in an element's list is
    /// missing it falls back to its built-in composite font, and when that finds nothing either it
    /// calls FailFast - the process is gone before a single exception handler runs. On Windows that
    /// never happens, because Segoe UI is always there. Under Wine it is the first thing that happens:
    /// a fresh prefix has almost no fonts in C:\windows\Fonts, which is the only place WPF looks, and
    /// Compile Pal died on its launch window before drawing anything.
    ///
    /// So each list ends in Liberation Sans or Liberation Mono, embedded as resources (SIL Open Font
    /// License, see Fonts/OFL.txt). They are last, so on Windows nothing changes: Segoe UI and Cascadia
    /// are found first exactly as before. They only ever draw when nothing ahead of them exists.
    ///
    /// The XAML side is the CompilePal.Fonts.* resources in App.xaml, which also override Wpf.Ui's
    /// ContentControlThemeFontFamily so its controls get the same fallback. This class is for the two
    /// places a family is built from a string at run time: the output font setting, and its preview.
    /// </summary>
    public static class AppFonts
    {
        /// <summary>Where "./Fonts/" in a family list resolves from: the application's own resources.</summary>
        private static readonly Uri ApplicationRoot = new("pack://application:,,,/");

        /// <summary>The embedded monospace font, as an entry for the end of a family list.</summary>
        public const string BundledMono = "./Fonts/#Liberation Mono";

        /// <summary>What the output pane uses when the setting is empty or unusable.</summary>
        public const string DefaultMono = "Cascadia Mono, Cascadia Code, Consolas, Courier New";

        /// <summary>
        /// A monospace family from a comma-separated list - typically the user's output font setting -
        /// with the embedded font appended, so there is always something to draw with.
        /// </summary>
        public static FontFamily Mono(string? families)
        {
            string list = string.IsNullOrWhiteSpace(families) ? DefaultMono : families.Trim().TrimEnd(',');

            try
            {
                return new FontFamily(ApplicationRoot, $"{list}, {BundledMono}");
            }
            catch (ArgumentException)
            {
                // The setting is free text; a malformed one should not cost the output pane its font.
                return new FontFamily(ApplicationRoot, $"{DefaultMono}, {BundledMono}");
            }
        }
    }

    /// <summary>Binds a family-list string - the output font picker's text - through <see cref="AppFonts.Mono"/>.</summary>
    public sealed class MonoFontConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => AppFonts.Mono(value as string);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
