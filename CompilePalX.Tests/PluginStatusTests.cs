using CompilePalX.Compiling;
using CompilePalX.Configuration;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// Reading what a plugin says about a queued map.
    ///
    /// The text here comes from a program in a plugin folder and lands on a card in the main window
    /// and in a dialog that can stop a compile, so the parsing is deliberately unforgiving about
    /// shape and deliberately forgiving about failure: anything it cannot read is no chip at all.
    /// </summary>
    public class PluginStatusTests
    {
        private static readonly Map Map = new(@"C:\mapsrc\gm_test.vmf");

        [Fact]
        public void ReadsTheFourFields()
        {
            var status = PluginStatus.Parse("Shipwright", Map,
                """{"label":"Atlas RP","detail":"replaces it for everyone","severity":"warn","confirm":true}""");

            Assert.NotNull(status);
            Assert.Equal("Shipwright", status!.StepName);
            Assert.Equal("gm_test.vmf", status.MapName);
            Assert.Equal("Atlas RP", status.Label);
            Assert.Equal("replaces it for everyone", status.Detail);
            Assert.Equal(StatusSeverity.Warn, status.Severity);
            Assert.True(status.Confirm);
        }

        [Fact]
        public void BlockingIsUnderstood() =>
            Assert.Equal(StatusSeverity.Blocking,
                PluginStatus.Parse("S", Map, """{"label":"not bound","severity":"blocking"}""")!.Severity);

        [Fact]
        public void AnUnknownSeverityIsTheHarmlessOne() =>
            Assert.Equal(StatusSeverity.Ok,
                PluginStatus.Parse("S", Map, """{"label":"x","severity":"catastrophic"}""")!.Severity);

        [Fact]
        public void ASeverityThatIsMissingIsTheHarmlessOne() =>
            Assert.Equal(StatusSeverity.Ok, PluginStatus.Parse("S", Map, """{"label":"x"}""")!.Severity);

        [Fact]
        public void ChattyOutputIsStillReadIfTheLastLineIsJson()
        {
            var status = PluginStatus.Parse("S", Map,
                "warn  could not read something\n{\"label\":\"bound\",\"severity\":\"info\"}\n");

            Assert.NotNull(status);
            Assert.Equal("bound", status!.Label);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not json at all")]
        [InlineData("{ this is broken")]
        [InlineData("""{"detail":"a label is the one required field"}""")]
        [InlineData("""{"label":""}""")]
        public void AnythingUnreadableIsNoChip(string output) =>
            Assert.Null(PluginStatus.Parse("S", Map, output));

        [Fact]
        public void ALabelCannotBreakTheCardLayout()
        {
            var status = PluginStatus.Parse("S", Map,
                """{"label":"one\ntwo\tthree","detail":"four\nfive"}""");

            Assert.NotNull(status);
            Assert.DoesNotContain('\n', status!.Label);
            Assert.DoesNotContain('\n', status.Detail);
        }

        [Fact]
        public void ALabelIsCappedRatherThanTrusted()
        {
            string huge = new('x', 5000);

            var status = PluginStatus.Parse("S", Map, $$"""{"label":"{{huge}}","detail":"{{huge}}"}""");

            Assert.NotNull(status);
            Assert.True(status!.Label.Length <= 61, $"label was {status.Label.Length} characters");
            Assert.True(status.Detail.Length <= 401, $"detail was {status.Detail.Length} characters");
        }
    }
}
