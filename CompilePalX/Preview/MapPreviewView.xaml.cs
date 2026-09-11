using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CompilePalX.Compiling;
using Microsoft.Web.WebView2.Core;

namespace CompilePalX.Preview
{
    /// <summary>
    /// The PREVIEW tab: a compiled map, drawn from its BSP, in a WebGL page inside WebView2.
    ///
    /// Everything heavy happens off the UI thread - reading a large map's BSP and packing its
    /// lightmaps takes a second or two - and the page is reloaded when the files are written.
    /// </summary>
    public partial class MapPreviewView : UserControl
    {
        private bool browserReady;
        private bool browserFailed;
        private bool exporting;
        private (IReadOnlyList<string> Candidates, string Reason)? pending;

        public MapPreviewView()
        {
            InitializeComponent();

            Browser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 28, 28, 28);
        }

        /// <summary>Whether the view reloads itself as compile steps finish.</summary>
        public bool FollowCompile => FollowCheckBox.IsChecked == true;

        /// <summary>
        /// Called when a step is about to start, which is the moment the step before it has finished
        /// and its output is on disk. Geometry exists after VBSP; lighting after VRAD.
        /// </summary>
        internal void OnStepStarting(CompileStepInfo info)
        {
            if (!FollowCompile || info.StepNumber < 2 || info.StepNumber - 2 >= info.StepNames.Count)
                return;

            string finished = info.StepNames[info.StepNumber - 2];
            if (finished is not ("VBSP" or "VRAD"))
                return;

            var map = CompilingManager.MapFiles.Where(m => m.Compile).ElementAtOrDefault(info.MapNumber - 1);
            if (map is null)
                return;

            string bsp = Path.ChangeExtension(map.File, "bsp");
            if (File.Exists(bsp))
                Show(bsp, $"after {finished}");
        }

        /// <summary>Called when the run has ended: the last map compiled, wherever its BSP ended up.</summary>
        public void OnCompileFinished()
        {
            if (!FollowCompile)
                return;

            var map = CompilingManager.MapFiles.LastOrDefault(m => m.Compile && m.State == MapCompileState.Succeeded);
            if (map is null)
                return;

            var copies = CompiledCopiesOf(map);
            if (copies.Count > 0)
                Show(copies, "finished");
        }

        private void LoadButton_OnClick(object sender, RoutedEventArgs e)
        {
            var map = ConfigurationManager.PreviewMap;
            if (map is null)
            {
                StatusText.Text = "Select a map in the queue first.";
                return;
            }

            var copies = CompiledCopiesOf(map);
            if (copies.Count == 0)
            {
                StatusText.Text = $"{map.FileName} has not been compiled yet - no .bsp next to it or in the game's maps folder.";
                return;
            }

            Show(copies, "loaded");
        }

        /// <summary>
        /// The compiled copies of a map, newest first: the one next to the .vmf and the one COPY put
        /// in the game's maps folder. Both are tried, so a copy that cannot be read for any reason
        /// does not stop the other from being shown. A queued .bsp is its own answer.
        /// </summary>
        private static IReadOnlyList<string> CompiledCopiesOf(Map map)
        {
            if (map.IsBSP)
                return File.Exists(map.File) ? [map.File] : [];

            var candidates = new[]
            {
                Path.ChangeExtension(map.File, "bsp"),
                GameConfigurationManager.GameConfiguration?.MapFolder is { } folder
                    ? Path.Combine(folder, Path.ChangeExtension(Path.GetFileName(map.File), "bsp"))
                    : null,
            };

            return candidates
                .Where(c => c is not null && File.Exists(c))
                .Select(c => c!)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
        }

        private void Show(string bspPath, string reason) => Show([bspPath], reason);

