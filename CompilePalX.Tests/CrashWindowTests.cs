using System;
using System.Windows;
using System.Windows.Controls;
using CompilePalX.Crash;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// The crash dialog builds and shows what it should.
    ///
    /// Worth testing rather than assuming, because this window only ever runs when something else
    /// has already failed - so a fault in it is discovered by the one person least able to report
    /// it. It is deliberately built from plain WPF with every colour written out, for the same
    /// reason: a crash dialog that resolves theme resources stops working exactly when the theming
    /// is what crashed.
    /// </summary>
    public class CrashWindowTests
    {
        private static CrashReport SampleReport(bool fatal = true) =>
            CrashReport.From(
                new UnauthorizedAccessException(
                    @"Access to the path 'C:\Users\someone\Downloads\Compile Pal\Parameters\CUBEMAPS\parameters.json' is denied."),
                fatal);

        [WpfFact]
        public void TheWindowBuilds()
        {
            var window = new CrashWindow(SampleReport());

            Assert.NotNull(window);
            Assert.Equal("Compile Pal has stopped", window.Title);
        }

        /// <summary>
        /// The point of the dialog: the user sees the report before deciding. If this box were
        /// empty, or showed a summary rather than the text that is sent, the choice would be
        /// uninformed.
        /// </summary>
        [WpfFact]
        public void TheReportShownIsTheReportThatWouldBeSent()
        {
            var report = SampleReport();
            var window = new CrashWindow(report);

            var box = (TextBox)window.FindName("ReportText");

            Assert.Equal(report.ToText(), box.Text);
            Assert.True(box.IsReadOnly, "the report must not be editable before being sent");
        }

        [WpfFact]
        public void TheShownReportCarriesNoUserPath()
        {
            var window = new CrashWindow(SampleReport());
            var box = (TextBox)window.FindName("ReportText");

            Assert.DoesNotContain("someone", box.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"C:\Users", box.Text);

            // and still names the fault
            Assert.Contains("CUBEMAPS", box.Text);
            Assert.Contains("UnauthorizedAccessException", box.Text);
        }

        /// <summary>Nothing is sent unless the button is pressed. The default is not to send.</summary>
        [WpfFact]
        public void NothingIsSentUnlessAsked()
        {
            var window = new CrashWindow(SampleReport());

            Assert.False(window.ShouldSend);
        }

        /// <summary>
        /// A build with no endpoint compiled in offers no send button, rather than one that
        /// silently does nothing. A fork, or anything built from source, is in that state.
        /// </summary>
        [WpfFact]
        public void ABuildThatCannotSendSaysSoInsteadOfOfferingAButton()
        {
            var window = new CrashWindow(SampleReport());

            var send = (Button)window.FindName("SendButton");
            var dismiss = (Button)window.FindName("DontSendButton");
            var note = (TextBlock)window.FindName("PrivacyNote");

            if (CrashReporter.CanSend)
            {
                Assert.Equal(Visibility.Visible, send.Visibility);
                Assert.Equal("Don't send", dismiss.Content);
                Assert.Contains("exactly what is sent", note.Text);
            }
            else
            {
                // The state every local build and every fork is in.
                Assert.Equal(Visibility.Collapsed, send.Visibility);
                Assert.Equal("Close", dismiss.Content);
                Assert.Contains("no reporting destination", note.Text);
            }
        }

        /// <summary>
        /// A non-fatal fault says the application is still running. Telling someone it has to close
        /// when it does not is its own kind of wrong.
        /// </summary>
        [WpfFact]
        public void TheWordingDistinguishesFatalFromRecoverable()
        {
            var fatal = (TextBlock)new CrashWindow(SampleReport(fatal: true)).FindName("Summary");
            var survivable = (TextBlock)new CrashWindow(SampleReport(fatal: false)).FindName("Summary");

            Assert.Contains("has to close", fatal.Text);
            Assert.Contains("still running", survivable.Text);
        }
    }
}
