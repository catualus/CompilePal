using System;
using System.IO;
using System.Text.RegularExpressions;
using CompilePalX.Crash;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// The crash report, and above all what it does not contain.
    ///
    /// Written from a real crash. Compile Pal 1.0.1 failed to start with
    ///
    ///     System.UnauthorizedAccessException: Access to the path
    ///     'C:\Users&lt;name&gt;\Downloads\Compile.Pal.1.0.1\Compile Pal 1.0.1\Parameters\CUBEMAPS\parameters.json'
    ///     is denied.
    ///
    /// which names the person running it in the message. Exception messages carry paths, Windows
    /// paths carry account names, and a crash reporter that sends messages verbatim sends those.
    /// </summary>
    public class CrashReportTests
    {
        private static string Home =>
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // ------------------------------------------------------------------ redaction

        [Fact]
        public void TheUserProfilePathIsReplaced()
        {
            string text = $"Access to the path '{Home}\\Downloads\\thing.json' is denied.";

            string redacted = CrashReport.Redact(text);

            Assert.DoesNotContain(Home, redacted);
            Assert.Contains("%USERPROFILE%", redacted);
        }

        [Fact]
        public void TheAccountNameIsReplacedEvenWithoutAPathAroundIt()
        {
            string user = Environment.UserName;
            if (user.Length < 3) return;   // nothing to find on a machine with a two-letter account

            Assert.DoesNotContain(user, CrashReport.Redact($"user {user} has no access"),
                StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheMachineNameIsReplaced()
        {
            string machine = Environment.MachineName;
            if (machine.Length < 3) return;

            Assert.DoesNotContain(machine, CrashReport.Redact($"on host {machine}"),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// An arbitrary rooted path, from a drive the redactor knows nothing about, still goes.
        /// The known locations cannot cover a map kept on D:\ or a network share.
        /// </summary>
        [Fact]
        public void AnyOtherLocalPathIsReducedToItsLastTwoSegments()
        {
            string redacted = CrashReport.Redact(
                @"Could not read D:\Maps\ClientWork\SecretProject\rp_unreleased.vmf");

            Assert.DoesNotContain("ClientWork", redacted);
            Assert.DoesNotContain(@"D:\", redacted);

            // The end is kept, because that is what makes a crash diagnosable.
            Assert.Contains("rp_unreleased.vmf", redacted);
        }

        /// <summary>
        /// The exact message from the crash this was built for. The file name has to survive,
        /// because "CUBEMAPS\parameters.json" is the entire diagnosis.
        /// </summary>
        [Fact]
        public void TheRealCrashMessageLosesTheUserAndKeepsTheDiagnosis()
        {
            string original =
                @"Access to the path 'C:\Users\someone\Downloads\Compile.Pal.1.0.1\Compile Pal 1.0.1\Parameters\CUBEMAPS\parameters.json' is denied.";

            string redacted = CrashReport.Redact(original);

            Assert.DoesNotContain("someone", redacted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"C:\Users", redacted);
            Assert.Contains("CUBEMAPS", redacted);
            Assert.Contains("parameters.json", redacted);
            Assert.Contains("is denied", redacted);
        }

        [Fact]
        public void RedactingHandlesNullAndEmpty()
        {
            Assert.Equal("", CrashReport.Redact(null));
            Assert.Equal("", CrashReport.Redact(""));
        }

        // ------------------------------------------------------------------ the report

        [Fact]
        public void AReportCarriesTheInnermostExceptionToo()
        {
            var inner = new InvalidOperationException("the actual problem");
            var outer = new Exception("the wrapper", inner);

            var report = CrashReport.From(outer, fatal: true);

            Assert.Contains("the wrapper", report.Message);
            Assert.Contains("the actual problem", report.Message);
            Assert.Contains("InvalidOperationException", report.Stack);
        }

        [Fact]
        public void AReportRecordsWhetherItWasFatal()
        {
            Assert.True(CrashReport.From(new Exception("x"), fatal: true).Fatal);
            Assert.False(CrashReport.From(new Exception("x"), fatal: false).Fatal);
        }

        /// <summary>
        /// The rendered text is what the dialog shows and what is sent. One rendering, so the user
        /// cannot be shown something different from what leaves the machine.
        /// </summary>
        [Fact]
        public void TheRenderedReportContainsNoUserPaths()
        {
            Exception caught;
            try
            {
                throw new UnauthorizedAccessException(
                    $"Access to the path '{Home}\\Downloads\\Compile Pal\\Parameters\\CUBEMAPS\\parameters.json' is denied.");
            }
            catch (Exception e)
            {
                caught = e;
            }

            string text = CrashReport.From(caught, fatal: true).ToText();

            Assert.DoesNotContain(Home, text);
            Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);

            // and still says what went wrong
            Assert.Contains("UnauthorizedAccessException", text);
            Assert.Contains("parameters.json", text);
        }

        [Fact]
        public void TheRenderedReportCarriesTheContextNeededToActOnIt()
        {
            string text = CrashReport.From(new Exception("boom"), fatal: true).ToText();

            Assert.Contains("Version", text);
            Assert.Contains("Windows", text);
            Assert.Contains("Runtime", text);
            Assert.Contains("Exception", text);
            Assert.Contains("Stack", text);
        }

        /// <summary>A runaway message must not produce an unbounded report.</summary>
        [Fact]
        public void AnEnormousMessageIsTruncated()
        {
            var report = CrashReport.From(new Exception(new string('x', 50_000)), fatal: true);

            Assert.True(report.Message.Length < 2_000, $"message was {report.Message.Length} characters");
            Assert.Contains("truncated", report.Message);
        }

        [Fact]
        public void ADeeplyNestedExceptionChainIsBounded()
        {
            Exception e = new("innermost");
            for (int i = 0; i < 50; i++)
                e = new Exception($"layer {i}", e);

            var report = CrashReport.From(e, fatal: true);

            // Followed, but not all fifty of them.
            Assert.Contains("layer 49", report.Message);
            Assert.DoesNotContain("innermost", report.Message);
        }

        // ------------------------------------------------------------------ the handler contract

        private static string SourceDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "CompilePalX", "ExceptionHandler.cs")))
                    return Path.Combine(dir.FullName, "CompilePalX");

                dir = dir.Parent;
            }

            throw new InvalidOperationException($"Could not find CompilePalX sources above {AppContext.BaseDirectory}");
        }

        /// <summary>
        /// The handler must not be async void.
        ///
        /// It was, and that is what turned a fault inside it into a loop: the exception was
        /// re-raised on the dispatcher, arrived back at the same handler, and repeated. It also
        /// meant the method returned before showing anything, racing the process exit that follows.
        /// </summary>
        [Fact]
        public void TheExceptionHandlerIsNotAsyncVoid()
        {
            string code = File.ReadAllText(Path.Combine(SourceDir(), "ExceptionHandler.cs"));

            Assert.DoesNotContain("async void LogException", code);
            Assert.Contains("public static void LogException", code);
        }

        /// <summary>A fatal crash must not report success to whatever launched it.</summary>
        [Fact]
        public void AFatalCrashExitsNonZero()
        {
            string code = File.ReadAllText(Path.Combine(SourceDir(), "ExceptionHandler.cs"));

            Assert.DoesNotContain("Environment.Exit(0)", code);
            Assert.Contains("Environment.Exit(1)", code);
        }

        /// <summary>
        /// ErrorProgress is called from the handler before any window exists, so its guard has to
        /// be at the top of the method rather than inside the dispatcher lambda. Reaching the
        /// lambda means dereferencing the taskbar item, which is the null that started all this.
        /// </summary>
        [Fact]
        public void TheTaskbarProgressGuardComesBeforeTheDereference()
        {
            string code = File.ReadAllText(Path.Combine(SourceDir(), "Compiling", "ProgressManager.cs"));

            int at = code.IndexOf("static public void ErrorProgress", StringComparison.Ordinal);
            Assert.True(at > 0, "ErrorProgress not found");

            string body = code[at..(at + 400)];

            int guard = body.IndexOf("if (!Ready) return;", StringComparison.Ordinal);
            int dereference = body.IndexOf("taskbarInfo.Dispatcher", StringComparison.Ordinal);

            Assert.True(guard > 0, "no guard in ErrorProgress");
            Assert.True(guard < dereference, "the guard must come before the taskbar is touched");
        }

        /// <summary>
        /// Every built-in step loads independently. One unreadable parameter file is a missing
        /// step, not a dead application, which is what it was.
        /// </summary>
        [Fact]
        public void BuiltInCompileStepsLoadIndependently()
        {
            string code = File.ReadAllText(Path.Combine(SourceDir(), "Configuration", "ConfigurationManager.cs"));

            Assert.Contains("private static void AddBuiltIn", code);
            Assert.DoesNotMatch(new Regex(@"CompileProcesses\.Add\(new \w+Process\(\)\)"), code);
            Assert.DoesNotContain("CompileProcesses.Add(new BSPPack())", code);
        }
    }
}
