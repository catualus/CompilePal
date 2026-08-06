using System;

namespace NavPal
{
    /// <summary>
    /// Whether a walker could actually get from one sampled point to the one beside it.
    ///
    /// Grid adjacency is not reachability. Two sample points a step apart can sit on opposite sides of a
    /// wall, on the two floors of a doorway's frame, or either side of a railing, and every one of those
    /// reads as "adjacent, similar height, both standable" to a test that only looks at the two ends.
    /// Deciding on the ends alone is what let areas grow straight through vertical geometry: the mesh
    /// followed the floor into the wall because the wall was never consulted.
    /// </summary>
    public static class Traversability
    {
        /// <summary>
        /// Whether a body could actually be dragged from one sample to the next.
        ///
        /// Swept flat at the height of the *higher* surface rather than following the ground between
        /// them. Sloping the sweep with the terrain looks more faithful and is wrong: on a step up, a
        /// sweep starting a step-height above the low side runs directly into the face of the step,
        /// which is the one obstruction a walker is guaranteed to be able to cross.
        /// </summary>
        public static bool CanStep(BspVisibility vis, BspFile.Vector3 from, BspFile.Vector3 to)
        {
            float top = MathF.Max(from.Z, to.Z);

            // Valve's own generation sweep: the NavTraceMins/Maxs box, dragged from here to there
            // against GetGenerationTraceMask. This replaced two separate lines at knee and chest
            // height, which between them proved only that those two heights were clear - a railing,
            // a pipe or a sill sitting between them read as open floor, and anything above chest
            // height but below standing height was never consulted at all. Sweeping the box tests the
            // whole 0..55 span continuously, which is the question "can a body get across" actually
            // asks.
            //
            // Lifted clear of the surface by a whisker so the box's own floor does not count as the
            // obstruction: Valve traces from the node position itself and lets the engine's epsilon
            // handle it, and this has no engine underneath it to do that.
            var a = new BspFile.Vector3(from.X, from.Y, top + 0.5f);
            var b = new BspFile.Vector3(to.X, to.Y, top + 0.5f);

            if (!vis.TryTraceHull(a, b, BspVisibility.NavTraceMins, BspVisibility.NavTraceMaxs,
                    BspVisibility.GenerationMask, out float fraction, out _, out bool startSolid))
            {
                return true;
            }

            return !startSolid && fraction >= 1f;
        }
    }
}
