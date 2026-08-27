using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CompilePalX.Compilers;
using CompilePalX.Compiling;
using Newtonsoft.Json.Linq;

namespace CompilePalX.Configuration
{
    /// <summary>How much attention a step says a queued map needs.</summary>
    public enum StatusSeverity
    {
        /// <summary>Nothing to say beyond what the chip already says.</summary>
        Ok,

        /// <summary>Worth reading before the run, and worth confirming if the step asks.</summary>
        Info,

        /// <summary>Something will happen that cannot be undone afterwards.</summary>
        Warn,

        /// <summary>The run must not start until this is dealt with.</summary>
        Blocking,
    }

    /// <summary>One step's answer about one map.</summary>
    public sealed record PluginMapStatus(
        string StepName,
        string MapName,
        string Label,
        string Detail,
        StatusSeverity Severity,
        bool Confirm);

    /// <summary>
    /// Asks each step what it makes of each queued map.
    ///
    /// WHY THIS EXISTS
    ///
    /// A compile step only gets to speak once the compile is running, which is too late for
    /// everything that is decided before it. A step that publishes somewhere public cannot say "this
    /// map is not bound to anything" until VBSP, VVIS and VRAD have already run - an hour of
    /// compiling to be told a text file was missing a number - and it cannot say "this replaces a map
    /// three thousand people are subscribed to" at a point where anyone could still stop.
    ///
    /// So a step may declare a MapStatus command in its meta.json. Compile Pal runs it for every
    /// queued map, shows what it says on the map's card, and refuses to start when one of them says
    /// the run must not.
    ///
    /// WHAT COMPILE PAL UNDERSTANDS
    ///
    /// A label, a sentence, a severity and whether to confirm. It has no idea what the step does with
    /// any of it. That is the point: the step owns the meaning, this owns the chip.
    /// </summary>
    /// <remarks>
    /// Internal because every one of these methods mentions <see cref="CompileProcess"/>, which is.
    /// The records above are public: they are bound to a card in the main window.
    /// </remarks>
    internal static class PluginStatus
    {
        /// <summary>
        /// How long a step gets to answer.
        ///
        /// This runs whenever the queue changes, in front of someone who is about to press Compile,
        /// so a step that hangs must not take the window with it. A status that does not arrive in
        /// time is simply not shown - it is decoration on a card, and the compile itself is unchanged.
        /// </summary>
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

        /// <summary>Whether any step could have something to say at all.</summary>
        public static bool AnyReporting(IEnumerable<CompileProcess> steps) =>
            steps.Any(s => !string.IsNullOrWhiteSpace(s.Metadata.MapStatus));

        /// <summary>What every reporting step makes of one map.</summary>
        public static async Task<List<PluginMapStatus>> QueryAsync(
            Map map, IEnumerable<CompileProcess> steps, CancellationToken cancellationToken = default)
        {
            var results = new List<PluginMapStatus>();

            foreach (var step in steps.Where(s => !string.IsNullOrWhiteSpace(s.Metadata.MapStatus)))
            {
                if (cancellationToken.IsCancellationRequested)
                    return results;

                // Not in this map's preset means it will not run for this map, so it has no standing
                // to say anything about it - least of all to stop the compile.
                if (map.Preset is not { } preset || !step.PresetDictionary.ContainsKey(preset))
                {
                    CompilePalLogger.LogLineDebug(
                        $"{step.Name} has no parameters under {map.FileName}'s preset ({map.Preset?.Name ?? "none"}), so it is not asked");
                    continue;
                }

                var status = await QueryAsync(step, map, preset, cancellationToken);

                if (status != null)
                    results.Add(status);
            }

            return results;
        }

        /// <summary>
        /// Refreshes the chips on every queued map's card.
        ///
        /// One refresh at a time: the previous one is cancelled rather than queued, because these
        /// are triggered by typing in a preset and the only answer anyone wants is the one for the
        /// state the queue is in now.
        /// </summary>
        public static async Task RefreshAllAsync(IEnumerable<Map> maps, IEnumerable<CompileProcess> steps)
        {
            var stepList = steps.ToList();
            var mapList = maps.ToList();

            if (!AnyReporting(stepList))
                return;

            var token = Restart();

            foreach (var map in mapList)
            {
                var statuses = await QueryAsync(map, stepList, token);

                if (token.IsCancellationRequested)
                    return;

                // The collection is bound to a card, so it can only be touched from the UI thread.
                MainWindow.ActiveDispatcher.Invoke(() =>
                {
                    map.PluginStatuses.Clear();

                    foreach (var status in statuses)
                        map.PluginStatuses.Add(status);
                });
            }
        }

        private static CancellationTokenSource? refreshing;

