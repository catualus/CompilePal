using System;
using System.Collections.Generic;

namespace NavPal
{
    /// <summary>
    /// Builds nav areas out of a sampled node grid, following Valve's <c>CreateNavAreasFromNodes</c>.
    ///
    /// The rule that matters, quoted from the source:
    ///
    /// > All of the nodes within the test area must have the same attributes. All of the nodes must be
    /// > approximately co-planar w.r.t the NW node's normal.
    ///
    /// Both halves earn their place. Same attributes keeps a crouch region from being swallowed by the
    /// standing floor around it; co-planarity is what stops one area spanning a staircase or a hillside,
    /// and it is only checkable because nodes carry normals.
    ///
    /// The payoff is the corner heights. An area's four corners are the four **corner nodes** of the
    /// rectangle, so an area sitting on a slope is genuinely sloped.
    ///
    /// **Where this deliberately parts company with Valve.** Their <c>CreateNavAreasFromNodes</c> sweeps
    /// every node looking for somewhere a 50x50 rectangle fits, then shrinks by one and sweeps again,
    /// down to a single cell. That was tried here and measured worse on every count that matters:
    /// gm_construct went from 2,285 areas to 4,433 against Valve's own 2,271, isolated areas from 49 to
    /// 276, ground coverage from 88.0% to 85.4%, and the pass from 2.7 to 22 seconds.
    ///
    /// The reason is the grid underneath, not the algorithm on top. Valve's nodes come from a walk that
    /// links each node to the one it stepped from, so a node almost always sits in a closed cell with
    /// its neighbours; the descending sweep can rely on that. These nodes come from a parallel flood
    /// that samples columns independently and links them afterwards, and 3,566 of gm_construct's 191,577
    /// end up in no closed cell at all. Placing fixed rectangles across a grid that ragged leaves
    /// slivers everywhere, where growing each seed as far as it will go absorbs them. Porting the
    /// consumer faithfully needs the producer ported first - the hull-trace sampler that guarantees the
    /// linkage - and until then this grows greedily and shapes the result afterwards.
    /// </summary>
    public static class NodeAreaBuilder
    {
        /// <summary>
        /// How far a node may sit off the starting corner's plane before the area stops growing.
        /// Valve's `offPlaneTolerance`.
        /// </summary>
        private const float OffPlaneTolerance = 5f;

        /// <summary>
        /// Longest an area may grow along one axis, in sampling steps: Valve's <c>nav_area_max_size</c>,
        /// which is 50 and is the number their own generator starts its descending sweep from.
        ///
        /// This was 52, inferred from the longest area in Valve's finished gm_construct mesh (1,275
        /// units) rather than read off the convar. Two steps does not sound like much, but it is the
        /// difference between a 1,250-unit cap and a 1,300-unit one, and a run in game found six
        /// 1,300x1,300 areas - single quads covering a quarter of a city block.
        /// </summary>
        private const int MaxSteps = 50;

        /// <summary>
        /// How far from square a rectangle may grow.
        ///
        /// Growth alternates between widening and deepening, so on open ground this never binds - the
        /// rectangle stays square by construction. It binds on the slivers, which is the point. A greedy
        /// grower working through nodes in a fixed order carves the big open shapes out first and leaves
        /// one-node-wide channels between them; unchecked, those channels run the length of whatever
        /// they are wedged between, and gm_construct produced one 4,625 units long. Those are the "long
        /// line" areas, and they are why a ladder or a doorway ends up attached to a strip spanning half
        /// the map.
        /// </summary>
        private const float MaxAspect = 4f;

        /// <summary>
        /// Thickness the first tiling pass insists on, in nodes.
        ///
        /// Four, measured. Raising it to five changed almost nothing (2,475 areas against 2,461) and
        /// lowering it cost shape steadily - at one, which is a single greedy pass, the median aspect is
        /// 4.0 and there are 4,015 areas. Four is also where the median longest side lands on Valve's
        /// own 125 units exactly.
        /// </summary>
        private const int LargestSquare = 4;

        public sealed class Result
        {
            public int AreasCreated;
            public int NodesConsumed;
            public int Rejected;
        }

