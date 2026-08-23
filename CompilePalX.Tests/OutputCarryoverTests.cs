using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Documents;
using System.Windows.Media;
using CompilePalX.Compiling;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// Compile output arrives from the tools in fixed-size chunks, not lines, so CompilePalLogger
    /// keeps a partial line in a process-wide buffer until the rest of it turns up.
    ///
    /// That buffer used never to be cleared between runs. A compile almost always ends with something
    /// in it, and cancelling makes that a certainty - the reader returns the instant the token trips,
    /// which is mid-line by definition. The next compile's first chunk was then appended to whatever
    /// the last one left behind and the two emitted as a single line, which is why a fresh run opened
    /// with the tail of the previous one. Clearing the FlowDocument did not help: the leftover was in
    /// the logger, not the document.
    /// </summary>
    public class OutputCarryoverTests : IDisposable
    {
        private readonly StringBuilder written = new();

        public OutputCarryoverTests()
        {
            // Subscribed by method group rather than through a named delegate variable: the delegate
            // types themselves are internal to CompilePalX, and -= matches on target and method, so
            // Dispose still detaches exactly these handlers.
            CompilePalLogger.OnWrite += Capture;

            // Invoked unconditionally by LogProgressive rather than through ?., so a test that does not
            // subscribe fails inside the logger instead of on its own assertion.
            CompilePalLogger.OnBacktrack += IgnoreBacktrack;

            // Static state, shared with whatever ran before this test.
            CompilePalLogger.ResetOutputState();
            written.Clear();
        }

        public void Dispose()
        {
            CompilePalLogger.OnWrite -= Capture;
            CompilePalLogger.OnBacktrack -= IgnoreBacktrack;
            CompilePalLogger.ResetOutputState();
        }

        private static void IgnoreBacktrack(List<Run> runs) { }

        private Run Capture(string s, Brush? b, int? fontWeight)
        {
            written.Append(s);
            return new Run(s);
        }

        private string Output => written.ToString();

        /// <summary>
        /// The reported bug: cancel a compile part-way through a line, start another, and the first
        /// line of the new output is the previous run's remnant with the new text stuck onto it.
        /// </summary>
        [WpfFact]
        public void AResetDropsThePartialLineLeftBehindByACancelledCompile()
        {
            // A step that was cut off mid-line. "Writing bsp fi" never got its newline.
            CompilePalLogger.LogProgressive("Building faces...\r\nWriting bsp fi");

            Assert.Contains("Building faces...", Output);

            // What StartCompile does once the output document has been cleared.
            CompilePalLogger.ResetOutputState();
            written.Clear();

            CompilePalLogger.LogProgressive("Starting compilation of de_test\r\n");

            Assert.Contains("Starting compilation of de_test", Output);
            Assert.DoesNotContain("Writing bsp fi", Output);
        }

        /// <summary>
        /// Guards the assertion above against passing for the wrong reason: without the reset the
        /// carryover really does happen, and the two are emitted as one line.
        /// </summary>
        [WpfFact]
        public void WithoutAResetThePartialLineIsPrependedToTheNextCompile()
        {
            CompilePalLogger.LogProgressive("Building faces...\r\nWriting bsp fi");
            written.Clear();

            CompilePalLogger.LogProgressive("Starting compilation of de_test\r\n");

            Assert.Contains("Writing bsp fiStarting compilation of de_test", Output);
        }

        /// <summary>
        /// A partial line still has to be completed by the chunk that follows it *within* a run - that
        /// is what the buffer is for, and a reset must not be mistaken for "flush on every chunk".
        /// </summary>
        [WpfFact]
        public void APartialLineIsStillCompletedByTheNextChunkOfTheSameRun()
        {
            CompilePalLogger.LogProgressive("Writing bsp fi");
            CompilePalLogger.LogProgressive("le c:\\maps\\de_test.bsp\r\n");

            Assert.Contains("le c:\\maps\\de_test.bsp", Output);
        }
    }
}
