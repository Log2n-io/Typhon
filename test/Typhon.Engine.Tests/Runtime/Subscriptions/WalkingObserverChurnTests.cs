using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// A walking observer is never told to forget something that is still in front of it, and never stops being told about something it is walking towards.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the instrument the demo's leave audit should have been.</b> That audit classified a leave as "legitimate" when the entity was still in the
/// cluster and slot the session was last told about, and reported that 84 % of leaves were of that kind. But an entity sitting exactly where it always sat is
/// precisely what an observer WALKING AWAY leaves behind — so the statistic cannot separate the engine dropping something it should have kept from a player
/// turning a corner. It answers a question about the entity when the question is about the pair.
/// </para>
/// <para>
/// <b>The test here is geometric and exact.</b> The entities that matter never move, so their positions are known to the metre for the whole run; the
/// observers move on a script, so their viewpoints are known for every tick. A leave is a defect if and only if the entity it names was inside the observer's
/// LEAVE radius on the tick the leave went out, and a missing entity is a defect if and only if it has been inside the ENTER radius long enough for the
/// backlog to have described it. Neither test needs to know anything about runs, masks or views.
/// </para>
/// <para>
/// <b>Both radii are exercised.</b> With no declared band the two radii are equal and an entity on the boundary is admitted and dropped by the same compare;
/// with a band the leave radius is larger, and the blend that implements it reads the session's committed mask — a different code path with a different way
/// of being wrong.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class WalkingObserverChurnTests : TestBase<WalkingObserverChurnTests>
{
    private const double EnterRadius = 192d;

    /// <summary>Metres between pinned entities.</summary>
    private const float PinSpacing = 18f;

    /// <summary>41 x 18 m = 738 m, so a 192 m disc stays wholly inside the lattice across the whole walk.</summary>
    private const int PinColumns = 41;

    private const int Movers = 2500;
    private const float MoverSpread = 700f;
    private const float MoverStepM = 20f;

    /// <summary>Metres the observers advance each tick. Brisk enough to cross several interest cells over the run.</summary>
    private const double ObserverStepM = 3d;

    private const int WarmupTicks = 10;
    private const int MeasuredTicks = 45;

    /// <summary>
    /// Ticks an entity must have been inside the enter radius before the replica is required to hold it.
    /// </summary>
    /// <remarks>
    /// The enter backlog is a real and intended behaviour: an entity that comes into range is described within a frame or two, not instantly. Requiring it
    /// immediately would make this fixture a statement about pacing. Three ticks is far longer than the budget here needs and far shorter than a defect.
    /// </remarks>
    private const int SettleTicks = 3;

    private const byte PinnedTemplate = 7;
    private const byte MoverTemplate = 3;

    private const double Centre = (PinColumns - 1) / 2 * PinSpacing;

    private static ProjBounds PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = 1f };

    /// <summary>A lattice point's key, from any position within half a spacing of it.</summary>
    private static long LatticeKey(double x, double y) =>
        ((long)Math.Round(x / PinSpacing) << 20) ^ (long)Math.Round(y / PinSpacing);

    /// <summary>
    /// Four observers walk across a churning world and the client is never wrong about the entities that are not moving.
    /// </summary>
    /// <param name="cellKeyed">Which broad phase resolves the sessions.</param>
    /// <param name="leaveRadius">The declared leave radius, or <c>0</c> for no hysteresis band.</param>
    /// <param name="workers">
    /// Worker-pool width for the two partitioned stages.
    /// </param>
    /// <remarks>
    /// <b>The worker count is a parameter because every other fixture in the track drives one.</b> The interest stage partitions sessions across workers and
    /// hands cell groups out from a shared cursor, and the frame stage partitions the same index space again; a group that straddled two workers, an arena
    /// range read from the wrong chunk or a view compacted under a neighbour would be invisible at a width of one and is what a live server always runs.
    /// </remarks>
    [Test]
    public void AWalkerIsNeverWrongAboutWhatStandsStill([Values(false, true)] bool cellKeyed, [Values(0, 240)] int leaveRadius,
        [Values(1, 4)] int workers)
    {
        double leave = leaveRadius;
        var effectiveLeave = leave == 0d ? EnterRadius : leave;
        var dbe = ProjectionTestSchema.SetupEngineWithHotRepair(ServiceProvider);
        var random = new Random(20260920);

        var pinned = new List<(float X, float Y)>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var c = 0; c < PinColumns; c++)
            {
                for (var r = 0; r < PinColumns; r++)
                {
                    var x = c * PinSpacing;
                    var y = r * PinSpacing;
                    pinned.Add((x, y));
                    Spawn(tx, x, y, PinnedTemplate);
                }
            }

            for (var i = 0; i < Movers; i++)
            {
                Spawn(tx,
                    (float)(Centre + (((random.NextDouble() * 2d) - 1d) * MoverSpread)),
                    (float)(Centre + (((random.NextDouble() * 2d) - 1d) * MoverSpread)),
                    MoverTemplate);
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        var walkStartX = Centre - (MeasuredTicks * ObserverStepM / 2d);
        var offsets = new[] { (0d, 0d), (12.6d, 7.4d), (25.1d, 19.3d), (38.7d, 31.9d) };

        var options = new SubscriptionsOptions { CellKeyedInterest = cellKeyed, EnterBudgetPerFrame = 8000 };
        using var harness = FrameHarness.Create(dbe, s => Declare(s, leave),
            nameof(AWalkerIsNeverWrongAboutWhatStandsStill) + cellKeyed + leaveRadius + "w" + workers, options);
        var sessions = harness.OpenSessions(offsets.Length, "near");
        var archetype = harness.PlanIndex("ProjCreature");
        Assert.That(archetype, Is.GreaterThanOrEqualTo(0));

        var kind = new Dictionary<uint, bool>[offsets.Length];
        var place = new Dictionary<uint, (double X, double Y)>[offsets.Length];
        for (var s = 0; s < offsets.Length; s++)
        {
            kind[s] = [];
            place[s] = [];
        }

        // The viewpoint history, so a leave can be judged against where its observer actually was on the tick it went out.
        var history = new List<Vector3D[]>();

        var tick = 1L;
        var spurious = 0;
        var missing = 0;
        var pinnedLeaves = 0;
        var pinnedEnters = 0;
        var cellsCrossed = 0;
        var failures = new StringBuilder();
        var cellSide = InterestPass.CellSideFor(effectiveLeave);
        var lastCell = long.MinValue;

        for (var step = 0; step < WarmupTicks + MeasuredTicks; step++)
        {
            var measuring = step >= WarmupTicks;
            var walked = (step - WarmupTicks) * ObserverStepM;
            var x = walkStartX + (measuring ? walked : 0d);

            var viewpoints = new Vector3D[offsets.Length];
            for (var s = 0; s < offsets.Length; s++)
            {
                viewpoints[s] = new Vector3D(x + offsets[s].Item1, Centre + offsets[s].Item2, 0d);
                Assert.That(harness.Sessions.SetViewpoint(sessions[s], viewpoints[s]), Is.True);
            }

            var cell = (long)Math.Floor(viewpoints[0].X / cellSide);
            if (lastCell != long.MinValue && cell != lastCell)
            {
                cellsCrossed++;
            }

            lastCell = cell;
            history.Add(viewpoints);

            DriftMovers(dbe, random);
            dbe.WriteTickFence(++tick);
            harness.RunTick(tick, workers);

            for (var s = 0; s < offsets.Length; s++)
            {
                foreach (var bytes in harness.Collect(sessions[s]))
                {
                    var log = new FrameLog();
                    log.Decode(bytes, harness.CatalogPlan);

                    foreach (var left in log.Leaves)
                    {
                        if (!kind[s].TryGetValue(left, out var wasPinned) || !wasPinned)
                        {
                            continue;
                        }

                        pinnedLeaves++;
                        var (px, py) = place[s][left];
                        var dx = px - viewpoints[s].X;
                        var dy = py - viewpoints[s].Y;
                        var dist = Math.Sqrt((dx * dx) + (dy * dy));
                        if (dist <= effectiveLeave)
                        {
                            spurious++;
                            if (failures.Length < 2500)
                            {
                                failures.AppendLine(
                                    $"  tick {tick} session {s}: told to forget netId {left}, which has never moved and sits {dist:F1} m away — "
                                    + $"inside the {effectiveLeave} m leave radius.");
                            }
                        }
                    }

                    foreach (var entered in log.Enters)
                    {
                        if (kind[s].TryGetValue(entered, out var wasPinned) && wasPinned)
                        {
                            pinnedEnters++;
                        }
                    }

                    harness.Replica(sessions[s]).Apply(bytes);
                }

                // What the replica now holds, and what it believes about each identity. Recorded AFTER the frames are applied, so the next tick's leaves
                // are judged against the state that preceded them.
                var replica = harness.Replica(sessions[s]);
                var heldPinned = new HashSet<long>();
                foreach (var netId in replica.NetIds(archetype))
                {
                    var isPinned = replica.Value(archetype, netId, "template") == PinnedTemplate;
                    kind[s][netId] = isPinned;
                    if (!isPinned)
                    {
                        continue;
                    }

                    var p = replica.Position(archetype, netId);
                    place[s][netId] = (p[0], p[1]);
                    heldPinned.Add(LatticeKey(p[0], p[1]));
                }

                if (!measuring || history.Count <= SettleTicks)
                {
                    continue;
                }

                // Every stationary entity that has been inside the enter radius for the last few ticks must be held. Anything inside the enter radius is
                // admitted unconditionally, band or no band, so this needs no knowledge of the hysteresis history.
                foreach (var (px, py) in pinned)
                {
                    var settled = true;
                    for (var back = 0; back <= SettleTicks && settled; back++)
                    {
                        var v = history[history.Count - 1 - back][s];
                        var dx = px - v.X;
                        var dy = py - v.Y;
                        settled = (dx * dx) + (dy * dy) <= EnterRadius * EnterRadius;
                    }

                    if (settled && !heldPinned.Contains(LatticeKey(px, py)))
                    {
                        missing++;
                        if (failures.Length < 2500)
                        {
                            var dx = px - viewpoints[s].X;
                            var dy = py - viewpoints[s].Y;
                            failures.AppendLine(
                                $"  tick {tick} session {s}: does not hold the entity at ({px}, {py}), {Math.Sqrt((dx * dx) + (dy * dy)):F1} m away, "
                                + $"which has never moved and has been inside the {EnterRadius} m enter radius for {SettleTicks + 1} ticks.");
                        }
                    }
                }
            }
        }

        // ── What the geometry demands ────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        //
        // An observer moving `d` sweeps a lune of about 2Rd out of the back of its disc every tick, so at a known density the number of entities it must
        // drop is arithmetic, and it is the ONLY number a correct engine can produce. Stating it turns "the leave rate looks high" — which is what started
        // this investigation — into a quantity with an expected value, and any future change that invents churn fails here instead of being argued about.
        var density = 1d / (PinSpacing * PinSpacing);
        var predictedLeaves = density * 2d * EnterRadius * ObserverStepM * offsets.Length * (MeasuredTicks - SettleTicks);

        var telemetry = dbe.GetSpatialTelemetry<ProjCreature>();
        TestContext.Out.WriteLine(
            $"cellKeyed={cellKeyed} leaveRadius={effectiveLeave}: {pinned.Count} stationary + {Movers} movers, 4 observers walking "
            + $"{MeasuredTicks * ObserverStepM} m over {MeasuredTicks} ticks across {cellsCrossed} interest-cell boundaries at {workers} worker(s). "
            + $"{pinnedLeaves} leaves named a stationary entity, of which {spurious} were still inside the leave radius. "
            + $"{pinnedEnters} re-entries. {missing} settled entities were not held. {telemetry.TotalMigrations} relocations. "
            + $"Geometry demands about {predictedLeaves:F0} leaves; the engine sent {pinnedLeaves} ({pinnedLeaves / predictedLeaves:F2}x).");

        Assert.Multiple(() =>
        {
            // ANTI-VACUITY: the walk must actually have moved the observers through the world and past cell boundaries, and the partitioner must have run.
            Assert.That(cellsCrossed, Is.GreaterThan(1),
                $"the observers crossed {cellsCrossed} interest-cell boundaries, so the broad phase never re-centred and this run says nothing about what "
                + "happens when it does");

            Assert.That(pinnedLeaves, Is.GreaterThan(0),
                "no stationary entity was ever dropped by any observer, so the walk never left anything behind and the zero below is the zero of a "
                + "fixture whose observers might as well have stood still");

            Assert.That(missing, Is.Zero,
                $"{missing} times an observer did not hold an entity that has never moved and has been well inside its enter radius for several ticks."
                + $"{Environment.NewLine}{failures}");

            // The leave rate is the geometric one, not merely non-zero. An engine inventing churn would show here as a multiple, which is exactly the
            // shape the demo's numbers were read as having.
            Assert.That(pinnedLeaves, Is.LessThan(predictedLeaves * 1.3d),
                $"the engine sent {pinnedLeaves} leaves for entities that never moved, against the {predictedLeaves:F0} the observers' own motion "
                + "accounts for. The excess is churn the geometry does not demand");

            if (leaveRadius == 0)
            {
                // Only without a band. With one, an entity admitted at the enter radius has to travel the width of the band before it leaves, so a window
                // this short holds a large fraction of them in flight and legitimately reports fewer.
                Assert.That(pinnedLeaves, Is.GreaterThan(predictedLeaves * 0.6d),
                    $"the engine sent only {pinnedLeaves} leaves against the {predictedLeaves:F0} the observers' motion demands, so entities the walk "
                    + "left behind are being kept — which is bandwidth spent on things the client cannot see");
            }

            Assert.That(spurious, Is.Zero,
                $"{spurious} retractions named an entity that has never moved and was still inside the observer's leave radius at the moment the "
                + $"retraction went out. Each one costs the client a full re-entry for something it should still have had."
                + $"{Environment.NewLine}{failures}");
        });
    }

    private static void Declare(SubscriptionsRegistry subs, double leaveRadius)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile("near", p => p.Sphere(EnterRadius, leaveRadius).Of<ProjCreature>());
    }

    private static void Spawn(Transaction tx, float x, float y, byte template)
    {
        var bounds = PointAt(x, y);
        var ai = new ProjAi { Template = template, Mode = ProjAiMode.Idle, Level = 1 };
        var vitals = new ProjVitals { Health = 100, MaxHealth = 100 };
        tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in bounds), ProjCreature.Ai.Set(in ai), ProjCreature.Vitals.Set(in vitals));
    }

    /// <summary>Drifts every mover. A pinned entity is identified by the marker it carries, which survives every relocation.</summary>
    /// <param name="dbe">The engine.</param>
    /// <param name="random">The drift source.</param>
    private static void DriftMovers(DatabaseEngine dbe, Random random)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ProjCreature>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var occupancy = cluster.OccupancyBits;
                var ai = cluster.GetReadOnlySpan(ProjCreature.Ai);
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    if (ai[slot].Template == PinnedTemplate)
                    {
                        continue;
                    }

                    ref readonly var b = ref cluster.GetReadOnlySpan(ProjCreature.Bounds)[slot];
                    var x = (float)Clamp(b.Bounds.MinX + (((random.NextDouble() * 2d) - 1d) * MoverStepM));
                    var y = (float)Clamp(b.Bounds.MinY + (((random.NextDouble() * 2d) - 1d) * MoverStepM));
                    cluster.WriteSpatial(ProjCreature.Bounds, slot, PointAt(x, y));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    private static double Clamp(double v)
    {
        var lo = Centre - MoverSpread;
        var hi = Centre + MoverSpread;
        return v < lo ? lo : v > hi ? hi : v;
    }
}
