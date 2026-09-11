using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CompilePalX;
using CompilePalX.Compilers;
using CompilePalX.Configuration;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// Which parameters a step offers, now that the compiler is asked rather than the JSON believed.
    ///
    /// The rule under test: a compiler that lists its options is the authority on them. Anything it
    /// lists is offered whatever the game lists in parameters.json say; anything it does not list is
    /// withheld, unless the file says the omission is known. A compiler that lists nothing - the stock
    /// tools - leaves the file as the only description, with the tools++-only entries hidden.
    ///
    /// The binary is stood in for by <see cref="ToolHelpProbe.Override"/>, keyed by the path the game
    /// configuration names, so nothing here runs a process.
    /// </summary>
    [Collection("Settings")]
    public class ToolArgumentAvailabilityTests : IDisposable
    {
        private const string VradPath = @"C:\not-real\vrad.exe";

        private readonly GameConfiguration? originalGame;
        private readonly ToolsPlusPlusMode originalMode;
        private readonly bool originalPrefer;
        private readonly string? originalFolder;

        private static readonly ToolHelp VradPlusPlus =
            ToolHelpParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "vrad++-help.txt")))!;

        public ToolArgumentAvailabilityTests()
        {
            originalGame = GameConfigurationManager.GameConfiguration;

            // Only these three fields are touched, on the shared Settings object, and put back after.
            // Replacing the object would pull it out from under the telemetry tests, which run in
            // parallel and set their own field on the same instance.
            var settings = ConfigurationManager.Settings;
            originalMode = settings.ToolsPlusPlusMode;
            originalPrefer = settings.PreferToolsPlusPlusBinaries;
            originalFolder = settings.ToolsPlusPlusFolder;

            // No standalone folder and no preference for one, so the configured path is the path
            // resolved - otherwise a real tools++ install on the machine running the tests would be
            // found under Documents and answer instead of the stub.
            settings.ToolsPlusPlusMode = ToolsPlusPlusMode.Auto;
            settings.PreferToolsPlusPlusBinaries = false;
            settings.ToolsPlusPlusFolder = null;

            GameConfigurationManager.GameConfiguration = new GameConfiguration
            {
                Name = "Counter-Strike: Global Offensive",
                SteamAppID = 730,
                VRAD = VradPath,
            };

            UseCompiler(VradPlusPlus);
        }

        public void Dispose()
        {
            ToolHelpProbe.Override = null;
            ToolsPlusPlusDetector.Invalidate();
            ConfigurationManager.Settings.ToolsPlusPlusMode = originalMode;
            ConfigurationManager.Settings.PreferToolsPlusPlusBinaries = originalPrefer;
            ConfigurationManager.Settings.ToolsPlusPlusFolder = originalFolder;
            GameConfigurationManager.GameConfiguration = originalGame;
        }

        /// <summary>Makes the configured VRAD answer -help with <paramref name="help"/>, or with nothing.</summary>
        private static void UseCompiler(ToolHelp? help)
        {
            ToolHelpProbe.Override = path =>
                string.Equals(path, VradPath, StringComparison.OrdinalIgnoreCase) ? help : null;
            ToolsPlusPlusDetector.Invalidate();
        }

        private static ConfigItem Vrad(string parameter, Action<ConfigItem>? configure = null)
        {
            var item = new ConfigItem { Name = parameter.Trim(), Parameter = parameter, OwningProcess = "VRAD" };
            configure?.Invoke(item);
            return item;
        }

        [Fact]
        public void AListedFlagIsOfferedWhateverTheGameListSays()
        {
            // -ldr is marked incompatible with CS:GO for the stock tools; vrad++ lists it and accepts it.
            var ldr = Vrad(" -ldr", i => i.IncompatibleGames = [730]);

            Assert.True(ldr.IsCompatible);
            Assert.Null(ldr.IncompatibilityReason);
        }

        [Fact]
        public void AnUnlistedFlagIsWithheldAndSaysWhy()
        {
            var final = Vrad(" -StaticPropLightingFinal");

            Assert.False(final.IsCompatible);
            Assert.Equal("not accepted by vrad++ (Sep 10 2026)", final.IncompatibilityReason);
        }

        [Fact]
        public void AKnownOmissionFallsBackToTheGameRules()
        {
            // vrad++ accepts -StaticPropBounce without listing it, and -normal_priority never reaches
            // the compiler at all. Both are declared as such and judged on the game lists only.
            var bounce = Vrad(" -StaticPropBounce", i => i.NotInToolHelp = true);
            var priority = Vrad(" -normal_priority", i => i.NotInToolHelp = true);
            var blocked = Vrad(" -normal_priority", i => { i.NotInToolHelp = true; i.IncompatibleGames = [730]; });

            Assert.True(bounce.IsCompatible);
            Assert.True(priority.IsCompatible);
            Assert.False(blocked.IsCompatible);
            Assert.Equal("not supported by Counter-Strike: Global Offensive", blocked.IncompatibilityReason);
        }

        [Fact]
        public void AParameterWithNoFlagIsNotLookedUp()
        {
            var custom = Vrad("", i => { i.Name = "Command Line Argument"; i.CanHaveValue = true; });

            Assert.True(custom.IsCompatible);
        }

        [Fact]
        public void AStockCompilerLeavesTheFileInCharge()
        {
            UseCompiler(null);

            var ao = Vrad(" -aoradius", i => i.RequiresToolsPlusPlus = true);
            var bounce = Vrad(" -bounce");
            var ldr = Vrad(" -ldr", i => i.IncompatibleGames = [730]);

            Assert.False(ao.IsCompatible);
            Assert.Equal("requires the Hammer++ compile tools", ao.IncompatibilityReason);
            Assert.True(bounce.IsCompatible);
            Assert.False(ldr.IsCompatible);
        }

        [Fact]
        public void ForceOnOffersToolsPlusPlusEntriesWithoutAnAnswer()
        {
            UseCompiler(null);
            ConfigurationManager.Settings.ToolsPlusPlusMode = ToolsPlusPlusMode.ForceOn;

            Assert.True(Vrad(" -aoradius", i => i.RequiresToolsPlusPlus = true).IsCompatible);
        }

        [Fact]
        public void ForceOffNeverAsksTheCompiler()
        {
            ConfigurationManager.Settings.ToolsPlusPlusMode = ToolsPlusPlusMode.ForceOff;

            // "never asks" is literal: not one call reaches the binary, through any path
            int asked = 0;
            ToolHelpProbe.Override = _ => { asked++; return VradPlusPlus; };
            ToolsPlusPlusDetector.Invalidate();

            // the stub would list it; the setting says not to look
            Assert.False(Vrad(" -aoradius", i => i.RequiresToolsPlusPlus = true).IsCompatible);
            // and an entry the compiler would have rejected is back to the game rules
            Assert.True(Vrad(" -StaticPropLightingFinal").IsCompatible);

            Assert.Null(ToolsPlusPlusDetector.HelpFor("VRAD"));
            Assert.Null(ToolHelpProbe.Probe(VradPath));
            Assert.True(ToolHelpProbe.TryPeek(VradPath, out var peeked));
            Assert.Null(peeked);
            ToolHelpProbe.EnsureProbed(VradPath);
            ToolsPlusPlusDetector.LogDetectionResults();

            Assert.Equal(0, asked);
        }

        [Fact]
        public void ADiscoveredOptionIsNeverHandedToAStockCompiler()
        {
            // Discovered under vrad++, then the configuration moves to a stock vrad. The preset still
            // carries the flag; the stock tool would reject it, so it must be withheld - both for one
            // discovered this session and for one read back out of a preset.
            var help = ToolHelpParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "vrad++-help.txt")))!;
            help.TryGet("-gpusubmit", out var option);

            var discovered = ConfigItem.FromToolOption("VRAD", option);
            var fromPreset = ConfigItem.FromPresetFlag("VRAD", "-gpusubmit", "0.25")!;

            Assert.True(discovered.IsCompatible);
            Assert.True(fromPreset.IsCompatible);

            UseCompiler(null);

            Assert.False(discovered.IsCompatible);
            Assert.False(fromPreset.IsCompatible);
            Assert.Equal("requires the Hammer++ compile tools", discovered.IncompatibilityReason);

            // and under ForceOn, with no listing to check against, a tools++ option is offered
            ConfigurationManager.Settings.ToolsPlusPlusMode = ToolsPlusPlusMode.ForceOn;
            Assert.True(discovered.IsCompatible);
        }

        [Fact]
        public void AnUnaskedCompilerIsAskedInTheBackgroundNotOnTheCaller()
        {
            // No override: a real path that is not cached must come back "not yet" without running
            // anything on this thread. The file does not exist, which settles it as "no help".
            ToolHelpProbe.Override = null;
            ToolsPlusPlusDetector.Invalidate();

            Assert.True(ToolHelpProbe.TryPeek(@"C:\not-real\missing-vrad.exe", out var help));
            Assert.Null(help);
        }

        [Fact]
        public void TheCompilersOwnOptionsAreAddedToTheStep()
        {
            using var step = new TemporaryStep("VRAD", """
                [
                  { "Name": "Bounces", "Parameter": " -bounce", "CanHaveValue": true },
                  { "Name": "Static Prop Lighting Final", "Parameter": " -StaticPropLightingFinal" }
                ]
                """);

            step.Process.RefreshDiscoveredParameters();

            var names = step.Process.ParameterList.Select(p => p.Name).ToList();

            // discovered under its flag, described in the compiler's words, with the default noted
            var submit = Assert.Single(step.Process.ParameterList, p => p.Name == "-gpusubmit");
            Assert.True(submit.FromToolHelp);
            Assert.True(submit.CanHaveValue);
            Assert.Equal("0.5", submit.ToolDefault);
            Assert.Contains("Default: 0.5.", submit.Description);
            Assert.Equal("VRAD", submit.OwningProcess);

            var cpu = Assert.Single(step.Process.ParameterList, p => p.Name == "-cpu");
            Assert.False(cpu.CanHaveValue);

            // the curated entry keeps its name and picks up the compiler's default
            var bounces = Assert.Single(step.Process.ParameterList, p => p.Name == "Bounces");
            Assert.False(bounces.FromToolHelp);
            Assert.Equal("100", bounces.ToolDefault);
            Assert.DoesNotContain("-bounce", names);

            // not everything the compiler lists is a preset parameter
            Assert.DoesNotContain("-game", names);
            Assert.DoesNotContain("-v", names);
            Assert.DoesNotContain("-StopOnExit", names);

            // the curated entry the compiler does not accept is still in the list, just not offered
            Assert.False(Assert.Single(step.Process.ParameterList, p => p.Name == "Static Prop Lighting Final").IsCompatible);
        }

        [Fact]
        public void BspzipsCommandsAreNotOfferedAsOptions()
        {
            var bspzip = ToolHelpParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "bspzip++-help.txt")))!;
            const string bspzipPath = @"C:\not-real\bspzip.exe";

            GameConfigurationManager.GameConfiguration!.BSPZip = bspzipPath;
            ToolHelpProbe.Override = path => string.Equals(path, bspzipPath, StringComparison.OrdinalIgnoreCase) ? bspzip : null;
            ToolsPlusPlusDetector.Invalidate();

            using var step = new TemporaryStep("BSPZIP", """[{ "Name": "Compress", "Parameter": " -compress" }]""");
            step.Process.RefreshDiscoveredParameters();

            var names = step.Process.ParameterList.Select(p => p.Name).ToList();

            // -extract <bspfile> <blah.zip> is a command, not a switch to add to a repack
            Assert.DoesNotContain("-extract", names);
            Assert.DoesNotContain("-dir", names);
            Assert.DoesNotContain("-addfile", names);

            // whereas these really are options
            Assert.Contains("-threads", names);
            Assert.Contains("-mountcfg", names);
        }

        [Fact]
        public void SwitchingToAStockCompilerTakesTheDiscoveredOnesAwayAgain()
        {
            using var step = new TemporaryStep("VRAD", """[{ "Name": "Bounces", "Parameter": " -bounce", "CanHaveValue": true }]""");

            step.Process.RefreshDiscoveredParameters();
            Assert.Contains(step.Process.ParameterList, p => p.FromToolHelp);

            UseCompiler(null);
            step.Process.RefreshDiscoveredParameters();

            Assert.DoesNotContain(step.Process.ParameterList, p => p.FromToolHelp);
            Assert.Null(Assert.Single(step.Process.ParameterList, p => p.Name == "Bounces").ToolDefault);
        }

        [Fact]
        public void APresetCanNameAFlagNoCurrentListCarries()
        {
            var kept = ConfigItem.FromPresetFlag("VRAD", "-softsun", "5");

            Assert.NotNull(kept);
            Assert.Equal("-softsun", kept!.Name);
            Assert.Equal(" -softsun", kept.Parameter);
            Assert.True(kept.CanHaveValue);
            Assert.True(kept.FromToolHelp);

            // only flags: a curated name that has gone missing is still an error
            Assert.Null(ConfigItem.FromPresetFlag("VRAD", "Bounces", "2"));
            Assert.Null(ConfigItem.FromPresetFlag("VRAD", "-two words", null));
        }

        [Fact]
        public void CloningKeepsWhatTheCompilerSaid()
        {
            var item = Vrad(" -bounce", i => { i.NotInToolHelp = true; i.FromToolHelp = true; i.ToolDefault = "100"; });

            var clone = (ConfigItem)item.Clone();

            Assert.True(clone.NotInToolHelp);
            Assert.True(clone.FromToolHelp);
            Assert.Equal("100", clone.ToolDefault);
        }

        /// <summary>A compile step built from files in a temporary folder, so no shipped file is read.</summary>
        private sealed class TemporaryStep : IDisposable
        {
            private readonly string root;
            public CompileProcess Process { get; }

            public TemporaryStep(string name, string parametersJson)
            {
                root = Path.Combine(Path.GetTempPath(), "CompilePalStep_" + Guid.NewGuid().ToString("N"));
                string folder = Path.Combine(root, name);
                Directory.CreateDirectory(folder);

                File.WriteAllText(Path.Combine(folder, "meta.json"), $$"""
                    { "Name": "{{name}}", "Path": "$vrad$", "Order": 3.0, "DoRun": true, "ReadOutput": true,
                      "Description": "", "Warning": "", "BasisString": " -game $game$ $vmfFile$" }
                    """);
                File.WriteAllText(Path.Combine(folder, "parameters.json"), parametersJson);

                Process = new CompileExecutable(name, root);
            }

            public void Dispose()
            {
                try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            }
        }
    }
}
