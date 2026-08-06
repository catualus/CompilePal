using System;
using System.Threading.Tasks;
using System.Windows;
using CompilePalX.Compiling;

namespace CompilePalX.Theming
{
    /// <summary>
    /// Modal dialogs, replacing the MahApps DialogManager extensions the app used before the move to
    /// WPF-UI. Kept deliberately small: the app only ever needs "tell the user something" and
    /// "ask the user to confirm something".
    /// </summary>
    public static class AppDialog
    {
        /// <summary>Shows a message with a single dismiss button.</summary>
        public static Task ShowAsync(string title, string message, string closeText = "OK")
        {
            return ShowCoreAsync(title, message, primaryText: null, closeText: closeText)
                .ContinueWith(_ => { }, TaskScheduler.Default);
        }

        /// <summary>
        /// Asks the user to confirm an action. Returns true only if the affirmative button was chosen,
        /// so dismissing the dialog is always treated as "no".
        /// </summary>
        public static async Task<bool> ConfirmAsync(string title, string message, string affirmativeText = "OK", string negativeText = "Cancel")
        {
            var result = await ShowCoreAsync(title, message, affirmativeText, negativeText);
            return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
        }

        private static Task<Wpf.Ui.Controls.MessageBoxResult> ShowCoreAsync(string title, string message, string? primaryText, string closeText)
        {
            // dialogs must be constructed and shown on the UI thread
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
                return Task.FromResult(Wpf.Ui.Controls.MessageBoxResult.None);

            return dispatcher.Invoke(async () =>
            {
                try
                {
                    var messageBox = new Wpf.Ui.Controls.MessageBox
                    {
                        Title = title,
                        Content = message,
                        CloseButtonText = closeText,
                    };

                    if (primaryText is not null)
                        messageBox.PrimaryButtonText = primaryText;

                    return await messageBox.ShowDialogAsync();
                }
                catch (Exception ex)
                {
                    CompilePalLogger.LogLineDebug($"Failed to show dialog \"{title}\": {ex.Message}");
                    return Wpf.Ui.Controls.MessageBoxResult.None;
                }
            });
        }
    }
}
