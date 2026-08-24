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

            /*
             * The world block, NOT an entity whose classname is worldspawn.
             *
             * This looked for the latter first time round and therefore never fired on a real map:
             * a VMF stores the world as a top-level "world { }" block, and only "entity { }" blocks
             * were indexed. The unit test passed because its fixture was written the way the
             * documentation talks about worldspawn rather than the way Hammer actually writes it.
             */
            if (vmf.World is { } entity)
            {
                string? sky = vmf.GetValue(entity, "skyname");
                if (!string.IsNullOrWhiteSpace(sky))
                {
                    string cleaned = sky.Replace('\\', '/').Trim();

                    int slash = cleaned.LastIndexOf('/');
                    if (slash >= 0)
                        cleaned = cleaned[(slash + 1)..];

                    if (cleaned.EndsWith(".vmt", StringComparison.OrdinalIgnoreCase) ||
                        cleaned.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase))
                        cleaned = cleaned[..^4];

                    if (cleaned.Length > 0 && cleaned != sky)
                    {
                        vmf.SetValue(entity, "skyname", cleaned);
                        applied.Add($"worldspawn: skyname \"{sky}\" -> \"{cleaned}\"");
                    }
                }
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

                // The world is never removable, whatever its classname says.
                if (entity.IsWorld || entity.HasChild("solid"))
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

        /// <summary>
        /// vbsp: "Error: displacement found on a(n) func_detail entity - not supported".
        ///
        /// Fatal - nothing compiles until it is gone. Displacements are only valid on world brushes;
        /// tying one to a brush entity is a mistake people make constantly, because Hammer allows it
        /// and says nothing, and the brush looks perfectly normal in the 3D view.
        ///
        /// Moving the solid back into the world is the only fix, and is exactly what doing it by hand
        /// amounts to. An entity left with no solids afterwards is removed too, since a brush entity
        /// with no brushes is itself fatal ("has no head node") - turning one fatal error into a
        /// different one would not be much of a fix.
        /// </summary>
        public static VmfFixResult MoveDisplacementsToWorld(VmfDocument vmf)
        {
            if (vmf.World is null)
                return VmfFixResult.None;

            var applied = new List<string>();
            int moved = 0;

            foreach (var entity in vmf.Entities)
            {
                if (entity.IsWorld)
                    continue;

                var solids = entity.Solids.ToList();
                var displaced = solids.Where(sd => vmf.BlockContains(sd, "dispinfo")).ToList();
                if (displaced.Count == 0)
                    continue;

                foreach (var solid in displaced)
                    if (vmf.MoveBlockToWorld(solid))
                        moved++;

                string cls = vmf.Classname(entity) ?? "entity";
                string name = vmf.GetValue(entity, "targetname") ?? "(no name)";

                if (displaced.Count == solids.Count)
                {
                    vmf.RemoveEntity(entity);
                    applied.Add($"{cls} {name}: moved {displaced.Count} displacement brush(es) into the world, "
                                + "and removed the entity, which had nothing else in it");
                }
                else
                {
                    applied.Add($"{cls} {name}: moved {displaced.Count} displacement brush(es) into the world");
                }
            }

            return new VmfFixResult(moved, applied);
        }

        /// <summary>
        /// vbsp: "Entity N: func_areaportal can only be a single brush".
        ///
        /// Fatal. An areaportal is one plane sealing one opening, so several brushes tied to a single
        /// entity cannot describe one. Splitting into one entity per brush is the documented fix and
        /// is purely mechanical: every brush keeps its own geometry, and every copy keeps the
        /// original's keyvalues, including the name that links it to its door.
        ///
        /// Non-solid blocks travel with the first copy only. Duplicating a connections block would
        /// fire the entity's outputs once per brush, which is a behaviour change rather than a fix.
        /// </summary>
        public static VmfFixResult SplitMultiBrushAreaportals(VmfDocument vmf)
        {
            var applied = new List<string>();
            int created = 0;

            foreach (var entity in vmf.Entities)
            {
                string? cls = vmf.Classname(entity);
                if (cls == null || !cls.StartsWith("func_areaportal", StringComparison.OrdinalIgnoreCase))
                    continue;

                var solids = entity.Solids.ToList();
                if (solids.Count < 2)
                    continue;

                var keyLines = entity.Keys.Values.Distinct().OrderBy(n => n)
                    .Select(n => vmf.Lines[n]).ToList();

                var extras = entity.Blocks
                    .Where(b => !string.Equals(b.Name, "solid", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(vmf.BlockLines)
                    .ToList();

                string head = vmf.Lines[entity.StartLine];
                string indent = head[..(head.Length - head.TrimStart().Length)];

                var rebuilt = new List<string>();
                for (int i = 0; i < solids.Count; i++)
                {
                    rebuilt.Add(indent + "entity");
                    rebuilt.Add(indent + "{");
                    rebuilt.AddRange(keyLines);
                    rebuilt.AddRange(vmf.BlockLines(solids[i]));
                    if (i == 0)
                        rebuilt.AddRange(extras);
                    rebuilt.Add(indent + "}");
                }

                string name = vmf.GetValue(entity, "targetname") ?? "(no name)";

                vmf.InsertBefore(entity.StartLine, rebuilt);
                vmf.RemoveEntity(entity);

                created += solids.Count - 1;
                applied.Add($"{cls} {name}: split {solids.Count} brushes into {solids.Count} separate areaportals");
            }

            return new VmfFixResult(created, applied);
        }

        /// <summary>
        /// vbsp: "Overlay (X) at ... has invalid render order (N)."
        ///
        /// Fatal. RenderOrder decides which of two overlapping overlays draws on top, and the BSP
        /// format stores it in two bits - so the only values that exist are 0 to 3. Anything else is a
        /// typo, or a value carried in by a decompiler.
        ///
        /// Clamped rather than reset to zero: someone who wrote 5 wanted this overlay above the
        /// others, and 3 is the nearest thing the format can express.
        /// </summary>
        public static VmfFixResult FixOverlayRenderOrder(VmfDocument vmf)
        {
            const int MaxRenderOrder = 3;
            var applied = new List<string>();

            foreach (var entity in vmf.Entities)
            {
                string? cls = vmf.Classname(entity);
                if (cls == null || !cls.Contains("overlay", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? raw = vmf.GetValue(entity, "RenderOrder");
                if (raw == null || !int.TryParse(raw.Trim(), out int order))
                    continue;

                if (order >= 0 && order <= MaxRenderOrder)
                    continue;

                int clamped = Math.Clamp(order, 0, MaxRenderOrder);
                vmf.SetValue(entity, "RenderOrder", clamped.ToString(CultureInfo.InvariantCulture));

                string origin = vmf.GetValue(entity, "origin") ?? "?";
                applied.Add($"{cls} ({origin}): RenderOrder {order} -> {clamped} (the valid range is 0-3)");
            }

            return new VmfFixResult(applied.Count, applied);
        }

        /// <summary>
        /// vbsp: "prop_physics at X Y Z uses model M, which has no propdata ... will not be able to be
        /// created", after which the prop is dropped from the map.
        ///
        /// A physics prop needs mass, health and break behaviour, all of which come from the model's
        /// prop_data block. A model without one cannot be a physics prop under any setting, so this is
        /// a fact about the model rather than a judgement about the map.
        ///
        /// Converted to prop_dynamic_override, matching the static prop fix: the geometry the mapper
        /// placed still exists and still looks right, it simply does not move. Turning it into
        /// something that DID move would be inventing behaviour the map never asked for.
        ///
        /// Models that cannot be read are left alone - they may be inside a VPK, and a failed file
        /// lookup is not evidence of anything.
        /// </summary>
        public static VmfFixResult FixPhysicsPropsWithoutPropData(
            VmfDocument vmf, IReadOnlyList<string> contentDirectories)
        {
            if (contentDirectories.Count == 0)
                return VmfFixResult.None;

            var converted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var entity in vmf.Entities)
            {
                string? cls = vmf.Classname(entity);
                if (cls == null || !cls.StartsWith("prop_physics", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? model = vmf.GetValue(entity, "model");
                if (string.IsNullOrWhiteSpace(model))
                    continue;

                // Only a definite "no". Null means the file could not be read, which decides nothing.
                if (StudioModelInfo.HasPropData(model, contentDirectories) != false)
                    continue;

                vmf.SetValue(entity, "classname", "prop_dynamic_override");
                converted[model] = converted.TryGetValue(model, out int n) ? n + 1 : 1;
            }

            var descriptions = converted
                .OrderByDescending(k => k.Value)
                .Select(kv => $"{kv.Value}x {kv.Key} -> prop_dynamic_override (the model has no propdata)")
                .ToList();

            return new VmfFixResult(converted.Values.Sum(), descriptions);
        }

        /// <summary>
        /// Faults that are visible in the VMF but must NOT be repaired automatically.
        ///
        /// Every one of these has more than one reasonable answer, and picking one would mean
        /// changing a map on a guess. They are still worth naming here, because VMFFIX runs before
        /// vbsp does: finding out about an origin brush in the world now is better than finding out
        /// twenty minutes into a compile that then has to be thrown away.
        ///
        /// This deliberately does not attempt the errors that only exist during compilation - leaks,
        /// every MAX_ limit, portal and t-junction counts, lightmap page overflow. Those are not
        /// properties of the VMF at all; they are properties of what vbsp makes of it, and nothing
        /// reading the source file can know them.
        /// </summary>
        public static VmfFixResult ReportUnfixableFaults(VmfDocument vmf)
        {
            var found = new List<string>();

            // vbsp: "brush N: origin brushes not allowed in world" - fatal.
            // Not fixed: the choices are to delete the brush or to tie it to the entity it was meant
            // for, and only the mapper knows which entity that was.
            if (vmf.World is { } world)
            {
                int originBrushes = world.Solids.Count(sd => BlockUsesMaterial(vmf, sd, "tools/toolsorigin"));
                if (originBrushes > 0)
                    found.Add($"{originBrushes} origin brush(es) in the world - vbsp stops on these. "
                              + "Tie each one to the brush entity it belongs to, or delete it.");
            }

            // vbsp: "Trying to create a non-quad displacement!" - fatal.
            // Not fixed: removing the displacement destroys the terrain the mapper sculpted, and the
            // face cannot be made four-sided without deciding where the new edges go.
            int nonQuad = 0;
            foreach (var entity in vmf.Entities)
                foreach (var solid in entity.Solids)
                    nonQuad += vmf.CountNonQuadDisplacementSides(solid);

            if (nonQuad > 0)
                found.Add($"{nonQuad} displacement(s) on a face that does not have four vertices - vbsp "
                          + "stops on these. Split the face into quads in Hammer before displacing it.");

            // prop_static entities with no model at all: vbsp drops them silently.
            // Not fixed: an empty model is either a half-finished prop or a leftover, and deleting
            // someone's entity on that basis is not a call this should make.
            int modelless = vmf.Entities.Count(e =>
                (vmf.Classname(e) ?? "").StartsWith("prop_", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(vmf.GetValue(e, "model")));

            if (modelless > 0)
                found.Add($"{modelless} prop entit(ies) with no model set. vbsp discards these without "
                          + "an error, so they simply will not be in the map.");

            return new VmfFixResult(found.Count, found);
        }

        private static bool BlockUsesMaterial(VmfDocument vmf, VmfBlock block, string material)
        {
            foreach (string line in vmf.BlockLines(block))
            {
                string t = line.Trim();
                if (!t.StartsWith("\"material\"", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (t.Replace('\\', '/').Contains(material, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool TryParse(string s, out double value) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
