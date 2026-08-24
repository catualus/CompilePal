using System;
using System.IO;
using CompilePalX.Compiling;
using CompilePalX.Crash;

namespace CompilePalX
{
    /// <summary>
    /// Where every unhandled exception ends up.
    ///
    /// Rewritten after 1.0.1 crashed on startup and told nobody. The chain was:
    ///
    ///   1. A parameters file could not be read, which threw while the main window was being built.
    ///   2. This handler called ProgressManager.ErrorProgress, which dereferenced a taskbar item
    ///      that does not exist until the main window finishes being built, and threw.
    ///   3. That second exception came out of an `async void` method, so it was re-raised on the
    ///      dispatcher, arrived back here, and did the same thing again.
    ///   4. The dialog was never shown and Environment.Exit was never reached. The application
    ///      disappeared, leaving a file in a folder nobody has a reason to look in.
    ///
    /// Three rules follow from that, and the code below is arranged around them:
    ///
    ///   - This method cannot throw. Every stage is guarded and no stage can stop a later one.
    ///   - It is not async. A crash handler that returns before it has done anything is a crash
    ///     handler that races the process exit it is supposed to precede.
    ///   - The user is told. Always, by a dialog if one can be shown and by the Win32 message box
    ///     if not. A crash that only writes a file is a crash that did not happen, as far as the
    ///     person using the application knows.
    /// </summary>
    static class ExceptionHandler
    {
        /// <summary>
        /// Handles an exception.
        ///
        /// <paramref name="crash"/> means the application cannot continue: the report is shown as
        /// fatal and the process exits once the user has answered. Otherwise this reports and
        /// returns, and the application carries on.
        /// </summary>
        public static void LogException(Exception e, bool crash = true)
        {
            // The log first, because it is the one thing that works even when everything else does
            // not, and because the debug log is what a bug report is reconstructed from.
            try
            {
                CompilePalLogger.LogLine("An exception was caught by the ExceptionHandler:");
                CompilePalLogger.LogLine(e.ToString());

                if (e.InnerException != null)
                    CompilePalLogger.LogLine(e.InnerException.ToString());
            }
            catch (Exception) { /* the logger itself is part of the UI; it can be broken too */ }

            // A fatal exception exits the process, so pending edits are persisted before they are
            // lost. Before the dialog, because the dialog waits for a person.
            try
            {
                ConfigurationManager.Flush();
            }
            catch (Exception flushException)
            {
                try
                {
                    CompilePalLogger.LogLine($"Failed to save pending changes during crash handling: {flushException}");
                }
                catch (Exception) { }
            }

            // Counted only if usage reporting is on. This is the routine counter and it stays
            // behind that setting; the report itself is asked for separately by CrashReporter.
            try
            {
                TelemetryManager.Error();
            }
            catch (Exception) { }

            // Taskbar first, and guarded. This is the call that used to throw: it is safe now,
            // but the guard stays because this method is the last thing standing.
            try
            {
                ProgressManager.ErrorProgress();
            }
            catch (Exception) { }

            // Writes the report, shows it, and sends it if the user says so. Returns only once
            // they have answered, which is what lets the exit below be immediate.
            try
            {
                CrashReporter.Handle(e, crash);
            }
            catch (Exception) { }

            if (crash)
            {
                // Non-zero. The old handler exited 0, which told anything watching that a fatal
                // crash was a clean shutdown.
                Environment.Exit(1);
            }
        }
    }
}
