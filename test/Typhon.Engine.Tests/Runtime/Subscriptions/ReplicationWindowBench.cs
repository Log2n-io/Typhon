using NUnit.Framework;
using System;
using System.Diagnostics;
using System.Numerics;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// What a session's window costs per tick, as a function of the cells it spans and the entities in them (10 § 4.3, § 5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <c>ReplicationGrid.MaxWindow = 16</c> caps a flat window at 15 cells per axis, so <c>⌈R / c⌉ ≤ 5</c>. That 16 is the width of a
/// row of <see cref="PushSessionState"/>'s inline window (one <c>ushort</c> per row), <b>not</b> a measured cost threshold — the designed bound is the cell
/// count, <c>W_x · W_y · W_z ≤ 2 809</c>, which allows a flat 53 × 53 window and <c>⌈R / c⌉ ≤ 24</c>. Nothing in
/// <c>claude/design/Subscriptions/08-measurements.md</c> measures window width at all: 10 § 5's 13 µs per anchor move over 1 331 cells is an [EST]. This
/// fixture is that measurement.
/// </para>
/// <para>
/// <b>Three sweeps, because reach alone confounds two costs.</b> Growing R at a fixed cell grows the window AND the entities inside it, so a single sweep
/// cannot say which one is paid:
/// </para>
/// <list type="bullet">
/// <item><b>A — cells only:</b> R fixed at 320 m, c from 160 m down to 20 m. The disc, and so every entity held, is identical in every case; only the
/// window's cell count changes (9² = 81 up to 35² = 1 225 cells). This is the sweep that answers the question.</item>
/// <item><b>B — both, the naive move:</b> c fixed at 64 m, R from 64 m to 320 m. What "just raise the radius" costs.</item>
/// <item><b>C — entities only:</b> c = R / 3, so the window is 11 × 11 cells at every radius while the disc's area grows 64-fold. The per-entity term,
/// with the cell term held still.</item>
/// </list>
/// <para>
/// <b>And three densities, for the same reason.</b> <c>empty</c> holds no entities at all, which prices a cell whose occupancy count is zero — the cheap
/// path (10 § 2.4 skips it); <c>sparse</c> and <c>dense</c> are 128 m and 32 m between point entities, a 16× difference in entities per cell. A cost that
/// is in the cells shows up in <c>empty</c>; a cost that is in the entities does not.
/// </para>
/// <para>
/// <b>The anchor moves, because a still session measures nothing.</b> Static entities and a still anchor leave the gather with an empty log to fold and no
/// cell straddling either sphere: the per-tick cost is a floor that no window width can move, which the <c>still</c> control cases report. The sweeps move
/// the viewpoint 2 m per tick — 20 m/s at the fixtures' 10 Hz, under the one-cell teleport threshold — so the window shifts and the crescent is swept at a
/// realistic cadence rather than once.
/// </para>
/// <para>
/// <b>Manual, and it asserts nothing about time.</b> Timings are reported; what is asserted is that the case measured what it claims to — the session holds
/// the disc, and the arm that should be in steady state is.
/// </para>
/// </remarks>
[TestFixture]
[Explicit("A measurement, not a check: run by hand")]
[Category("Manual")] // It reports µs per tick and asserts nothing about them: a CI box's timing would be read as a result it is not.
[NonParallelizable]
[Property("CacheSize", 256 * 1024 * 1024)]
class ReplicationWindowBench : TestBase<ReplicationWindowBench>
{
    /// <summary>Half the square the fixture populates, in metres: a disc of the largest radius swept by the longest drift stays inside it.</summary>
    private const double PopulatedHalfM = 2048d;

    /// <summary>Ticks of fill before the measurement, ample for every cell a disc reaches to have been delivered at the enter budget below.</summary>
    private const int FillTicks = 40;

    /// <summary>Ticks measured. Long enough that a re-anchor every few ticks is averaged over many of them.</summary>
    private const int MeasuredTicks = 1200;

