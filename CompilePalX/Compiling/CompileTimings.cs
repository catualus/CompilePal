using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace CompilePalX.Compiling
{
	/// <summary>
	/// How long each compile step has taken on previous runs.
	///
	/// The progress bar used to give every step an equal share of the total, so a COPY that finishes in
	/// 200ms advanced it exactly as far as a VVIS that runs for half an hour. On a real compile that
	/// makes the bar close to meaningless: it sits at 40% for twenty minutes, then jumps. Recording what
	/// steps actually cost lets the bar be weighted by it, and gives the numbers a "time remaining"
	/// estimate needs.
	///
	/// Durations are kept per map as well as globally, because the same step varies enormously between
	/// maps - VVIS on a large open map and on a small corridor map have nothing to say about each other.
	/// The global figure is the fallback for a map that has not been compiled before.
	/// </summary>
	public static class CompileTimings
	{
		private static readonly string TimingsFile = "./CompileTimings.json";

		/// <summary>
		/// Durations in seconds, keyed by step name and by "map|step".
		///
		/// Kept as a list rather than a running average so a single freak run - a machine that went to
		/// sleep, a first compile with a cold shader cache - gets outvoted rather than permanently
		/// skewing the estimate. See <see cref="Median(string,string)"/>.
		/// </summary>
		private static Dictionary<string, List<double>> samples = new();

		/// <summary>
		/// Guards <see cref="samples"/>, which is genuinely reached from two threads at once.
		///
		/// Record runs on the compile thread as each step finishes. Save runs from postCompile - which a
		/// cancel calls on the UI thread, straight off the Cancel button, while the compile thread may
		/// still be finishing the step it was in. Serializing the dictionary while that step records into
		/// it throws "collection was modified", which would take the cancel down with it.
		/// </summary>
		private static readonly object gate = new();

		/// <summary>
		/// How many runs are remembered per key.
		///
		/// Enough to be robust to one bad sample, few enough that the estimate still follows a map as it
		/// grows rather than averaging in what it looked like months ago.
		/// </summary>
		private const int MaxSamples = 10;

		private static string MapKey(string mapName, string stepName) => $"{mapName}|{stepName}";

		public static void Init()
		{
			if (!File.Exists(TimingsFile))
				return;

			try
			{
				var loaded = JsonConvert.DeserializeObject<Dictionary<string, List<double>>>(
					File.ReadAllText(TimingsFile));

				lock (gate)
					samples = loaded ?? new Dictionary<string, List<double>>();
			}
			catch (Exception e)
			{
				// Timings are an optimisation, never data the user would miss. A damaged file is worth a
				// debug line and a fresh start, not a failed launch.
				CompilePalLogger.LogLineDebug($"Could not read compile timings, starting fresh: {e.Message}");

				lock (gate)
					samples = new Dictionary<string, List<double>>();
			}
		}

		public static void Record(string mapName, string stepName, TimeSpan duration)
		{
			// A step that returned instantly did not really run - it was skipped, or failed on a missing
			// binary. Recording zeroes would drag the median toward nothing and starve the step of its
			// share of the bar on the next run.
			if (duration.TotalSeconds < 0.05)
				return;

			lock (gate)
			{
				Add(stepName, duration.TotalSeconds);
				Add(MapKey(mapName, stepName), duration.TotalSeconds);
			}
		}

		/// <remarks>Callers hold <see cref="gate"/>.</remarks>
		private static void Add(string key, double seconds)
		{
			if (!samples.TryGetValue(key, out var list))
				samples[key] = list = new List<double>();

			list.Add(seconds);

			if (list.Count > MaxSamples)
				list.RemoveRange(0, list.Count - MaxSamples);
		}

		public static void Save()
		{
			try
			{
				string json;
				lock (gate)
					json = JsonConvert.SerializeObject(samples, Formatting.Indented);

				// Outside the lock: the compile thread should not be made to wait on a disk write just to
				// record the step it has finished.
				File.WriteAllText(TimingsFile, json);
			}
			catch (Exception e)
			{
				CompilePalLogger.LogLineDebug($"Could not save compile timings: {e.Message}");
			}
		}

		/// <summary>
		/// Typical duration of a step in seconds, preferring what it cost on this map, or null when it
		/// has never been seen.
		/// </summary>
		public static double? Median(string mapName, string stepName)
		{
			lock (gate)
				return MedianOf(MapKey(mapName, stepName)) ?? MedianOf(stepName);
		}

		/// <remarks>Callers hold <see cref="gate"/>.</remarks>
		private static double? MedianOf(string key)
		{
			if (!samples.TryGetValue(key, out var list) || list.Count == 0)
				return null;

			var sorted = list.OrderBy(v => v).ToList();
			int middle = sorted.Count / 2;

			return sorted.Count % 2 == 1
				? sorted[middle]
				: (sorted[middle - 1] + sorted[middle]) / 2d;
		}

		/// <summary>
		/// The share of a map's compile each step should account for.
		///
		/// Steps never seen before are given the average of the ones that have been, so an unknown step
		/// is treated as typical rather than as free. With nothing known at all this returns equal
		/// shares, which is exactly the old behaviour - the bar is no worse on a first run than it was.
		///
		/// The shares sum to 1 across <paramref name="stepNames"/>, not across the returned dictionary:
		/// a name appearing twice in the list (every custom program reports as "CUSTOM") has one entry
		/// here but is counted once per appearance, so a caller walking the list still totals 1.
		/// </summary>
		public static Dictionary<string, double> Shares(string mapName, IReadOnlyList<string> stepNames)
		{
			var result = new Dictionary<string, double>();
			if (stepNames.Count == 0)
				return result;

			// One lock for the whole calculation: taking it per lookup would let a step finishing on the
			// compile thread change the weights halfway through building them.
			lock (gate)
			{
				var known = new Dictionary<string, double>();
				foreach (var step in stepNames)
				{
					if (!known.ContainsKey(step) && (MedianOf(MapKey(mapName, step)) ?? MedianOf(step)) is { } median)
						known[step] = median;
				}

				double fallback = known.Count != 0 ? known.Values.Average() : 1d;

				double total = stepNames.Sum(s => known.TryGetValue(s, out var w) ? w : fallback);
				if (total <= 0)
					total = stepNames.Count;

				foreach (var step in stepNames)
				{
					double weight = known.TryGetValue(step, out var w) ? w : fallback;
					result[step] = weight / total;
				}
			}

			return result;
		}
	}
}
