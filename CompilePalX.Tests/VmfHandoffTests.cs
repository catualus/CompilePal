using System;
using System.IO;
using CompilePalX;
using CompilePalX.Compiling;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// A step ahead of VBSP handing the compile a different VMF.
    ///
    /// <c>COMPILE_PAL_SET file</c> was documented to do this, but it only set a field that the
    /// map's context had already been built from, so it reached nothing until the next map. The
    /// live context now moves, and what VBSP writes beside the copy is brought back beside the
    /// queued map so Hammer and the preview find it where they always did.
    /// </summary>
    [Collection("Settings")]
    public class VmfHandoffTests : IDisposable
    {
        private readonly GameConfiguration? originalGame;
        private readonly string root;
        private readonly string sourceVmf;
        private readonly string copyVmf;

        public VmfHandoffTests()
        {
            originalGame = GameConfigurationManager.GameConfiguration;
            root = Path.Combine(Path.GetTempPath(), "cpx-handoff-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "maps"));
            Directory.CreateDirectory(Path.Combine(root, "game", "maps"));
            Directory.CreateDirectory(Path.Combine(root, "temp"));

            GameConfigurationManager.GameConfiguration = new GameConfiguration
            {
                Name = "Garry's Mod",
                GameFolder = Path.Combine(root, "game"),
                MapFolder = Path.Combine(root, "game", "maps"),
            };

            sourceVmf = Path.Combine(root, "maps", "rp_test.vmf");
            copyVmf = Path.Combine(root, "temp", "rp_test.vmf");
            File.WriteAllText(sourceVmf, "versioninfo\n{\n}\n");
            File.WriteAllText(copyVmf, "versioninfo\n{\n}\n");
        }

        public void Dispose()
        {
            GameConfigurationManager.RestoreCurrentContext();
            GameConfigurationManager.GameConfiguration = originalGame;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }

        private CompileContext Build()
        {
            GameConfigurationManager.BackupCurrentContext();
            return GameConfigurationManager.BuildContext(new Map(sourceVmf));
        }

        [Fact]
        public void SetFileMovesTheLiveContext()
        {
            var context = Build();

            GameConfigurationManager.ModifyCurrentContext($"COMPILE_PAL_SET file \"{copyVmf}\"");

            Assert.Equal(copyVmf, context.MapFile);
            Assert.Equal(Path.ChangeExtension(copyVmf, "bsp"), context.BSPFile);
            Assert.Equal(sourceVmf, context.SourceMapFile);
            Assert.True(context.IsRedirected);
            // Same file name, so the game's maps folder gets the map under the name it always had.
            Assert.Equal(Path.Combine(root, "game", "maps", "rp_test.bsp"), context.CopyLocation);
        }

        /// <summary>
        /// The route a plugin's line actually takes: its output arrives in chunks, is split into lines,
        /// checked against the error catalogue, and only then reaches the command handler.
        /// </summary>
        [Fact]
        public void APluginsOutputLineMovesTheLiveContext()
        {
            var context = Build();
            LogBacktrack ignore = _ => { };
            CompilePalLogger.OnBacktrack += ignore;
            try
            {
                CompilePalLogger.LogProgressive("vmf   rp_test.vmf\r\nout   done\r\nCOMPILE_PAL_SET file \"");
                // The chunk ended mid-path. Nothing may happen until the line is whole.
                Assert.Equal(sourceVmf, context.MapFile);

                CompilePalLogger.LogProgressive($"{copyVmf}\"\r\n");
                Assert.Equal(copyVmf, context.MapFile);
            }
            finally
            {
                CompilePalLogger.OnBacktrack -= ignore;
            }
        }

        [Fact]
        public void ACommandCutInsideItsNameDoesNotThrow()
        {
            Build();
            LogBacktrack ignore = _ => { };
            CompilePalLogger.OnBacktrack += ignore;
            try
            {
                CompilePalLogger.LogProgressive("COMPILE_PAL_SET fi");
                CompilePalLogger.LogProgressive("le \"x.vmf\"\r\n");
            }
            finally
            {
                CompilePalLogger.OnBacktrack -= ignore;
            }
        }

        [Fact]
        public void TheNextMapStartsFromItsOwnFile()
        {
            Build();
            GameConfigurationManager.ModifyCurrentContext($"COMPILE_PAL_SET file \"{copyVmf}\"");
            GameConfigurationManager.RestoreCurrentContext();

            var next = Build();

            Assert.Equal(sourceVmf, next.MapFile);
            Assert.False(next.IsRedirected);
        }

        /// <summary>
        /// A cancelled or failed map never reaches RestoreCurrentContext in the map loop. The redirect
        /// used to live on in a static field that BuildContext preferred, so the next compile, of
        /// any map, built the previous run's temp copy.
        /// </summary>
        [Fact]
        public void ARedirectFromAnAbandonedMapDoesNotReachTheNextCompile()
        {
            Build();
            GameConfigurationManager.ModifyCurrentContext($"COMPILE_PAL_SET file \"{copyVmf}\"");

            // no RestoreCurrentContext: the map was cancelled
            var next = Build();

            Assert.Equal(sourceVmf, next.MapFile);
            Assert.Equal(Path.ChangeExtension(sourceVmf, "bsp"), next.BSPFile);
            Assert.False(next.IsRedirected);
        }

        [Fact]
        public void AStepsConfigurationChangeEndsWithTheMap()
        {
            var chosen = GameConfigurationManager.GameConfiguration!;
            string? vbsp = chosen.VBSP;

            Build();
            GameConfigurationManager.ModifyCurrentContext("COMPILE_PAL_SET vbsp_exe \"C:\\elsewhere\\vbsp.exe\"");
            Assert.Equal("C:\\elsewhere\\vbsp.exe", GameConfigurationManager.GameConfiguration!.VBSP);

            GameConfigurationManager.RestoreCurrentContext();

            Assert.Same(chosen, GameConfigurationManager.GameConfiguration);
            Assert.Equal(vbsp, chosen.VBSP);
        }

        [Fact]
        public void SubstitutionSeparatesTheCompiledVmfFromTheQueuedOne()
        {
            string text = GameConfigurationManager.SubstituteValues("$vmfFile$|$sourceVmf$", copyVmf, quote: false, sourceMapFile: sourceVmf);

            Assert.Equal($"{copyVmf}|{sourceVmf}", text);
        }

        [Fact]
        public void SourceVmfFallsBackToTheMapGiven()
        {
            Assert.Equal(sourceVmf, GameConfigurationManager.SubstituteValues("$sourceVmf$", sourceVmf, quote: false));
        }

        [Fact]
        public void CompiledFilesComeBackBesideTheQueuedMap()
        {
            var context = Build();
            GameConfigurationManager.RedirectMapFile(context, copyVmf);
            File.WriteAllText(Path.ChangeExtension(copyVmf, "bsp"), "new bsp");
            File.WriteAllText(Path.ChangeExtension(copyVmf, "prt"), "new prt");
            File.WriteAllText(Path.ChangeExtension(copyVmf, "lin"), "new lin");

            GameConfigurationManager.MirrorRedirectedOutputs(context);

            Assert.Equal("new bsp", File.ReadAllText(Path.ChangeExtension(sourceVmf, "bsp")));
            Assert.Equal("new prt", File.ReadAllText(Path.ChangeExtension(sourceVmf, "prt")));
            Assert.Equal("new lin", File.ReadAllText(Path.ChangeExtension(sourceVmf, "lin")));
        }

        [Fact]
        public void AStalePointfileIsRemovedWhenTheCompileDidNotLeak()
        {
            File.WriteAllText(Path.ChangeExtension(sourceVmf, "lin"), "old leak");
            var context = Build();
            GameConfigurationManager.RedirectMapFile(context, copyVmf);
            File.WriteAllText(Path.ChangeExtension(copyVmf, "bsp"), "new bsp");

            GameConfigurationManager.MirrorRedirectedOutputs(context);

            Assert.False(File.Exists(Path.ChangeExtension(sourceVmf, "lin")));
        }

        /// <summary>
        /// File times come from a clock that ticks every few milliseconds, so a BSP written just after the
        /// redirect can be stamped just before it. CI caught this: it is this run's file, not a leftover.
        /// </summary>
        [Fact]
        public void ABspStampedJustBeforeTheRedirectIsStillThisRuns()
        {
            var context = Build();
            GameConfigurationManager.RedirectMapFile(context, copyVmf);
            string bsp = Path.ChangeExtension(copyVmf, "bsp");
            File.WriteAllText(bsp, "new bsp");
            File.SetLastWriteTimeUtc(bsp, context.RedirectedAt!.Value.AddMilliseconds(-10));

            GameConfigurationManager.MirrorRedirectedOutputs(context);

            Assert.Equal("new bsp", File.ReadAllText(Path.ChangeExtension(sourceVmf, "bsp")));
        }

        [Fact]
        public void ABspFromAnEarlierRunIsLeftAlone()
        {
            string leftover = Path.ChangeExtension(copyVmf, "bsp");
            File.WriteAllText(leftover, "earlier run");
            File.SetLastWriteTimeUtc(leftover, DateTime.UtcNow.AddHours(-1));
            File.WriteAllText(Path.ChangeExtension(sourceVmf, "lin"), "old leak");
            var context = Build();
            GameConfigurationManager.RedirectMapFile(context, copyVmf);

            GameConfigurationManager.MirrorRedirectedOutputs(context);

            Assert.False(File.Exists(Path.ChangeExtension(sourceVmf, "bsp")));
            Assert.True(File.Exists(Path.ChangeExtension(sourceVmf, "lin")));
        }

        [Fact]
        public void NothingIsCopiedWithoutARedirect()
        {
            var context = Build();
            File.WriteAllText(Path.ChangeExtension(sourceVmf, "lin"), "real leak");

            GameConfigurationManager.MirrorRedirectedOutputs(context);

            Assert.True(File.Exists(Path.ChangeExtension(sourceVmf, "lin")));
        }

        private const string InstanceMap = "versioninfo\r\n{\r\n}\r\nentity\r\n{\r\n\t\"id\" \"2\"\r\n\t\"classname\" \"func_instance\"\r\n\t\"file\" \"{0}\"\r\n}\r\n";

        private string WriteInstanceMap(string file)
        {
            File.WriteAllText(sourceVmf, InstanceMap.Replace("{0}", file));
            return sourceVmf;
        }

        [Fact]
        public void TheCopyKeepsTheMapsNameInAFolderOfItsOwn()
        {
            string copy = VmfHandoff.CopyPath(sourceVmf, "VMFFIX");

            Assert.Equal("rp_test.vmf", Path.GetFileName(copy));
            Assert.NotEqual(Path.GetDirectoryName(sourceVmf), Path.GetDirectoryName(copy));
            // A map of the same name elsewhere gets a different folder.
            Assert.NotEqual(copy, VmfHandoff.CopyPath(Path.Combine(root, "other", "rp_test.vmf"), "VMFFIX"));
        }

        [Fact]
        public void AnInstanceBesideTheMapIsStillFoundFromTheCopy()
        {
            Directory.CreateDirectory(Path.Combine(root, "maps", "instances"));
            string instance = Path.Combine(root, "maps", "instances", "door.vmf");
            File.WriteAllText(instance, "");
            var vmf = VmfDocument.Load(WriteInstanceMap("instances/door.vmf"));

            Assert.Equal(1, VmfHandoff.RetargetInstances(vmf, sourceVmf, copyVmf));

            string written = vmf.GetValue(vmf.Entities[0], "file")!;
            // VBSP joins the value onto the copy's folder.
            // VBSP reads a backslash in a value as an escape, so the path must use forward slashes.
            Assert.DoesNotContain('\\', written);
            Assert.Equal(instance, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(copyVmf)!, written)));
        }

        [Fact]
        public void AnInstanceUnderTheMapsFolderIsFoundTheWayVbspFindsIt()
        {
            Directory.CreateDirectory(Path.Combine(root, "maps", "sub"));
            Directory.CreateDirectory(Path.Combine(root, "maps", "instances"));
            string nested = Path.Combine(root, "maps", "sub", "rp_test.vmf");
            File.WriteAllText(nested, InstanceMap.Replace("{0}", "instances/door"));
            string instance = Path.Combine(root, "maps", "instances", "door.vmf");
            File.WriteAllText(instance, "");

            Assert.Equal(instance, VmfHandoff.ResolveInstance(nested, "instances/door"));
        }

        [Fact]
        public void AMissingInstanceIsLeftAsWritten()
        {
            var vmf = VmfDocument.Load(WriteInstanceMap("instances/gone.vmf"));

            Assert.Equal(0, VmfHandoff.RetargetInstances(vmf, sourceVmf, copyVmf));
            Assert.Equal("instances/gone.vmf", vmf.GetValue(vmf.Entities[0], "file"));
        }

        [Fact]
        public void WritingTheCopyLeavesTheMapAlone()
        {
            byte[] before = File.ReadAllBytes(sourceVmf);
            var vmf = VmfDocument.Load(sourceVmf);

            string copy = VmfHandoff.Write(vmf, sourceVmf, sourceVmf, "VmfHandoffTests-" + Guid.NewGuid().ToString("N"));
            try
            {
                Assert.True(File.Exists(copy));
                Assert.Equal(before, File.ReadAllBytes(sourceVmf));
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(copy))!, recursive: true);
            }
        }

        [Fact]
        public void TheQueuedVmfIsNeverWritten()
        {
            byte[] before = File.ReadAllBytes(sourceVmf);
            var context = Build();
            GameConfigurationManager.RedirectMapFile(context, copyVmf);
            File.WriteAllText(Path.ChangeExtension(copyVmf, "bsp"), "new bsp");

            GameConfigurationManager.MirrorRedirectedOutputs(context);

            Assert.Equal(before, File.ReadAllBytes(sourceVmf));
        }
    }
}
