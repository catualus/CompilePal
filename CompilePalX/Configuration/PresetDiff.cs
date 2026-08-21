using System;
using System.Collections.Generic;
using System.Linq;

namespace CompilePalX.Configuration
{
	/// <summary>One parameter, as it stands in each of the two presets being compared.</summary>
	public sealed class PresetDifference
	{
		public required string Step { get; init; }
		public required string Parameter { get; init; }
		public required string Left { get; init; }
		public required string Right { get; init; }

		public bool Differs => !string.Equals(Left, Right, StringComparison.Ordinal);
	}

	/// <summary>
	/// One compile step's parameters under each of the two presets. A null side means that preset does
	/// not carry the step at all, which is different from carrying it with nothing set.
	/// </summary>
	public sealed class PresetStepComparison
	{
		public required string Step { get; init; }
		public IReadOnlyCollection<ConfigItem>? Left { get; init; }
		public IReadOnlyCollection<ConfigItem>? Right { get; init; }
	}

	/// <summary>
	/// Compares two presets parameter by parameter.
	///
	/// Answering "how does Best differ from Best (tools++)" previously meant selecting each step of one
	/// preset in turn, reading its parameters, switching preset and doing it again from memory. With
	/// thirteen presets, six of them near-identically named, that is the question people most often had
	/// and least often answered.
	///
	/// Takes the parameters already extracted rather than the CompileProcess they came from: that keeps
	/// this a pure function over data - so it can be tested without a CompileProcess, which cannot be
	/// constructed without its meta.json on disk - and keeps the type out of a signature that would
	/// otherwise have to be internal.
	/// </summary>
	public static class PresetDiff
	{
		/// <summary>Shown where a preset does not carry a parameter at all.</summary>
		public const string Absent = "—";

		/// <summary>Shown for a flag: present, but with no value of its own.</summary>
		public const string Flag = "on";

		public static List<PresetDifference> Compare(IEnumerable<PresetStepComparison> steps)
		{
			var rows = new List<PresetDifference>();

			foreach (var step in steps)
			{
				// A step neither preset runs has nothing to say about either of them.
				if (step.Left == null && step.Right == null)
					continue;

				// Whether the step is part of the preset at all is itself a difference, and the most
				// consequential one - it decides whether the step runs.
				rows.Add(new PresetDifference
				{
					Step = step.Step,
					Parameter = "(step included)",
					Left = step.Left != null ? "yes" : "no",
					Right = step.Right != null ? "yes" : "no",
				});

				var left = Values(step.Left);
				var right = Values(step.Right);

				foreach (var name in left.Keys.Union(right.Keys).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase))
				{
					rows.Add(new PresetDifference
					{
						Step = step.Step,
						Parameter = name,
						Left = left.GetValueOrDefault(name, Absent),
						Right = right.GetValueOrDefault(name, Absent),
					});
				}
			}

			return rows;
		}

		/// <summary>
		/// A preset's parameters for one step, keyed by name.
		///
		/// Values for a repeatable parameter are joined rather than listed separately: an "Include"
		/// added three times is one setting with three values, and pairing them up positionally across
		/// two presets would invent an ordering neither of them has - reporting a value added to one
		/// side as though a different value had been removed from the other.
		/// </summary>
		private static Dictionary<string, string> Values(IReadOnlyCollection<ConfigItem>? items)
		{
			var values = new Dictionary<string, string>();

			if (items == null)
				return values;

			foreach (var group in items.GroupBy(i => i.Name))
			{
				values[group.Key] = string.Join(", ", group.Select(i =>
					i.CanHaveValue && !string.IsNullOrWhiteSpace(i.Value) ? i.Value.Trim() : Flag));
			}

			return values;
		}
	}
}
