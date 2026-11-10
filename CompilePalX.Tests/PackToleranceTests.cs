using System;
using System.IO;
using CompilePalX.Compilers.BSPPack;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// A model's keyvalues can hold several top-level blocks: sittable chairs and vehicle seats
    /// carry prop_data and vehicle_entry/exit as separate "mdlkeyvalue" blocks. The strict parser
    /// threw on that, out of the whole model reader, and the model was packed with none of its
    /// materials.
    /// </summary>
    public class ModelKeyValueTests
    {
        [Fact]
        public void SeveralKeyvalueBlocksStillYieldTheDoorDamageModels()
        {
            const string text =
                "mdlkeyvalue\n{\nprop_data {\n\"base\" \"Wooden.Medium\"\n}\n}\n" +
                "mdlkeyvalue\n{\ndoor_options {\ndefaults {\n\"damage1\" \"props_doors/door_damage1\"\n}\n}\n}\n";

            var models = AssetUtils.ModelsFromKeyValues(text, "chair.mdl");

            Assert.Equal(["models\\props_doors/door_damage1.mdl"], models);
        }

        [Fact]
        public void OneBlockReadsAsItAlwaysDid()
        {
            const string text = "mdlkeyvalue\n{\ndoor_options {\ndefaults {\n\"damage1\" \"a\"\n\"damage2\" \"b\"\n}\n}\n}\n";

            var models = AssetUtils.ModelsFromKeyValues(text, "door.mdl");

            Assert.Equal(["models\\a.mdl", "models\\b.mdl"], models);
        }

        [Fact]
        public void UnreadableKeyvaluesCostOnlyTheDamageModels()
        {
            var models = AssetUtils.ModelsFromKeyValues("mdlkeyvalue\n{\n\"unterminated", "broken.mdl");

            Assert.Empty(models);
        }
    }

    /// <summary>
    /// One VMT the engine loads but the strict parser does not (a missing final brace) used to throw
    /// out of PACK and cancel every step after it.
    /// </summary>
    public class MalformedVmtTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "cp-vmt-" + Guid.NewGuid().ToString("N"));

        public MalformedVmtTests() => Directory.CreateDirectory(root);

        public void Dispose()
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }

        [Fact]
        public void AVmtMissingItsLastBraceDoesNotThrow()
        {
            string vmt = Path.Combine(root, "broken.vmt");
            File.WriteAllText(vmt, "\"LightmappedGeneric\"\n{\n\"$basetexture\" \"brick/brickwall001a\"\n\"$bumpmap\" \"brick/brickwall001a_normal\"\n");

            var materials = AssetUtils.FindVmtMaterials(vmt);

            Assert.Empty(materials);
        }
    }
}
