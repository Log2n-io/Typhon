using NUnit.Framework;
using System;
using System.Numerics;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// The differential oracle: seeded churn, decoded clients, and a comparison against the ECS whenever the world is quiet.
/// </summary>
/// <remarks>
/// <para>
/// Spawns, destroys, sub-tolerance drifts, teleports through <c>WriteSpatial</c> that really migrate, and writes to both change groups through
/// <c>GetSpan</c>. In <see cref="PushDetection.Automatic"/> the engine compares every live entity; in <see cref="PushDetection.Explicit"/> the workload
/// pushes each slot it writes, and the engine adds spawns, destroys, spatial writes and migrations itself.
/// </para>
/// <para>
/// Sessions either sit at the origin with a disc that covers the world, so the truth set is every live entity, or walk with a smaller disc and are
/// compared against it. A skipped session is caught up from the push log while its missed ticks are in it, and reset otherwise; converging after
/// either is the claim the skip rates test.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
sealed class PushOracleTests : TestBase<PushOracleTests>
{
    private const int GateTicks = 200;
    private const int CompareEvery = 50;

    /// <summary>A client's world is the server's after seeded churn, in both detection modes and at four delivery rates.</summary>
    /// <param name="detection">Who signals a change.</param>
    /// <param name="skipPercent">The percentage of ticks on which the session's frames are left undrained.</param>
    [Test]
    [VerifiesRule("SUB-03")]
    [VerifiesRule("SUB-06")]
    [VerifiesRule("SUB-10")]
    public void AClientsWorldIsTheServersUnderPush(
        [Values(PushDetection.Explicit, PushDetection.Automatic)] PushDetection detection,
        [Values(0, 30, 60, 90)] int skipPercent)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 20260923 + skipPercent, [skipPercent],
            nameof(PushOracleTests), detection: detection);

        for (var i = 0; i < GateTicks; i++)
        {
            oracle.Step();
            if ((i + 1) % CompareEvery == 0)
            {
                oracle.Quiesce();
                oracle.AssertConverged($"{detection} push, after {i + 1} churned ticks at a {skipPercent}% skip rate");
            }
        }

        oracle.Quiesce();
        oracle.AssertConverged($"{detection} push, at the end of a {GateTicks}-tick run at a {skipPercent}% skip rate");

        Assert.That(oracle.Workload.Destroyed, Is.GreaterThan(10), "the workload destroyed too little to put any pressure on identity");
        Assert.That(oracle.Workload.Teleports, Is.GreaterThan(10), "the workload never forced a motion epoch change");
        if (detection == PushDetection.Explicit)
        {
            Assert.That(oracle.Workload.Pushed, Is.GreaterThan(20), "the workload pushed too little for explicit detection to have been exercised");
        }

        AssertLookedAtSomething(oracle);
    }

    /// <summary>The same gate case through a push <c>World</c> observer: cells delivered in grid order behind one cursor, then every event.</summary>
    /// <param name="detection">Who signals a change.</param>
    /// <param name="skipPercent">The percentage of ticks on which the session's frames are left undrained.</param>
    [Test]
    public void AClientsWorldIsTheServersUnderAPushWorldObserver(
        [Values(PushDetection.Explicit, PushDetection.Automatic)] PushDetection detection,
        [Values(0, 30, 60, 90)] int skipPercent)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 20260924 + skipPercent, [skipPercent, 0],
            nameof(PushOracleTests), detection: detection, worldObserver: true);

        for (var i = 0; i < GateTicks; i++)
        {
            oracle.Step();
            if ((i + 1) % CompareEvery == 0)
            {
                oracle.Quiesce();
                oracle.AssertConverged($"{detection} push World, after {i + 1} churned ticks at a {skipPercent}% skip rate");
            }
        }

        Assert.That(oracle.Workload.Destroyed, Is.GreaterThan(10), "the workload destroyed too little to put any pressure on identity");
        if (skipPercent >= 60)
        {
            Assert.That(oracle.Push.LogCatchUps, Is.GreaterThan(0), "no skipped frame was replayed from the log");
        }

        AssertLookedAtSomething(oracle);
    }

    /// <summary>A profile served one tick in two or four converges, every frame carrying the union since the session's last one (the push log).</summary>
    /// <param name="every">The profile's tick divisor.</param>
    /// <param name="world">A World observer rather than a covering disc.</param>
    [Test]
    [VerifiesRule("SUB-03")]
    public void AProfileServedEveryFewTicksConverges([Values(2, 4)] int every, [Values(false, true)] bool world)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 5150 + every, [0, 30, 0], nameof(PushOracleTests),
            detection: PushDetection.Explicit, worldObserver: world, every: every);

        for (var i = 0; i < GateTicks; i++)
        {
            oracle.Step();
            if ((i + 1) % CompareEvery == 0)
            {
                oracle.Quiesce();
                oracle.AssertConverged($"a profile served every {every} ticks, after {i + 1} churned ticks");
            }
        }

        Assert.That(oracle.Push.LogCatchUps, Is.GreaterThan(GateTicks / every), "the rate class never replayed the log");
        AssertLookedAtSomething(oracle);
    }

    /// <summary>
    /// Distance LOD: updates to far entities deferred and flushed every few ticks as a union — walking sessions still hold exactly their disc, with the
    /// right values, once the world is quiet (the quiet window outlasts the flush period).
    /// </summary>
    /// <param name="detection">Who signals a change.</param>
    /// <param name="skipPercent">The percentage of ticks on which each session's frames are left undrained.</param>
    /// <param name="deterministic">Whether the index and the far flushes are built serially, in the frame prologue.</param>
    [Test]
    [VerifiesRule("SUB-19")]
    public void DeferredFarUpdatesStillConverge(
        [Values(PushDetection.Explicit, PushDetection.Automatic)] PushDetection detection,
        [Values(0, 30)] int skipPercent,
        [Values(false, true)] bool deterministic)
    {
        // Deterministic projection builds the index, and so folds the far flushes, serially in the frame prologue rather than in their stages.
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 7300 + skipPercent, [skipPercent, 0, 30, 0],
            nameof(PushOracleTests), detection: detection, walkRadius: 3000, deterministicProjection: deterministic, visibilitySlackM: 0, farEvery: 4);

        // h = 0: the drifts this workload makes are what the LOD defers most, and v̂ would remove them as events altogether (09 § 2).

        for (var i = 0; i < GateTicks; i++)
        {
            oracle.Step();
            if ((i + 1) % CompareEvery == 0)
            {
                oracle.Quiesce();
                oracle.AssertConverged($"{detection} push with far updates every 4 ticks, after {i + 1} ticks at a {skipPercent}% skip rate");
            }
        }

        Assert.That(oracle.Push.UpdatesDeferred, Is.GreaterThan(20), "no far update was ever deferred, so the LOD was not exercised");
        Assert.That(oracle.Push.FarFlushes, Is.GreaterThan(20), "the deferred updates were never flushed");
    }

    /// <summary>
    /// Three declared bands (09 § 9) — every 2 ticks beyond 0.4 R, 4 beyond 0.6 R, 8 beyond 0.8 R: walking sessions still hold exactly their disc with
    /// the right values once the world is quiet, the updates of every band are deferred and flushed, and entities the anchors' moves bring inward across
    /// any boundary get their state — with skipped frames, and with v̂ on (the rule's default slack).
    /// </summary>
    /// <param name="skipPercent">The percentage of ticks on which each session's frames are left undrained.</param>
    /// <param name="deterministic">Whether the index and the far flushes are built serially, in the frame prologue.</param>
    [Test]
    [VerifiesRule("SUB-19")]
    public void ThreeBandsConvergeAtEverySkipRate([Values(0, 30, 60)] int skipPercent, [Values(false, true)] bool deterministic)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 7600 + skipPercent, [skipPercent, 0, 30, skipPercent],
            nameof(PushOracleTests), walkRadius: 3000, deterministicProjection: deterministic,
            bands: b => b.Every(2, beyond: 0.4).Every(4, beyond: 0.6).Every(8, beyond: 0.8));
        oracle.Workload.WalkStrideM = 1.5f;
        Assert.That((oracle.Push.FarPhase, oracle.Push.FarWindow), Is.EqualTo((2, 8)), "the fold runs at the innermost period over the outermost's window");

        for (var i = 0; i < GateTicks; i++)
        {
            oracle.Step();
            if ((i + 1) % CompareEvery == 0)
            {
                oracle.Quiesce();
                oracle.AssertConverged($"three bands, after {i + 1} ticks at a {skipPercent}% skip rate");
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(oracle.Push.UpdatesDeferred, Is.GreaterThan(20), "the bands deferred nothing, so they were not exercised");
            Assert.That(oracle.Push.FarFlushes, Is.GreaterThan(20), "the deferred updates were never flushed");
            Assert.That(oracle.Push.FarCrescentStates, Is.GreaterThan(0), "no entity came inward across a boundary with the anchor's move");
        });
    }

    /// <summary>
    /// A far change whose update was deferred still reaches a session that then walks closer to the entity, with no further event from it: the only thing
    /// that brings the entity inside half the radius is the viewer's own move.
    /// </summary>
    /// <param name="seed">The run's seed.</param>
    /// <param name="skipPercent">The percentage of ticks on which half the sessions' frames are left undrained: the flushes then arrive by catch-up.</param>
    [Test]
    [VerifiesRule("SUB-19")]
    public void ADeferredFarChangeReachesASessionThatWalksCloser([Range(7400, 7409)] int seed, [Values(0, 30)] int skipPercent, [Values] bool nested)
    {
        // Nested: every 2 ticks beyond 0.3 R, every 8 beyond 0.6 R — a viewer's step brings entities from the outer band into the inner one, not near,
        // and what the outer band withheld (up to 8 ticks of changes) is not in the inner band's flushes (2 ticks).
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed,
            [skipPercent, 0, skipPercent, 0, skipPercent, 0, skipPercent, 0], nameof(PushOracleTests), detection: PushDetection.Explicit, walkRadius: 3000,
            visibilitySlackM: 0, farEvery: 4, bands: nested ? b => b.Every(2, beyond: 0.3).Every(8, beyond: 0.6) : null);


        for (var i = 0; i < 80; i++)
        {
            oracle.Step();
        }

        // Writes stop; only the viewers move, by 0.3 R a tick — under a cell (R/3), so no move reads as a teleport, and far enough that an entity just past
        // R/2 is inside it before the session's next flush. Whatever a session was not told while an entity was far must reach it as its disc closes in.
        // Two steps only: further, and the discs leave the entities behind, which then come back through an enter — with their whole state.
        var crescentBefore = oracle.Push.FarCrescentStates;
        for (var i = 0; i < 2; i++)
        {
            oracle.StepWalkingOnly(0.3);
        }

        Assert.That(oracle.Push.FarCrescentStates, Is.GreaterThan(crescentBefore), "no held entity crossed inside R/2 on a viewer's move: the case did not run");

        oracle.Quiesce();
        oracle.AssertConverged("far changes deferred, then the sessions walked closer with no further write");
        Assert.That(oracle.Push.UpdatesDeferred, Is.GreaterThan(20), "no far update was ever deferred, so the case was not exercised");
    }

    /// <summary>
    /// A change withheld on the last frame a session received, its flush missed in a skipped frame, and the viewer's move bringing the entity inward on the
    /// catch-up: the catch-up leaves the gap's flush of an inward entity to the inner crescent, so the crescent must reach back past the gap.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-19")]
    [VerifiesRule("SUB-03")]
    public void AChangeWithheldBeforeASkippedFlushReachesASessionThatCameCloser()
    {
        const double Radius = 3000;
        const int Every = 2;
        const string Profile = "far";
        var engine = ProjectionTestSchema.SetupEngine(ServiceProvider);
        using var harness = FrameHarness.Create(engine, subs =>
        {
            ProjectionTestSchema.DeclareCreature(subs);
            subs.Profile(Profile, p => p.Sphere(Radius).Bands(b => b.Every(Every, beyond: 0.5)).Of<ProjCreature>());
        }, nameof(PushOracleTests), new SubscriptionsOptions
        {
            PushShadow = true,
            MaxSessions = 4,
            StatePoolBudgetBytes = 64L * 1024 * 1024,
            FramePoolBudgetBytes = 64L * 1024 * 1024,
            ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(Radius),
        });

        var push = harness.Subscriptions.Push;

        // Far at 0.6 R; a near entity whose changes fill the session's two frame slots, so the frame after them is refused.
        const float FarX = (float)(Radius * 0.6);
        EntityId entity;
        EntityId near;
        using (var tx = engine.CreateQuickTransaction())
        {
            var ai = new ProjAi { Template = 1, Mode = ProjAiMode.Wander, Level = 10 };
            var vitals = new ProjVitals { Health = 10, MaxHealth = 20 };
            var bounds = BoundsAt(FarX);
            entity = tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in bounds), ProjCreature.Ai.Set(in ai), ProjCreature.Vitals.Set(in vitals));
            bounds = BoundsAt(100f);
            near = tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in bounds), ProjCreature.Ai.Set(in ai), ProjCreature.Vitals.Set(in vitals));
            tx.Commit();
        }

        var session = harness.OpenSessions(1, Profile)[0];
        Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d)), Is.True);
        var replica = harness.Replica(session);
        var creature = harness.CatalogPlan.ArchetypeByName(nameof(ProjCreature)).Idx;

        long tick = 0;
        void Run(bool deliver)
        {
            tick++;
            engine.WriteTickFence(tick);
            harness.RunTick(tick);
            if (deliver)
            {
                harness.Deliver(session);
            }
        }

        for (var i = 0; i < 4; i++)
        {
            Run(deliver: true);
        }

        var netId = NetIdAt(replica, creature, FarX);

        // T0, the change's tick, is not the entity's flush tick; T0 + 1 is, and its frame is refused.
        var t0 = tick + 2;
        if (((netId % Every) + ((ulong)t0 % Every)) % Every == 0)
        {
            t0++;
        }

        while (tick < t0 - 2)
        {
            Run(deliver: true);
        }

        WriteLevel(harness, near, 11, null);
        Run(deliver: false);
        WriteLevel(harness, near, 12, null);
        WriteLevel(harness, entity, 4242, null);
        var deferred = push.UpdatesDeferred;
        Run(deliver: false);
        Assert.That(push.UpdatesDeferred, Is.GreaterThan(deferred), "the change was not withheld on its tick, so the case did not run");
        var catchUps = push.LogCatchUps;
        Run(deliver: false);

        // Drained; the viewer steps 0.3 R east, which brings the entity inside R/2 — the next frame is the catch-up over the refused one.
        harness.Deliver(session);
        Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(Radius * 0.3, 0d, 0d)), Is.True);
        for (var i = 0; i < 4; i++)
        {
            Run(deliver: true);
        }

        Assert.That(push.LogCatchUps, Is.GreaterThan(catchUps), "the session was not caught up through the log, so the case did not run");
        Assert.That(push.ShadowIllegal, Is.Zero, "a record the client could not apply was published");
        Assert.That(replica.Value(creature, netId, "level"), Is.EqualTo(4242d), "the withheld change was lost between the skipped flush and the crescent");
    }

    /// <summary>
    /// Changes in the run's first ticks, under an 8-tick band: an entity whose flush falls before tick 8 carries them — the window's floor is tick 0, not a
    /// wrapped tick past every stamp — and every client converges.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-19")]
    public void AChangeInTheFirstTicksReachesAFarSession()
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 7430, [0, 0], nameof(PushOracleTests),
            walkRadius: 3000, bands: b => b.Every(8, beyond: 0.05));
        for (var i = 0; i < 5; i++)
        {
            oracle.Step();
        }

        oracle.Quiesce();
        oracle.AssertConverged("changes of the first ticks, under an 8-tick band");
        Assert.That(oracle.Push.UpdatesDeferred, Is.GreaterThan(0), "nothing was deferred: the case did not run");
    }

    /// <summary>
    /// Far flushes delivered through the log's catch-up, with small cells that the workload's moves cross often and most frames skipped, under the shadow
    /// legality check.
    /// </summary>
    /// <remarks>
    /// Written for the catch-up meeting an entity's event twice on its phase tick — its secondary, a copy taken before the fold flagged the primary, first
    /// — but it does not reliably reach that case: removing the fix (CollectLog's merge of the flag on the second meeting) leaves it green.
    /// <see cref="AFarFlushMetFirstAsASecondaryReachesACaughtUpSession"/> scripts it.
    /// </remarks>
    /// <param name="seed">The run's seed.</param>
    [Test]
    [VerifiesRule("SUB-19")]
    public void FarFlushesConvergeThroughCatchUpWithSmallCells([Range(7500, 7507)] int seed)
    {
        // A small radius, so cells are small and the workload's moves cross them often; most frames skipped, so most flushes arrive by catch-up.
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed, [60, 60, 60, 60, 60, 60], nameof(PushOracleTests),
            detection: PushDetection.Explicit, walkRadius: 400, farEvery: 4);

        for (var i = 0; i < 150; i++)
        {
            oracle.Step();
        }

        oracle.Quiesce();
        oracle.AssertConverged("far flushes on cell crossings, with most frames skipped");
        Assert.That(oracle.Push.LogCatchUps, Is.GreaterThan(0), "no session ever caught up, so no flush travelled through the log");
    }

    /// <summary>
    /// A far flush met first as a secondary during the log's catch-up still reaches the session: an entity beyond R/2 changes a group and crosses into the
    /// next cell eastward on its phase tick, while the session's frames are full.
    /// </summary>
    /// <remarks>
    /// The event is indexed twice: its primary in the new cell, flagged in place by the far fold, and a secondary in the old one, copied before the fold
    /// ran and so unflagged. The catch-up walks cells in ascending order and meets the secondary first. Dropping the flag on the second meeting leaves the
    /// change withheld for good: the entity's next flush, N ticks later, covers only the changes after this one.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-19")]
    [VerifiesRule("SUB-03")]
    public void AFarFlushMetFirstAsASecondaryReachesACaughtUpSession()
    {
        const double Radius = 3000;
        const int Every = 4;
        const string Profile = "far";
        var engine = ProjectionTestSchema.SetupEngine(ServiceProvider);
        using var harness = FrameHarness.Create(engine, subs =>
        {
            ProjectionTestSchema.DeclareCreature(subs);
            subs.Profile(Profile, p => p.Sphere(Radius).Bands(b => b.Every(Every, beyond: 0.5)).Of<ProjCreature>());
        }, nameof(PushOracleTests), new SubscriptionsOptions
        {
            PushShadow = true,
            MaxSessions = 4,
            StatePoolBudgetBytes = 64L * 1024 * 1024,
            FramePoolBudgetBytes = 64L * 1024 * 1024,
            ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(Radius),
        });

        var push = harness.Subscriptions.Push;

        // A cell edge past R/2 east of the viewer, with the grid starting at the world's west edge: the entity sits half a metre west of it, far.
        Assert.That(push.CellSize, Is.EqualTo(Radius / 3));
        var edge = (float)(-ProjectionTestSchema.WorldExtentM + (Math.Ceiling(((Radius * 0.5) + ProjectionTestSchema.WorldExtentM) / push.CellSize)
            * push.CellSize));
        Assert.That(edge - 0.5f, Is.GreaterThan(Radius * 0.5).And.LessThan(Radius * 0.75));

        // A near entity too, whose changes fill the session's frames before the phase tick: an idle frame is abandoned rather than queued.
        EntityId entity;
        EntityId near;
        using (var tx = engine.CreateQuickTransaction())
        {
            var ai = new ProjAi { Template = 1, Mode = ProjAiMode.Wander, Level = 10 };
            var vitals = new ProjVitals { Health = 10, MaxHealth = 20 };
            var bounds = BoundsAt(edge - 0.5f);
            entity = tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in bounds), ProjCreature.Ai.Set(in ai), ProjCreature.Vitals.Set(in vitals));
            bounds = BoundsAt(100f);
            near = tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in bounds), ProjCreature.Ai.Set(in ai), ProjCreature.Vitals.Set(in vitals));
            tx.Commit();
        }

        var session = harness.OpenSessions(1, Profile)[0];
        Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d)), Is.True);
        var replica = harness.Replica(session);
        var creature = harness.CatalogPlan.ArchetypeByName(nameof(ProjCreature)).Idx;

        long tick = 0;
        void Run(bool deliver)
        {
            tick++;
            engine.WriteTickFence(tick);
            harness.RunTick(tick);
            if (deliver)
            {
                harness.Deliver(session);
            }
        }

        for (var i = 0; i < 4; i++)
        {
            Run(deliver: true);
        }

        Assert.That(replica.NetIds(creature), Has.Length.EqualTo(2), "the session holds both entities before the change");
        var netId = NetIdAt(replica, creature, edge - 0.5);

        // The entity's phase tick, far enough ahead to fill the session's two frame slots before it.
        var phase = tick + 3;
        while (((netId % Every) + (phase % Every)) % Every != 0)
        {
            phase++;
        }

        while (tick < phase - 3)
        {
            Run(deliver: true);
        }

        WriteLevel(harness, near, 11, null);
        Run(deliver: false);
        WriteLevel(harness, near, 12, null);
        Run(deliver: false);
        var catchUps = push.LogCatchUps;

        // The phase tick: a new level and a step east across the cell edge, in one event.
        WriteLevel(harness, entity, 4242, edge + 0.5f);
        Run(deliver: false);
        Run(deliver: false);

        // Drained: the next frame is the catch-up over the phase tick. Then quiet ticks past the next flush, which carries nothing new.
        harness.Deliver(session);
        for (var i = 0; i < (2 * Every) + 2; i++)
        {
            Run(deliver: true);
        }

        Assert.That(push.LogCatchUps, Is.GreaterThan(catchUps), "the session was not caught up through the log, so the case did not run");
        Assert.That(push.ShadowIllegal, Is.Zero, "a record the client could not apply was published");
        Assert.That(replica.Value(creature, netId, "level"), Is.EqualTo(4242d), "the far flush on the phase tick was lost in the catch-up");
        Assert.That(replica.Position(creature, netId)[0], Is.EqualTo(edge + 0.5).Within(OracleHarness.MotionToleranceM),
            "the flushed segment was lost in the catch-up");
    }

    private static uint NetIdAt(SessionReplica replica, int archetype, double x)
    {
        foreach (var id in replica.NetIds(archetype))
        {
            if (Math.Abs(replica.Position(archetype, id)[0] - x) < 1)
            {
                return id;
            }
        }

        Assert.Fail($"the replica holds no entity at x = {x}");
        return 0;
    }

    private static void WriteLevel(FrameHarness harness, EntityId entity, ushort level, float? x)
    {
        var found = false;
        using var tx = harness.Engine.CreateQuickTransaction();
        var accessor = tx.For<ProjCreature>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var occupancy = cluster.OccupancyBits;
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    if (cluster.GetEntityId(slot).RawValue != entity.RawValue)
                    {
                        continue;
                    }

#pragma warning disable TYPHON009
                    var ai = cluster.GetSpan(ProjCreature.Ai);
#pragma warning restore TYPHON009
                    ai[slot].Level = level;
                    cluster.MarkDirty(ProjCreature.Ai);
                    if (x.HasValue)
                    {
                        cluster.WriteSpatial(ProjCreature.Bounds, slot, BoundsAt(x.Value));
                    }

                    harness.Subscriptions.Commands.Replicate(in cluster, slot);
                    found = true;
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
        Assert.That(found, Is.True, "the entity is live");
    }

    private static ProjBounds BoundsAt(float x) => new()
    {
        Bounds = new AABB2F { MinX = x - 0.5f, MinY = -0.5f, MaxX = x + 0.5f, MaxY = 0.5f },
        Speed = 1f,
    };

    /// <summary>Automatic detection is off unless the runtime is told otherwise (ADR-067): replication is explicit.</summary>
    [Test]
    public void AutomaticDetectionIsRefusedUnlessEnabled()
    {
        var engine = ProjectionTestSchema.SetupEngine(ServiceProvider);
        try
        {
            var refused = Assert.Throws<System.NotSupportedException>(() => FrameHarness.Create(engine, subs =>
            {
                ProjectionTestSchema.DeclareCreature(subs);
                subs.Profile("auto", p => p.Detection(PushDetection.Automatic).Sphere(100).Of<ProjCreature>());
            }, nameof(AutomaticDetectionIsRefusedUnlessEnabled)));
            Assert.That(refused.Message, Does.Contain("AllowAutomaticPushDetection"));
        }
        finally
        {
            engine.Dispose();
        }
    }

    /// <summary>Two sessions at opposite delivery rates converge on the same world.</summary>
    [Test]
    public void SessionsAtDifferentDeliveryRatesConvergeUnderPush([Values(PushDetection.Explicit, PushDetection.Automatic)] PushDetection detection)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 778, [0, 85], nameof(PushOracleTests),
            detection: detection);

        for (var i = 0; i < GateTicks; i++)
        {
            oracle.Step();
        }

        oracle.Quiesce();
        oracle.AssertConverged($"{detection} push: two sessions at different delivery rates hold the same world once both are caught up");
        AssertLookedAtSomething(oracle);
    }

    /// <summary>
    /// Walking sessions with a disc a fraction of the world's size hold exactly what their disc names, through strides that move the anchor, teleports
    /// that reset it, identity churn and skipped frames.
    /// </summary>
    /// <param name="detection">Who signals a change.</param>
    /// <param name="skipPercent">The percentage of ticks on which each session's frames are left undrained.</param>
    /// <remarks>
    /// The whole-world cases above never test the known-set's boundary: every entity is inside every disc. Here the geometry is the whole claim — the
    /// crescent sweep as the anchor moves, the cell delivery after a teleport, and the push step's enter and leave as entities cross a disc's edge. An
    /// entity well inside a disc must be held and one well outside must not; the band between is the anchor's slack plus the motion tolerance.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-16")]
    public void WalkingSessionsHoldExactlyWhatTheirDiscNames(
        [Values(PushDetection.Explicit, PushDetection.Automatic)] PushDetection detection,
        [Values(0, 60)] int skipPercent)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 9100 + skipPercent, [skipPercent, 0, 30, skipPercent],
            nameof(PushOracleTests), detection: detection, walkRadius: 3000);

        var required = 0L;
        for (var i = 0; i < GateTicks; i++)
        {
            oracle.Step();
            if ((i + 1) % CompareEvery == 0)
            {
                oracle.Quiesce();
                oracle.AssertConverged($"{detection} push, walking sessions, after {i + 1} ticks at a {skipPercent}% skip rate");
                oracle.AssertWritesSurvived($"{detection} push, walking sessions, after {i + 1} ticks");
                required += oracle.RequiredAtLastPoint;
            }
        }

        Assert.That(required, Is.GreaterThan(200), "the discs held too little for the comparison to mean anything");
        Assert.That(oracle.ViewpointTeleports, Is.GreaterThan(5), "no session teleported, so the reset path was not exercised");
        Assert.That(oracle.Push.Sweeps, Is.GreaterThan(50), "the anchors barely moved, so the crescent sweep was not exercised");
    }

    /// <summary>
    /// Creatures that walk steadily leave v̂ behind by up to the slack h (09 § 2), and every session still holds exactly what its disc names in terms of
    /// v̂: an entity within R − h of the viewpoint is held, one past R + h is not — through walking sessions, teleports, churn and skipped frames.
    /// </summary>
    /// <param name="slackM">h in metres; −1 for the rule, R / 48 = 62.5 m at this radius. An int: the test name becomes a database name.</param>
    /// <param name="skipPercent">The percentage of ticks on which each session's frames are left undrained.</param>
    /// <remarks>
    /// The drift and teleport workload never exercises v̂: a drift stays under a centimetre and a teleport moves v̂ with it. A 1.5 m stride per tick — 15 m/s, under the
    /// declared 20 m/s teleport — is what makes v̂ lag, cross cells late and cross the disc's edge late — the cases the widened cluster margins exist for. At h = 0 every step is an event; at
    /// h > 0 a walker makes one per h of travel, which the event count shows.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-16")]
    [VerifiesRule("SUB-20")]
    public void SteadyWalkersAreHeldWithinTheSlackOfTheirDisc([Values(0, -1, 8)] int slackM, [Values(0, 60)] int skipPercent)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 9300 + skipPercent, [skipPercent, 0, 30, skipPercent],
            nameof(PushOracleTests), walkRadius: 3000, visibilitySlackM: slackM < 0 ? double.NaN : slackM);
        oracle.Workload.WalkStrideM = 1.5f;
        var expectedSlack = slackM < 0 ? 3000d / 48d : slackM;
        Assert.That(oracle.CreatureSlackM, Is.EqualTo(expectedSlack), "the slack the creatures were resolved to");

        var required = 0L;
        var walkerTicks = 0L;
        var events0 = oracle.Push.Events;
        for (var i = 0; i < GateTicks; i++)
        {
            walkerTicks += oracle.Workload.Creatures.Count;
            oracle.Step();
            if ((i + 1) % CompareEvery == 0)
            {
                oracle.Quiesce();
                oracle.AssertConverged($"h = {expectedSlack} m, steady walkers, after {i + 1} ticks at a {skipPercent}% skip rate");
                oracle.AssertWritesSurvived($"h = {expectedSlack} m, steady walkers, after {i + 1} ticks");
                required += oracle.RequiredAtLastPoint;
            }
        }

        var eventsPerWalkerTick = (oracle.Push.Events - events0) / (double)walkerTicks;
        Assert.Multiple(() =>
        {
            Assert.That(required, Is.GreaterThan(200), "the discs held too little for the comparison to mean anything");
            Assert.That(oracle.Push.Sweeps, Is.GreaterThan(50), "the anchors barely moved, so the crescent sweep was not exercised");
            if (expectedSlack == 0)
            {
                Assert.That(eventsPerWalkerTick, Is.GreaterThan(0.9), "at h = 0 every stride is an event");
            }
            else
            {
                // A 1.5 m stride moves v̂ once per ⌈h / 1.5⌉ strides: a sixth of the h = 0 count at 8 m, a fortieth at 62.5 m, plus segments and churn.
                Assert.That(eventsPerWalkerTick, Is.LessThan(expectedSlack > 10 ? 0.15 : 0.5), $"at h = {expectedSlack} m v̂ moves once per h of travel");
            }
        });
    }

    /// <summary>
    /// Two Sphere profiles of different radii in one runtime, one of them with a leave band (09 § 3–4): even sessions hold the band's disc,
    /// <c>Sphere(1 500, leave: 1 600)</c> tested at R′ = 1 550 m, odd sessions a 1 000 m disc — each exactly, with steady walkers, walking sessions,
    /// teleports, churn and skipped frames.
    /// </summary>
    /// <param name="skipPercent">The percentage of ticks on which each session's frames are left undrained.</param>
    [Test]
    [VerifiesRule("SUB-16")]
    [VerifiesRule("SUB-20")]
    public void TwoProfilesAndABandEachHoldTheirOwnDisc([Values(0, 60)] int skipPercent)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 9400 + skipPercent,
            [skipPercent, skipPercent, 0, 30, skipPercent, 0], nameof(PushOracleTests), walkRadius: 1500, leaveRadius: 1600, secondRadius: 1000);
        oracle.Workload.WalkStrideM = 1.5f;

        // h_A is the smaller profile's: the band asks for (1 600 − 1 500) / 2 = 50 m, the plain 1 000 m disc for 1 000 / 48.
        Assert.That(oracle.CreatureSlackM, Is.EqualTo(1000d / 48d).Within(1e-9), "h_A is the smallest over the profiles");

        var required = 0L;
        for (var i = 0; i < GateTicks; i++)
        {
            oracle.Step();
            if ((i + 1) % CompareEvery == 0)
            {
                oracle.Quiesce();
                oracle.AssertConverged($"two profiles and a band, after {i + 1} ticks at a {skipPercent}% skip rate");
                oracle.AssertWritesSurvived($"two profiles and a band, after {i + 1} ticks");
                required += oracle.RequiredAtLastPoint;
            }
        }

        Assert.That(required, Is.GreaterThan(200), "the discs held too little for the comparison to mean anything");
    }

    /// <summary>
    /// The contract explicit detection rests on, shown failing: a system that writes and does not push leaves its clients stale, and the oracle sees it.
    /// </summary>
    /// <remarks>
    /// This is not a defect of the engine — it is the developer contract <see cref="PushDetection.Explicit"/> accepts, and the reason
    /// <see cref="PushDetection.Automatic"/> exists. It is here so that the explicit arm above cannot pass because the oracle is blind to missing pushes.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-10")]
    public void AForgottenPushLeavesTheClientStaleAndTheOracleSeesIt()
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 4243, [0], nameof(PushOracleTests),
            detection: PushDetection.Explicit);
        oracle.Workload.ForgetPushesAfter = 0;

        RuleMutants.AssertDetects("SUB-10", "on the client and", () =>
        {
            for (var i = 0; i < GateTicks; i++)
            {
                oracle.Step();
            }

            // Churn alone may heal a forgotten write — a later teleport pushes the entity whole — so the run ends with one no entity recovers from.
            oracle.Workload.WriteModeOnEveryCreature();
            oracle.Quiesce();
            oracle.AssertConverged("writes that were never pushed");
        });
    }

    /// <summary>
    /// The forgotten-push validator finds the writes a system did not push, and — sweeping every cluster — heals them, so the client converges anyway.
    /// </summary>
    [Test]
    public void TheValidatorFindsAndHealsForgottenPushes()
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 4244, [0], nameof(PushOracleTests),
            detection: PushDetection.Explicit);
        oracle.Workload.ForgetPushesAfter = 0;
        oracle.Push.ValidateClustersPerTick = int.MaxValue;

        for (var i = 0; i < GateTicks; i++)
        {
            oracle.Step();
        }

        oracle.Quiesce();
        oracle.AssertConverged("writes that were never pushed, found by a validator that visits every cluster every tick");
        Assert.That(oracle.Push.ForgottenPushes, Is.GreaterThan(20), "the workload wrote without pushing, and the validator should have seen it");
        AssertLookedAtSomething(oracle);
    }

    /// <summary>A workload that pushes everything it writes gives the validator nothing to find.</summary>
    [Test]
    public void TheValidatorIsSilentWhenEveryWriteIsPushed()
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 4245, [0], nameof(PushOracleTests),
            detection: PushDetection.Explicit);
        oracle.Push.ValidateClustersPerTick = int.MaxValue;

        for (var i = 0; i < GateTicks; i++)
        {
            oracle.Step();
        }

        Assert.That(oracle.Push.ValidatedSlots, Is.GreaterThan(1000), "the validator checked too little to mean anything");
        Assert.That(oracle.Push.ForgottenPushes, Is.Zero, "every write was pushed, so anything the validator reports is a false positive");
    }

    /// <summary>
    /// Ticks the track skipped — no session connected, an aborted tick — lose their pushes at the fence; the next tick re-pushes every live entity, so spawns,
    /// destroys and writes made in the gap still reach the client.
    /// </summary>
    /// <param name="skippedTicks">How many consecutive ticks run the fence without the track.</param>
    [Test]
    [VerifiesRule("SUB-10")]
    public void TicksTheTrackSkippedStillReachTheClient([Values(1, 20)] int skippedTicks)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 4246 + skippedTicks, [0], nameof(PushOracleTests),
            detection: PushDetection.Explicit);
        for (var i = 0; i < 20; i++)
        {
            oracle.Step();
        }

        var destroyed = oracle.Workload.Destroyed;
        var pushed = oracle.Workload.Pushed;
        for (var i = 0; i < skippedTicks; i++)
        {
            oracle.StepWithoutTrack();
        }

        oracle.Workload.WriteModeOnEveryCreature();
        oracle.StepWithoutTrack();

        Assert.That(oracle.Workload.Pushed, Is.GreaterThan(pushed), "the gap pushed nothing, so there was nothing for the fence to lose");
        if (skippedTicks > 1)
        {
            Assert.That(oracle.Workload.Destroyed, Is.GreaterThan(destroyed), "the gap destroyed nothing, so no ghost could have been left behind");
        }

        oracle.Quiesce();
        oracle.AssertConverged($"after {skippedTicks + 1} ticks the track did not run");
        Assert.That(oracle.Push.GapRepushes, Is.EqualTo(1), "the tick after the gap should re-push every live entity once");
        AssertLookedAtSomething(oracle);
    }

    private static void AssertLookedAtSomething(OracleHarness oracle)
    {
        var live = oracle.LiveEntityCount;
        Assert.That(live, Is.GreaterThan(20), $"the world held only {live} entities, so the run proves very little whatever the oracle compared");
        Assert.That(oracle.ComparedAtLastPoint, Is.EqualTo((long)live * oracle.Sessions.Length),
            $"the last comparison covered {oracle.ComparedAtLastPoint} entities but the engine holds {live} across {oracle.Sessions.Length} session(s)");
    }
}