    /// <summary>The spatial cell the fixtures' own world uses, and what sweep D varies around.</summary>
    private const double SpatialCellM = 256d;

    /// <summary>What a SWG Tatooine session is served: <c>TatooineReplication.PlayerRadiusM</c>.</summary>
    private const double DemoRadiusM = 192d;

    /// <summary>What its profile declares as the slider's ceiling, and so what its window is sized for: <c>PlayerMaxRadiusM</c>.</summary>
    private const double DemoMaxRadiusM = 320d;

    /// <summary>Ticks of the moving path run before the timers start, so the measurement is not the first anchor move and the JIT's.</summary>
    private const int WarmupTicks = 60;

    /// <summary>Metres the viewpoint moves per tick in the moving arms: 20 m/s at the fixtures' 10 Hz, below the one-cell teleport threshold.</summary>
    private const double DriftPerTickM = 2d;

    /// <summary>A player's own speed for comparison: 5 m/s at the fixtures' 10 Hz, the demo's run speed.</summary>
    private const double WalkPerTickM = 0.5d;

    private const string Near = "near";

    // ── Sweep A: the window's cells, with the disc held still ───────────────────────────────────────────────────────────────────────────────────────────
    //
    // R = 320 m throughout, so every case holds the same entities; c shrinks, so the window grows. The last four are refused by MaxWindow today and the
    // case reports that rather than failing, which is what makes this file a before/after of the spike.
    [TestCase(320d, 160d, 32f, TestName = "A-reach-2-dense")]
    [TestCase(320d, 320d / 3d, 32f, TestName = "A-reach-3-dense")]
    [TestCase(320d, 80d, 32f, TestName = "A-reach-4-dense")]
    [TestCase(320d, 64d, 32f, TestName = "A-reach-5-dense")]
    [TestCase(320d, 320d / 6d, 32f, TestName = "A-reach-6-dense")]
    [TestCase(320d, 320d / 7d, 32f, TestName = "A-reach-7-dense")]
    [TestCase(320d, 40d, 32f, TestName = "A-reach-8-dense")]
    [TestCase(320d, 32d, 32f, TestName = "A-reach-10-dense")]
    [TestCase(320d, 320d / 12d, 32f, TestName = "A-reach-12-dense")]
    [TestCase(320d, 20d, 32f, TestName = "A-reach-16-dense")]
    // The same window widths over a world 16× sparser, and over no world at all: what is in the cells rather than in the entities.
    [TestCase(320d, 160d, 128f, TestName = "A-reach-2-sparse")]
    [TestCase(320d, 64d, 128f, TestName = "A-reach-5-sparse")]
    [TestCase(320d, 40d, 128f, TestName = "A-reach-8-sparse")]
    [TestCase(320d, 32d, 128f, TestName = "A-reach-10-sparse")]
    [TestCase(320d, 320d / 12d, 128f, TestName = "A-reach-12-sparse")]
    [TestCase(320d, 20d, 128f, TestName = "A-reach-16-sparse")]
    [TestCase(320d, 160d, 0f, TestName = "A-reach-2-empty")]
    [TestCase(320d, 320d / 3d, 0f, TestName = "A-reach-3-empty")]
    [TestCase(320d, 80d, 0f, TestName = "A-reach-4-empty")]
    [TestCase(320d, 64d, 0f, TestName = "A-reach-5-empty")]
    [TestCase(320d, 320d / 6d, 0f, TestName = "A-reach-6-empty")]
    [TestCase(320d, 40d, 0f, TestName = "A-reach-8-empty")]
    [TestCase(320d, 32d, 0f, TestName = "A-reach-10-empty")]
    [TestCase(320d, 320d / 12d, 0f, TestName = "A-reach-12-empty")]
    [TestCase(320d, 20d, 0f, TestName = "A-reach-16-empty")]
    // ── Sweep B: raising the radius on one cell size, which grows both ──────────────────────────────────────────────────────────────────────────────────
    [TestCase(64d, 64d, 32f, TestName = "B-R-64-c-64")]
    [TestCase(128d, 64d, 32f, TestName = "B-R-128-c-64")]
    [TestCase(192d, 64d, 32f, TestName = "B-R-192-c-64")]
    [TestCase(256d, 64d, 32f, TestName = "B-R-256-c-64")]
    [TestCase(320d, 64d, 32f, TestName = "B-R-320-c-64")]
    [TestCase(512d, 64d, 32f, TestName = "B-R-512-c-64")]
    [TestCase(1024d, 64d, 32f, TestName = "B-R-1024-c-64")]
    // ── Sweep C: the radius with the window held at 11 × 11 cells (c = R / 3) ───────────────────────────────────────────────────────────────────────────
    [TestCase(128d, 128d / 3d, 32f, TestName = "C-R-128-reach-3")]
    [TestCase(256d, 256d / 3d, 32f, TestName = "C-R-256-reach-3")]
    [TestCase(512d, 512d / 3d, 32f, TestName = "C-R-512-reach-3")]
    [TestCase(1024d, 1024d / 3d, 32f, TestName = "C-R-1024-reach-3")]
    public void MovingAnchor(double radius, double cell, float spacing) => Measure(radius, cell, spacing, DriftPerTickM);

