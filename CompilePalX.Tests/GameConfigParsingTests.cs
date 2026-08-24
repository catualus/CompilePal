using System;
using System.IO;
using System.Linq;
using System.Reflection;
using ValveKeyValue;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// Game configurations are written by Hammer, by Hammer++, by mod authors and by hand, and a
    /// hand-written or mod-specific one routinely omits keys the stock file has.
    ///
    /// The parser read them as <c>(hdb["Vis"] ?? hdb["vis"]).ToString()</c>, which throws a
    /// NullReferenceException when neither spelling is present. The loop's handler caught it, so
    /// the symptom a user saw was their game silently missing from the list and a stack trace in
    /// the log that never said which key was responsible.
    /// </summary>
    public class GameConfigParsingTests
    {
        /// <summary>
        /// Invokes the private helper. Testing it directly rather than through Parse keeps this
        /// from needing a gameconfig file on disk and a real bin folder.
        /// </summary>
        private static MethodInfo Required()
        {
            var method = typeof(GameConfigurationParser)
                .GetMethod("Required", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            return method!;
        }

        /// <summary>
        /// Drives the real helper with a stand-in for a parsed KeyValues block.
        ///
        /// KVValue is used directly rather than bridged by reflection: it has an implicit
        /// conversion from string, and an earlier version of this test tried to build one with
        /// `as KVValue` on a string, which silently produced null - so every supplied value looked
        /// absent and the tests failed for a reason that had nothing to do with the code.
        /// </summary>
        private static string Invoke(Func<string, string?> read, string game, params string[] names)
        {
            Func<string, KVValue?> accessor = name => read(name) is { } v ? (KVValue)v : null;

            try
            {
                return (string)Required().Invoke(null, [accessor, game, names])!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        }

        /// <summary>
        /// The case that used to be a NullReferenceException. It must name the game and the key,
        /// because "something failed to parse" leaves a user with nowhere to look.
        /// </summary>
        [Fact]
        public void AMissingKeyReportsWhichGameAndWhichKey()
        {
            var ex = Assert.Throws<InvalidDataException>(
                () => Invoke(_ => null, "Half-Life 2: Deathmatch", "BSPDir", "bspdir"));

            Assert.Contains("Half-Life 2: Deathmatch", ex.Message);
            Assert.Contains("BSPDir", ex.Message);

            // Not a bare exception dump - it should read as a sentence pointing somewhere.
            Assert.Contains("gameconfig", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A NullReferenceException here would mean the old behaviour is back: it is caught by the
        /// generic handler and logged as a stack trace, which is precisely the unhelpful outcome.
        /// </summary>
        [Fact]
        public void AMissingKeyDoesNotThrowNullReference()
        {
            var ex = Record.Exception(() => Invoke(_ => null, "Some Mod", "Vis", "vis"));

            Assert.NotNull(ex);
            Assert.IsNotType<NullReferenceException>(ex);
        }

        /// <summary>
        /// Casing varies between the tools that write these files, which is why each read lists
        /// alternatives. Every spelling has to be tried before giving up.
        /// </summary>
        [Fact]
        public void EverySpellingIsTriedBeforeFailing()
        {
            // Only the lowercase spelling is present, as Hammer++ writes some of them.
            string value = Invoke(name => name == "vis" ? "hl2/bin/vvis.exe" : null,
                "Half-Life 2", "Vis", "vis");

            Assert.Equal("hl2/bin/vvis.exe", value);
        }

        [Fact]
        public void TheFirstSpellingWinsWhenBothArePresent()
        {
            string value = Invoke(name => name == "Vis" ? "upper" : "lower", "Half-Life 2", "Vis", "vis");

            Assert.Equal("upper", value);
        }

        /// <summary>
        /// GetFullPath had a second operand that could never be true - every string starts with
        /// the empty string - so it contributed nothing to the condition. Removing it must not
        /// change which paths get resolved.
        /// </summary>
        [Theory]
        [InlineData(@"C:\Steam\hl2\bin\vbsp.exe", @"C:\Steam\hl2\bin\vbsp.exe")]  // absolute, unchanged
        [InlineData("hl2/bin/vbsp.exe", "hl2/bin/vbsp.exe")]                      // relative but not "..", unchanged
        public void AbsoluteAndPlainRelativePathsAreLeftAlone(string input, string expected)
        {
            var method = typeof(GameConfigurationParser)
                .GetMethod("GetFullPath", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            Assert.Equal(expected, (string)method!.Invoke(null, [input, @"C:\Steam\game\bin"])!);
        }

        [Fact]
        public void ADotDotPathIsResolvedAgainstTheBinFolder()
        {
            var method = typeof(GameConfigurationParser)
                .GetMethod("GetFullPath", BindingFlags.NonPublic | BindingFlags.Static);

            string resolved = (string)method!.Invoke(null, ["../hl2/bin/vbsp.exe", @"C:\Steam\game\bin"])!;

            Assert.DoesNotContain("..", resolved);
            Assert.EndsWith(Path.Combine("hl2", "bin", "vbsp.exe"), resolved);
        }
    }
}
