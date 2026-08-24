using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace CompilePalX.Crash
{
    /// <summary>
    /// The crash dialog.
    ///
    /// Before this existed a fatal error wrote a file into CrashLogs and the application vanished,
    /// so the only way to find out what had happened was to know that folder was there. This is the
    /// ordinary thing every other desktop application does: say that it crashed, show what would be
    /// sent, and let the person decide whether to send it.
    ///
    /// Sending is per crash and never automatic. Usage reporting has its own setting and this does
    /// not read it: someone who leaves usage reporting off has not thereby refused to report a
    /// crash they are being shown, and someone who left it on has not agreed in advance to send a
    /// stack trace. Both are separate decisions and both are asked.
    /// </summary>
    public partial class CrashWindow : Window
    {
        private readonly CrashReport report;

        /// <summary>True if the user chose to send. Read by the caller after ShowDialog returns.</summary>
        public bool ShouldSend { get; private set; }

        public CrashWindow(CrashReport report)
        {
            this.report = report;

            InitializeComponent();

            Summary.Text = report.Fatal
                ? "Something went wrong and Compile Pal has to close. The details below have been saved, "
                  + "and can be sent to help get it fixed."
                : "Something went wrong. Compile Pal is still running, but part of it may not work "
                  + "properly until it is restarted.";

            ReportText.Text = report.ToText();

            bool canSend = CrashReporter.CanSend;

            SendButton.IsEnabled = canSend;
            SendButton.Visibility = canSend ? Visibility.Visible : Visibility.Collapsed;

            DontSendButton.Content = canSend ? "Don't send" : "Close";

            PrivacyNote.Text = canSend
                ? "This is exactly what is sent, and nothing else. File paths and account names have "
                  + "been removed. Sending is a one-off choice for this crash and is not affected by "
                  + "the usage reporting setting."
                : "This build has no reporting destination compiled in, so nothing can be sent from "
                  + "here. Use Copy to attach the report to a bug report on GitHub.";
        }

        private void OnCopy(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(ReportText.Text);
                CopyButton.Content = "Copied";
            }
            catch (Exception)
            {
                // The clipboard can be locked by another process. Nothing worth escalating from a
                // window whose whole job is to be the last thing that still works.
                CopyButton.Content = "Copy failed";
            }
        }

        private void OnOpenLogFolder(object sender, RoutedEventArgs e)
        {
            try
            {
                string folder = Path.GetFullPath(CrashReporter.CrashLogFolder);
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

                // ArgumentList, not an interpolated command string: the path is ours, but building
                // shell arguments by concatenation is a habit worth not having.
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                start.ArgumentList.Add(folder);

                Process.Start(start);
            }
            catch (Exception)
            {
                OpenLogButton.Content = "Could not open";
            }
        }

        private void OnSend(object sender, RoutedEventArgs e)
        {
            ShouldSend = true;
            DialogResult = true;
            Close();
        }

        private void OnDontSend(object sender, RoutedEventArgs e)
        {
            ShouldSend = false;
            DialogResult = false;
            Close();
        }
    }
}
