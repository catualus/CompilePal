using System.Collections.Generic;
using CompilePalX.Compiling;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace CompilePalX.Compilers
{
    class ShutdownProcess : CompileProcess
    {
        public ShutdownProcess() : base("SHUTDOWN") { }

        public override void Run(CompileContext context, CancellationToken cancellationToken)
        {

            CompileErrors = [];
            if (!CanRun(context)) return;

            if (cancellationToken.IsCancellationRequested)
                return;

            // don't run unless it's the last map of the queue
            if (CompilingManager.MapFiles.Last().File == context.MapFile)
            {
                // Under Wine, Windows' shutdown command can at most end the Wine session - it cannot
                // power off the Linux machine, which is what this step promises. Saying so beats a step
                // that reports success and leaves the computer running all night.
                if (Platform.Wine.IsRunning)
                {
                    CompilePalLogger.LogLineColor(
                        "\nSHUTDOWN skipped: Compile Pal is running under Wine, which cannot shut down the Linux machine. " +
                        "To power off after a compile on Linux, run Compile Pal from a script that calls `systemctl poweroff` when it exits.",
                        Error.GetSeverityBrush(2));
                    return;
                }

                CompilePalLogger.LogLine("\nCompilePal - Shutdown", 900);
                CompilePalLogger.LogLine("The system will shutdown soon.");
                CompilePalLogger.LogLine("You can cancel this shutdown by using the command \"shutdown -a\"");

                var startInfo = new ProcessStartInfo("shutdown", GetParameterString());
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;

                Process = new Process { StartInfo = startInfo };
                Process.Start();
            }
        }
    }
}
