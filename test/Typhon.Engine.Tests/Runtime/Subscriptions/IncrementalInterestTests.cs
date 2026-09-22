using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;
using Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// The temporal gather: a session's frame built as a difference against what its interest reached last tick, rather than by re-deriving its whole view.
/// </summary>
/// <remarks>
/// <para>
/// <b>The property under test is that the difference emits exactly what the full walk emits — byte for byte, frame for frame.</b> Comparing record COUNTS,
/// or comparing the replicas at the end, would both pass on a pair of compensating errors — a leave sent a tick late and an enter sent a tick early converge
/// on the same world while describing two different histories, and a client that reconnects mid-stream sees the difference.
/// </para>
/// <para>
/// <b>What validates WHICH arm, stated precisely, because it is easy to get backwards.</b> <c>DifferentialOracleTests</c> runs on the engine's defaults, and
/// the default is the difference — so the oracle validates the DIFFERENCE against the server's own state, and nothing validates the full walk that way any
/// more. This fixture is therefore a consistency check between two paths, not an appeal to an already-trusted one: it says the two agree, and the oracle
/// says the one it runs is right. Together those are what the pair is worth; separately neither would be enough.
/// </para>
/// <para>
/// <b>The world is the differential oracle's own seeded churn</b> (<see cref="OracleWorkload"/>) — spawns, destroys, drifts below the motion tolerance,
/// teleports that force real cluster migrations, and writes to both change groups — under SPHERE observers that move every tick. That last part is what this
/// fixture adds to the oracle's coverage: the oracle watches the whole world, so its interest never loses a slot, and the difference's leave half would never
/// run. Here every tick gains and loses entities for every session.
/// </para>
/// <para>
/// <b>Both arms run on one binary.</b> <c>SubscriptionsOptions.TemporalGather</c> switches the shape.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class IncrementalInterestTests : TestBase<IncrementalInterestTests>
{
    /// <summary>
    /// The observer radius, against the schema's ±8 192 m world: a disc holding a useful fraction of it, and losing a useful fraction of that per tick.
    /// </summary>
    private const double Radius = 4000d;

    /// <summary>
    /// How many churned ticks a run covers. Long enough for identities to be released and re-leased, which is what puts a slot's reuse under test.
    /// </summary>
    private const int Ticks = 200;

    /// <summary>How many observers. More than one so that a cluster is reached by several sessions with different answers about it.</summary>
    private const int SessionCount = 6;

    private const string Profile = "temporal-near";

    /// <summary>One run of the world: the frames each session was handed, and what the assembler counted while producing them.</summary>
    private sealed class Run
    {
        public List<byte[]>[] Frames;
        public CatalogPlan Catalog;
        public long TemporalGathers;
        public long FullGathers;
        public long FramesProduced;
        public int Destroyed;
        public int Teleports;
        public long SparseRunsSkipped;
        public long SyntheticRuns;
        public long RunsRetainedByView;
        public long StationaryRetained;
    }

    /// <summary>
    /// The difference emits exactly what the full walk emits.
    /// </summary>
    /// <remarks>
    /// The two arms are the same seed, the same world, the same observer path and the same tick numbers, so the only thing that differs is which slots the
    /// frame stage looked at. Any divergence in the bytes is a record the difference invented or dropped.
    /// </remarks>
    /// <param name="skipPercent">
    /// The percentage of ticks on which a session's frames are left undrained, which fills its hand-off slots and makes the engine skip it.
    /// </param>
    /// <param name="crowd">
    /// Observers crowded into shared interest cells and drifting slowly (<see cref="CrowdViewpointAt"/>), rather than orbiting apart — the shape that
    /// exercises cell groups, stationary retention and the view pass.
    /// </param>
    [Test]
    [VerifiesRule("SUB-18")]
    public void TheDifferenceEmitsExactlyWhatTheFullWalkEmits([Values(0, 60, 90)] int skipPercent, [Values(false, true)] bool crowd)
    {
        var reference = Execute(temporal: false, skipPercent, crowd);
        var difference = Execute(temporal: true, skipPercent, crowd);

        Assert.Multiple(() =>
        {
            // Anti-vacuity FIRST, because everything below is satisfied trivially by a path that never ran, and the obvious form of it does NOT work:
            // both gather counters are incremented only on the incremental path, so "the reference arm took no difference" reads zero whether the arm walked
            // everything or produced nothing at all. What separates those two is that both arms must have produced the SAME, non-trivial number of frames.
            Assert.That(reference.FramesProduced, Is.GreaterThan(SessionCount),
                $"the reference arm produced {reference.FramesProduced} frames, which is too few to compare anything");
            Assert.That(difference.FramesProduced, Is.EqualTo(reference.FramesProduced),
                $"the difference produced {difference.FramesProduced} frames against the walk's {reference.FramesProduced}; the two arms did not run the "
                + "same world");
            // TemporalGathers is incremented ONLY on the incremental path, so zero there says the reference really did take the other one. FullGathers is
            // not usable for that — Phase 1's walk increments it too, beside its call to Sweep — so it is asserted in the opposite direction, as proof that
            // the reference arm walked at all rather than producing nothing.
            Assert.That(reference.TemporalGathers, Is.Zero, "the reference arm took the incremental path, so it is not a reference for it");
            Assert.That(reference.FullGathers, Is.GreaterThan(SessionCount), "the reference arm barely walked anything");
            // The REDUCTION needs the session to be exactly one tick behind, so how often it applies is a property of the delivery pattern and not of the
            // difference: a session that is being skipped is by definition behind, and SUB-03 makes it read every slot. At full delivery the reduction is
            // the steady state and is asserted as such; under skipping it must merely still happen, or the arms would be two full walks and the byte
            // comparison would compare nothing. That the reduction switches itself off under back-pressure is a real property and is recorded in 15 § 11.
            // The crowd holds many more entities per session and churns them at a far higher rate, so the full walk is taken often there for the ordinary
            // reasons (a session owed enters); the steady-state claim is the orbiting workload's.
            if (skipPercent == 0 && !crowd)
            {
                Assert.That(difference.TemporalGathers, Is.GreaterThan(difference.FullGathers),
                    $"at full delivery the difference was taken {difference.TemporalGathers} times against {difference.FullGathers} full walks; it is "
                    + "meant to be the steady state, not an occasional shortcut");
            }
            else
            {
                Assert.That(difference.TemporalGathers, Is.GreaterThan(0),
                    $"the difference never applied at a {skipPercent}% skip rate, so both arms walked everything and the comparison compares nothing");
            }
            // A FLOOR on identity churn, because that is what this fixture's hardest case is made of. The defect the SWG demo found and this fixture did
            // not was a netId that named two block entries at once — the engine re-leases an identity as entities are destroyed and slots reused — and a run
            // with few destroys never reaches it however many ticks it runs.
            Assert.That(difference.Destroyed, Is.GreaterThan(30), "the workload destroyed too little to put any pressure on identity or on slot reuse");
            // The sparse path must have run both halves — skipping unchanged runs, and delivering content changes through the changed-block tables — or the
            // comparison says nothing about it.
            Assert.That(difference.SparseRunsSkipped, Is.GreaterThan(0), "no session ever skipped an unchanged run");
            Assert.That(difference.SyntheticRuns, Is.GreaterThan(0), "no content change ever reached a session through the changed-block tables");
            Assert.That(reference.SparseRunsSkipped, Is.Zero, "the reference arm took the sparse path");
            Assert.That(difference.RunsRetainedByView, Is.GreaterThan(0), "no moving member's run was ever retained by the pass over its view");
            Assert.That(difference.Teleports, Is.GreaterThan(10), "the workload never forced a cluster migration, which is the leave-and-enter this path has "
                + "to recognise as neither");

            for (var s = 0; s < SessionCount; s++)
            {
                var a = reference.Frames[s];
                var b = difference.Frames[s];
                Assert.That(b, Has.Count.EqualTo(a.Count), $"session {s} received {b.Count} frames under the difference against {a.Count} under the full walk");

                Assert.That(a, Is.Not.Empty, $"session {s} received no frames at all under the full walk, so comparing the two arms compares nothing");

                var frames = Math.Min(a.Count, b.Count);
                for (var f = 0; f < frames; f++)
                {
                    Assert.That(b[f], Is.EqualTo(a[f]),
                        $"session {s}, frame {f}: the difference produced different bytes from the full walk at a {skipPercent}% skip rate. The two "
                        + "paths describe the same world, so exactly one of them is wrong and the difference is the one that changed");
                }
            }
        });
    }

    /// <summary>
    /// The observers really do gain and lose entities, so the halves of the difference that handle them really do run.
    /// </summary>
    /// <remarks>
    /// This is the measurement the fixture above rests on and cannot make itself: byte equality between two arms is as easily achieved by two sessions that
    /// see nothing. A count of ENTER and LEAVE records over the run is what says the enter half and the leave half were both exercised, and it is taken from
    /// the decoded wire rather than from an engine counter so that it describes what a client was told.
    /// </remarks>
    [Test]
    public void TheObserversGainAndLoseEntitiesThroughoutTheRun()
    {
        var run = Execute(temporal: true);

        var enters = 0;
        var leaves = 0;
        for (var s = 0; s < SessionCount; s++)
        {
            foreach (var frame in run.Frames[s])
            {
                var log = new FrameLog();
                log.Decode(frame, run.Catalog);
                enters += log.Enters.Count;
                leaves += log.Leaves.Count;

                // SUB-06, per frame and not merely in the design's remarks: an entity that crossed clusters departs one and arrives in another within one
                // tick, and both halves of the difference see it. A frame carrying a leave and an enter for one identity tells the client to forget
                // something it is being given in the same breath, and which of the two wins is decided by the decoder's ordering rather than by the engine.
                foreach (var netId in log.Enters)
                {
                    Assert.That(log.Leaves, Does.Not.Contain(netId),
                        $"netId {netId} both entered and left in one frame; an entity that merely moved between clusters must produce neither");
                }
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(enters, Is.GreaterThan(200), $"only {enters} entities ever entered a view over {Ticks} ticks and {SessionCount} moving observers");
            Assert.That(leaves, Is.GreaterThan(200),
                $"only {leaves} entities ever left a view. The leave half of the difference is the part that replaces the known-set sweep, and a run that "
                + "loses nothing never reaches it");
        });
    }

    private static void Declare(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        ProjectionTestSchema.DeclareRock(subs);
        subs.Profile(Profile, p => p.Sphere(Radius).Of<ProjCreature>().Of<ProjRock>());
    }

    /// <summary>
    /// The observer path: deterministic, shared by both arms, and chosen so that every tick moves each disc by a real fraction of its radius.
    /// </summary>
    /// <remarks>
    /// A circle rather than a straight line, because a straight line eventually leaves the populated world and the run ends with four empty discs — which is
    /// the one arrangement in which both arms agree for the wrong reason. The radius of the path breathes so that the four discs overlap differently from
    /// tick to tick, which is what makes a cluster reached by several sessions with different answers about it.
    /// </remarks>
    private static Vector3D ViewpointAt(int session, long tick)
    {
        var angle = (tick * 0.13d) + (session * Math.PI / 2d);
        var distance = 2600d + (900d * Math.Sin((tick * 0.07d) + session));
        return new Vector3D(distance * Math.Cos(angle), distance * Math.Sin(angle), 0d);
    }

    /// <summary>
    /// A crowd: every observer within a few hundred metres of a common point that drifts a few metres a tick, so several share an interest cell and keep
    /// sharing it — the shape the cell cache exists for.
    /// </summary>
    private static Vector3D CrowdViewpointAt(int session, long tick)
    {
        var cx = 1200d + (tick * 3d);
        var cy = -800d + (tick * 1.5d);
        var angle = session * (Math.PI * 2d / SessionCount);
        var spread = 150d + (40d * Math.Sin((tick * 0.05d) + session));
        return new Vector3D(cx + (spread * Math.Cos(angle)), cy + (spread * Math.Sin(angle)), 0d);
    }

    /// <summary>
    /// The crowd, but moving in bursts: five ticks of drift, then ten standing still — so members take the stationary path, where the held mask is
    /// re-emitted without the kernel, and do so across skipped frames.
    /// </summary>
    private static Vector3D PausingViewpointAt(int session, long tick) => CrowdViewpointAt(session, (tick / 15 * 5) + Math.Min(tick % 15, 5));

    /// <summary>
    /// Stationary retention emits exactly what the full walk emits: members standing still re-emit what they hold without running the kernel, and the
    /// frames must not tell the difference — through skipped frames, spawns and destroys around them.
    /// </summary>
    /// <param name="skipPercent">The percentage of ticks on which a session's frames are left undrained.</param>
    [Test]
    [VerifiesRule("SUB-18")]
    public void StationaryRetentionEmitsExactlyWhatTheFullWalkEmits([Values(0, 60, 90)] int skipPercent)
    {
        var reference = Execute(temporal: false, skipPercent, crowd: true, pausing: true);
        var retained = Execute(temporal: true, skipPercent, crowd: true, pausing: true);

        Assert.Multiple(() =>
        {
            Assert.That(reference.FramesProduced, Is.GreaterThan(SessionCount), "the reference arm produced too few frames to compare anything");
            Assert.That(retained.FramesProduced, Is.EqualTo(reference.FramesProduced), "the two arms did not run the same world");
            Assert.That(retained.StationaryRetained, Is.GreaterThan(0), "no stationary member ever retained a run, so the path was not exercised");
            Assert.That(reference.StationaryRetained, Is.Zero, "the reference arm has no views, so nothing can be retained there");
            AssertSameFrames(reference, retained, $"stationary retention at {skipPercent}% skip");
        });
    }

    /// <summary>
    /// The sparse topology emits exactly what the run walk emits with the incremental gather on in both arms, so a failure isolates the sparse path from
    /// the difference it rides on.
    /// </summary>
    /// <param name="skipPercent">The percentage of ticks on which a session's frames are left undrained.</param>
    [Test]
    public void SparseTopologyEmitsExactlyWhatTheRunWalkEmits([Values(0, 60)] int skipPercent)
    {
        var walk = Execute(temporal: true, skipPercent, crowd: true, sparse: false);
        var sparse = Execute(temporal: true, skipPercent, crowd: true, sparse: true);

        Assert.Multiple(() =>
        {
            Assert.That(walk.FramesProduced, Is.GreaterThan(SessionCount), "the walk arm produced too few frames to compare anything");
            Assert.That(sparse.FramesProduced, Is.EqualTo(walk.FramesProduced), "the two arms did not run the same world");
            Assert.That(walk.SparseRunsSkipped, Is.Zero, "the walk arm took the sparse path");
            Assert.That(sparse.SparseRunsSkipped, Is.GreaterThan(0), "no session ever skipped an unchanged run");
            AssertSameFrames(walk, sparse, $"sparse topology at {skipPercent}% skip");
        });
    }

    private static void AssertSameFrames(Run a, Run b, string what)
    {
        for (var s = 0; s < SessionCount; s++)
        {
            Assert.That(b.Frames[s], Has.Count.EqualTo(a.Frames[s].Count), $"session {s}: frame count differs ({what})");
            for (var f = 0; f < Math.Min(a.Frames[s].Count, b.Frames[s].Count); f++)
            {
                Assert.That(b.Frames[s][f], Is.EqualTo(a.Frames[s][f]), $"session {s}, frame {f}: bytes differ ({what})");
            }
        }
    }

    private Run Execute(bool temporal, int skipPercent = 0, bool crowd = false, bool pausing = false, bool? sparse = null)
    {
        // A FRESH service provider per arm. The engine is a singleton of it and its spatial grid may be configured exactly once, so a second arm built on
        // the fixture's own provider is refused with "ConfigureSpatialGrid must be called before InitializeArchetypes". Tearing the provider down and
        // running the fixture's Setup again is what gives the second arm the same starting world as the first, which is the whole premise of comparing them.
        (ServiceProvider as IDisposable)?.Dispose();
        Setup();

        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        using var harness = FrameHarness.Create(dbe, Declare, $"{nameof(IncrementalInterestTests)}-{temporal}", new SubscriptionsOptions
        {
            MaxSessions = 16,
            IncrementalInterest = temporal,

            // The difference arm also takes the sparse topology: a session that is up to date emits runs only where its membership moved, and its
            // content changes arrive through the changed-block tables. It must describe the same world in the same bytes as the full walk.
            SparseTopology = sparse ?? temporal,

            // Raised for the same reason the oracle raises them: a pool that runs out makes the producer SKIP, and a skipped frame is a difference between
            // the arms that has nothing to do with the path under test.
            StatePoolBudgetBytes = 64L * 1024 * 1024,
            FramePoolBudgetBytes = 64L * 1024 * 1024,

            // High enough that the initial fill never defers an enter. A deferred enter is owed, and an owed session takes the full walk — so a binding
            // budget would keep the difference from ever engaging and this fixture would compare the reference path against itself.
            EnterBudgetPerFrame = 100_000,
        });

        harness.RunFence = true;
        var workload = new OracleWorkload(harness, seed: 20260918);
        workload.Seed(creatures: 320, rocks: 120);

        var sessions = harness.OpenSessions(SessionCount, Profile);
        var frames = new List<byte[]>[SessionCount];
        var delivery = new Random[SessionCount];
        for (var s = 0; s < SessionCount; s++)
        {
            frames[s] = [];
            delivery[s] = new Random(90210 + s);
            Assert.That(harness.Sessions.SetViewpoint(sessions[s], pausing ? PausingViewpointAt(s, 1) : crowd ? CrowdViewpointAt(s, 1) : ViewpointAt(s, 1)), Is.True);
        }

        // The priming tick: every hit cluster is given its replication block here and is watched from the next one, so a session's first tick has hits and
        // no records.
        harness.PrimeBlocks();

        for (var tick = 2L; tick <= Ticks + 1; tick++)
        {
            workload.Step();
            for (var s = 0; s < SessionCount; s++)
            {
                Assert.That(harness.Sessions.SetViewpoint(sessions[s], pausing ? PausingViewpointAt(s, tick) : crowd ? CrowdViewpointAt(s, tick) : ViewpointAt(s, tick)), Is.True);
            }

            harness.RunTick(tick, crowd ? 8 : 1);
            for (var s = 0; s < SessionCount; s++)
            {
                // A session whose frames are left undrained fills its hand-off slots, and the engine then refuses to begin another for it (SUB-04). That
                // is the only path on which a session's held membership lags the world by more than one tick, and it is the path the reused-slot defect
                // lived on: a destroy and a spawn into one slot inside the skip window leave the cluster mask identical across the whole of it. The
                // pattern is seeded and identical in both arms, so the two runs skip on the same ticks and the byte comparison stays a comparison.
                if (delivery[s].Next(100) < skipPercent)
                {
                    continue;
                }

                frames[s].AddRange(harness.Collect(sessions[s]));
            }
        }

        // Everything still sitting in a hand-off slot when the run ends is part of what the server said and has to be compared with the rest.
        for (var s = 0; s < SessionCount; s++)
        {
            frames[s].AddRange(harness.Collect(sessions[s]));
        }

        return new Run
        {
            Frames = frames,
            Catalog = harness.CatalogPlan,
            TemporalGathers = harness.Assembler.TemporalGathers,
            FullGathers = harness.Assembler.FullGathers,
            FramesProduced = harness.Assembler.FramesProduced,
            Destroyed = workload.Destroyed,
            Teleports = workload.Teleports,
            SparseRunsSkipped = harness.Subscriptions.Interest.SparseRunsSkipped,
            SyntheticRuns = harness.Assembler.SyntheticRuns,
            RunsRetainedByView = harness.Subscriptions.Interest.RunsRetainedByView,
            StationaryRetained = harness.Subscriptions.Interest.TopologyRunsRetained,
        };
    }

    /// <summary>
    /// The identity table survives a session that reads far more identities than it was sized for.
    /// </summary>
    /// <remarks>
    /// <b>This is a regression test for a hang, not for a slowdown.</b> The table is open-addressed with linear probing, and the probe's only exits are an
    /// empty slot and a match — so a table that fills does not degrade, it spins forever. The first measured run of the difference at four times the density
    /// the hint was chosen for froze the server dead: the tick stopped producing frames entirely and every session was reported unserved. Sizing the table
    /// from the previous tick makes that rare; growing on insert makes it impossible, and this is the assertion that says so.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-18")]
    public void TheIdentityTableGrowsPastTheSizeItWasGivenRatherThanSpinning()
    {
        using var scratch = new FrameIdentityScratch();

        // The table starts at its floor and is never told how much is coming, so the INSERT path is what has to cope with five thousand identities —
        // which is the shape that froze the server when the insert path could not.
        scratch.BeginSession();

        const int Count = 5000;
        for (var i = 1; i <= Count; i++)
        {
            scratch.NoteSeen((uint)i);
        }

        Assert.Multiple(() =>
        {
            for (var i = 1; i <= Count; i++)
            {
                Assert.That(scratch.WasSeen((uint)i), Is.True, $"identity {i} was recorded and then could not be found again, so a leave for it would be "
                    + "emitted although this tick read it");
            }

            Assert.That(scratch.WasSeen(Count + 1), Is.False, "an identity that was never recorded must not be found, or the table answers yes to everything "
                + "and no leave is ever emitted");
        });

        // And the reset really resets: a second session on the same worker must not inherit the first one's identities, which would suppress its leaves.
        scratch.BeginSession();
        Assert.That(scratch.WasSeen(1), Is.False, "the table was not emptied between sessions, so one session's reads would suppress another's leaves");
    }
}
