using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Shell;
using CompilePalX.Compiling;

namespace CompilePalX
{
    static class ExceptionHandler
    {
        public static async void LogException(Exception e, bool crash = true)
        {
            if (!Directory.Exists("./CrashLogs"))
                Directory.CreateDirectory("./CrashLogs");


            CompilePalLogger.LogLine("An exception was caught by the ExceptionHandler:");
            CompilePalLogger.LogLine(e.ToString());
            if (e.InnerException != null)
                CompilePalLogger.LogLine(e.InnerException.ToString());

            // a fatal exception exits the process outright, so persist pending edits before we lose them
            try {
                ConfigurationManager.Flush();
            } catch (Exception flushException) {
                CompilePalLogger.LogLine($"Failed to save pending changes during crash handling: {flushException}");
            }

            try {
                TelemetryManager.Error();//risky, but /interesting/
            } catch (Exception) {}

            if (crash)
            {
                string crashLogName = DateTime.Now.ToString("s").Replace(":", "-");

                File.WriteAllText(Path.Combine("CrashLogs", crashLogName + ".txt"), e.ToString() + e.InnerException ?? "");
				
				ProgressManager.ErrorProgress();
				await Theming.AppDialog.ShowAsync("A fatal exception has occurred", e.Message, closeText: "Exit");

                Environment.Exit(0);
            }
        }
    }
}
