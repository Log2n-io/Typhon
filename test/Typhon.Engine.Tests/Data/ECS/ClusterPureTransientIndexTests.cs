using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// A PURE-Transient archetype: no SingleVersion or Versioned sibling, so it gets no PersistentStore cluster segment at all. Its Transient segment is primary
// AND data at once, which is the shape #655 step 4 had to make the query path handle.
[Component("Typhon.Test.ECS.PTIdx.Runtime", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct PtIdxRuntime
{
    [Index(AllowMultiple = true)] public int Bucket;
    public int Tick;

    public PtIdxRuntime(int bucket, int tick)
    {
        Bucket = bucket;
        Tick = tick;
    }
}

[Archetype]
class PtIdxArch : Archetype<PtIdxArch>
{
    public static readonly Comp<PtIdxRuntime> Runtime = Register<PtIdxRuntime>();
}

/// <summary>
/// A pure-Transient archetype with an indexed field is cluster-backed and queryable — the last half of issue #655.
/// </summary>
/// <remarks>
/// <para>
/// The mixed case (#655 step 3) had a <c>ClusterSegment</c> to read the occupancy word and entity-id tail from. A pure-Transient archetype has none: its
/// Transient segment is both <i>primary</i> and <i>data</i>. Path B (<c>EcsQuery.ScanClusterSoa</c>) had those two roles fused into one accessor typed to
/// <c>PersistentStore</c>, so it could not run here at all, and the non-cluster fallback reads <c>ComponentTable.TransientComponentSegment</c>, which holds
/// nothing for a cluster-backed archetype. The combination returned silently empty — which is why the exclusion outlived the mixed fix by one step.
/// </para>
/// <para>
/// <c>TransientIndexTests</c> already covers this archetype shape end-to-end and now exercises the cluster path throughout; it does not assert the PLACEMENT,
/// which is what this fixture pins.
/// </para>
/// </remarks>
[TestFixture]
class ClusterPureTransientIndexTests : TestBase<ClusterPureTransientIndexTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<PtIdxRuntime>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ArchetypeClusterState ClusterState(DatabaseEngine dbe)
        => dbe._archetypeStates[Archetype<PtIdxArch>.Metadata.ArchetypeId].ClusterState;

    private static EntityId Spawn(Transaction tx, int bucket, int tick = 0)
        => tx.Spawn<PtIdxArch>(PtIdxArch.Runtime.Set(new PtIdxRuntime(bucket, tick)));

    /// <summary>
    /// Every entity the archetype's Transient index reports for <paramref name="bucket"/>, read straight from the tree. The entity-id tail lives in the
    /// TRANSIENT chunk here — there is no other segment to read it from.
    /// </summary>
    private static unsafe HashSet<EntityId> Indexed(DatabaseEngine dbe, int bucket)
    {
        var clusterState = ClusterState(dbe);
        Assert.That(clusterState.TransientIndexSlots, Is.Not.Null.And.Not.Empty, "fixture must own a Transient index home");

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        ref var field = ref clusterState.TransientIndexSlots[0].Fields[0];
        var primary = clusterState.TransientSegment.CreateChunkAccessor();
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
        }

        return result;
    }

    /// <summary>AC: the archetype is cluster-backed with a Transient index home and NO persistent one. Without this the rest proves nothing.</summary>
    [Test]
    public void Fixture_PureTransientArchetypeIsClusterBacked()
    {
        using var dbe = SetupEngine();
        var meta = Archetype<PtIdxArch>.Metadata;
        var clusterState = ClusterState(dbe);

        Assert.Multiple(() =>
        {
            Assert.That(meta.IsClusterEligible, Is.True, "an indexed Transient field must no longer disqualify a pure-Transient archetype either");
            Assert.That(meta.HasClusterIndexes, Is.True);
            Assert.That(clusterState.ClusterSegment, Is.Null, "premise: no PersistentStore segment — the Transient one is primary AND data");
            Assert.That(clusterState.TransientSegment, Is.Not.Null);
            Assert.That(clusterState.TransientIndexSlots, Is.Not.Null.And.Not.Empty, "the Transient component owns a per-archetype tree");
            Assert.That(clusterState.IndexSlots, Is.Empty, "and there is no persistent home to put anything in");
        });
    }

    /// <summary>AC: spawn populates the per-archetype Transient index, keyed off a cluster location the Transient segment resolves.</summary>
    [Test]
    public void Spawn_PopulatesThePerArchetypeIndex()
    {
        using var dbe = SetupEngine();

        var inBucket7 = new HashSet<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 6; i++)
            {
                var id = Spawn(tx, bucket: i % 2 == 0 ? 7 : 9, tick: i);
                if (i % 2 == 0)
                {
                    inBucket7.Add(id);
                }
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        Assert.That(Indexed(dbe, 7), Is.EquivalentTo(inBucket7), "every entity spawned into bucket 7 must be in the archetype's own tree");
        Assert.That(Indexed(dbe, 5), Is.Empty, "an unused key must resolve to nothing");
    }

    /// <summary>
    /// AC: a query still finds an entity whose indexed field was mutated OUTSIDE the range spawn established — the zone map is refreshed at the fence.
    /// </summary>
    /// <remarks>
    /// The regression this guards is a silently empty query, not a stale count. Path B prunes a cluster whose zone map cannot contain the queried value, and
    /// spawn only ever WIDENS the map; the fence recompute is what lets it track a value moving out of range. That recompute walked the persistent home alone,
    /// so a pure-Transient archetype never got one and every post-mutation query on the new key returned nothing (#655).
    /// </remarks>
    [Test]
    public void Query_AfterMutatingOutOfTheSpawnRange_StillFindsTheEntity()
    {
        using var dbe = SetupEngine();

        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 4; i++)
            {
                ids.Add(Spawn(tx, bucket: 10 + i));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using (var txPre = dbe.CreateQuickTransaction())
        {
            Assert.That(txPre.Query<PtIdxArch>().WhereField<PtIdxRuntime>(r => r.Bucket == 999).Count(), Is.EqualTo(0),
                "premise: nothing is in bucket 999 before the mutation");
        }

        // 999 is far outside [10, 13] — the spawn-time zone map prunes the whole cluster unless the fence recomputed it.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(ids[2]).Write(PtIdxArch.Runtime) = new PtIdxRuntime(999, 1);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        using var txQ = dbe.CreateQuickTransaction();
        Assert.Multiple(() =>
        {
            Assert.That(txQ.Query<PtIdxArch>().WhereField<PtIdxRuntime>(r => r.Bucket == 999).Execute(), Is.EquivalentTo(new[] { ids[2] }),
                "the mutated entity must be findable under its new key");
            Assert.That(txQ.Query<PtIdxArch>().WhereField<PtIdxRuntime>(r => r.Bucket == 12).Count(), Is.EqualTo(0), "and must not answer to the old one");
            Assert.That(txQ.Query<PtIdxArch>().WhereField<PtIdxRuntime>(r => r.Bucket == 11).Count(), Is.EqualTo(1), "its siblings are untouched");
        });
    }

    /// <summary>AC: the fence drains the shadow buffer, so the TREE follows the mutation too — not just the SoA data Path B scans.</summary>
    /// <remarks>
    /// Separate from the query assertion above on purpose: Path B never reads the tree, so a passing query proves only that the DATA and the zone map are
    /// right. The capture that feeds this drain reads the entity's old key from a segment base, and a pure-Transient archetype has no separate Transient base
    /// — <c>EntityRef._transientClusterBase</c> is null and <c>_clusterBase</c> IS the Transient one. Passing the null through captured nothing at all, which
    /// left the tree on the pre-mutation key for the entity's whole lifetime.
    /// </remarks>
    [Test]
    public void Mutate_MovesTheKeyInTheTreeAtTheFence()
    {
        using var dbe = SetupEngine();

        EntityId moved, sibling;
        using (var tx = dbe.CreateQuickTransaction())
        {
            moved = Spawn(tx, bucket: 1, tick: 0);
            sibling = Spawn(tx, bucket: 1, tick: 1);
            tx.Commit();
        }
        dbe.WriteTickFence(1);
        Assert.That(Indexed(dbe, 1), Is.EquivalentTo(new[] { moved, sibling }), "premise: both start in bucket 1");

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(moved).Write(PtIdxArch.Runtime) = new PtIdxRuntime(42, 2);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        Assert.Multiple(() =>
        {
            Assert.That(Indexed(dbe, 42), Is.EquivalentTo(new[] { moved }), "the mutated entity must appear under its new key");
            Assert.That(Indexed(dbe, 1), Is.EquivalentTo(new[] { sibling }), "and must have left the old one, without taking its sibling");
        });
    }

    /// <summary>
    /// AC: destroying one of several entities sharing an AllowMultiple key removes only that entity. The element-id tail this needs lives in the PRIMARY
    /// chunk, which here is the Transient segment — the same chunk as the data, unlike a mixed archetype where the two split.
    /// </summary>
    [Test]
    public void Destroy_AllowMultipleKey_PreservesSiblings()
    {
        using var dbe = SetupEngine();

        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 4; i++)
            {
                ids.Add(Spawn(tx, bucket: 3, tick: i));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);
        Assert.That(Indexed(dbe, 3), Has.Count.EqualTo(4), "premise: all four share key 3");

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(ids[1]);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        var remaining = Indexed(dbe, 3);
        Assert.Multiple(() =>
        {
            Assert.That(remaining, Does.Not.Contain(ids[1]), "the destroyed entity must be gone from the tree");
            Assert.That(remaining, Has.Count.EqualTo(3), "a plain Remove(key) would have wiped the whole buffer and taken all three siblings");
        });
    }
}
