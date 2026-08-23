using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CompilePalX.Compiling;
using CompilePalX.Configuration;
using Newtonsoft.Json;

namespace CompilePalX
{
    /// <summary>
    /// Opt-in usage reporting. Off unless the user turns it on, and silent when it is off.
    ///
    /// This replaces the Segment SDK the app used to carry, and the change is not only about
    /// which host receives the data. A third-party analytics SDK decides for itself what to
    /// attach to an event, when to send, and what to keep on disk between runs - none of which
    /// is visible from the call sites here, and none of which can be honestly described to a
    /// user in a settings window. What is left is a single POST of a JSON object small enough
    /// to print in full, which is what <see cref="DescribePayload"/> does.
    ///
    /// Three properties are deliberate and worth not undoing:
    ///
    ///   No identifier. Nothing in the payload distinguishes one install from another - no
    ///   GUID, no fingerprint, no machine name. The server counts distinct installs by
    ///   bucketing the connection address under a salt it rotates and discards every day, so
    ///   even it cannot link today's count to yesterday's.
    ///
    ///   One submission per session. Counters accumulate in memory and go out once, as the
    ///   app closes. A stream of individual events would carry timestamps, and a timestamped
    ///   event stream describes when someone sits down to work and for how long. The totals
    ///   are the same either way; the working-hours pattern is not.
    ///
    ///   Nothing is queued to disk. A failed send is dropped, never retried. A spool file of
    ///   pending telemetry is a privacy liability sitting in the install directory, and the
    ///   data is not worth it.
    /// </summary>
    static class TelemetryManager
    {
        /// <summary>Counter names the server accepts. Anything else it discards, so nothing else is sent.</summary>
        private static class Metric
        {
            public const string Sessions = "sessions";
            public const string Compiles = "compiles";
            public const string CompilesOk = "compiles_ok";
            public const string CompilesFailed = "compiles_failed";
            public const string CompilesCancelled = "compiles_cancel";
            public const string Errors = "errors";
            public const string PresetsNew = "presets_new";
            public const string PresetsModified = "presets_modified";
            public const string Crashes = "crashes";
        }

        private static readonly ConcurrentDictionary<string, long> counters = new();

        /// <summary>
        /// Games seen this session, as a set.
        ///
        /// The game name in Compile Pal is an editable text box, so only names the app itself
        /// ships a configuration for are ever sent; anything else becomes "other". Someone who
        /// renames a configuration after themselves, or pastes a path into it, must not have
        /// that leave their machine.
        /// </summary>
        private static readonly ConcurrentDictionary<string, byte> games = new();

        /// <summary>Games Compile Pal ships a configuration for. Kept in step with the server's list.</summary>
        private static readonly HashSet<string> KnownGames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Counter-Strike: Source", "Counter-Strike: Global Offensive", "Counter-Strike 2",
            "Team Fortress 2", "Half-Life 2", "Half-Life 2: Deathmatch", "Half-Life 2: Episode One",
            "Half-Life 2: Episode Two", "Portal", "Portal 2", "Garry's Mod", "Day of Defeat: Source",
            "Left 4 Dead", "Left 4 Dead 2", "Black Mesa", "Alien Swarm", "Insurgency",
        };