    /// <summary>
    /// The same windows at a player's own speed, 5 m/s, rather than a speeder's 20.
    /// </summary>
    /// <remarks>
    /// <b>The anchor slack is <c>min(R / 48, c / 2)</c>, so the re-anchor RATE is a function of the radius</b> — a 1 024 m sphere re-anchors eight times less
    /// often than a 128 m one at the same speed, which partly cancels its larger window. Both readings are needed: the per-tick figure at a fixed speed is
    /// what a server pays, and the per-move figure these rows also carry is the algorithm's own cost.
    /// </remarks>
    [TestCase(320d, 160d, 32f, TestName = "A-walk-reach-2-dense")]
    [TestCase(320d, 64d, 32f, TestName = "A-walk-reach-5-dense")]
    [TestCase(320d, 40d, 32f, TestName = "A-walk-reach-8-dense")]
    [TestCase(320d, 320d / 12d, 32f, TestName = "A-walk-reach-12-dense")]
    [TestCase(1024d, 1024d / 3d, 32f, TestName = "C-walk-R-1024-reach-3")]
    [TestCase(1024d, 64d, 32f, TestName = "B-walk-R-1024-c-64")]
    public void MovingAnchorAtWalkingSpeed(double radius, double cell, float spacing) => Measure(radius, cell, spacing, WalkPerTickM);

    /// <summary>
    /// The floor: static entities and a still anchor, so the gather folds an empty log over cells that straddle nothing.
    /// </summary>
    /// <remarks>
    /// Two window widths, because the point of the control is that the floor does NOT move with them. Whatever these report has to be subtracted from the
    /// moving cases to read the window's own cost.
    /// </remarks>
    [TestCase(320d, 160d, 32f, TestName = "still-reach-2-dense")]
    [TestCase(320d, 64d, 32f, TestName = "still-reach-5-dense")]
    [TestCase(320d, 40d, 32f, TestName = "still-reach-8-dense")]
    [TestCase(320d, 320d / 12d, 32f, TestName = "still-reach-12-dense")]
    [TestCase(320d, 20d, 32f, TestName = "still-reach-16-dense")]
    public void StillAnchor(double radius, double cell, float spacing) => Measure(radius, cell, spacing, 0d);

