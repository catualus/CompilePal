using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CompilePalX.Compiling;
using Microsoft.Win32;

namespace CompilePalX
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
	    protected override void OnStartup(StartupEventArgs e)
	    {
            /*
             * Every route an unhandled exception can take out of a WPF application.
             *
             * All three previously passed crash: false, which logged the exception and returned -
             * so an exception that had already killed whatever it came from left the application
             * running in an unknown state, with nothing said to the user. That is how 1.0.1 failed
             * to start and told nobody: the crash was real, the handler treated it as a note.
             *
             * They are now classified by what each one actually means.
             */

            // The CLR is going to terminate the process after this returns. Nothing survives it,
            // so it is fatal by definition and the handler must not return before the user has
            // seen it.
            AppDomain.CurrentDomain.UnhandledException += (s, err) =>
            {
                ExceptionHandler.LogException(err.ExceptionObject as Exception
                    ?? new Exception($"Non-exception was thrown: {err.ExceptionObject}"), crash: true);
            };

            /*
             * An exception that escaped a UI event handler.
             *
             * Marked handled, because leaving it unhandled tears down the application anyway and
             * does so without the dialog. LogException decides whether to exit; for a fault during
             * startup it will, and for one from a stray button handler the application keeps
             * running with the user told what happened.
             */
            DispatcherUnhandledException += (s, err) =>
            {
                err.Handled = true;
                ExceptionHandler.LogException(err.Exception, crash: !HasStarted);
            };

            // A faulted Task nobody awaited. Observed so the finalizer does not later escalate it,
            // and reported without exiting: the application is usually still perfectly usable.
            TaskScheduler.UnobservedTaskException += (s, err) =>
            {
                err.SetObserved();
                ExceptionHandler.LogException(err.Exception, crash: false);
            };

            // force invariant culture so stack traces are always in english
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

            // last-chance save; MainWindow's Closing handler calls Environment.Exit, and this still runs
            AppDomain.CurrentDomain.ProcessExit += (s, err) =>
            {
                try { ConfigurationManager.Flush(); } catch { /* nothing useful to do while exiting */ }
            };

            // set working directory
            Directory.SetCurrentDirectory(Path.GetDirectoryName(AppContext.BaseDirectory));

            // settings hold the theme choice, so load them before any window is shown
            ConfigurationManager.LoadSettings();
            Theming.ThemeBridge.Initialize();

            // store path in registry
            RegistryManager.Write("Path", AppContext.BaseDirectory);

            /*
             * From here on a dispatcher exception is survivable.
             *
             * Before this point there is no window, no loaded configuration and nothing for the
             * application to return to, so a failure is fatal and is treated as such. Afterwards a
             * fault in one event handler is not a reason to close somebody's work.
             */
            HasStarted = true;

            base.OnStartup(e);
        }

        /// <summary>Whether startup finished. See the dispatcher handler above.</summary>
        private static bool HasStarted;
    }
}
