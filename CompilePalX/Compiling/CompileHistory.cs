using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace CompilePalX.Compiling
{
	/// <summary>
	/// One past compile: what was built, how it went, and where its transcript is.
	///
	/// The transcript itself has always been written - every finished compile drops a timestamped .txt
	/// into CompileLogs - but nothing ever read one back, so the record existed and was invisible. The
	/// log is plain text with no structure to mine, so the facts worth listing are recorded alongside
	/// it rather than parsed back out of it.
	/// </summary>
	public sealed class CompileRun
	{
		public DateTime Finished { get; set; }
		public string LogFile { get; set; } = "";
		public string Maps { get; set; } = "";
		public string Preset { get; set; } = "";
		public string Game { get; set; } = "";
		public TimeSpan Duration { get; set; }
		public int Errors { get; set; }
		public int Warnings { get; set; }
		public bool Cancelled { get; set; }

		[JsonIgnore]
		public string Outcome => Cancelled ? "cancelled" : Errors > 0 ? "failed" : "succeeded";

		[JsonIgnore]
		public string Summary
		{
			get
			{
				var parts = new List<string> { Duration.ToString(Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss") };

				if (Errors > 0)
					parts.Add($"{Errors} error{(Errors == 1 ? "" : "s")}");
				if (Warnings > 0)
					parts.Add($"{Warnings} warning{(Warnings == 1 ? "" : "s")}");
				if (Cancelled)
					parts.Insert(0, "cancelled");

				return string.Join(" · ", parts);
			}
		}

		[JsonIgnore]
		public string When => Finished.ToString("ddd d MMM, HH:mm");
	}

	/// <summary>
	/// The index of past compiles, kept beside the transcripts it describes.
	/// </summary>
	public static class CompileHistory
	{
		public static readonly string LogDirectory = "CompileLogs";
		private static string IndexFile => Path.Combine(LogDirectory, "history.json");

		/// <summary>
		/// How many runs to keep in the index.
		///
		/// The transcripts themselves are left alone - deleting a user's logs to tidy a list would be
		/// a poor trade - so this only bounds what the History tab lists.
		/// </summary>
		private const int MaxRuns = 100;

		public static List<CompileRun> Load()
		{
			if (!File.Exists(IndexFile))
				return [];

			try
			{
				var runs = JsonConvert.DeserializeObject<List<CompileRun>>(File.ReadAllText(IndexFile)) ?? [];

				// Newest first, and without any entry whose transcript has since been deleted by hand.
				return runs
					.Where(r => File.Exists(Path.Combine(LogDirectory, r.LogFile)))
					.OrderByDescending(r => r.Finished)
					.ToList();
			}
			catch (Exception e)
			{
				CompilePalLogger.LogLineDebug($"Could not read compile history: {e.Message}");
				return [];
			}
		}

		public static void Add(CompileRun run)
		{
			try
			{
				var runs = Load();
				runs.Insert(0, run);

				if (runs.Count > MaxRuns)
					runs = runs.Take(MaxRuns).ToList();

				Directory.CreateDirectory(LogDirectory);
				File.WriteAllText(IndexFile, JsonConvert.SerializeObject(runs, Formatting.Indented));
			}
			catch (Exception e)
			{
				// A compile that finished must not be reported as failed because its history entry
				// could not be written.
				CompilePalLogger.LogLineDebug($"Could not record compile history: {e.Message}");
			}
		}

		/// <summary>Reads a past transcript back, or null if it has gone missing since being indexed.</summary>
		public static string? ReadLog(CompileRun run)
		{
			try
			{
				string path = Path.Combine(LogDirectory, run.LogFile);
				return File.Exists(path) ? File.ReadAllText(path) : null;
			}
			catch (Exception e)
			{
				CompilePalLogger.LogLineDebug($"Could not read {run.LogFile}: {e.Message}");
				return null;
			}
		}
	}
}
