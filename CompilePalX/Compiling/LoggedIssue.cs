using System;
using System.Windows.Documents;
using System.Windows.Media;

namespace CompilePalX.Compiling
{
	/// <summary>
	/// One error or warning recognised in the compile output, with enough context to be listed away
	/// from the log and jumped back to.
	///
	/// The log is a single flat document of tens of thousands of lines, and the only way to find what
	/// went wrong was to read it or step through matches one at a time. Collecting the recognised
	/// issues as they are logged gives an "errors only" view without having to filter the document
	/// itself - FlowDocument blocks have no visibility to toggle, so a real filter would mean
	/// rebuilding the whole document on every change.
	/// </summary>
	public sealed class LoggedIssue
	{
		public required Error Error { get; init; }

		/// <summary>The line as it appeared in the log, trimmed for display in the list.</summary>
		public required string Text { get; init; }

		/// <summary>Compile step this was logged during, or empty if it arrived outside one.</summary>
		public string Step { get; init; } = "";

		/// <summary>
		/// The hyperlink this issue was rendered as, used to scroll the log to it.
		///
		/// Null for issues restored from a saved log, which are read back as text and have no document
		/// of their own to point into.
		/// </summary>
		public Hyperlink? Link { get; init; }

		public int Severity => Error.Severity;

		public Brush SeverityBrush => Error.ErrorColor;

		/// <summary>
		/// Severity 4 and 5 are what the log calls errors; below that is advice worth reading but not
		/// worth redoing a compile over. The same split the queue cards and the footer counters use.
		/// </summary>
		public bool IsError => Severity >= 4;

		public string SeverityLabel => IsError ? "error" : "warning";

		/// <summary>What the list shows: the catalogue's short description, falling back to the line.</summary>
		public string Title =>
			string.IsNullOrWhiteSpace(Error.ShortDescription) ? Text : Error.ShortDescription;
	}
}
