using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// SingleVersion sibling: makes the archetype cluster-eligible and gives the fixture a PERSISTED index home to compare against.
[Component("Typhon.Test.ECS.TIdx.State", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct TIdxState
{
    [Index(AllowMultiple = true)] public int Hp;
    public int Pad;

    public TIdxState(int hp)
    {
        Hp = hp;
        Pad = 0;
    }
}

// Transient component WITH an indexed field — the shape that disqualified its whole archetype from cluster storage until #655.
[Component("Typhon.Test.ECS.TIdx.Runtime", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct TIdxRuntime
{
    [Index(AllowMultiple = true)] public int Bucket;
    public int Tick;

    public TIdxRuntime(int bucket, int tick)
    {
        Bucket = bucket;
        Tick = tick;
    }
}

[Archetype]
class TIdxMixedArch : Archetype<TIdxMixedArch>
{
    public static readonly Comp<TIdxState> State = Register<TIdxState>();
    public static readonly Comp<TIdxRuntime> Runtime = Register<TIdxRuntime>();
}

/// <summary>
/// A Transient component with indexed fields no longer disqualifies its archetype from cluster storage — issue #655.
/// </summary>
/// <remarks>
/// <para>
/// The exclusion was archetype-wide: one indexed Transient field pushed the archetype's SingleVersion and Versioned components onto the legacy
/// per-ComponentTable index home too. Both documented reasons were wrong — the <c>BTree&lt;TransientStore&gt;</c> / <c>BTree&lt;PersistentStore&gt;</c> split
/// constrains tree INSTANCES rather than archetype placement, and the "cluster <c>Write&lt;T&gt;</c> returns a ref so there is no hook" claim was false, since
/// the Transient write branch already runs the shadow capture before returning.
/// </para>
/// <para>
/// Assertions read the B+Tree directly wherever the point is "the index is maintained": at these entity counts the planner takes the zone-map path and never
/// touches the tree, so a passing query would prove only that the DATA is right. Query-level assertions are kept separately, for the user-visible half.
/// </para>
/// </remarks>
[TestFixture]
class ClusterTransientIndexTests : TestBase<ClusterTransientIndexTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TIdxState>();
        dbe.RegisterComponentFromAccessor<TIdxRuntime>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ArchetypeClusterState ClusterState(DatabaseEngine dbe)
        => dbe._archetypeStates[Archetype<TIdxMixedArch>.Metadata.ArchetypeId].ClusterState;

    private static EntityId Spawn(Transaction tx, int hp, int bucket, int tick = 0)
        => tx.Spawn<TIdxMixedArch>(TIdxMixedArch.State.Set(new TIdxState(hp)), TIdxMixedArch.Runtime.Set(new TIdxRuntime(bucket, tick)));

    /// <summary>Every entity the archetype's TRANSIENT index reports for <paramref name="bucket"/>, read straight from the tree.</summary>
    private static unsafe HashSet<EntityId> TransientIndexed(DatabaseEngine dbe, int bucket)
    {
        var clusterState = ClusterState(dbe);
        Assert.That(clusterState.TransientIndexSlots, Is.Not.Null.And.Not.Empty, "fixture must own a Transient index home");

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        ref var field = ref clusterState.TransientIndexSlots[0].Fields[0];
        var idxAccessor = field.Index.Segment.CreateChunkAccessor();

        // The entity-id array lives in the PRIMARY chunk — the cluster segment for this mixed archetype, not the Transient one.
        var primary = clusterState.ClusterSegment.CreateChunkAccessor();
        var result = new HashSet<EntityId>();
        try
        {
            var tree = (BTree<int, TransientStore>)field.Index;
            var enumerator = tree.EnumerateRangeMultiple(bucket, bucket);
            try
            {
                while (enumerator.MoveNextKey())
                {
                    do
                    {
                        var values = enumerator.CurrentValues;
                        for (var j = 0; j < values.Length; j++)
                        {
                            var loc = values[j];
                            var basePtr = primary.GetChunkAddress(loc >> 6);
                            result.Add(EntityId.FromRaw(*(long*)(basePtr + clusterState.Layout.EntityIdsOffset + (loc & 0x3F) * 8)));
                        }
                    }
                    while (enumerator.NextChunk());
                }
            }
            finally
            {
                enumerator.Dispose();
            }
        }
        finally
        {
            primary.Dispose();
            idxAccessor.Dispose();
        }

        return result;
    }

    /// <summary>AC: the archetype is cluster-backed and owns BOTH index homes. Without this the rest proves nothing.</summary>
    [Test]
    public void Fixture_MixedArchetypeIsClusterBackedWithBothIndexHomes()
    {
        using var dbe = SetupEngine();

        var meta = Archetype<TIdxMixedArch>.Metadata;
        var clusterState = ClusterState(dbe);
        Assert.Multiple(() =>
        {
            Assert.That(meta.IsClusterEligible, Is.True, "an indexed Transient field must no longer disqualify the archetype");
            Assert.That(meta.HasClusterIndexes, Is.True);
            Assert.That(clusterState.IndexSlots, Is.Not.Null.And.Not.Empty, "the SingleVersion component keeps a per-archetype tree");
            Assert.That(clusterState.TransientIndexSlots, Is.Not.Null.And.Not.Empty, "the Transient component gets its own");
            Assert.That(clusterState.TransientIndexSegment, Is.Not.Null, "the Transient trees need a heap-backed segment");
        });
    }

    /// <summary>AC: spawn populates the Transient index.</summary>
    [Test]
    public void Spawn_PopulatesTheTransientIndex()
    {
        using var dbe = SetupEngine();

        var inBucket7 = new HashSet<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 12; i++)
            {
                var id = Spawn(tx, hp: 100 + i, bucket: i % 3 == 0 ? 7 : 9);
                if (i % 3 == 0)
                {
                    inBucket7.Add(id);
                }
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        Assert.That(TransientIndexed(dbe, 7), Is.EquivalentTo(inBucket7), "every entity spawned into bucket 7 must be in the Transient tree");
        Assert.That(TransientIndexed(dbe, 5), Is.Empty, "an unused key must resolve to nothing");
    }

    /// <summary>AC: an in-place write to a Transient indexed field moves its key at the tick fence — capture + drain, both homes.</summary>
    [Test]
    public void Mutate_TransientIndexedField_MovesTheKeyAtTheFence()
    {
        using var dbe = SetupEngine();

        EntityId moved;
        using (var tx = dbe.CreateQuickTransaction())
        {
            moved = Spawn(tx, hp: 50, bucket: 1);
            Spawn(tx, hp: 60, bucket: 1);
            tx.Commit();
        }
        dbe.WriteTickFence(1);
        Assert.That(TransientIndexed(dbe, 1).Count, Is.EqualTo(2), "premise: both start in bucket 1");

        using (var tx = dbe.CreateQuickTransaction())
        {
            ref var rt = ref tx.OpenMut(moved).Write(TIdxMixedArch.Runtime);
            rt.Bucket = 42;
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        Assert.Multiple(() =>
        {
            Assert.That(TransientIndexed(dbe, 42), Is.EquivalentTo(new[] { moved }), "the mutated entity must appear under its new key");
            Assert.That(TransientIndexed(dbe, 1).Count, Is.EqualTo(1), "and must have left the old one, without taking its sibling");
        });
    }

    /// <summary>AC: AllowMultiple — destroying one entity at a shared key must not take its siblings with it.</summary>
    [Test]
    public void Destroy_AllowMultipleTransientKey_PreservesSiblings()
    {
        using var dbe = SetupEngine();

        var ids = new EntityId[4];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < ids.Length; i++)
            {
                ids[i] = Spawn(tx, hp: i, bucket: 3);
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);
        Assert.That(TransientIndexed(dbe, 3).Count, Is.EqualTo(4), "premise: all four share key 3");

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(ids[1]);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        var remaining = TransientIndexed(dbe, 3);
        Assert.Multiple(() =>
        {
            Assert.That(remaining, Does.Not.Contain(ids[1]), "the destroyed entity must be gone from the tree");
            Assert.That(remaining.Count, Is.EqualTo(3), "a plain Remove(key) would have wiped the whole buffer and taken all three siblings");
        });
    }

    /// <summary>AC: the SingleVersion sibling's index still lives on the archetype and still works — the exclusion used to push it off too.</summary>
    [Test]
    public void SvSibling_KeepsItsPerArchetypeIndex()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 6; i++)
            {
                Spawn(tx, hp: 500 + i, bucket: 0);
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using var txQ = dbe.CreateQuickTransaction();
        var result = txQ.Query<TIdxMixedArch>().WhereField<TIdxState>(s => s.Hp == 503).Execute();
        Assert.That(result.Count, Is.EqualTo(1), "the SV component's per-archetype index must still answer queries");
    }

    /// <summary>AC: a query over the Transient indexed field returns the right entities — the user-visible half.</summary>
    [Test]
    public void Query_OnTransientIndexedField_ReturnsTheRightEntities()
    {
        using var dbe = SetupEngine();

        var inBucket2 = new HashSet<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 10; i++)
            {
                var id = Spawn(tx, hp: i, bucket: i < 4 ? 2 : 8);
                if (i < 4)
                {
                    inBucket2.Add(id);
                }
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using var txQ = dbe.CreateQuickTransaction();
        var result = txQ.Query<TIdxMixedArch>().WhereField<TIdxRuntime>(r => r.Bucket == 2).Execute();

        // The regression this guards: a cluster-backed archetype whose where-component has no entry in IndexSlots used to make
        // ScanPerArchetypeBTree return with NO results rather than fall back to a scan — a silently empty query (the #663 shape).
        Assert.That(result, Is.EquivalentTo(inBucket2),
            "querying a Transient indexed field on a cluster archetype must not silently return nothing");
    }

    /// <summary>
    /// AC: reopen — Transient trees are absent, never reloaded stale. Transient data does not survive the process, so its index must not either.
    /// </summary>
    [Test]
    public void Reopen_TransientIndexIsEmptyNotStale()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<TIdxState>();
            dbe.RegisterComponentFromAccessor<TIdxRuntime>();
            dbe.InitializeArchetypes();

            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < 8; i++)
                {
                    Spawn(tx, hp: i, bucket: 11);
                }
                tx.Commit();
            }
            dbe.WriteTickFence(1);
            Assert.That(TransientIndexed(dbe, 11).Count, Is.EqualTo(8), "premise: the index is populated before the close");
            dbe.Dispose();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
        reopened.RegisterComponentFromAccessor<TIdxState>();
        reopened.RegisterComponentFromAccessor<TIdxRuntime>();
        reopened.InitializeArchetypes();

        Assert.That(TransientIndexed(reopened, 11), Is.Empty,
            "a Transient tree must never be reloaded: its segment is heap-backed and given no SPI, so the correct post-reopen state is empty");
    }
}
