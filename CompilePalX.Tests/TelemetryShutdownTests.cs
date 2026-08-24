using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// The usage report is sent from the window's closing path, and the way it is waited on there
    /// is load-bearing.
    ///
    /// The shipped 1.0.0 build hung on close. The cause was sync-over-async on the UI thread:
    /// OnClosing called FlushAsync(...).GetAwaiter().GetResult(), which blocks the WPF dispatcher,
    /// while the continuation after the HTTP await is posted back to that same dispatcher. The
    /// request went out and was answered - the endpoint logged it - and then the continuation
    /// waited for a thread that was waiting for the continuation.
    ///
    /// The three-second timeout inside FlushAsync could not save it. Cancelling the request only
    /// makes the same continuation runnable on the same blocked thread.
    ///
    /// Asserted against the source. Reproducing it needs a real SynchronizationContext and a
    /// window, and a test that deadlocks on failure is worse than no test.
    /// </summary>
    public class TelemetryShutdownTests
    {
        private static string SourceDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "CompilePalX", "MainWindow.xaml.cs")))
                    return Path.Combine(dir.FullName, "CompilePalX");

                dir = dir.Parent;
            }

            throw new InvalidOperationException($"Could not find CompilePalX sources above {AppContext.BaseDirectory}");
        }

        private static string Closing()
        {
            string code = File.ReadAllText(Path.Combine(SourceDir(), "MainWindow.xaml.cs"));

            int at = code.IndexOf("protected override void OnClosing", StringComparison.Ordinal);
            Assert.True(at > 0, "OnClosing not found");

            return code[at..];
        }

        /// <summary>The exact call that hung the shipped build.</summary>
        [Fact]
        public void TheClosingPathDoesNotBlockOnTheFlushDirectly()
        {
            Assert.DoesNotContain("FlushAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult()", Closing());
            Assert.DoesNotMatch(new Regex(@"TelemetryManager\.FlushAsync\([^)]*\)\s*\.GetAwaiter\(\)"), Closing());
        }

        /// <summary>
        /// Moving it to the pool is what removes the deadlock: a pool thread has no
        /// SynchronizationContext, so nothing the flush does needs the dispatcher.
        /// </summary>
        [Fact]
        public void TheFlushIsStartedOnTheThreadPool()
        {
            Assert.Contains("Task.Run(() => TelemetryManager.FlushAsync", Closing());
        }

        /// <summary>
        /// And the wait is bounded independently, so a flush that ignores its own timeout still
        /// cannot hold the window open.
        /// </summary>
        [Fact]
        public void TheWaitHasACeilingOfItsOwn()
        {
            string closing = Closing();

            Assert.Matches(new Regex(@"\.Wait\([^)]+\)"), closing);
            Assert.DoesNotMatch(new Regex(@"\.Wait\(\s*\)"), closing);
        }

        /// <summary>
        /// The send itself must not capture a context either. MainWindow no longer blocks the UI
        /// thread, but a method whose correctness depends on every caller knowing that is a method
        /// waiting to be called wrongly.
        /// </summary>
        [Fact]
        public void TheSendDoesNotCaptureASynchronizationContext()
        {
            string telemetry = File.ReadAllText(Path.Combine(SourceDir(), "Telemetry", "TelemetryManager.cs"));

            int at = telemetry.IndexOf("PostAsync(", StringComparison.Ordinal);
            Assert.True(at > 0, "the POST was not found");

            Assert.Contains("ConfigureAwait(false)", telemetry[at..(at + 200)]);
        }

        /// <summary>
        /// The shape of the bug, demonstrated on a context that behaves like WPF's.
        ///
        /// Not a test of Compile Pal's code - it is here so the failure mode is legible to whoever
        /// reads this file next, and so the fix below is visibly the thing that resolves it. Runs
        /// with a timeout rather than blocking forever if the premise ever stops holding.
        /// </summary>
        [Fact]
        public async Task BlockingASingleThreadedContextOnItsOwnContinuationDeadlocks()
        {
            var context = new SingleThreadContext();
            var deadlocked = new TaskCompletionSource<bool>();

            var worker = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(context);

                async Task Work()
                {
                    // No ConfigureAwait: the continuation is posted back to this context.
                    await Task.Delay(10);
                }

                try
                {
                    // Blocks this thread, which is the only thread that can run the continuation.
                    deadlocked.SetResult(!Work().Wait(TimeSpan.FromMilliseconds(500)));
                }
                catch (Exception)
                {
                    deadlocked.SetResult(false);
                }
            });

            worker.IsBackground = true;
            worker.Start();

            Assert.True(await deadlocked.Task, "the premise no longer holds: this should deadlock");
        }

        /// <summary>The same work, off the context, completes. Which is the fix.</summary>
        [Fact]
        public async Task TheSameWorkOnThePoolCompletes()
        {
            var context = new SingleThreadContext();
            var finished = new TaskCompletionSource<bool>();

            var worker = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(context);

                async Task Work() => await Task.Delay(10);

                finished.SetResult(Task.Run(Work).Wait(TimeSpan.FromSeconds(2)));
            });

            worker.IsBackground = true;
            worker.Start();

            Assert.True(await finished.Task);
        }

        /// <summary>A context that only runs work when its owning thread pumps it, like a dispatcher.</summary>
        private sealed class SingleThreadContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object? state)
            {
                // Queued and never pumped, which is exactly the state a blocked dispatcher is in.
            }
        }
    }
}
