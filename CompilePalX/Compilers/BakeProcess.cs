using System;
using System.IO;
using System.Linq;
using System.Threading;
using CompilePalX.Bake;
using CompilePalX.Compiling;

namespace CompilePalX.Compilers
{
    /// <summary>
    /// Bakes Viswright's shut windows: draws what each window looks out on into the picture it shows
    /// once shut, and puts the pictures in the map's pakfile (see <see cref="WindowBaker"/>).
    ///
    /// Order 10.5: after VRAD, whose lighting the pictures are drawn with, and after PACK, so its
    /// pakfile is the one added to; before CUBEMAPS and ENTLUMP, which both need the map as it will
    /// ship. It works on the BSP in the game's maps folder, as ENTLUMP does, and copies the result to
    /// the BSP beside the VMF when the two are separate.
    /// </summary>
    class BakeProcess : CompileProcess
    {
        public BakeProcess() : base("BAKE") { }

        public override void Run(CompileContext context, CancellationToken cancellationToken)
        {
            CompileErrors = [];
            if (!CanRun(context)) return;

            try
            {
                CompilePalLogger.LogLine("\nCompilePal - Bake Window Pictures", 900);
                string bsp = File.Exists(context.CopyLocation) ? context.CopyLocation : context.BSPFile;
                if (!File.Exists(bsp))
                {
                    CompilePalLogger.LogCompileError($"Could not find a compiled BSP: {bsp}\n",
                        new Error($"Could not find a compiled BSP: {bsp}", ErrorSeverity.Error));
                    return;
                }

                var result = WindowBaker.Bake(bsp, context.Configuration.GameFolder, line => CompilePalLogger.LogLine(line));
                foreach (var note in result.Notes)
                    CompilePalLogger.LogLineColor(note, 1);
                if (result.Faces == 0)
                {
                    CompilePalLogger.LogLine("No Viswright window stand-ins with pictures in this map; nothing to bake.");
                    return;
                }
                if (cancellationToken.IsCancellationRequested) return;

                PakWriter.AddFiles(bsp, result.Files);
                long bytes = result.Files.Values.Sum(f => (long)f.Length);
                CompilePalLogger.LogLine($"Baked {result.Faces} window picture(s) into the map's pakfile ({bytes / 1024:N0} KB).");

                // the BSP beside the VMF, when COPY made the maps-folder one from it
                if (!string.Equals(Path.GetFullPath(bsp), Path.GetFullPath(context.BSPFile), StringComparison.OrdinalIgnoreCase) && File.Exists(context.BSPFile))
                    File.Copy(bsp, context.BSPFile, overwrite: true);
            }
            catch (Exception e)
            {
                CompilePalLogger.LogLine("Something broke:");
                CompilePalLogger.LogLineCompileError($"{e}",
                    new Error(e.ToString(), "CompilePal Internal Error", ErrorSeverity.FatalError));
            }
        }
    }
}