        private static CancellationToken Restart()
        {
            var previous = refreshing;
            refreshing = new CancellationTokenSource();

            try
            {
                previous?.Cancel();
                previous?.Dispose();
            }
            catch (ObjectDisposedException) { }

            return refreshing.Token;
        }

        /// <summary>
        /// Asks every step about every map that is about to be compiled, from scratch.
        ///
        /// Not the chips: those are whatever the last refresh left, and a compile is exactly the
        /// moment where a stale answer matters. Something may have been bound, unbound or published
        /// since - by the settings window, or by a text editor.
        /// </summary>
        public static async Task<List<PluginMapStatus>> CollectAsync(
            IEnumerable<Map> maps, IEnumerable<CompileProcess> steps, CancellationToken cancellationToken = default)
        {
            var stepList = steps.ToList();
            var all = new List<PluginMapStatus>();

            if (!AnyReporting(stepList))
                return all;

            foreach (var map in maps.ToList())
                all.AddRange(await QueryAsync(map, stepList, cancellationToken));

            return all;
        }

        private static async Task<PluginMapStatus?> QueryAsync(
            CompileProcess step, Map map, Preset preset, CancellationToken cancellationToken)
        {
            try
            {
                string command = GameConfigurationManager.SubstituteValues(
                    step.Metadata.MapStatus!, map.File, quote: false);

                var (rawFileName, arguments) = PluginCommand.Split(command);

                if (PluginCommand.Resolve(rawFileName) is not { } fileName)
                {
                    CompilePalLogger.LogLineDebug(
                        $"{step.Name} declares a MapStatus program that is not there: {rawFileName}");
                    return null;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = Path.GetDirectoryName(fileName) ?? ".",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                /*
                 * The step's own arguments, under this map's preset - not the preset being edited.
                 * A step cannot answer "what will happen to this map" without them: whether it is
                 * set to publish, or to do the harmless half of what it does, is a parameter.
                 *
                 * Through the environment because they are a command line already, and nesting one
                 * inside another is how a quote in a change note ends up splitting an argument.
                 */
                startInfo.Environment["COMPILE_PAL_STEP_ARGS"] = step.GetParameterString(preset);
                startInfo.Environment["COMPILE_PAL_STEP_ENABLED"] = step.DoRun ? "true" : "false";

                using var process = new Process { StartInfo = startInfo };

                process.Start();

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(Timeout);

                string output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                string errors = await process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);

                var parsed = Parse(step.Name, map, output);

                if (parsed is null)
                    CompilePalLogger.LogLineDebug(
                        $"{step.Name} status for {map.FileName} was not readable (exit {process.ExitCode}): " +
                        $"out=[{Tidy(output, 200)}] err=[{Tidy(errors, 200)}]");

                return parsed;
            }
            catch (OperationCanceledException)
            {
                CompilePalLogger.LogLineDebug($"{step.Name} did not report a status for {map.FileName} in time");
                return null;
            }
            catch (Exception e)
            {
                // A status is a nicety. A step whose status command is broken should show no chip,
                // not stop someone from compiling.
                CompilePalLogger.LogLineDebug($"{step.Name} status for {map.FileName} failed: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Reads one line of JSON from a step.
        ///
        /// Everything is optional and everything is bounded: this text comes from a program in a
        /// plugin folder and lands on a card in the main window, so a thousand-character label is a
        /// broken layout and a newline in one is a card that pushes the queue off screen.
        /// </summary>
        internal static PluginMapStatus? Parse(string stepName, Map map, string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return null;

            // The last non-empty line, so a step that printed something else first is still read.
            string? line = output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(l => l.TrimStart().StartsWith('{'));

            if (line is null)
                return null;

            JObject json;

            try
            {
                json = JObject.Parse(line);
            }
            catch (Exception)
            {
                return null;
            }

            string label = Tidy((string?)json["label"], 60);

            if (label.Length == 0)
                return null;

            return new PluginMapStatus(
                stepName,
                map.FileName,
                label,
                Tidy((string?)json["detail"], 400),
                ParseSeverity((string?)json["severity"]),
                (bool?)json["confirm"] ?? false);
        }

        private static StatusSeverity ParseSeverity(string? value) => value?.ToLowerInvariant() switch
        {
            "blocking" => StatusSeverity.Blocking,
            "warn" => StatusSeverity.Warn,
            "info" => StatusSeverity.Info,
            _ => StatusSeverity.Ok,
        };

        /// <summary>One line, no control characters, capped.</summary>
        private static string Tidy(string? text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var builder = new StringBuilder(text!.Length);

            foreach (char c in text)
                builder.Append(char.IsControl(c) ? ' ' : c);

            string flattened = builder.ToString().Trim();

            return flattened.Length <= maxLength ? flattened : flattened[..maxLength].TrimEnd() + "…";
        }
    }
}
