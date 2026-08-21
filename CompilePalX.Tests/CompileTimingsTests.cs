using System;
using System.Collections.Generic;
using System.Linq;
using CompilePalX.Compiling;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// The compile progress bar used to give every step an equal slice, so a 200ms COPY moved it as far
    /// as a half-hour VVIS. These cover the recorded-duration weighting that replaced it.
    ///
    /// CompileTimings keeps its samples in static state and the tests share one process, so every test
    /// here uses step and map names unique to itself rather than resetting shared state between runs -
    /// that keeps them order-independent without adding a Reset() that only tests would ever call.
    /// </summary>
    public class CompileTimingsTests
    {
        private static string Unique(string label) => $"{label}_{Guid.NewGuid():N}";

        [Fact]
        public void Median_IsNullForAStepNeverSeen()
        {
            Assert.Null(CompileTimings.Median(Unique("map"), Unique("STEP")));
        }

        [Fact]
        public void Median_PrefersTheTimeTheStepTookOnThisMap()
        {
            string step = Unique("VVIS");
            string bigMap = Unique("rp_big");
            string smallMap = Unique("gm_small");

            CompileTimings.Record(bigMap, step, TimeSpan.FromSeconds(600));
            CompileTimings.Record(smallMap, step, TimeSpan.FromSeconds(4));

            // The whole reason timings are kept per map: VVIS on a large open map says nothing useful
            // about VVIS on a small one, so neither may be answered with the other's number.
            Assert.Equal(600, CompileTimings.Median(bigMap, step)!.Value, 3);
            Assert.Equal(4, CompileTimings.Median(smallMap, step)!.Value, 3);
        }

        [Fact]
        public void Median_FallsBackToTheStepsOverallTimeOnAnUnseenMap()
        {
            string step = Unique("VRAD");
            CompileTimings.Record(Unique("map"), step, TimeSpan.FromSeconds(30));

            // A map compiled for the first time still gets a sensible weighting from what the step has
            // cost elsewhere, rather than being treated as unknown.
            Assert.Equal(30, CompileTimings.Median(Unique("never_compiled"), step)!.Value, 3);
        }

        [Fact]
        public void Record_IgnoresStepsThatReturnedInstantly()
        {
            string step = Unique("SKIPPED");
            string map = Unique("map");

            // A step that took no time did not run - it was skipped, or died on a missing binary.
            // Recording zeroes would drag the median down and starve it of bar on the next compile.
            CompileTimings.Record(map, step, TimeSpan.Zero);
            CompileTimings.Record(map, step, TimeSpan.FromMilliseconds(10));

            Assert.Null(CompileTimings.Median(map, step));
        }

        [Fact]
        public void Median_IsNotDraggedAroundByOneFreakRun()
        {
            string step = Unique("VBSP");
            string map = Unique("map");

            foreach (var _ in Enumerable.Range(0, 5))
                CompileTimings.Record(map, step, TimeSpan.FromSeconds(10));

            // A machine that went to sleep mid-compile, or a first run with a cold cache. A mean would
            // follow it; the median outvotes it.
            CompileTimings.Record(map, step, TimeSpan.FromSeconds(4000));

            Assert.Equal(10, CompileTimings.Median(map, step)!.Value, 3);
        }

        [Fact]
        public void Record_KeepsOnlyRecentRunsSoEstimatesFollowAGrowingMap()
        {
            string step = Unique("VVIS");
            string map = Unique("map");

            // Twelve old cheap runs, then ten expensive ones: only the last ten are retained, so the
            // old figures must have been forgotten entirely.
            foreach (var _ in Enumerable.Range(0, 12))
                CompileTimings.Record(map, step, TimeSpan.FromSeconds(5));
            foreach (var _ in Enumerable.Range(0, 10))
                CompileTimings.Record(map, step, TimeSpan.FromSeconds(100));

            Assert.Equal(100, CompileTimings.Median(map, step)!.Value, 3);
        }

        [Fact]
        public void Shares_AreEqualWhenNothingHasEverBeenRecorded()
        {
            var steps = new List<string> { Unique("A"), Unique("B"), Unique("C"), Unique("D") };

            var shares = CompileTimings.Shares(Unique("map"), steps);

            // The first compile after a fresh install has no history, and must be no worse than the
            // even split it used to get.
            Assert.All(shares.Values, v => Assert.Equal(0.25, v, 6));
        }

        [Fact]
        public void Shares_AreProportionalToRecordedDurationsAndSumToOne()
        {
            string fast = Unique("COPY");
            string slow = Unique("VVIS");
            string map = Unique("map");

            CompileTimings.Record(map, fast, TimeSpan.FromSeconds(1));
            CompileTimings.Record(map, slow, TimeSpan.FromSeconds(9));

            var shares = CompileTimings.Shares(map, new List<string> { fast, slow });

            Assert.Equal(0.1, shares[fast], 6);
            Assert.Equal(0.9, shares[slow], 6);
            Assert.Equal(1.0, shares.Values.Sum(), 6);
        }

        [Fact]
        public void Shares_TreatAnUnknownStepAsTypicalRatherThanFree()
        {
            string known = Unique("VRAD");
            string unknown = Unique("BRAND_NEW");
            string map = Unique("map");

            CompileTimings.Record(map, known, TimeSpan.FromSeconds(10));

            var shares = CompileTimings.Shares(map, new List<string> { known, unknown });

            // A step added to a preset for the first time gets the average of what is known, not zero -
            // otherwise the bar claims to be finished while it is still running.
            Assert.Equal(0.5, shares[known], 6);
            Assert.Equal(0.5, shares[unknown], 6);
        }

        [Fact]
        public void Shares_GiveARepeatedStepNameOneSlicePerAppearance()
        {
            string once = Unique("VBSP");
            string twice = Unique("CUSTOM");
            string map = Unique("map");

            CompileTimings.Record(map, once, TimeSpan.FromSeconds(10));
            CompileTimings.Record(map, twice, TimeSpan.FromSeconds(10));

            var steps = new List<string> { once, twice, twice };
            var shares = CompileTimings.Shares(map, steps);

            // Every custom program reports its name as "CUSTOM", so the same key really can appear more
            // than once in a compile order. The caller walks the order and adds a share per step, so the
            // totals have to come out at 1 that way round - not by summing the dictionary.
            Assert.Equal(1.0, steps.Sum(s => shares[s]), 6);
            Assert.Equal(1d / 3d, shares[twice], 6);
        }

        [Fact]
        public void Shares_AreEmptyForAPresetWithNoSteps()
        {
            // A preset with nothing ticked reaches the progress maths before the check that reports it,
            // so this must not divide by zero on the way through.
            Assert.Empty(CompileTimings.Shares(Unique("map"), new List<string>()));
        }
    }
}
