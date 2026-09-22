using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>AC-10.1</c>, <c>AC-10.2</c>, <c>AC-10.8</c> and <c>AC-10.10</c> — intra-cell drift DETECTION (#872 step 10).
/// </summary>
/// <remarks>
/// <para><b>What detection is for.</b> A cluster's bound is set when its entities are placed and only ever grows from
/// there, because <c>ClaimSlotInCell</c> takes the first cluster with a free slot and nothing revisits the decision. Under
/// motion the bound spreads until it covers most of its cell, at which point a narrow query opens every cluster instead of
/// the few it overlaps — the ~24× this issue exists to recover. Detection is the cheap half of the repair: look at
/// everything that moved, decide what actually drifted.</para>
/// <para><b>The reference is <see cref="ClusterDriftOracle"/>, shared with the parallel fixture.</b> One implementation of
/// the rule, measured against by both the serial and the parallel driver, which is what makes their results comparable
/// through a third party rather than only to each other.</para>
/// <para><b>The two-level shape is what these tests pin.</b> A cluster still inside the target extent is never opened, so a
/// tight world costs three float compares per written cluster; only a spread cluster pays a per-entity walk. Getting that
/// backwards would not fail a correctness test — it would just make the scan cost what §5.2 says it must not.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ClusterDriftDetectionTests : TestBase<ClusterDriftDetectionTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private const float CellSize = 100f;
    private const float WorldMax = 1000f;

    /// <summary>P4's default: the target region is a quarter of the cell, so 25 world units here.</summary>
    private const float TargetExtent = CellSize * 0.25f;

    /// <summary>The intra-cell dead zone: 5 world units at the default ratio.</summary>
    private const float DriftMargin = CellSize * 0.05f;

    private static ClMigPos PointAt(float x, float y, int tag = 0) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = tag };

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        // #872 step 12 turned the repair path on by default, and these fixtures are about the DELTA path. Repair legitimately
        // preempts relocation — a cell it re-packs comes out tight, so the drift gate stops firing and the deliberately
        // distinct clusters this fixture builds are collapsed into a Morton packing before a single placement decision is
        // made. Three placement tests and one shrink test went red that way, all of them correctly. Pinning the budget to
        // zero scopes each fixture to the mechanism it is written to measure; it is not a workaround for a defect.
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(WorldMax, WorldMax), CellSize,
            reclusterBudgetMs: 0f, batchSpawnSortThreshold: 0 /* step 15: single-point batches >= 128 would be permuted by the ordering's unstable sort; slot order is what these read */,
            // Constant-mode target (step 14): the oracle defines drifters against the configured ratio on cells too sparse for the density target.
            clusterTargetPackingSlack: 0f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<ClMigUnit>.Metadata.ArchetypeId].ClusterState;

    private static ushort ArchetypeId => Archetype<ClMigUnit>.Metadata.ArchetypeId;

    /// <summary>Move every entity of every cluster through the write barrier, by a per-entity delta the caller supplies.</summary>
    private static unsafe void MoveAll(DatabaseEngine dbe, Func<int, int, (float x, float y)> position)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ClMigUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                ulong bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    int slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    var (x, y) = position(cluster.ChunkId, slot);
                    cluster.WriteSpatial(ClMigUnit.Pos, slot, PointAt(x, y));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
        tx.Commit();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary><c>AC-10.8</c> — a tick in which nothing moved does no relocation work.</summary>
    /// <remarks>
    /// The cheap half of the design's promise. Written first because it is the one that fails if detection is wired to run
    /// unconditionally rather than off the process bitmap, and because a "no work" claim that is never tested is the kind
    /// that quietly stops being true.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void AMotionlessTick_DetectsNothing()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 40; i++)
            {
                tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(20f + (i % 8), 20f + (i / 8), i)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);
        dbe.WriteTickFence(2);   // the quiet tick under test

        var t = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(t.ClustersScanned, Is.Zero, "no cluster was written, so none should have been scanned");
            Assert.That(t.DriftersDetected, Is.Zero);
            Assert.That(t.DriftAbsorbedCount, Is.Zero, "a tick that scanned nothing cannot have absorbed anything either");
        });
    }

    /// <summary><c>AC-10.2</c> — a cluster inside its target extent yields no drifters however much its entities move.</summary>
    [Test]
    [VerifiesRule("CR-03")]
    [CancelAfter(30_000)]
    public void ATightClusterYieldsNoDrifters_EvenWhenEveryEntityMoves()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 40; i++)
            {
                tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(30f, 30f)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Every entity moves, but the cluster stays well inside the 25-unit target extent.
        MoveAll(dbe, (chunk, slot) => (30f + (slot % 4), 30f + (slot % 3)));
        dbe.WriteTickFence(2);

        var t = dbe.GetSpatialTelemetry(ArchetypeId);

        Assert.Multiple(() =>
        {
            Assert.That(t.ClustersScanned, Is.GreaterThan(0), "the cluster was written, so the scan must have considered it");
            Assert.That(t.DriftersDetected, Is.Zero, "no entity left a target region the whole cluster fits inside");
        });
    }

    /// <summary>
    /// <c>AC-10.1</c> as a SET — the entities queued for relocation are exactly the ones the rule rejects, not merely as
    /// many of them.
    /// </summary>
    /// <remarks>
    /// <para><b>Why cardinality is not enough.</b> The sibling test compares <c>DriftersDetected</c> against
    /// <c>expected.Drifters.Count</c>, so a detector that found the right NUMBER of drifters and the wrong ONES passes it —
    /// and an off-by-one slot index, or a walk that read a neighbouring cluster's occupancy, is exactly that shape of bug.
    /// The oracle has returned a sorted <c>(chunk, slot)</c> list all along; this asserts against it.</para>
    /// <para><b>The population is built so that every drifter can be placed</b>, which is what makes set EQUALITY legitimate
    /// rather than mere containment. <c>ChooseRelocationTarget</c> skips full clusters, so in a packed cell a genuine
    /// drifter is detected and then dropped, and the queue is a strict subset of the rule's answer through no fault of
    /// detection. Freeing a slot in every cluster first removes that gap, so queue and oracle must agree exactly — and if
    /// they ever do not, the difference is a real disagreement about the rule rather than an artefact of occupancy.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("CR-03")]
    [CancelAfter(60_000)]
    public void QueuedDriftersAreExactlyTheRulesDrifterSet()
    {
        using var dbe = SetupEngine();
        var cs = ClusterStateOf(dbe);

        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 160; i++)
            {
                ids.Add(tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(40f, 40f, i))));
            }

            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Free a slot in every cluster so placement always has somewhere to put a drifter.
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < ids.Count; i += 7)
            {
                tx.Destroy(ids[i]);
            }

            tx.Commit();
        }
        dbe.WriteTickFence(2);
        dbe.WriteTickFence(3);

        var rng = new Random(9091);
        MoveAll(dbe, (chunk, slot) => (5f + (float)rng.NextDouble() * 90f, 5f + (float)rng.NextDouble() * 90f));
        dbe.WriteTickFence(4);

        var expected = ClusterDriftOracle.Evaluate(dbe, cs, TargetExtent, DriftMargin);

        var queued = new List<(int, int)>();
        for (int i = 0; i < cs.PendingMigrationCount; i++)
        {
            ref readonly var r = ref cs.PendingMigrations[i];
            queued.Add((r.SourceClusterChunkId, r.SourceSlotIndex));
        }

        queued.Sort(static (a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2.CompareTo(b.Item2));

        Assert.Multiple(() =>
        {
            Assert.That(expected.Drifters, Is.Not.Empty, "the population must produce drifters, or this compares two empty sets");
            Assert.That(queued, Is.EqualTo(expected.Drifters),
                "the queued relocation set differs from the set the rule defines — same count is not the same entities");
        });
    }

    /// <summary><c>AC-10.1</c> — every drifter the rule defines is found, against an independently computed oracle.</summary>
    [Test]
    [VerifiesRule("CR-03")]
    [CancelAfter(60_000)]
    public void SpreadClusters_DetectDriftersMatchingTheOracle()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 120; i++)
            {
                tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(40f, 40f)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Spread every cluster far past the 25-unit target extent, but keep everything inside cell (0,0) so this stays an
        // INTRA-cell question — a cell crossing would be the outlier guard's business, not this step's.
        var rng = new Random(1010);
        MoveAll(dbe, (chunk, slot) => (5f + (float)rng.NextDouble() * 90f, 5f + (float)rng.NextDouble() * 90f));
        dbe.WriteTickFence(2);

        var expected = ClusterDriftOracle.Evaluate(dbe, ClusterStateOf(dbe), TargetExtent, DriftMargin);
        var t = dbe.GetSpatialTelemetry(ArchetypeId);

        Assert.Multiple(() =>
        {
            Assert.That(expected.Drifters, Is.Not.Empty, "the population must actually produce drifters, or this compares two zeroes");
            // These two counts mean different things and are equal only because this test writes EVERY entity: the oracle
            // counts every mapped non-empty cluster, production counts the clusters WRITTEN this tick. The equality is a
            // property of the population, not of the rule, and a future population that moves a subset would break it
            // without anything being wrong. Kept because it does catch a scan that visits the wrong set on this input.
            Assert.That(t.ClustersScanned, Is.EqualTo(expected.ClustersScanned),
                "engine and oracle disagree on how many clusters were considered (note: equal only because every entity moved)");
            Assert.That(t.DriftersDetected, Is.EqualTo(expected.Drifters.Count), "engine and oracle disagree on the drifter set");

            // AC-10.10 — the intra-cell margin has its own counter, and it is NOT the cell-crossing one. Asserting both in
            // the same breath is the point: a margin folded into HysteresisAbsorbedCount would still make the line above
            // pass, and the two ratios tune different knobs.
            Assert.That(t.DriftAbsorbedCount, Is.EqualTo(expected.Absorbed), "engine and oracle disagree on how many drifters the margin absorbed");
            Assert.That(t.HysteresisAbsorbedCount, Is.Zero, "nothing crossed a cell boundary here, so the cell-crossing counter must not have moved");
        });
    }
}
