using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CompilePalX.Compiling;

namespace CompilePalX
{
    internal delegate void UpdateFound();

    /// <summary>
    /// Checks whether a newer build of this fork exists.
    ///
    /// Versions are <see cref="SemanticVersion"/>, not the scheme inherited from upstream. That
    /// one wrote a zero-padded integer for stable releases ("029") and used the minor component
    /// as a prerelease counter ("029.1"), which left no minor version to use for anything and
    /// made every prerelease sort *above* the stable release of the same line. It also meant this
    /// fork's numbers were indistinguishable from upstream's, so "which 030 are you running" had
    /// no answer.
    /// </summary>
    static class UpdateManager
    {
        public static event UpdateFound OnUpdateFound;

        /// <summary>
        /// The repository releases are published to, and which the update check reads from.
        ///
        /// This is a fork. Pointed at upstream - which is what it was - the check compares this
        /// build against *their* version file, so every upstream release makes this fork announce
        /// itself as out of date and sends the user to a download that is not this program.
        /// </summary>
        private const string Repository = "catualus/CompilePal";

        /// <summary>Branch the channel pointers are read from. Updated by the release workflow.</summary>
        private const string VersionBranch = "master";

        /*
         * Two pointers, one per channel, rather than one file meaning several things.
         *
         * Each holds the newest version published on that channel. They are fetched from the raw
         * file CDN rather than the releases API deliberately: the API rate limits to 60 requests
         * an hour per address, which several users behind one connection would exhaust, and an
         * update check that starts failing for a whole office is worse than no update check.
         */
        private const string StableVersionURL =
            $"https://raw.githubusercontent.com/{Repository}/{VersionBranch}/CompilePalX/version.txt";

        private const string PrereleaseVersionURL =
            $"https://raw.githubusercontent.com/{Repository}/{VersionBranch}/CompilePalX/version_prerelease.txt";

        /// <summary>
        /// What this build reports as its own version, compiled in by the release workflow.
        ///
        /// Empty in anything not built by that workflow, which is reported as a development build
        /// rather than being made to impersonate a release.
        /// </summary>
        public static SemanticVersion? Current { get; } =
            SemanticVersion.TryParse(BuildInfo.Version, out var parsed) ? parsed : null;

        public static string CurrentVersion => Current?.ToString() ?? "dev";

        /// <summary>Whether this build is a prerelease, and so which channel it follows.</summary>
        public static bool IsPreRelease => Current?.IsPreRelease ?? false;

        private static SemanticVersion? latest;

        public static string LatestVersion => latest?.ToString() ?? CurrentVersion;

        /// <summary>
        /// Where to send someone who wants the update.
        ///
        /// A prerelease is only reachable by its tag; the releases page shows the latest stable.
        /// Falls back to the releases page whenever a specific tag cannot be named, which was
        /// previously a null dereference waiting for anything that read this before a check had
        /// succeeded.
        /// </summary>
        public static Uri UpdateURL =>
            new(latest is { IsPreRelease: true }
                ? $"https://github.com/{Repository}/releases/tag/v{latest}"
                : $"https://github.com/{Repository}/releases/latest");

        static UpdateManager()
        {
            CompilePalLogger.LogDebug($"Current version: {CurrentVersion}\n");

            RegistryManager.Write("Version", CurrentVersion);
        }

        public static void CheckVersion()
        {
            // Background: nothing waits on this, and an update check must not keep the process
            // alive after the window has closed.
            new Thread(ThreadedCheck) { IsBackground = true }.Start();
        }

        static async void ThreadedCheck()
        {
            try
            {
                if (Current is null)
                {
                    // A build with no version cannot meaningfully be compared against a release.
                    CompilePalLogger.LogLineDebug("Development build, skipping the update check.");
                    return;
                }

                CompilePalLogger.LogLine("Fetching update information...");

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

                /*
                 * A prerelease build checks the prerelease pointer, a stable build the stable one.
                 *
                 * A prerelease also checks stable, because SemVer puts 1.2.0 above 1.2.0-rc.1 -
                 * so someone on a release candidate should be told when the finished release
                 * arrives. Under the old scheme that could not happen: 029.1 outranked 029, so a
                 * prerelease user was never offered the stable build of their own line.
                 */
                var candidates = IsPreRelease
                    ? new[] { PrereleaseVersionURL, StableVersionURL }
                    : new[] { StableVersionURL };

                SemanticVersion? newest = null;

                foreach (var url in candidates)
                {
                    var text = (await client.GetStringAsync(url)).Trim();

                    if (!SemanticVersion.TryParse(text, out var candidate) || candidate is null)
                    {
                        CompilePalLogger.LogLineDebug($"Ignoring unparseable version '{text}' from {url}");
                        continue;
                    }

                    if (newest is null || candidate > newest)
                        newest = candidate;
                }

                if (newest is null)
                {
                    CompilePalLogger.LogLine("Could not read any published version.");
                    return;
                }

                latest = newest;

                if (newest > Current)
                {
                    MainWindow.ActiveDispatcher.Invoke(OnUpdateFound);
                    CompilePalLogger.LogLine($"Updater found that Compile Pal is outdated. Latest is {newest}.");
                }
                else
                {
                    CompilePalLogger.LogLine("Updater found that Compile Pal is up to date.");
                }

                ProgressManager.SetProgress(ProgressManager.Progress);
            }
            catch (Exception e)
            {
                // Every exception, not just HttpRequestException. This is an async void method, so
                // anything it does not catch is rethrown on the thread pool and takes the process
                // down with it - and a request timeout is an entirely ordinary way for an update
                // check to fail.
                CompilePalLogger.LogLine("Failed to find update information as an error was returned:");
                CompilePalLogger.LogLine(e.Message);
                CompilePalLogger.LogLineDebug(e.ToString());
            }
        }
    }
}
