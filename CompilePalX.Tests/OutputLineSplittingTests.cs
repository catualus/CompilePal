using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// Compiler output arrives as fixed-size chunks of a byte stream, and the logger has to work out
    /// where the lines are. Getting that wrong is not cosmetic: the issues list takes an entry's
    /// summary from the whole matched line, and errors are recognised line by line.
    ///
    /// Asserted against the source rather than by calling LogProgressive, which writes into a live
    /// WPF FlowDocument and cannot run outside the application.
    /// </summary>
    public class OutputLineSplittingTests
    {
        private static string Logger() =>
            File.ReadAllText(Path.Combine(SourceDir(), "Compiling", "Logger.cs"));

        /// <summary>
        /// The bug: Split("\r\n") alone.
        ///
        /// The Source tools are not consistent about line endings - plenty of their output arrives as
        /// a bare "\n" - so those lines never terminated, stayed in the buffer, and were emitted glued
        /// to whatever came next:
        ///
        ///     Building Faces...Water: $LightMapWaterFog doesn't work without $FlowMap
        ///
        /// which is two messages from two different subsystems on one line.
        /// </summary>
        [Fact]
        public void TheSplitHandlesBareLineFeedsNotOnlyCarriageReturnLineFeed()
        {
            string code = Logger();

            Assert.DoesNotContain("lineBuffer.ToString().Split(\"\\r\\n\")", code);
            Assert.Contains(".Split('\\n')", code);
        }

        /// <summary>A lone CR - used to overwrite a counter in place - also ends a line for us.</summary>
        [Fact]
        public void ALoneCarriageReturnAlsoEndsALine()
        {
            Assert.Contains(".Replace('\\r', '\\n')", Logger());
        }

        /// <summary>
        /// The partial-line guard and the split have to agree on what a line ending is. While the
        /// guard tested only "\n", a chunk ending in a bare "\r" was echoed to the live run as
        /// still-in-progress text and then split into a finished line moments later - printing twice.
        /// </summary>
        [Fact]
        public void ThePartialLineGuardAgreesWithTheSplit()
        {
            Assert.Contains("s.IndexOfAny(['\\n', '\\r'])", Logger());
        }

        /// <summary>
        /// The behaviour the fix produces, exercised directly on the same expression rather than
        /// only asserted to be present in the file.
        /// </summary>
        [Theory]
        [InlineData("a\r\nb\r\n", new[] { "a", "b" })]
        [InlineData("a\nb\n", new[] { "a", "b" })]
        [InlineData("a\rb\r", new[] { "a", "b" })]
        [InlineData("Building Faces...\nWater: $LightMapWaterFog\n", new[] { "Building Faces...", "Water: $LightMapWaterFog" })]
        public void MixedLineEndingsAllSplit(string input, string[] expected)
        {
            var lines = input
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n')
                .ToList();

            // the trailing empty element is the in-progress remainder the logger keeps buffered
            Assert.Equal(expected, lines.Take(lines.Count - 1));
        }

        private static string SourceDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "CompilePalX", "Compiling", "Logger.cs")))
                    return Path.Combine(dir.FullName, "CompilePalX");

                dir = dir.Parent;
            }

            throw new InvalidOperationException($"Could not find CompilePalX sources above {AppContext.BaseDirectory}");
        }

        // ------------------------------------------------------------------ the log-spam regression

        /// <summary>
        /// GetParameterString is a pure query: ArgumentSummary binds it to a UI row and BSPPack calls
        /// it about twenty times in a row to test for individual flags.
        ///
        /// Logging from inside it meant one preset carrying a single CS:GO-only flag printed the same
        /// "Skipping ..." warning several dozen times before the compile started, and again as it
        /// finished, burying the compile's own output.
        /// </summary>
        [Fact]
        public void GetParameterStringDoesNotLog()
        {
            string code = File.ReadAllText(Path.Combine(SourceDir(), "Compilers", "CompileProcess.cs"));

            int start = code.IndexOf("public string GetParameterString()", StringComparison.Ordinal);
            Assert.True(start > 0, "GetParameterString not found");

            int end = code.IndexOf("\n        }", start, StringComparison.Ordinal);
            string body = code[start..end];

            Assert.DoesNotContain("CompilePalLogger", body);
        }

        /// <summary>And the warning still exists - reported once, from the compile loop.</summary>
        [Fact]
        public void IncompatibleParametersAreStillReportedOncePerCompile()
        {
            string manager = File.ReadAllText(Path.Combine(SourceDir(), "Compiling", "CompilingManager.cs"));

            Assert.Contains("ReportIncompatibleParameters", manager);
            Assert.Contains("IncompatibleParameters()",
                File.ReadAllText(Path.Combine(SourceDir(), "Compilers", "CompileProcess.cs")));

            // Once, so exactly one call site besides the declaration.
            Assert.Equal(2, Regex.Matches(manager, @"ReportIncompatibleParameters\(\)").Count);
        }

        /// <summary>
        /// The report must walk a snapshot. UpdateOrder clears and refills CurrentOrder, and several
        /// UI actions call it - including simply opening the ORDER tab - so iterating the live
        /// collection throws "collection was modified" out of the middle of a compile.
        /// </summary>
        [Fact]
        public void TheReportWalksASnapshotOfTheOrder()
        {
            string manager = File.ReadAllText(Path.Combine(SourceDir(), "Compiling", "CompilingManager.cs"));

            Assert.DoesNotMatch(
                new Regex(@"foreach\s*\([^)]*\s+in\s+OrderManager\.CurrentOrder\s*\)"),
                manager);
        }
    }
}
