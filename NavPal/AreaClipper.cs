using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NavPal
{
    /// <summary>
    /// Pulls each area's trailing edges back to the geometry that actually stops them.
    ///
    /// An area is built from the sampled nodes it covers, and it runs one sampling step past the last of
    /// them - otherwise a single node would describe a rectangle of no size at all. Where the next step
    /// is more floor that is harmless, because the neighbouring area starts exactly there. Where the next
    /// step is a wall, the area ends up inside it, by anything from nothing to a full 25 units depending
    /// on where the wall happens to fall between two samples. That is the "nav follows the floor into the
    /// wall" effect: the mesh is not tracking geometry at the boundary, it is tracking the sample grid.
    ///
    /// Measured on gm_construct, 3.2% of generated areas had part of their footprint in solid against
    /// 0.5% of Valve's own mesh, and every one of the worst offenders was a lone 25x25 area with its node
    /// hard against a wall.
    ///
    /// Only the east and south edges need this. North and west sit exactly on a node, so they can be
    /// short of a wall but never past it; extending those outward would be a coverage improvement and a
    /// different change, and it would overlap the neighbour whose own trailing edge already ends there.
    /// </summary>
    public static class AreaClipper
    {
        /// <summary>
        /// Height above the surface at which the edge is probed for obstruction.
        ///
        /// Knee height, matching the low probe used to decide whether two samples are connected at all.
        /// High enough to clear the floor and anything lying flush on it, low enough that a kerb, a
        /// crate or a railing at the edge of a floor reads as the boundary it is.
        /// </summary>
        private const float ProbeHeight = NavConstants.StepHeight;

        /// <summary>
        /// Samples taken across the edge being clipped. The result is their median, so a doorway or a
        /// gap along an otherwise solid edge does not drag the whole edge out to meet it, and one
        /// stray reading cannot pull it in.
        /// </summary>
        private const int Samples = 5;

        /// <summary>
        /// Smallest overhang left behind, whatever the trace says.
        ///
        /// A node can sit arbitrarily close to a wall, and clipping to the wall exactly would leave a
        /// zero-width area - a quad the format can hold and nothing can path across. Four units is small
        /// enough to be invisible next to the 25 it replaces and large enough to keep the area real.
        /// </summary>
        private const float MinimumOverhang = 4f;

        public sealed class Result
        {
            public int Clipped;
            public float Reclaimed;
        }

        public static Result Clip(NavFile nav, BspVisibility vis, float stepSize,
            NavProgress? progress = null)
        {
            var result = new Result();

            int clipped = 0, done = 0;
            double reclaimed = 0;
            double total = Math.Max(1, nav.Areas.Count);
            object gate = new();

            // Each area is clipped independently against read-only geometry, so this parallelises
            // cleanly; only the two counters need guarding.
            Parallel.ForEach(nav.Areas, NavConcurrency.Options, area =>
            {
                progress?.Report(System.Threading.Interlocked.Increment(ref done) / total);

                // Jump areas stand in for ground too steep to walk on, so they sit on the very face a
                // horizontal probe is bound to hit. Clipping them against it would delete the thing
                // they exist to represent.
                if (((NavAttributes)area.AttributeFlags & NavAttributes.Jump) != 0)
                    return;

                float before = Extent(area);

                bool east = ClipEast(area, vis, stepSize);
                bool south = ClipSouth(area, vis, stepSize);

                if (!east && !south)
                    return;

                float shrunk = before - Extent(area);

                lock (gate)
                {
                    clipped++;
                    reclaimed += shrunk;
                }
            });

            result.Clipped = clipped;
            result.Reclaimed = (float)reclaimed;
            return result;
        }

        private static float Extent(NavArea area)
        {
            var b = NavGeometry.GetBounds(area);
            return b.Width * b.Depth;
        }

        private static bool ClipEast(NavArea area, BspVisibility vis, float stepSize)
        {
            var b = NavGeometry.GetBounds(area);
            if (b.Width <= MinimumOverhang || b.Depth <= 0.01f)
                return false;

            float from = b.MaxX - stepSize;
            var distances = new List<float>(Samples);

            for (int i = 0; i < Samples; i++)
            {
                float y = b.MinY + (i + 0.5f) / Samples * b.Depth;

                // The area's own corner heights come from real samples, so its interpolated surface is
                // a good enough model of the ground to probe above. Taking the higher of the two ends
                // keeps a ray over a rising slope above the slope rather than into it.
                float z = MathF.Max(NavGeometry.SurfaceZ(area, from, y), NavGeometry.SurfaceZ(area, b.MaxX, y));

                distances.Add(Reach(vis, from, y, b.MaxX, y, z, stepSize));
            }

            float keep = MathF.Max(Median(distances), MinimumOverhang);
            if (keep >= stepSize - 0.5f)
                return false;

            SetMaxX(area, from + keep);
            return true;
        }

        private static bool ClipSouth(NavArea area, BspVisibility vis, float stepSize)
        {
            var b = NavGeometry.GetBounds(area);
            if (b.Depth <= MinimumOverhang || b.Width <= 0.01f)
                return false;

            float from = b.MaxY - stepSize;
            var distances = new List<float>(Samples);

            for (int i = 0; i < Samples; i++)
            {
                float x = b.MinX + (i + 0.5f) / Samples * b.Width;
                float z = MathF.Max(NavGeometry.SurfaceZ(area, x, from), NavGeometry.SurfaceZ(area, x, b.MaxY));

                distances.Add(Reach(vis, x, from, x, b.MaxY, z, stepSize));
            }

            float keep = MathF.Max(Median(distances), MinimumOverhang);
            if (keep >= stepSize - 0.5f)
                return false;

            SetMaxY(area, from + keep);
            return true;
        }

        /// <summary>
        /// How far along the edge's outward run the world stays open, up to the full step.
        /// </summary>
        private static float Reach(BspVisibility vis, float x0, float y0, float x1, float y1, float z,
            float stepSize)
        {
            var a = new BspFile.Vector3(x0, y0, z + ProbeHeight);
            var b = new BspFile.Vector3(x1, y1, z + ProbeHeight);

            // GenerationMask: an area should be clipped back at anything a player's body cannot pass,
            // which includes grates and windows the sight mask lets straight through.
            if (!vis.TryTraceSurface(a, b, BspVisibility.GenerationMask, out var hit, out _))
                return stepSize;

            float dx = hit.X - x0;
            float dy = hit.Y - y0;

            return Math.Clamp(MathF.Sqrt(dx * dx + dy * dy), 0f, stepSize);
        }

        private static float Median(List<float> values)
        {
            values.Sort();
            return values[values.Count / 2];
        }

        /// <summary>
        /// Moves the eastern boundary, keeping the corner heights consistent by re-reading the surface
        /// the area already describes at the new position rather than carrying the old corner values
        /// across - a shortened area on a slope would otherwise claim the height of ground it no
        /// longer covers.
        /// </summary>
        private static void SetMaxX(NavArea area, float x)
        {
            bool seIsMax = area.SeCorner[0] >= area.NwCorner[0];

            float ne = NavGeometry.SurfaceZ(area, x, area.NwCorner[1]);
            float se = NavGeometry.SurfaceZ(area, x, area.SeCorner[1]);

            if (seIsMax)
            {
                area.SeCorner[0] = x;
                area.NeZ = ne;
                area.SeCorner[2] = se;
            }
            else
            {
                area.NwCorner[0] = x;
                area.NwCorner[2] = ne;
                area.SwZ = se;
            }
        }

        private static void SetMaxY(NavArea area, float y)
        {
            bool seIsMax = area.SeCorner[1] >= area.NwCorner[1];

            float sw = NavGeometry.SurfaceZ(area, area.NwCorner[0], y);
            float se = NavGeometry.SurfaceZ(area, area.SeCorner[0], y);

            if (seIsMax)
            {
                area.SeCorner[1] = y;
                area.SwZ = sw;
                area.SeCorner[2] = se;
            }
            else
            {
                area.NwCorner[1] = y;
                area.NwCorner[2] = sw;
                area.NeZ = se;
            }
        }
    }
}
