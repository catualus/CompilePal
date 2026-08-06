using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using CompilePalX.Compiling;
using CompilePalX.Configuration;
using Wpf.Ui.Appearance;

namespace CompilePalX.Theming
{
    /// <summary>
    /// Applies the WPF-UI (Fluent) theme and keeps the legacy MahApps resource keys working.
    ///
    /// The views still reference roughly 150 MahApps.Brushes.* / MahApps.Colors.* keys. Rather than
    /// rewrite every call site - which would be a large, error-prone diff for no visual benefit - those
    /// keys are aliased onto the equivalent WPF-UI theme resources here. Because the aliases are
    /// re-applied whenever the theme changes, views bound with DynamicResource follow light/dark
    /// automatically. New XAML should use the WPF-UI keys directly; this map is a migration aid.
    /// </summary>
    public static class ThemeBridge
    {
        /// <summary>Legacy key -> WPF-UI theme resource key.</summary>
        private static readonly Dictionary<string, string> BrushAliases = new()
        {
            // text
            ["MahApps.Brushes.Text"] = "TextFillColorPrimaryBrush",
            ["MahApps.Brushes.TextDisabled"] = "TextFillColorDisabledBrush",
            ["MahApps.Brushes.ThemeForeground"] = "TextFillColorPrimaryBrush",
            ["MahApps.Brushes.Foreground"] = "TextFillColorPrimaryBrush",
            ["MahApps.Brushes.IdealForeground"] = "TextOnAccentFillColorPrimaryBrush",
            ["MahApps.Brushes.IdealForegroundDisabled"] = "TextOnAccentFillColorDisabledBrush",

            // surfaces
            ["MahApps.Brushes.ThemeBackground"] = "ApplicationBackgroundBrush",
            ["MahApps.Brushes.Gray.SemiTransparent"] = "ControlFillColorSecondaryBrush",
            ["MahApps.Brushes.Gray1"] = "ControlFillColorDefaultBrush",
            ["MahApps.Brushes.Gray3"] = "ControlFillColorSecondaryBrush",
            ["MahApps.Brushes.Gray10"] = "ControlFillColorDisabledBrush",
            ["MahApps.Brushes.Button.Square.Background.MouseOver"] = "SubtleFillColorSecondaryBrush",
            ["MahApps.Brushes.WindowButtonCommands.Background.MouseOver"] = "SubtleFillColorSecondaryBrush",
            ["MahApps.Brushes.WindowButtonCommands.Background.Pressed"] = "SubtleFillColorTertiaryBrush",
            ["MahApps.Brushes.Button.Border.Focus"] = "ControlStrokeColorDefaultBrush",

            // accent
            ["MahApps.Brushes.Accent"] = "AccentFillColorDefaultBrush",
            ["MahApps.Brushes.Accent2"] = "AccentFillColorSecondaryBrush",
            ["MahApps.Brushes.Accent3"] = "AccentFillColorTertiaryBrush",
            ["MahApps.Brushes.Accent4"] = "AccentFillColorTertiaryBrush",
            ["MahApps.Brushes.Highlight"] = "AccentFillColorDefaultBrush",
            ["MahApps.Brushes.WindowTitle"] = "AccentFillColorDefaultBrush",
            ["MahApps.Brushes.CheckmarkFill"] = "AccentFillColorDefaultBrush",
            ["MahApps.Brushes.RightArrowFill"] = "AccentTextFillColorPrimaryBrush",
            ["MahApps.Brushes.Progress"] = "AccentFillColorDefaultBrush",

            // data grid selection
            ["MahApps.Brushes.DataGrid.Selection.Background"] = "AccentFillColorDefaultBrush",
            ["MahApps.Brushes.DataGrid.Selection.Background.Inactive"] = "AccentFillColorSecondaryBrush",
            ["MahApps.Brushes.DataGrid.Selection.Background.MouseOver"] = "AccentFillColorTertiaryBrush",
            ["MahApps.Brushes.DataGrid.Selection.Text.Inactive"] = "TextOnAccentFillColorPrimaryBrush",
        };

        /// <summary>Legacy colour keys, resolved from the brush they correspond to.</summary>
        private static readonly Dictionary<string, string> ColorAliases = new()
        {
            ["MahApps.Colors.Accent"] = "AccentFillColorDefaultBrush",
            ["MahApps.Colors.Accent2"] = "AccentFillColorSecondaryBrush",
            ["MahApps.Colors.Accent3"] = "AccentFillColorTertiaryBrush",
            ["MahApps.Colors.Accent4"] = "AccentFillColorTertiaryBrush",
            ["MahApps.Colors.HighlightColor"] = "AccentFillColorDefaultBrush",
            ["MahApps.Colors.ThemeForeground"] = "TextFillColorPrimaryBrush",
            ["MahApps.Colors.ThemeBackground"] = "ApplicationBackgroundBrush",
            ["MahApps.Colors.IdealForeground"] = "TextOnAccentFillColorPrimaryBrush",
            ["MahApps.Colors.Gray10"] = "ControlFillColorDisabledBrush",
        };

        /// <summary>Applies the theme from settings and refreshes the legacy aliases.</summary>
        public static void Initialize()
        {
            Apply(ConfigurationManager.Settings.Theme);
        }

        /// <summary>Switches theme at runtime. Views using DynamicResource update in place.</summary>
        public static void Apply(AppTheme theme)
        {
            var applicationTheme = theme switch
            {
                AppTheme.Light => ApplicationTheme.Light,
                AppTheme.Dark => ApplicationTheme.Dark,
                _ => ApplicationTheme.Unknown, // follow the OS
            };

            try
            {
                if (applicationTheme == ApplicationTheme.Unknown)
                {
                    ApplicationThemeManager.ApplySystemTheme();
                }
                else
                {
                    ApplicationThemeManager.Apply(applicationTheme, Wpf.Ui.Controls.WindowBackdropType.Mica);
                }
            }
            catch (Exception ex)
            {
                CompilePalLogger.LogLineDebug($"Failed to apply theme {theme}: {ex.Message}");
            }

            RefreshAliases();
        }

        /// <summary>
        /// Points every legacy key at the brush the current theme resolves it to. Must run after each
        /// theme change, since the underlying WPF-UI brushes are replaced wholesale.
        /// </summary>
        private static void RefreshAliases()
        {
            var resources = Application.Current?.Resources;
            if (resources is null)
                return;

            foreach (var (legacyKey, themeKey) in BrushAliases)
            {
                if (resources[themeKey] is Brush brush)
                    resources[legacyKey] = brush;
            }

            foreach (var (legacyKey, themeKey) in ColorAliases)
            {
                if (resources[themeKey] is SolidColorBrush brush)
                    resources[legacyKey] = brush.Color;
            }
        }
    }
}
