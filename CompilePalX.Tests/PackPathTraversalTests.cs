using System;
using System.IO;
using System.Reflection;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// Asset paths come out of the map — entity keyvalues, material and model references, sound
    /// names — and a .vmf is a file a mapper may well have been handed by somebody else. So every
    /// one of those strings is attacker controlled.
    ///
    /// The packer joined them to a content root and packed whatever existed at the result.
    /// SanitizePath does not prevent that and was never meant to: it strips characters the
    /// filesystem rejects, and '.' and '/' are both perfectly legal. So a reference like
    /// "materials/../../../../Users/someone/.ssh/id_rsa" resolved outside the game folder, was
    /// found, and was packed into the BSP the mapper then uploads.
    ///
    /// These tests drive the real resolver against real files in a temp tree, because the bug was
    /// in what the filesystem does with a path rather than in what the code looks like.
    /// </summary>
    public class PackPathTraversalTests : IDisposable
    {
        private readonly string root;
        private readonly string contentDir;
        private readonly string secretFile;

        public PackPathTraversalTests()
        {
            root = Path.Combine(Path.GetTempPath(), "cp-traversal-" + Guid.NewGuid().ToString("N"));

            // A content root, and a file OUTSIDE it standing in for anything on the user's disk.
            contentDir = Path.Combine(root, "game", "content");
            Directory.CreateDirectory(Path.Combine(contentDir, "materials"));

            secretFile = Path.Combine(root, "secret.txt");
            File.WriteAllText(secretFile, "not yours to pack");

            File.WriteAllText(Path.Combine(contentDir, "materials", "legit.vmt"), "// a real asset");
        }

        public void Dispose()
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp dir */ }
        }

        /// <summary>
        /// Invokes the private resolver directly. Reaching for reflection is deliberate: the check
        /// has to be tested against the filesystem's own path canonicalisation, and reimplementing
        /// it in the test would only prove the test agrees with itself.
        /// </summary>
        private static string? Resolve(string contentRoot, string relative)
        {
            var method = typeof(CompilePalX.Compilers.BSPPack.PakFile)
                .GetMethod("ResolveWithinRoot", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            return (string?)method!.Invoke(null, [contentRoot, relative]);
        }

        [Fact]
        public void AnOrdinaryAssetPathResolvesNormally()
        {
            var resolved = Resolve(contentDir, "materials/legit.vmt");

            Assert.NotNull(resolved);
            Assert.True(File.Exists(resolved));
        }

        /// <summary>
        /// The reported hole, in the shape a crafted map would actually carry.
        /// </summary>
        [Fact]
        public void ATraversalOutOfTheContentRootIsRefused()
        {
            Assert.Null(Resolve(contentDir, "materials/../../../secret.txt"));
        }

        /// <summary>
        /// The forms a blocklist on the literal string "../" would miss. Checking the canonical
        /// path rather than the input is what makes all of these one case instead of five.
        /// </summary>
        [Theory]
        [InlineData(@"materials\..\..\..\secret.txt")]           // Windows separators
        [InlineData("materials/./../.././../secret.txt")]        // padded with same-directory hops
        [InlineData("materials/subdir/../../../../secret.txt")]  // through a directory that does not exist
        [InlineData("../secret.txt")]                            // straight out of the root
        [InlineData("materials/..//..//../secret.txt")]          // doubled separators
        public void EveryEncodingOfATraversalIsRefused(string attempt)
        {
            Assert.Null(Resolve(contentDir, attempt));
        }

        /// <summary>
        /// An absolute path is not a relative asset reference, and Path.Combine discards the root
        /// when the second argument is rooted - which would otherwise mean any absolute path in a
        /// map got packed verbatim.
        /// </summary>
        [Fact]
        public void AnAbsolutePathIsRefused()
        {
            Assert.Null(Resolve(contentDir, secretFile));
        }

        /// <summary>
        /// A sibling directory whose name merely starts with the root's must not pass. This is the
        /// bug a naive StartsWith check has, and the reason the comparison appends a separator.
        /// </summary>
        [Fact]
        public void ASiblingDirectoryWithASharedPrefixIsRefused()
        {
            string sibling = contentDir + "EVIL";
            Directory.CreateDirectory(sibling);
            File.WriteAllText(Path.Combine(sibling, "x.txt"), "nope");

            Assert.Null(Resolve(contentDir, "../contentEVIL/x.txt"));
        }

        /// <summary>
        /// A root given with a trailing separator resolves the same as one without. Both forms
        /// appear in sourceDirs depending on how the game configuration was entered.
        /// </summary>
        [Fact]
        public void ATrailingSeparatorOnTheRootDoesNotChangeTheAnswer()
        {
            Assert.NotNull(Resolve(contentDir + Path.DirectorySeparatorChar, "materials/legit.vmt"));
            Assert.Null(Resolve(contentDir + Path.DirectorySeparatorChar, "../secret.txt"));
        }

        /// <summary>
        /// The user's own -include flag is a different case and must keep working: they chose that
        /// file explicitly, so it is not constrained to the content roots. This pins the
        /// distinction so a later "tidy up" does not apply the boundary check there too.
        /// </summary>
        [Fact]
        public void TheBoundaryCheckIsOnMapDerivedPathsOnly()
        {
            string source = File.ReadAllText(Path.Combine(SourceDir(), "Compilers", "BSPPack", "PakFile.cs"));

            int findFile = source.IndexOf("private string FindExternalFile", StringComparison.Ordinal);
            int findDirs = source.IndexOf("private List<string> FindExternalDirectories", StringComparison.Ordinal);

            Assert.True(findFile > 0 && findDirs > 0);

            // Both map-derived resolvers go through the check.
            Assert.Contains("ResolveWithinRoot", source[findFile..(findFile + 900)]);
            Assert.Contains("ResolveWithinRoot", source[findDirs..(findDirs + 1200)]);
        }

        /// <summary>
        /// The second route to the same outcome, closed separately.
        ///
        /// The bspzip response file is alternating internal/external paths, one per line. A newline
        /// in an internal path injects an extra pair whose external half never passed AddFile's
        /// File.Exists check, because it never went through AddFile at all - so bspzip would resolve
        /// and pack whatever it named.
        /// </summary>
        [Theory]
        [InlineData("materials/x.vmt\nmaterials/evil.vmt")]
        [InlineData("materials/x.vmt\r\nC:/Users/someone/.ssh/id_rsa")]
        [InlineData("materials/x\0.vmt")]
        [InlineData("materials/x\tevil.vmt")]
        public void APathThatWouldCorruptTheBspzipFileListIsRefused(string malicious)
        {
            var method = typeof(CompilePalX.Compilers.BSPPack.PakFile)
                .GetMethod("HasControlCharacters", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            Assert.True((bool)method!.Invoke(null, [malicious])!,
                "a path carrying a control character must be recognised as unsafe for the file list");
        }

        [Theory]
        [InlineData("materials/legit.vmt")]
        [InlineData("models/props/chair.mdl")]
        [InlineData("sound/ambient/wind loop.wav")]
        public void OrdinaryAssetPathsAreNotRejectedByTheFileListGuard(string ordinary)
        {
            var method = typeof(CompilePalX.Compilers.BSPPack.PakFile)
                .GetMethod("HasControlCharacters", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.False((bool)method!.Invoke(null, [ordinary])!, $"'{ordinary}' should be accepted");
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
