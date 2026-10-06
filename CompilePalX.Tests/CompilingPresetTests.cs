using System;
using System.IO;
using System.Linq;
using CompilePalX;
using CompilePalX.Compilers;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// That a compile reads its arguments from the map being compiled, not from whatever the window is
    /// showing - and that the window's preset is left alone by a compile.
    ///
    /// The compile loop used to write each map's preset into CurrentPreset, and every step read its
    /// arguments back out of it when it started. CurrentPreset also belongs to the window, and the map
    /// queue stays clickable during a run, so clicking another queued map while VBSP ran handed that
    /// map's arguments to VVIS and VRAD.
    /// </summary>
    [Collection("Settings")]
    public class CompilingPresetTests : IDisposable
    {
        private readonly Preset? originalCurrent;
        private readonly string root;
        private readonly CompileProcess step;

        private readonly Preset onScreen = new() { Name = "On screen" };
        private readonly Preset compiling = new() { Name = "Compiling" };

        public CompilingPresetTests()
        {
            originalCurrent = ConfigurationManager.CurrentPreset;

            root = Path.Combine(Path.GetTempPath(), "CompilePalCompilingPreset_" + Guid.NewGuid().ToString("N"));
            string folder = Path.Combine(root, "STEP");
            Directory.CreateDirectory(folder);

            File.WriteAllText(Path.Combine(folder, "meta.json"), """
                { "Name": "STEP", "Path": "plugin.exe", "Order": 1.0, "DoRun": true, "ReadOutput": true,
                  "Description": "", "Warning": "", "BasisString": "" }
                """);
            File.WriteAllText(Path.Combine(folder, "parameters.json"), """
                [
                  { "Name": "Fast", "Parameter": " -fast" },
                  { "Name": "Final", "Parameter": " -final" },
                  { "Name": "Content", "Parameter": " -content", "CanHaveValue": true, "ValueIsFolder": true }
                ]
                """);

            step = new CompileExecutable("STEP", root);

            step.PresetDictionary[onScreen] = [Clone("Fast")];
            step.PresetDictionary[compiling] = [Clone("Final")];
        }

        public void Dispose()
        {
            ConfigurationManager.CurrentPreset = originalCurrent;
            ConfigurationManager.CompilingPreset = null;

            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }

        private ConfigItem Clone(string name) => (ConfigItem)step.ParameterList.First(p => p.Name == name).Clone();

        [Fact]
        public void AStepRunsWithTheCompilingMapsPreset()
        {
            ConfigurationManager.CurrentPreset = onScreen;
            ConfigurationManager.CompilingPreset = compiling;

            Assert.Contains("-final", step.GetParameterString());
            Assert.DoesNotContain("-fast", step.GetParameterString());
        }

        /// <summary>The row on screen describes the preset on screen, compiling or not.</summary>
        [Fact]
        public void TheRowStillDescribesThePresetOnScreen()
        {
            ConfigurationManager.CurrentPreset = onScreen;
            ConfigurationManager.CompilingPreset = compiling;

            Assert.Equal("-fast", step.ArgumentSummary);
        }

        [Fact]
        public void WithNoCompileRunningTheEditedPresetApplies()
        {
            ConfigurationManager.CurrentPreset = onScreen;
            ConfigurationManager.CompilingPreset = null;

            Assert.Contains("-fast", step.GetParameterString());
        }

        /// <summary>
        /// A folder picked from the picker is quoted like a file. Passed bare, "D:\Steam Library"
        /// reached the step as two arguments.
        /// </summary>
        [Fact]
        public void AFolderValueIsQuoted()
        {
            var content = Clone("Content");
            content.Value = @"D:\Steam Library\addons";
            step.PresetDictionary[onScreen].Add(content);

            Assert.Contains("-content \"D:\\Steam Library\\addons\"", step.GetParameterString(onScreen));
        }
    }

    /// <summary>
    /// That a preset's name is checked before it becomes a folder.
    ///
    /// The folder is built from the name, and the name was never checked: an empty one resolved to
    /// Presets itself and the preset silently failed to save, a rename onto an existing name deleted
    /// the old preset and then found the new folder already there, and ".." - possible in any shared
    /// preset's meta.json - made Remove Preset delete the Compile Pal folder recursively.
    /// </summary>
    [Collection("Settings")]
    public class PresetNameTests : IDisposable
    {
        private readonly string existing = "Existing_" + Guid.NewGuid().ToString("N");

        public PresetNameTests() => Directory.CreateDirectory(Path.Combine("./Presets", existing));

        public void Dispose()
        {
            try { Directory.Delete(Path.Combine("./Presets", existing), recursive: true); } catch (IOException) { }
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(".")]
        [InlineData("..")]
        [InlineData("a/b")]
        [InlineData("a\\b")]
        [InlineData(" padded ")]
        public void ANameThatIsNotAFolderNameIsRefused(string name)
        {
            Assert.NotNull(ConfigurationManager.PresetProblem(new Preset { Name = name }));
        }

        [Fact]
        public void AMapFilterThatEscapesPresetsIsRefused()
        {
            Assert.NotNull(ConfigurationManager.PresetProblem(new Preset { Name = "Fine", Map = "../../x" }));
        }

        [Fact]
        public void AnInvalidMatchPatternIsRefused()
        {
            Assert.NotNull(ConfigurationManager.PresetProblem(
                new Preset { Name = "Fine_" + Guid.NewGuid().ToString("N"), Map = "map", MapRegex = "(" }));
        }

        [Fact]
        public void ANameAnotherPresetHasIsRefused()
        {
            Assert.Contains("already exists", ConfigurationManager.PresetProblem(new Preset { Name = existing }));
        }

        /// <summary>Editing a preset without renaming it is not a clash with itself.</summary>
        [Fact]
        public void KeepingYourOwnNameIsFine()
        {
            var original = new Preset { Name = existing };

            Assert.Null(ConfigurationManager.PresetProblem(new Preset { Name = existing }, replacing: original));
        }

        [Fact]
        public void AnOrdinaryNewNameIsFine()
        {
            Assert.Null(ConfigurationManager.PresetProblem(new Preset { Name = "Brand new " + Guid.NewGuid().ToString("N") }));
        }
    }
}
