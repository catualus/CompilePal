using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CompilePalX.Crash
{
    /// <summary>
    /// A crash, reduced to what is useful for fixing it and nothing else.
    ///
    /// The whole report is built here, as text, so that the dialog can show the user the exact
    /// bytes that would be sent. That is the same principle as the usage reporting settings: a
    /// report the user cannot read before sending is a report they cannot consent to.
    ///
    /// Redaction is not decoration. The crash this class was written for carried the message
    ///
    ///     Access to the path 'C:\Users\&lt;name&gt;\Downloads\Compile Pal 1.0.1\Parameters\...' is denied.
    ///
    /// which names the person running it. Exception messages routinely contain paths, and paths on
    /// Windows routinely contain the account name, so anything leaving the machine goes through
    /// <see cref="Redact"/> first.
    /// </summary>
    public sealed record CrashReport(
        string Kind,
        string Message,
        string Stack,
        string AppVersion,
        string OsVersion,
        string Runtime,
        DateTimeOffset When,
        bool Fatal)
    {
        /// <summary>
        /// Builds a report from an exception, redacting as it goes.
        ///
        /// Inner exceptions are followed, because the useful one is usually the innermost:
        /// a TypeInitializationException tells you nothing, the thing that failed inside the static
        /// constructor tells you everything.
        /// </summary>
        public static CrashReport From(Exception e, bool fatal)
        {
            var chain = new List<Exception>();
            for (Exception? current = e; current is not null && chain.Count < 8; current = current.InnerException)
                chain.Add(current);

            var message = new StringBuilder();
            foreach (var link in chain)
            {
                if (message.Length > 0) message.Append(" -> ");
                message.Append(Redact(link.Message));
            }

            return new CrashReport(
                Kind: e.GetType().FullName ?? "Exception",
                Message: Truncate(message.ToString(), MaxMessage),
                Stack: Truncate(Redact(BuildStack(chain)), MaxStack),
                AppVersion: SafeVersion(),
                OsVersion: Environment.OSVersion.Version.ToString(),
                Runtime: System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                When: DateTimeOffset.UtcNow,
                Fatal: fatal);
        }

        /// <summary>Bounds, so a pathological exception cannot produce a megabyte of report.</summary>
        private const int MaxMessage = 1_000;
        private const int MaxStack = 12_000;

        private static string SafeVersion()
        {
            // The version comes from a static this crash may itself have broken.
            try { return UpdateManager.CurrentVersion; }
            catch { return "unknown"; }
        }

        private static string BuildStack(IReadOnlyList<Exception> chain)
        {
            var stack = new StringBuilder();

            foreach (var link in chain)
            {
                if (stack.Length > 0) stack.Append("--- inner exception ---\n");

                stack.Append(link.GetType().FullName).Append(": ").Append(link.Message).Append('\n');
                if (link.StackTrace is { } trace) stack.Append(trace).Append('\n');
            }

            return stack.ToString();
        }

        /*
         * Patterns are ordered longest-context-first: the user profile directory is replaced before
         * the bare account name, so "C:\Users\bob\..." becomes "%USERPROFILE%\..." rather than
         * "C:\Users\%USER%\...".
         */

        /// <summary>Any Windows path, whether or not it belongs to the user.</summary>
        private static readonly Regex AnyPath =
            new(@"[A-Za-z]:\\[^""'<>|\r\n]*", RegexOptions.Compiled);

        /// <summary>
        /// Replaces anything that identifies the machine or its owner.
        ///
        /// Deliberately blunt. A stack trace from a release build carries the BUILD machine's paths
        /// (D:\a\CompilePal\...) which are harmless and useful, while messages carry the user's,
        /// which are neither - so the specific paths are replaced first and anything else that still
        /// looks like a local path is reduced to its file name.
        /// </summary>
        public static string Redact(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            string result = text;

            // The specific, well-known locations first, so they keep a meaningful label.
            foreach (var (value, placeholder) in KnownLocations())
            {
                if (string.IsNullOrWhiteSpace(value) || value.Length < 4) continue;

                result = result.Replace(value, placeholder, StringComparison.OrdinalIgnoreCase);
            }

            /*
             * Then anything left that is still a rooted local path. Keeping the file name is the
             * point: "Parameters\CUBEMAPS\parameters.json" is what makes a crash diagnosable, and
             * it says nothing about who was running it.
             */
            result = AnyPath.Replace(result, match =>
            {
                string path = match.Value.TrimEnd('.', ',', ')', ';', '\'', '"');
                string tail = LastTwoSegments(path);
                string trailing = match.Value[path.Length..];

                return tail.Length > 0 ? $"...\\{tail}{trailing}" : $"...{trailing}";
            });

            // A bare account name can still appear without a path around it.
            string user = SafeGet(() => Environment.UserName);
            if (user.Length >= 3)
                result = result.Replace(user, "%USER%", StringComparison.OrdinalIgnoreCase);

            string machine = SafeGet(() => Environment.MachineName);
            if (machine.Length >= 3)
                result = result.Replace(machine, "%MACHINE%", StringComparison.OrdinalIgnoreCase);

            return result;
        }

        private static IEnumerable<(string Value, string Placeholder)> KnownLocations()
        {
            yield return (SafeGet(() => AppContext.BaseDirectory).TrimEnd('\\'), "%APPDIR%");
            yield return (SafeGet(() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), "%USERPROFILE%");
            yield return (SafeGet(() => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)), "%APPDATA%");
            yield return (SafeGet(() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), "%LOCALAPPDATA%");
        }

        private static string SafeGet(Func<string> get)
        {
            try { return get() ?? ""; }
            catch { return ""; }
        }

        /// <summary>The last directory and file name, which is enough to locate a fault in the tree.</summary>
        private static string LastTwoSegments(string path)
        {
            var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "";

            return parts.Length == 1 ? parts[^1] : $"{parts[^2]}\\{parts[^1]}";
        }

        private static string Truncate(string value, int limit) =>
            value.Length <= limit ? value : value[..limit] + "\n... truncated ...";

        /// <summary>
        /// The report as the user sees it and as it is sent. One rendering, so the two cannot differ.
        /// </summary>
        public string ToText()
        {
            var text = new StringBuilder();

            text.AppendLine("Compile Pal crash report");
            text.AppendLine();
            text.AppendLine($"When      : {When.ToString("u", CultureInfo.InvariantCulture)}");
            text.AppendLine($"Version   : {AppVersion}");
            text.AppendLine($"Windows   : {OsVersion}");
            text.AppendLine($"Runtime   : {Runtime}");
            text.AppendLine($"Fatal     : {(Fatal ? "yes" : "no")}");
            text.AppendLine();
            text.AppendLine($"Exception : {Kind}");
            text.AppendLine($"Message   : {Message}");
            text.AppendLine();
            text.AppendLine("Stack");
            text.AppendLine(Stack);

            return text.ToString();
        }
    }
}
