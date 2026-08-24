using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
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

        /// <summary>
        /// Where a summary goes. A compile-time constant, and deliberately not a setting.
        ///
        /// This used to be user-editable, which was a mistake with a name: an endpoint read from
        /// settings.json is an exfiltration primitive. Anything able to write that file - malware,
        /// or a "fixed config" someone shares in a mapping Discord - repoints this client at a host
        /// of its choosing and turns it into a beacon. And it bought the user nothing: nobody
        /// running a map compiler wants to operate a telemetry collector.
        ///
        /// A fork that wants its own endpoint changes this line and rebuilds, which is the correct
        /// amount of friction for the decision.
        /// </summary>
        private static readonly string Endpoint = TelemetryEndpoints.Default;

        /// <summary>
        /// The crash route, derived from the events route rather than injected separately.
        ///
        /// One value is compiled in, so there is one thing to configure and no way for a build to
        /// end up able to report usage but not crashes, or the reverse.
        /// </summary>
        private static readonly string CrashEndpoint =
            Endpoint.EndsWith("/events", StringComparison.OrdinalIgnoreCase)
                ? Endpoint[..^"/events".Length] + "/crash"
                : Endpoint;

        /// <summary>
        /// Whether this build has anywhere to report to at all.
        ///
        /// Empty in any build that did not have the endpoint AND a signing key injected - a local
        /// build, a fork, a clone of the public repository. Those send nothing regardless of the
        /// setting, which is intended rather than a limitation: a fork should not inherit somewhere
        /// to send other people's usage data.
        ///
        /// Both halves are required because the service refuses unsigned submissions. A build with
        /// an endpoint but no key could only ever collect a session's counters and then be given a
        /// 401 for them - so it does not collect at all, which is the honest behaviour and keeps the
        /// settings toggle from claiming something the build cannot do.
        /// </summary>
        internal static bool IsConfigured =>
            Endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && TelemetryEndpoints.SigningKey.Length > 0
            && TelemetryEndpoints.SigningKeyGeneration.Length > 0;

        /// <summary>
        /// Whether the user has asked for reporting. Collection depends on this and nothing else.
        ///
        /// Deliberately separate from <see cref="IsConfigured"/>, which asks a different question -
        /// whether this build has anywhere to send. Gating collection on both made the whole
        /// subsystem inert in a build without an endpoint, which is right for production and wrong
        /// for everything else: the payload's shape stopped being observable in exactly the
        /// configuration CI builds, so the tests that pin what leaves the machine only passed
        /// locally, where Debug injects development values. Tests should not depend on the build
        /// configuration to see the behaviour they assert.
        ///
        /// Counters accumulated by a build that cannot send are held in memory and discarded at
        /// exit, which costs a few integers and keeps the two questions honest.
        /// </summary>
        private static bool CollectionEnabled =>
            ConfigurationManager.Settings.TelemetryEnabled
            && !System.Diagnostics.Debugger.IsAttached;

        /// <summary>Whether a submission may actually be sent: opted in, and somewhere to send it.</summary>
        private static bool Enabled => CollectionEnabled && IsConfigured;

        private static void Count(string metric, long amount = 1)
        {
            // Checked here rather than at each call site, so the accounting lives in one place.
            // Nothing leaves on collection alone - FlushAsync checks Enabled, which additionally
            // requires this build to have somewhere to send.
            if (!CollectionEnabled) return;

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
            if (!CollectionEnabled) return;

            var name = game?.Trim() ?? "";
            games.TryAdd(KnownGames.Contains(name) ? name : "other", 0);
        }

        /// <summary>
        /// Signs the submission, when this build was given a key.
        ///
        /// v1 = hex HMAC-SHA256(releaseKey, "&lt;nonce&gt;.&lt;unix seconds&gt;.&lt;body&gt;"). The timestamp is
        /// inside the MAC so it cannot be edited to extend a captured request's life, and the
        /// service refuses anything outside its skew window; the nonce lets it reject a resent
        /// capture without rejecting a second genuine submission that happens to be identical.
        ///
        /// The key is specific to this release - the workflow derives it from a root secret and the
        /// version - so the service reproduces it from the version inside the signed body. Two
        /// consequences worth knowing: shipping a release needs no server-side change, and a key
        /// lifted out of one build can only ever sign as that version.
        ///
        /// Worth being plain about what this is for. The key ships inside a desktop application
        /// handed to the public, so it is a published key to anyone who cares to look - this is not
        /// authentication and does not make a submission trustworthy. It lets the service tell our
        /// releases from scripted traffic, and lets one release be revoked server-side. A build
        /// with no key still reports: the service never refuses a submission for being unsigned.
        /// </summary>
        private static void Sign(StringContent content, string json)
        {
            // IsConfigured already guarantees both are present - Flush returns before reaching
            // here otherwise. Checked again rather than assumed, because an unsigned request is
            // refused and a silent 401 per session is a hard failure to notice.
            if (string.IsNullOrEmpty(TelemetryEndpoints.SigningKey)
                || string.IsNullOrEmpty(TelemetryEndpoints.SigningKeyGeneration))
            {
                CompilePalLogger.LogLineDebug("Telemetry: no signing key in this build, not sending.");
                return;
            }

            try
            {
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                /*
                 * A fresh random value per submission, inside the MAC.
                 *
                 * Not an identifier: it is generated at send time, never stored, and never reused,
                 * so it says nothing about this install. It exists so the service can reject a
                 * captured submission that is resent without also rejecting a second genuine one
                 * that happens to be byte-identical - two installs on the same Windows build and
                 * app version, each reporting a single session, produce exactly that.
                 */
                var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(TelemetryEndpoints.SigningKey));
                var mac = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{nonce}.{timestamp}.{json}"));

                content.Headers.TryAddWithoutValidation("X-CP-Key-Gen", TelemetryEndpoints.SigningKeyGeneration);
                content.Headers.TryAddWithoutValidation("X-CP-Nonce", nonce);
                content.Headers.TryAddWithoutValidation("X-CP-Timestamp", timestamp.ToString());
                content.Headers.TryAddWithoutValidation("X-CP-Signature", "v1=" + Convert.ToHexString(mac).ToLowerInvariant());
            }
            catch (Exception e)
            {
                // Nothing about the key is logged. The send goes ahead and will be refused, which
                // is preferable to silently dropping it: a 401 in the debug log is a signal that
                // something is wrong with the build, and an omission is not.
                CompilePalLogger.LogLineDebug($"Could not sign the telemetry submission: {e.Message}");
            }
        }

        /// <summary>
        /// Whether this build can report at all, for the settings window to say so plainly.
        ///
        /// False in anything built without an endpoint and signing key - a local build, a fork, a
        /// clone of the public repository. Those collect while the user has the setting on and
        /// then discard it, so telling them the toggle does nothing here is the honest thing to
        /// show rather than letting the switch imply otherwise.
        /// </summary>
        public static bool CanReport => IsConfigured;

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
        ///
        /// Reports the payload whenever the user has opted in, whether or not this particular
        /// build has an endpoint - see <see cref="CanReport"/> for that half, which the settings
        /// window states separately. Showing the shape either way is the honest answer to "what
        /// would you send", and it keeps this observable in every build configuration.
        /// </summary>
        public static string DescribePayload()
        {
            if (!CollectionEnabled)
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
        /// <summary>
        /// Sends one crash report, because the user asked for it to be sent.
        ///
        /// Deliberately separate from FlushAsync and deliberately NOT gated on the usage reporting
        /// setting. The two are different questions and both are asked: usage reporting is a
        /// standing preference about routine counters, while this is a one-off answer to a dialog
        /// showing the exact text about to leave the machine. Someone who leaves usage reporting
        /// off has not refused to report a crash they were shown and chose to send, and someone who
        /// left it on has not agreed in advance to send a stack trace.
        ///
        /// It still requires a configured build. A fork with no endpoint sends nothing, and the
        /// dialog hides the send button rather than offering one that does nothing.
        /// </summary>
        public static async Task SendCrashAsync(CompilePalX.Crash.CrashReport report)
        {
            if (!IsConfigured) return;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                /*
                 * The version is normalised by the service against a closed set, and the crash
                 * route derives this release's signing key from it - the same arrangement as the
                 * usage endpoint, so a release that can report usage can report a crash.
                 */
                var payload = new
                {
                    app = "compilepal",
                    version = BuildInfo.Version,
                    os = report.OsVersion,
                    runtime = report.Runtime,
                    fatal = report.Fatal,
                    kind = report.Kind,
                    message = report.Message,
                    stack = report.Stack,
                };

                var json = JsonConvert.SerializeObject(payload);

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                Sign(content, json);

                var response = await http.Value
                    .PostAsync(CrashEndpoint, content, cts.Token)
                    .ConfigureAwait(false);

                CompilePalLogger.LogLineDebug($"Crash report: {(int)response.StatusCode}");
            }
            catch (Exception e)
            {
                // The user asked for this to be sent and it could not be. Worth a line in the debug
                // log, but not worth a second dialog on top of the crash one.
                CompilePalLogger.LogLineDebug($"Crash report send failed: {e.Message}");
            }
        }

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

            try
            {
                using var cts = new CancellationTokenSource(timeout);
                // Serialised once and kept: the signature has to cover the exact bytes sent, so
                // signing a second serialisation would produce a MAC over different text.
                var json = JsonConvert.SerializeObject(payload);

                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                Sign(content, json);

                /*
                 * ConfigureAwait(false), because this is called from the window's closing path.
                 *
                 * Without it the continuation is posted back to whatever SynchronizationContext
                 * was current when the await was reached - the WPF dispatcher - and any caller
                 * that blocks that thread waiting for this task deadlocks. MainWindow no longer
                 * blocks the UI thread here, but a method whose correctness depends on every
                 * caller knowing that is a method waiting to be called wrongly.
                 */
                var response = await http.Value
                    .PostAsync(Endpoint, content, cts.Token)
                    .ConfigureAwait(false);

                CompilePalLogger.LogLineDebug($"Telemetry: {(int)response.StatusCode}");
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
