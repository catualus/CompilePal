using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CompilePalX.Compiling;
using Newtonsoft.Json;

namespace CompilePalX
{
    /// <summary>One line of a compiler's <c>-help</c> table.</summary>
    public sealed record ToolOption(string Flag, string Default, string Description)
    {
        /// <summary>
        /// Whether the option expects something after it. The table leaves the Default column empty
        /// for a plain switch and fills it - with a number, a word, or a placeholder like
        /// <c>&lt;N&gt;</c> - for anything that takes a value.
        ///
        /// An inference, not a fact: the table has no arity column, and a handful of options take a
        /// value while printing no default (<c>-insert_search_path</c>, vbsp's <c>-forcematerial</c>).
        /// Those are described in parameters.json, whose entry wins over anything inferred here, and
        /// ShippedParameterDriftTests lists them so a new one is noticed.
        /// </summary>
        [JsonIgnore]
        public bool TakesValue => !string.IsNullOrWhiteSpace(Default);
    }

    /// <summary>
    /// What a compiler says about itself when asked: its banner, and every option it accepts.
    ///
    /// This is the replacement for knowing the option list in advance. The parameter files that ship
    /// with Compile Pal were written against one build of the tools and went stale as soon as the
    /// tools moved - and they move often now that they are developed outside the SDK. The binary is
    /// the only thing that knows what the binary accepts, so it is asked.
    /// </summary>
    public sealed class ToolHelp
    {
        /// <summary>The first banner line, e.g. <c>Breadworks - vrad++ (Sep 10 2026)</c>.</summary>
        public string Banner { get; init; } = "";

        /// <summary>Whoever the banner names as the author: "Breadworks", "ficool2".</summary>
        public string? Author { get; init; }

        /// <summary>The tool as it names itself: "vrad++", "vradplusplus.exe".</summary>
        public string? ToolName { get; init; }

        /// <summary>The build date from the banner, when it carries one.</summary>
        public DateTime? BuildDate { get; init; }

        public IReadOnlyList<ToolOption> Options { get; init; } = [];

        private Dictionary<string, ToolOption>? byFlag;

        /// <summary>Whether the tool lists this flag. Case-insensitive, as the tools themselves are.</summary>
        public bool Has(string flag) => TryGet(flag, out _);

        public bool TryGet(string flag, out ToolOption option)
        {
            byFlag ??= Options
                .GroupBy(o => o.Flag, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            return byFlag.TryGetValue(flag.Trim(), out option!);
        }

        /// <summary>A short label for the UI and the log: "vrad++ (Sep 10 2026)".</summary>
        [JsonIgnore]
        public string Label =>
            (ToolName ?? "tools++")
            + (BuildDate is { } date ? $" ({date.ToString("MMM d yyyy", CultureInfo.InvariantCulture)})" : "");
    }

    /// <summary>
    /// Reads the <c>-help</c> output the tools++ compilers print.
    ///
    /// Every tools++ binary - the standalone "Breadworks" builds and the older ones bundled with
    /// Hammer++ - answers <c>-help</c> with the same shape:
    ///
    /// <code>
    /// Breadworks - vrad++ (Sep 10 2026)
    /// ...
    /// Options:
    ///                          Name | Default | Description
    /// ====================================================================================
    ///                      -ambient |   0 0 0 | Apply a minimum ambient lighting value to the map
    ///                        -final |         | High quality processing. Equivalent to:
    ///                                           -extrasky 8 -extrasoft 4 -StaticPropSampleScale 4
    /// </code>
    ///
    /// A row is a flag, a default and a description separated by pipes; an indented line with no
    /// flag continues the description above it. The stock Valve tools print prose instead of a table
    /// and so parse to nothing, which is the signal that they have to be described by hand.
    /// </summary>
    public static class ToolHelpParser
    {
        private static readonly Regex BannerPattern = new(
            @"^\s*(?<author>[^\-\r\n]+?)\s+-\s+(?<tool>\S+)\s+\((?<date>[A-Za-z]{3}\s+\d{1,2}\s+\d{4})\)\s*$",
            RegexOptions.Compiled);

