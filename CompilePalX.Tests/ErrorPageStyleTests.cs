using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// The error description window renders catalogue HTML in a WebView. That HTML specifies no colours
    /// of its own, so with the app in dark mode the runtime darkened the page background without
    /// reliably lifting the text off it - dark grey on near-black, which is unreadable.
    ///
    /// The fix has two halves and both have to stay in place:
    ///   - errorpage.css states both colour schemes outright rather than leaving either to the renderer;
    ///   - ErrorWindow injects that stylesheet when the page is shown, not when the entry is parsed.
    ///
    /// The second half is the easy one to undo by accident. Entry HTML is *persisted* - errors.txt holds
    /// whatever the source returned with the template already applied - so styling at parse time leaves
    /// every cached catalogue on the colours it was fetched with, and a fix to this file would appear to
    /// do nothing until the cache expired a week later.
    /// </summary>
    public class ErrorPageStyleTests
    {
        private static string SourceDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "CompilePalX", "MainWindow.xaml.cs");
                if (File.Exists(candidate))
                    return Path.Combine(dir.FullName, "CompilePalX");

                dir = dir.Parent;
            }

            throw new InvalidOperationException($"Could not find CompilePalX sources above {AppContext.BaseDirectory}");
        }

        /// <summary>Reads the stylesheet from the build output, so a missing csproj entry fails here.</summary>
        private static string Stylesheet()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Compiling", "errorpage.css");
            Assert.True(File.Exists(path),
                $"errorpage.css was not copied to the output at {path} - the error window will fall back " +
                "to its inline emergency style.");
            return File.ReadAllText(path);
        }

        [Fact]
        public void TheStylesheetSetsAForegroundAndABackgroundForBothSchemes()
        {
            string css = Stylesheet();

            // Leaving either half to the renderer is the whole bug: a background without a matching
            // foreground is exactly how the text ended up dark on dark.
            Assert.Contains("background", css, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("color", css, StringComparison.OrdinalIgnoreCase);

            Assert.Contains("prefers-color-scheme: dark", css);

            // color-scheme tells the renderer the page handles both itself, which stops it applying its
            // own auto-darkening pass on top.
            Assert.Matches(@"color-scheme\s*:\s*light\s+dark", css);
        }

        /// <summary>
        /// Every colour the page uses has to resolve in both schemes. A variable defined only inside the
        /// dark media query is transparent-or-black in light mode and vice versa.
        /// </summary>
        [Fact]
        public void EveryColourVariableUsedIsDefinedInTheDefaultScheme()
        {
            string css = Stylesheet();

            int darkBlock = css.IndexOf("prefers-color-scheme: dark", StringComparison.Ordinal);
            Assert.True(darkBlock > 0, "no dark scheme block");

            string light = css.Substring(0, darkBlock);

            foreach (Match use in Regex.Matches(css, @"var\(\s*(--[a-z0-9-]+)\s*\)"))
            {
                string name = use.Groups[1].Value;
                Assert.True(Regex.IsMatch(light, Regex.Escape(name) + @"\s*:"),
                    $"{name} is used but never defined outside the dark-scheme block, so it has no value " +
                    "in light mode.");
            }
        }

        /// <summary>
        /// The original bug was not "no colours were set", it was "the two that ended up applied were
        /// too close to tell apart". Stating a palette is only worth anything if the pairs in it are
        /// actually readable, so every foreground is measured against its own scheme's background.
        ///
        /// 4.5:1 is the WCAG AA threshold for body text, which is what these pages are.
        /// </summary>
        [Fact]
        public void EveryForegroundIsReadableAgainstItsOwnBackground()
        {
            string css = Stylesheet();

            int darkAt = css.IndexOf("prefers-color-scheme: dark", StringComparison.Ordinal);
            var light = Variables(css.Substring(0, darkAt));

            // The dark block redefines a subset, so it starts from the light values.
            var dark = new Dictionary<string, string>(light);
            foreach (var kv in Variables(css.Substring(darkAt)))
                dark[kv.Key] = kv.Value;

            string[] foregrounds =
            [
                "--page-fg", "--muted-fg", "--link",
                "--sev1", "--sev2", "--sev3", "--sev4", "--sev5",
            ];

            foreach (var (scheme, palette) in new[] { ("light", light), ("dark", dark) })
            {
                Assert.True(palette.ContainsKey("--page-bg"), $"{scheme} defines no page background");
                string background = palette["--page-bg"];

                foreach (string key in foregrounds)
                {
                    Assert.True(palette.ContainsKey(key), $"{scheme} defines no {key}");

                    double contrast = Contrast(palette[key], background);
                    Assert.True(contrast >= 4.5,
                        $"{scheme}: {key} ({palette[key]}) against {background} is only {contrast:0.00}:1 - " +
                        "below the 4.5:1 needed for body text.");
                }
            }
        }

        private static Dictionary<string, string> Variables(string block)
        {
            var found = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(block, @"(--[a-z0-9-]+)\s*:\s*(#[0-9a-fA-F]{6})"))
                found[m.Groups[1].Value] = m.Groups[2].Value;
            return found;
        }

        /// <summary>WCAG 2.1 relative luminance contrast ratio.</summary>
        private static double Contrast(string a, string b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        private static double Luminance(string hex)
        {
            double Channel(int offset)
            {
                double c = Convert.ToInt32(hex.Substring(offset, 2), 16) / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
        }

        /// <summary>
        /// Guards the half of the fix that is invisible from the stylesheet itself.
        /// </summary>
        [Fact]
        public void TheErrorWindowAppliesTheStylesheetWhenThePageIsShown()
        {
            string code = File.ReadAllText(Path.Combine(SourceDir(), "Compiling", "ErrorWindow.xaml.cs"));

            Assert.Contains("errorpage.css", code);

            // Appended after the entry's own markup: cached entries carry an older template that sets a
            // body font of its own, and document order is what lets this win without out-specifying it.
            Assert.Matches(@"html\s*\+\s*""\\n<style>", code);

            // The renderer follows the app's theme rather than Windows, or a user who has picked a theme
            // against their OS setting gets a description page in the opposite scheme to the window.
            Assert.Contains("PreferredColorScheme", code);
        }

        /// <summary>
        /// The wrapper is applied at parse time and frozen into errors.txt, so anything visual in it is
        /// stuck in the cache. It must stay presentation-free.
        /// </summary>
        [Fact]
        public void TheCachedWrapperCarriesNoStylingOfItsOwn()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Compiling", "errorstyle.html");
            Assert.True(File.Exists(path), $"errorstyle.html missing at {path}");

            string html = File.ReadAllText(path);

            Assert.Contains("%content%", html);
            Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        }
    }
}