        /// <summary>
        /// Shared, and created once. A fresh HttpClient per send strands a socket each time one
        /// is collected - which would barely matter for a once-per-session call, but there is no
        /// reason to get it wrong.
        /// </summary>
        private static readonly Lazy<HttpClient> http = new(() =>
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", $"CompilePal/{UpdateManager.CurrentVersion}");

            return client;
        });

        private static bool Enabled =>
            ConfigurationManager.Settings.TelemetryEnabled
            && !string.IsNullOrWhiteSpace(ConfigurationManager.Settings.TelemetryEndpoint)
            && !System.Diagnostics.Debugger.IsAttached;

        private static void Count(string metric, long amount = 1)
        {
            // Recorded even while disabled costs nothing, and checking here rather than at each
            // call site keeps the accounting in one place. Nothing leaves without Enabled, which
            // Flush checks again immediately before sending.
            if (!Enabled) return;

            counters.AddOrUpdate(metric, amount, (_, existing) => existing + amount);
        }

        public static void Launch() => Count(Metric.Sessions);
        public static void Compile() => Count(Metric.Compiles);
        public static void CompileSucceeded() => Count(Metric.CompilesOk);
        public static void CompileFailed() => Count(Metric.CompilesFailed);
        public static void CompileCancelled() => Count(Metric.CompilesCancelled);
        public static void CompileError() => Count(Metric.Errors);
        public static void NewPreset() => Count(Metric.PresetsNew);
        public static void ModifyPreset() => Count(Metric.PresetsModified);

        /// <summary>A crash happened. A count, never a message, a stack, or a file path.</summary>
        public static void Error() => Count(Metric.Crashes);

        public static void SelectGameConfiguration(string game) => NoteGame(game);
        public static void NewGameConfiguration(string game) => NoteGame(game);
        public static void ModifyGameConfiguration(string game) => NoteGame(game);

        private static void NoteGame(string? game)
        {
            if (!Enabled) return;

            var name = game?.Trim() ?? "";
            games.TryAdd(KnownGames.Contains(name) ? name : "other", 0);
        }

        /// <summary>
        /// Throws away everything collected so far without sending it.
        ///
        /// Called when the user switches reporting off. Flushing already refuses to send while
        /// disabled, so nothing would have left either way - but leaving a session's worth of
        /// counters sitting in memory after someone has just declined to share them is the wrong
        /// answer to have given. Switching off means there is nothing held.
        /// </summary>
        public static void Discard()
        {
            counters.Clear();
            games.Clear();
        }

        /// <summary>
        /// Exactly what would be sent right now, formatted for a human to read.
        ///
        /// The settings window shows this so "we send anonymous usage counts" is something the
        /// user can check rather than take on faith. It is generated from the same state the
        /// send uses, so it cannot drift from the truth the way a hand-written description would.
        /// </summary>
        public static string DescribePayload()
        {
            if (!Enabled)
                return "Nothing is sent while usage reporting is off.";

            return JsonConvert.SerializeObject(BuildPayload(), Formatting.Indented);
        }

        /// <summary>
        /// The entire wire format. Nine optional counters, two coarse version strings and a
        /// short list of game names - and deliberately nothing else, so the class definition
        /// itself is the documentation of what leaves the machine.
        /// </summary>
        private sealed class Payload
        {
            [JsonProperty("app")]
            public string App { get; init; } = "compilepal";

            [JsonProperty("version")]
            public string Version { get; init; } = "";

            [JsonProperty("os")]
            public string Os { get; init; } = "";

            [JsonProperty("counts")]
            public Dictionary<string, long> Counts { get; init; } = new();

            [JsonProperty("games")]
            public string[] Games { get; init; } = [];

            [JsonIgnore]
            public bool IsEmpty => Counts.Count == 0 && Games.Length == 0;
        }

        private static Payload BuildPayload()
        {
            var os = Environment.OSVersion.Version;

            return new Payload
            {
                Version = UpdateManager.CurrentVersion,

                // Major.minor.build only. The revision is granular enough to narrow a machine
                // down within a small population and says nothing a build number does not.
                Os = $"{os.Major}.{os.Minor}.{os.Build}",

                Counts = counters.ToArray().ToDictionary(kv => kv.Key, kv => kv.Value),
                Games = games.Keys.ToArray(),
            };
        }

        /// <summary>
        /// Sends this session's totals, once, and clears them.
        ///
        /// Called as the window closes. Bounded by <paramref name="timeout"/> because it runs on
        /// the shutdown path: a slow or unreachable endpoint must delay closing the app by a
        /// visible moment at most, and dropping the submission is always preferable to hanging.
        /// </summary>
        public static async Task FlushAsync(TimeSpan timeout)
        {
            if (!Enabled) return;

            var payload = BuildPayload();

            // Cleared before the send, not after. A failure must not leave the counters in place
            // to be sent again by a later flush - the totals would be double-counted, and there
            // is no retry by design.
            counters.Clear();
            games.Clear();

            if (payload.IsEmpty)
                return;

            var endpoint = ConfigurationManager.Settings.TelemetryEndpoint!.Trim();

            // Plain HTTP would put the payload, and the fact that this machine runs Compile Pal,
            // in front of every hop between here and the server. Refused rather than downgraded.
            if (!endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                CompilePalLogger.LogLineDebug($"Telemetry endpoint is not https, not sending: {endpoint}");
                return;
            }

            try
            {
                using var cts = new CancellationTokenSource(timeout);
                using var content = new StringContent(
                    JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

                var response = await http.Value.PostAsync(endpoint, content, cts.Token);

                CompilePalLogger.LogLineDebug($"Telemetry: {(int)response.StatusCode} from {endpoint}");
            }
            catch (Exception e)
            {
                // Every failure path ends here and none of them matter. Usage reporting is the
                // least important thing the app does, and it must never delay a shutdown, raise
                // a dialog, or reach the visible compile output.
                CompilePalLogger.LogLineDebug($"Telemetry send failed: {e.Message}");
            }
        }
    }
}
