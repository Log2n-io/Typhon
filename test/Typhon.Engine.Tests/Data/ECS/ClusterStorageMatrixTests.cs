using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// The cluster storage behaviours that <c>ClusterStorageTests</c>, <c>ClusterVersionedTests</c> and <c>ClusterTransientTests</c> each tested on ONE storage
/// shape, run on every shape instead (#704 T2).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this replaces.</b> Six behaviours — cluster eligibility, spawn+read, in-place write, destroy, multi-cluster fan-out and bulk iteration — were
/// written two to four times each across those three fixtures, once per storage shape the author had in mind. Same assertions, different pinned axis value.
/// Here each behaviour is one body driven by <see cref="EngineAxes"/>, so it runs on all six shapes and all three index kinds rather than on the one the
/// fixture was named after. Fewer lines, more cells.
/// </para>
/// <para>
/// <b>What it deliberately does NOT replace.</b> The layout and introspection tests in <c>ClusterStorageTests</c> (<c>ClusterInfo_*</c>,
/// <c>TryGetCluster*</c>, <c>EnumerateStorageSegments</c>) assert facts about a specific declared component set — a stride, a packing decision, a reported
/// name list. Those are not axis-dependent and running them on six shapes would add cost and no information. Neither are the mode-SPECIFIC behaviours that
/// only one shape can express, such as <c>ClusterTransientTests.PureTransient_NoPageCacheSegment</c>. They stay where they are.
/// </para>
/// <para>
/// <b>Reopen is not an axis here.</b> Every case is in-session. Crossing storage shape with the crash path is what found #710, and it lives in
/// <c>AxisArchetypesTests</c> where the durability contract is stated once; duplicating it per behaviour would multiply cost without adding a cell.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class ClusterStorageMatrixTests : TestBase<ClusterStorageMatrixTests>
{
    // Enough to span more than one cluster at any ClusterSize in [8,64], so the second-cluster and placement paths are exercised rather than assumed.
    private const int ManyEntities = 96;

    /// <summary>In-session cells the kit can build. Reopen and the two unbuilt axes are narrowed away explicitly rather than skipped at run time.</summary>
    public static IEnumerable<TestCaseData> Cells() =>
        EngineAxes.PairwiseWhere(c => AxisArchetypes.SupportsBase(c) && c.Reopen == ReopenKind.None);

    private DatabaseEngine Open(Cell cell)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        AxisArchetypes.Register(dbe, cell);
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ── Behaviour 1: every shape the kit builds is cluster-eligible ──────────────────────────────────────────────────────────────────────────────────────
    // Replaces ClusterVersionedTests.MixedArchetype_IsClusterEligible, ClusterTransientTests.{MixedSvTransient,PureTransient,ThreeWay}_IsClusterEligible and
    // ClusterStorageTests.ClusterEligible_{Sv,Versioned}Archetype_HasClusterState — six hand-written tests asserting one property of six different archetypes.

    private sealed class ClusterStateVisitor : ICellVisitor<DatabaseEngine, ArchetypeClusterState>
    {
        public ArchetypeClusterState Visit<TArch>(DatabaseEngine dbe) where TArch : Archetype<TArch> =>
            dbe._archetypeStates[Archetype<TArch>.Metadata.ArchetypeId].ClusterState;
    }

    private sealed class EligibilityVisitor : ICellVisitor<DatabaseEngine, (bool Eligible, bool HasLayout)>
    {
        public (bool Eligible, bool HasLayout) Visit<TArch>(DatabaseEngine dbe) where TArch : Archetype<TArch>
        {
            var meta = Archetype<TArch>.Metadata;
            return (meta.IsClusterEligible, meta.ClusterLayout != null);
        }
    }

    [Test]
    [TestCaseSource(nameof(Cells))]
    public void EveryShape_IsClusterEligible_WithTheSegmentsItsCompositionImplies(Cell cell)
    {
        using var dbe = Open(cell);
        var (eligible, hasLayout) = AxisArchetypes.Dispatch(cell, dbe, new EligibilityVisitor());
        var clusterState = AxisArchetypes.Dispatch(cell, dbe, new ClusterStateVisitor());

        // Versioned-only is cluster-backed too, and that is not obvious: #629 inverted it. The cluster holds the HEAD in its slot while the history stays in
        // the revision chain — the arrangement mixed SV+Versioned archetypes had used since Phase 5, so composition was never the obstacle.
        Assert.That(eligible, Is.True, $"{cell}: every shape the kit builds must be cluster-eligible");
        Assert.That(hasLayout, Is.True, $"{cell}: a cluster-eligible archetype must have a ClusterLayout");

        Assert.That(clusterState, Is.Not.Null,
            $"{cell}: the archetype must be cluster-backed. A null ClusterState means the test silently exercised the non-cluster path and proved nothing "
            + "about clustering — the failure mode #655 left behind for pure-Transient archetypes");

        // Segment presence stated as a FUNCTION of the shape rather than as six hand-written expectations. This is what makes the assertion stronger than the
        // per-fixture versions it replaces: those each pinned one shape's answer, so nothing checked that the rule itself held across the axis.
        Assert.Multiple(() =>
        {
            Assert.That(clusterState.ClusterSegment, cell.HasDurableComponent ? Is.Not.Null : Is.Null,
                $"{cell}: a PersistentStore cluster segment must exist iff the shape has a non-Transient component");
            Assert.That(clusterState.TransientSegment, cell.HasTransient ? Is.Not.Null : Is.Null,
                $"{cell}: a TransientStore segment must exist iff the shape has a Transient component");
        });
    }

    // ── Behaviour 2: spawn then read ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // Replaces ClusterVersionedTests.SpawnAndRead_VersionedHead_CorrectValue, ClusterTransientTests.{MixedSvT_SpawnAndRead_BothComponents,
    // PureTransient_SpawnAndRead, ThreeWay_SpawnAndRead_AllThree} and ClusterStorageTests.Read_SpawnedEntity_CorrectData.

    [Test]
    [TestCaseSource(nameof(Cells))]
    public void Spawn_ThenRead_ReturnsEveryField(Cell cell)
    {
        using var dbe = Open(cell);

        using var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline);
        var id = AxisArchetypes.Spawn(t, cell, 7);
        Assert.That(t.Commit(), Is.True, $"{cell}: spawn commit");

        using var read = dbe.CreateQuickTransaction();
        AxisArchetypes.AssertRoundTrip(read, cell, id, 7);
    }

    // ── Behaviour 3: an in-place write is visible to the next read ──────────────────────────────────────────────────────────────────────────────────────
    // Replaces ClusterVersionedTests.{WriteVersioned_CommitUpdatesClusterSlot, WriteSvComponent_InMixedArchetype_InPlaceUpdate,
    // MultipleWritesSameTransaction_FinalValuePersists}, ClusterTransientTests.{Transient_Write_InPlace, ThreeWay_WriteAll_CorrectValues} and
    // ClusterStorageTests.Write_Entity_DataPersisted.

    [Test]
    [TestCaseSource(nameof(Cells))]
    public void Write_ThenRead_SeesTheNewValue(Cell cell)
    {
        using var dbe = Open(cell);

        EntityId id;
        using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            id = AxisArchetypes.Spawn(t, cell, 3);
            Assert.That(t.Commit(), Is.True, $"{cell}: spawn commit");
        }

        using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            AxisArchetypes.Update(t, cell, id, 11);
            Assert.That(t.Commit(), Is.True, $"{cell}: update commit");
        }

        using var read = dbe.CreateQuickTransaction();
        AxisArchetypes.AssertRoundTrip(read, cell, id, 11);
    }

    [Test]
    [TestCaseSource(nameof(Cells))]
    public void TwoWritesInOneTransaction_KeepTheSecond(Cell cell)
    {
        using var dbe = Open(cell);

        EntityId id;
        using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            id = AxisArchetypes.Spawn(t, cell, 3);
            Assert.That(t.Commit(), Is.True, $"{cell}: spawn commit");
        }

        using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            AxisArchetypes.Update(t, cell, id, 11);
            AxisArchetypes.Update(t, cell, id, 19);
            Assert.That(t.Commit(), Is.True, $"{cell}: update commit");
        }

        using var read = dbe.CreateQuickTransaction();
        AxisArchetypes.AssertRoundTrip(read, cell, id, 19);
    }

    // ── Behaviour 4: destroy frees the slot and the entity stops resolving ──────────────────────────────────────────────────────────────────────────────
    // Replaces ClusterVersionedTests.Destroy_VersionedClusterEntity_SlotFreed, ClusterTransientTests.{Destroy_BothSegmentsFreed, Destroy_PureTransient} and
    // ClusterStorageTests.{Destroy_Entity_OccupancyCleared, Destroy_AllInCluster_ClusterFreed}.

    [Test]
    [TestCaseSource(nameof(Cells))]
    public void Destroy_FreesTheSlot_AndTheEntityStopsResolving(Cell cell)
    {
        using var dbe = Open(cell);

        EntityId id;
        using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            id = AxisArchetypes.Spawn(t, cell, 5);
            Assert.That(t.Commit(), Is.True, $"{cell}: spawn commit");
        }

        Assert.That(LiveCount(dbe, cell), Is.EqualTo(1), $"{cell}: precondition — the entity is there before the destroy");

        using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            t.Destroy(id);
            Assert.That(t.Commit(), Is.True, $"{cell}: destroy commit");
        }

        Assert.That(LiveCount(dbe, cell), Is.Zero, $"{cell}: the destroyed entity must leave the cluster's occupancy");
    }

    // ── Behaviour 5: many entities span more than one cluster and all stay correct ──────────────────────────────────────────────────────────────────────
    // Replaces ClusterVersionedTests.{ManyEntities_AcrossClusters_AllCorrect, MultipleEntities_AllHeadsCorrect},
    // ClusterTransientTests.ManyEntities_MultiCluster
    // and ClusterStorageTests.{Spawn_FillOneCluster_AllReadable, Spawn_Overflow_SecondCluster, BatchSpawn_MultipleClustersFilled}.

    [Test]
    [TestCaseSource(nameof(Cells))]
    public void ManyEntities_AcrossClusters_AllReadBackCorrectly(Cell cell)
    {
        using var dbe = Open(cell);

        var ids = new EntityId[ManyEntities];
        using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            for (var i = 0; i < ManyEntities; i++)
            {
                ids[i] = AxisArchetypes.Spawn(t, cell, i);
            }

            Assert.That(t.Commit(), Is.True, $"{cell}: spawn commit");
        }

        Assert.That(LiveCount(dbe, cell), Is.EqualTo(ManyEntities), $"{cell}: every spawned entity is live");

        // More than one cluster is the PRECONDITION for the placement paths to mean anything: with a single cluster, code that pins a destination slot passes.
        var clusterState = AxisArchetypes.Dispatch(cell, dbe, new ClusterStateVisitor());
        Assert.That(clusterState.ActiveClusterCount, Is.GreaterThan(1),
            $"{cell}: {ManyEntities} entities must span more than one cluster, or this test proves nothing about placement");

        using var read = dbe.CreateQuickTransaction();
        for (var i = 0; i < ManyEntities; i++)
        {
            // Per-entity distinctive payloads: a slot mix-up across the cluster boundary surfaces as ANOTHER entity's value, not as a plausible one.
            AxisArchetypes.AssertRoundTrip(read, cell, ids[i], i);
        }
    }

    // ── Behaviour 6: bulk iteration agrees with individual reads ────────────────────────────────────────────────────────────────────────────────────────
    // Replaces ClusterVersionedTests.{BulkIteration_ReadsVersionedHeadFromCluster, BulkIteration_AfterVersionedWrite_SeesNewHead,
    // BulkIteration_MatchesIndividualReads},
    // ClusterTransientTests.{BulkIteration_DualSegment_CorrectValues, BulkIteration_PureTransient_CorrectValues} and
    // ClusterStorageTests.Iteration_AllEntities_ProcessedOnce. This is a differential assertion, not an example: the query path and the per-entity path must
    // agree, which is a property no single hand-picked expected value can express.

    [Test]
    [TestCaseSource(nameof(Cells))]
    public void BulkIteration_VisitsEveryEntityExactlyOnce_AfterAnUpdate(Cell cell)
    {
        using var dbe = Open(cell);

        const int count = 24;
        var ids = new EntityId[count];
        using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            for (var i = 0; i < count; i++)
            {
                ids[i] = AxisArchetypes.Spawn(t, cell, i);
            }

            Assert.That(t.Commit(), Is.True, $"{cell}: spawn commit");
        }

        // Update half of them, so iteration has to read the CURRENT value rather than whatever the spawn wrote — the case
        // ClusterVersionedTests.BulkIteration_AfterVersionedWrite_SeesNewHead was pinned on.
        using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            for (var i = 0; i < count; i += 2)
            {
                AxisArchetypes.Update(t, cell, ids[i], i + 100);
            }

            Assert.That(t.Commit(), Is.True, $"{cell}: update commit");
        }

        Assert.That(LiveCount(dbe, cell), Is.EqualTo(count), $"{cell}: iteration must see each entity exactly once — no duplicates, no drops");

        using var read = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            AxisArchetypes.AssertRoundTrip(read, cell, ids[i], i % 2 == 0 ? i + 100 : i);
        }
    }

    // ── The nightly tier: the same behaviour at strength 3 ──────────────────────────────────────────────────────────────────────────────────────────────
    //
    // `EngineAxes.Triplewise` had ZERO callers before #704 — the covering array shipped with its own nightly mode unused. This is that consumer, and it is
    // what makes "coverage grows with CI-hours" concrete rather than aspirational: the PR gate pays for pairwise, and the nightly explores every TRIPLE of
    // axis values for a few minutes more. Pairwise catches defects triggered by one parameter or an interaction of two; a 3-way interaction — storage shape ×
    // index kind × discipline, say — is exactly the shape #710 turned out to have.
    //
    // [Explicit] + [Category("Nightly")] per the #703 taxonomy: the marker is honest only because nightly-suppressed.yml actually runs that tier.

    public static IEnumerable<TestCaseData> NightlyCells() =>
        EngineAxes.TriplewiseWhere(c => AxisArchetypes.SupportsBase(c) && c.Reopen == ReopenKind.None);

    [Test]
    [Explicit("nightly tier — triplewise is ~4x the pairwise case count")]
    [Category("Nightly")]
    [TestCaseSource(nameof(NightlyCells))]
    public void Nightly_ManyEntities_AcrossClusters_AllReadBackCorrectly(Cell cell)
    {
        ManyEntities_AcrossClusters_AllReadBackCorrectly(cell);
    }

    private sealed class CountVisitor : ICellVisitor<Transaction, int>
    {
        public int Visit<TArch>(Transaction t) where TArch : Archetype<TArch> => t.Query<TArch>().Count();
    }

    private static int LiveCount(DatabaseEngine dbe, Cell cell)
    {
        using var t = dbe.CreateQuickTransaction();
        return AxisArchetypes.Dispatch(cell, t, new CountVisitor());
    }
}
