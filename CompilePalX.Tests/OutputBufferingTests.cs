using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// Compile output is buffered and flushed on a timer rather than written to the document a line at
    /// a time, because appending (and scrolling to the end of) a FlowDocument per line stalled the UI
    /// thread throughout a verbose step.
    ///
    /// That buffering has one way to fail badly, which it did: if the flush runs at a lower dispatcher
    /// priority than the log writes arrive on, it never gets a turn while a compile is producing output.
    /// The OUTPUT tab stayed empty for the whole compile and only filled when the writing stopped -
    /// making Cancel look like the thing that produced the log.
    /// </summary>
    public class OutputBufferingTests
    {
        [WpfFact]
        public void BackgroundPriorityWorkIsStarvedByNormalPriorityWork()
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var order = new List<string>();

            // Queued first, so ordering here is decided by priority alone and not by arrival.
            dispatcher.BeginInvoke(DispatcherPriority.Background, () => order.Add("flush"));

            // The log writes: each line reaches the UI thread on a Normal-priority Dispatcher.Invoke.
            for (int i = 0; i < 5; i++)
            {
                int line = i;
                dispatcher.BeginInvoke(DispatcherPriority.Normal, () => order.Add($"line{line}"));
            }

            // Blocks until everything above ApplicationIdle - which is everything queued above - has run.
            dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            Assert.Equal(6, order.Count);

            // Every line, however many arrive, is handled before the flush that was queued before them.
            // With output arriving continuously that "before them" never ends, which is the starvation.
            Assert.Equal("flush", order[^1]);
        }

        [WpfFact]
        public void NormalPriorityWorkIsNotStarved()
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var order = new List<string>();

            dispatcher.BeginInvoke(DispatcherPriority.Normal, () => order.Add("flush"));

            for (int i = 0; i < 5; i++)
            {
                int line = i;
                dispatcher.BeginInvoke(DispatcherPriority.Normal, () => order.Add($"line{line}"));
            }

            dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            // At equal priority the queue is FIFO, so the flush keeps its place and runs on schedule.
            Assert.Equal("flush", order[0]);
        }

        private static string SourceDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "CompilePalX", "MainWindow.xaml.cs");
                if (File.Exists(candidate))
                    return Path.Combine(dir.FullName, "CompilePalX");

                dir = dir.Parent;
            }

            throw new InvalidOperationException($"Could not find CompilePalX sources above {AppContext.BaseDirectory}");
        }

        /// <summary>
        /// DispatcherTimer's parameterless constructor uses Background priority, so this has to be stated
        /// explicitly. A future edit that "tidies" it back to <c>new()</c> reintroduces the empty console.
        /// </summary>
        [Fact]
        public void TheOutputFlushTimerDoesNotRunAtBackgroundPriority()
        {
            string code = File.ReadAllText(Path.Combine(SourceDir(), "MainWindow.xaml.cs"));

            var match = Regex.Match(code, @"outputFlushTimer\s*=\s*new\s*\(([^)]*)\)");
            Assert.True(match.Success, "could not find the outputFlushTimer construction in MainWindow.xaml.cs");

            string priority = match.Groups[1].Value;
            Assert.False(string.IsNullOrWhiteSpace(priority),
                "outputFlushTimer uses DispatcherTimer's default priority, which is Background - log " +
                "flushes will be starved for the whole of a compile.");
            Assert.DoesNotContain("Background", priority);
        }

        /// <summary>
        /// The timer alone is not enough of a guarantee, so a full buffer flushes on the spot. Without
        /// it, any future delay to the timer becomes an invisible log again.
        /// </summary>
        [Fact]
        public void AFullBufferFlushesWithoutWaitingForTheTimer()
        {
            string code = File.ReadAllText(Path.Combine(SourceDir(), "MainWindow.xaml.cs"));

            Assert.Matches(@"pendingOutputInlines\.Count\s*>=\s*MaxPendingOutputInlines", code);
        }
    }
}