        /// <summary>
        /// Exports and shows the first of <paramref name="candidates"/> that can be read. One export
        /// at a time; a request that arrives while one is running replaces anything already waiting,
        /// since only the latest matters.
        /// </summary>
        private void Show(IReadOnlyList<string> candidates, string reason)
        {
            if (exporting)
            {
                pending = (candidates, reason);
                return;
            }

            _ = ShowAsync(candidates, reason);
        }

        private async Task ShowAsync(IReadOnlyList<string> candidates, string reason)
        {
            exporting = true;
            string name = Path.GetFileName(candidates[0]);
            StatusText.Text = $"Reading {name}…";

            try
            {
                PreviewExporter.Export? export = null;
                string? lastProblem = null;

                foreach (var candidate in candidates)
                {
                    try
                    {
                        export = await Task.Run(() => PreviewExporter.Write(candidate));
                        name = Path.GetFileName(candidate);
                        break;
                    }
                    catch (InvalidDataException e)
                    {
                        // an unreadable copy; the next candidate may be fine
                        CompilePalLogger.LogLineDebug($"Preview skipped \"{candidate}\": {e.Message}");
                        lastProblem = e.Message;
                    }
                }

                if (export is null)
                    throw new InvalidDataException(lastProblem ?? "no readable copy");

                if (!await EnsureBrowserAsync())
                {
                    StatusText.Text = $"{name} was read, but the preview needs the Microsoft Edge WebView2 runtime, which could not be started.";
                    return;
                }

                Browser.CoreWebView2.Navigate($"https://{PreviewExporter.HostName}/viewer.html?v={export.Stamp}");
                Browser.Visibility = Visibility.Visible;
                Placeholder.Visibility = Visibility.Collapsed;

                var scene = export.Scene;
                string lighting = scene.LightingMode switch
                {
                    "hdr" => "HDR lighting",
                    "ldr" => "LDR lighting",
                    _ => "no lighting yet",
                };
                StatusText.Text = $"{name} ({reason}) · {scene.DrawnFaces:N0} faces · {scene.DrawnDisplacements:N0} displacements · {lighting}" +
                                  (scene.Compressed ? " · read from a compressed BSP" : "");
            }
            catch (Exception e)
            {
                CompilePalLogger.LogLineDebug($"Preview of \"{name}\" failed: {e}");
                StatusText.Text = $"Could not preview {name}: {e.Message}";
            }
            finally
            {
                exporting = false;

                if (pending is { } next)
                {
                    pending = null;
                    Show(next.Candidates, next.Reason);
                }
            }
        }

        /// <summary>Starts the browser the first time it is needed. False when the runtime is missing.</summary>
        private async Task<bool> EnsureBrowserAsync()
        {
            if (browserReady)
                return true;
            if (browserFailed)
                return false;

            try
            {
                await Browser.EnsureCoreWebView2Async(await ErrorWindow.SharedEnvironment());
            }
            catch (Exception e)
            {
                CompilePalLogger.LogLineDebug($"WebView2 unavailable for the map preview: {e.Message}");
                browserFailed = true;
                PlaceholderTitle.Text = "The map preview needs WebView2";
                PlaceholderText.Text = "The Microsoft Edge WebView2 runtime could not be started. It ships with Windows 11 and current Windows 10; on other systems it is a free download from Microsoft.";
                return false;
            }

            var core = Browser.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsZoomControlEnabled = false;

            // The preview folder becomes an origin of its own, so the page can fetch its data with
            // plain relative URLs and nothing else on the machine is reachable from it.
            core.SetVirtualHostNameToFolderMapping(PreviewExporter.HostName, PreviewExporter.Folder, CoreWebView2HostResourceAccessKind.Allow);

            // nothing in here should be able to leave the folder
            core.NavigationStarting += (_, args) =>
            {
                if (!args.Uri.StartsWith($"https://{PreviewExporter.HostName}/", StringComparison.OrdinalIgnoreCase))
                    args.Cancel = true;
            };
            core.NewWindowRequested += (_, args) => args.Handled = true;

            browserReady = true;
            return true;
        }
    }
}
