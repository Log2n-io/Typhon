using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ══════════════════════════════════════════════════════════════════════════
// #1594 — branch 2 of the fence's Prep allocated two long[] per dirty archetype every tick: the dirty-bitmap snapshot, then the
// pre-size's Array.Resize of it. On the SWG server at 2 000 bots, 47 % of every byte the process allocated.
//
// Own archetype, for the reason CleanBranchChangeListTests gives: ArchetypeRegistry is process-global (#720).
// ══════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.Dsp.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct DspPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

[Archetype]
partial class DspUnit : Archetype<DspUnit>
{
    public static readonly Comp<DspPos> Pos = Register<DspPos>();
}

/// <summary>
/// The dirty-bitmap snapshot of branch 2 is one of two retained buffers, alternated (#1594, design/Runtime/11-prep-phase-optimisation.md §7.3): the one
/// published as <c>PreviousTickDirtySnapshot</c> is read by the next tick's change-filtered dispatch, so the next Prep takes the other, and only the words
/// past the bitmap's are cleared. A buffer grown mid-tick stays in the pair.
/// </summary>
[TestFixture]
[NonParallelizable]
class DirtySnapshotPairTests : TestBase<DirtySnapshotPairTests>
{
    private const float CellSize = 100f;
    private const float WorldMax = 1000f;

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<DspPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(WorldMax, WorldMax),
            cellSize: CellSize,
            reclusterBudgetMs: 0f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static DspPos PointAt(float x, float y) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y } };

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) => dbe._archetypeStates[Archetype<DspUnit>.Metadata.ArchetypeId].ClusterState;

    private static EntityId Spawn(DatabaseEngine dbe, float x, float y)
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<DspUnit>(DspUnit.Pos.Set(PointAt(x, y)));
        tx.Commit();
        return id;
    }

    /// <summary>A write through <c>OpenMut</c>, in place: it marks the dirty bitmap, which is what routes the fence to branch 2.</summary>
    private static void Touch(DatabaseEngine dbe, EntityId id, float x, float y)
    {
        using var tx = dbe.CreateQuickTransaction();
        tx.OpenMut(id).Set(DspUnit.Pos, PointAt(x, y));
        tx.Commit();
    }

    /// <summary>The clusters a snapshot names dirty, as chunk ids.</summary>
    private static List<int> DirtyClusters(long[] snapshot)
    {
        var clusters = new List<int>();
        for (var w = 0; snapshot != null && w < snapshot.Length; w++)
        {
            if (snapshot[w] != 0)
            {
                clusters.Add(w);
            }
        }

        return clusters;
    }

    private static unsafe int ClusterOf(DatabaseEngine dbe, EntityId id)
    {
        var cs = ClusterStateOf(dbe);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (var i = 0; i < cs.ActiveClusterCount; i++)
            {
                var cid = cs.ActiveClusterIds[i];
                var clusterBase = accessor.GetChunkAddress(cid);
                var occupancy = *(ulong*)clusterBase;
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    if (*(long*)(clusterBase + cs.Layout.EntityIdsOffset + slot * 8) == (long)id.RawValue)
                    {
                        return cid;
                    }
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return -1;
    }

    /// <summary>
    /// AC-2 and AC-4: the snapshot a tick publishes names exactly the clusters written in it — A, then B, then A — never one written two ticks before,
    /// although the buffers are retained and the one taken at the third tick is the first tick's.
    /// </summary>
    [Test]
    public void EachTickPublishes_ExactlyItsOwnDirtySet_FromAlternatingBuffers()
    {
        using var dbe = SetupEngine();
        var a = Spawn(dbe, 50f, 50f);
        var b = Spawn(dbe, 850f, 850f);
        dbe.WriteTickFence(1);
        var clusterA = ClusterOf(dbe, a);
        var clusterB = ClusterOf(dbe, b);
        Assert.That(clusterA, Is.Not.EqualTo(clusterB), "the premise: the two entities live in different clusters");

        var cs = ClusterStateOf(dbe);
        Touch(dbe, a, 55f, 50f);
        dbe.WriteTickFence(2);
        Assert.That(cs.FenceBranchPath, Is.EqualTo(2), "the premise: an in-place write takes branch 2");
        var first = cs.PreviousTickDirtySnapshot;
        var atTwo = DirtyClusters(first);

        Touch(dbe, b, 855f, 850f);
        dbe.WriteTickFence(3);
        var second = cs.PreviousTickDirtySnapshot;
        var atThree = DirtyClusters(second);
        var firstAfterThree = DirtyClusters(first);

        // What a Migrate phase leaves in the padded tail — a destination bit past the bitmap's words — planted in the free buffer the next tick takes.
        Assert.That(first.Length, Is.GreaterThan(cs.FenceDirtyWords), "the premise: the retained buffer is padded past the bitmap's words");
        first[^1] = -1L;

        Touch(dbe, a, 60f, 50f);
        dbe.WriteTickFence(4);
        var third = cs.PreviousTickDirtySnapshot;
        var atFour = DirtyClusters(third);

        Assert.Multiple(() =>
        {
            Assert.That(atTwo, Is.EqualTo(new[] { clusterA }));
            Assert.That(atThree, Is.EqualTo(new[] { clusterB }), "a cluster dirty two ticks before still read as dirty");
            Assert.That(firstAfterThree, Is.EqualTo(new[] { clusterA }), "the next tick's Prep wrote into the buffer still published");
            Assert.That(atFour, Is.EqualTo(new[] { clusterA }), "the reused buffer kept bits of the tick that last used it, or of its tail");
            Assert.That(second, Is.Not.SameAs(first), "two ticks in a row shared one buffer: the second overwrote what the first published");
            Assert.That(third, Is.SameAs(first), "the third tick allocated a buffer instead of taking the first tick's back");
        });
    }

    /// <summary>AC-1: in steady state the fence hands out two buffers, no more — every tick's snapshot is one of the pair.</summary>
    [Test]
    public void ASteadyStateOfDirtyTicks_UsesTwoBuffersAndNoMore()
    {
        using var dbe = SetupEngine();
        var a = Spawn(dbe, 50f, 50f);
        dbe.WriteTickFence(1);
        var cs = ClusterStateOf(dbe);
        var seen = new HashSet<long[]>(ReferenceEqualityComparer.Instance);
        for (var tick = 2; tick < 40; tick++)
        {
            Touch(dbe, a, 50f + (tick % 5), 50f);
            dbe.WriteTickFence(tick);
            if (tick > 4)
            {
                seen.Add(cs.FenceDirtyBits);
            }
        }

        Assert.That(seen, Has.Count.EqualTo(2), "branch 2 allocated its snapshot again instead of alternating its pair");
    }

    /// <summary>
    /// AC-3: a buffer the pre-size grew stays in the pair. Spawning past the segment's capacity raises the bound, so a resize runs; the grown array must be
    /// the pair's, and the ticks after it reuse it.
    /// </summary>
    [Test]
    public void ABufferGrownByThePreSize_StaysInThePair()
    {
        using var dbe = SetupEngine();
        var a = Spawn(dbe, 50f, 50f);
        dbe.WriteTickFence(1);
        var cs = ClusterStateOf(dbe);

        // Enough entities in enough cells that the cluster segment grows past what the first buffers were sized for.
        var tick = 2;
        for (var round = 0; round < 6; round++)
        {
            for (var i = 0; i < 400; i++)
            {
                Spawn(dbe, 5f + (i % 10) * 99f, 5f + (round * 160f) % 990f + (i / 10) * 0.01f);
            }

            Touch(dbe, a, 50f + round, 50f);
            dbe.WriteTickFence(tick++);
            var pair = cs.DirtySnapshotsForTests;
            Assert.That(ReferenceEquals(cs.FenceDirtyBits, pair.First) || ReferenceEquals(cs.FenceDirtyBits, pair.Second), Is.True,
                $"round {round}: the tick's buffer, grown or not, is not one of the pair — it left it, and the next tick will allocate");
        }

        var seen = new HashSet<long[]>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < 6; i++)
        {
            Touch(dbe, a, 60f + i, 50f);
            dbe.WriteTickFence(tick++);
            seen.Add(cs.FenceDirtyBits);
        }

        Assert.That(seen, Has.Count.EqualTo(2), "after the growth, the ticks reused the grown pair");
    }

    /// <summary>
    /// A tick whose dirty bits all mask out — the entity written was destroyed before the fence — publishes no snapshot: it used to leave the tick before's
    /// in place, and the next tick's change-filtered systems read those clusters as changed again.
    /// </summary>
    [Test]
    public void ATickWhoseDirtySetMasksOut_PublishesNoSnapshot()
    {
        using var dbe = SetupEngine();
        var a = Spawn(dbe, 50f, 50f);
        var b = Spawn(dbe, 850f, 850f);
        dbe.WriteTickFence(1);
        var cs = ClusterStateOf(dbe);
        Touch(dbe, a, 55f, 50f);
        dbe.WriteTickFence(2);
        Assert.That(cs.PreviousTickDirtySnapshot, Is.Not.Null, "the premise: a written tick publishes its snapshot");

        Touch(dbe, b, 855f, 850f);
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(b);
            tx.Commit();
        }

        dbe.WriteTickFence(3);
        Assert.Multiple(() =>
        {
            Assert.That(cs.FenceBranchPath, Is.EqualTo(2), "the premise: the dirty bit took branch 2");
            Assert.That(cs.PreviousTickDirtySnapshot, Is.Null, "a tick with nothing left dirty published the tick before's set");
        });
    }
}