        private static readonly Regex RowPattern = new(
            @"^\s*(?<flag>-[A-Za-z0-9_]+)\s*\|(?<default>[^|]*)\|\s?(?<desc>.*)$",
            RegexOptions.Compiled);

        private static readonly Regex HeaderPattern = new(
            @"^\s*Name\s*\|\s*Default\s*\|\s*Description\s*$",
            RegexOptions.Compiled);

        /// <summary>
        /// Parses captured output. Returns null when it holds no option table at all - a stock tool,
        /// a crash, or a binary that is not a compiler.
        /// </summary>
        public static ToolHelp? Parse(string? output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return null;

            var lines = output.Replace("\r\n", "\n").Split('\n');

            string banner = "";
            string? author = null, tool = null;
            DateTime? buildDate = null;

            var options = new List<ToolOption>();
            var description = new StringBuilder();
            string? flag = null, def = null;
            bool inTable = false;

            void Commit()
            {
                if (flag is null)
                    return;

                options.Add(new ToolOption(flag, def!.Trim(), description.ToString().Trim()));
                flag = null;
                def = null;
                description.Clear();
            }

            foreach (var raw in lines)
            {
                string line = raw.TrimEnd();

                if (banner.Length == 0 && line.Trim().Length > 0)
                {
                    // The banner is the first non-empty line. Not every tool prints one that matches -
                    // bspzip++ leads with a blank line and then the banner, which the trim handles - but
                    // the first line is kept whatever it says, so the UI has something to show.
                    banner = line.Trim();

                    var bannerMatch = BannerPattern.Match(banner);
                    if (bannerMatch.Success)
                    {
                        author = bannerMatch.Groups["author"].Value.Trim();
                        tool = bannerMatch.Groups["tool"].Value.Trim();

                        if (DateTime.TryParseExact(
                                Regex.Replace(bannerMatch.Groups["date"].Value, @"\s+", " "),
                                "MMM d yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                            buildDate = parsed;
                    }
                }

                if (!inTable)
                {
                    if (HeaderPattern.IsMatch(line))
                        inTable = true;
                    continue;
                }

                // the rule under the header
                if (line.Trim().Length > 0 && line.Trim().All(c => c == '='))
                    continue;

                var row = RowPattern.Match(line);
                if (row.Success)
                {
                    Commit();
                    flag = row.Groups["flag"].Value;
                    def = row.Groups["default"].Value;
                    description.Append(row.Groups["desc"].Value.Trim());
                    continue;
                }

                if (line.Length == 0)
                {
                    // Blank lines appear inside the table (after -lightmapformat's list of formats)
                    // as well as after it, so they neither continue nor end anything.
                    continue;
                }

                if (char.IsWhiteSpace(raw[0]) && flag is not null)
                {
                    // an indented line with no flag carries on the description above it
                    if (description.Length > 0)
                        description.Append(' ');
                    description.Append(line.Trim());
                    continue;
                }

                // anything else at the left margin ("No map name specified") ends the table
                Commit();
                inTable = false;
            }

            Commit();

            if (options.Count == 0)
                return null;

            return new ToolHelp
            {
                Banner = banner,
                Author = author,
                ToolName = tool,
                BuildDate = buildDate,
                Options = options,
            };
        }
    }

    /// <summary>
    /// Runs a compiler with <c>-help</c> and keeps what it said.
    ///
    /// Cached twice: in memory for the session, and on disk keyed by the binary's path, size and
    /// modification time, so a given build of a tool is asked exactly once ever. The probe is cheap -
    /// the tools answer in well under a second and need no game to do it - but it is consulted from
    /// property bindings on the UI thread, so "cheap" has to mean "free after the first time".
    /// </summary>
    public static class ToolHelpProbe
    {
        /// <summary>How long a tool gets to print its help before it is killed and treated as mute.</summary>
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        private static readonly string CacheFile = "./ToolHelpCache.json";

        private sealed class CacheEntry
        {
            public string Path { get; set; } = "";
            public long Length { get; set; }
            public DateTime LastWriteUtc { get; set; }
            /// <summary>Null when the binary was asked and printed no table: a stock tool.</summary>
            public ToolHelp? Help { get; set; }
        }

        private static Dictionary<string, CacheEntry>? entries;
        private static readonly HashSet<string> InFlight = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object Gate = new();

        /// <summary>
        /// Stands in for the real binary in tests, which have no compiler to run. Keyed by path;
        /// a path it does not know falls through to the real probe.
        /// </summary>
        internal static Func<string, ToolHelp?>? Override;

        /// <summary>
        /// Raised on a worker thread when a probe started by <see cref="EnsureProbed"/> has finished
        /// and its answer is in the cache. Whoever listens re-reads the parameter lists on the UI
        /// thread; nothing here touches the UI.
        /// </summary>
        public static event Action? Completed;

        /// <summary>
        /// Whether any probe started by <see cref="EnsureProbed"/> is still running. Lets a listener
        /// wait for the last of a batch rather than rebuilding once per compiler.
        /// </summary>
        public static bool AnyInFlight
        {
            get
            {
                lock (Gate)
                {
                    return InFlight.Count > 0;
                }
            }
        }

        /// <summary>
        /// Under ForceOff no compiler is ever asked anything: the shipped lists are the whole truth,
        /// and "never asks" has to mean never - not "asks, then ignores the answer".
        /// </summary>
        private static bool Disabled =>
            ConfigurationManager.Settings.ToolsPlusPlusMode == Configuration.ToolsPlusPlusMode.ForceOff;

        /// <summary>
        /// The answer already on hand for <paramref name="path"/>, without running anything.
        ///
        /// Returns true when the question is settled - the cache holds an answer, the binary does not
        /// exist, or asking is disabled - and <paramref name="help"/> is that answer (null meaning
        /// "prints no table"). Returns false when the binary has not been asked yet; call
        /// <see cref="EnsureProbed"/> and expect <see cref="Completed"/>.
        /// </summary>
        public static bool TryPeek(string? path, out ToolHelp? help)
        {
            help = null;

            if (string.IsNullOrWhiteSpace(path) || Disabled)
                return true;

            if (Override is { } stub)
            {
                help = stub(path);
                return true;
            }

            if (Identify(path) is not { } info)
                return true;

            lock (Gate)
            {
                entries ??= LoadCache();

                if (entries.TryGetValue(path, out var cached) && Matches(cached, info))
                {
                    help = cached.Help;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Asks the binary at <paramref name="path"/> on a worker thread if it has not been asked
        /// already, and raises <see cref="Completed"/> when the answer is in. Asking the same binary
        /// twice at once is collapsed into one run.
        ///
        /// This is how the UI thread gets its answers: the parameter lists are built while the main
        /// window is being constructed, and four compilers that each get five seconds to answer are
        /// twenty seconds the window would otherwise spend frozen if one of them stalled.
        /// </summary>
        public static void EnsureProbed(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || Disabled || Override is not null)
                return;

            lock (Gate)
            {
                if (!InFlight.Add(path))
                    return;
            }

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    Probe(path);
                }
                finally
                {
                    lock (Gate)
                    {
                        InFlight.Remove(path);
                    }
                }

                try
                {
                    Completed?.Invoke();
                }
                catch (Exception e)
                {
                    CompilePalLogger.LogLineDebug($"A tool help listener failed: {e}");
                }
            });
        }

        /// <summary>
        /// What the binary at <paramref name="path"/> accepts, or null if it does not describe itself.
        /// Runs the binary here and now if it has not been asked before, so this is for callers that
        /// can wait - tests, the settings window's Detect button, a worker thread. The UI uses
        /// <see cref="TryPeek"/> and <see cref="EnsureProbed"/>. Never throws: a tool that cannot be
        /// started is a tool with no help.
        /// </summary>
        public static ToolHelp? Probe(string? path)
        {
            if (TryPeek(path, out var known))
                return known;

            var info = Identify(path!)!;

            lock (Gate)
            {
                entries ??= LoadCache();

                // settled by someone else while this call waited for the lock
                if (entries.TryGetValue(path!, out var cached) && Matches(cached, info))
                    return cached.Help;

                var help = Run(path!);

                entries[path!] = new CacheEntry
                {
                    Path = path!,
                    Length = info.Length,
                    LastWriteUtc = info.LastWriteTimeUtc,
                    Help = help,
                };

                SaveCache(entries);

                CompilePalLogger.LogLineDebug(help is null
                    ? $"\"{path}\" printed no option table for -help; using the shipped parameter list."
                    : $"\"{path}\" reports {help.Options.Count} options as {help.Label}.");

                return help;
            }
        }

        private static FileInfo? Identify(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info : null;
            }
            catch (Exception)
            {
                // a path typed by hand can be anything
                return null;
            }
        }

