using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace CompilePalX
{
    /// <summary>
    /// Live "will use tools++ instead" hint for the game configuration binary fields. The configured
    /// VBSP/VVIS/VRAD/BSPZip path is what's shown and saved, but <see cref="ToolsPlusPlusDetector"/> can
    /// silently substitute a tools++ build found next to it at compile time - this surfaces that swap in
    /// the field itself instead of leaving it invisible.
    /// </summary>
    public class ToolsPlusPlusHintConverter : IValueConverter
    {
        /// <summary>ConverterParameter must be the process name: "VBSP", "VVIS", "VRAD", or "BSPZIP".</summary>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string? configuredPath = value as string;
            string? processName = parameter as string;

            if (string.IsNullOrWhiteSpace(configuredPath) || processName is null)
                return string.Empty;

            string? resolved = ToolsPlusPlusDetector.PreviewResolveBinary(processName, configuredPath);

            if (resolved is null || string.Equals(resolved, configuredPath, StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            return $"tools++ found - will run \"{Path.GetFileName(resolved)}\" from {Path.GetDirectoryName(resolved)} instead of the path above";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
