using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CompilePalX.Compiling;
using CompilePalX.Configuration;
using Segment;
using Segment.Model;

namespace CompilePalX
{
    /// <summary>
    /// Optional usage telemetry. Off unless the user turns it on.
    ///
    /// This used to be on for everyone with no way to refuse, sending to two hardcoded Segment write
    /// keys - one of which the code itself described as belonging to an account nobody involved could
    /// identify. That is not something this fork can ship: the data went to a third party the user was
    /// never told about, from a build they did not get from that third party, and no setting anywhere
    /// stopped it. It now sends nothing at all until <see cref="Settings.AnalyticsEnabled"/> is set,
    /// and only ever to the single endpoint named in settings.
    ///
    /// The identifier changed too. It was a SHA256 of the machine name, every CPU's processor ID and
    /// the C: volume serial - a hardware fingerprint, stable across reinstalls and across any other
    /// program computing it the same way, and salted with a constant sitting in open source, so anyone
    /// holding a guess at those three values could confirm it against a captured ID. It is now a random
    /// GUID generated on first use and kept in the settings file, which counts installs just as well
    /// and identifies nothing.
    /// </summary>
    static class AnalyticsManager
    {
        private static Options? options;
        private static Client? client;

        /// <summary>
        /// Set false to suppress reporting for the rest of the session regardless of the setting -
        /// used when initialisation fails, so a broken client is not retried on every event.
        /// </summary>
        private static bool available = true;

        /// <summary>
        /// Whether anything may be sent right now.
        ///
        /// Read live rather than captured at startup, so turning the setting off in the settings window
        /// takes effect immediately instead of at the next launch.
        /// </summary>
        private static bool Enabled =>
            available
            && ConfigurationManager.Settings.AnalyticsEnabled
            && !System.Diagnostics.Debugger.IsAttached;

        /// <summary>
        /// Builds the client on first use rather than in a static constructor, because "the user has
        /// consented" is not knowable until settings have loaded.
        /// </summary>
        private static Client? Instance()
        {
            if (!Enabled)
                return null;

            if (client is not null)
                return client;

            string writeKey = ConfigurationManager.Settings.AnalyticsWriteKey;
            if (string.IsNullOrWhiteSpace(writeKey))
            {
                // Consent without a destination. Nothing to do, and nothing to warn about - a build
                // that ships no key of its own is the expected state for a fork with no backend.
                available = false;
                return null;
            }

            try
            {
                OperatingSystem os = Environment.OSVersion;
                options = new Options
                {
                    Context =
                    {
                        ["direct"] = true,
                        ["os"] = new Dict
                        {
                            ["name"] = os.Platform.ToString(),
                            ["version"] = $"{os.Version.Major}.{os.Version.Minor}.{os.Version.Build}",
                        },
                        ["app"] = new Dict
                        {
                            ["name"] = "CompilePal",
                            ["version"] = UpdateManager.CurrentVersion,
                        },
                    },
                };

                var config = new Config();

                // Points at whatever collector the user configured. Blank means Segment's own. Anything
                // implementing the same batch API - a self-hosted RudderStack or Jitsu, or a handful of
                // lines of your own - works here without touching this file.
                string host = ConfigurationManager.Settings.AnalyticsHost;
                if (!string.IsNullOrWhiteSpace(host))
                    config.SetHost(host);

                client = new Client(writeKey, config);
                return client;
            }
            catch (Exception e)
            {
                // Telemetry must never be the reason the app misbehaves.
                CompilePalLogger.LogLineDebug($"Analytics disabled, failed to initialise: {e.Message}");
                available = false;
                return null;
            }
        }

        /// <summary>
        /// A random per-install identifier, created on first use and kept in the settings file.
        ///
        /// Deleting the settings file, or pressing the reset in the settings window, produces a new one -
        /// which is the point: the user can start over, which a hardware fingerprint never allowed.
        /// </summary>
        private static readonly object installIdLock = new();

        private static string InstallId()
        {
            // Locked: CompileError is reported from the compile thread, so the first event of a session
            // is not reliably on the UI thread and two of them could otherwise mint two different IDs.
            lock (installIdLock)
            {
                var settings = ConfigurationManager.Settings;

                if (string.IsNullOrWhiteSpace(settings.AnalyticsInstallId))
                    settings.AnalyticsInstallId = Guid.NewGuid().ToString("N");

                // Set on the settings object and left for the ordinary save on shutdown to persist,
                // rather than calling SaveSettings here. That would raise OnSettingsSaved - which open
                // windows handle by touching their own controls - from whichever thread happened to
                // report the event. Losing the ID to a crash before the next save just counts one extra
                // install, which is not worth marshalling a settings write for.
                return settings.AnalyticsInstallId!;
            }
        }

        public static void Launch() => Track("Launch");
        public static void Compile() => Track("Compile");
        public static void NewPreset() => Track("NewPreset");
        public static void ModifyPreset() => Track("ModifyPreset");
        public static void Error() => Track("Error");
        public static void CompileError() => Track("CompileError");

        // The game name is whatever the user typed into the game configuration window - a free text box,
        // not a fixed list - so it is reported only when it matches a game Compile Pal actually knows
        // about. Sending it verbatim meant an arbitrary user-authored string, which is exactly the kind
        // of field that ends up holding a path, a nickname or worse.
        public static void NewGameConfiguration(string game) => Track("NewGameConfiguration", GameProperty(game));
        public static void ModifyGameConfiguration(string game) => Track("ModifyGameConfiguration", GameProperty(game));
        public static void SelectGameConfiguration(string game) => Track("SelectGameConfiguration", GameProperty(game));

        /// <summary>The Source games Compile Pal ships configurations for. Anything else reports "other".</summary>
        private static readonly HashSet<string> KnownGames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Counter-Strike: Source", "Counter-Strike: Global Offensive", "Counter-Strike 2",
            "Team Fortress 2", "Half-Life 2", "Half-Life 2: Deathmatch", "Half-Life 2: Episode One",
            "Half-Life 2: Episode Two", "Portal", "Portal 2", "Garry's Mod", "Day of Defeat: Source",
            "Left 4 Dead", "Left 4 Dead 2", "Black Mesa", "Alien Swarm", "Insurgency",
        };

        private static Dictionary<string, object> GameProperty(string game) =>
            new() { ["game"] = KnownGames.Contains(game?.Trim() ?? "") ? game!.Trim() : "other" };

        private static void Track(string eventName, IDictionary<string, object>? additionalProperties = null)
        {
            var c = Instance();
            if (c is null)
                return;

            try
            {
                // Fully qualified: CompilePalX.Properties is a namespace in this assembly and shadows it.
                var properties = new Segment.Model.Properties();

                if (additionalProperties is not null)
                {
                    foreach (var item in additionalProperties)
                        properties[item.Key] = item.Value;
                }

                c.Track(InstallId(), eventName, properties, options);
            }
            catch (Exception e)
            {
                CompilePalLogger.LogLineDebug($"Failed to report '{eventName}': {e.Message}");
            }
        }
    }
}