    /// <summary>
    /// Sweep D: the same session and the same replication cell over spatial grids of four different cell sides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Delivering a replication cell queries the spatial index over that cell's box</b> (10 § 5, <c>QueryBox</c>). When the replication cell is much
    /// smaller than the spatial cell, every such query reaches into a spatial cell holding many times the entities it wants, and a window of small cells
    /// pays that over and over — so the cost of shrinking <c>c</c> would be a property of the two grids' RATIO rather than of the window at all.
    /// </para>
    /// <para>
    /// <b>This is not hypothetical for the demo:</b> SWG Tatooine runs <c>ReplicationCellM = 64</c> on a spatial grid whose cell defaults to 256 m
    /// (<c>SimConfig.ResolveCellSize</c>), exactly the 4:1 ratio the middle of this sweep measures.
    /// </para>
    /// </remarks>
    [TestCase(320d, 64d, 128d, TestName = "D-spatial-128")]
    [TestCase(320d, 64d, 256d, TestName = "D-spatial-256")]
    [TestCase(320d, 64d, 512d, TestName = "D-spatial-512")]
    [TestCase(320d, 160d, 256d, TestName = "D-spatial-256-c-160")]
    public void SpatialCellRatio(double radius, double cell, double spatialCell) =>
        Measure(radius, cell, 32f, DriftPerTickM, spatialCell);

    /// <summary>
    /// Sweep E: the demo's own geometry — a session served 192 m whose profile declares a 320 m maximum — over six replication cells.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The window is sized by the declared MAXIMUM, not the radius in use</b> (09 § 4): <c>⌈R_max / c⌉</c>. So the slider's ceiling is what sets every
    /// human session's window, and the cell side is the only knob that buys a wide ceiling AND a small window at once. These rows hold the demo's served
    /// radius and ceiling fixed and vary only <c>c</c>, which moves the window (225 cells down to 49) and the spatial-to-replication ratio (4:1 down to
    /// 0.8:1) together — the two effects pull in opposite directions, and where they cross is the question.
    /// </para>
    /// <para>
    /// <b>Why 1:1 and below are here:</b> a coarse replication cell means each delivery queries a box the sphere mostly rejects, and at <c>c</c> above the
    /// spatial cell one query straddles several spatial cells. Coarser cannot be monotonically better, so the optimum has to be bracketed rather than
    /// extrapolated. Every case is legal on the shipped engine — no spike involved.
    /// </para>
    /// </remarks>
    [TestCase(64d, TestName = "E-c-64")]
    [TestCase(96d, TestName = "E-c-96")]
    [TestCase(128d, TestName = "E-c-128")]
    [TestCase(192d, TestName = "E-c-192")]
    [TestCase(256d, TestName = "E-c-256")]
    [TestCase(320d, TestName = "E-c-320")]
    public void DemoGeometry(double cell) =>
        Measure(DemoRadiusM, cell, 32f, DriftPerTickM, SpatialCellM, DemoMaxRadiusM);

    /// <summary>
    /// Sweep F: at the cell side sweep E likes, what a MORE generous slider ceiling costs — the window is sized for the ceiling, not the radius in use.
    /// </summary>
    /// <remarks>
    /// The session is still served 192 m in every row. Only the declared maximum changes, and with it <c>⌈R_max / c⌉</c>: a ceiling of <c>5 c</c> is the most
    /// the window bound allows and takes the window back to 225 cells, so "a coarser cell buys a cheaper gather" and "a coarser cell buys a wider slider" are
    /// not the same win and this is whether they can be had together.
    /// </remarks>
    [TestCase(320d, TestName = "F-max-320")]
    [TestCase(640d, TestName = "F-max-640")]
    [TestCase(1280d, TestName = "F-max-1280")]
    public void DemoCeiling(double maxRadius) =>
        Measure(DemoRadiusM, 256d, 32f, DriftPerTickM, SpatialCellM, maxRadius);

