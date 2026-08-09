using System;
using System.IO;
using CompilePalX.Compiling;
using Xunit;

namespace CompilePalX.Tests
{
    public class VmfFixesTests : IDisposable
    {
        private readonly string tempDir =
            Path.Combine(Path.GetTempPath(), "cpx-vmftests-" + Guid.NewGuid().ToString("N"));

        public VmfFixesTests() => Directory.CreateDirectory(tempDir);

        public void Dispose()
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }

        private string WriteVmf(string contents)
        {
            string path = Path.Combine(tempDir, "test.vmf");
            File.WriteAllText(path, contents.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            return path;
        }

        /// <summary>
        /// One reversed light (fifty 67 / zero 40), one correct, one with falloff disabled, plus a
        /// brush entity whose nested solid block carries keys that must not be mistaken for the
        /// entity's own.
        /// </summary>
        private const string Sample = """
            versioninfo
            {
            	"editorversion" "400"
            }
            entity
            {
            	"id" "1"
            	"classname" "light_spot"
            	"_fifty_percent_distance" "67"
            	"_zero_percent_distance" "40"
            	"origin" "-1566 1699 66"
            	editor
            	{
            		"color" "220 30 220"
            	}
            }
            entity
            {
            	"id" "2"
            	"classname" "light"
            	"_fifty_percent_distance" "40"
            	"_zero_percent_distance" "67"
            	"origin" "0 0 0"
            }
            entity
            {
            	"id" "3"
            	"classname" "light"
            	"_fifty_percent_distance" "0"
            	"_zero_percent_distance" "0"
            	"origin" "1 1 1"
            }
            entity
            {
            	"id" "4"
            	"classname" "func_detail"
            	solid
            	{
            		side
            		{
            			"material" "CONCRETE/CONCRETEFLOOR038C"
            			"classname" "not_the_entity_classname"
            		}
            	}
            }
            """;

        [Fact]
        public void SwapsOnlyTheReversedLight()
        {
            var vmf = VmfDocument.Load(WriteVmf(Sample));

            var result = VmfFixes.FixLightFalloff(vmf);

            Assert.Equal(1, result.Count);
            Assert.Contains("-1566 1699 66", Assert.Single(result.Descriptions));

            var reversed = vmf.Entities[0];
            Assert.Equal("40", vmf.GetValue(reversed, "_fifty_percent_distance"));
            Assert.Equal("67", vmf.GetValue(reversed, "_zero_percent_distance"));
        }

        [Fact]
        public void LeavesCorrectlyOrderedFalloffAlone()
        {
            var vmf = VmfDocument.Load(WriteVmf(Sample));
            VmfFixes.FixLightFalloff(vmf);

            var ok = vmf.Entities[1];
            Assert.Equal("40", vmf.GetValue(ok, "_fifty_percent_distance"));
            Assert.Equal("67", vmf.GetValue(ok, "_zero_percent_distance"));
        }

        /// <summary>Zero means "no custom falloff", not a distance, so it is not reversed.</summary>
        [Fact]
        public void LeavesDisabledFalloffAlone()
        {
            var vmf = VmfDocument.Load(WriteVmf(Sample));
            VmfFixes.FixLightFalloff(vmf);

            var disabled = vmf.Entities[2];
            Assert.Equal("0", vmf.GetValue(disabled, "_fifty_percent_distance"));
            Assert.Equal("0", vmf.GetValue(disabled, "_zero_percent_distance"));
        }

        /// <summary>
        /// A brush entity's solid/side blocks contain their own "classname"-looking keys. Reading
        /// them as the entity's would misidentify the entity entirely.
        /// </summary>
        [Fact]
        public void NestedBlockKeysAreNotReadAsEntityKeys()
        {
            var vmf = VmfDocument.Load(WriteVmf(Sample));

            var brush = vmf.Entities[3];
            Assert.Equal("func_detail", vmf.Classname(brush));
            Assert.Null(vmf.GetValue(brush, "material"));
        }

        [Fact]
        public void UnmodifiedDocumentRoundTripsByteForByte()
        {
            string path = WriteVmf(Sample);
            byte[] before = File.ReadAllBytes(path);

            var vmf = VmfDocument.Load(path);
            Assert.False(vmf.Modified);

            string outPath = Path.Combine(tempDir, "roundtrip.vmf");
            vmf.Save(outPath);

            Assert.Equal(before, File.ReadAllBytes(outPath));
        }

        [Fact]
        public void SavingPreservesEverythingExceptTheChangedValues()
        {
            string path = WriteVmf(Sample);
            var vmf = VmfDocument.Load(path);
            VmfFixes.FixLightFalloff(vmf);

            string outPath = Path.Combine(tempDir, "fixed.vmf");
            vmf.Save(outPath);

            string original = File.ReadAllText(path);
            string fixedText = File.ReadAllText(outPath);

            Assert.Equal(original.Length, fixedText.Length);   // only two values swapped, same digits
            Assert.NotEqual(original, fixedText);

            // Indentation and quoting must survive, or Hammer sees a malformed file.
            Assert.Contains("\t\"_fifty_percent_distance\" \"40\"", fixedText);
            Assert.Contains("\t\"_zero_percent_distance\" \"67\"", fixedText);

            // Everything not touched is still there verbatim.
            Assert.Contains("\"editorversion\" \"400\"", fixedText);
            Assert.Contains("\"material\" \"CONCRETE/CONCRETEFLOOR038C\"", fixedText);
        }

        [Fact]
        public void CollectsBrushMaterials()
        {
            var vmf = VmfDocument.Load(WriteVmf(Sample));

            var materials = vmf.CollectMaterials();

            Assert.Contains("CONCRETE/CONCRETEFLOOR038C", materials);
        }

        [Fact]
        public void UnknownModelIsLeftAloneRatherThanConverted()
        {
            // No content directories means nothing can be resolved, and converting a prop on the
            // strength of a failed lookup would change the map because we could not see something.
            var vmf = VmfDocument.Load(WriteVmf(Sample));

            var result = VmfFixes.FixStaticProps(vmf, Array.Empty<string>());

            Assert.Equal(0, result.Count);
        }
    }
}
