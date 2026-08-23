using System;
using System.Collections.Generic;
using System.Linq;
using CompilePalX;
using CompilePalX.Configuration;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// What leaves the machine, pinned.
    ///
    /// The settings window tells the user that a submission carries no identifier of any kind
    /// and no map names, paths or compile output. That is a promise made in a UI string, which
    /// is exactly the sort of promise that quietly stops being true when someone adds "just one
    /// more field" to the payload. These tests are the thing that notices.
    ///
    /// They run against the real <see cref="TelemetryManager"/>, so a new field appears here
    /// as a failure rather than as a line in a diff nobody read.
    /// </summary>
    [Collection("Telemetry")]
    public class TelemetryPayloadTests : IDisposable
    {
        private readonly bool originalEnabled;
        private readonly string originalEndpoint;

        public TelemetryPayloadTests()
        {
            originalEnabled = ConfigurationManager.Settings.TelemetryEnabled;
            originalEndpoint = ConfigurationManager.Settings.TelemetryEndpoint;

            // DescribePayload deliberately says nothing while reporting is off, so the tests have
            // to turn it on to see the shape at all.
            ConfigurationManager.Settings.TelemetryEnabled = true;
            ConfigurationManager.Settings.TelemetryEndpoint = "https://example.invalid/events";

            // The counters are process-wide, so without this each test would see whatever the
            // previous one recorded.
            TelemetryManager.Discard();
        }

        public void Dispose()
        {
            TelemetryManager.Discard();
            ConfigurationManager.Settings.TelemetryEnabled = originalEnabled;
            ConfigurationManager.Settings.TelemetryEndpoint = originalEndpoint;
        }

        private static JObject Payload()
        {
            TelemetryManager.Launch();
            TelemetryManager.Compile();
            TelemetryManager.SelectGameConfiguration("Garry's Mod");

            return JObject.Parse(TelemetryManager.DescribePayload());
        }

        /// <summary>
        /// The whole contract, as a list. A field not on it is either something new that has not
        /// been thought about, or something that should never have been added.
        /// </summary>
        [Fact]
        public void TheSubmissionHasExactlyFiveTopLevelFields()
        {
            var keys = Payload().Properties().Select(p => p.Name).OrderBy(n => n).ToArray();

            Assert.Equal(new[] { "app", "counts", "games", "os", "version" }, keys);
        }

        [Fact]
        public void NothingInTheSubmissionIdentifiesTheInstall()
        {
            string raw = Payload().ToString();

            // Names an identifier would plausibly be given. Cheap, and it has caught this class
            // of regression in other codebases.
            foreach (string forbidden in new[]
                     {
                         "userId", "user_id", "anonymousId", "installId", "install_id",
                         "machine", "hostname", "guid", "uuid", "fingerprint", "deviceId",
                         "session_id", "sessionId", "email", "username",
                     })
            {
                Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
            }

            // The machine's own name is the specific thing the old implementation hashed into an
            // identifier, so it gets its own assertion rather than relying on the list above.
            Assert.DoesNotContain(Environment.MachineName, raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.UserName, raw, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheOsVersionCarriesNoRevision()
        {
            string os = Payload().Value<string>("os")!;

            // major.minor.build, and no fourth component. The revision narrows a machine down
            // within a small population and adds nothing a build number does not.
            Assert.Matches(@"^\d+\.\d+\.\d+$", os);
        }

        /// <summary>
        /// Counter names have to match the set the server accepts, or a metric is silently
        /// discarded on arrival and the feature looks like it works while collecting nothing.
        /// </summary>
        [Fact]
        public void EveryCounterNameIsOneTheServerAccepts()
        {
            // The set the endpoint accepts. A counter whose name is not on this list is
            // discarded on arrival, so a mismatch here means the metric is collected on the
            // client and silently thrown away - which looks exactly like the feature working.
            // Kept in step by hand; the receiving service is not part of this repository.
            var accepted = new HashSet<string>
            {
                "sessions", "compiles", "compiles_ok", "compiles_failed", "compiles_cancel",
                "errors", "presets_new", "presets_modified", "crashes",
            };

            TelemetryManager.Launch();
            TelemetryManager.Compile();
            TelemetryManager.CompileSucceeded();
            TelemetryManager.CompileFailed();
            TelemetryManager.CompileCancelled();
            TelemetryManager.CompileError();
            TelemetryManager.NewPreset();
            TelemetryManager.ModifyPreset();
            TelemetryManager.Error();

            var counts = JObject.Parse(TelemetryManager.DescribePayload()).Value<JObject>("counts")!;
            var names = counts.Properties().Select(p => p.Name).ToList();

            Assert.NotEmpty(names);
            foreach (string name in names)
                Assert.Contains(name, accepted);
        }

        /// <summary>
        /// The game name is an editable text box in the game configuration window. A user who
        /// types a path, or their own name, into it must not have that sent.
        /// </summary>
        [Fact]
        public void AnUnrecognisedGameNameIsReplacedRatherThanSent()
        {
            const string secret = @"C:\Users\somebody\private-maps";

            TelemetryManager.Launch();
            TelemetryManager.SelectGameConfiguration(secret);

            var payload = TelemetryManager.DescribePayload();

            Assert.DoesNotContain("somebody", payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private-maps", payload, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("other", payload);
        }

        [Fact]
        public void ARecognisedGameNameIsSentAsItself()
        {
            TelemetryManager.Launch();
            TelemetryManager.SelectGameConfiguration("Portal 2");

            Assert.Contains("Portal 2", TelemetryManager.DescribePayload());
        }

        [Fact]
        public void NothingIsDescribedWhileReportingIsOff()
        {
            ConfigurationManager.Settings.TelemetryEnabled = false;

            TelemetryManager.Launch();
            TelemetryManager.Compile();

            Assert.DoesNotContain("compilepal", TelemetryManager.DescribePayload());
        }

        /// <summary>
        /// Turning the feature off has to stop collection, not merely stop sending. A build that
        /// accumulated counters regardless would be keeping a record the user declined.
        /// </summary>
        [Fact]
        public void CountersAreNotAccumulatedWhileReportingIsOff()
        {
            TelemetryManager.Discard();
            ConfigurationManager.Settings.TelemetryEnabled = false;
            TelemetryManager.Compile();
            TelemetryManager.Compile();

            ConfigurationManager.Settings.TelemetryEnabled = true;
            var counts = JObject.Parse(TelemetryManager.DescribePayload()).Value<JObject>("counts")!;

            Assert.False(counts.ContainsKey("compiles"),
                "compiles recorded while the user had reporting switched off");
        }

        /// <summary>
        /// A blank endpoint has to be as complete an off switch as the toggle. It is what a build
        /// with no backend of its own ships with, and what a user clearing the box expects.
        /// </summary>
        [Fact]
        public void AnEmptyEndpointDisablesCollectionEntirely()
        {
            ConfigurationManager.Settings.TelemetryEndpoint = "";

            TelemetryManager.Launch();

            Assert.DoesNotContain("compilepal", TelemetryManager.DescribePayload());
        }
    }
}