    private void Measure(double radius, double cell, float spacing, double drift, double spatialCell = SpatialCellM, double maxRadius = 0d)
    {
        var moving = drift > 0d;

        // The window is sized for the largest radius a session can take, which is the declared maximum when there is one (09 § 4).
        var sized = maxRadius > 0d ? maxRadius : radius;
        var reach = (int)Math.Ceiling(sized / cell);
        var window = (2 * (reach + 2)) + 1;

        // The anchor moves only when the viewpoint has drifted past the slack (10 § 4.1), so this is how often the window shifts and the crescent is swept.
        var slack = Math.Min(radius / 48d, cell / 2d);
        var movesPerTick = moving ? Math.Min(1d, drift / slack) : 0d;
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider, SpatialGridConfig.Flat(
            worldMin: new Vector2(-ProjectionTestSchema.WorldExtentM, -ProjectionTestSchema.WorldExtentM),
            worldMax: new Vector2(ProjectionTestSchema.WorldExtentM, ProjectionTestSchema.WorldExtentM),
            cellSize: (float)spatialCell));
        var entities = spacing > 0 ? Populate(dbe, spacing) : 0;

        FrameHarness harness;
        try
        {
            harness = FrameHarness.Create(dbe, subs =>
            {
                ProjectionTestSchema.DeclareCreature(subs);
                subs.Profile(Near, p => p.Sphere(radius, max: maxRadius).Of<ProjCreature>());
            }, nameof(ReplicationWindowBench), new SubscriptionsOptions
            {
                MaxSessions = 16,
                ReplicationCellM = cell,

                // Ample: a disc of 1 024 m at 32 m spacing is ~3 200 entities, and a fill that was rationed would measure the budget.
                EnterBudgetPerFrame = 8192,
                FramePoolBudgetBytes = 512L * 1024 * 1024,
            });
        }
        catch (InvalidOperationException refused)
        {
            // The window bound. Reported rather than thrown, so one run of the fixture shows exactly where today's cap falls.
            TestContext.Out.WriteLine(
                $"WINDOW,{radius:F0},{cell:F2},{reach},{window},{window * window},{spacing:F0},{entities},{drift:F1},refused,,,,,,,{spatialCell:F0}  # "
                + refused.Message.Split(':')[0]);
            Assert.Ignore($"window {window} cells is past the bound at R = {radius}, c = {cell:F2}");
            return;
        }

