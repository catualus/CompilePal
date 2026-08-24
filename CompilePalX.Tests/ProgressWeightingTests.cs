using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CompilePalX.Compiling;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// The progress bar is weighted by how long each step has taken before, so that it moves at a
    /// roughly constant rate instead of sitting still through VVIS and then leaping.
    ///
    /// The arithmetic has one job: the shares must total the whole compile. If they total more, the
    /// bar reaches the end early and stops; if less, it never arrives. Both look like "the progress
    /// bar is wrong" and neither is visible from reading the code, which is why they are pinned here.
    /// </summary>
    public class ProgressWeightingTests
    {
        private const double Tolerance = 1e-9;

        /// <summary>
        /// Shares must sum to 1 across the step LIST, not across the returned dictionary.
        ///
        /// The distinction matters because a name can repeat - every custom program reports as
        /// "CUSTOM" - so the dictionary has one entry for two steps. A caller walking the order still
        /// has to arrive at exactly 1.
        /// </summary>
        [Fact]
        public void SharesSumToOneAcrossTheStepList()
        {
            var steps = new List<string> { "VBSP", "VVIS", "VRAD", "COPY" };
            var shares = CompileTimings.Shares("some_map", steps);

            double total = steps.Sum(s => shares[s]);

            Assert.Equal(1d, total, Tolerance);
        }

        [Fact]
        public void ARepeatedStepNameIsCountedOncePerAppearance()
        {
            var steps = new List<string> { "VBSP", "CUSTOM", "CUSTOM", "COPY" };
            var shares = CompileTimings.Shares("some_map", steps);

            // Four appearances, three distinct names - and the list must still total 1.
            Assert.Equal(3, shares.Count);
            Assert.Equal(1d, steps.Sum(s => shares[s]), Tolerance);
        }

        /// <summary>
        /// With nothing recorded every step is equally weighted, which is the behaviour a first
        /// compile has always had. The bar is no worse than it was, and never zero-width.
        /// </summary>
        [Fact]
        public void WithNoHistoryEveryStepIsWeightedEqually()
        {
            var steps = new List<string> { "A_unseen", "B_unseen", "C_unseen", "D_unseen" };
            var shares = CompileTimings.Shares(Guid.NewGuid().ToString(), steps);

            foreach (var step in steps)
                Assert.Equal(0.25d, shares[step], 1e-6);
        }

        [Fact]
        public void AnEmptyOrderProducesNoSharesRatherThanDividingByZero()
        {
            Assert.Empty(CompileTimings.Shares("some_map", new List<string>()));
        }

        /// <summary>
        /// The bug this file was written for.
        ///
        /// Two fallbacks existed for a step with no timing history: the segmented bar's weights used
        /// 1/steps, and the actual progress advance used 1/steps/maps. On a multi-map compile the two
        /// disagreed by exactly the map count, so the segments drawn did not correspond to how far
        /// the bar moved through them.
        ///
        /// Asserted against the source, because the expression lives inside a long compile loop that
        /// cannot be invoked from a test without a game configuration and real compile tools.
        /// </summary>
        [Fact]
        public void TheUnknownStepFallbackHasExactlyOneDefinition()
        {
            string code = File.ReadAllText(Path.Combine(SourceDir(), "Compiling", "CompilingManager.cs"));

            // One helper, used by both the weights and the advance.
            Assert.Contains("double UnknownStepShare()", code);

            int uses = code.Split("UnknownStepShare()").Length - 1;
            Assert.True(uses >= 3, $"expected the helper to be declared and used twice, saw {uses} mentions");

            // And no hand-rolled copy of the same arithmetic left behind.
            Assert.DoesNotContain("1d / Math.Max(1, order.Count) / Math.Max(1, queued.Count)", code);
        }

        /// <summary>
        /// Per-map shares must divide by the number of maps, or a queue of several maps fills the bar
        /// once per map and finishes at the first one.
        /// </summary>
        [Fact]
        public void SharesAreScaledByTheNumberOfMaps()
        {
            string code = File.ReadAllText(Path.Combine(SourceDir(), "Compiling", "CompilingManager.cs"));

            Assert.Contains("shares[name] /= mapCount", code);
        }

        /// <summary>
        /// The estimate and the bar have to be refreshed between step boundaries, not only at them.
        ///
        /// Both were written once when a step began and left until the next - so on a real compile
        /// they stood still for the whole of VVIS and VRAD, which is where the time goes. That is
        /// what "the progress bar is wrong" actually looked like.
        /// </summary>
        [Fact]
        public void TheFooterIsRefreshedOnTheTimerNotOnlyOnStepChange()
        {
            string code = File.ReadAllText(Path.Combine(SourceDir(), "MainWindow.xaml.cs"));

            int tick = code.IndexOf("private void TickElapsedTimer", StringComparison.Ordinal);
            Assert.True(tick > 0, "could not find the elapsed timer tick");

            // The one-second tick must drive the estimate, not just the elapsed clock.
            Assert.Contains("UpdateEstimates()", code[tick..(tick + 400)]);

            // And the step must publish an expected duration for it to interpolate against.
            string manager = File.ReadAllText(Path.Combine(SourceDir(), "Compiling", "CompilingManager.cs"));
            Assert.Contains("public TimeSpan? Expected", manager);
        }

        /// <summary>
        /// A step that overruns its median must not fill its segment completely, or it reads as
        /// finished while still working - and must never bleed into the next segment.
        /// </summary>
        [Fact]
        public void AnOverrunningStepDoesNotCompleteItsOwnSegment()
        {
            string code = File.ReadAllText(Path.Combine(SourceDir(), "MainWindow.xaml.cs"));

            int method = code.IndexOf("private void UpdateEstimates", StringComparison.Ordinal);
            Assert.True(method > 0);

            string body = code[method..(method + 2000)];

            Assert.Contains("Math.Min(", body);
            Assert.Contains("0.99", body);
        }

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
    }
}
