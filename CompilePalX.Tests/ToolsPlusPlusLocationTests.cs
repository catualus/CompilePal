using System;
using System.IO;
using System.Text;
using CompilePalX;
using CompilePalX.Configuration;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// Finding tools++ once it stopped living in a game's bin folder.
    ///
    /// The tools shipped for years as drop-in replacements extracted over bin/win64, so the search was
    /// "look next to the configured compiler" and that was enough. The current builds are statically
    /// linked and normally kept in one folder outside every game, which nothing in a game configuration
    /// points at - so a user with tools++ installed had them silently ignored.
    ///
    /// Two things had to change and both are pinned here: a folder the user can name, and detection that
    /// recognises the standalone builds at all. The standalone bspzip++ carries no version banner, no
    /// author tag and no marker of any kind, so scanning its contents can only ever come back negative.
    /// </summary>
    [Collection("Settings")]
    public class ToolsPlusPlusLocationTests : IDisposable
    {
        private readonly string root;
        private readonly string standalone;
        private readonly string gameBin;

        private readonly string? originalFolder;
        private readonly bool originalPrefer;

        public ToolsPlusPlusLocationTests()
        {
            root = Path.Combine(Path.GetTempPath(), "CompilePalToolsPlusPlus_" + Guid.NewGuid().ToString("N"));
            standalone = Path.Combine(root, "Tools++");
            gameBin = Path.Combine(root, "game", "bin", "win64");

            Directory.CreateDirectory(standalone);
            Directory.CreateDirectory(gameBin);

            originalFolder = ConfigurationManager.Settings.ToolsPlusPlusFolder;
            originalPrefer = ConfigurationManager.Settings.PreferToolsPlusPlusBinaries;

            ConfigurationManager.Settings.PreferToolsPlusPlusBinaries = true;
            ConfigurationManager.Settings.ToolsPlusPlusFolder = standalone;

            ToolsPlusPlusDetector.Invalidate();
        }

        public void Dispose()
        {
            ConfigurationManager.Settings.ToolsPlusPlusFolder = originalFolder;
            ConfigurationManager.Settings.PreferToolsPlusPlusBinaries = originalPrefer;
            ToolsPlusPlusDetector.Invalidate();

            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // a temp folder that outlives the run is not worth failing a test over
            }
        }

        /// <summary>Writes a stand-in binary. Content is searched as ASCII, so plain text is enough.</summary>
        private static string WriteBinary(string folder, string fileName, string content = "not a compiler")
        {
            string path = Path.Combine(folder, fileName);
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes(content));
            return path;
        }

        [Fact]
        public void AStandaloneFolderSuppliesTheCompiler()
        {
            string expected = WriteBinary(standalone, "vbsp++.exe");
            string configured = WriteBinary(gameBin, "vbsp.exe");

            Assert.Equal(expected, ToolsPlusPlusDetector.PreviewResolveBinary("VBSP", configured));
        }

        [Fact]
        public void BspzipIsFoundEvenThoughItCarriesNoMarker()
        {
            // The real bspzip++.exe contains neither "BSPZIP++" nor an author tag - verified against the
            // 2026-09-04 build. Recognising it by content is impossible, so the name has to decide.
            string expected = WriteBinary(standalone, "bspzip++.exe", "nothing identifying in here at all");
            string configured = WriteBinary(gameBin, "bspzip.exe");

            Assert.Equal(expected, ToolsPlusPlusDetector.PreviewResolveBinary("BSPZIP", configured));
        }

        [Fact]
        public void TheStandaloneBannerIsRecognised()
        {
            // Named like the stock binary, so only the banner can identify it. The standalone builds
            // print "Breadworks - vrad++ (date)" where the Hammer++ era ones printed "VRAD++".
            string expected = WriteBinary(standalone, "vrad.exe", "Breadworks - vrad++ (Sep  4 2026)");
            string configured = WriteBinary(gameBin, "vrad.exe");

            Assert.Equal(expected, ToolsPlusPlusDetector.PreviewResolveBinary("VRAD", configured));
        }

        [Fact]
        public void TheOlderHammerPlusPlusBannerStillWorks()
        {
            string expected = WriteBinary(standalone, "vvis.exe", "VVIS++ by ficool2");
            string configured = WriteBinary(gameBin, "vvis.exe");

            Assert.Equal(expected, ToolsPlusPlusDetector.PreviewResolveBinary("VVIS", configured));
        }

        [Fact]
        public void AStockBinarySittingInTheFolderIsNotMistakenForToolsPlusPlus()
        {
            WriteBinary(standalone, "vrad.exe", "Valve Software - vrad.exe");
            string configured = WriteBinary(gameBin, "vrad.exe");

            Assert.Equal(configured, ToolsPlusPlusDetector.PreviewResolveBinary("VRAD", configured));
        }

        [Fact]
        public void TheStandaloneFolderWinsOverACopyInTheGameBinFolder()
        {
            // The bin folder copy is the one that goes stale: it was dropped in once and never updated,
            // while the folder the user pointed at is the one they replace on each release.
            string expected = WriteBinary(standalone, "vbsp++.exe");
            WriteBinary(gameBin, "vbspplusplus.exe");
            string configured = WriteBinary(gameBin, "vbsp.exe");

            Assert.Equal(expected, ToolsPlusPlusDetector.PreviewResolveBinary("VBSP", configured));
        }

        [Fact]
        public void ACopyInTheGameBinFolderIsStillFoundWhenTheFolderHasNone()
        {
            // Pointed at an empty folder rather than left unset: unset falls through to auto-detection,
            // which probes the real Documents and Downloads of whoever is running the tests.
            string expected = WriteBinary(gameBin, "vbspplusplus.exe");
            string configured = WriteBinary(gameBin, "vbsp.exe");

            Assert.Equal(expected, ToolsPlusPlusDetector.PreviewResolveBinary("VBSP", configured));
        }

        [Fact]
        public void NothingConfiguredForBspzipStillResolvesFromTheFolder()
        {
            // Game configurations written before bspzip mattered leave it empty. A standalone install
            // supplies one regardless, and that is the whole point of repacking with bspzip++.
            string expected = WriteBinary(standalone, "bspzip++.exe");

            Assert.Equal(expected, ToolsPlusPlusDetector.PreviewResolveBinary("BSPZIP", ""));
        }

        [Fact]
        public void AConfiguredPathIsKeptWhenTheFolderHasNothingToOffer()
        {
            string configured = WriteBinary(gameBin, "vbsp.exe");

            Assert.Equal(configured, ToolsPlusPlusDetector.PreviewResolveBinary("VBSP", configured));
        }

        [Fact]
        public void TurningThePreferenceOffRunsExactlyWhatIsConfigured()
        {
            WriteBinary(standalone, "vbsp++.exe");
            string configured = WriteBinary(gameBin, "vbsp.exe");

            ConfigurationManager.Settings.PreferToolsPlusPlusBinaries = false;
            ToolsPlusPlusDetector.Invalidate();

            Assert.Equal(configured, ToolsPlusPlusDetector.PreviewResolveBinary("VBSP", configured));
        }

        [Fact]
        public void ToolsInFolderNamesWhatItFound()
        {
            WriteBinary(standalone, "vbsp++.exe");
            WriteBinary(standalone, "vrad++.exe");

            Assert.Equal(new[] { "VBSP", "VRAD" }, ToolsPlusPlusDetector.ToolsInFolder(standalone));
        }

        [Fact]
        public void AFolderWithTheRightNameAndNoToolsInItCountsForNothing()
        {
            // What the settings window needs to distinguish: a folder that exists and is empty looks
            // exactly like a correctly configured one until it is asked what is inside.
            Assert.Empty(ToolsPlusPlusDetector.ToolsInFolder(standalone));
            Assert.Empty(ToolsPlusPlusDetector.ToolsInFolder(Path.Combine(root, "does not exist")));
            Assert.Empty(ToolsPlusPlusDetector.ToolsInFolder(""));
        }

        [Fact]
        public void ToolsUnpackedIntoABinSubfolderAreStillFound()
        {
            string bin = Path.Combine(standalone, "bin");
            Directory.CreateDirectory(bin);
            string expected = WriteBinary(bin, "vvis++.exe");

            Assert.Equal(expected, ToolsPlusPlusDetector.PreviewResolveBinary("VVIS", Path.Combine(gameBin, "vvis.exe")));
        }
    }
}
