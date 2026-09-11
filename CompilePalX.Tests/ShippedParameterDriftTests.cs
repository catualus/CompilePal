using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// The shipped parameter lists and presets, checked against what the compilers actually accept.
    ///
    /// Discovery covers options the files do not mention. It cannot cover the other direction: an
    /// entry in parameters.json that claims to be a tools++ option and is not, or a preset that hands
    /// a tools++ build a flag it will reject. Those were found by hand before and are found here now,
    /// against the same captured help the parser tests use.
    /// </summary>
    public class ShippedParameterDriftTests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "CompilePalX.sln")))
                    return dir.FullName;
                dir = dir.Parent!;
            }

            throw new InvalidOperationException($"Could not find the repository above {AppContext.BaseDirectory}");
        }

        private static ToolHelp Help(string fixture) =>
            ToolHelpParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)))!;

        private static ConfigItem[] Parameters(string step)
        {
            string text = File.ReadAllText(Path.Combine(Root(), "CompilePalX", "Parameters", step, "parameters.json"));
            // the shipped files carry // comments naming the games in the app id lists
            text = Regex.Replace(text, @"//[^\r\n]*", "");
            return JsonConvert.DeserializeObject<ConfigItem[]>(text)!;
        }

        private static readonly (string Step, string Fixture)[] Compilers =
        [
            ("VBSP", "vbsp++-help.txt"),
            ("VVIS", "vvis++-help.txt"),
            ("VRAD", "vrad++-help.txt"),
        ];

        [Theory]
        [InlineData("VBSP", "vbsp++-help.txt")]
        [InlineData("VVIS", "vvis++-help.txt")]
        [InlineData("VRAD", "vrad++-help.txt")]
        public void EveryEntryClaimedForToolsPlusPlusExists(string step, string fixture)
        {
            var help = Help(fixture);

            var wrong = Parameters(step)
                .Where(p => p.RequiresToolsPlusPlus && !p.NotInToolHelp && !help.Has(p.Flag))
                .Select(p => $"{p.Name} ({p.Flag})")
                .ToList();

            Assert.True(wrong.Count == 0,
                $"{step}: marked RequiresToolsPlusPlus but not listed by {help.Label}: {string.Join(", ", wrong)}");
        }

        [Theory]
        [InlineData("VBSP", "vbsp++-help.txt")]
        [InlineData("VVIS", "vvis++-help.txt")]
        [InlineData("VRAD", "vrad++-help.txt")]
        public void AKnownOmissionIsReallyOmitted(string step, string fixture)
        {
            // NotInToolHelp exists for flags the help does not list. One it does list is a stale
            // marker that would let a later removal of the flag go unnoticed.
            var help = Help(fixture);

            var stale = Parameters(step)
                .Where(p => p.NotInToolHelp && help.Has(p.Flag))
                .Select(p => p.Flag)
                .ToList();

            Assert.True(stale.Count == 0, $"{step}: NotInToolHelp but {help.Label} lists it: {string.Join(", ", stale)}");
        }

        [Theory]
        [InlineData("Best (tools++)")]
        [InlineData("Publish - Best (tools++)")]
        public void AToolsPlusPlusPresetOnlyUsesFlagsTheToolsAccept(string presetName)
        {
            string metaPath = Path.Combine(Root(), "CompilePalX", "Presets", presetName, "meta.json");
            var preset = JsonConvert.DeserializeObject<Preset>(File.ReadAllText(metaPath))!;

            var rejected = new List<string>();

            foreach (var (step, fixture) in Compilers)
            {
                if (!preset.Processes.TryGetValue(step, out var chosen))
                    continue;

                var help = Help(fixture);
                var byName = Parameters(step).ToDictionary(p => p.Name, p => p);

                foreach (var parameter in chosen)
                {
                    Assert.True(byName.ContainsKey(parameter.Name), $"{presetName}/{step}: no parameter named \"{parameter.Name}\"");

                    var item = byName[parameter.Name];
                    if (!item.NotInToolHelp && item.Flag.StartsWith('-') && !help.Has(item.Flag))
                        rejected.Add($"{step} {item.Flag}");
                }
            }

            Assert.True(rejected.Count == 0, $"{presetName} uses flags tools++ does not accept: {string.Join(", ", rejected)}");
        }

        /// <summary>
        /// The help table has no column saying whether an option takes a value; discovery infers it
        /// from the Default column, and an option with a value but no default is read as a switch.
        /// Those have to be described by hand or they are offered as flags that emit nothing after
        /// them. This is the list of the ones the tools print that way.
        /// </summary>
        [Theory]
        [InlineData("VBSP", "vbsp++-help.txt", "-insert_search_path", "-append_search_path", "-entfirst", "-forcematerial", "-missingmaterial")]
        [InlineData("VVIS", "vvis++-help.txt", "-insert_search_path", "-append_search_path")]
        [InlineData("VRAD", "vrad++-help.txt", "-insert_search_path", "-append_search_path")]
        [InlineData("BSPZIP", "bspzip++-help.txt", "-insert_search_path", "-append_search_path")]
        public void AnOptionThatTakesAValueButPrintsNoDefaultIsCurated(string step, string fixture, params string[] flags)
        {
            var help = Help(fixture);
            var curated = Parameters(step).ToDictionary(p => p.Flag, p => p, StringComparer.OrdinalIgnoreCase);

            foreach (var flag in flags)
            {
                Assert.True(help.TryGet(flag, out var option), $"{step}: {flag} is no longer listed; drop it from this test");
                Assert.False(option.TakesValue, $"{step}: {flag} now prints a default; discovery handles it and the curated entry is optional");

                Assert.True(curated.TryGetValue(flag, out var item), $"{step}: {flag} takes a value but prints no default, so it needs a parameters.json entry");
                Assert.True(item!.CanHaveValue, $"{step}: the entry for {flag} must allow a value");
            }
        }

        [Fact]
        public void TheGpuOptionsAreDescribed()
        {
            // The one change in the tools that prompted all of this: lighting moved to the GPU, with
            // -cpu to opt out and -gpusubmit to tune it. Both deserve a name rather than a bare flag.
            var vrad = Parameters("VRAD");

            var cpu = Assert.Single(vrad, p => p.Flag == "-cpu");
            var submit = Assert.Single(vrad, p => p.Flag == "-gpusubmit");

            Assert.True(cpu.RequiresToolsPlusPlus);
            Assert.False(cpu.CanHaveValue);
            Assert.True(submit.RequiresToolsPlusPlus);
            Assert.True(submit.CanHaveValue);
        }
    }
}
