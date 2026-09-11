using System;
using System.IO;
using System.Linq;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// Reading a compiler's own account of what it accepts.
    ///
    /// The fixtures are what the binaries actually print - the standalone Breadworks builds of
    /// September 2026, the Hammer++-era vradplusplus before them, and a stock Valve vrad for
    /// contrast. The parameter lists that ship with Compile Pal used to be the only description of
    /// the compilers and were already forty options behind within weeks of a tools update; this is
    /// the parser that replaces them as the source of truth.
    /// </summary>
    public class ToolHelpParserTests
    {
        private static string Fixture(string name) =>
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

        [Fact]
        public void ReadsEveryOptionTheStandaloneVradLists()
        {
            var help = ToolHelpParser.Parse(Fixture("vrad++-help.txt"));

            Assert.NotNull(help);
            Assert.Equal(99, help!.Options.Count);
            Assert.True(help.Has("-cpu"));
            Assert.True(help.Has("-gpusubmit"));
            Assert.True(help.Has("-ambientocclusion"));
        }

        [Fact]
        public void ReadsTheBanner()
        {
            var help = ToolHelpParser.Parse(Fixture("vrad++-help.txt"))!;

            Assert.Equal("Breadworks", help.Author);
            Assert.Equal("vrad++", help.ToolName);
            Assert.Equal(new DateTime(2026, 9, 10), help.BuildDate);
            Assert.Equal("vrad++ (Sep 10 2026)", help.Label);
        }

        [Fact]
        public void ADefaultMeansTheOptionTakesAValue()
        {
            var help = ToolHelpParser.Parse(Fixture("vrad++-help.txt"))!;

            Assert.True(help.TryGet("-gpusubmit", out var submit));
            Assert.Equal("0.5", submit.Default);
            Assert.True(submit.TakesValue);

            Assert.True(help.TryGet("-cpu", out var cpu));
            Assert.Equal("", cpu.Default);
            Assert.False(cpu.TakesValue);

            // a placeholder is still a value
            Assert.True(help.TryGet("-threads", out var threads));
            Assert.Equal("<N>", threads.Default);
            Assert.True(threads.TakesValue);
        }

        [Fact]
        public void IndentedLinesContinueTheDescriptionAboveThem()
        {
            var help = ToolHelpParser.Parse(Fixture("vrad++-help.txt"))!;

            Assert.True(help.TryGet("-final", out var final));
            Assert.Contains("-extrasky 8 -extrasoft 4 -StaticPropSampleScale 4", final.Description);
            Assert.Contains("-aofacesamples 96 -aopropsamples 32", final.Description);

            // a blank line inside the table, after -lightmapformat's list, must not end it
            Assert.True(help.TryGet("-lightmapformat", out var format));
            Assert.Contains("rgbeastyle3", format.Description);
            Assert.True(help.Has("-lights"));
        }

        [Fact]
        public void FlagsAreMatchedRegardlessOfCase()
        {
            var help = ToolHelpParser.Parse(Fixture("vrad++-help.txt"))!;

            Assert.True(help.Has("-staticproplighting"));
            Assert.True(help.Has("-STATICPROPLIGHTING"));
            Assert.True(help.Has(" -StaticPropLighting "));
        }

        [Fact]
        public void TheTableEndsAtTheFirstLineBackAtTheMargin()
        {
            var help = ToolHelpParser.Parse(Fixture("vrad++-help.txt"))!;

            // "No map name specified" follows the table and is not an option
            Assert.DoesNotContain(help.Options, o => o.Description.Contains("No map name"));
            Assert.Equal("-worldtextureshadows", help.Options.Last().Flag);
        }

        [Theory]
        [InlineData("vbsp++-help.txt", "vbsp++", 92, "-bspformat")]
        [InlineData("vvis++-help.txt", "vvis++", 14, "-trace")]
        [InlineData("bspzip++-help.txt", "bspzip++", 19, "-repack")]
        public void ReadsTheOtherTools(string fixture, string tool, int count, string sample)
        {
            var help = ToolHelpParser.Parse(Fixture(fixture));

            Assert.NotNull(help);
            Assert.Equal(tool, help!.ToolName);
            Assert.Equal(count, help.Options.Count);
            Assert.True(help.Has(sample));
        }

        [Fact]
        public void TheHammerPlusPlusEraBuildPrintsTheSameTable()
        {
            var help = ToolHelpParser.Parse(Fixture("vradplusplus-hammerpp-help.txt"));

            Assert.NotNull(help);
            Assert.Equal("ficool2", help!.Author);
            Assert.Equal("vradplusplus.exe", help.ToolName);
            Assert.Equal(new DateTime(2026, 8, 29), help.BuildDate);

            // the same layout, an older option set: no GPU lighting yet
            Assert.True(help.Has("-ambientocclusion"));
            Assert.False(help.Has("-cpu"));
        }

        [Fact]
        public void TheStockToolsPrintNoTable()
        {
            // Valve's usage text is prose; there is nothing to read, and nothing is the right answer -
            // it is what tells the caller to fall back to the shipped parameter list.
            Assert.Null(ToolHelpParser.Parse(Fixture("vrad-stock-usage.txt")));
        }

        [Fact]
        public void NothingParsesToNothing()
        {
            Assert.Null(ToolHelpParser.Parse(null));
            Assert.Null(ToolHelpParser.Parse(""));
            Assert.Null(ToolHelpParser.Parse("Unable to find gameinfo.txt"));
        }

        [Fact]
        public void ALoneRowWithoutAHeaderIsNotATable()
        {
            // A stray "-foo | | bar" in ordinary output is not a help listing.
            Assert.Null(ToolHelpParser.Parse("  -foo |  | bar\n"));
        }
    }
}
