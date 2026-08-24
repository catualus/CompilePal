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

        /// <summary>
        /// A prop whose fade distances are the wrong way round.
        ///
        /// fademindist is where a prop STARTS to fade and fademaxdist is where it has fully gone, so
        /// the maximum has to be the larger. Reversed, the engine has the prop vanish the instant it
        /// is beyond fademindist instead of fading out - so it pops out of existence close to the
        /// camera, which reads as a missing prop rather than a fade setting.
        ///
        /// Both numbers are the mapper's own, transposed, so swapping them restores exactly what was
        /// intended - the same reasoning as the light falloff fix above.
        ///
        /// -1 is left alone. It is the sentinel for "no minimum, use fademaxdist only", not a distance.
        /// </summary>
        public static VmfFixResult FixPropFadeDistances(VmfDocument vmf)
        {
            var applied = new List<string>();

            foreach (var entity in vmf.Entities)
            {
                string? cls = vmf.Classname(entity);
                if (cls == null || !cls.StartsWith("prop_", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? minRaw = vmf.GetValue(entity, "fademindist");
                string? maxRaw = vmf.GetValue(entity, "fademaxdist");
                if (minRaw == null || maxRaw == null)
                    continue;

                if (!TryParse(minRaw, out double min) || !TryParse(maxRaw, out double max))
                    continue;

                if (min <= 0 || max <= 0 || max >= min)
                    continue;

                vmf.SetValue(entity, "fademindist", maxRaw);
                vmf.SetValue(entity, "fademaxdist", minRaw);

                string origin = vmf.GetValue(entity, "origin") ?? "?";
                applied.Add($"{cls} ({origin}): swapped fade distances {minRaw}/{maxRaw} -> {maxRaw}/{minRaw}");
            }

            return new VmfFixResult(applied.Count, applied);
        }

        /// <summary>
        /// A skybox named as a file path instead of a name.
        ///
        /// vbsp appends the six side suffixes and the .vmt itself, so worldspawn's skyname has to be
        /// the bare name - "sky_day01_01". Hammer's texture browser and copying from a file manager
        /// both produce "materials/skybox/sky_day01_01.vmt", and vbsp then looks for
        /// "materials/skybox/materials/skybox/sky_day01_01.vmtup.vmt", finds nothing, and the map
        /// compiles with a black or default sky.
        ///
        /// Mechanical: strip the directory and the extension. Nothing is guessed - the name is
        /// already there, wrapped in text vbsp adds for itself.
        /// </summary>
        public static VmfFixResult FixSkyName(VmfDocument vmf)
        {
            var applied = new List<string>();

            foreach (var entity in vmf.Entities)
            {
                if (!string.Equals(vmf.Classname(entity), "worldspawn", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? sky = vmf.GetValue(entity, "skyname");
                if (string.IsNullOrWhiteSpace(sky))
                    continue;

                string cleaned = sky.Replace('\\', '/').Trim();

                int slash = cleaned.LastIndexOf('/');
                if (slash >= 0)
                    cleaned = cleaned[(slash + 1)..];

                if (cleaned.EndsWith(".vmt", StringComparison.OrdinalIgnoreCase) ||
                    cleaned.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase))
                    cleaned = cleaned[..^4];

                if (cleaned.Length == 0 || cleaned == sky)
                    continue;

                vmf.SetValue(entity, "skyname", cleaned);
                applied.Add($"worldspawn: skyname \"{sky}\" -> \"{cleaned}\"");
            }

            return new VmfFixResult(applied.Count, applied);
        }

        /// <summary>
        /// Classnames that are meaningless without brushes. Each is brush-only in the FGD: there is no
        /// point-entity form, so one with no solid block is not a different usage, it is broken.
        /// </summary>
        private static readonly HashSet<string> BrushOnlyClasses = new(StringComparer.OrdinalIgnoreCase)
        {
            "func_detail", "func_brush", "func_wall", "func_wall_toggle", "func_illusionary",
            "func_areaportal", "func_areaportalwindow", "func_occluder", "func_viscluster",
            "func_ladder", "func_lod", "func_water_analog", "func_conveyor", "func_breakable",
            "func_door", "func_door_rotating", "func_movelinear", "func_rotating", "func_tracktrain",
            "func_smokevolume", "func_precipitation", "func_dustcloud", "func_clip_vphysics",
            "func_buyzone", "func_bomb_target", "func_hostage_rescue", "func_nobuild",
        };

        /// <summary>
        /// A brush entity that has no brushes.
        ///
        /// vbsp stops on this: "bmodel N has no head node (class 'X', targetname 'Y')" - a fatal error,
        /// so nothing compiles until it is gone. It happens when the brushes are tied to another entity
        /// in "ignore groups" mode, or deleted while the entity survives, leaving a keyvalue block with
        /// no geometry. It is invisible in Hammer's 3D view, which is what makes it hard to find.
        ///
        /// Removing it is what fixing it by hand amounts to: the entity has nothing to act on, cannot
        /// be given geometry automatically, and its continued presence only prevents the map compiling.
        ///
        /// Restricted to classnames that are brush-only. A trigger_* or a func_* that also has a point
        /// form would be a judgement call, and those are left alone.
        /// </summary>
        public static VmfFixResult RemoveEmptyBrushEntities(VmfDocument vmf)
        {
            var applied = new List<string>();

            foreach (var entity in vmf.Entities)
            {
                string? cls = vmf.Classname(entity);
                if (cls == null || !BrushOnlyClasses.Contains(cls))
                    continue;

                if (entity.Children.Contains("solid"))
                    continue;

                // An origin means Hammer is treating it as a point entity, which is a different
                // problem and not one to solve by deleting the mapper's work.
                if (vmf.GetValue(entity, "origin") != null)
                    continue;

                string name = vmf.GetValue(entity, "targetname") ?? "(no name)";
                vmf.RemoveEntity(entity);
                applied.Add($"{cls} {name}: removed - brush entity with no brushes (vbsp: \"has no head node\")");
            }

            return new VmfFixResult(applied.Count, applied);
        }

        private static bool TryParse(string s, out double value) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
