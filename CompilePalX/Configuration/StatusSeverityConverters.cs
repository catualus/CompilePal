using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CompilePalX.Configuration
{
    /// <summary>
    /// Paints a plugin's status chip.
    ///
    /// Reuses the log's severity brushes rather than inventing colours, so a chip that says something
    /// needs attention is the same colour as the warning it will turn into in the output. Severity 2
    /// is the log's warning, 4 its error; ok and info stay in the ordinary text colours because most
    /// chips just say what a map is bound to.
    /// </summary>
    public sealed class StatusSeverityBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string key = value is StatusSeverity severity
                ? severity switch
                {
                    StatusSeverity.Blocking => "CompilePal.Brushes.Severity4",
                    StatusSeverity.Warn => "CompilePal.Brushes.Severity2",
                    StatusSeverity.Info => "TextFillColorSecondaryBrush",
                    _ => "TextFillColorTertiaryBrush",
                }
                : "TextFillColorTertiaryBrush";

            return Application.Current?.TryFindResource(key) as Brush
                   ?? (Brush)new SolidColorBrush(Colors.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Collapses a chip row when the plugin had nothing to say.</summary>
    public sealed class EmptyToCollapsedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool empty = value switch
            {
                null => true,
                string text => text.Length == 0,
                System.Collections.ICollection collection => collection.Count == 0,
                _ => false,
            };

            return empty ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
