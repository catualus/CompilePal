using System;
using System.Diagnostics;
using System.Net;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CompilePalX.Theming;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CompilePalX.Compiling
{
    /// <summary>
    /// Interaction logic for ErrorWindow.xaml
    /// </summary>
    public partial class ErrorWindow
    {
        private readonly string html;

        private static string stylePath = Path.Combine("./Compiling", "errorpage.css");

        /// <summary>
        /// Read once and kept. Every error window needs the same stylesheet, and these windows are
        /// opened and closed one after another as the user works down a compile log.
        /// </summary>
        private static string? cachedStyle;

        /// <summary>
        /// One WebView2 environment for the whole process.
        ///
        /// Each window creates its own WebView2 control, and left to itself each control creates its
        /// own environment over the default user data folder. That folder is single-writer: while a
        /// previous window's browser process still holds it, creating the next environment fails, so
        /// opening several error descriptions in quick succession left later windows blank (or in the
        /// plain-text fallback) until enough of them had finished shutting down. Sharing one
        /// environment means the folder is opened once and every window attaches to it.
        /// </summary>
        private static Task<CoreWebView2Environment>? environment;
        private static readonly object environmentLock = new();

        public ErrorWindow(Error error)
        {
            InitializeComponent();

            html = BuildHtml(error);

            // Set before the core is created: afterwards the first paint has already happened, and on a
            // dark theme that paint is a full-window white flash.
            ErrorBrowser.DefaultBackgroundColor = ThemeBridge.IsDarkTheme()
                ? System.Drawing.Color.FromArgb(255, 32, 32, 32)
                : System.Drawing.Color.White;

            Loaded += async (_, _) => await ShowAsync();

            // The browser process outlives the window otherwise, and each one that lingers holds part of
            // the shared environment open. Closing enough of them without this is what eventually
            // exhausted the renderer processes.
            Closed += (_, _) => ErrorBrowser.Dispose();
        }

        /// <summary>
        /// Fills the catalogue's HTML template with the values captured from the log line, then appends
        /// the stylesheet.
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

                // Escaped. group.Value is captured out of compile output, which is derived from
                // whatever map, material and model files were fed to the tools - so it is attacker
                // controlled by anyone able to hand someone a .vmf. Substituted raw, as it was, this
                // is markup injection into a page the app then renders.
                html = html.Replace($"[sub:{i}]", WebUtility.HtmlEncode(group.Value));
                i++;
            }

            /*
             * A policy forbidding every subresource, prepended so it is parsed before anything the
             * entry itself contains.
             *
             * Script is already disabled on the WebView, so injected markup cannot execute - but
             * that was never the whole exposure. NavigateToString gives the document `about:blank`
             * as its base, which breaks relative URLs and stops nothing absolute: an injected
             * `<img src="http://...">` would still have been fetched, turning "open an error
             * description" into a beacon reporting the reader's address to whoever authored the map.
             *
             * Escaping the substitutions above closes the injection route. This closes the
             * capability itself, which also covers a hostile entry that arrived through the
             * catalogue legitimately. style-src permits the inline block appended below; img-src
             * data: keeps embedded images working. Nothing else is allowed out.
             */
            const string policy =
                "<meta http-equiv=\"Content-Security-Policy\" " +
                "content=\"default-src 'none'; style-src 'unsafe-inline'; img-src data:\">";

            // The stylesheet is appended rather than prepended, and deliberately so. Entries cached
            // in errors.txt were written with an older template that set its own body font and left
            // the colours to the renderer; a stylesheet placed after that one wins on document order
            // without having to out-specify it selector by selector.
            return policy + "\n" + html + "\n<style>\n" + Style() + "\n</style>\n";
        }

        private static string Style()
        {
            if (cachedStyle is not null)
                return cachedStyle;

            try
            {
                cachedStyle = File.ReadAllText(stylePath);
            }
            catch (Exception e)
            {
                // Losing the stylesheet must not cost the description its legibility, which is the whole
                // reason the file exists. The minimum that keeps the text readable is stated inline.
                CompilePalLogger.LogLineDebug($"Could not read {stylePath}: {e.Message}");
                cachedStyle = "html,body{background:#202020;color:#e6e6e6;" +
                              "font-family:'Segoe UI',sans-serif;padding:16px}a{color:#5cb0ff}";
            }

            return cachedStyle;
        }

        private static Task<CoreWebView2Environment> Environment()
        {
            lock (environmentLock)
            {
                // Cleared on failure so a transient problem - the runtime still installing, a locked
                // folder - does not permanently pin every later window to the plain-text fallback.
                if (environment is { IsFaulted: true })
                    environment = null;

                return environment ??= CoreWebView2Environment.CreateAsync();
            }
        }

        private async Task ShowAsync()
        {
            try
            {
                await ErrorBrowser.EnsureCoreWebView2Async(await Environment());
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

            ApplyTheme(core);

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

        /// <summary>
        /// Points the renderer's prefers-color-scheme at the app's own theme setting.
        ///
        /// Without this the WebView follows Windows, so a user who has picked Light or Dark in settings
        /// - against the OS - gets a description page in the opposite scheme to the window around it.
        /// It also pins the auto-dark behaviour: left on Auto the runtime is free to darken a page it
        /// considers light-only, which it did here without lifting the text colour to match.
        /// </summary>
        private static void ApplyTheme(CoreWebView2 core)
        {
            try
            {
                core.Profile.PreferredColorScheme = ThemeBridge.IsDarkTheme()
                    ? CoreWebView2PreferredColorScheme.Dark
                    : CoreWebView2PreferredColorScheme.Light;
            }
            catch (Exception e)
            {
                // Older runtimes do not expose Profile. The stylesheet still defaults to the light
                // palette in that case, which is readable either way.
                CompilePalLogger.LogLineDebug($"Could not set the WebView colour scheme: {e.Message}");
            }
        }

        private void ShowFallback()
        {
            ErrorBrowser.Visibility = Visibility.Collapsed;
            FallbackScroller.Visibility = Visibility.Visible;

            // Crude, but the template is simple markup and this only has to be readable. The stylesheet
            // appended by BuildHtml is dropped whole - stripping its tags would leave the CSS text
            // itself sitting at the bottom of the description.
            var body = Regex.Replace(html, "<style[^>]*>.*?</style>", " ",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            FallbackText.Text = Regex.Replace(body, "<[^>]+>", " ")
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
