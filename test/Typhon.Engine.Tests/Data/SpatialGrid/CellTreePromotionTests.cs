using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// Static-mode cluster archetype (#872 step 9, AC-9.4). Mode = Static is what makes
// the tick fence skip this archetype's AABB recompute entirely — which is the
// property AC-9.4 asserts, and it cannot be asserted without an archetype that has it.
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.CellTree.StaticPos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct CtStaticPos
{
    [Field]
    [SpatialIndex(Mode = SpatialMode.Static)]
    public AABB2F Bounds;

    [Field]
    public float Mass;
}

[Archetype]
partial class CtStaticProp : Archetype<CtStaticProp>
{
    public static readonly Comp<CtStaticPos> Pos = Register<CtStaticPos>();
}


/// <summary>
/// The per-cell R-Tree wired into the live query path, behind the promotion threshold (#872 step 9) — <c>AC-9.4</c>, <c>AC-9.5</c> and the differential that
/// makes the hybrid safe at all.
/// </summary>
/// <remarks>
/// <para><b>What has to be true for a threshold-keyed hybrid to be acceptable.</b> Not that the tree is fast — that was measured elsewhere — but that a cell
/// answers the SAME question either side of the threshold. Two structures serving one query shape is an invitation to an <c>SQ-01</c> false negative that only
/// appears above a cluster count no unit test normally reaches, which is the hardest possible place to notice one. So the central test here runs the identical
/// population and the identical queries through both configurations and compares the entity sets.</para>
/// <para>Promotion is disabled by default in production, so every test in this fixture sets the threshold explicitly. That is the point: the code path exists
/// and is exercised, without the engine betting on it.</para>
/// </remarks>
[TestFixture]
class CellTreePromotionTests : TestBase<CellTreePromotionTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private const float CellSize = 1_000f;

    /// <summary>Low enough that a modest population crosses it, high enough that a leaf split is forced first — R3Df32's LeafCapacity is 13 (11 before
    /// #872 step 13 dropped the leaf's ComponentChunkId column).</summary>
    private const int PromoteAt = 24;

    private static ClCohPos PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Mass = 1.0f };

    private DatabaseEngine SetupEngine(IServiceScope scope, int promoteThreshold)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(4_000f, 4_000f), CellSize));

        // Must precede InitializeArchetypes: the threshold is copied onto each ArchetypeClusterState as it is constructed.
        dbe.ClusterCellTreePromoteThreshold = promoteThreshold;
        // Step 16: these fixtures exercise the TREE, not the gate that decides when a cell gets one — their clusters are scattered over the cell
        // on purpose, which is exactly the shape the tightness gate refuses. Count-only promotion keeps them testing what they were written for.
        dbe.ClusterCellTreePromoteTightness = 1f;
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<ClCohUnit>.Metadata.ArchetypeId].ClusterState;

    /// <summary>
    /// Fill one cell with enough entities to force many clusters, then query it. Returns the hit set plus how many cells ended up promoted.
    /// </summary>
    private (HashSet<long> hits, int promotedCells) RunPopulation(int promoteThreshold, int entityCount, float qMin, float qMax)
    {
        // A fresh DATABASE, not merely a fresh scope. Both configurations run inside one test method, and every scope opens the same file under the fixture's
        // database name — so the second engine LOADS the first one's population and spawns another on top. The query then returns exactly twice as many
        // entities, which reads as "the tree over-returns" rather than as "the fixture is comparing 6000 entities against 3000".
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();

        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine(scope, promoteThreshold);

        var rng = new Random(20260903);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < entityCount; i++)
            {
                // All inside cell (0,0) so a single cell accumulates the whole population and crosses the threshold.
                float x = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                float y = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(x, y)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        var cs = ClusterStateOf(dbe);
        var hits = new HashSet<long>();
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            foreach (var r in cs.QueryAabb(dbe.SpatialGrid, qMin, qMin, float.NegativeInfinity, qMax, qMax, float.PositiveInfinity))
            {
                hits.Add(r.EntityId);
            }
        }

        return (hits, cs.PromotedCellCount);
    }

    /// <summary>
    /// <c>AC-9.1</c> at the integration level: a promoted cell returns exactly what the linear scan returns, for the same population and the same query.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("SQ-01")]
    public void PromotedCell_AnswersIdenticallyToTheLinearScan()
    {
        const int EntityCount = 3_000;   // ~47 clusters at 64 entities each, comfortably past PromoteAt
        const float QMin = 200f;
        const float QMax = 500f;

        var linear = RunPopulation(int.MaxValue, EntityCount, QMin, QMax);
        var promoted = RunPopulation(PromoteAt, EntityCount, QMin, QMax);

        TestContext.Out.WriteLine($"PROMOTE linear hits={linear.hits.Count} promotedCells={linear.promotedCells} | "
            + $"tree hits={promoted.hits.Count} promotedCells={promoted.promotedCells}");

        Assert.Multiple(() =>
        {
            // Non-vacuity first: if nothing promoted, the two runs are the same code path and the comparison proves nothing.
            Assert.That(linear.promotedCells, Is.Zero, "the control run must not promote — otherwise it is not a control");
            Assert.That(promoted.promotedCells, Is.GreaterThan(0), "the population must actually cross the threshold, or the tree path never ran");
            Assert.That(promoted.hits, Is.Not.Empty, "the query must return something, or identical empty sets would pass trivially");
            Assert.That(promoted.hits, Is.EquivalentTo(linear.hits),
                "a promoted cell returned a different entity set from the linear scan — the two structures must answer the same question, or the threshold "
                + "silently changes query results");
        });
    }

    /// <summary>
    /// Crossing the threshold downward hands the cell back to the linear index, and the answers survive the round trip.
    /// </summary>
    /// <remarks>
    /// Demotion rebuilds a linear index from the tree's contents and re-issues every back-pointer. Getting that wrong strands clusters — they stay reachable
    /// from neither structure — which reads as an empty region rather than an error.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void DemotionRebuildsTheLinearIndex_AndKeepsAnswering()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine(scope, PromoteAt);

        var rng = new Random(77);
        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 3_000; i++)
            {
                float x = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                float y = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                ids.Add(tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(x, y))));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        var cs = ClusterStateOf(dbe);
        Assert.That(cs.PromotedCellCount, Is.GreaterThan(0), "precondition: the cell must be promoted before demotion can be tested");

        // Destroy most of the population so the cell falls below the demote threshold.
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < ids.Count - 200; i++)
            {
                tx.Destroy(ids[i]);
            }
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        int survivors = 0;
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            foreach (var r in cs.QueryAabb(dbe.SpatialGrid, 0f, 0f, float.NegativeInfinity, CellSize, CellSize, float.PositiveInfinity))
            {
                survivors++;
            }
        }

        TestContext.Out.WriteLine($"PROMOTE after destroy: promotedCells={cs.PromotedCellCount} survivorsFound={survivors} expected={ids.Count - (ids.Count - 200)}");
        Assert.That(survivors, Is.EqualTo(200), "every surviving entity must still be reachable after the cell changed structure");
    }

    /// <summary>
    /// <c>ST-07</c>: the in-place update path leaves leaf MBRs loose, and the fence must make that good before anything queries the tree.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("ST-07")]
    public void FenceLeavesNoLooseLeaves()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine(scope, PromoteAt);

        var rng = new Random(31337);
        var ids = new List<EntityId>();
        var xs = new List<float>();
        var ys = new List<float>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 3_000; i++)
            {
                float x = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                float y = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                xs.Add(x);
                ys.Add(y);
                ids.Add(tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(x, y))));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        var cs = ClusterStateOf(dbe);
        Assert.That(cs.PromotedCellCount, Is.GreaterThan(0), "precondition: nothing is owed a refit if nothing was promoted");

        // Move a slice of the population a little — enough to drive in-place updates through the tree.
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < ids.Count; i += 3)
            {
                float nx = Math.Clamp(xs[i] + (((float)rng.NextDouble() - 0.5f) * 4f), 1f, CellSize - 1f);
                float ny = Math.Clamp(ys[i] + (((float)rng.NextDouble() - 0.5f) * 4f), 1f, CellSize - 1f);
                var eref = tx.OpenMut(ids[i]);
                ref var pos = ref eref.Write(ClCohUnit.Pos);
                pos.Bounds = new AABB2F { MinX = nx, MinY = ny, MaxX = nx, MaxY = ny };
            }
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        int owed = 0;
        for (int i = 0; i < cs.PerCellIndex.Length; i++)
        {
            var slot = cs.PerCellIndex[i];
            owed += slot?.DynamicTree?.LooseLeafCount ?? 0;
            owed += slot?.StaticTree?.LooseLeafCount ?? 0;
        }

        TestContext.Out.WriteLine($"PROMOTE loose leaves owed after the fence: {owed}");
        Assert.That(owed, Is.Zero,
            "the fence must refit every leaf the in-place update path left loose — ST-01 states leaf MBR EQUALITY, and a leaf left wider than the union of "
            + "its entries makes that literally false outside the exclusive window");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // AC-9.4 / AC-9.5
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Register both archetypes so one engine holds a Dynamic and a Static spatial cluster archetype at once.</summary>
    private DatabaseEngine SetupMixedEngine(IServiceScope scope, int promoteThreshold)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.RegisterComponentFromAccessor<CtStaticPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(4_000f, 4_000f), CellSize));
        dbe.ClusterCellTreePromoteThreshold = promoteThreshold;
        // Step 16: these fixtures exercise the TREE, not the gate that decides when a cell gets one — their clusters are scattered over the cell
        // on purpose, which is exactly the shape the tightness gate refuses. Count-only promotion keeps them testing what they were written for.
        dbe.ClusterCellTreePromoteTightness = 1f;
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ArchetypeClusterState StaticStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<CtStaticProp>.Metadata.ArchetypeId].ClusterState;

    private static CtStaticPos StaticPointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Mass = 1.0f };

    /// <summary>Sum of every tree's mutation counter across an archetype — a real write counter, not an absence of evidence.</summary>
    private static int TotalTreeMutations(ArchetypeClusterState cs)
    {
        if (cs.PerCellIndex == null)
        {
            return 0;
        }

        int total = 0;
        for (int i = 0; i < cs.PerCellIndex.Length; i++)
        {
            var slot = cs.PerCellIndex[i];
            total += slot?.DynamicTree?.Tree.MutationVersion ?? 0;
            total += slot?.StaticTree?.Tree.MutationVersion ?? 0;
        }
        return total;
    }

    /// <summary>
    /// <c>AC-9.4</c> — the fence recompute pass must not touch a Static archetype's tree, even while a Dynamic archetype in the same cells is churning.
    /// </summary>
    /// <remarks>
    /// Asserted with a MUTATION COUNTER rather than "no test touched it". An absent write and an untested path look identical from the outside, and the whole
    /// point of the static/dynamic split is that static clusters pay nothing per tick — a claim worth exactly as much as the instrument behind it. The dynamic
    /// archetype is present and moving in the same cells specifically so that a pass which wrongly walked every archetype would be caught.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void StaticTree_IsNotTouchedByTheFence()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupMixedEngine(scope, PromoteAt);

        var rng = new Random(9001);
        var movers = new List<EntityId>();
        var xs = new List<float>();
        var ys = new List<float>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 3_000; i++)
            {
                float x = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                float y = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                xs.Add(x);
                ys.Add(y);
                movers.Add(tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(x, y))));
                tx.Spawn<CtStaticProp>(CtStaticProp.Pos.Set(StaticPointAt(x, y)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        var dynamicState = ClusterStateOf(dbe);
        var staticState = StaticStateOf(dbe);

        Assert.Multiple(() =>
        {
            Assert.That(dynamicState.PromotedCellCount, Is.GreaterThan(0), "the dynamic half must be promoted, or the churn below exercises no tree at all");
            Assert.That(staticState.PromotedCellCount, Is.GreaterThan(0), "the static half must be promoted, or there is no static tree to leave untouched");
        });

        int staticBefore = TotalTreeMutations(staticState);
        int dynamicBefore = TotalTreeMutations(dynamicState);

        // Move every dynamic entity. Nothing touches the static ones.
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < movers.Count; i++)
            {
                float nx = Math.Clamp(xs[i] + (((float)rng.NextDouble() - 0.5f) * 20f), 1f, CellSize - 1f);
                float ny = Math.Clamp(ys[i] + (((float)rng.NextDouble() - 0.5f) * 20f), 1f, CellSize - 1f);
                var eref = tx.OpenMut(movers[i]);
                ref var pos = ref eref.Write(ClCohUnit.Pos);
                pos.Bounds = new AABB2F { MinX = nx, MinY = ny, MaxX = nx, MaxY = ny };
            }
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        int staticAfter = TotalTreeMutations(staticState);
        int dynamicAfter = TotalTreeMutations(dynamicState);

        TestContext.Out.WriteLine($"PROMOTE AC-9.4 static mutations {staticBefore} -> {staticAfter} | dynamic {dynamicBefore} -> {dynamicAfter}");

        Assert.Multiple(() =>
        {
            Assert.That(staticAfter, Is.EqualTo(staticBefore),
                "the fence wrote to a Static archetype's tree — static entities do not move, so the recompute pass must skip them entirely");

            // Non-vacuity: if the dynamic tree did not move either, the fence did nothing at all and the assertion above proves nothing.
            Assert.That(dynamicAfter, Is.GreaterThan(dynamicBefore),
                "the dynamic tree must have been written, or the tick did no work and the static assertion is vacuous");
        });
    }

    /// <summary>
    /// <c>AC-9.5</c> — rebuilding from cluster data reproduces the same trees, compared by CONTENT.
    /// </summary>
    /// <remarks>
    /// <b>Never by node id or structure.</b> Node chunk ids come from allocation order, and a rebuild allocates in a different order than the incremental
    /// spawn path did — so a structural comparison fails on a correct rebuild, which is the trap step 8 already walked into once. What must be reproduced is
    /// what the tree ANSWERS: the same entities, with the same bounds.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void RebuildReproducesTheTrees_ByContent()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine(scope, PromoteAt);

        var rng = new Random(555);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 3_000; i++)
            {
                float x = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                float y = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(x, y)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        var cs = ClusterStateOf(dbe);
        Assert.That(cs.PromotedCellCount, Is.GreaterThan(0), "precondition: a rebuild of an unpromoted archetype would not exercise a tree");

        var before = QueryAll(dbe, cs);
        int promotedBefore = cs.PromotedCellCount;

        // Rebuild from cluster data, exactly as a reopen would.
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            cs.RebuildCellState(dbe.SpatialGrid);
            cs.RebuildClusterAabbs(dbe.SpatialGrid);
        }

        var after = QueryAll(dbe, cs);

        TestContext.Out.WriteLine($"PROMOTE AC-9.5 before={before.Count} after={after.Count} promotedCells {promotedBefore} -> {cs.PromotedCellCount}");

        Assert.Multiple(() =>
        {
            Assert.That(before, Is.Not.Empty, "the population must be queryable before the rebuild, or the comparison is between two empty sets");
            Assert.That(after, Is.EquivalentTo(before), "the rebuild changed what the trees answer");
            Assert.That(cs.PromotedCellCount, Is.EqualTo(promotedBefore), "the rebuild must re-promote the same cells it found above the threshold");
        });
    }

    /// <summary>Every entity a whole-world query returns, with its bounds — the content signature a rebuild has to reproduce.</summary>
    private static HashSet<string> QueryAll(DatabaseEngine dbe, ArchetypeClusterState cs)
    {
        var found = new HashSet<string>();
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        foreach (var r in cs.QueryAabb(dbe.SpatialGrid, 0f, 0f, float.NegativeInfinity, 4_000f, 4_000f, float.PositiveInfinity))
        {
            // Entity AND bounds: an entity that survived into the wrong cluster, or with a bound the rebuild recomputed differently, must not compare equal.
            found.Add($"{r.EntityId}:{r.MinX:R},{r.MinY:R},{r.MaxX:R},{r.MaxY:R}");
        }
        return found;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Concurrency hardening (#872 step 9) — the stale-handle path and the guard
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A handle that does not name its cluster must throw, and — the half that actually matters — must NOT delete whoever it does name.
    /// </summary>
    /// <remarks>
    /// <para><b>Both assertions, because a fix that only adds the throw still loses the victim.</b> Before this change <c>UpdateAt</c> treated every
    /// <c>false</c> from <c>TryUpdateLeafEntryInPlace</c> as a geometric escape and called an unchecked <c>Remove</c> at the handle it had just been told was
    /// not ours — deleting the entry that was, and retiring ITS back-pointer. That is <c>ST-05</c>'s own documented failure, and nothing raised.</para>
    /// <para><b>Why the test has to poison the handle by hand.</b> Under a single writer this state is unreachable: every relocation repairs
    /// <c>PayloadBackPointers</c> and every removal retires it. That is precisely why the branch must fail loudly rather than fall through — reaching it means
    /// <c>ST-05</c> has a gap, and the three non-geometric returns are the only detector for one. A test that waited for the state to occur naturally would
    /// never run.</para>
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("ST-05")]
    public void StaleHandle_ThrowsAndDoesNotDeleteTheClusterItNames()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine(scope, PromoteAt);

        var rng = new Random(606);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 3_000; i++)
            {
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(
                    1f + ((float)rng.NextDouble() * (CellSize - 2f)),
                    1f + ((float)rng.NextDouble() * (CellSize - 2f)))));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        var cs = ClusterStateOf(dbe);
        Assert.That(cs.PromotedCellCount, Is.GreaterThan(0), "precondition: the cell must be promoted, or UpdateAt never reaches the tree");

        // Two live clusters in the promoted cell: the aggressor whose handle we corrupt, and the victim its handle will point at.
        int aggressor = -1;
        int victim = -1;
        for (int chunkId = 0; chunkId < cs.ClusterSpatialIndexSlot.Length && victim < 0; chunkId++)
        {
            if (SpatialRTree<TransientStore>.IsNullHandle(cs.ClusterSpatialIndexSlot[chunkId]))
            {
                continue;
            }
            if (aggressor < 0)
            {
                aggressor = chunkId;
            }
            else
            {
                victim = chunkId;
            }
        }

        Assert.That(victim, Is.GreaterThanOrEqualTo(0), "the fixture needs two live clusters to distinguish 'threw' from 'threw without eating the victim'");

        int victimHandle = cs.ClusterSpatialIndexSlot[victim];
        var slot = cs.PerCellIndex[cs.ClusterCellMap[aggressor]];

        // Point the aggressor at the victim's slot — the exact shape an ST-05 gap would produce.
        cs.ClusterSpatialIndexSlot[aggressor] = victimHandle;

        var moved = new ClusterSpatialAabb
        {
            MinX = 5f, MinY = 5f, MinZ = float.PositiveInfinity,
            MaxX = 6f, MaxY = 6f, MaxZ = float.NegativeInfinity,
            CategoryMask = uint.MaxValue,
        };

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        Assert.Throws<InvalidOperationException>(() => slot.DynamicTree.UpdateAt(aggressor, in moved, out _),
            "a handle that does not name its cluster must be refused, not treated as a geometric escape");

        // The half that matters: the victim is still in the tree and still reachable.
        Assert.Multiple(() =>
        {
            Assert.That(SpatialRTree<TransientStore>.IsNullHandle(cs.ClusterSpatialIndexSlot[victim]), Is.False,
                $"cluster {victim}'s back-pointer was retired by an update that belonged to cluster {aggressor} — ST-05's silent failure");

            var found = new HashSet<int>();
            foreach (int id in slot.DynamicTree.EnumerateClusterIds())
            {
                found.Add(id);
            }
            Assert.That(found, Does.Contain(victim), $"cluster {victim} was deleted from the tree by another cluster's update");
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // #916 O1 — the DFS traversal stack moved out of the enumerator and into a per-thread pool. These are the two properties that move with it.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>AC-5</c>: two live enumerators on one thread, both traversing promoted cells, answer exactly what they answer alone.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the test that fails if the pooled stack is shared naively.</b> Before #916 each enumerator embedded its own 1 KB stack, so nesting
    /// was safe by construction and nothing had to assert it. A single <c>[ThreadStatic]</c> buffer would not crash here — the inner query would overwrite
    /// the outer's traversal state and the outer would resume describing a different subtree, returning a SUBSET. Hence the outer set is compared against
    /// the same query run alone, not against a count.</para>
    /// <para>The inner query runs only on the first few outer iterations. Running it on all of them would multiply a 3 000-entity fixture by its own hit
    /// count for no extra coverage: what has to be true is that the outer enumerator survives an inner traversal <b>begun while it is mid-descent</b>, and
    /// three of those prove it as well as three hundred.</para>
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("SQ-01")]
    public void NestedQueriesOverPromotedCells_AnswerAsTheyDoAlone()
    {
        const int EntityCount = 3_000;
        const int NestedRuns = 3;

        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine(scope, PromoteAt);

        var rng = new Random(20260908);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < EntityCount; i++)
            {
                float x = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                float y = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(x, y)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        var cs = ClusterStateOf(dbe);
        Assert.That(cs.PromotedCellCount, Is.GreaterThan(0), "the population must promote, or neither enumerator descends a tree and the test proves nothing");

        // Two DIFFERENT boxes over the same promoted cell: identical boxes would let a clobbered outer stack still produce the right answer by accident.
        var outerAlone = QueryHits(dbe, cs, 100f, 600f);
        var innerAlone = QueryHits(dbe, cs, 400f, 900f);

        Assert.Multiple(() =>
        {
            Assert.That(outerAlone, Is.Not.Empty, "the outer query must return something, or an empty-vs-empty comparison passes trivially");
            Assert.That(innerAlone, Is.Not.Empty, "the inner query must return something");
            Assert.That(outerAlone, Is.Not.EquivalentTo(innerAlone), "the two boxes must select different sets, or nesting cannot be distinguished");
        });

        var outerNested = new HashSet<long>();
        var innerResults = new List<HashSet<long>>();

        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            foreach (var outerHit in cs.QueryAabb(dbe.SpatialGrid, 100f, 100f, float.NegativeInfinity, 600f, 600f, float.PositiveInfinity))
            {
                outerNested.Add(outerHit.EntityId);

                if (innerResults.Count >= NestedRuns)
                {
                    continue;
                }

                // Begun and fully drained while the outer enumerator is suspended mid-descent — the whole point.
                var inner = new HashSet<long>();
                foreach (var innerHit in cs.QueryAabb(dbe.SpatialGrid, 400f, 400f, float.NegativeInfinity, 900f, 900f, float.PositiveInfinity))
                {
                    inner.Add(innerHit.EntityId);
                }
                innerResults.Add(inner);
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(outerNested, Is.EquivalentTo(outerAlone),
                "the outer query returned a different set when an inner query ran inside it — the two enumerators shared a traversal stack");

            for (int i = 0; i < innerResults.Count; i++)
            {
                Assert.That(innerResults[i], Is.EquivalentTo(innerAlone), $"nested run {i} returned a different set from the same query run alone");
            }

            Assert.That(innerResults, Has.Count.EqualTo(NestedRuns), "the outer query yielded fewer hits than the nested runs needed");
        });
    }

    /// <summary>
    /// <c>AC-2</c>: the query path allocates nothing once the pool is warm, and <c>AC-4</c>'s depth half — no promoted query overflows the 256-slot stack.
    /// </summary>
    /// <remarks>
    /// <para><b>Asserted with a counter, not by inspection</b>, which is what #916 asks for. The FIRST query on a thread does allocate — that is the pooled
    /// buffer being created — so the measurement is taken after a warm-up pass. Steady state is the property that matters: a game loop issuing one query per
    /// agent per tick must not hand the GC a kilobyte each time, which is precisely what the enumerator's inline stack avoided by being on the stack and
    /// what the pool has to keep avoiding now that it is not.</para>
    /// <para>The overflow counter is process-wide and always on (#422). Reading it either side of the queries turns "the pooled stack is deep enough" into a
    /// measurement rather than an assumption — an overflow would silently drop children, so it cannot be left to show up as a wrong answer somewhere else.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("SQ-05")]
    public void WarmQueryPathAllocatesNothing_AndNeverOverflowsTheStack()
    {
        const int EntityCount = 3_000;
        const int MeasuredQueries = 64;

        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine(scope, PromoteAt);

        var rng = new Random(20260909);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < EntityCount; i++)
            {
                float x = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                float y = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(x, y)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        var cs = ClusterStateOf(dbe);
        Assert.That(cs.PromotedCellCount, Is.GreaterThan(0),
            "without a promoted cell no query descends a tree, so neither the pool nor the depth is exercised");

        long overflowsBefore = Interlocked.Read(ref SpatialRTreeDiagnostics.DfsStackOverflowCount);

        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            // Warm-up: creates the pooled buffer and settles every other first-touch cost on this thread.
            long warmHits = 0;
            for (int i = 0; i < 8; i++)
            {
                foreach (var r in cs.QueryAabb(dbe.SpatialGrid, 200f, 200f, float.NegativeInfinity, 500f, 500f, float.PositiveInfinity))
                {
                    warmHits += r.EntityId;
                }
            }
            Assert.That(warmHits, Is.Not.Zero, "the warm-up must actually hit entities, or the measured loop below walks a different path");

            long before = GC.GetAllocatedBytesForCurrentThread();
            long hits = 0;
            for (int i = 0; i < MeasuredQueries; i++)
            {
                foreach (var r in cs.QueryAabb(dbe.SpatialGrid, 200f, 200f, float.NegativeInfinity, 500f, 500f, float.PositiveInfinity))
                {
                    hits += r.EntityId;
                }
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            TestContext.Out.WriteLine($"{MeasuredQueries} warm queries over a promoted cell allocated {allocated} bytes ({hits} accumulated ids)");

            Assert.That(allocated, Is.Zero,
                $"{MeasuredQueries} warm spatial queries allocated {allocated} bytes. The pooled traversal stack must be reused, not reallocated — AC-2.");
        }

        Assert.That(Interlocked.Read(ref SpatialRTreeDiagnostics.DfsStackOverflowCount), Is.EqualTo(overflowsBefore),
            "a promoted query overflowed the 256-slot traversal stack and silently dropped children — the pooled stack is shallower than the inline one was");
    }

    /// <summary>Drain one query into a hit set, inside its own epoch scope.</summary>
    private static HashSet<long> QueryHits(DatabaseEngine dbe, ArchetypeClusterState cs, float qMin, float qMax)
    {
        var hits = new HashSet<long>();
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        foreach (var r in cs.QueryAabb(dbe.SpatialGrid, qMin, qMin, float.NegativeInfinity, qMax, qMax, float.PositiveInfinity))
        {
            hits.Add(r.EntityId);
        }

        return hits;
    }
}