        private static bool Matches(CacheEntry cached, FileInfo info) =>
            cached.Length == info.Length && cached.LastWriteUtc == info.LastWriteTimeUtc;

        /// <summary>
        /// Drops the in-memory copy. The disk cache is keyed by file identity and stays valid, so this
        /// only matters when a test has swapped the override or a binary was replaced in place.
        /// </summary>
        public static void Invalidate()
        {
            lock (Gate)
            {
                entries = null;
            }
        }

        private static ToolHelp? Run(string path)
        {
            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "-help",
                    WorkingDirectory = Path.GetDirectoryName(path) ?? ".",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                };

                var stopwatch = Stopwatch.StartNew();
                process.Start();

                // Both streams are drained, because a tool that fills its stderr pipe while nobody
                // reads it blocks forever and never gets to exit.
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
                {
                    try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                    CompilePalLogger.LogLineDebug($"\"{path}\" did not answer -help within {Timeout.TotalSeconds}s.");
                    return null;
                }

                string output = stdout.GetAwaiter().GetResult();
                string errors = stderr.GetAwaiter().GetResult();

                CompilePalLogger.LogLineDebug($"Asked \"{path}\" for -help in {stopwatch.ElapsedMilliseconds} ms.");

                return ToolHelpParser.Parse(output) ?? ToolHelpParser.Parse(errors);
            }
            catch (Exception e)
            {
                CompilePalLogger.LogLineDebug($"Could not ask \"{path}\" for -help: {e.Message}");
                return null;
            }
        }

        private static Dictionary<string, CacheEntry> LoadCache()
        {
            try
            {
                if (File.Exists(CacheFile))
                {
                    var list = JsonConvert.DeserializeObject<List<CacheEntry>>(File.ReadAllText(CacheFile));
                    if (list is not null)
                        return list
                            .Where(e => !string.IsNullOrEmpty(e.Path))
                            .GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception e)
            {
                // an unreadable cache is re-made by asking the tools again, which is all it ever held
                CompilePalLogger.LogLineDebug($"Could not read {CacheFile}: {e.Message}");
            }

            return new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        }

        private static void SaveCache(Dictionary<string, CacheEntry> cache)
        {
            try
            {
                ConfigurationManager.WriteFileAtomic(CacheFile,
                    JsonConvert.SerializeObject(cache.Values.ToList(), Formatting.Indented));
            }
            catch (Exception e)
            {
                CompilePalLogger.LogLineDebug($"Could not write {CacheFile}: {e.Message}");
            }
        }
    }
}
