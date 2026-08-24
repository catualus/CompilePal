using System;
using System.IO;
using System.Linq;
using CompilePalX.Compiling;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// The fixers that change a map's structure rather than one of its values: moving displacements
    /// into the world, splitting multi-brush areaportals, and clamping overlay render order.
    ///
    /// Every fixture here uses a real <c>world { }</c> block, because that is the shape of an actual
    /// VMF. An earlier fixture wrote worldspawn as an <c>entity</c> - which is how Valve's
    /// documentation talks about it, and is not how Hammer writes it - and a fixer written against
    /// that fixture passed its test while never once firing on a real map.
    /// </summary>
    public class VmfStructuralFixesTests : IDisposable
    {
        private readonly string tempDir =
            Path.Combine(Path.GetTempPath(), "cpx-vmfstruct-" + Guid.NewGuid().ToString("N"));

        public VmfStructuralFixesTests() => Directory.CreateDirectory(tempDir);

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

        /// <summary>Reloading the saved file is what proves an edit produced a VMF that still parses.</summary>
        private VmfDocument SaveAndReload(VmfDocument vmf)
        {
            string path = Path.Combine(tempDir, "reload.vmf");
            vmf.Save(path);
            return VmfDocument.Load(path);
        }

        // ------------------------------------------------------------------- the world block

        private const string WithWorld = """
            versioninfo
            {
            	"editorversion" "400"
            }
            world
            {
            	"id" "1"
            	"classname" "worldspawn"
            	"skyname" "materials/skybox/sky_day01_01.vmt"
            	solid
            	{
            		"id" "2"
            		side
            		{
            			"material" "TOOLS/TOOLSNODRAW"
            		}
            	}
            }
            entity
            {
            	"id" "9"
            	"classname" "prop_static"
            	"model" "models/props/x.mdl"
            	"origin" "0 0 0"
            }
            """;

        /// <summary>
        /// worldspawn lives in a top-level "world" block, not an "entity" one. If this is not indexed,
        /// nothing that reads a worldspawn keyvalue can ever work.
        /// </summary>
        [Fact]
        public void TheWorldBlockIsIndexedAndIsNotAnEntity()
        {
            var vmf = Load(WithWorld);

            Assert.NotNull(vmf.World);
            Assert.Equal("worldspawn", vmf.Classname(vmf.World!));
            Assert.True(vmf.World!.IsWorld);
            Assert.Single(vmf.Entities.Where(e => e.IsWorld));
        }

        [Fact]
        public void ASkyNameOnTheRealWorldBlockIsCorrected()
        {
            var vmf = Load(WithWorld);

            Assert.Equal(1, VmfFixes.FixSkyName(vmf).Count);
            Assert.Equal("sky_day01_01", vmf.GetValue(vmf.World!, "skyname"));
        }

        [Fact]
        public void TheWorldIsNeverRemovedAsAnEmptyBrushEntity()
        {
            var vmf = Load(WithWorld);
            VmfFixes.RemoveEmptyBrushEntities(vmf);

            Assert.Contains("worldspawn", SaveAndRead(vmf));
        }

        // ------------------------------------------------------------------- displacements

        private const string DisplacementInFuncDetail = """
            world
            {
            	"id" "1"
            	"classname" "worldspawn"
            	solid
            	{
            		"id" "2"
            		side
            		{
            			"material" "TOOLS/TOOLSNODRAW"
            		}
            	}
            }
            entity
            {
            	"id" "10"
            	"classname" "func_detail"
            	solid
            	{
            		"id" "11"
            		side
            		{
            			"material" "NATURE/DIRT"
            			dispinfo
            			{
            				"power" "3"
            			}
            		}
            	}
            }
            entity
            {
            	"id" "20"
            	"classname" "func_detail"
            	solid
            	{
            		"id" "21"
            		side
            		{
            			"material" "BRICK/WALL"
            		}
            	}
            	solid
            	{
            		"id" "22"
            		side
            		{
            			"material" "NATURE/SAND"
            			dispinfo
            			{
            				"power" "2"
            			}
            		}
            	}
            }
            """;

        [Fact]
        public void ADisplacementTiedToABrushEntityIsMovedIntoTheWorld()
        {
            var vmf = Load(DisplacementInFuncDetail);
            var result = VmfFixes.MoveDisplacementsToWorld(vmf);

            Assert.Equal(2, result.Count);

            var reloaded = SaveAndReload(vmf);

            // Both displacement solids now belong to the world.
            var worldSolids = reloaded.World!.Solids.ToList();
            Assert.Equal(3, worldSolids.Count);
            Assert.Equal(2, worldSolids.Count(sd => reloaded.BlockContains(sd, "dispinfo")));
        }

        /// <summary>
        /// A func_detail that held nothing but displacements is empty once they leave, and an empty
        /// brush entity is itself fatal - so trading one error for another would not be a fix.
        /// </summary>
        [Fact]
        public void AnEntityEmptiedByTheMoveIsRemoved()
        {
            var vmf = Load(DisplacementInFuncDetail);
            VmfFixes.MoveDisplacementsToWorld(vmf);

            var reloaded = SaveAndReload(vmf);

            // Entity 10 held only the displacement; entity 20 keeps its ordinary brush.
            var detail = reloaded.Entities.Where(e => !e.IsWorld).ToList();
            Assert.Single(detail);
            Assert.Equal("21", reloaded.GetValue(detail[0].Solids.First(), "id"));
        }

        [Fact]
        public void AnOrdinaryBrushInTheSameEntityStaysWhereItIs()
        {
            var vmf = Load(DisplacementInFuncDetail);
            VmfFixes.MoveDisplacementsToWorld(vmf);

            string saved = SaveAndRead(vmf);
            Assert.Contains("BRICK/WALL", saved);
            Assert.Contains("NATURE/DIRT", saved);
            Assert.Contains("NATURE/SAND", saved);
        }

        [Fact]
        public void ADisplacementAlreadyInTheWorldIsLeftAlone()
        {
            var vmf = Load("""
                world
                {
                	"id" "1"
                	"classname" "worldspawn"
                	solid
                	{
                		"id" "2"
                		side
                		{
                			"material" "NATURE/DIRT"
                			dispinfo
                			{
                				"power" "3"
                			}
                		}
                	}
                }
                """);

            Assert.Equal(0, VmfFixes.MoveDisplacementsToWorld(vmf).Count);
            Assert.False(vmf.Modified);
        }

        // ------------------------------------------------------------------- areaportals

        private const string MultiBrushAreaportal = """
            world
            {
            	"id" "1"
            	"classname" "worldspawn"
            }
            entity
            {
            	"id" "30"
            	"classname" "func_areaportal"
            	"targetname" "portal_a"
            	solid
            	{
            		"id" "31"
            		side
            		{
            			"material" "TOOLS/TOOLSAREAPORTAL"
            		}
            	}
            	solid
            	{
            		"id" "32"
            		side
            		{
            			"material" "TOOLS/TOOLSAREAPORTAL"
            		}
            	}
            	editor
            	{
            		"color" "0 0 255"
            	}
            }
            """;

        [Fact]
        public void AMultiBrushAreaportalIsSplitIntoOnePerBrush()
        {
            var vmf = Load(MultiBrushAreaportal);

            // Two brushes become two entities: one more than there was.
            Assert.Equal(1, VmfFixes.SplitMultiBrushAreaportals(vmf).Count);

            var reloaded = SaveAndReload(vmf);
            var portals = reloaded.Entities
                .Where(e => reloaded.Classname(e) == "func_areaportal")
                .ToList();

            Assert.Equal(2, portals.Count);
            Assert.All(portals, p => Assert.Single(p.Solids));
            Assert.All(portals, p => Assert.Equal("portal_a", reloaded.GetValue(p, "targetname")));
        }

        /// <summary>
        /// Each copy has to keep its OWN brush, not a copy of the same one - otherwise the split
        /// produces two entities sealing the same opening and leaves the other one unsealed.
        /// </summary>
        [Fact]
        public void EachSplitPortalKeepsADifferentBrush()
        {
            var vmf = Load(MultiBrushAreaportal);
            VmfFixes.SplitMultiBrushAreaportals(vmf);

            var reloaded = SaveAndReload(vmf);
            var ids = reloaded.Entities
                .Where(e => reloaded.Classname(e) == "func_areaportal")
                .Select(e => reloaded.GetValue(e.Solids.First(), "id"))
                .OrderBy(x => x)
                .ToList();

            Assert.Equal(new[] { "31", "32" }, ids);
        }

        [Fact]
        public void ASingleBrushAreaportalIsUntouched()
        {
            var vmf = Load("""
                world
                {
                	"id" "1"
                	"classname" "worldspawn"
                }
                entity
                {
                	"id" "30"
                	"classname" "func_areaportal"
                	solid
                	{
                		"id" "31"
                		side
                		{
                			"material" "TOOLS/TOOLSAREAPORTAL"
                		}
                	}
                }
                """);

            Assert.Equal(0, VmfFixes.SplitMultiBrushAreaportals(vmf).Count);
            Assert.False(vmf.Modified);
        }

        // ------------------------------------------------------------------- overlay render order

        [Theory]
        [InlineData("5", "3")]
        [InlineData("-2", "0")]
        public void AnInvalidOverlayRenderOrderIsClampedIntoRange(string written, string expected)
        {
            var vmf = Load($$"""
                world
                {
                	"id" "1"
                	"classname" "worldspawn"
                }
                entity
                {
                	"id" "40"
                	"classname" "info_overlay"
                	"RenderOrder" "{{written}}"
                	"origin" "0 0 0"
                }
                """);

            Assert.Equal(1, VmfFixes.FixOverlayRenderOrder(vmf).Count);

            var overlay = vmf.Entities.Single(e => vmf.Classname(e) == "info_overlay");
            Assert.Equal(expected, vmf.GetValue(overlay, "RenderOrder"));
        }

        [Theory]
        [InlineData("0")]
        [InlineData("3")]
        public void AValidOverlayRenderOrderIsLeftAlone(string written)
        {
            var vmf = Load($$"""
                world
                {
                	"id" "1"
                	"classname" "worldspawn"
                }
                entity
                {
                	"id" "40"
                	"classname" "info_overlay"
                	"RenderOrder" "{{written}}"
                	"origin" "0 0 0"
                }
                """);

            Assert.Equal(0, VmfFixes.FixOverlayRenderOrder(vmf).Count);
            Assert.False(vmf.Modified);
        }

        /// <summary>
        /// A document that has not been edited must save back byte for byte.
        ///
        /// This is the whole justification for the line-based design: VMFFIX writes over the mapper's
        /// own source file, so anything it does not deliberately change has to survive untouched -
        /// every float format, every tab, every ordering quirk Hammer produced. Verified separately
        /// against a real 53 MB map, where load and save round-trip identically in about a second.
        /// </summary>
        [Fact]
        public void SavingAnUneditedDocumentReproducesTheFileExactly()
        {
            string path = Path.Combine(tempDir, "identity.vmf");
            string original = string.Join("\n", WithWorld, DisplacementInFuncDetail, MultiBrushAreaportal)
                .Replace("\r\n", "\n")
                .Replace("\n", "\r\n");
            File.WriteAllText(path, original);

            var vmf = VmfDocument.Load(path);
            Assert.False(vmf.Modified);

            string outPath = Path.Combine(tempDir, "identity-out.vmf");
            vmf.Save(outPath);

            Assert.Equal(original, File.ReadAllText(outPath));
        }

        // ------------------------------------------------------------------- edit bookkeeping

        /// <summary>
        /// Removals, insertions and moves all record absolute line numbers into one shared list, so
        /// several of them in a single run must not disturb each other's ranges. Running every fixer
        /// over one document and then reloading the result is what proves it.
        /// </summary>
        [Fact]
        public void EveryFixerCanRunOnOneDocumentAndStillProduceAParseableVmf()
        {
            var vmf = Load(DisplacementInFuncDetail);

            VmfFixes.FixOverlayRenderOrder(vmf);
            VmfFixes.MoveDisplacementsToWorld(vmf);
            VmfFixes.SplitMultiBrushAreaportals(vmf);
            VmfFixes.RemoveEmptyBrushEntities(vmf);
            VmfFixes.FixSkyName(vmf);

            var reloaded = SaveAndReload(vmf);

            Assert.NotNull(reloaded.World);
            Assert.Equal(3, reloaded.World!.Solids.Count());

            // Braces still balance, which is the crudest but most important property of the output.
            string saved = SaveAndRead(vmf);
            Assert.Equal(saved.Count(c => c == '{'), saved.Count(c => c == '}'));
        }
    }
}
