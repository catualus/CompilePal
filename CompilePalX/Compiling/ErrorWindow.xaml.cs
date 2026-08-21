using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace CompilePalX.Compiling
{
    /// <summary>
    /// Interaction logic for ErrorWindow.xaml
    /// </summary>
    public partial class ErrorWindow
    {
        private readonly string html;

        public ErrorWindow(Error error)
        {
            InitializeComponent();

            html = BuildHtml(error);

            Loaded += async (_, _) => await ShowAsync();
        }

        /// <summary>
        /// Fills the catalogue's HTML template with the values captured from the log line.
        /// </summary>
        private static string BuildHtml(Error error)
        {
            var html = error.Message;
            var i = 0;

            // The compiled Regex, not a fresh one built from its pattern string. Supplementary errors
            // are created with RegexOptions.IgnoreCase, and rebuilding from ToString() drops the
            // options - so a trigger that only matched because of them fails to match a second time
            // here, and the [sub:N] placeholders are left in the page unsubstituted.
            foreach (Group group in error.RegexTrigger.Match(error.ShortDescription).Groups)
            {
                // first group is always the entire match, ignore it
                if (i == 0)
                {
                    i++;
                    continue;
                }

                html = html.Replace($"[sub:{i}]", group.Value);
                i++;
            }

            return html;
        }

        private async System.Threading.Tasks.Task ShowAsync()
        {
            try
            {
                await ErrorBrowser.EnsureCoreWebView2Async();
            }
            catch (Exception e)
            {
                // No WebView2 runtime, or it failed to start. The error description is the whole point
                // of the window, so fall back to showing it rather than presenting an empty frame.
                CompilePalLogger.LogLineDebug($"WebView2 unavailable, falling back to plain text: {e.Message}");
                ShowFallback();
                return;
            }

            var core = ErrorBrowser.CoreWebView2;

            // Nothing here should be able to navigate, run a download or open a context menu - it is a
            // static description rendered from a template, and the only outbound links are handled below.
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;

            // The page is HTML from a remote error catalogue (interlopers.net by default, and whatever
            // URL the user has configured). It is static markup, so nothing needs to execute - and
            // leaving script on would make a compromised or hostile catalogue entry far more than a
            // wrong error description.
            core.Settings.IsScriptEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsWebMessageEnabled = false;

            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenExternally(e.Uri);
            };

            core.NavigationStarting += (_, e) =>
            {
                // The first navigation is the document being set below; every later one is a link the
                // user clicked, which belongs in their browser rather than in this window.
                if (e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    return;

                e.Cancel = true;
                OpenExternally(e.Uri);
            };

            ErrorBrowser.NavigateToString(html);
        }

        private void ShowFallback()
        {
            ErrorBrowser.Visibility = Visibility.Collapsed;
            FallbackScroller.Visibility = Visibility.Visible;

            // Crude, but the template is simple markup and this only has to be readable.
            FallbackText.Text = Regex.Replace(html, "<[^>]+>", " ")
                .Replace("&nbsp;", " ")
                .Replace("&amp;", "&")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">");
        }

        private static void OpenExternally(string url)
        {
            // The catalogue's pages use these two placeholder schemes for its own site.
            if (url.StartsWith("about:forum"))
                url = url.Replace("about:forum", "http://www.interlopers.net/forum");

            if (url.StartsWith("about:tutorials"))
                url = url.Replace("about:tutorials", "http://www.interlopers.net/tutorials");

            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception e)
            {
                CompilePalLogger.LogLineDebug($"Could not open {url}: {e.Message}");
            }
        }
    }
}
