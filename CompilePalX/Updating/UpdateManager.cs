using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CompilePalX.Compiling;

namespace CompilePalX
{
    internal delegate void UpdateFound();
    static class UpdateManager
    {
        public static event UpdateFound OnUpdateFound;

        private static Version currentVersion;
        public static string CurrentVersion => currentVersion.ToString(isPrerelease ? 2 : 1);

        private static Version latestVersion;
        public static string LatestVersion => latestVersion.ToString(isPrerelease ? 2 : 1);

        /// <summary>
        /// The repository releases are published to, and which the update check reads version.txt from.
        ///
        /// This is a fork. Pointed at ruarai/CompilePal - which is what it was - the check compares this
        /// build against *upstream's* version file, so every upstream release makes this fork announce
        /// itself as out of date and sends the user to a download that is not this program. Both the
        /// version lookup and the "get the update" link have to follow whoever publishes the build.
        /// </summary>
        private const string Repository = "catualus/CompilePal";

        /// <summary>Branch the version files are read from. Updated by the release workflow.</summary>
        private const string VersionBranch = "master";

        private const string LatestVersionURL = $"https://raw.githubusercontent.com/{Repository}/{VersionBranch}/CompilePalX/version.txt";
        private const string LatestPrereleaseVersionURL = $"https://raw.githubusercontent.com/{Repository}/{VersionBranch}/CompilePalX/version_prerelease.txt";

        private static string MajorUpdateURL = $"https://github.com/{Repository}/releases/latest";
        // Tags must be in form: v0major.minor
        private static string PrereleaseUpdateURL => $"https://github.com/{Repository}/releases/tag/v0{LatestVersion}";

        /// <summary>
        /// Falls back to the releases page whenever a specific prerelease tag cannot be named. Building
        /// the prerelease URL dereferences latestVersion, which is null until a check has succeeded -
        /// so anything that reached for this link before then (or after a failed check) threw.
        /// </summary>
        public static Uri UpdateURL =>
            new Uri(isPrerelease && latestVersion is not null ? PrereleaseUpdateURL : MajorUpdateURL);

        private static bool isPrerelease = false;

        static UpdateManager()
        {
            // Trimmed. The files are written with a trailing newline by the release workflow, and
            // while int.Parse happens to tolerate the whitespace either side of a '.', relying on that
            // makes the whole static constructor - and so CompilePalLogger, which asks it for the
            // version on first use - one formatting change away from taking the app down at startup.
            string currentVersionString = GetValidVersionString(File.ReadAllText("./version.txt").Trim());
            string currentPrereleaseVersionString = GetValidVersionString(File.ReadAllText("./version_prerelease.txt").Trim() + ".0.0");

            currentVersion = Version.Parse(currentVersionString);
            Version currentPrereleaseVersion = Version.Parse(currentPrereleaseVersionString);

            if (currentPrereleaseVersion > currentVersion)
            {
	            currentVersion = currentPrereleaseVersion;
                isPrerelease = true;
            }

            CompilePalLogger.LogDebug($"Current version: {currentVersion}\n");

            // store version info in registry
            RegistryManager.Write("Version", currentVersionString);
            RegistryManager.Write("PrereleaseVersion", currentPrereleaseVersionString);
        }

        public static void CheckVersion()
        {
            // Background: nothing waits on this and an update check must not keep the process alive
            // after the window has closed.
            Thread updaterThread = new Thread(ThreadedCheck) { IsBackground = true };
            updaterThread.Start();
        }

        static async void ThreadedCheck()
        {
            try
            {
                CompilePalLogger.LogLine("Fetching update information...");

                using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var version = c.GetStringAsync(new Uri(isPrerelease ? LatestPrereleaseVersionURL : LatestVersionURL));

                // Trimmed: the version files end with a newline, and Version.Parse does not accept one.
                string newVersion = GetValidVersionString((await version).Trim());

                latestVersion = Version.Parse(newVersion);

                if (currentVersion < latestVersion)
                {
                    MainWindow.ActiveDispatcher.Invoke(OnUpdateFound);

                    CompilePalLogger.LogLine("Updater found that Compile Pal is outdated.");
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
                // anything it does not catch is rethrown on the thread pool and takes the process down
                // with it - and a request timeout (TaskCanceledException) or an unparseable version file
                // are both entirely ordinary ways for an update check to fail.
                CompilePalLogger.LogLine("Failed to find update information as an error was returned:");
                CompilePalLogger.LogLine(e.Message);
                CompilePalLogger.LogLineDebug(e.ToString());
            }
        }

        private static string GetValidVersionString(string str)
        {
            // Ensures string is always in format: major.minor.build.revision
            return str + string.Concat(Enumerable.Repeat(".0", 3 - str.Count(s => s == '.')));
        }
    }
}
