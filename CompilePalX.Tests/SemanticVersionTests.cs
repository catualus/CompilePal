using System;
using System.Linq;
using CompilePalX;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// The ordering rules, pinned against the specification's own examples.
    ///
    /// This exists because the scheme it replaces got the central rule backwards. Upstream encoded
    /// a prerelease in the minor component - stable "029", prerelease "029.1" - so 029.1 parsed to
    /// 29.1.0.0 and sorted *above* 029. A user on a prerelease was therefore never told that the
    /// stable release of the same line was available, and there was no minor version left to use
    /// for anything else.
    /// </summary>
    public class SemanticVersionTests
    {
        [Theory]
        [InlineData("1.0.0", 1, 0, 0)]
        [InlineData("0.1.2", 0, 1, 2)]
        [InlineData("10.20.30", 10, 20, 30)]
        [InlineData("v1.2.3", 1, 2, 3)]          // tags carry a 'v'
        [InlineData("1.2.3+build.7", 1, 2, 3)]   // build metadata is discarded
        public void ParsesWellFormedVersions(string text, int major, int minor, int patch)
        {
            var version = SemanticVersion.Parse(text);

            Assert.Equal(major, version.Major);
            Assert.Equal(minor, version.Minor);
            Assert.Equal(patch, version.Patch);
            Assert.False(version.IsPreRelease);
        }

        [Theory]
        [InlineData("1.2.3-rc.1", "rc", "1")]
        [InlineData("1.0.0-alpha", "alpha")]
        [InlineData("1.0.0-0.3.7", "0", "3", "7")]
        [InlineData("1.0.0-x-y-z.-", "x-y-z", "-")]
        public void ParsesPreReleaseIdentifiers(string text, params string[] expected)
        {
            var version = SemanticVersion.Parse(text);

            Assert.True(version.IsPreRelease);
            Assert.Equal(expected, version.PreRelease.ToArray());
        }

        [Theory]
        [InlineData("")]
        [InlineData("1")]
        [InlineData("1.2")]
        [InlineData("1.2.3.4")]
        [InlineData("029")]            // the scheme being replaced
        [InlineData("029.1")]
        [InlineData("01.2.3")]         // leading zeroes are a second spelling of the same version
        [InlineData("1.2.3-")]         // empty prerelease
        [InlineData("1.2.3-rc..1")]    // empty identifier
        [InlineData("1.2.3-rc.01")]    // leading zero in a numeric identifier
        [InlineData("1.2.3-rc!")]      // illegal character
        [InlineData(" 1.2.3 ")]        // trimmed, then valid - included to prove trimming happens
        public void RejectsMalformedVersions(string text)
        {
            var ok = SemanticVersion.TryParse(text, out var version);

            if (text.Trim() == "1.2.3")
            {
                Assert.True(ok);
                return;
            }

            Assert.False(ok, $"'{text}' should not parse");
            Assert.Null(version);
        }

        /// <summary>The rule the previous scheme inverted.</summary>
        [Fact]
        public void APreReleaseSortsBelowItsOwnRelease()
        {
            Assert.True(SemanticVersion.Parse("1.2.0-rc.1") < SemanticVersion.Parse("1.2.0"));
            Assert.True(SemanticVersion.Parse("1.2.0") > SemanticVersion.Parse("1.2.0-rc.9"));
        }

        /// <summary>
        /// Straight from the specification, item 11. Kept as one ordered list rather than pairs so
        /// a change that breaks transitivity shows up rather than passing every pairwise check.
        /// </summary>
        [Fact]
        public void OrdersExactlyAsTheSpecificationSays()
        {
            string[] ascending =
            [
                "1.0.0-alpha",
                "1.0.0-alpha.1",
                "1.0.0-alpha.beta",
                "1.0.0-beta",
                "1.0.0-beta.2",
                "1.0.0-beta.11",
                "1.0.0-rc.1",
                "1.0.0",
                "1.0.1",
                "1.1.0",
                "2.0.0",
            ];

            for (var i = 0; i < ascending.Length - 1; i++)
            {
                var lower = SemanticVersion.Parse(ascending[i]);
                var higher = SemanticVersion.Parse(ascending[i + 1]);

                Assert.True(lower < higher, $"expected {lower} < {higher}");
                Assert.True(higher > lower, $"expected {higher} > {lower}");
            }
        }

        /// <summary>
        /// beta.11 above beta.2 is the case a string comparison gets wrong, and the one most
        /// likely to be reintroduced by someone "simplifying" the comparer.
        /// </summary>
        [Fact]
        public void NumericIdentifiersCompareNumericallyNotAsText()
        {
            Assert.True(SemanticVersion.Parse("1.0.0-beta.2") < SemanticVersion.Parse("1.0.0-beta.11"));
        }

        [Fact]
        public void ANumericIdentifierSortsBelowAnAlphanumericOne()
        {
            Assert.True(SemanticVersion.Parse("1.0.0-1") < SemanticVersion.Parse("1.0.0-alpha"));
        }

        [Fact]
        public void MoreIdentifiersWinATie()
        {
            Assert.True(SemanticVersion.Parse("1.0.0-alpha") < SemanticVersion.Parse("1.0.0-alpha.1"));
        }

        [Fact]
        public void BuildMetadataDoesNotAffectOrdering()
        {
            Assert.Equal(SemanticVersion.Parse("1.0.0+a"), SemanticVersion.Parse("1.0.0+b"));
        }

        [Fact]
        public void RoundTripsThroughToString()
        {
            foreach (var text in new[] { "1.0.0", "1.2.3-rc.1", "0.0.1-alpha.beta.11" })
            {
                Assert.Equal(text, SemanticVersion.Parse(text).ToString());
            }
        }

        /// <summary>
        /// The comparison the updater actually performs, in both directions, so "is there an
        /// update" cannot silently invert.
        /// </summary>
        [Theory]
        [InlineData("1.0.0", "1.0.1", true)]
        [InlineData("1.0.0", "1.1.0", true)]
        [InlineData("1.0.0", "2.0.0", true)]
        [InlineData("1.2.0-rc.1", "1.2.0", true)]     // a prerelease user should be offered the release
        [InlineData("1.2.0", "1.2.0-rc.2", false)]    // and never offered a prerelease as an upgrade
        [InlineData("1.0.0", "1.0.0", false)]
        [InlineData("2.0.0", "1.9.9", false)]
        public void AnUpdateIsOfferedOnlyWhenTheRemoteIsGenuinelyNewer(string local, string remote, bool expected)
        {
            Assert.Equal(expected, SemanticVersion.Parse(remote) > SemanticVersion.Parse(local));
        }
    }
}
