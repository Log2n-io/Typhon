using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Spatial-index maintenance across every storage shape that can carry a <c>[SpatialIndex]</c> (#704 T2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this axis.</b> #548 was a Versioned <c>[SpatialIndex]</c> update DOUBLE-INSERTING — its back-pointer was keyed on a chunk id that MVCC churns, so
/// the storage mode was the whole story. Every spatial fixture in the suite (<c>ClusterSpatialTests</c>, <c>ClusterSpatial3DTests</c>,
/// <c>ClusterSpatialCoherenceTests</c>, <c>ClusterSpatialAabbRecomputeTests</c>, <c>SpatialEcsIntegrationTests</c>) pins one composition, and none of them
/// crosses spatial with the storage shape.
/// </para>
/// <para>
/// <b>The model is exact, not approximate.</b> The kit places entity <c>i</c> at the lattice point <c>(i·10, i·10, i·10)</c> as a POINT box, with a zero
/// <c>[SpatialIndex]</c> margin — so "how many entities are inside [0, max]" is arithmetic (<see cref="AxisArchetypes.ExpectedInBox"/>) and needs no
/// per-cell expected value. A non-zero margin would make a box query legitimately return entities outside the region, which would turn the model into a
/// bound rather than an equality; the margin's own behaviour is <c>ClusterSpatialTests</c>' subject.
/// </para>
/// <para>
/// <b>Double-insertion is what the count catches.</b> #548's symptom was an entity appearing TWICE in a spatial query after an update. An assertion of the
/// form "id is in the result" cannot see that; an exact count can, which is why every assertion here is a cardinality.
/// </para>
/// <para>
/// <b>WHICH SPATIAL PATH THIS COVERS, stated because a green run would otherwise imply more than it earns.</b> Every archetype the kit builds is
/// cluster-eligible — <c>ClusterStorageMatrixTests</c> asserts precisely that — so these cases drive the <b>cluster-grid</b> path. They do NOT reach the
/// <b>non-cluster R-Tree</b> maintenance path, which is where #548 lives ("the back-pointer is keyed on the component content chunk id, which Versioned MVCC
/// re-mints per revision"); its repro is <c>NonClusterSpatialMaintainerTests</c>. This fixture passing therefore says the cluster-grid path is sound across
/// all five shapes, and says nothing whatever about #548. Extending the kit to a non-cluster composition is the follow-up that would.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class SpatialMatrixTests : TestBase<SpatialMatrixTests>
{
    private const int EntityCount = 40;

    private long _tick;

    [SetUp]
    public void ResetTick() => _tick = 0;

    /// <summary>The spatial compositions the kit builds: every shape but pure-Transient, at <c>Index=None</c>.</summary>
    public static IEnumerable<TestCaseData> Cells() =>
        EngineAxes.PairwiseWhere(c => AxisArchetypes.SupportsSpatial(c) && c.Reopen == ReopenKind.None);

    private DatabaseEngine Open(Cell cell)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        AxisArchetypes.Register(dbe, cell);   // also configures the engine-wide grid — it must precede InitializeArchetypes
        dbe.InitializeArchetypes();
        return dbe;
    }

    private EntityId[] Seed(DatabaseEngine dbe, Cell cell, int count)
    {
        var ids = new EntityId[count];
        using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            for (var i = 0; i < count; i++)
            {
                ids[i] = AxisArchetypes.Spawn(t, cell, i);
            }

            Assert.That(t.Commit(), Is.True, $"{cell}: spawn commit");
        }

        // Spatial maintenance for an SV bounds field lands at the tick fence, exactly as secondary-index maintenance does.
        dbe.WriteTickFence(++_tick);
        return ids;
    }

    // ── Spawn ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [TestCaseSource(nameof(Cells))]
    public void Spawn_PutsEveryEntityInTheGrid_Once(Cell cell)
    {
        using var dbe = Open(cell);
        Seed(dbe, cell, EntityCount);

        using var t = dbe.CreateQuickTransaction();
        foreach (var max in new[] { 0f, 95f, 205f, 10_000f })
        {
            var expected = AxisArchetypes.ExpectedInBox(EntityCount, max);
            Assert.That(AxisArchetypes.QueryInBox(t, cell, max, QueryTerminal.Execute), Is.EqualTo(expected),
                $"{cell}: box [0,{max}] must hold exactly {expected} of {EntityCount} entities — a larger answer means an entity is indexed twice");
        }
    }

    [Test]
    [TestCaseSource(nameof(Cells))]
    public void Spawn_ThenRead_ReturnsTheBoundsAndThePayload(Cell cell)
    {
        using var dbe = Open(cell);
        var ids = Seed(dbe, cell, EntityCount);

        using var read = dbe.CreateQuickTransaction();
        for (var i = 0; i < EntityCount; i++)
        {
            AxisArchetypes.AssertRoundTrip(read, cell, ids[i], i);
        }
    }

    // ── Update — the #548 shape ─────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [TestCaseSource(nameof(Cells))]
    public void MovingAnEntity_DoesNotLeaveItAtTheOldPosition(Cell cell)
    {
        // #548 exactly: an update that inserts the new position without removing the old one leaves the entity in BOTH, so the total count grows even though
        // no entity was spawned. Asserting the total is what makes a double-insert visible; asserting "it is at the new place" would pass.
        using var dbe = Open(cell);
        var ids = Seed(dbe, cell, EntityCount);

        using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            // Move the first half far away, beyond every box the assertions below use.
            for (var i = 0; i < EntityCount / 2; i++)
            {
                AxisArchetypes.MoveSpatial(t, cell, ids[i], i + 500);
            }

            Assert.That(t.Commit(), Is.True, $"{cell}: move commit");
        }

        dbe.WriteTickFence(++_tick);

        using var read = dbe.CreateQuickTransaction();
        var world = AxisArchetypes.QueryInBox(read, cell, 10_000f, QueryTerminal.Execute);
        Assert.That(world, Is.EqualTo(EntityCount),
            $"{cell}: the whole world must still hold exactly {EntityCount} entities after moving half of them — a larger count is a double-insert (#548)");

        // The moved half must have LEFT the near region. Entity i of the unmoved half sits at i*10, so the box [0, (EntityCount-1)*10] holds exactly them.
        var nearMax = (EntityCount - 1) * AxisArchetypes.Spacing;
        var near = AxisArchetypes.QueryInBox(read, cell, nearMax, QueryTerminal.Execute);
        Assert.That(near, Is.EqualTo(EntityCount - EntityCount / 2),
            $"{cell}: the moved entities must no longer answer a query at their OLD position");
    }

    [Test]
    [TestCaseSource(nameof(Cells))]
    public void RepeatedMoves_DoNotAccumulateEntries(Cell cell)
    {
        // A single move can be right while a sequence leaks: each round re-inserts and the stale entries pile up. Three rounds is enough for the total to
        // diverge if any one of them fails to remove.
        using var dbe = Open(cell);
        var ids = Seed(dbe, cell, EntityCount);

        for (var round = 1; round <= 3; round++)
        {
            using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
            {
                for (var i = 0; i < ids.Length; i++)
                {
                    AxisArchetypes.MoveSpatial(t, cell, ids[i], i + round * 100);
                }

                Assert.That(t.Commit(), Is.True, $"{cell}: round {round} commit");
            }

            dbe.WriteTickFence(++_tick);

            using var read = dbe.CreateQuickTransaction();
            Assert.That(AxisArchetypes.QueryInBox(read, cell, 10_000f, QueryTerminal.Execute), Is.EqualTo(EntityCount),
                $"{cell}: after move round {round} the world must still hold exactly {EntityCount} entities");
        }
    }

    // ── Destroy ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [TestCaseSource(nameof(Cells))]
    public void Destroy_RemovesTheEntityFromTheGrid(Cell cell)
    {
        using var dbe = Open(cell);
        var ids = Seed(dbe, cell, EntityCount);

        using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            for (var i = 0; i < ids.Length; i += 2)
            {
                t.Destroy(ids[i]);
            }

            Assert.That(t.Commit(), Is.True, $"{cell}: destroy commit");
        }

        dbe.WriteTickFence(++_tick);

        using var read = dbe.CreateQuickTransaction();
        Assert.That(AxisArchetypes.QueryInBox(read, cell, 10_000f, QueryTerminal.Execute), Is.EqualTo(EntityCount / 2),
            $"{cell}: a destroyed entity must leave the spatial index, not merely the entity map");
    }

    [Test]
    [TestCaseSource(nameof(Cells))]
    public void MoveThenDestroy_InOneTransaction_LeavesNothingBehind(Cell cell)
    {
        // The spatial counterpart of #711's shape: a staged move plus a destroy in one transaction, where the removal has to target whichever position the
        // move would have published.
        using var dbe = Open(cell);
        var ids = Seed(dbe, cell, EntityCount);

        using (var t = dbe.CreateQuickTransaction(cell.Durability, cell.Discipline))
        {
            AxisArchetypes.MoveSpatial(t, cell, ids[3], 900);
            t.Destroy(ids[3]);
            Assert.That(t.Commit(), Is.True, $"{cell}: move-then-destroy commit");
        }

        dbe.WriteTickFence(++_tick);

        using var read = dbe.CreateQuickTransaction();
        Assert.That(AxisArchetypes.QueryInBox(read, cell, 10_000f, QueryTerminal.Execute), Is.EqualTo(EntityCount - 1),
            $"{cell}: exactly one entity was destroyed, so the grid must hold one fewer — neither its old nor its new position may survive");
    }

    // ── Terminal agreement, on the spatial plan path ────────────────────────────────────────────────────────────────

    [Test]
    [TestCaseSource(nameof(Cells))]
    public void AllTerminalsAgree_OnASpatialQuery(Cell cell)
    {
        // The spatial plan path is a different one from the secondary-index path QueryTerminalMatrixTests covers, and a terminal can be right on one and
        // wrong on the other — which is what made #590/#592 a 2x2 rather than a single bug.
        using var dbe = Open(cell);
        Seed(dbe, cell, EntityCount);

        using var t = dbe.CreateQuickTransaction();
        foreach (var max in new[] { 0f, 205f, 10_000f })
        {
            var execute = AxisArchetypes.QueryInBox(t, cell, max, QueryTerminal.Execute);
            var count = AxisArchetypes.QueryInBox(t, cell, max, QueryTerminal.Count);
            var any = AxisArchetypes.QueryInBox(t, cell, max, QueryTerminal.Any);

            Assert.Multiple(() =>
            {
                Assert.That(count, Is.EqualTo(execute), $"{cell}: box [0,{max}] — Count() disagrees with Execute()");
                Assert.That(any > 0, Is.EqualTo(execute > 0), $"{cell}: box [0,{max}] — Any() disagrees with Execute()");
            });
        }
    }
}
