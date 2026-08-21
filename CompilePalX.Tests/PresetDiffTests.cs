using System.Collections.Generic;
using System.Linq;
using CompilePalX;
using CompilePalX.Configuration;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// Comparing two presets used to mean opening each step of one, reading its parameters, switching
    /// preset and repeating from memory - with six near-identically named presets in the default set.
    ///
    /// These run against the real PresetDiff.Compare. It takes the parameters already extracted rather
    /// than the CompileProcess they came from, which is what makes that possible: a CompileProcess
    /// cannot be constructed here, because its constructor reads meta.json from disk and throws
    /// without it.
    /// </summary>
    public class PresetDiffTests
    {
        private static ConfigItem Flag(string name) =>
            new() { Name = name, Parameter = $" -{name.ToLowerInvariant()}", CanHaveValue = false };

        private static ConfigItem Valued(string name, string value) =>
            new() { Name = name, Parameter = $" -{name.ToLowerInvariant()}", CanHaveValue = true, Value = value };

        private static PresetStepComparison Step(string name, ConfigItem[]? left, ConfigItem[]? right) =>
            new() { Step = name, Left = left, Right = right };

        private static List<PresetDifference> Compare(params PresetStepComparison[] steps) =>
            PresetDiff.Compare(steps);

        [Fact]
        public void AParameterSetOnlyOnOneSideReadsAsAbsentOnTheOther()
        {
            var rows = Compare(Step("VRAD", [Flag("Final")], [Flag("Fast")]));

            var final = rows.Single(r => r.Parameter == "Final");
            Assert.Equal(PresetDiff.Flag, final.Left);
            Assert.Equal(PresetDiff.Absent, final.Right);
            Assert.True(final.Differs);
        }

        [Fact]
        public void TheSameParameterWithTheSameValueIsNotADifference()
        {
            var row = Compare(Step("VVIS",
                    [Valued("Radius Override", "2048")],
                    [Valued("Radius Override", "2048")]))
                .Single(r => r.Parameter == "Radius Override");

            Assert.Equal("2048", row.Left);
            Assert.False(row.Differs);
        }

        [Fact]
        public void TheSameParameterWithDifferentValuesIsADifference()
        {
            var row = Compare(Step("VVIS",
                    [Valued("Radius Override", "2048")],
                    [Valued("Radius Override", "512")]))
                .Single(r => r.Parameter == "Radius Override");

            Assert.True(row.Differs);
            Assert.Equal("2048", row.Left);
            Assert.Equal("512", row.Right);
        }

        [Fact]
        public void WhetherAStepIsInThePresetAtAllIsReported()
        {
            var row = Compare(Step("PACK", [Flag("Detail Props")], null))
                .Single(r => r.Parameter == "(step included)");

            // The most consequential difference of all: it decides whether the step runs.
            Assert.Equal("yes", row.Left);
            Assert.Equal("no", row.Right);
            Assert.True(row.Differs);
        }

        [Fact]
        public void AStepPresentButEmptyIsNotTheSameAsAStepThatIsAbsent()
        {
            var included = Compare(Step("PACK", [], null)).Single(r => r.Parameter == "(step included)");

            // An empty list means "this preset runs PACK with nothing configured"; null means it does
            // not run PACK at all. Conflating them would hide a step being switched off entirely.
            Assert.Equal("yes", included.Left);
            Assert.Equal("no", included.Right);
        }

        [Fact]
        public void AStepNeitherPresetUsesIsLeftOutEntirely()
        {
            Assert.Empty(Compare(Step("CUBEMAPS", null, null)));
        }

        [Fact]
        public void ARepeatedParameterIsJoinedRatherThanPairedUpPositionally()
        {
            var row = Compare(Step("PACK",
                    [Valued("Include", "a.vmt"), Valued("Include", "b.vmt")],
                    [Valued("Include", "a.vmt")]))
                .Single(r => r.Parameter == "Include");

            // Pairing repeats by position would invent an ordering neither preset has, and would report
            // a value added on one side as though a different value had been removed from the other.
            Assert.Equal("a.vmt, b.vmt", row.Left);
            Assert.Equal("a.vmt", row.Right);
            Assert.True(row.Differs);
        }

        [Fact]
        public void AFlagShowsAsOnRatherThanAsAnEmptyValue()
        {
            var row = Compare(Step("REPACK", [Flag("Compress")], null))
                .Single(r => r.Parameter == "Compress");

            // A switch has no value to show, and rendering it blank made "on" and "not set" identical.
            Assert.Equal("on", row.Left);
        }

        [Fact]
        public void IdenticalPresetsProduceNoDifferences()
        {
            var rows = Compare(Step("VBSP",
                [Flag("Only Entities"), Valued("Block Size", "1024")],
                [Flag("Only Entities"), Valued("Block Size", "1024")]));

            Assert.NotEmpty(rows);
            Assert.DoesNotContain(rows, r => r.Differs);
        }
    }
}
