using System;
using System.Globalization;
using System.IO;
using System.Linq;
using CompilePalX;
using CompilePalX.Compilers;
using CompilePalX.Configuration;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// What a step's row says about the compiler it runs, and the command it would run.
    ///
    /// Both used to be answerable only from the debug log. The badge names the binary and, for VRAD,
    /// whether it will light on the GPU; the expanded row shows the command for the selected map with
    /// every placeholder filled in rather than "-game $game$ $vmfFile$".
    /// </summary>
    [Collection("Settings")]
    public class StepRowPresentationTests : IDisposable
    {
        private const string VradPath = @"C:\not-real\vrad++.exe";
        private const string GameFolder = @"C:\not real\garrysmod";

        private readonly GameConfiguration? originalGame;
        private readonly Preset? originalPreset;
        private readonly string? originalPreviewMap;
        private readonly ToolsPlusPlusMode originalMode;
        private readonly bool originalPrefer;
        private readonly string? originalFolder;

        private static readonly ToolHelp VradPlusPlus =
            ToolHelpParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "vrad++-help.txt")))!;

        private readonly string root;
        private readonly CompileProcess vrad;
        private readonly Preset preset = new() { Name = "Test" };

        public StepRowPresentationTests()
        {
            originalGame = GameConfigurationManager.GameConfiguration;
            originalPreset = ConfigurationManager.CurrentPreset;
            originalPreviewMap = ConfigurationManager.PreviewMap;

            var settings = ConfigurationManager.Settings;
            originalMode = settings.ToolsPlusPlusMode;
            originalPrefer = settings.PreferToolsPlusPlusBinaries;
            originalFolder = settings.ToolsPlusPlusFolder;
            settings.ToolsPlusPlusMode = ToolsPlusPlusMode.Auto;
            settings.PreferToolsPlusPlusBinaries = false;
            settings.ToolsPlusPlusFolder = null;

            GameConfigurationManager.GameConfiguration = new GameConfiguration
            {
                Name = "Garry's Mod",
                SteamAppID = 4000,
                GameFolder = GameFolder,
                MapFolder = Path.Combine(GameFolder, "maps"),
                BinFolder = @"C:\not real\bin",
                VRAD = VradPath,
            };

            ToolHelpProbe.Override = path => string.Equals(path, VradPath, StringComparison.OrdinalIgnoreCase) ? VradPlusPlus : null;
            ToolsPlusPlusDetector.Invalidate();

            root = Path.Combine(Path.GetTempPath(), "CompilePalStepRow_" + Guid.NewGuid().ToString("N"));
            string folder = Path.Combine(root, "VRAD");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "meta.json"), """
                { "Name": "VRAD", "Path": "$vrad$", "Order": 3.0, "DoRun": true, "ReadOutput": true,
                  "Description": "", "Warning": "", "BasisString": " -game $game$ $vmfFile$" }
                """);
            File.WriteAllText(Path.Combine(folder, "parameters.json"), """
                [
                  { "Name": "Final", "Parameter": " -final" },
                  { "Name": "CPU Only", "Parameter": " -cpu", "RequiresToolsPlusPlus": true },
                  { "Name": "Bounces", "Parameter": " -bounce", "CanHaveValue": true }
                ]
                """);

            vrad = new CompileExecutable("VRAD", root);
            vrad.PresetDictionary[preset] = [];
            ConfigurationManager.CurrentPreset = preset;
        }

        public void Dispose()
        {
            ToolHelpProbe.Override = null;
            ToolsPlusPlusDetector.Invalidate();
            ConfigurationManager.Settings.ToolsPlusPlusMode = originalMode;
            ConfigurationManager.Settings.PreferToolsPlusPlusBinaries = originalPrefer;
            ConfigurationManager.Settings.ToolsPlusPlusFolder = originalFolder;
            ConfigurationManager.CurrentPreset = originalPreset;
            ConfigurationManager.PreviewMap = originalPreviewMap;
            GameConfigurationManager.GameConfiguration = originalGame;

            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }

        private void Add(string name)
        {
            var item = vrad.ParameterList.First(p => p.Name == name);
            vrad.PresetDictionary[preset].Add((ConfigItem)item.Clone());
        }

        [Fact]
        public void TheBadgeNamesTheBuildAndSaysGpuByDefault()
        {
            Assert.True(vrad.HasCompilerBadge);
            Assert.Equal("vrad++ (Sep 10 2026) · GPU", vrad.CompilerBadge);
            Assert.Contains(VradPath, vrad.CompilerBadgeDetail);
            Assert.Contains("99 options", vrad.CompilerBadgeDetail);
        }

        [Fact]
        public void AddingCpuOnlyFlipsTheBadge()
        {
            Add("CPU Only");

            Assert.Equal("vrad++ (Sep 10 2026) · CPU", vrad.CompilerBadge);
        }

        [Fact]
        public void AStockCompilerIsNamedAsSuch()
        {
            ToolHelpProbe.Override = _ => null;
            ToolsPlusPlusDetector.Invalidate();

            // no help, no tools++ name, no banner in a file that does not exist: stock
            GameConfigurationManager.GameConfiguration!.VRAD = @"C:\not-real\vrad.exe";

            Assert.Equal("stock vrad.exe", vrad.CompilerBadge);
            Assert.Contains("does not list its options", vrad.CompilerBadgeDetail);
        }

        [Fact]
        public void NothingConfiguredSaysSo()
        {
            GameConfigurationManager.GameConfiguration!.VRAD = "";

            Assert.Equal("not configured", vrad.CompilerBadge);
        }

        [Fact]
        public void AStepIsJudgedByTheCompilerItRunsNotItsOwnName()
        {
            // REPACK runs bspzip. Its tools++-only parameters were never offered, because the lookup
            // went by the step name and nothing is configured under "REPACK".
            const string bspzipPath = @"C:\not-real\bspzip++.exe";
            var bspzip = ToolHelpParser.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "bspzip++-help.txt")))!;
            GameConfigurationManager.GameConfiguration!.BSPZip = bspzipPath;
            ToolHelpProbe.Override = path => string.Equals(path, bspzipPath, StringComparison.OrdinalIgnoreCase) ? bspzip : null;
            ToolsPlusPlusDetector.Invalidate();

            string folder = Path.Combine(root, "REPACK");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "meta.json"), """
                { "Name": "REPACK", "Path": "$bspZip$", "Order": 12.0, "DoRun": true, "ReadOutput": true,
                  "Description": "", "Warning": "", "BasisString": " $mapCopyLocation$" }
                """);
            File.WriteAllText(Path.Combine(folder, "parameters.json"), """
                [{ "Name": "Threads", "Parameter": " -threads", "CanHaveValue": true, "RequiresToolsPlusPlus": true }]
                """);

            var repack = new CompileExecutable("REPACK", root);

            Assert.Equal("BSPZIP", repack.CompilerName);
            Assert.Equal("bspzip++ (Sep 10 2026)", repack.CompilerBadge);

            var threads = repack.ParameterList.First(p => p.Name == "Threads");
            Assert.Equal("BSPZIP", threads.OwningProcess);
            Assert.True(threads.IsCompatible);
        }

        [Fact]
        public void StepsThatRunNoCompilerHaveNoBadge()
        {
            string folder = Path.Combine(root, "COPY");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "meta.json"), """
                { "Name": "COPY", "Path": "xcopy", "Order": 4.0, "DoRun": true, "ReadOutput": false,
                  "Description": "", "Warning": "", "BasisString": " /y /f $bsp$ $mapFolder$" }
                """);
            File.WriteAllText(Path.Combine(folder, "parameters.json"), "[]");

            var copy = new CompileExecutable("COPY", root);

            Assert.False(copy.HasCompilerBadge);
            Assert.Null(copy.CompilerBadge);
        }

        [Fact]
        public void ThePreviewFillsInTheSelectedMap()
        {
            Add("Final");
            Add("Bounces");
            vrad.PresetDictionary[preset][1].Value = "2";
            ConfigurationManager.PreviewMap = @"C:\maps\rp_test.vmf";

            string preview = vrad.CommandPreview;

            Assert.StartsWith($"\"{VradPath}\" ", preview);
            Assert.Contains("-final -bounce 2", preview);
            Assert.Contains($"-game \"{GameFolder}\"", preview);
            Assert.EndsWith("\"C:\\maps\\rp_test.vmf\"", preview);
            Assert.DoesNotContain("$", preview);
        }

        [Fact]
        public void WithNoMapSelectedThePreviewIsTheTemplate()
        {
            Add("Final");
            ConfigurationManager.PreviewMap = null;

            Assert.Equal("-final -game $game$ $vmfFile$", vrad.CommandPreview);
        }

        [Fact]
        public void ThePickerFoldsDiscoveredOptionsUnderTheirOwnHeading()
        {
            var group = new IsCompatiblePropertyGroup();
            var culture = CultureInfo.InvariantCulture;

            var curated = new ConfigItem { Name = "Final", Parameter = " -final", OwningProcess = "VRAD" };
            var discovered = ConfigItem.FromToolOption("VRAD", new ToolOption("-softsun", "0", "Treat the sun as an area light"));
            var rejected = new ConfigItem { Name = "Old", Parameter = " -StaticPropLightingOld", OwningProcess = "VRAD" };

            Assert.Equal(IsCompatiblePropertyGroup.Compatible, group.GroupNameFromItem(curated, 0, culture));
            Assert.Equal(IsCompatiblePropertyGroup.Discovered, group.GroupNameFromItem(discovered, 0, culture));
            Assert.Equal(IsCompatiblePropertyGroup.Incompatible, group.GroupNameFromItem(rejected, 0, culture));
        }
    }
}