        /// <summary>
        /// Consumes the grid into areas appended to <paramref name="nav"/>.
        ///
        /// Nodes are taken in a fixed order so the same grid always yields the same mesh - a compile
        /// step that produced a different result each run would be worse than useless.
        /// </summary>
        public static Result Build(NavFile nav, NavNodeGrid grid, float stepSize,
            NavProgress? progress = null)
        {
            var result = new Result();

            var ordered = new List<NavNode>(grid.Nodes);
            ordered.Sort((a, b) =>
            {
                int byX = a.Gx.CompareTo(b.Gx);
                if (byX != 0) return byX;

                int byY = a.Gy.CompareTo(b.Gy);
                return byY != 0 ? byY : a.Z.CompareTo(b.Z);
            });

            uint nextId = 1;
            foreach (var existing in nav.Areas)
                nextId = Math.Max(nextId, existing.Id + 1);

            // Chunkiest first. A single greedy pass in scan order carves the biggest rectangle it can
            // out of wherever it starts, and everything after it has to fit the L-shaped remainder -
            // which is why the areas came out elongated as a rule rather than as an exception, at a
            // median of 3.5:1 against Valve's 2.0:1. Refusing to emit anything thinner than the current
            // threshold, and lowering the threshold on each pass, lets the square shapes claim their
            // ground first and leaves only genuinely awkward corners to the thin ones.
            for (int minimum = LargestSquare; minimum >= 1; minimum--)
            {
                // Reported across all the passes together, so the bar runs once from end to end rather
                // than resetting four times.
                double passBase = (LargestSquare - minimum) / (double)LargestSquare;
                int seen = 0;

                foreach (var seed in ordered)
                {
                    progress?.Report(passBase + seen++ / (double)(ordered.Count * LargestSquare));

                    if (seed.IsCovered)
                        continue;

                    var lattice = new Lattice(seed);
                    var (width, depth) = Grow(lattice);

                    if (Math.Min(width, depth) < minimum)
                        continue;

                    var area = MakeArea(lattice, width, depth, stepSize, nextId++);

                    if (area is null)
                    {
                        // Only a failure on the last pass is a failure at all: until then the nodes are
                        // simply being left for a threshold that suits them.
                        if (minimum == 1)
                            result.Rejected++;

                        continue;
                    }

                    nav.Areas.Add(area);
                    result.AreasCreated++;

                    // claim every node the rectangle covers
                    for (int dx = 0; dx < width; dx++)
                    {
                        for (int dy = 0; dy < depth; dy++)
                        {
                            var node = lattice.At(dx, dy);
                            if (node is null) continue;

                            node.AreaIndex = nav.Areas.Count - 1;
                            result.NodesConsumed++;
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// The grid of nodes reachable from one seed, addressed by offset.
        ///
        /// Reached by walking the node links rather than by looking up grid coordinates. Looking up says
        /// "is there a sample there", which is true on the far side of a wall as readily as across an
        /// open floor; walking answers "can you get there", which is the question an area is supposed to
        /// be a claim about. Valve's <c>CreateNavAreasFromNodes</c> walks for the same reason.
        ///
        /// Results are memoised because growing tests the same offsets repeatedly, and a walk costs a
        /// step per unit of offset.
        /// </summary>
        private sealed class Lattice(NavNode seed)
        {
            private readonly Dictionary<(int, int), NavNode?> resolved = new() { [(0, 0)] = seed };
            private readonly Dictionary<(int, int), NavNode?> unfiltered = new() { [(0, 0)] = seed };

            public NavNode Seed { get; } = seed;

            /// <summary>
            /// The node at an offset whether or not it belongs in this area. Used only for corner
            /// heights: an area of W by D nodes physically covers the ground out to the node one step
            /// further on, and that node is where its far edge actually sits - Valve's own areas run
            /// from node[0] to node[width] inclusive, which is why <c>TestArea</c> goes out of its way
            /// to check "the final (x=width) node" after its loop.
            ///
            /// It has to bypass <see cref="Accepts"/> to be any use. The node one step past is
            /// routinely off the seed's plane - on a staircase it is the next tread, a whole step down -
            /// and that is exactly the case whose height must be read rather than assumed.
            /// </summary>
            public NavNode? RawAt(int dx, int dy)
            {
                if (unfiltered.TryGetValue((dx, dy), out var cached))
                    return cached;

                var previous = dx == 0 ? RawAt(0, dy - 1)?.To[NavGeometry.South]
                                       : RawAt(dx - 1, dy)?.To[NavGeometry.East];

                unfiltered[(dx, dy)] = previous;
                return previous;
            }

            public NavNode? At(int dx, int dy)
            {
                if (resolved.TryGetValue((dx, dy), out var cached))
                    return cached;

                // Rows first, then along them: the seed's own column is reached by walking south, and
                // every other node by walking east from the node at the same row.
                var previous = dx == 0 ? At(0, dy - 1)?.To[NavGeometry.South]
                                       : At(dx - 1, dy)?.To[NavGeometry.East];

                var node = previous is not null && Accepts(Seed, previous) ? previous : null;

                resolved[(dx, dy)] = node;
                return node;
            }
        }

        /// <summary>
        /// Grows the largest rectangle of compatible nodes anchored at the seed.
        ///
        /// Extends in whichever direction still admits a full row or column, preferring the one that
        /// keeps the area squarer - long thin slivers path badly and Valve spends a whole later pass
        /// (`SquareUpAreas`) undoing them.
        /// </summary>
        private static (int Width, int Depth) Grow(Lattice lattice)
        {
            int width = 1;
            int depth = 1;

            while (true)
            {
                bool canWiden = Allowed(width + 1, depth) && ColumnFits(lattice, width, depth);
                bool canDeepen = Allowed(width, depth + 1) && RowFits(lattice, width, depth);

                if (!canWiden && !canDeepen)
                    break;

                if (canWiden && (!canDeepen || width <= depth))
                    width++;
                else
                    depth++;
            }

            return (width, depth);
        }

        /// <summary>Whether a rectangle of this shape is one the pass is willing to emit at all.</summary>
        private static bool Allowed(int width, int depth)
            => width <= MaxSteps && depth <= MaxSteps
               && Math.Max(width, depth) <= Math.Min(width, depth) * MaxAspect;

        private static bool ColumnFits(Lattice lattice, int width, int depth)
        {
            for (int dy = 0; dy < depth; dy++)
            {
                if (lattice.At(width, dy) is null)
                    return false;
            }

            return true;
        }

        private static bool RowFits(Lattice lattice, int width, int depth)
        {
            for (int dx = 0; dx < width; dx++)
            {
                if (lattice.At(dx, depth) is null)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Whether a node reached from the seed belongs in the same area as it: still free, carrying the
        /// same attributes, and close enough to the seed's ground plane.
        /// </summary>
        private static bool Accepts(NavNode seed, NavNode node)
            => !node.IsCovered
               && node.Attributes == seed.Attributes
               && seed.DistanceOffPlane(node.Position) <= OffPlaneTolerance;

        /// <summary>
        /// The height for a far corner, given the node it lands on and the last node inside the area.
        ///
        /// Takes the far node's own height only when the two are within a step of each other, so the
        /// area follows a staircase but does not tilt out over a cliff. Nodes stay linked across drops
        /// of up to <c>DeathDrop</c> - that is what makes a drop connection possible - so without this
        /// an area beside a ledge would take its far corner from the ground far below and slope down
        /// into open air. That cost 3.3 points of ground coverage when the far height was read
        /// unconditionally, against the 1.6 it gained on shape.
        /// </summary>
        private static float FarHeight(NavNode? far, NavNode near)
            => far is not null && MathF.Abs(far.Z - near.Z) <= NavConstants.StepHeight ? far.Z : near.Z;

        /// <summary>
        /// Turns a rectangle of nodes into an area, taking each corner height from the corresponding
        /// corner node. This is the whole point of building from nodes rather than cells.
        /// </summary>
        private static NavArea? MakeArea(Lattice lattice, int width, int depth, float stepSize, uint id)
        {
            var nw = lattice.At(0, 0);
            var ne = lattice.At(width - 1, 0);
            var sw = lattice.At(0, depth - 1);
            var se = lattice.At(width - 1, depth - 1);

            if (nw is null || ne is null || sw is null || se is null)
                return null;

            var area = new NavArea { Id = id };

            // The rectangle spans from the NW node to one step past the SE node, so a single node still
            // produces an area a step across rather than a degenerate zero-width one.
            area.NwCorner[0] = nw.Position.X;
            area.NwCorner[1] = nw.Position.Y;
            area.NwCorner[2] = nw.Z;

            area.SeCorner[0] = se.Position.X + stepSize;
            area.SeCorner[1] = se.Position.Y + stepSize;

            // Heights for the three far corners come from the nodes those corners actually land on -
            // one step past the last node the rectangle grew through - not from the last node itself.
            // Taking them from the last node is what made every area a dead flat plate: with all four
            // corners equal there is no slope to express, so a run of stairs came out as one 25-unit
            // plate per tread. Flat plates cannot merge across a step either, since the seam between two
            // of them is a 16-unit cliff rather than a shared edge, which is why a whole flight stayed a
            // row of fragments no matter how the merge was tuned.
            area.SeCorner[2] = FarHeight(lattice.RawAt(width, depth), se);
            area.NeZ = FarHeight(lattice.RawAt(width, 0), ne);
            area.SwZ = FarHeight(lattice.RawAt(0, depth), sw);

            area.AttributeFlags = (int)lattice.Seed.Attributes;

            return area;
        }
    }
}
