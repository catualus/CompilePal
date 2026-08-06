using System;
using System.Collections.Generic;

namespace NavPal
{
    /// <summary>
    /// Adds the step, jump and drop connections between areas that the engine's generator leaves out.
    ///
    /// Two areas can share an edge in plan view and still have no connection recorded, because the
    /// generator only links areas it managed to flood-fill between during sampling. A ledge you can
    /// clearly step down from, a crate you can climb onto, a rooftop reachable from the next roof - all
    /// of these routinely end up unlinked, which is what "misses some jump-ups" looks like from inside
    /// the game.
    ///
    /// Every candidate is confirmed with a real trace before being added. A wrong connection is much
    /// worse than a missing one: a bot walks confidently into a wall or off a fatal drop, whereas a
    /// missing link only makes it take the long way round.
    /// </summary>
    public static class ConnectionBuilder
    {
        /// <summary>How far apart two edges may sit and still count as touching.</summary>
        private const float EdgeGap = 40f;

        /// <summary>Shared edge shorter than this is a corner clip, not a doorway worth linking.</summary>
        private const float MinimumOverlap = 16f;

        /// <summary>How far in from the shared edge the trace endpoints sit, so they are clear of it.</summary>
        private const float Inset = 6f;

        /// <summary>
        /// Heights above each surface at which the crossing must be clear. Ankle height alone would let
        /// a connection through a waist-high railing; head height alone would let one through a low
        /// tunnel mouth that is actually solid at the floor.
        /// </summary>
        private static readonly float[] ClearanceHeights = [8f, 34f, 60f];

        public sealed class Result
        {
            public int Steps;
            public int JumpsUp;
            public int Drops;
            public int Rejected;

            // split by direction so a systematic asymmetry cannot hide behind a single total
            public int UpCandidates, UpRejectedByReach, UpRejectedByTrace;
            public int DownCandidates, DownRejectedByReach, DownRejectedByTrace;

            public int Total => Steps + JumpsUp + Drops;
        }

