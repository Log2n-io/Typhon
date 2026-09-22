using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Design 23's phase 1: how the interest stage schedules its work — a snapshot claim that never waits, and groups claimed by last tick's cost — changes
/// who resolves what and when, never the answer.
/// </summary>
/// <remarks>
/// <para>
/// <b>The expectation comes from the geometry, not from another arm.</b> One engine per test, so each arm is checked on its own: every session's hit count,
/// on every measured tick, must equal the number of lattice points inside its own radius of its own viewpoint — the check <c>CellKeyedInterestTests</c>
/// makes. Half the crowd moves between the later ticks.
/// </para>
/// <para>
/// <b>The stationary path is not covered here.</b> This harness runs no frame stage, so no session can retain its last frame and every member takes the
/// ordinary path. The stationary skip, with these options at their defaults, is compared with the full walk byte for byte by
/// <c>IncrementalInterestTests.StationaryRetentionEmitsExactlyWhatTheFullWalkEmits</c>, and the block kernel with the candidate filter by
/// <c>IncrementalInterestTests.TheBlockKernelEmitsExactlyWhatTheCandidateFilterEmits</c>.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class InterestClaimOrderTests : TestBase<InterestClaimOrderTests>
{
    private const float Spacing = 7.5f;
    private const int Columns = 133;
    private const int CreatureCount = Columns * Columns;
    private const double Radius = 192d;
    private const double WorldEdge = Columns * Spacing;
    private const int SessionCount = 48;

    private static void DeclareSphere(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile("near", p => p.Sphere(Radius).Of<ProjCreature>());
    }

    private static void Populate(DatabaseEngine dbe)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < CreatureCount; i++)
            {
                var x = i % Columns * Spacing;
                var y = i / Columns * Spacing;
                tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(new ProjBounds
                {
                    Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y },
                    Speed = 1f,
                }));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
    }

    private static int PointsWithin(double cx, double cy)
    {
        var inside = 0;
        const double rsq = Radius * Radius;
        for (var i = 0; i < CreatureCount; i++)
        {
            var dx = (i % Columns * Spacing) - cx;
            var dy = (i / Columns * Spacing) - cy;
            if ((dx * dx) + (dy * dy) <= rsq)
            {
                inside++;
            }
        }

        return inside;
    }

    /// <summary>
    /// A crowd over a handful of adjacent cells, so discs overlap and cells share clusters, plus scattered singles. Seeded, so every arm sees one crowd.
    /// </summary>
    private static Vector3D[] Viewpoints(int step)
    {
        var random = new Random(20260922);
        var centre = WorldEdge / 2d;
        var viewpoints = new Vector3D[SessionCount];
        for (var i = 0; i < SessionCount; i++)
        {
            var spread = i < SessionCount * 3 / 4 ? 70d : 400d;
            var x = centre + (((random.NextDouble() * 2d) - 1d) * spread);
            var y = centre + (((random.NextDouble() * 2d) - 1d) * spread);

            // Every other session moves from tick 4, so the reach, the cost order and the pre-filled set all change under the stage.
            var moved = i % 2 == 0 ? step * 3.3d : 0d;
            viewpoints[i] = new Vector3D(x + moved, y - moved, 0d);
        }

        return viewpoints;
    }

    /// <summary>
    /// Every session resolves exactly its own disc on every tick, whichever way the stage schedules its work.
    /// </summary>
    /// <param name="neverWait">Whether a claim on a cluster being filled reads the page instead of waiting.</param>
    /// <param name="costOrdered">Whether groups are claimed by last tick's cost.</param>
    /// <param name="workers">Chunks, run in parallel when more than one — the only way two workers meet on one fill.</param>
    /// <param name="holdClaims">
    /// Whether every cluster's snapshot claim is taken, and never filled, before the last tick — so every reader finds the fill "in progress" and must
    /// read the page itself. Only with <paramref name="neverWait"/>: a waiting reader would spin forever.
    /// </param>
    /// <param name="prefill">Whether the snapshot is filled by a wave before any group.</param>
    /// <param name="blockKernel">Whether members test boundary clusters from the snapshot's columns.</param>
    [TestCase(false, false, false, false, 1, false, TestName = "ClaimsWaitCellOrder")]
    [TestCase(false, true, false, false, 1, false, TestName = "CostOrderedGroups")]
    [TestCase(true, false, false, false, 1, true, TestName = "EveryClaimBusyReadFromThePage")]
    [TestCase(true, true, false, false, 8, false, TestName = "BothOnEightParallelWorkers")]
    [TestCase(false, false, true, false, 1, false, TestName = "PrefilledSnapshot")]
    [TestCase(false, false, false, true, 1, false, TestName = "BlockKernel")]
    [TestCase(true, false, false, true, 1, true, TestName = "BlockKernelEveryClaimBusy")]
    [TestCase(true, true, true, true, 8, false, TestName = "AllOnEightParallelWorkers")]
    public void EverySessionResolvesItsOwnDisc(bool neverWait, bool costOrdered, bool prefill, bool blockKernel, int workers, bool holdClaims)
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        Populate(dbe);

        var options = new SubscriptionsOptions
        {
            InterestClaimNeverWaits = neverWait,
            InterestCostOrderedGroups = costOrdered,
            InterestPrefillSnapshots = prefill,
            InterestBlockKernel = blockKernel,
        };
        using var harness = InterestHarness.Create(dbe, DeclareSphere,
            $"{nameof(InterestClaimOrderTests)}{workers}{neverWait}{costOrdered}{prefill}{blockKernel}", options);
        var sessions = harness.OpenSessions(SessionCount, "near");
        RunPass(harness, 1, workers);
        harness.CreateRequestedBlocks();

        var costOrderedSeen = false;
        var sharedFrom = harness.Interest.SessionsShared;
        var privateReadsBefore = 0L;
        var checkedSessions = 0;
        for (var tick = 2L; tick <= 6; tick++)
        {
            var viewpoints = Viewpoints(tick >= 4 ? (int)tick : 0);
            for (var i = 0; i < SessionCount; i++)
            {
                Assert.That(harness.Sessions.SetViewpoint(sessions[i], viewpoints[i]), Is.True);
            }

            if (holdClaims && tick == 6)
            {
                privateReadsBefore = harness.Interest.StageShape.PrivateReads;
                var store = harness.Interest.SnapshotOf(0);
                for (var c = 0; store.Covers(c); c++)
                {
                    store.TryGet(c, tick, true, out _, out _);
                }
            }

            var before = harness.Interest.StageShape;
            RunPass(harness, tick, workers);
            costOrderedSeen |= harness.Interest.TickCostOrdered;
            if (prefill && tick == 3)
            {
                // Nobody moved since tick 2, so the wave filled exactly what the cells read: a group that still fills lazily read a cluster the wave missed.
                // With several workers a group can reach a cluster in a block another worker claimed but has not filled yet, so only mostly.
                var after = harness.Interest.StageShape;
                var prefilled = after.Prefills - before.Prefills;
                var lazy = after.Fills - before.Fills;
                Assert.That(prefilled, Is.GreaterThan(100), "the pre-fill wave filled nothing on a steady tick");
                Assert.That(lazy, workers == 1 ? Is.Zero : Is.LessThan(prefilled / 4), $"{lazy} lazy fills beside {prefilled} pre-filled on a steady tick");
            }

            harness.CreateRequestedBlocks();
            if (tick < 3)
            {
                continue;
            }

            var bySession = new Dictionary<uint, int>();
            for (var i = 0; i < harness.Interest.TickSessionCount; i++)
            {
                bySession[harness.Interest.SessionAt(i).Value] = harness.Interest.HitsCountOf(i);
            }

            for (var i = 0; i < SessionCount; i++)
            {
                var expected = PointsWithin(viewpoints[i].X, viewpoints[i].Y);
                Assert.That(bySession[sessions[i].Value], Is.EqualTo(expected),
                    $"tick {tick}: session {i} at ({viewpoints[i].X:F1}, {viewpoints[i].Y:F1}) resolved {bySession[sessions[i].Value]} entities, "
                    + $"not the {expected} inside its radius");
                checkedSessions++;
            }
        }

        Assert.That(checkedSessions, Is.EqualTo(SessionCount * 4));
        Assert.That(harness.Interest.SessionsShared - sharedFrom, Is.GreaterThan(SessionCount * 2),
            "the crowd did not share cells, so the cell path went untested");
        Assert.That(costOrderedSeen, Is.EqualTo(costOrdered), "the cost order was built when it should not have been, or never when it should");
        Assert.That(harness.Interest.SnapshotOf(0).Columnar, Is.EqualTo(blockKernel), "the snapshot's layout does not match the kernel option");
        if (prefill)
        {
            Assert.That(harness.Interest.StageShape.Prefills, Is.GreaterThan(100), "the pre-fill wave filled nothing");
        }

        if (holdClaims)
        {
            Assert.That(harness.Interest.StageShape.PrivateReads - privateReadsBefore, Is.GreaterThan(100),
                "the held claims never sent a reader to the page, so the private path went untested");
        }
    }

    /// <summary>Drives one tick's chunks, in parallel when more than one worker is asked for.</summary>
    private static void RunPass(InterestHarness harness, long tick, int workers)
    {
        if (workers <= 1)
        {
            harness.RunPass(tick);
            return;
        }

        var chunks = harness.Interest.BeginTick(tick, workers);
        Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = workers }, c =>
        {
            using (EpochGuard.Enter(harness.Engine.EpochManager))
            {
                harness.Interest.ExecuteChunk(c, chunks);
            }
        });
    }
}
