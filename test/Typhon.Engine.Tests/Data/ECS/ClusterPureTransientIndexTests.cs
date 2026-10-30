using System.Linq;
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

// Two indexed fields: a predicate on one, the order on the other.
[Component("Typhon.Test.ECS.PTIdx.Ranked", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct PtIdxRanked
{
    [Index(AllowMultiple = true)] public int Group;
    [Index(AllowMultiple = true)] public int Rank;
}

[Archetype]
class PtIdxRankedArch : Archetype<PtIdxRankedArch>
{
    public static readonly Comp<PtIdxRanked> Ranked = Register<PtIdxRanked>();
}

[Archetype]
class PtIdxHiddenArch : Archetype<PtIdxHiddenArch>
{
    public static readonly Comp<CompA> A = Register<CompA>();
    public static readonly Comp<PtIdxRuntime> Runtime = Register<PtIdxRuntime>();
}

// A second Transient component, without an index: a tick that writes only it leaves the archetype's written-slot union naming another slot than the
// indexed one.
[Component("Typhon.Test.ECS.PTIdx.Note", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct PtIdxNote
{
    public int Value;
    public int Pad;
}

[Archetype]
class PtIdxNotedArch : Archetype<PtIdxNotedArch>
{
    public static readonly Comp<PtIdxRuntime> Runtime = Register<PtIdxRuntime>();
    public static readonly Comp<PtIdxNote> Note = Register<PtIdxNote>();
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
        dbe.RegisterComponentFromAccessor<PtIdxNote>();
        dbe.RegisterComponentFromAccessor<PtIdxRanked>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.RegisterComponentFromAccessor<TIdxState>();
        dbe.RegisterComponentFromAccessor<TIdxRuntime>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>Out of order, so a sort by entity key cannot pass.</summary>
    private static readonly int[] OutOfOrder = [40, 15, 10, 30, 5, 20, 50];

    /// <summary>The issue's query: a pure-Transient archetype, its only index the ordered one (#1186).</summary>
    [Test]
    [VerifiesRule("QORD-01")]
    public void AnOrderedQuery_OnATransientField_ReturnsItsRowsInThatOrder([Values] bool descending)
    {
        using var dbe = SetupEngine();
        var ids = new Dictionary<int, EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var bucket in OutOfOrder)
            {
                ids[bucket] = Spawn(tx, bucket);
            }

            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(1);

        using var read = dbe.CreateQuickTransaction();
        var query = read.Query<PtIdxArch>().WhereField<PtIdxRuntime>(r => r.Bucket >= 10);
        var ordered = descending ? query.OrderByFieldDescending<PtIdxRuntime, int>(r => r.Bucket) : query.OrderByField<PtIdxRuntime, int>(r => r.Bucket);
        var expected = OutOfOrder.Where(b => b >= 10).OrderBy(b => descending ? -b : b).Select(b => ids[b]).ToArray();
        Assert.That(ordered.ExecuteOrdered(), Is.EqualTo(expected));
        Assert.That(ordered.Skip(1).Take(2).ExecuteOrdered(), Is.EqualTo(expected[1..3]));
    }

    /// <summary>A transaction's own spawns come back too, in their place: they were collected after the skip.</summary>
    [Test]
    [VerifiesRule("QORD-01")]
    public void AnOrderedQuery_OnATransientField_ReturnsTheTransactionsOwnSpawns()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        var ids = new Dictionary<int, EntityId>();
        foreach (var bucket in OutOfOrder)
        {
            ids[bucket] = Spawn(tx, bucket);
        }

        var expected = OutOfOrder.Order().Select(b => ids[b]).ToArray();
        Assert.That(tx.Query<PtIdxArch>().WhereField<PtIdxRuntime>(r => r.Bucket >= 0).OrderByField<PtIdxRuntime, int>(r => r.Bucket).ExecuteOrdered(),
            Is.EqualTo(expected));
    }

    /// <summary>A predicate on another field of the component went to the sort fallback, which had no source and sorted by entity key: the right rows, silently
    /// in the wrong order.</summary>
    [Test]
    [VerifiesRule("QORD-01")]
    public void AnOrderedQuery_PredicateOnAnotherTransientField_SortsByTheOrderedField()
    {
        using var dbe = SetupEngine();
        var ids = new Dictionary<int, EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var rank in OutOfOrder)
            {
                ids[rank] = tx.Spawn<PtIdxRankedArch>(PtIdxRankedArch.Ranked.Set(new PtIdxRanked { Group = 1, Rank = rank }));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(1);

        using var read = dbe.CreateQuickTransaction();
        Assert.That(read.Query<PtIdxRankedArch>().WhereField<PtIdxRanked>(r => r.Group == 1).OrderByField<PtIdxRanked, int>(r => r.Rank).ExecuteOrdered(),
            Is.EqualTo(OutOfOrder.Order().Select(r => ids[r]).ToArray()));
    }

    /// <summary>
    /// A row a destroy hid from HEAD and a snapshot still sees: no point read answers, so its key comes out of the slot's bytes on the Transient segment. The
    /// ordered field is Transient, the archetype Versioned, or nothing would hide (VIS-09).
    /// </summary>
    [Test]
    [VerifiesRule("QORD-01")]
    public void AnOrderedQuery_OnATransientField_KeysAHiddenRowFromItsSlot()
    {
        using var dbe = SetupEngine();
        var ids = new Dictionary<int, EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var bucket in OutOfOrder)
            {
                ids[bucket] = tx.Spawn<PtIdxHiddenArch>(PtIdxHiddenArch.A.Set(new CompA(bucket, 0, 0)),
                    PtIdxHiddenArch.Runtime.Set(new PtIdxRuntime(bucket, 0)));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(1);

        using var snapshot = dbe.CreateQuickTransaction();
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(ids[20]);
            Assert.That(tx.Commit(), Is.True);
        }

        Assert.That(
            snapshot.Query<PtIdxHiddenArch>().WhereField<PtIdxRuntime>(r => r.Bucket >= 0).OrderByField<PtIdxRuntime, int>(r => r.Bucket).ExecuteOrdered(),
            Is.EqualTo(OutOfOrder.Order().Select(b => ids[b]).ToArray()));
    }

    /// <summary>A mixed archetype, ordered by its Transient component: the title's "pure" was too narrow.</summary>
    [Test]
    [VerifiesRule("QORD-01")]
    public void AnOrderedQuery_OnAMixedArchetypesTransientField_ReturnsItsRowsInThatOrder()
    {
        using var dbe = SetupEngine();
        var ids = new Dictionary<int, EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var bucket in OutOfOrder)
            {
                ids[bucket] = tx.Spawn<TIdxMixedArch>(TIdxMixedArch.State.Set(new TIdxState(1)), TIdxMixedArch.Runtime.Set(new TIdxRuntime(bucket, 0)));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(1);

        using var read = dbe.CreateQuickTransaction();
        Assert.That(read.Query<TIdxMixedArch>().WhereField<TIdxRuntime>(r => r.Bucket >= 0).OrderByField<TIdxRuntime, int>(r => r.Bucket).ExecuteOrdered(),
            Is.EqualTo(OutOfOrder.Order().Select(b => ids[b]).ToArray()));
    }

    private static ArchetypeClusterState ClusterState(DatabaseEngine dbe)
        => dbe._archetypeStates[Archetype<PtIdxArch>.Metadata.ArchetypeId].ClusterState;

    private static EntityId Spawn(Transaction tx, int bucket, int tick = 0)
        => tx.Spawn<PtIdxArch>(PtIdxArch.Runtime.Set(new PtIdxRuntime(bucket, tick)));

    /// <summary>
    /// Every entity the archetype's Transient index reports for <paramref name="bucket"/>, read straight from the tree. The entity-id tail lives in the
    /// TRANSIENT chunk here — there is no other segment to read it from.
    /// </summary>
    private static HashSet<EntityId> Indexed(DatabaseEngine dbe, int bucket) => Indexed(dbe, ClusterState(dbe), bucket);

    private static unsafe HashSet<EntityId> Indexed(DatabaseEngine dbe, ArchetypeClusterState clusterState, int bucket)
    {
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
            tx.OpenMut(ids[2]).Set(PtIdxArch.Runtime, new PtIdxRuntime(999, 1));
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
            tx.OpenMut(moved).Set(PtIdxArch.Runtime, new PtIdxRuntime(42, 2));
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

    /// <summary>
    /// #1187: the same move through <see cref="ClusterRef{TArch}.Set{T}"/>. A pure-Transient archetype has one base, the Transient segment's, which the
    /// capture must read the old key from — an earlier capture passed a null second base through and recorded nothing.
    /// </summary>
    [Test]
    [VerifiesRule("IX-07")]
    public void ClusterSet_MovesTheKeyInTheTreeAtTheFence()
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

        using (var tx = dbe.CreateQuickTransaction())
        {
            {
                var accessor = tx.For<PtIdxArch>();
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    var ids = cluster.EntityIds;
                    for (var slot = 0; slot < cluster.ClusterSize; slot++)
                    {
                        if ((cluster.OccupancyBits & (1UL << slot)) != 0 && ids[slot] == (long)moved.RawValue)
                        {
                            cluster.Set(PtIdxArch.Runtime, slot, new PtIdxRuntime(42, 2));
                        }
                    }
                }
            }

            tx.Commit();
        }
        dbe.WriteTickFence(2);

        Assert.Multiple(() =>
        {
            Assert.That(Indexed(dbe, 42), Is.EquivalentTo(new[] { moved }), "the written entity under its new key");
            Assert.That(Indexed(dbe, 1), Is.EquivalentTo(new[] { sibling }), "and off the old one, without its sibling");
        });
    }

    /// <summary>
    /// The pure-Transient fence drained the shadow buffers BEFORE it took this tick's written-slot union, so the drain's skip of unwritten components read
    /// the previous tick's union: a tick that wrote only an unindexed component, then a tick that moved the indexed key, and the key's records were
    /// thrown away as unwritten — the tree kept the old key for good.
    /// </summary>
    [Test]
    [VerifiesRule("IX-07")]
    public void AKeyMove_AfterATickThatWroteOnlyAnotherComponent_MovesTheKey()
    {
        using var dbe = SetupEngine();
        var clusterState = dbe._archetypeStates[Archetype<PtIdxNotedArch>.Metadata.ArchetypeId].ClusterState;
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<PtIdxNotedArch>(PtIdxNotedArch.Runtime.Set(new PtIdxRuntime(1, 0)), PtIdxNotedArch.Note.Set(new PtIdxNote()));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Set(PtIdxNotedArch.Note, new PtIdxNote { Value = 5 });   // only the unindexed component
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Set(PtIdxNotedArch.Runtime, new PtIdxRuntime(42, 1));
            tx.Commit();
        }
        dbe.WriteTickFence(3);

        Assert.Multiple(() =>
        {
            Assert.That(Indexed(dbe, clusterState, 42), Is.EquivalentTo(new[] { id }), "the new key");
            Assert.That(Indexed(dbe, clusterState, 1), Is.Empty, "the old key");
        });
    }
}
