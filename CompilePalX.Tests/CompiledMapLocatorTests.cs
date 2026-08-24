using System;
using System.IO;
using CompilePalX.Compilers;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// Where the "show the compiled map" button points.
    ///
    /// The rule worth pinning is the stale-copy one. A BSP sitting in the game's maps folder from a
    /// compile last week is indistinguishable, by existence alone, from one copied five seconds ago
    /// - and preferring it on existence would open a folder containing an old map while telling the
    /// user it is the one they just built. That is a wrong answer that looks completely right, which
    /// is exactly the kind worth a test.
    /// </summary>
    public class CompiledMapLocatorTests : IDisposable
    {
        private readonly string root;
        private readonly string source;
        private readonly string maps;

        public CompiledMapLocatorTests()
        {
            root = Path.Combine(Path.GetTempPath(), "cp-locator-" + Guid.NewGuid().ToString("N"));
            source = Path.Combine(root, "mapsrc");
            maps = Path.Combine(root, "maps");

            Directory.CreateDirectory(source);
            Directory.CreateDirectory(maps);
        }

        public void Dispose()
        {
            try { Directory.Delete(root, recursive: true); } catch { /* a temp folder */ }
        }

        private string Write(string folder, string name, DateTime writtenUtc)
        {
            string path = Path.Combine(folder, name);

            File.WriteAllText(path, "not really a bsp");
            File.SetLastWriteTimeUtc(path, writtenUtc);

            return path;
        }

        private string Vmf => Path.Combine(source, "de_test.vmf");

        [Fact]
        public void Finds_the_bsp_beside_the_vmf_when_copy_did_not_run()
        {
            string beside = Write(source, "de_test.bsp", DateTime.UtcNow);

            Assert.Equal(beside, CompiledMapLocator.ResolveBsp(Vmf, isBsp: false, maps));
        }

        [Fact]
        public void Prefers_the_maps_folder_copy_when_copy_did_run()
        {
            var now = DateTime.UtcNow;

            Write(source, "de_test.bsp", now);
            string copied = Write(maps, "de_test.bsp", now);

            // Same instant counts as current: xcopy preserves the timestamp, so the copy routinely
            // matches its source exactly rather than being newer.
            Assert.Equal(copied, CompiledMapLocator.ResolveBsp(Vmf, isBsp: false, maps));
        }

        [Fact]
        public void Ignores_a_stale_copy_left_by_an_earlier_compile()
        {
            Write(maps, "de_test.bsp", DateTime.UtcNow.AddDays(-7));
            string beside = Write(source, "de_test.bsp", DateTime.UtcNow);

            // The regression this file exists for.
            Assert.Equal(beside, CompiledMapLocator.ResolveBsp(Vmf, isBsp: false, maps));
        }

        [Fact]
        public void Takes_the_copy_on_trust_when_there_is_nothing_to_compare_against()
        {
            string copied = Write(maps, "de_test.bsp", DateTime.UtcNow.AddDays(-7));

            // Nothing beside the source, so the copy is the only artifact there is - old or not.
            Assert.Equal(copied, CompiledMapLocator.ResolveBsp(Vmf, isBsp: false, maps));
        }

        [Fact]
        public void Returns_null_when_nothing_was_built()
        {
            // A compile that failed before VBSP wrote anything. Not an error, just no answer.
            Assert.Null(CompiledMapLocator.ResolveBsp(Vmf, isBsp: false, maps));
        }

        [Fact]
        public void A_bsp_queued_directly_is_its_own_answer()
        {
            string bsp = Write(source, "de_test.bsp", DateTime.UtcNow);

            Assert.Equal(bsp, CompiledMapLocator.ResolveBsp(bsp, isBsp: true, gameMapFolder: null));
        }

        [Fact]
        public void Works_with_no_game_configured()
        {
            string beside = Write(source, "de_test.bsp", DateTime.UtcNow);

            Assert.Equal(beside, CompiledMapLocator.ResolveBsp(Vmf, isBsp: false, gameMapFolder: null));
            Assert.Equal(beside, CompiledMapLocator.ResolveBsp(Vmf, isBsp: false, gameMapFolder: ""));
            Assert.Equal(beside, CompiledMapLocator.ResolveBsp(Vmf, isBsp: false, gameMapFolder: "   "));
        }

        [Fact]
        public void A_version_suffix_is_kept()
        {
            // MapName strips _rc2 and _final; the file on disk does not. Looking up the stripped
            // name would miss the map that was actually built.
            string vmf = Path.Combine(source, "de_test_rc2.vmf");
            string beside = Write(source, "de_test_rc2.bsp", DateTime.UtcNow);

            Assert.Equal(beside, CompiledMapLocator.ResolveBsp(vmf, isBsp: false, maps));
        }

        [Fact]
        public void Nothing_usable_returns_null_rather_than_throwing()
        {
            // Called from a click handler, so an exception here would surface as a crash dialog.
            Assert.Null(CompiledMapLocator.ResolveBsp("", isBsp: false, maps));
            Assert.Null(CompiledMapLocator.ResolveBsp("   ", isBsp: false, maps));
            Assert.Null(CompiledMapLocator.ResolveBsp(Vmf, isBsp: false, "a|b|c<>invalid"));
        }
    }
}
