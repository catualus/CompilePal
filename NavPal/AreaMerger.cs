using System;
using System.Collections.Generic;

namespace NavPal
{
    /// <summary>
    /// Merges adjacent areas that describe the same piece of ground - Valve's
    /// <c>MergeGeneratedAreas</c>.
    ///
    /// Growing rectangles out of a node grid leaves seams. A rectangle stops the moment one node in the
    /// next row is missing, covered, or off-plane, so a plain floor comes out as a patchwork of strips
    /// that happen to have started in different places. Merging puts them back together, and it is the
    /// pass that makes splitting long areas safe: on its own, `SquareUpAreas` can only fragment, which
    /// is why it measured worse than doing nothing at all.
    ///
    /// Valve's conditions, all of which must hold: both areas generated, neither marked NoMerge, a
    /// shared edge rather than a shared corner, matching attributes, coplanar surfaces, and a result
    /// within the maximum area size.
    ///
    /// There used to be one more condition here that Valve has no equivalent for: a refusal to merge
    /// anything that would end up more than six times longer than it is wide. It was standing in for
    /// <see cref="AreaSquarer"/>, which was written but left switched off, and it is the wrong half of
    /// that pair to keep. Valve merges without any shape limit and then splits the results back to
    /// roughly square; capping the merge instead means a staircase flight - long, narrow, and exactly
    /// the thing that most needs to become one area - stops merging a few steps in and stays a row of
    /// fragments. No area then spans enough of the flight for the stair test's own "has this climbed
    /// more than one step" gate to even engage.
    /// </summary>
    public static class AreaMerger
    {
        /// <summary>How closely the two heights along a shared edge must agree to count as one surface.</summary>
        private const float EdgeHeightTolerance = 2f;

        /// <summary>
        /// Ceiling on a merged area's longest side: Valve's own <c>GenerationStepSize *
        /// nav_area_max_size</c>, 25 * 50. This is the *only* limit their merge applies to shape.
        /// </summary>
        private const float MaxSize = NavConstants.GenerationStepSize * 50f;

        /// <summary>Edges closer than this are treated as the same line.</summary>
        private const float Epsilon = 0.5f;

        public sealed class Result
        {
            public int Merges;
            public int Passes;
        }

        public static Result Merge(NavFile nav)
        {
            var result = new Result();

            while (true)
            {
                int merged = MergePass(nav);
                result.Passes++;
                result.Merges += merged;

                // Merging opens up further merges - two strips joined may now align with a third - so
                // this repeats until it settles, as Valve's does.
                if (merged == 0 || result.Passes > 32)
                    break;
            }

            return result;
        }

        private static int MergePass(NavFile nav)
        {
            var dead = new HashSet<NavArea>();

            // Areas keyed by the edge they present, so a partner is found by lookup rather than by
            // comparing every area with every other one.
            var byWestEdge = new Dictionary<(long, long, long), List<NavArea>>();
            var byNorthEdge = new Dictionary<(long, long, long), List<NavArea>>();

            foreach (var area in nav.Areas)
            {
                if (!CanMerge(area)) continue;

                var b = NavGeometry.GetBounds(area);
                Add(byWestEdge, (Q(b.MinX), Q(b.MinY), Q(b.MaxY)), area);
                Add(byNorthEdge, (Q(b.MinY), Q(b.MinX), Q(b.MaxX)), area);
            }

            int merges = 0;

            foreach (var area in nav.Areas)
            {
                if (dead.Contains(area) || !CanMerge(area))
                    continue;

                var b = NavGeometry.GetBounds(area);

                // Someone whose west edge is our east edge, spanning exactly the same Y.
                if (byWestEdge.TryGetValue((Q(b.MaxX), Q(b.MinY), Q(b.MaxY)), out var eastward) &&
                    TryTake(eastward, dead, area, out var east) &&
                    MergeAlongX(area, east))
                {
                    dead.Add(east);
                    merges++;
                    continue;
                }

                if (byNorthEdge.TryGetValue((Q(b.MaxY), Q(b.MinX), Q(b.MaxX)), out var southward) &&
                    TryTake(southward, dead, area, out var south) &&
                    MergeAlongY(area, south))
                {
                    dead.Add(south);
                    merges++;
                }
            }

            if (merges > 0)
                nav.Areas.RemoveAll(dead.Contains);

            return merges;
        }

        private static bool CanMerge(NavArea area)
            => ((NavAttributes)area.AttributeFlags & NavAttributes.NoMerge) == 0;

        /// <summary>Quantised so two edges meant to be the same line hash together.</summary>
        private static long Q(float v) => (long)MathF.Round(v / Epsilon);

        private static void Add(Dictionary<(long, long, long), List<NavArea>> into,
            (long, long, long) key, NavArea area)
        {
            if (!into.TryGetValue(key, out var list))
                into[key] = list = [];

            list.Add(area);
        }

        private static bool TryTake(List<NavArea> candidates, HashSet<NavArea> dead, NavArea self,
            out NavArea found)
        {
            foreach (var candidate in candidates)
            {
                if (ReferenceEquals(candidate, self) || dead.Contains(candidate))
                    continue;

                if (candidate.AttributeFlags != self.AttributeFlags)
                    continue;

                found = candidate;
                return true;
            }

            found = null!;
            return false;
        }

        /// <summary>
        /// Joins an area with its eastern neighbour. The shared edge's two heights must agree on both
        /// sides - that is the coplanarity test in the only form that matters here, since a seam where
        /// the heights differ is a step, not one surface.
        /// </summary>
        private static bool MergeAlongX(NavArea west, NavArea east)
        {
            if (MathF.Abs(west.NeZ - east.NwCorner[2]) > EdgeHeightTolerance ||
                MathF.Abs(west.SeCorner[2] - east.SwZ) > EdgeHeightTolerance)
            {
                return false;
            }

            var a = NavGeometry.GetBounds(west);
            var b = NavGeometry.GetBounds(east);

            float width = b.MaxX - a.MinX;

            if (width > MaxSize)
                return false;

            west.SeCorner[0] = east.SeCorner[0];
            west.NeZ = east.NeZ;
            west.SeCorner[2] = east.SeCorner[2];

            return true;
        }

        private static bool MergeAlongY(NavArea north, NavArea south)
        {
            if (MathF.Abs(north.SwZ - south.NwCorner[2]) > EdgeHeightTolerance ||
                MathF.Abs(north.SeCorner[2] - south.NeZ) > EdgeHeightTolerance)
            {
                return false;
            }

            var a = NavGeometry.GetBounds(north);
            var b = NavGeometry.GetBounds(south);

            float depth = b.MaxY - a.MinY;

            if (depth > MaxSize)
                return false;

            north.SeCorner[1] = south.SeCorner[1];
            north.SwZ = south.SwZ;
            north.SeCorner[2] = south.SeCorner[2];

            return true;
        }
    }
}
