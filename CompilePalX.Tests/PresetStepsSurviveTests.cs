using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// New, Clone and Edit Preset wrote the preset through SavePreset, which rebuilds the step list
    /// from the steps' preset dictionaries. A preset that has not been loaded is in none of them, so
    /// all three wrote a preset with no steps, and Edit wiped one that had them.
    ///
    /// Runs against the Presets folder beside the test binaries, as the application does, under a
    /// name no shipped preset uses, and removes what it made.
    /// </summary>
    [Collection("Settings")]
    public class PresetStepsSurviveTests : IDisposable
    {
        private readonly string name = "zz-review-test-" + Guid.NewGuid().ToString("N")[..8];
        private readonly Preset? current = ConfigurationManager.CurrentPreset;

        public void Dispose()
        {
            foreach (var preset in ConfigurationManager.KnownPresets.Where(p => p.Name.StartsWith(name)).ToList())
                ConfigurationManager.RemovePreset(preset);
            ConfigurationManager.CurrentPreset = current;
        }

        private static string[] StepsOnDisk(string presetName)
        {
            var meta = JObject.Parse(File.ReadAllText(Path.Combine("Presets", presetName, "meta.json")));
            return ((JObject)meta["Processes"]!).Properties().Select(p => p.Name).OrderBy(n => n).ToArray();
        }

        [Fact]
        public void ANewPresetKeepsItsDefaultSteps()
        {
            ConfigurationManager.NewPreset(new Preset { Name = name });

            Assert.Equal(["COPY", "GAME", "VBSP", "VRAD", "VVIS"], StepsOnDisk(name));
        }

        [Fact]
        public void CloneAndEditKeepTheSteps()
        {
            var source = ConfigurationManager.NewPreset(new Preset { Name = name })!;
            source = ConfigurationManager.KnownPresets.First(p => p.Equals(source));

            ConfigurationManager.ClonePreset(source, new Preset { Name = name + "-clone" });
            Assert.Equal(StepsOnDisk(name), StepsOnDisk(name + "-clone"));

            ConfigurationManager.EditPreset(source, new Preset { Name = name + "-renamed" });
            Assert.Equal(["COPY", "GAME", "VBSP", "VRAD", "VVIS"], StepsOnDisk(name + "-renamed"));
        }
    }
}
