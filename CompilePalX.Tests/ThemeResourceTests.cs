using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// CompilePalTheme.xaml used to declare four aliases as <c>&lt;StaticResource x:Key=... /&gt;</c>
    /// elements. Compiled to BAML, those scrambled the dictionary's keys: the aliases themselves and
    /// <c>CompilePal.Brushes.Severity5</c>, declared just before them, were missing once loaded, and
    /// the severity 5 brush turned up under the last alias's key instead.
    ///
    /// Nothing failed. Every lookup of the severity 5 brush quietly returned null, and a Run given a
    /// null Foreground draws nothing - so a fatal error, the one message that stops a compile, was
    /// printed into the log invisibly. "Something broke:" followed by blank lines, in every release
    /// from 1.0.0.
    ///
    /// Loads the compiled dictionary, as the app does, rather than parsing the XAML, because the XAML
    /// was always correct.
    /// </summary>
    public class ThemeResourceTests
    {
        private static string SourceDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "CompilePalX", "CompilePalTheme.xaml");
                if (File.Exists(candidate))
                    return Path.Combine(dir.FullName, "CompilePalX");

                dir = dir.Parent;
            }

            throw new InvalidOperationException($"Could not find CompilePalX sources above {AppContext.BaseDirectory}");
        }

        // Static LoadComponent, not a pack URI on a new ResourceDictionary: creating an Application to
        // make pack URIs resolve would change Application.Current for every other test in the run.
        private static ResourceDictionary LoadTheme() =>
            (ResourceDictionary)Application.LoadComponent(
                new Uri("/CompilePalX;component/CompilePalTheme.xaml", UriKind.Relative));

        [WpfFact]
        public void EveryDeclaredKeySurvivesLoading()
        {
            var declared = Regex.Matches(
                    File.ReadAllText(Path.Combine(SourceDir(), "CompilePalTheme.xaml")),
                    "x:Key=\"([^\"{]+)\"")
                .Select(m => m.Groups[1].Value)
                .ToList();

            Assert.NotEmpty(declared);

            var theme = LoadTheme();
            var missing = declared.Where(key => !theme.Contains(key)).ToList();
            Assert.True(missing.Count == 0, "Missing once loaded: " + string.Join(", ", missing));
        }

        [WpfTheory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void EachSeverityBrushIsItsOwnColour(int severity)
        {
            var theme = LoadTheme();

            var brush = Assert.IsType<SolidColorBrush>(theme[$"CompilePal.Brushes.Severity{severity}"]);
            Assert.Equal((Color)theme[$"CompilePal.Colors.Severity{severity}"], brush.Color);
        }
    }
}
