using CompilePalX.Compiling;
using NavPal;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace CompilePalX.Compilers
{
    /// <summary>
    /// Runs the offline nav mesh passes over the mesh the NAV step produced.
    ///
    /// This does the work the engine either cannot do or does slowly. Ladders it never builds outside
    /// Left 4 Dead; movement connections it leaves incomplete wherever its flood fill did not reach; and
    /// visibility it computes single-threaded in game, which is roughly 88% of the time a full
    /// nav_generate takes. Here all of it happens on the BSP directly, across every core, without
    /// launching the game.
    ///
    /// NavPal is linked rather than shelled out to. Compile Pal publishes self-contained and single-file,
    /// so a sibling navpal.exe would need its own runtime files carried alongside; as a reference it is
    /// simply compiled in. The command line tool still exists and is still the way to test the passes in
    /// isolation - this shares its code, not its process.
    /// </summary>
    class NavPalProcess : CompileProcess
    {
        public NavPalProcess() : base("NAVPAL") { }

        public override void Run(CompileContext context, CancellationToken cancellationToken)
        {
            CompileErrors = [];

            if (!CanRun(context)) return;

            try
            {
                CompilePalLogger.LogLine("\nCompilePal - Nav Mesh Passes", 900);

                if (!File.Exists(context.CopyLocation))
                {
                    LogError($"Could not find {context.CopyLocation}", "NavPal failed");
                    return;
                }

                string navPath = Path.ChangeExtension(context.CopyLocation, ".nav");
                bool generateAreas = GetParameterString().Contains("-generateareas");

                if (!File.Exists(navPath) && !generateAreas)
                {
                    // Nothing to post-process. This is a configuration mistake rather than a failure, so
                    // say what is missing and how to get it rather than throwing. The advice has to
                    // account for the NAV step being unavailable on some games - telling someone to
                    // enable a step their game configuration filters out is worse than saying nothing.
                    var navStep = ConfigurationManager.CompileProcesses
                        .FirstOrDefault(p => p.Name == "NAV");

                    string advice = navStep is { IsCompatible: true }
                        ? "Enable the NAV step, or generate one in game, then run this."
                        : $"The NAV step does not support {GameConfigurationManager.GameConfiguration?.Name}, " +
                          "so generate one in game (nav_generate) and place it beside the BSP.";

                    CompilePalLogger.LogLineColor($"No nav mesh at {navPath}. {advice}",
                        Error.GetSeverityBrush(2));
                    return;
                }

                string parameters = GetParameterString();

                bool doLadders = !parameters.Contains("-noladders");
                bool doMovement = !parameters.Contains("-nomovement");
                bool doVisibility = !parameters.Contains("-novisibility");
                bool compress = !parameters.Contains("-nocompress");
                float maxViewDistance = ReadDistance(parameters);

                // Every core by default, same as the CLI. Explicit so a shared build box, a laptop
                // someone wants to keep using, or several compiles running at once are not each fighting
                // the others for every thread NavPal can grab.
                int? threads = ReadThreads(parameters);
                if (threads is { } t)
                {
                    NavConcurrency.MaxThreads = t;
                    CompilePalLogger.LogLine($"Using {t} thread{(t == 1 ? "" : "s")} (of {Environment.ProcessorCount} available)");
                }

                // Without this, cancelling a compile only stopped things between whole passes -
                // ThrowIfCancellationRequested below only runs between areas/ladders/movement/visibility,
                // never inside one. The visibility trace, several minutes on a large map, kept running in
                // the background on every thread it had regardless, still writing to this same log, while
                // the UI already reported the compile as stopped and let a new one start on top of it.
                NavConcurrency.CancellationToken = cancellationToken;

                var stopwatch = Stopwatch.StartNew();

                var bsp = BspFile.Load(context.CopyLocation);
                var vis = BspVisibility.Load(context.CopyLocation, bsp);
                vis.AttachModels(BspModels.Load(context.CopyLocation, bsp));
                vis.AttachDisplacements(BspDisplacements.Load(context.CopyLocation));

                NavFile nav;
                if (File.Exists(navPath))
                {
                    nav = NavFile.Load(navPath);
                    CompilePalLogger.LogLine($"Loaded {nav.Areas.Count:N0} areas from {Path.GetFileName(navPath)}");
                }
                else
                {
                    nav = new NavFile();
                    CompilePalLogger.LogLine("No existing nav mesh; generating one from scratch.");
                }

                // Set unconditionally, not just when creating a new mesh. This is what the engine checks
                // an existing .nav's stored size against to decide whether it is stale for the BSP it is
                // loading - a compile that changes the BSP without touching this leaves every existing
                // nav permanently mismatched against its own map, since nothing else in this pipeline
                // ever revisits the field. Missed exactly this case: a live 47 MB rp_downtown_meowy.nav
                // whose stored size (62,897,548) no longer matched the BSP CompilePal had just compiled
                // (66,833,360), because loading an existing mesh skipped the line that sets it.
                nav.BspSize = (uint)new FileInfo(context.CopyLocation).Length;

                // Discards whatever mesh was loaded and generates fresh, seeding from player spawns the
                // way the engine's own flood does, instead of adding to what is there. Only meaningful
                // alongside -generateareas; requested without it there would leave every later pass
                // (ladders, movement, visibility) running over an empty mesh, so it is a no-op with a
                // warning in that case rather than a silent trap.
                if (parameters.Contains("-scratch"))
                {
                    if (generateAreas)
                    {
                        nav.Areas.Clear();
                        nav.Ladders.Clear();
                        CompilePalLogger.LogLine(
                            "Discarding the existing nav mesh - generating fresh from player spawns.");
                    }
                    else
                    {
                        CompilePalLogger.LogLineColor(
                            "\"Start from scratch\" has no effect without \"Generate missing areas\" enabled; ignored.",
                            Error.GetSeverityBrush(2));
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();

                var progress = BuildProgress(generateAreas, doLadders, doMovement, doVisibility, compress);

                // Areas first. Ladders and connections both attach to areas, so anything generated here
                // is available to them - which is also why ladders whose base had no area now find one.
                if (generateAreas)
                    RunAreas(nav, vis, bsp, progress);

                cancellationToken.ThrowIfCancellationRequested();

                if (doLadders)
                {
                    progress.Enter(PhaseLadders);
                    RunLadders(nav, vis, bsp);
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (doMovement)
                    RunConnections(nav, vis, progress);

                cancellationToken.ThrowIfCancellationRequested();

                // After the connection graph exists, never before it - see ClipToGeometry. Pulling an
                // area's edge off a wall moves it away from whatever used to abut it, and the connection
                // pass has only the edges to go on.
                if (generateAreas)
                {
                    var trimmed = AreaGenerator.ClipToGeometry(nav, vis, progress);
                    if (trimmed.Clipped > 0)
                    {
                        CompilePalLogger.LogLine(
                            $"Clipped: {trimmed.Clipped:N0} areas pulled back to geometry " +
                            $"({trimmed.Reclaimed / 1024f:N0}k sq units out of walls)" +
                            (trimmed.Discarded > 0
                                ? $", discarded {trimmed.Discarded:N0} left too narrow to walk"
                                : ""));
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Everything that reads an area's final shape goes after clipping, not before it.
                // Marking stairs is the one that shows: the test probes the real floor along six lines
                // across an area, and an area still overhanging the geometry it is about to be pulled
                // back from sends those probes off the end of the flight, where they find no floor and
                // veto it. Running this before the clip marked 8 areas on gm_construct against 17 after
                // it - and 17 is what the CLI has been reporting all along, which is exactly the sort of
                // discrepancy between the two entry points that should not exist.
                if (doMovement)
                    RunPostClipMovement(nav, vis, bsp, progress);

                cancellationToken.ThrowIfCancellationRequested();

                // Cover positions for bots. Cheap next to everything around it - tens of milliseconds on
                // a full map - and the engine computes them during nav_generate, so a mesh without them
                // is missing something the game expects to find.
                if (generateAreas)
                {
                    var spots = HidingSpotFinder.Find(nav, vis);
                    if (spots.Spots > 0)
                    {
                        CompilePalLogger.LogLine(
                            $"Hiding spots: {spots.Spots:N0} across {spots.AreasWithSpots:N0} areas " +
                            $"({spots.InCover:N0} in cover, {spots.Exposed:N0} exposed)");
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (doVisibility)
                    RunVisibility(nav, vis, maxViewDistance, compress, progress);

                progress.Finish();

                // Write via a temp file and move: a half-written nav is worse than the old one, and a
                // cancel or a crash mid-save would otherwise leave exactly that.
                string temporary = navPath + ".tmp";
                nav.Save(temporary);
                File.Move(temporary, navPath, overwrite: true);

                stopwatch.Stop();

                CompilePalLogger.LogLine(
                    $"Wrote {Path.GetFileName(navPath)} ({new FileInfo(navPath).Length:N0} bytes) " +
                    $"in {stopwatch.Elapsed.TotalSeconds:F1}s");
            }
            catch (OperationCanceledException)
            {
                CompilePalLogger.LogLine("NavPal cancelled.");
            }
            catch (Exception e)
            {
                ExceptionHandler.LogException(e, false);
                LogError(e.Message, "NavPal failed");
            }
            finally
            {
                // Reset rather than leave this compile's token set: NavConcurrency is process-wide state,
                // so anything else that reads it afterwards - another compile step, the CLI hosted in the
                // same process - must not inherit an already-cancelled or otherwise unrelated token.
                NavConcurrency.CancellationToken = CancellationToken.None;
            }
        }

        private const string PhaseSampling = "Sampling walkable space";
        private const string PhaseLadders = "Building ladders";
        private const string PhaseStairs = "Marking stairs";
        private const string PhaseConnections = "Connecting areas";
        private const string PhaseVisibility = "Tracing area visibility";
        private const string PhaseCompress = "Compressing visibility";

        /// <summary>
        /// Wires NavPal's phase reporting to Compile Pal's progress bar and log.
        ///
        /// Two audiences with opposite needs. The bar and taskbar want continuous movement, because this
        /// is comfortably the longest step in a compile and a still bar reads as a hang. The log wants
        /// almost nothing: a line when the phase changes, and a heartbeat slow enough that a ten minute
        /// visibility pass leaves a handful of lines rather than several thousand.
        ///
        /// Weights are shares of a typical run. Visibility dominates by a wide margin - it is roughly
        /// 88% of what a full in-game nav_generate spends its time on - so an even split would park the
        /// bar early and leave it there.
        /// </summary>
        private static NavProgress BuildProgress(bool areas, bool ladders, bool movement,
            bool visibility, bool compress)
        {
            var steps = new List<NavProgress.Step>();

            if (areas)
            {
                steps.Add(new NavProgress.Step(PhaseSampling, 0.10));
                steps.Add(new NavProgress.Step("Linking samples", 0.03));
                steps.Add(new NavProgress.Step("Building areas", 0.03));
                steps.Add(new NavProgress.Step("Merging areas", 0.01));
            }

            if (ladders) steps.Add(new NavProgress.Step(PhaseLadders, 0.01));
            if (movement) steps.Add(new NavProgress.Step(PhaseConnections, 0.05));

            // Declared in the order they are actually entered, which is what the phase counter counts.
            // Clipping runs after connections exist, and stair marking after clipping - listing stairs
            // before it, as this did, left the bar reporting "[8/10]" then "[11/11]" as it tried to
            // reconcile a phase arriving out of order.
            if (areas) steps.Add(new NavProgress.Step("Clipping areas to geometry", 0.02));
            if (movement) steps.Add(new NavProgress.Step(PhaseStairs, 0.02));
            if (visibility) steps.Add(new NavProgress.Step(PhaseVisibility, 0.68));
            if (visibility && compress) steps.Add(new NavProgress.Step(PhaseCompress, 0.05));

            string lastPhase = "";
            var lastBeat = TimeSpan.MinValue;

            return new NavProgress(update =>
            {
                ReportStepProgress(update.Overall);

                bool changed = !string.Equals(update.Phase, lastPhase, StringComparison.Ordinal);
                if (!changed && (update.Elapsed - lastBeat).TotalSeconds < 15)
                    return;

                lastPhase = update.Phase;
                lastBeat = update.Elapsed;

                string detail = update.Fraction is { } f
                    ? $"{f * 100:F0}%"
                    : $"{update.Count:N0} cells";

                CompilePalLogger.LogLine(
                    $"[{update.Index}/{update.Total}] {update.Phase} - {detail}");
            }, steps);
        }

        private static void RunAreas(NavFile nav, BspVisibility vis, BspFile bsp, NavProgress progress)
        {
            progress.Enter(PhaseSampling);
            var result = AreaGenerator.Generate(nav, vis, bsp, progress: progress);

            CompilePalLogger.LogLine(
                $"Areas: {result.AreasAdded:N0} added, flooded from {result.Seeds:N0} seeds " +
                $"({result.Visited:N0} cells visited)");

            foreach (string note in result.Notes)
                CompilePalLogger.LogLine($"       {note}");

            if (result.AreasAdded > 0)
            {
                CompilePalLogger.LogLineColor(
                    "       Generated areas are experimental and unverified in game - review before shipping.",
                    Error.GetSeverityBrush(2));
            }
        }

        private static void RunLadders(NavFile nav, BspVisibility vis, BspFile bsp)
        {
            var brushes = LadderFinder.Find(bsp);
            if (brushes.Count == 0)
            {
                CompilePalLogger.LogLine("Ladders: none found in the BSP");
                return;
            }

            // The tracer is what stops a ladder wiring itself to whatever is nearest through a wall.
            var result = LadderBuilder.Build(nav, brushes, vis);
            CompilePalLogger.LogLine(
                $"Ladders: {result.LaddersAdded:N0} added from {brushes.Count:N0} brushes " +
                $"({result.BottomConnected:N0} bottom, {result.TopConnected:N0} top connections)");

            if (result.Unresolved > 0)
                CompilePalLogger.LogLine($"         {result.Unresolved:N0} skipped - no nav area at the base");
        }

        /// <summary>
        /// The half of the movement work that has to happen before areas are clipped: building the
        /// connection graph, and folding away the jump areas that graph identifies.
        /// </summary>
        private static void RunConnections(NavFile nav, BspVisibility vis, NavProgress progress)
        {
            progress.Enter(PhaseConnections);
            var links = ConnectionBuilder.Build(nav, vis, progress);
            CompilePalLogger.LogLine(
                $"Connections: {links.Steps:N0} steps, {links.JumpsUp:N0} jumps up, {links.Drops:N0} drops " +
                $"({links.Rejected:N0} rejected by trace)");

            // Right after connections, not before: stitching needs to know what each jump area joined,
            // which only exists once ConnectionBuilder has run. This was previously only reachable
            // through the CLI's own build-areas command - CompilePal's own compile path never called it
            // at all, so every jump area a real compile generated stayed in the final mesh permanently
            // un-stitched and, since AreaClipper deliberately skips clipping them, un-clipped too. That
            // is what "jump-up navmesh generates to the side, not where the top actually is" was: the
            // steep face geometry a jump area exists to represent, sitting exactly where the raw node
            // sample put it, never bridged into a connection and never folded away.
            var stitched = JumpAreaStitcher.Stitch(nav);
            if (stitched.JumpAreas > 0)
            {
                CompilePalLogger.LogLine(
                    $"Jump areas: {stitched.JumpAreas:N0} stitched out, " +
                    $"{stitched.ConnectionsAdded:N0} connections bridged across them");
            }

        }

        /// <summary>
        /// The half that has to happen after clipping, because every one of these passes reads an area's
        /// final shape: where its edges are, which corners are isolated, what the floor under it does.
        ///
        /// Ordered as in <c>CreateNavAreasFromNodes</c>: the stair marking comes after the jump areas
        /// have been stitched away, since those are steep stair-shaped fragments that exist only to be
        /// deleted and testing them first marks a pile of them.
        /// </summary>
        private static void RunPostClipMovement(NavFile nav, BspVisibility vis, BspFile bsp,
            NavProgress progress)
        {
            progress.Enter(PhaseStairs);
            var stairs = StairMarker.Mark(nav, vis, progress);
            CompilePalLogger.LogLine($"Stairs: {stairs.Marked:N0} marked, {stairs.Cleared:N0} cleared");

            var elevators = ElevatorConnector.Build(nav, bsp);
            if (elevators.Platforms > 0)
            {
                CompilePalLogger.LogLine(
                    $"Lifts: {elevators.Platforms:N0} platforms, {elevators.Connections:N0} connections " +
                    $"at {elevators.Stops:N0} stops");

                foreach (string note in elevators.Notes)
                    CompilePalLogger.LogLine($"       {note}");
            }

            // Corner patching before the shortcut fixup, matching Valve's own FixUpGeneratedAreas order:
            // it needs the connection graph above to know which corners are isolated, and it adds new
            // connections the shortcut pass should then be free to prune if they turn out redundant.
            var patched = CornerPatcher.Patch(nav, vis);
            if (patched.PatchesAdded > 0)
                CompilePalLogger.LogLine($"Corners: {patched.PatchesAdded:N0} corner-only touches patched");

            var fixup = AreaConnectionFixer.Fix(nav);
            if (fixup.ShortcutsRemoved > 0)
                CompilePalLogger.LogLine($"Fixup: {fixup.ShortcutsRemoved:N0} redundant shortcuts removed");
        }

        private static void RunVisibility(NavFile nav, BspVisibility vis, float maxViewDistance,
            bool compress, NavProgress progress)
        {
            if (!vis.HasVisibilityData)
            {
                CompilePalLogger.LogLineColor(
                    "Visibility: the BSP has no vis data, so nothing can be culled before tracing. " +
                    "Did VVIS run?", Error.GetSeverityBrush(2));
            }

            progress.Enter(PhaseVisibility);

            var filter = new VisibilityFilter(nav, vis, maxViewDistance);
            var tracer = new VisibilityTracer(filter, vis, nav.Areas.Count);
            var stats = filter.Run(tracer, progress);

            CompilePalLogger.LogLine(
                $"Visibility: {stats.TotalPairs:N0} pairs -> {stats.AfterDistance:N0} after distance " +
                $"-> {stats.AfterPvs:N0} after PVS");
            CompilePalLogger.LogLine(
                $"            {tracer.VisibleLinks:N0} visible links from {tracer.RaysCast:N0} rays " +
                $"in {stats.ElapsedMilliseconds:N0} ms on {NavConcurrency.MaxThreads} threads");

            var visible = tracer.Symmetrise();
            foreach (var ids in visible)
                Array.Sort(ids);

            if (compress)
            {
                progress.Enter(PhaseCompress);
                var compression = VisibilityCompressor.Apply(nav, visible, progress);
                CompilePalLogger.LogLine(
                    $"            compressed {compression.Compressed:N0} areas, " +
                    $"{compression.EntriesBefore:N0} -> {compression.EntriesAfter:N0} entries");
            }
            else
            {
                for (int i = 0; i < nav.Areas.Count; i++)
                {
                    var area = nav.Areas[i];
                    area.VisibleAreas.Clear();
                    area.InheritVisibilityFrom = 0;

                    foreach (int j in visible[i])
                        area.VisibleAreas.Add(new VisibleArea { AreaId = nav.Areas[j].Id, Attributes = 1 });
                }
            }

            nav.IsAnalyzed = true;
        }

        /// <summary>
        /// Reads the max view distance from the parameter string, falling back to the engine's own
        /// default when it is absent or unparseable.
        /// </summary>
        private static float ReadDistance(string parameters)
        {
            var match = System.Text.RegularExpressions.Regex.Match(parameters, @"-maxviewdistance\s+(\d+(?:\.\d+)?)");

            return match.Success &&
                   float.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture,
                       out float distance)
                ? distance
                : VisibilityFilter.DefaultMaxViewDistance;
        }

        /// <summary>Null when the argument is absent, so the caller can tell "use the default" apart
        /// from "was set to something".</summary>
        private static int? ReadThreads(string parameters)
        {
            var match = System.Text.RegularExpressions.Regex.Match(parameters, @"-threads\s+(\d+)");

            return match.Success && int.TryParse(match.Groups[1].Value, out int threads) && threads > 0
                ? threads
                : null;
        }

        private void LogError(string message, string title)
        {
            CompilePalLogger.LogCompileError($"{message}\n", new Error(message, title, ErrorSeverity.Error));
        }
    }
}
