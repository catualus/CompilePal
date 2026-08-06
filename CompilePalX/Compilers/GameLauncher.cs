using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using CompilePalX.Compiling;

namespace CompilePalX.Compilers
{
    /// <summary>
    /// Launches a Source game and returns a handle to the process that actually ends up running it.
    ///
    /// Steps that drive the game (nav generation, cubemap building) used to call Process.Start on the
    /// game executable and trust the returned handle. That only holds when Steam is already up: with a
    /// cold Steam, launching the game with -steam makes Steam bootstrap and re-spawn the game as a
    /// separate process, and the process we started exits within a second or two. The callers then saw
    /// an immediate exit, assumed the work was finished, and reported success without anything having
    /// been built. See upstream issues #256 and #262.
    ///
    /// This finds the re-spawned process and hands that back instead, so callers can wait on the real
    /// game. Verify the artifact afterwards regardless - a running game is not proof of a built nav mesh.
    /// </summary>
    public static class GameLauncher
    {
        /// <summary>How long to wait for Steam to re-spawn the game after our own process exits.</summary>
        private static readonly TimeSpan AdoptionTimeout = TimeSpan.FromSeconds(90);

        /// <summary>An exit sooner than this is treated as a Steam hand-off rather than a real exit.</summary>
        private static readonly TimeSpan HandoffWindow = TimeSpan.FromSeconds(20);

        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

        /// <summary>Warns if Steam is not running, since that is what triggers the hand-off.</summary>
        public static void WarnIfSteamNotRunning()
        {
            try
            {
                if (Process.GetProcessesByName("steam").Length == 0)
                    CompilePalLogger.LogLine("Steam does not appear to be running. The game may take longer to start, or fail to start entirely.");
            }
            catch (Exception ex)
            {
                CompilePalLogger.LogLineDebug($"Could not check whether Steam is running: {ex.Message}");
            }
        }

        /// <summary>
        /// Starts the game and returns the process that is really running it, or null if the game never
        /// came up. The returned process may be a different one from that started here.
        /// </summary>
        public static Process? Launch(string gameExe, string args, CancellationToken cancellationToken)
        {
            string processName = Path.GetFileNameWithoutExtension(gameExe);

            // remember pre-existing instances so an already-open game is never mistaken for ours
            var preExisting = SafeGetProcessIds(processName);

            WarnIfSteamNotRunning();

            var startInfo = new ProcessStartInfo(gameExe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = false,
            };

            var started = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            started.Start();

            var launchedAt = DateTime.UtcNow;

            // give the process a moment to either settle or hand off to Steam
            while (!started.HasExited)
            {
                if (cancellationToken.IsCancellationRequested)
                    return started;

                if (DateTime.UtcNow - launchedAt > HandoffWindow)
                {
                    // survived the hand-off window, so this really is the game
                    CompilePalLogger.LogLineDebug($"Game process {started.Id} ({processName}) is running directly.");
                    return started;
                }

                Thread.Sleep(PollInterval);
            }

            // Our process exited quickly. Either Steam re-spawned the game elsewhere, or it failed outright.
            CompilePalLogger.LogLine($"Launcher process exited immediately (exit code {SafeExitCode(started)}); looking for the game process Steam started...");

            var adopted = WaitForNewProcess(processName, preExisting, cancellationToken);
            if (adopted is null)
            {
                CompilePalLogger.LogCompileError($"Could not find a running {processName} process after launching it. The game did not start.\n",
                    new Error($"Game process {processName} never started", "Game failed to launch", ErrorSeverity.Error));
                return null;
            }

            CompilePalLogger.LogLine($"Tracking game process {adopted.Id} ({processName}).");
            return adopted;
        }

        /// <summary>Polls for a process with the given name that was not running before we launched.</summary>
        private static Process? WaitForNewProcess(string processName, HashSet<int> preExisting, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + AdoptionTimeout;

            while (DateTime.UtcNow < deadline)
            {
                if (cancellationToken.IsCancellationRequested)
                    return null;

                Process[] candidates;
                try
                {
                    candidates = Process.GetProcessesByName(processName);
                }
                catch (Exception ex)
                {
                    CompilePalLogger.LogLineDebug($"Failed to enumerate processes: {ex.Message}");
                    return null;
                }

                var match = candidates.FirstOrDefault(p => !preExisting.Contains(p.Id));
                if (match != null)
                    return match;

                Thread.Sleep(PollInterval);
            }

            return null;
        }

        /// <summary>
        /// Blocks until the game exits, cancellation is requested, or the optional predicate reports the
        /// work as finished. Returns true if the predicate completed the wait.
        /// </summary>
        public static bool WaitForExit(Process process, CancellationToken cancellationToken, Func<bool>? isComplete = null)
        {
            while (!SafeHasExited(process))
            {
                if (cancellationToken.IsCancellationRequested)
                    return false;

                if (isComplete != null && isComplete())
                    return true;

                Thread.Sleep(PollInterval);
            }

            return false;
        }

        private static HashSet<int> SafeGetProcessIds(string processName)
        {
            try
            {
                return Process.GetProcessesByName(processName).Select(p => p.Id).ToHashSet();
            }
            catch (Exception ex)
            {
                CompilePalLogger.LogLineDebug($"Failed to enumerate existing {processName} processes: {ex.Message}");
                return [];
            }
        }

        private static bool SafeHasExited(Process process)
        {
            try { return process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }

        private static string SafeExitCode(Process process)
        {
            try { return process.ExitCode.ToString(); }
            catch (Exception) { return "unknown"; }
        }
    }
}
