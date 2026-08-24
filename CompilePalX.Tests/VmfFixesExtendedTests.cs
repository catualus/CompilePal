using System;
using System.IO;
using System.Linq;
using CompilePalX.Compiling;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// The fixers added after the second review: reversed prop fade distances, a skyname written as a
    /// file path, and brush entities that contain no brushes.
    ///
    /// Each is here because it is a defect with exactly one correct outcome. Anything needing a
    /// judgement call is deliberately NOT a fixer, and several tests below pin that boundary by
    /// asserting the fixers leave the ambiguous cases alone - that is the part most at risk of being
    /// "improved" later into something that quietly rewrites someone's map on a guess.
    /// </summary>
    public class VmfFixesExtendedTests : IDisposable
    {
        private readonly string tempDir =
            Path.Combine(Path.GetTempPath(), "cpx-vmfext-" + Guid.NewGuid().ToString("N"));

        public VmfFixesExtendedTests() => Directory.CreateDirectory(tempDir);

        public void Dispose()
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }

        private VmfDocument Load(string contents)
        {
            string path = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".vmf");
            File.WriteAllText(path, contents.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            return VmfDocument.Load(path);
        }

        private string SaveAndRead(VmfDocument vmf)
        {
            string path = Path.Combine(tempDir, "out.vmf");
            vmf.Save(path);
            return File.ReadAllText(path);
        }

        // ------------------------------------------------------------------- fade distances

        private const string Fades = """
            entity
            {
            	"id" "1"
            	"classname" "prop_static"
            	"model" "models/props/x.mdl"
            	"fademindist" "900"
            	"fademaxdist" "500"
            	"origin" "0 0 0"
            }
            entity
            {
            	"id" "2"
            	"classname" "prop_dynamic"
            	"fademindist" "500"
            	"fademaxdist" "900"
            	"origin" "1 1 1"
            }
            entity
            {
            	"id" "3"
            	"classname" "prop_static"
            	"fademindist" "-1"
            	"fademaxdist" "900"
            	"origin" "2 2 2"
            }
            """;

        [Fact]
        public void ReversedFadeDistancesAreSwapped()
        {
            var vmf = Load(Fades);
            var result = VmfFixes.FixPropFadeDistances(vmf);

            Assert.Equal(1, result.Count);

            var repaired = vmf.Entities.Single(e => vmf.GetValue(e, "id") == "1");
            Assert.Equal("500", vmf.GetValue(repaired, "fademindist"));
            Assert.Equal("900", vmf.GetValue(repaired, "fademaxdist"));
        }

        [Fact]
        public void CorrectlyOrderedFadeDistancesAreLeftAlone()
        {
            var vmf = Load(Fades);
            VmfFixes.FixPropFadeDistances(vmf);

            var untouched = vmf.Entities.Single(e => vmf.GetValue(e, "id") == "2");
            Assert.Equal("500", vmf.GetValue(untouched, "fademindist"));
            Assert.Equal("900", vmf.GetValue(untouched, "fademaxdist"));
        }

        /// <summary>
        /// -1 is the sentinel for "no minimum, use fademaxdist only", not a distance. Reading it as a
        /// reversed pair would rewrite an entirely ordinary prop and change how it fades.
        /// </summary>
        [Fact]
        public void TheMinusOneFadeSentinelIsNotTreatedAsReversed()
        {
            var vmf = Load(Fades);
            VmfFixes.FixPropFadeDistances(vmf);

            var sentinel = vmf.Entities.Single(e => vmf.GetValue(e, "id") == "3");
            Assert.Equal("-1", vmf.GetValue(sentinel, "fademindist"));
        }

        // ------------------------------------------------------------------- skyname

        // skyname now lives in VmfStructuralFixesTests, against a real world block. The fixtures
        // here wrote worldspawn as an entity, which is not how Hammer stores it - so the tests
        // passed while the fixer could never fire on an actual map.

        // ------------------------------------------------------------------- empty brush entities

        private const string BrushEntities = """
            entity
            {
            	"id" "1"
            	"classname" "func_detail"
            	"targetname" "orphan"
            }
            entity
            {
            	"id" "2"
            	"classname" "func_detail"
            	solid
            	{
            		"id" "9"
            		side
            		{
            			"material" "TOOLS/TOOLSNODRAW"
            		}
            	}
            }
            entity
            {
            	"id" "3"
            	"classname" "prop_static"
            	"model" "models/props/x.mdl"
            	"origin" "0 0 0"
            }
            entity
            {
            	"id" "4"
            	"classname" "trigger_multiple"
            	"targetname" "ambiguous"
            }
            """;

        [Fact]
        public void ABrushEntityWithNoBrushesIsRemoved()
        {
            var vmf = Load(BrushEntities);

            Assert.Equal(1, VmfFixes.RemoveEmptyBrushEntities(vmf).Count);
            Assert.DoesNotContain("orphan", SaveAndRead(vmf));
        }

        [Fact]
        public void ABrushEntityThatHasBrushesSurvives()
        {
            var vmf = Load(BrushEntities);
            VmfFixes.RemoveEmptyBrushEntities(vmf);

            string saved = SaveAndRead(vmf);
            Assert.Contains("TOOLS/TOOLSNODRAW", saved);

            // and the solid's own id line, so we know the whole block survived rather than half of it
            Assert.Contains("\"id\" \"9\"", saved);
        }

        /// <summary>
        /// trigger_multiple is deliberately not in the brush-only list: an empty one might be
        /// intentional, so removing it would be a guess rather than a fix.
        /// </summary>
        [Fact]
        public void AnEntityOutsideTheBrushOnlyListIsLeftAlone()
        {
            var vmf = Load(BrushEntities);
            VmfFixes.RemoveEmptyBrushEntities(vmf);

            Assert.Contains("ambiguous", SaveAndRead(vmf));
        }

        [Fact]
        public void APointEntityIsNeverRemoved()
        {
            var vmf = Load(BrushEntities);
            VmfFixes.RemoveEmptyBrushEntities(vmf);

            Assert.Contains("models/props/x.mdl", SaveAndRead(vmf));
        }

        /// <summary>
        /// Every entity holds absolute line numbers into one shared list, so removing two of them has
        /// to suppress lines rather than delete them: deleting the first outright would shift the
        /// second's recorded range and cut the wrong lines out of the file.
        /// </summary>
        [Fact]
        public void RemovingTwoEntitiesCutsBothAndNothingElse()
        {
            var vmf = Load("""
                entity
                {
                	"id" "1"
                	"classname" "func_detail"
                	"targetname" "first_orphan"
                }
                entity
                {
                	"id" "2"
                	"classname" "func_brush"
                	"targetname" "second_orphan"
                }
                entity
                {
                	"id" "3"
                	"classname" "prop_static"
                	"targetname" "keep_me"
                	"origin" "0 0 0"
                }
                """);

            Assert.Equal(2, VmfFixes.RemoveEmptyBrushEntities(vmf).Count);

            string saved = SaveAndRead(vmf);
            Assert.DoesNotContain("first_orphan", saved);
            Assert.DoesNotContain("second_orphan", saved);
            Assert.Contains("keep_me", saved);
        }
    }
}