        using (harness)
        {
            harness.RunFence = true;
            var session = harness.OpenSessions(1, Near)[0];
            var at = new Vector3D(0d, 0d, 0d);
            Assert.That(harness.Sessions.SetViewpoint(session, at), Is.True, "a just-opened session can be placed");

            for (var t = 1; t <= FillTicks; t++)
            {
                harness.RunTick(harness.Tick + 1);
                harness.Deliver(session);
            }

            var held = harness.Replica(session).NetIds(harness.CatalogPlan.ArchetypeByName(nameof(ProjCreature)).Idx).Length;

            // Warm the moving path before the timers start: the first anchor move of a case delivers cells the fill never reached, and JIT-compiles the
            // sweep. Without this the first case of a run reads high and the ordering of the sweep becomes part of the result.
            for (var t = 0; t < WarmupTicks; t++)
            {
                if (moving)
                {
                    at = new Vector3D(at.X + drift, at.Y, at.Z);
                    harness.Sessions.SetViewpoint(session, at);
                }

                harness.RunTick(harness.Tick + 1);
                harness.Drain(session);
            }

            var flowBefore = harness.Assembler.EnterFlow;

            var timingWas = FrameAssembler.PhaseTimingEnabled;
            FrameAssembler.PhaseTimingEnabled = true;
            double gatherFrom;
            double gatherTo;
            long bytesFrom;
            Stopwatch watch;
            try
            {
                gatherFrom = harness.Assembler.PhaseMilliseconds.Gather;
                bytesFrom = harness.Assembler.BytesEncoded;
                watch = Stopwatch.StartNew();
                for (var t = 0; t < MeasuredTicks; t++)
                {
                    if (moving)
                    {
                        at = new Vector3D(at.X + DriftPerTickM, at.Y, at.Z);
                        harness.Sessions.SetViewpoint(session, at);
                    }

                    harness.RunTick(harness.Tick + 1);
                    harness.Drain(session);
                }

                watch.Stop();
                gatherTo = harness.Assembler.PhaseMilliseconds.Gather;
            }
            finally
            {
                FrameAssembler.PhaseTimingEnabled = timingWas;
            }

            var flow = harness.Assembler.EnterFlow;
            var gatherUs = (gatherTo - gatherFrom) * 1000d / MeasuredTicks;
            var wholeUs = watch.Elapsed.TotalMicroseconds / MeasuredTicks;
            var moved = (flow.Entered - flowBefore.Entered + flow.Left - flowBefore.Left) / (double)MeasuredTicks;
            var bytes = (harness.Assembler.BytesEncoded - bytesFrom) / (double)MeasuredTicks;

            // One CSV row per case, parsed by the sweep's reporting script:
            // R, c, reach, W, cells, spacing, entities, drift m/tick, held, gather µs/tick, whole µs/tick, enters+leaves/tick, bytes/tick,
            // anchor slack m, anchor moves/tick, gather µs per anchor move.
            TestContext.Out.WriteLine(
                $"WINDOW,{radius:F0},{cell:F2},{reach},{window},{window * window},{spacing:F0},{entities},{drift:F1},{held}," +
                $"{gatherUs:F2},{wholeUs:F2},{moved:F2},{bytes:F0},{slack:F2},{movesPerTick:F3}," +
                $"{(movesPerTick > 0 ? (gatherUs / movesPerTick).ToString("F2") : "-")},{spatialCell:F0},{sized:F0}");

            Assert.Multiple(() =>
            {
                Assert.That(held, Is.EqualTo(PointsWithin(0d, 0d, radius, spacing)),
                    "the session must hold exactly the disc, or the case is timing the wrong geometry");
                if (!moving)
                {
                    Assert.That(moved, Is.Zero, "a still anchor over static entities is the floor: nothing should enter or leave");
                }
            });
        }
    }

    /// <summary>A square grid of still point entities, so the count inside a radius is arithmetic rather than a guess.</summary>
    /// <param name="dbe">The engine.</param>
    /// <param name="spacing">Metres between neighbours.</param>
    /// <returns>How many were spawned.</returns>
    /// <remarks>
    /// <b>Speed zero, deliberately.</b> <c>ProjCreature</c> is a motion archetype, and an entity with a declared speed keeps extrapolating — every slot
    /// pushed every tick, which is the projection's cost rather than the window's. Still entities leave the gather folding an empty log, so what the moving
    /// arms measure is the anchor's own work.
    /// </remarks>
    private static int Populate(DatabaseEngine dbe, float spacing)
    {
        var side = (int)(2 * PopulatedHalfM / spacing) + 1;

        // Committed in batches: one transaction over every entity dirties more pages than the cache's backpressure allows once the spatial grid is fine
        // enough to put each of them in its own cluster, and the fixture would then be measuring the page cache's limit instead of a gather.
        const int Batch = 2048;
        for (var from = 0; from < side * side; from += Batch)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = from; i < Math.Min(side * side, from + Batch); i++)
            {
                var x = (float)(-PopulatedHalfM + (i % side * spacing));
                var y = (float)(-PopulatedHalfM + (i / side * spacing));
                tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(new ProjBounds
                {
                    Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = 0f,
                }));
            }

            tx.Commit();
        }

        return side * side;
    }

    /// <summary>How many of the spawned grid points lie within a radius of a centre.</summary>
    private static int PointsWithin(double cx, double cy, double radius, float spacing)
    {
        if (spacing <= 0)
        {
            return 0;
        }

        var side = (int)(2 * PopulatedHalfM / spacing) + 1;
        var inside = 0;
        for (var i = 0; i < side * side; i++)
        {
            var dx = -PopulatedHalfM + (i % side * spacing) - cx;
            var dy = -PopulatedHalfM + (i / side * spacing) - cy;
            if ((dx * dx) + (dy * dy) <= radius * radius)
            {
                inside++;
            }
        }

        return inside;
    }
}