        public static Result Build(NavFile nav, BspVisibility vis, NavProgress? progress = null)
        {
            var result = new Result();
            var index = new NavGeometry.Index(nav.Areas);

            for (int i = 0; i < nav.Areas.Count; i++)
            {
                progress?.Report(i / (double)Math.Max(1, nav.Areas.Count));

                var area = nav.Areas[i];
                var bounds = NavGeometry.GetBounds(area);

                for (int direction = 0; direction < NavGeometry.DirectionCount; direction++)
                {
                    var existing = new HashSet<uint>(area.Connections[direction]);

                    foreach (int j in CandidatesBeyond(index, bounds, direction))
                    {
                        if (j == i) continue;

                        var other = nav.Areas[j];
                        if (existing.Contains(other.Id))
                            continue;

                        if (!SharedEdge(bounds, NavGeometry.GetBounds(other), direction,
                                out float centreA, out float centreB, out float overlap))
                            continue;

                        if (overlap < MinimumOverlap)
                            continue;

                        var (fromX, fromY) = EdgePoint(bounds, direction, centreA, -Inset);
                        var (toX, toY) = EdgePoint(NavGeometry.GetBounds(other),
                            NavGeometry.Opposite(direction), centreB, -Inset);

                        float fromZ = NavGeometry.SurfaceZ(area, fromX, fromY);
                        float toZ = NavGeometry.SurfaceZ(other, toX, toY);
                        float climb = toZ - fromZ;

                        bool upward = climb > NavConstants.StepHeight;
                        bool downward = climb < -NavConstants.StepHeight;

                        if (upward) result.UpCandidates++;
                        if (downward) result.DownCandidates++;

                        if (!Reachable(climb))
                        {
                            if (upward) result.UpRejectedByReach++;
                            if (downward) result.DownRejectedByReach++;
                            continue;
                        }

                        if (!IsCrossingClear(vis, fromX, fromY, fromZ, toX, toY, toZ))
                        {
                            result.Rejected++;
                            if (upward) result.UpRejectedByTrace++;
                            if (downward) result.DownRejectedByTrace++;
                            continue;
                        }

                        area.Connections[direction].Add(other.Id);
                        existing.Add(other.Id);

                        if (MathF.Abs(climb) <= NavConstants.StepHeight) result.Steps++;
                        else if (climb > 0) result.JumpsUp++;
                        else result.Drops++;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Whether a height change can be crossed at all. Upward is capped by a crouch jump; downward
        /// only by the drop the engine considers survivable.
        /// </summary>
        private static bool Reachable(float climb) =>
            climb >= -NavConstants.DeathDrop && climb <= NavConstants.JumpCrouchHeight;

        /// <summary>Areas in the band just beyond one edge of the footprint.</summary>
        private static IEnumerable<int> CandidatesBeyond(NavGeometry.Index index, NavGeometry.Bounds b, int direction)
            => direction switch
            {
                NavGeometry.North => index.Overlapping(b.MinX, b.MinY - EdgeGap, b.MaxX, b.MinY),
                NavGeometry.South => index.Overlapping(b.MinX, b.MaxY, b.MaxX, b.MaxY + EdgeGap),
                NavGeometry.West => index.Overlapping(b.MinX - EdgeGap, b.MinY, b.MinX, b.MaxY),
                _ => index.Overlapping(b.MaxX, b.MinY, b.MaxX + EdgeGap, b.MaxY),
            };

        /// <summary>
        /// Whether the two footprints actually abut across the given direction, and where along the
        /// shared span the crossing should be tested. The centres come back separately because the two
        /// areas' edges need not be the same length.
        /// </summary>
        private static bool SharedEdge(NavGeometry.Bounds a, NavGeometry.Bounds b, int direction,
            out float centreA, out float centreB, out float overlap)
        {
            centreA = centreB = overlap = 0;

            bool alongY = direction is NavGeometry.North or NavGeometry.South;

            // the facing edges must be within touching distance of each other
            float faceA = direction switch
            {
                NavGeometry.North => a.MinY,
                NavGeometry.South => a.MaxY,
                NavGeometry.West => a.MinX,
                _ => a.MaxX,
            };

            float faceB = direction switch
            {
                NavGeometry.North => b.MaxY,
                NavGeometry.South => b.MinY,
                NavGeometry.West => b.MaxX,
                _ => b.MinX,
            };

            if (MathF.Abs(faceA - faceB) > EdgeGap)
                return false;

            // and they must overlap along the perpendicular axis
            float lowA = alongY ? a.MinX : a.MinY;
            float highA = alongY ? a.MaxX : a.MaxY;
            float lowB = alongY ? b.MinX : b.MinY;
            float highB = alongY ? b.MaxX : b.MaxY;

            float low = MathF.Max(lowA, lowB);
            float high = MathF.Min(highA, highB);
            overlap = high - low;

            if (overlap <= 0)
                return false;

            centreA = centreB = (low + high) / 2f;
            return true;
        }

        /// <summary>
        /// A point inset from the middle of one edge. A negative <paramref name="offset"/> moves inward,
        /// which keeps trace endpoints off the boundary itself where they would land ambiguously.
        /// </summary>
        private static (float X, float Y) EdgePoint(NavGeometry.Bounds b, int direction, float centre, float offset)
            => direction switch
            {
                NavGeometry.North => (centre, b.MinY - offset),
                NavGeometry.South => (centre, b.MaxY + offset),
                NavGeometry.West => (b.MinX - offset, centre),
                _ => (b.MaxX + offset, centre),
            };

        /// <summary>
        /// Whether a walker could actually get between the two points.
        ///
        /// The horizontal tests run at the height of the *higher* surface, not each area's own. Sighting
        /// straight across from the lower surface is wrong for anything but a flat crossing: the line
        /// runs directly into the face of the step, so every jump-up gets rejected while the matching
        /// drop is accepted. That asymmetry is exactly what it looked like - 258 drops found and not one
        /// jump. Movement over a step happens in the space above it, so that is the space to test.
        ///
        /// Both ends then need vertical room to reach that level, or the connection passes under a
        /// ceiling too low to stand up in.
        /// </summary>
        private static bool IsCrossingClear(BspVisibility vis,
            float fromX, float fromY, float fromZ, float toX, float toY, float toZ)
        {
            float high = MathF.Max(fromZ, toZ);

            // Offsets perpendicular to the crossing, spanning a player's width. A single centre line is
            // infinitely thin and slips through gaps a 32 unit wide player cannot fit through; checked
            // against real hull traces in game, centre-only let about 3% of added connections through
            // that a player is actually blocked by.
            float dx = toX - fromX, dy = toY - fromY;
            float length = MathF.Sqrt(dx * dx + dy * dy);

            float sideX = 0, sideY = 0;
            if (length > 0.01f)
            {
                sideX = -dy / length * NavConstants.HalfHumanWidth;
                sideY = dx / length * NavConstants.HalfHumanWidth;
            }

            foreach (float height in ClearanceHeights)
            {
                for (int side = -1; side <= 1; side++)
                {
                    float ox = sideX * side, oy = sideY * side;

                    var a = new BspFile.Vector3(fromX + ox, fromY + oy, high + height);
                    var b = new BspFile.Vector3(toX + ox, toY + oy, high + height);

                    // Whether a body fits through the gap, not whether a bot can see through it -
                    // GenerationMask throughout, matching what Valve traces movement against.
                    if (!vis.IsLineClear(a, b, BspVisibility.GenerationMask))
                        return false;
                }
            }

            float headroom = high + ClearanceHeights[^1];

            if (!vis.IsLineClear(new BspFile.Vector3(fromX, fromY, fromZ + 4f),
                    new BspFile.Vector3(fromX, fromY, headroom), BspVisibility.GenerationMask) ||
                !vis.IsLineClear(new BspFile.Vector3(toX, toY, toZ + 4f),
                    new BspFile.Vector3(toX, toY, headroom), BspVisibility.GenerationMask))
            {
                return false;
            }

            return HasGroundBetween(vis, fromX, fromY, fromZ, toX, toY, toZ);
        }

        /// <summary>
        /// Whether there is floor along the crossing rather than open air.
        ///
        /// Clear line of sight is not enough on its own. Two rooftops forty units apart with a fatal
        /// drop between them have a perfectly clear line, and connecting them tells a bot it can simply
        /// walk across. Probing the ground at the midpoint distinguishes a gap in the mesh - which is
        /// what these connections exist to repair - from a gap in the world.
        /// </summary>
        private static bool HasGroundBetween(BspVisibility vis,
            float fromX, float fromY, float fromZ, float toX, float toY, float toZ)
        {
            float dx = toX - fromX, dy = toY - fromY;
            if (dx * dx + dy * dy <= NavConstants.StepHeight * NavConstants.StepHeight)
                return true; // the areas effectively touch; there is nothing to span

            float midX = (fromX + toX) / 2f;
            float midY = (fromY + toY) / 2f;
            float low = MathF.Min(fromZ, toZ);

            if (!StairMarker.TryFindFloor(vis, midX, midY, MathF.Max(fromZ, toZ) + 8f,
                    NavConstants.DeathDrop, out float groundZ))
            {
                return false;
            }

            return groundZ >= low - NavConstants.StepHeight;
        }
    }
}
