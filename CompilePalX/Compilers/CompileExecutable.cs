using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Threading;
using System.Windows.Media;
using CompilePalX.Compiling;

namespace CompilePalX.Compilers
{
    class CompileExecutable(string metadata, string? parameterFolder = null) : CompileProcess(metadata, parameterFolder)
    {
        public override void Run(CompileContext c, CancellationToken cancellationToken)
        {
            CompileErrors = [];

            if (!CanRun(c)) return;

            // listen for cancellations
            cancellationToken.Register(() =>
            {
                try
                {
                    Cancel();
                }
                catch (InvalidOperationException) { }
                catch (Exception e) { ExceptionHandler.LogException(e); }
            });

            Process = new Process();
            if (Metadata.ReadOutput)
            {
                Process.StartInfo = new ProcessStartInfo
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardInput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
            }

            var args = GameConfigurationManager.SubstituteValues(GetParameterString(), c.MapFile);

            bool normalPriority = false;
            if (args.Contains("-normal_priority"))
            {
                args = args.Replace("-normal_priority", string.Empty);
                normalPriority = true;
            }

            Process.StartInfo.FileName = GameConfigurationManager.SubstituteValues(Metadata.Path);
            Process.StartInfo.Arguments = string.Join(" ", args);
            Process.StartInfo.WorkingDirectory = Metadata.WorkingDirectory != null ? GameConfigurationManager.SubstituteValues(Metadata.WorkingDirectory, quote: false) : ".";

            CompilePalLogger.LogLineDebug($"Running '{Process.StartInfo.FileName}' with args '{Process.StartInfo.Arguments}'");

            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    CompilePalLogger.LogDebug($"Cancelled {Metadata.Name}");
                    return;
                }
                Process.Start();
            }
            catch (Exception e)
            {
                CompilePalLogger.LogDebug(e.ToString());
                CompilePalLogger.LogCompileError($"Failed to run executable: {Process.StartInfo.FileName}\n", new Error($"Failed to run executable: {Process.StartInfo.FileName}", ErrorSeverity.FatalError));
                return;
            }

            if (normalPriority)
            {
                Process.PriorityClass = ProcessPriorityClass.Normal;
                CompilePalLogger.LogLine($"Running {Name} with normal priority");
            }
            else 
                Process.PriorityClass = ProcessPriorityClass.BelowNormal;

            if (Metadata.ReadOutput)
            { 
                ReadOutput(cancellationToken);

                /*
                 * A compile tool that fails calls Error() in cmdlib, which prints the reason and exits
                 * non-zero. The message it prints is whatever the failure happened to be - there are
                 * several hundred across vbsp, vvis and vrad, and the error catalogue recognises only
                 * the ones somebody has written an entry for.
                 *
                 * So the exit code, not the message, is what tells us the step failed. Without this a
                 * fatal vbsp error printed as ordinary white text and the compile carried on into vvis
                 * and vrad against a .bsp that had never been written - which then fails in turn with
                 * something misleading ("couldn't read .prt / the map likely has a leak") and sends you
                 * hunting a leak that does not exist.
                 *
                 * Fatal rather than a warning: these three run in sequence and each needs the previous
                 * one's output, so once one fails there is nothing useful left to do. Severity 5 is what
                 * CompilingManager watches for to stop the run.
                 *
                 * Not while cancelling. Cancel kills the process, which is a non-zero exit by
                 * definition, and reporting the user's own cancellation as a compile failure would be
                 * both wrong and alarming.
                 */
                if (Metadata.CheckExitCode && !cancellationToken.IsCancellationRequested && Process.ExitCode != 0)
                {
                    string message = $"{Name} failed with exit code {Process.ExitCode} (0x{Process.ExitCode:X}). " +
                                     "The reason it gives is in its output above.";

                    CompilePalLogger.LogCompileError(message + "\n",
                        new Error(message, $"{Name} failed", ErrorSeverity.FatalError));
                }
            }
        }

        private void ReadOutput(CancellationToken cancellationToken)
        {
            char[] buffer = new char [256];
            Task<int>? read = null;
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                if (read == null)
                    read = Process.StandardOutput.ReadAsync(buffer, 0, buffer.Length);

                read.Wait(100, cancellationToken); // an arbitrary timeout

                if (read.IsCompleted)
                {
                    if (read.Result > 0)
                    {
                        string text = new (buffer, 0, read.Result);
                        CompilePalLogger.LogProgressive(text);

                        read = null; // task completed so we need to create a new one
                        continue;
                    }

                    // got -1, process ended
                    break;
                }

            }

            Process.WaitForExit();
        }
    }
}
