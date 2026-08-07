using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Secondary-index maintenance across every (storage shape × index kind) cell, checked by the bidirectional oracle rather than by hand-picked expectations
/// (#704 T2).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this replaces.</b> <c>ClusterIndexTests</c> (SV), <c>ClusterTransientIndexTests</c> (SV+Transient), <c>ClusterPureTransientIndexTests</c> (pure
/// Transient) and <c>ClusterMultiValueIndexTests</c> (SV, AllowMultiple) each test the same five things — spawn populates the index, a mutation moves the key,
/// a destroy removes the entry, a duplicate key's siblings survive, and it all still holds across a cluster boundary — on one pinned (shape, index kind) pair
/// apiece. The behaviour is the axis-independent part; the fixture name was the axis.
/// </para>
/// <para>
/// <b>Why the oracle instead of expected values.</b> <see cref="IndexDataOracle"/> checks the index against the data in BOTH directions: every tree entry
/// points at an entity that really holds that key, and every live entity appears under its key. That is a property, not an example — it holds for any cell
/// without the fixture having to know what the right answer looks like there, which is exactly what makes it parameterisable. A hand-picked "expect 3
/// entities" assertion would have to be re-derived per cell, and re-deriving it per cell is how the suite ended up pinned to one cell in the first place.
/// </para>
/// <para>
/// <b>Not covered here, deliberately.</b> The predicated query tests (<c>EqualityQuery_ExactMatch</c>, <c>TargetedQuery_*</c>, the zone-map assertions) need a
/// <c>WhereField</c> predicate written against a statically-known component type, so they cannot be expressed generically over the kit and stay in their
/// fixtures. So do the index-home and segment assertions, which are claims about one declared composition rather than about the axis.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class ClusterIndexMatrixTests : TestBase<ClusterIndexMatrixTests>
{
    private const int ManyEntities = 96;

    // Monotonic tick number for the fences below. Index maintenance for SingleVersion and Transient indexed fields is
    // applied AT THE FENCE, not at commit — which is what ClusterTransientIndexTests means by "MovesTheKeyAtTheFence".
    // An oracle check between the commit and the fence asserts a state the engine never promised, so every mutating
    // step here is followed by Fence(). Missing this is what made the first run of this fixture report 23 phantom
    // failures.
    private long _tick;

    private void Fence(DatabaseEngine dbe) => dbe.WriteTickFence(++_tick);

    [SetUp]
    public void ResetTick() => _tick = 0;

    /// <summary>Cells that actually have an index. An index matrix over <see cref="IndexShape.None"/> would assert nothing.</summary>
    public static IEnumerable<TestCaseData> IndexedCells() =>
        EngineAxes.PairwiseWhere(c => AxisArchetypes.SupportsBase(c) && c.Reopen == ReopenKind.None && c.Index != IndexShape.None);

    /// <summary>Cells whose index permits duplicates — the only ones where "the siblings at this key survive" means anything.</summary>
    public static IEnumerable<TestCaseData> MultiValueCells() =>
        EngineAxes.PairwiseWhere(c => AxisArchetypes.SupportsBase(c) && c.Reopen == ReopenKind.None && c.Index == IndexShape.AllowMultiple);

    /// <summary>
    /// Indexed cells MINUS the mixed-publication-timing shapes, which #711 breaks. Only the behaviours that stage a KEY MOVE narrow this way — spawn and
    /// destroy-without-a-move pass on every shape and keep their full cell set, so the exclusion is as small as the bug is.
    /// </summary>
    public static IEnumerable<TestCaseData> KeyMoveCells() =>
        EngineAxes.PairwiseWhere(c => AxisArchetypes.SupportsBase(c) && c.Reopen == ReopenKind.None
            && c.Index != IndexShape.None && !AxisArchetypes.MixesPublicationTimings(c));

    /// <summary>Duplicate-key cells, minus #711's shapes, for the same reason.</summary>
    public static IEnumerable<TestCaseData> MultiValueKeyMoveCells() =>
        EngineAxes.PairwiseWhere(c => AxisArchetypes.SupportsBase(c) && c.Reopen == ReopenKind.None
            && c.Index == IndexShape.AllowMultiple && !AxisArchetypes.MixesPublicationTimings(c));

    private sealed class OracleVisitor : ICellVisitor<(DatabaseEngine Dbe, string When), bool>
    {
        public bool Visit<TArch>((DatabaseEngine Dbe, string When) arg) where TArch : Archetype<TArch>
        {
            IndexDataOracle.AssertIndexAgreesWithData<TArch>(arg.Dbe, arg.When);
            return true;
        }
    }

    private sealed class CountVisitor : ICellVisitor<Transaction, int>
    {
        public int Visit<TArch>(Transaction t) where TArch : Archetype<TArch> => t.Query<TArch>().Count();
    }

    private DatabaseEngine Open(Cell cell)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        AxisArchetypes.Register(dbe, cell);
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static void AssertOracle(DatabaseEngine dbe, Cell cell, string when) =>
        AxisArchetypes.Dispatch(cell, (dbe, $"{cell} — {when}"), new OracleVisitor());

    private static int LiveCount(DatabaseEngine dbe, Cell cell)
    {
        using var t = dbe.CreateQuickTransaction();
        return AxisArchetypes.Dispatch(cell, t, new CountVisitor());
    }

    private EntityId[] Seed(DatabaseEngine dbe, Cell cell, int count)
    {
        var ids = new EntityId[count];

        // An explicit block, not `using var`: the fence must run AFTER the transaction is disposed, and a `using var`
        // would hold it open until the method returned.
        using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            for (var i = 0; i < count; i++)
            {
                ids[i] = AxisArchetypes.Spawn(t, cell, i);
            }

            Assert.That(t.Commit(), Is.True, $"{cell}: spawn commit");
        }

        Fence(dbe);
        return ids;
    }

    // ── Spawn ───────────────────────────────────────────────────────────────────────────────────────────────────────
    // Replaces ClusterIndexTests.BulkSpawn_AllIndexed, ClusterTransientIndexTests.Spawn_PopulatesTheTransientIndex and
    // ClusterPureTransientIndexTests.Spawn_PopulatesThePerArchetypeIndex.

    [Test]
    [TestCaseSource(nameof(IndexedCells))]
    public void Spawn_LeavesTheIndexAgreeingWithTheData(Cell cell)
    {
        using var dbe = Open(cell);
        Seed(dbe, cell, 32);
        AssertOracle(dbe, cell, "after spawn");
    }

    // ── Mutation moves the key ──────────────────────────────────────────────────────────────────────────────────────
    // Replaces ClusterIndexTests.TargetedQuery_AfterMutation_ReturnsUpdatedResults,
    // ClusterTransientIndexTests.Mutate_TransientIndexedField_MovesTheKeyAtTheFence and
    // ClusterPureTransientIndexTests.{Mutate_MovesTheKeyInTheTreeAtTheFence, Query_AfterMutatingOutOfTheSpawnRange_StillFindsTheEntity}.

    [Test]
    [TestCaseSource(nameof(IndexedCells))]
    public void Mutation_MovesTheKey_AndTheIndexStillAgrees(Cell cell)
    {
        using var dbe = Open(cell);
        var ids = Seed(dbe, cell, 32);

        // Move every key well OUT of the range the spawn used (payload i+500), which is what catches an index that
        // updated the data but left the old key in the tree — the entity is then findable under a value it no longer holds.
        using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            for (var i = 0; i < ids.Length; i++)
            {
                AxisArchetypes.Update(t, cell, ids[i], i + 500);
            }

            Assert.That(t.Commit(), Is.True, $"{cell}: update commit");
        }

        Fence(dbe);

        AssertOracle(dbe, cell, "after moving every key out of the spawn range");

        using var read = dbe.CreateQuickTransaction();
        for (var i = 0; i < ids.Length; i++)
        {
            AxisArchetypes.AssertRoundTrip(read, cell, ids[i], i + 500);
        }
    }

    // ── Destroy ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Replaces ClusterIndexTests.{Destroy_WithoutWrite_IndexEntryRemoved, Destroy_MutateAndDestroy_SameTransaction}.

    [Test]
    [TestCaseSource(nameof(IndexedCells))]
    public void Destroy_RemovesTheEntry_AndTheIndexStillAgrees(Cell cell)
    {
        using var dbe = Open(cell);
        var ids = Seed(dbe, cell, 32);

        using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            for (var i = 0; i < ids.Length; i += 2)
            {
                t.Destroy(ids[i]);
            }

            Assert.That(t.Commit(), Is.True, $"{cell}: destroy commit");
        }

        Fence(dbe);

        Assert.That(LiveCount(dbe, cell), Is.EqualTo(16), $"{cell}: half the entities were destroyed");
        AssertOracle(dbe, cell, "after destroying half");
    }

    [Test]
    [TestCaseSource(nameof(KeyMoveCells))]
    public void MutateAndDestroy_InOneTransaction_LeaveTheIndexAgreeing(Cell cell)
    {
        // The ordering hazard: a write stages a key move and the destroy then has to remove the entry the move would
        // have created, not the one it replaced.
        using var dbe = Open(cell);
        var ids = Seed(dbe, cell, 16);

        using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            AxisArchetypes.Update(t, cell, ids[3], 900);
            t.Destroy(ids[3]);
            Assert.That(t.Commit(), Is.True, $"{cell}: mutate-then-destroy commit");
        }

        Fence(dbe);

        Assert.That(LiveCount(dbe, cell), Is.EqualTo(15), $"{cell}: exactly one entity was destroyed");
        AssertOracle(dbe, cell, "after mutate-then-destroy in one transaction");
    }

    // ── Duplicate keys ──────────────────────────────────────────────────────────────────────────────────────────────
    // Replaces ClusterTransientIndexTests.Destroy_AllowMultipleTransientKey_PreservesSiblings,
    // ClusterPureTransientIndexTests.Destroy_AllowMultipleKey_PreservesSiblings and
    // ClusterMultiValueIndexTests.{SvMultiValueShadowedDestroy_AtFence_SiblingAtSameKeySurvives,
    // SvMultiValueMutation_AtFence_SiblingAtSameKeySurvives}.

    [Test]
    [TestCaseSource(nameof(MultiValueCells))]
    public void DestroyingOneOfADuplicateKey_LeavesItsSiblingsIndexed(Cell cell)
    {
        using var dbe = Open(cell);

        // The kit's Bucket key is i % 4, so 32 entities put 8 under each key — the precondition for "siblings" to exist
        // at all. A multi-value index over distinct values never exercises the duplicate path.
        var ids = Seed(dbe, cell, 32);

        using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            t.Destroy(ids[0]);   // bucket 0
            t.Destroy(ids[4]);   // bucket 0 again — two siblings of the same key removed in one transaction
            Assert.That(t.Commit(), Is.True, $"{cell}: destroy commit");
        }

        Fence(dbe);

        Assert.That(LiveCount(dbe, cell), Is.EqualTo(30), $"{cell}: only the two named entities were destroyed");
        AssertOracle(dbe, cell, "after destroying two siblings of one duplicate key");
    }

    [Test]
    [TestCaseSource(nameof(MultiValueKeyMoveCells))]
    public void RepeatedMovesAcrossDuplicateKeys_KeepTheIndexAgreeing(Cell cell)
    {
        // ClusterMultiValueIndexTests.SvMultiValueMutation_AtFence_RepeatedMovesKeepBufferIntact pinned this on one
        // shape: a multi-value key's element buffer must survive an entity being moved in and out of it repeatedly.
        using var dbe = Open(cell);
        var ids = Seed(dbe, cell, 32);

        for (var round = 0; round < 3; round++)
        {
            using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
            {
                for (var i = 0; i < ids.Length; i += 3)
                {
                    AxisArchetypes.Update(t, cell, ids[i], i + round * 7 + 1);
                }

                Assert.That(t.Commit(), Is.True, $"{cell}: round {round} commit");
            }

            Fence(dbe);
            AssertOracle(dbe, cell, $"after move round {round}");
        }
    }

    // ── Across a cluster boundary ───────────────────────────────────────────────────────────────────────────────────
    // Replaces ClusterIndexTests.BulkSpawn_MultipleCluster_ZoneMapPerCluster's index half.

    [Test]
    [TestCaseSource(nameof(IndexedCells))]
    public void ManyEntities_AcrossClusters_LeaveTheIndexAgreeing(Cell cell)
    {
        using var dbe = Open(cell);
        Seed(dbe, cell, ManyEntities);

        Assert.That(LiveCount(dbe, cell), Is.EqualTo(ManyEntities), $"{cell}: every entity is live");
        AssertOracle(dbe, cell, $"after spawning {ManyEntities} across more than one cluster");
    }

    // ── #711, quarantined ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// #711 — on an archetype whose components publish at different times, a staged key move plus a destroy in one
    /// transaction leaves the secondary index disagreeing with the data. Quarantined rather than deleted: it is the
    /// regression lock for when #711 is fixed, and it fails today for exactly the reason the issue documents.
    /// </summary>
    [Test]
    [Category("Quarantine")]
    public void MixedPublicationTimings_MutateAndDestroy_LeaveTheIndexAgreeing()
    {
        var cell = new Cell(StorageShape.VerPlusTransient, DurabilityMode.Immediate, IndexShape.AllowMultiple, ReopenKind.None);
        Assert.That(AxisArchetypes.MixesPublicationTimings(cell), Is.True, "precondition: this is one of #711's shapes");

        using var dbe = Open(cell);
        var ids = Seed(dbe, cell, 16);

        using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            AxisArchetypes.Update(t, cell, ids[3], 900);
            t.Destroy(ids[3]);
            Assert.That(t.Commit(), Is.True, $"{cell}: mutate-then-destroy commit");
        }

        Fence(dbe);
        AssertOracle(dbe, cell, "#711 — after mutate-then-destroy on a mixed-timing archetype");
    }
}
