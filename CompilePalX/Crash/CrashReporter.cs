using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using CompilePalX.Compiling;
using Newtonsoft.Json;

namespace CompilePalX.Crash
{
    /// <summary>
    /// Shows a crash to the user, and sends it if they say so.
    ///
    /// Everything here is written on the assumption that the application is already broken. Each
    /// stage is independently guarded, the order runs from most to least important, and no stage is
    /// allowed to stop a later one:
    ///
    ///   1. Write the crash to disk. Always, first, before anything that could fail.
    ///   2. Show the dialog. If WPF cannot, fall back to the Win32 message box.
    ///   3. Send, only if asked.
    ///
    /// The previous handler did none of that. It called into the taskbar progress manager, which
    /// threw a NullReferenceException when the window did not exist yet, out of an async void
    /// method - so the exception was re-raised on the dispatcher, handled again, and thrown again.
    /// The dialog was never reached, Environment.Exit was never reached, and the application
    /// disappeared leaving only a file in CrashLogs.
    /// </summary>
    public static class CrashReporter
    {
        public const string CrashLogFolder = "./CrashLogs";

        /// <summary>Whether this build has anywhere to send a report.</summary>
        public static bool CanSend => TelemetryManager.IsConfigured;

        /// <summary>
        /// Guards against a crash inside crash handling.
        ///
        /// A fault in the dialog, or in the send, comes back through the same global handlers that
        /// called this. Without the latch that is an unbounded loop, which is close to what the
        /// 1.0.1 startup failure did - the same exception three times, each one triggering the next.
        /// </summary>
        private static int handling;

        /// <summary>
        /// Handles one crash from start to finish.
        ///
        /// Returns only after the user has answered, because a fatal crash exits immediately
        /// afterwards and a dialog nobody waited for would never be seen.
        /// </summary>
        public static void Handle(Exception exception, bool fatal)
        {
            // Re-entrant crashes are written down and otherwise ignored. Showing a second dialog on
            // top of the first helps nobody and can stack indefinitely.
            if (Interlocked.Exchange(ref handling, 1) == 1)
            {
                TryWrite(exception, fatal, suffix: "-during-crash-handling");
                return;
            }

            try
            {
                CrashReport report;
                try
                {
                    report = CrashReport.From(exception, fatal);
                }
                catch (Exception)
                {
                    // Building the report failed, which means the exception itself is malformed in
                    // some way. Write what can be written and stop.
                    TryWrite(exception, fatal, suffix: "-unreadable");
                    return;
                }

                string? savedTo = TryWriteReport(report);

                bool send = Ask(report);

                if (send)
                    TrySend(report);

                if (savedTo is not null)
                    CompilePalLogger.LogLineDebug($"Crash report saved to {savedTo}");
            }
            catch (Exception)
            {
                // Nothing above is allowed to escape. Anything that reaches here would otherwise be
                // raised on the dispatcher and land straight back in the handler that called this.
            }
            finally
            {
                Interlocked.Exchange(ref handling, 0);
            }
        }

        /// <summary>
        /// Asks the user, falling back through progressively simpler options.
        ///
        /// The WPF dialog needs an Application and a dispatcher. Neither exists if the crash
        /// happened before OnStartup finished, or after shutdown began, so the Win32 message box is
        /// the floor: it needs nothing but user32.
        /// </summary>
        private static bool Ask(CrashReport report)
        {
            try
            {
                var application = Application.Current;

                if (application?.Dispatcher is { } dispatcher && !dispatcher.HasShutdownStarted)
                {
                    return dispatcher.Invoke(() => ShowWindow(report));
                }
            }
            catch (Exception)
            {
                // Fall through to the message box.
            }

            return ShowNativeFallback(report);
        }

        private static bool ShowWindow(CrashReport report)
        {
            try
            {
                var window = new CrashWindow(report);

                // Owner only if there is a live one. A window whose owner is mid-teardown will not
                // show at all, which is the failure this whole class exists to avoid.
                try
                {
                    var owner = Application.Current?.MainWindow;
                    if (owner is not null && owner.IsLoaded && owner.IsVisible)
                        window.Owner = owner;
                }
                catch (Exception) { /* no owner, still shows */ }

                window.ShowDialog();
                return window.ShouldSend;
            }
            catch (Exception)
            {
                return ShowNativeFallback(report);
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        private const uint MB_YESNO = 0x00000004;
        private const uint MB_OK = 0x00000000;
        private const uint MB_ICONERROR = 0x00000010;
        private const uint MB_SETFOREGROUND = 0x00010000;
        private const int IDYES = 6;

        /// <summary>
        /// The last resort, and the reason a crash is never silent again.
        ///
        /// user32's message box needs no Application, no dispatcher, no theme and no resources. If
        /// this does not appear then the process could not display anything at all.
        /// </summary>
        private static bool ShowNativeFallback(CrashReport report)
        {
            try
            {
                string body =
                    "Compile Pal has stopped.\n\n" +
                    $"{report.Kind}\n{report.Message}\n\n" +
                    $"A full report has been saved to the CrashLogs folder next to the application.";

                if (!CanSend)
                {
                    MessageBoxW(IntPtr.Zero, body, "Compile Pal", MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
                    return false;
                }

                int answer = MessageBoxW(
                    IntPtr.Zero,
                    body + "\n\nSend an anonymous report so this can be fixed?",
                    "Compile Pal",
                    MB_YESNO | MB_ICONERROR | MB_SETFOREGROUND);

                return answer == IDYES;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ writing

        private static string? TryWriteReport(CrashReport report)
        {
            try
            {
                Directory.CreateDirectory(CrashLogFolder);

                string name = report.When.ToString("yyyy-MM-ddTHH-mm-ss") + ".txt";
                string path = Path.Combine(CrashLogFolder, name);

                // The saved copy is the redacted one, so handing someone a crash log does not hand
                // them the account name of whoever produced it.
                File.WriteAllText(path, report.ToText(), Encoding.UTF8);

                return path;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Raw last-ditch write for when even building the report failed.</summary>
        private static void TryWrite(Exception e, bool fatal, string suffix)
        {
            try
            {
                Directory.CreateDirectory(CrashLogFolder);

                string name = DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ss") + suffix + ".txt";
                File.WriteAllText(
                    Path.Combine(CrashLogFolder, name),
                    CrashReport.Redact(e.ToString()),
                    Encoding.UTF8);
            }
            catch (Exception)
            {
                // There is nothing below this.
            }
        }

        // ------------------------------------------------------------------ sending

        private static void TrySend(CrashReport report)
        {
            try
            {
                // Bounded and synchronous. The caller exits the process immediately after a fatal
                // crash, so a fire-and-forget send would be killed before it left the machine - and
                // Task.Run keeps it off whatever thread is unwinding.
                var send = System.Threading.Tasks.Task.Run(() => TelemetryManager.SendCrashAsync(report));

                if (!send.Wait(TimeSpan.FromSeconds(5)))
                    CompilePalLogger.LogLineDebug("Crash report send timed out.");
            }
            catch (Exception e)
            {
                CompilePalLogger.LogLineDebug($"Crash report send failed: {e.Message}");
            }
        }
    }
}
