using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CompilePalX.Compiling
{
    /// <summary>
    /// What a fixer changed: how many entities, and lines describing it for the compile log.
    /// The two differ - one description can cover thousands of entities sharing a model.
    /// </summary>
    public sealed record VmfFixResult(int Count, IReadOnlyList<string> Descriptions)
    {
        public static readonly VmfFixResult None = new(0, Array.Empty<string>());
    }

    /// <summary>
    /// The actual VMF repairs, kept free of logging and compile plumbing so they can be tested.
    ///
    /// Each one targets a defect where the correct result is unambiguous. Anything requiring a
    /// judgement call belongs in a report, not here - a compile step that quietly rewrites a map
    /// according to a guess is worse than the warning it silenced.
    /// </summary>
    public static class VmfFixes
    {
        /// <summary>
        /// vrad: "Light at (x y z) has _fifty_percent_distance of A but _zero_percent_distance of B".
        ///
        /// Both values are points on one falloff curve, so the 50% distance has to be the nearer of
        /// the two. When they are reversed vrad discards the authored zero distance and substitutes
        /// twice the fifty distance, so the map lights differently from what was built. Swapping
        /// keeps both numbers the mapper actually chose.
        /// </summary>
        public static VmfFixResult FixLightFalloff(VmfDocument vmf)
        {
            var applied = new List<string>();

            foreach (var entity in vmf.Entities)
            {
                string? cls = vmf.Classname(entity);
                if (cls == null || !cls.StartsWith("light", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? fiftyRaw = vmf.GetValue(entity, "_fifty_percent_distance");
                string? zeroRaw = vmf.GetValue(entity, "_zero_percent_distance");
                if (fiftyRaw == null || zeroRaw == null)
                    continue;

                if (!TryParse(fiftyRaw, out double fifty) || !TryParse(zeroRaw, out double zero))
                    continue;

                // 0 means "no custom falloff" rather than a distance of zero, so it is not reversed.
                if (fifty <= 0 || zero <= 0 || zero >= fifty)
                    continue;

                vmf.SetValue(entity, "_fifty_percent_distance", zeroRaw);
                vmf.SetValue(entity, "_zero_percent_distance", fiftyRaw);

                string origin = vmf.GetValue(entity, "origin") ?? "?";
                applied.Add($"light ({origin}): swapped falloff {fiftyRaw}/{zeroRaw} -> {zeroRaw}/{fiftyRaw}");
            }

            return new VmfFixResult(applied.Count, applied);
        }

        /// <summary>
        /// vbsp: "To use model X as static prop, it must be compiled with $staticprop! Deleted."
        ///
        /// Note "Deleted" - there is no fallback, the prop is absent from the compiled map.
        /// prop_dynamic_override is the conventional replacement: same model, no physics, no baked
        /// lighting, so the geometry the mapper placed still exists.
        ///
        /// Models that cannot be found on disk are left alone. They may well be inside a VPK where
        /// vbsp will find them, and converting a prop on the strength of a failed file lookup would
        /// be changing the map because we could not see something.
        /// </summary>
        public static VmfFixResult FixStaticProps(VmfDocument vmf, IReadOnlyList<string> contentDirectories)
        {
            if (contentDirectories.Count == 0)
                return VmfFixResult.None;

            var converted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var entity in vmf.Entities)
            {
                if (!string.Equals(vmf.Classname(entity), "prop_static", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? model = vmf.GetValue(entity, "model");
                if (string.IsNullOrWhiteSpace(model))
                    continue;

                if (StudioModelInfo.SupportsStaticProp(model, contentDirectories) !=
                    StudioModelInfo.StaticPropSupport.NotSupported)
                    continue;

                vmf.SetValue(entity, "classname", "prop_dynamic_override");
                converted[model] = converted.TryGetValue(model, out int n) ? n + 1 : 1;
            }

            var descriptions = converted
                .OrderByDescending(k => k.Value)
                .Select(kv => $"{kv.Value}x {kv.Key} -> prop_dynamic_override (not compiled with $staticprop)")
                .ToList();

            return new VmfFixResult(converted.Values.Sum(), descriptions);
        }

        private static bool TryParse(string s, out double value) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
