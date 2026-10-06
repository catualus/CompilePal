using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CompilePalX.Platform;
using CompilePalX.Theming;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// The pieces that let Compile Pal run on Linux under Wine. The behaviour itself is checked by
    /// running it under Wine; these pin the parts that can be reasoned about on any machine.
    /// </summary>
    public class LinuxSupportTests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "CompilePalX.sln")))
                    return dir.FullName;
                dir = dir.Parent!;
            }

            throw new InvalidOperationException($"Could not find the repository above {AppContext.BaseDirectory}");
        }

        [Fact]
        public void WinesHomeVariableLosesItsNtPrefix()
        {
            Assert.Equal(@"Z:\home\you", Wine.FromNtPath(@"\??\Z:\home\you"));
            Assert.Equal(@"Z:\home\you", Wine.FromNtPath(@"Z:\home\you"));
            Assert.Null(Wine.FromNtPath(null));
            Assert.Null(Wine.FromNtPath("  "));
        }

        /// <summary>Steam for Linux lists its libraries as Unix paths; under Wine they live on Z:.</summary>
        [Fact]
        public void AUnixPathIsReachedThroughDriveZ()
        {
            Assert.Equal(@"Z:\mnt\games\SteamLibrary", Wine.ToWindowsPath("/mnt/games/SteamLibrary"));
            Assert.Equal(@"C:\Steam", Wine.ToWindowsPath(@"C:\Steam"));
        }

        [Fact]
        public void TheNativeFlatpakAndSnapSteamFoldersAreAllTried()
        {
            var folders = Wine.LinuxSteamFolders(@"Z:\home\you").ToList();

            Assert.Equal(Path.Combine(@"Z:\home\you", ".steam", "steam"), folders[0]);
            Assert.Contains(folders, f => f.EndsWith(Path.Combine(".local", "share", "Steam")) && !f.Contains("com.valvesoftware"));
            Assert.Contains(folders, f => f.Contains("com.valvesoftware.Steam"));
            Assert.Contains(folders, f => f.Contains(Path.Combine("snap", "steam")));
        }

        /// <summary>
        /// The output font is built from a free-text setting. Whatever it names, the embedded font is
        /// last in the list, which is what keeps WPF from FailFast when none of the rest exist.
        /// </summary>
        [Theory]
        [InlineData("Consolas")]
        [InlineData("Cascadia Mono, Consolas,")]
        [InlineData("")]
        [InlineData(null)]
        public void TheOutputFontAlwaysEndsInTheEmbeddedOne(string? setting)
        {
            // Registers the pack:// scheme, which a running WPF application has already done.
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack;

            string source = AppFonts.Mono(setting).Source;

            Assert.EndsWith(", " + AppFonts.BundledMono, source);
            Assert.DoesNotContain(",,", source.Replace("pack://application:,,,", ""));
        }

        /// <summary>
        /// A window naming its fonts directly is a window that crashes under Wine when none of them are
        /// installed. Every font list has to come from the shared resources, which end in an embedded font.
        /// </summary>
        [Fact]
        public void NoWindowNamesItsFontsDirectly()
        {
            string app = Path.Combine(Root(), "CompilePalX");

            var offenders = Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                            && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .SelectMany(f => File.ReadLines(f)
                    .Select((line, i) => (File: Path.GetRelativePath(app, f), Line: i + 1, Text: line)))
                .Where(l => Regex.IsMatch(l.Text, "FontFamily=\"[^{]"))
                .Select(l => $"{l.File}:{l.Line}")
                .ToList();

            Assert.True(offenders.Count == 0, "Literal FontFamily in: " + string.Join(", ", offenders));
        }

        [Fact]
        public void TheSharedFontListsEndInTheEmbeddedFonts()
        {
            string appXaml = File.ReadAllText(Path.Combine(Root(), "CompilePalX", "App.xaml"));

            foreach (var key in new[] { "CompilePal.Fonts.UI", "CompilePal.Fonts.Mono", "ContentControlThemeFontFamily" })
            {
                var match = Regex.Match(appXaml, $"x:Key=\"{Regex.Escape(key)}\">([^<]+)<");
                Assert.True(match.Success, $"{key} is not defined in App.xaml");
                Assert.Matches(@", \./Fonts/#Liberation (Sans|Mono)$", match.Groups[1].Value.Trim());
            }

            foreach (var font in new[] { "LiberationSans-Regular.ttf", "LiberationMono-Regular.ttf", "OFL.txt" })
                Assert.True(File.Exists(Path.Combine(Root(), "CompilePalX", "Fonts", font)), $"Fonts/{font} is missing");
        }
    }
}
