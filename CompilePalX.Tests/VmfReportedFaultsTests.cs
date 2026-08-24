using System;
using System.IO;
using System.Linq;
using CompilePalX.Compiling;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// Faults VMFFIX finds but deliberately refuses to repair.
    ///
    /// The tests that matter most here are the negative ones: each of these has more than one
    /// reasonable answer, so the value of the feature depends on it continuing to report rather than
    /// quietly starting to "help".
    /// </summary>
    public class VmfReportedFaultsTests : IDisposable
    {
        private readonly string tempDir =
            Path.Combine(Path.GetTempPath(), "cpx-vmfreport-" + Guid.NewGuid().ToString("N"));

        public VmfReportedFaultsTests() => Directory.CreateDirectory(tempDir);

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

        private const string Faulty = """
            world
            {
            	"id" "1"
            	"classname" "worldspawn"
            	solid
            	{
            		"id" "2"
            		side
            		{
            			"material" "TOOLS/TOOLSORIGIN"
            		}
            	}
            	solid
            	{
            		"id" "3"
            		side
            		{
            			"material" "NATURE/DIRT"
            			vertices_plus
            			{
            				"v" "0 0 0"
            				"v" "1 0 0"
            				"v" "0 1 0"
            			}
            			dispinfo
            			{
            				"power" "3"
            			}
            		}
            	}
            }
            entity
            {
            	"id" "10"
            	"classname" "prop_static"
            	"model" ""
            	"origin" "0 0 0"
            }
            """;

        [Fact]
        public void AnOriginBrushInTheWorldIsReported()
        {
            var result = VmfFixes.ReportUnfixableFaults(Load(Faulty));

            Assert.Contains(result.Descriptions, d => d.Contains("origin brush"));
        }

        /// <summary>
        /// A displacement needs a four-vertex face; Hammer will let you put one on a triangle produced
        /// by a clip and says nothing until vbsp stops.
        /// </summary>
        [Fact]
        public void ADisplacementOnANonQuadFaceIsReported()
        {
            var result = VmfFixes.ReportUnfixableFaults(Load(Faulty));

            Assert.Contains(result.Descriptions, d => d.Contains("four vertices"));
        }

        [Fact]
        public void APropWithNoModelIsReported()
        {
            var result = VmfFixes.ReportUnfixableFaults(Load(Faulty));

            Assert.Contains(result.Descriptions, d => d.Contains("no model"));
        }

        /// <summary>
        /// Reporting must not edit. If this ever fails, something that should only be describing the
        /// map has started changing it.
        /// </summary>
        [Fact]
        public void ReportingNeverModifiesTheDocument()
        {
            var vmf = Load(Faulty);
            VmfFixes.ReportUnfixableFaults(vmf);

            Assert.False(vmf.Modified);
        }

        [Fact]
        public void AFourVertexDisplacementIsNotReported()
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
                			vertices_plus
                			{
                				"v" "0 0 0"
                				"v" "1 0 0"
                				"v" "1 1 0"
                				"v" "0 1 0"
                			}
                			dispinfo
                			{
                				"power" "3"
                			}
                		}
                	}
                }
                """);

            Assert.DoesNotContain(VmfFixes.ReportUnfixableFaults(vmf).Descriptions,
                d => d.Contains("four vertices"));
        }

        [Fact]
        public void ACleanMapReportsNothing()
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
                			"material" "BRICK/WALL"
                		}
                	}
                }
                entity
                {
                	"id" "10"
                	"classname" "prop_static"
                	"model" "models/props/x.mdl"
                	"origin" "0 0 0"
                }
                """);

            Assert.Empty(VmfFixes.ReportUnfixableFaults(vmf).Descriptions);
        }
    }
}
