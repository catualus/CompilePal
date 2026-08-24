using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// A compile step that fails has to stop the compile.
    ///
    /// It did not. VBSP printed "Too many brushes in one leaf, max = 65536" as ordinary white text and
    /// the run carried on into VVIS and VRAD against a .bsp that had never been written - which failed
    /// in turn with a misleading message about a leak. The map had no leak; VBSP had simply stopped.
    ///
    /// Two independent mechanisms are pinned here, because either alone leaves a gap:
    ///   - the exit code, which catches every failure regardless of what was printed;
    ///   - the error catalogue, which is what explains the failure to the user.
    /// </summary>
    public class CompileFailureDetectionTests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "CompilePalX", "CompilePalX.csproj")))
                    return dir.FullName;

                dir = dir.Parent;
            }

            throw new InvalidOperationException($"Could not find the repository above {AppContext.BaseDirectory}");
        }

        private static string Param(params string[] parts) =>
            Path.Combine(new[] { Root(), "CompilePalX", "Parameters" }.Concat(parts).ToArray());

        // -------------------------------------------------------------------------- exit codes

        /// <summary>
        /// All three shipped with CheckExitCode false, so a tool could fail and be ignored. cmdlib's
        /// Error() prints the reason and exits non-zero, which is the one signal common to every
        /// failure - there are several hundred distinct messages and the catalogue covers a fraction.
        /// </summary>
        [Theory]
        [InlineData("VBSP")]
        [InlineData("VVIS")]
        [InlineData("VRAD")]
        public void TheCoreToolsCheckTheirExitCode(string tool)
        {
            string meta = File.ReadAllText(Param(tool, "meta.json"));

            Assert.Matches(new Regex("\"CheckExitCode\"\\s*:\\s*true"), meta);
        }

        [Fact]
        public void ANonZeroExitIsFatalRatherThanAWarning()
        {
            string code = File.ReadAllText(
                Path.Combine(Root(), "CompilePalX", "Compilers", "CompileExecutable.cs"));

            int at = code.IndexOf("Metadata.CheckExitCode", StringComparison.Ordinal);
            Assert.True(at > 0);

            string body = code[at..(at + 900)];

            // Severity 5 is what CompilingManager watches for to stop the run; a Warning stopped nothing.
            Assert.Contains("ErrorSeverity.FatalError", body);
            Assert.DoesNotContain("ErrorSeverity.Warning", body);
        }

        /// <summary>
        /// Cancelling kills the process, which is a non-zero exit by definition. Reporting the user's
        /// own cancellation as a compile failure would be both wrong and alarming.
        /// </summary>
        [Fact]
        public void CancellationIsNotReportedAsAFailure()
        {
            string code = File.ReadAllText(
                Path.Combine(Root(), "CompilePalX", "Compilers", "CompileExecutable.cs"));

            int at = code.IndexOf("Metadata.CheckExitCode", StringComparison.Ordinal);
            string condition = code[at..code.IndexOf('\n', at)];

            Assert.Contains("!cancellationToken.IsCancellationRequested", condition);
        }

        // -------------------------------------------------------------------------- catalogue

        private sealed class Entry
        {
            public string Pattern { get; set; } = "";
            public int Severity { get; set; }
            public string Title { get; set; } = "";
            public string Html { get; set; } = "";
        }

        private static List<(int Severity, Regex Pattern)> Catalogue()
        {
            var result = new List<(int, Regex)>();

            string dir = Path.Combine(Root(), "CompilePalX", "Compiling");

            foreach (string line in File.ReadAllLines(Path.Combine(dir, "errors.default.txt")))
            {
                var m = Regex.Match(line, @"^([1-5])\|(.+)$");
                if (!m.Success) continue;

                try { result.Add((int.Parse(m.Groups[1].Value), new Regex(m.Groups[2].Value, RegexOptions.IgnoreCase))); }
                catch (ArgumentException) { /* upstream ships a few patterns .NET will not compile */ }
            }

            string json = File.ReadAllText(Path.Combine(dir, "errors.supplement.json"));
            json = Regex.Replace(json, @"^\s*//.*$", "", RegexOptions.Multiline);

            foreach (var e in JsonConvert.DeserializeObject<List<Entry>>(json)!)
                result.Add((e.Severity, new Regex(e.Pattern, RegexOptions.IgnoreCase)));

            return result;
        }

        /// <summary>
        /// Real lines from a real failed compile. Each was previously printed as plain white text.
        ///
        /// The LoadPortals one is the subtle case: upstream HAS an entry for it, but its pattern
        /// builds the filename from a character class that excludes ':', so it cannot match an
        /// absolute Windows path - which is the only form the tools ever print.
        /// </summary>
        [Theory]
        [InlineData("Too many brushes in one leaf, max = 65536", 5)]
        [InlineData(@"LoadPortals: couldn't read c:\users\x\maps\rp_southside.prt", 5)]
        [InlineData("The map likely has a leak, or you forgot to run BSP step", 4)]
        public void AFatalCompilerMessageIsRecognisedAtTheRightSeverity(string line, int minimumSeverity)
        {
            var matches = Catalogue().Where(e => e.Pattern.IsMatch(line)).ToList();

            Assert.True(matches.Count > 0, $"nothing in the catalogue matches: {line}");
            Assert.True(matches[0].Severity >= minimumSeverity,
                $"first match is severity {matches[0].Severity}, expected at least {minimumSeverity}");
        }

        [Fact]
        public void EverySupplementaryPatternCompilesAndHasABody()
        {
            string json = File.ReadAllText(
                Path.Combine(Root(), "CompilePalX", "Compiling", "errors.supplement.json"));
            json = Regex.Replace(json, @"^\s*//.*$", "", RegexOptions.Multiline);

            var entries = JsonConvert.DeserializeObject<List<Entry>>(json)!;
            Assert.NotEmpty(entries);

            foreach (var e in entries)
            {
                _ = new Regex(e.Pattern);
                Assert.InRange(e.Severity, 1, 5);
                Assert.False(string.IsNullOrWhiteSpace(e.Title), $"no title: {e.Pattern}");
                Assert.Contains("<h4>", e.Html);
            }
        }

        /// <summary>
        /// The supplement used to be appended only by LoadOfflineErrorData, so anyone whose fetch from
        /// the remote catalogue succeeded - or who had a usable cache - ran with none of it. The
        /// supplementary definitions exist precisely because the upstream catalogue is years stale, so
        /// the feature worked only for the users whose network had failed.
        /// </summary>
        [Fact]
        public void TheSupplementIsAppendedOnEveryLoadPath()
        {
            string code = File.ReadAllText(
                Path.Combine(Root(), "CompilePalX", "Compiling", "ErrorFinder.cs"));

            int at = code.IndexOf("static void Publish(", StringComparison.Ordinal);
            Assert.True(at > 0, "Publish not found");

            string body = code[at..(at + 1400)];
            Assert.Contains("ParseSupplementErrorData", body);

            // Exactly one caller besides the declaration - adding it in a loader as well would load
            // every supplementary entry twice.
            Assert.Equal(2, Regex.Matches(code, @"ParseSupplementErrorData\(").Count);
        }
    }
}
