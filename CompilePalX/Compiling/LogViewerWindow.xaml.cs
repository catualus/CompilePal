using System;
using System.Collections.Generic;
using System.Windows;

namespace CompilePalX.Compiling
{
	/// <summary>
	/// Read-only viewer for a transcript from a past compile.
	/// </summary>
	public partial class LogViewerWindow
	{
		private readonly string log;

		public LogViewerWindow(CompileRun run, string log)
		{
			InitializeComponent();

			this.log = log;

			Title = $"Compile Log - {run.Maps}";
			HeadingBlock.Text = run.Maps;

			// Preset and game can be blank on a run recorded before they were known; skipping them keeps
			// the line from reading as "24 Aug, 14:02  ·    ·    ·  4:12".
			var parts = new List<string> { run.When };
			if (!string.IsNullOrWhiteSpace(run.Preset)) parts.Add(run.Preset);
			if (!string.IsNullOrWhiteSpace(run.Game)) parts.Add(run.Game);
			parts.Add(run.Summary);

			SubheadingBlock.Text = string.Join("  ·  ", parts);

			LogBox.Text = log;
		}

		private void CopyButton_OnClick(object sender, RoutedEventArgs e)
		{
			try
			{
				Clipboard.SetText(log);
			}
			catch (Exception ex)
			{
				// Another process can hold the clipboard open; losing a copy is not worth a crash.
				CompilePalLogger.LogLineDebug($"Could not copy the log to the clipboard: {ex.Message}");
			}
		}
	}
}
