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
    /// The offline catalogue is the only thing standing between a compile log and no error
    /// recognition at all: the remote source (interlopers.net) returns 403 for every request from a
    /// non-browser client, and a fresh install has no cache to fall back to.
    ///
    /// Two files make it up. errors.default.txt is a copy of the upstream interlopers catalogue,
    /// which has not been updated in years; errors.supplement.json fills the gaps it predates.
    /// These tests read the shipped files directly rather than going through ErrorFinder, which
    /// starts a background thread and reaches for the network.
    /// </summary>
    public class ErrorCatalogueTests
    {
        private class Supplement
        {
            public string Pattern { get; set; } = "";
            public int Severity { get; set; }
            public string Title { get; set; } = "";
            public string Html { get; set; } = "";
        }

        private static string Dir => Path.Combine(AppContext.BaseDirectory, "Compiling");

        private static List<Supplement> LoadSupplement()
        {
            string path = Path.Combine(Dir, "errors.supplement.json");
            Assert.True(File.Exists(path), $"supplement missing at {path}");

            var entries = JsonConvert.DeserializeObject<List<Supplement>>(File.ReadAllText(path));
            Assert.NotNull(entries);
            return entries!;
        }

        /// <summary>
        /// Parses errors.default.txt if it is present. Optional on purpose - it is a copy of a
        /// third-party file, so the build must not depend on someone having fetched it.
        /// </summary>
        private static List<(int Severity, string Pattern)> LoadUpstream()
        {
            var result = new List<(int, string)>();
            string path = Path.Combine(Dir, "errors.default.txt");
            if (!File.Exists(path))
                return result;

            var lines = File.ReadAllLines(path);
            if (lines.Length == 0 || !int.TryParse(lines[0].Trim(), out int count))
                return result;

            for (int i = 1; i < (count * 2) + 1 && i < lines.Length; i += 2)
            {
                var parts = lines[i].Split('|', 2);
                if (parts.Length == 2 && int.TryParse(parts[0], out int sev))
                    result.Add((sev, parts[1]));
            }

            return result;
        }

        private static (int Severity, string Pattern)? MatchUpstream(string line) =>
            LoadUpstream().Cast<(int Severity, string Pattern)?>()
                .FirstOrDefault(e => SafeMatch(line, e!.Value.Pattern));

        private static Supplement? MatchSupplement(string line) =>
            LoadSupplement().FirstOrDefault(e => SafeMatch(line, e.Pattern));

        private static bool SafeMatch(string line, string pattern)
        {
            try { return Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase); }
            catch (ArgumentException) { return false; }
        }

        [Fact]
        public void SupplementParsesAndEveryEntryIsComplete()
        {
            foreach (var e in LoadSupplement())
            {
                var ex = Record.Exception(() => new Regex(e.Pattern, RegexOptions.IgnoreCase));
                Assert.True(ex == null, $"invalid pattern for '{e.Title}': {ex?.Message}");

                Assert.False(string.IsNullOrWhiteSpace(e.Title), "entry with no title");
                Assert.False(string.IsNullOrWhiteSpace(e.Html), $"'{e.Title}' has no explanation");
                Assert.InRange(e.Severity, 1, 5);
            }
        }

        /// <summary>
        /// Real lines from a Garry's Mod compile. Every one is absent from the upstream catalogue,
        /// which is the reason the supplement exists - if upstream ever covers one, the supplement
        /// entry becomes dead weight and this test says so.
        /// </summary>
        public static TheoryData<string, int> MessagesUpstreamDoesNotCover() => new()
        {
            { "Can't find surfaceprop concretegrit for material CONCRETE/CONCRETEFLOOR038C, using default", 2 },
            { "Water: $LightMapWaterFog doesn't work without $FlowMap", 2 },
            { "Error! To use model \"models/props_c17/door01_left.mdl\" as static prop, it must be compiled with $staticprop! Deleted.", 3 },
            { "Light at (-1566 1699 66) has _fifty_percent_distance of 67 but _zero_percent_distance of 40", 3 },
        };

        [Theory]
        [MemberData(nameof(MessagesUpstreamDoesNotCover))]
        public void SupplementRecognisesMessagesUpstreamMisses(string line, int expectedSeverity)
        {
            var hit = MatchSupplement(line);

            Assert.True(hit != null, $"no supplement entry matched: {line}");
            Assert.Equal(expectedSeverity, hit!.Severity);
        }

        [Theory]
        [MemberData(nameof(MessagesUpstreamDoesNotCover))]
        public void SupplementDoesNotDuplicateUpstream(string line, int _)
        {
            // Skipped silently when errors.default.txt has not been placed yet.
            if (LoadUpstream().Count == 0)
                return;

            var upstreamHit = MatchUpstream(line);
            Assert.True(upstreamHit == null,
                $"upstream now covers this, so the supplement entry is redundant: {line}");
        }

        /// <summary>
        /// The messages VMFFIX can act on must say so, or the user has no way to discover the fix.
        /// </summary>
        [Theory]
        [InlineData("Error! To use model \"x.mdl\" as static prop, it must be compiled with $staticprop! Deleted.")]
        [InlineData("Light at (0 0 0) has _fifty_percent_distance of 67 but _zero_percent_distance of 40")]
        [InlineData("Can't find surfaceprop concretegrit for material A/B, using default")]
        [InlineData("Water: $LightMapWaterFog doesn't work without $FlowMap")]
        public void AutoFixableEntriesPointAtVmfFix(string line)
        {
            var hit = MatchSupplement(line);

            Assert.NotNull(hit);
            Assert.Contains("VMFFIX", hit!.Html);
        }

        [Fact]
        public void OrdinaryProgressOutputIsNotFlaggedAsAnError()
        {
            var supplement = LoadSupplement();
            var upstream = LoadUpstream();

            foreach (string line in new[]
                     {
                         "Building Faces...",
                         "Compiling map: rp_downtown_meowy.vmf",
                         "Loaded JSON metadata PACK from ./Parameters\\PACK\\meta.json at order 10",
                         "writing c:\\maps\\test.bsp",
                         "0...1...2...3...4...5...6...7...8...9...10 (0)",
                     })
            {
                var s = supplement.FirstOrDefault(e => SafeMatch(line, e.Pattern));
                Assert.True(s == null, $"'{line}' wrongly matched supplement entry '{s?.Title}'");

                foreach (var u in upstream)
                    Assert.False(SafeMatch(line, u.Pattern),
                        $"'{line}' wrongly matched upstream pattern '{u.Pattern}'");
            }
        }

        /// <summary>
        /// Regression for the text parser: a pattern using regex alternation contains '|', which a
        /// plain Split would truncate at, silently matching far less than intended.
        /// </summary>
        [Fact]
        public void TextFormatSplitPreservesAlternationInPatterns()
        {
            const string line = "5|Cannot load VBSP|Cannot load VVIS";

            var parts = line.Split('|', 2);

            Assert.Equal("5", parts[0]);
            Assert.Equal("Cannot load VBSP|Cannot load VVIS", parts[1]);
        }

        /// <summary>
        /// Verifies the upstream text format is handled correctly without requiring the real
        /// (third-party, 100KB) file to be present: count line, then alternating
        /// "severity|pattern" and HTML-description lines.
        /// </summary>
        [Fact]
        public void UpstreamTextFormatIsParsedCorrectly()
        {
            string sample = string.Join("\n", new[]
            {
                "2",
                @"4|\*\*\*\*\s+leaked\s+\*\*\*\*",
                "<div class=\"error_box\"><h4>**** leaked ****</h4><p>You have a leak.</p></div>",
                @"3|Brush\s+([\d\.,-]+)\:\s+no\s+visible\s+sides\s+on\s+brush",
                "<div class=\"error_box\"><h4>Brush [sub:1]: no visible sides on brush</h4><p>Invalid brush.</p></div>",
            });

            var lines = sample.Split('\n');
            int count = int.Parse(lines[0]);
            Assert.Equal(2, count);

            var parsed = new List<(int Severity, Regex Pattern, string Title)>();
            var titleRegex = new Regex("<h4>(.*?)</h4>");

            for (int i = 1; i < (count * 2) + 1; i += 2)
            {
                var parts = lines[i].Split('|', 2);
                var title = titleRegex.Match(lines[i + 1]);
                parsed.Add((int.Parse(parts[0]), new Regex(parts[1]), title.Groups[1].Value));
            }

            Assert.Equal(2, parsed.Count);

            Assert.Equal(4, parsed[0].Severity);
            Assert.Equal("**** leaked ****", parsed[0].Title);
            Assert.Matches(parsed[0].Pattern, "**** leaked ****");

            Assert.Equal(3, parsed[1].Severity);
            Assert.Matches(parsed[1].Pattern, "Brush 4123: no visible sides on brush");
            Assert.DoesNotMatch(parsed[1].Pattern, "Building Faces...");
        }

        [Fact]
        public void UpstreamCatalogueParsesWhenPresent()
        {
            var upstream = LoadUpstream();
            if (upstream.Count == 0)
                return;   // not fetched on this machine

            Assert.True(upstream.Count >= 50, $"expected the full catalogue, got {upstream.Count}");
            foreach (var (severity, pattern) in upstream)
            {
                Assert.InRange(severity, 0, 5);
                var ex = Record.Exception(() => new Regex(pattern));
                Assert.True(ex == null, $"invalid upstream pattern '{pattern}': {ex?.Message}");
            }
        }
    }
}
