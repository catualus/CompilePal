using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// Two paths in the error-handling code take data that someone else controls, and both were
    /// missing a guard.
    ///
    /// The error catalogue is fetched from <c>Settings.ErrorSourceURL</c>. Its patterns are compiled
    /// into <see cref="Regex"/> objects and every one of them is run against every line a compile
    /// tool prints. Its descriptions are HTML rendered in a WebView, with values captured out of
    /// that same compile output substituted into them - and compile output is derived from whatever
    /// .vmf, .vmt and .mdl files were fed to the tools, which is to say from a file a mapper may
    /// well have been handed by someone else.
    /// </summary>
    public class UntrustedInputTests
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

        /// <summary>
        /// The pattern that motivated the timeout: nested quantifiers over a non-matching tail, which
        /// backtracks exponentially. Against a line of 30 a's this runs effectively forever.
        /// </summary>
        private const string CatastrophicPattern = @"^(a+)+$";
        private static readonly string EvilInput = new string('a', 30) + "!";

        /// <summary>
        /// Demonstrates the hazard rather than asserting it away: with no timeout this is a hang, so
        /// the assertion is only that a bounded Regex gives up instead.
        /// </summary>
        [Fact]
        public void ABoundedRegexAbandonsACatastrophicPatternInsteadOfHanging()
        {
            var bounded = new Regex(CatastrophicPattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));

            var stopwatch = Stopwatch.StartNew();
            Assert.Throws<RegexMatchTimeoutException>(() => bounded.IsMatch(EvilInput));
            stopwatch.Stop();

            // Generous - the point is that it returned at all, in something like the budget rather
            // than in minutes.
            Assert.True(stopwatch.ElapsedMilliseconds < 5000,
                $"took {stopwatch.ElapsedMilliseconds}ms, so the timeout is not being honoured");
        }

        /// <summary>
        /// Every Regex built from catalogue data must carry a match timeout.
        ///
        /// Without one, a single hostile or merely careless pattern wedges a compile with no way out
        /// but killing the process - the patterns run against every line of tool output.
        /// </summary>
        [Fact]
        public void EveryRegexBuiltFromCatalogueDataIsBounded()
        {
            string code = File.ReadAllText(Path.Combine(SourceDir(), "Compiling", "ErrorFinder.cs"));

            foreach (Match construction in Regex.Matches(code, @"new Regex\(([^;]*?)\)\s*[,;]"))
            {
                string args = construction.Groups[1].Value;
                Assert.Contains("MatchTimeout", args);
            }

            // And a pattern that times out must cost only that line, not the rest of the scan.
            Assert.Contains("RegexMatchTimeoutException", code);
        }

        /// <summary>
        /// Values captured from compile output are substituted into the error page. They must be
        /// escaped, or a crafted map name turns the description into markup injection.
        /// </summary>
        [Fact]
        public void SubstitutionsIntoTheErrorPageAreEscaped()
        {
            string code = File.ReadAllText(Path.Combine(SourceDir(), "Compiling", "ErrorWindow.xaml.cs"));

            Assert.Matches(@"Replace\(\$""\[sub:\{i\}\]"",\s*WebUtility\.HtmlEncode\(", code);
        }

        /// <summary>
        /// Escaping closes the injection route; the policy closes the capability.
        ///
        /// Script is already off on the WebView, but NavigateToString leaves absolute URLs working -
        /// so an injected or hostile <c>&lt;img src="http://..."&gt;</c> would still be fetched, which
        /// reports the reader's address to whoever authored the entry. `default-src 'none'` means
        /// nothing can be fetched at all.
        /// </summary>
        [Fact]
        public void TheErrorPageForbidsEverySubresource()
        {
            string code = File.ReadAllText(Path.Combine(SourceDir(), "Compiling", "ErrorWindow.xaml.cs"));

            Assert.Contains("Content-Security-Policy", code);
            Assert.Contains("default-src 'none'", code);

            // Script staying disabled is the other half of this and is easy to switch back on by
            // accident while debugging a page that will not render.
            Assert.Contains("IsScriptEnabled = false", code);
        }

        /// <summary>
        /// The endpoint must not be reachable from configuration.
        ///
        /// It was briefly a setting, which made settings.json an exfiltration primitive: anything
        /// able to write that file could repoint the client at a host of its choosing. A desktop
        /// application handed to the public has no business taking its reporting destination from
        /// a file on the user's disk.
        /// </summary>
        [Fact]
        public void TheTelemetryDestinationIsNotReadFromConfiguration()
        {
            string settings = File.ReadAllText(Path.Combine(SourceDir(), "Configuration", "Settings.cs"));

            Assert.DoesNotContain("TelemetryEndpoint", settings);
            Assert.DoesNotContain("AnalyticsHost", settings);
            Assert.DoesNotContain("AnalyticsWriteKey", settings);

            string manager = File.ReadAllText(Path.Combine(SourceDir(), "Telemetry", "TelemetryManager.cs"));

            Assert.DoesNotContain("Settings.TelemetryEndpoint", manager);

            // Nor is it written in the source. The endpoint is injected at build time, so the
            // public repository carries no reporting destination - a fork does not silently
            // inherit one, and an unofficial build reports nowhere.
            Assert.DoesNotContain("https://telemetry.", manager);
            Assert.Contains("TelemetryEndpoints.Default", manager);
        }

        /// <summary>
        /// A build with no endpoint injected must send nothing, whatever the user toggles.
        ///
        /// This is what makes the build-time injection meaningful rather than cosmetic: the
        /// absence of a destination is itself the off switch for every unofficial build.
        /// </summary>
        [Fact]
        public void ABuildWithNoEndpointReportsNowhere()
        {
            string csproj = File.ReadAllText(Path.Combine(SourceDir(), "CompilePalX.csproj"));

            Assert.Contains("GenerateTelemetryEndpoint", csproj);

            string manager = File.ReadAllText(Path.Combine(SourceDir(), "Telemetry", "TelemetryManager.cs"));

            // Both an endpoint AND a signing key are required. The service refuses unsigned
            // submissions, so a build with an endpoint and no key could only ever collect a
            // session's counters and be handed a 401 for them - once per session, silently, for
            // the life of the release.
            Assert.Contains("TelemetryEndpoints.SigningKey.Length > 0", manager);
            Assert.Contains("TelemetryEndpoints.SigningKeyGeneration.Length > 0", manager);

            // Sending must require a destination, not only the user setting. Asserted as a
            // property of the expression rather than as word order: an earlier version of this
            // demanded IsConfigured be the first term, which failed the moment the collection
            // check was split out into its own name without changing the meaning at all.
            var enabled = Regex.Match(manager, @"bool Enabled\s*=>(.*?);", RegexOptions.Singleline);

            Assert.True(enabled.Success, "could not find the Enabled expression");
            Assert.Contains("IsConfigured", enabled.Groups[1].Value);

            // And collection must NOT require it, or the payload stops being observable in any
            // build without an endpoint - which is the configuration CI builds.
            var collection = Regex.Match(manager, @"bool CollectionEnabled\s*=>(.*?);", RegexOptions.Singleline);

            Assert.True(collection.Success, "could not find the CollectionEnabled expression");
            Assert.DoesNotContain("IsConfigured", collection.Groups[1].Value);
        }
    }
}
