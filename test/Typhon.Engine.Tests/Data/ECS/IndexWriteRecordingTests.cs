using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// Nine indexed fields: a full cluster's keys (64 × 9) exceed the batched capture's stack budget, so the batched Set records one entity at a time.
[Component("Typhon.Test.ECS.Iwr.Wide", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct IwrWide
{
    [Index(AllowMultiple = true)] public int F0;
    [Index(AllowMultiple = true)] public int F1;
    [Index(AllowMultiple = true)] public int F2;
    [Index(AllowMultiple = true)] public int F3;
    [Index(AllowMultiple = true)] public int F4;
    [Index(AllowMultiple = true)] public int F5;
    [Index(AllowMultiple = true)] public int F6;
    [Index(AllowMultiple = true)] public int F7;
    [Index] public int Key;
}

[Archetype]
class IwrWideUnit : Archetype<IwrWideUnit>
{
    public static readonly Comp<IwrWide> Wide = Register<IwrWide>();
}

/// <summary>
/// What a write records for the tick fence's index maintenance, and what the fence does with it (#1346): a write records an entity's old keys only when it
/// writes a component with a fence-maintained index, and a move that finds no element never leaves -1 in the entity's element-id tail.
/// </summary>
/// <remarks>Reuses <see cref="QfUnit"/>: <see cref="QfData"/> carries a unique <c>Key</c> and a multi-value <c>Group</c>, <see cref="QfOther"/> no
/// index.</remarks>
[TestFixture]
class IndexWriteRecordingTests : TestBase<IndexWriteRecordingTests>
{
    private const int Clusters = 4;

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<QfData>();
        dbe.RegisterComponentFromAccessor<QfOther>();
        dbe.RegisterComponentFromAccessor<QfVData>();
        dbe.RegisterComponentFromAccessor<IwrWide>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ArchetypeClusterState ClusterState(DatabaseEngine dbe) => dbe._archetypeStates[Archetype<QfUnit>.Metadata.ArchetypeId].ClusterState;

    /// <summary>Full clusters of entities with <c>Key = 2 * i</c> and <c>Group = i % 8</c>, fenced.</summary>
    private static EntityId[] Spawn(DatabaseEngine dbe)
    {
        var rows = Clusters * ClusterState(dbe).Layout.ClusterSize;
        var ids = new EntityId[rows];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < rows; i++)
            {
                var d = new QfData(i % 8, i * 2);
                var o = new QfOther();
                ids[i] = tx.Spawn<QfUnit>(QfUnit.Data.Set(in d), QfUnit.Other.Set(in o));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(0);
        return ids;
    }

    private static void Write<T>(DatabaseEngine dbe, EntityId id, Comp<T> comp, Func<T, T> change) where T : unmanaged
    {
        using var tx = dbe.CreateQuickTransaction();
        var e = tx.OpenMut(id);
        e.Set(comp, change(e.Read(comp)));
        tx.Commit();
    }

    private static EntityId[] GroupThroughTheIndex(DatabaseEngine dbe, int group)
    {
        QueryPathProbe.Forced = ClusterScanPath.Selective;
        try
        {
            using var tx = dbe.CreateQuickTransaction();
            return [.. tx.Query<QfUnit>().WhereField<QfData>(d => d.Group == group).Execute()];
        }
        finally
        {
            QueryPathProbe.Reset();
        }
    }

    /// <summary>
    /// #1344: a point write to a component without an index records nothing, though the archetype has an indexed one. It recorded every indexed key of the
    /// entity and gave the fence entries to drain.
    /// </summary>
    [VerifiesRule("IX-07")]
    [Test]
    public void APointWrite_ToAComponentWithoutAnIndex_RecordsNothing()
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe);
        var cs = ClusterState(dbe);

        Write(dbe, ids[10], QfUnit.Other, o => o with { Hp = 5 });

        Assert.That(cs.ClusterShadowBitmap.AnyTestAndSetSinceClear, Is.False, "no key recorded for a write to QfOther");
        Assert.That(cs.ClusterDirtyBitmap.HasDirty, Is.True, "the write is still marked for the fence");
    }

    /// <summary>
    /// #1344's premise: an unindexed write first, the key-changing write after it in the same tick, and the key still moves — the second write records the
    /// keys the tree holds.
    /// </summary>
    [Test]
    public void AnUnindexedWrite_ThenAKeyChange_InOneTick_MovesTheKey()
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe);

        Write(dbe, ids[10], QfUnit.Other, o => o with { Hp = 5 });
        Write(dbe, ids[10], QfUnit.Data, d => d with { Key = 100_001, Group = 7 });
        dbe.WriteTickFence(1);

        IndexDataOracle.AssertIndexAgreesWithData<QfUnit>(dbe, "after the fence");
    }

    /// <summary>
    /// #1345: a move that finds no element (the entity's element-id tail names a chunk that does not hold it) indexes the entity under its new key and
    /// stores a real element id. It stored the -1 MoveValue returned, which every later move, removal or location update read as a chunk id.
    /// </summary>
    [Test]
    public unsafe void AMoveThatFindsNoElement_StoresNoMinusOne_AndIndexesTheNewKey()
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe);
        var cs = ClusterState(dbe);
        ref var group = ref FieldAt(cs, 0);
        Assert.That(group.AllowMultiple, Is.True, "premise: Group is the multi-value field, at offset 0");

        // The staging: entity 10's tail takes entity 11's element id — another key's chunk (Group 3, not 2), which does not hold entity 10.
        var (chunk10, slot10) = LocationOf(dbe, ids[10]);
        var (chunk11, slot11) = LocationOf(dbe, ids[11]);
        Assert.That(chunk11, Is.EqualTo(chunk10), "premise: neighbours in one cluster");
        var tailOffset10 = cs.Layout.IndexElementIdOffset(group.MultiFieldIndex, slot10);
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            var accessor = cs.ClusterSegment.CreateChunkAccessor();
            try
            {
                var chunk = accessor.GetChunkAddress(chunk10, true);
                *(int*)(chunk + tailOffset10) = *(int*)(chunk + cs.Layout.IndexElementIdOffset(group.MultiFieldIndex, slot11));
            }
            finally
            {
                accessor.Dispose();
            }
        }

        var missesBefore = BTreeBase<PersistentStore>.MoveValueMisses;
        Write(dbe, ids[10], QfUnit.Data, d => d with { Group = 7 });
        dbe.WriteTickFence(1);

        Assert.That(BTreeBase<PersistentStore>.MoveValueMisses, Is.GreaterThan(missesBefore), "premise: the move found no element");
        Assert.That(GroupThroughTheIndex(dbe, 7), Does.Contain(ids[10]), "the entity is indexed under its new key");
        int tail10;
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            var accessor = cs.ClusterSegment.CreateChunkAccessor();
            try
            {
                tail10 = *(int*)(accessor.GetChunkAddress(chunk10) + tailOffset10);
            }
            finally
            {
                accessor.Dispose();
            }
        }

        Assert.That(tail10, Is.GreaterThanOrEqualTo(0), "the tail holds a real element id");
    }

    /// <summary>
    /// #1346: a write whose value keeps every indexed key of the component records nothing, through either API — the tree's keys are still the entity's.
    /// </summary>
    [TestCase(true, TestName = "ThroughClusterRefSet")]
    [VerifiesRule("IX-07")]
    [TestCase(false, TestName = "ThroughOpenMutSet")]
    public void AWriteKeepingEveryKey_RecordsNothing(bool throughTheCluster)
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe);
        var cs = ClusterState(dbe);

        if (throughTheCluster)
        {
            OnItsCluster(dbe, ids[10], (cluster, slot) => cluster.Set(QfUnit.Data, slot, cluster.GetReadOnly(QfUnit.Data, slot)));
        }
        else
        {
            Write(dbe, ids[10], QfUnit.Data, d => d);
        }

        Assert.That(cs.ClusterShadowBitmap.AnyTestAndSetSinceClear, Is.False, "no key recorded for a write that keeps them all");
        Assert.That(cs.ClusterDirtyBitmap.HasDirty, Is.True, "the write is still marked for the fence");
        dbe.WriteTickFence(1);
        IndexDataOracle.AssertIndexAgreesWithData<QfUnit>(dbe, "after the fence");
    }

    /// <summary>
    /// #1346: a write keeping the keys, then one moving them, in one tick: the second records the keys the tree holds — the first left them in place — and
    /// the fence moves them.
    /// </summary>
    [TestCase(true, TestName = "ThroughClusterRefSet")]
    [VerifiesRule("IX-07")]
    [TestCase(false, TestName = "ThroughOpenMutSet")]
    public void AWriteKeepingTheKeys_ThenOneMovingThem_MovesThem(bool throughTheCluster)
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe);

        if (throughTheCluster)
        {
            OnItsCluster(dbe, ids[10], (cluster, slot) => cluster.Set(QfUnit.Data, slot, cluster.GetReadOnly(QfUnit.Data, slot)));
            OnItsCluster(dbe, ids[10],
                (cluster, slot) => cluster.Set(QfUnit.Data, slot, cluster.GetReadOnly(QfUnit.Data, slot) with { Key = 100_001, Group = 7 }));
        }
        else
        {
            Write(dbe, ids[10], QfUnit.Data, d => d);
            Write(dbe, ids[10], QfUnit.Data, d => d with { Key = 100_001, Group = 7 });
        }

        dbe.WriteTickFence(1);
        IndexDataOracle.AssertIndexAgreesWithData<QfUnit>(dbe, "after the fence");
    }

    /// <summary>
    /// #1346: a write keeping the keys, then a destroy, in one tick: with no record the destroy removes the entity's index entries itself, and its slot owes
    /// the fence nothing (#1188's hold is not engaged).
    /// </summary>
    [VerifiesRule("IX-07")]
    [Test]
    public void AWriteKeepingTheKeys_ThenADestroy_RemovesTheEntriesAtOnce()
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe);
        var cs = ClusterState(dbe);
        var (chunk, slot) = LocationOf(dbe, ids[10]);

        Write(dbe, ids[10], QfUnit.Data, d => d);
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(ids[10]);
            tx.Commit();
        }

        Assert.That(cs.FenceOwedBits(chunk) & (1UL << slot), Is.Zero, "the freed slot owes the fence nothing");
        // Exact before the fence too: no key moved, so the destroy must have removed the entries itself (a query would hide them by occupancy).
        IndexDataOracle.AssertIndexAgreesWithData<QfUnit>(dbe, "after the destroy, before the fence");
        dbe.WriteTickFence(1);
        IndexDataOracle.AssertIndexAgreesWithData<QfUnit>(dbe, "after the fence");
    }

    /// <summary>
    /// #1346: the batched <c>ClusterRef.Set</c> records the entities whose keys it changes, and only them, and the fence moves every one; the others keep
    /// theirs. A single <c>Set</c> of one of them later in the tick records nothing more, and the key ends on the last value written.
    /// </summary>
    [VerifiesRule("IX-07")]
    [Test]
    public void TheBatchedSet_RecordsTheEntitiesWhoseKeysItChanges_AndTheFenceMovesThem()
    {
        using var dbe = SetupEngine();
        Spawn(dbe);
        var cs = ClusterState(dbe);
        int changedChunk = -1;
        ulong changedSlots = 0;
        Span<QfData> values = stackalloc QfData[64];
        using (var tx = dbe.CreateQuickTransaction())
        {
            using (var accessor = tx.For<QfUnit>())
            {
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    if (changedChunk >= 0)
                    {
                        continue;
                    }

                    changedChunk = cluster.ChunkId;
                    var all = cluster.OccupancyBits;
                    for (var rest = all; rest != 0; rest &= rest - 1)
                    {
                        var i = BitOperations.TrailingZeroCount(rest);
                        values[i] = cluster.GetReadOnly(QfUnit.Data, i);
                        if (i % 3 == 0)
                        {
                            values[i].Key += 1_000_001;   // a key move for every third entity; the others keep their keys
                            changedSlots |= 1UL << i;
                        }
                    }

                    cluster.Set(QfUnit.Data, all, values);
                    var first = BitOperations.TrailingZeroCount(changedSlots);
                    cluster.Set(QfUnit.Data, first, cluster.GetReadOnly(QfUnit.Data, first) with { Group = 77 });   // a second write, single Set
                }
            }

            tx.Commit();
        }

        Assert.That(cs.ClusterShadowBitmap.ReadWord(changedChunk), Is.EqualTo(changedSlots), "recorded: exactly the entities whose keys moved");
        Assert.That(cs.ClusterDirtyBitmap.ReadWord(changedChunk), Is.Not.Zero, "the cluster is marked for the fence");
        dbe.WriteTickFence(1);
        IndexDataOracle.AssertIndexAgreesWithData<QfUnit>(dbe, "after the fence");
    }

    /// <summary>
    /// #1346: an archetype with more indexed fields than the batched capture's key buffer holds for a full cluster records its entities one at a time, and
    /// the fence still moves every key.
    /// </summary>
    [VerifiesRule("IX-07")]
    [Test]
    [NonParallelizable]   // lowers a process-wide budget for its duration
    public void TheBatchedSet_OnAWideIndexedComponent_MovesEveryKey()
    {
        var budget = ArchetypeClusterState.BatchedCaptureKeyBudget;
        ArchetypeClusterState.BatchedCaptureKeyBudget = 64;
        try
        {
            BatchedSetOnAWideComponent();
        }
        finally
        {
            ArchetypeClusterState.BatchedCaptureKeyBudget = budget;
        }
    }

    private void BatchedSetOnAWideComponent()
    {
        using var dbe = SetupEngine();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 128; i++)
            {
                var w = new IwrWide { F0 = i % 5, F1 = i % 6, F2 = i % 7, F3 = i % 8, F4 = i % 9, F5 = i % 10, F6 = i % 11, F7 = i % 12, Key = i };
                tx.Spawn<IwrWideUnit>(IwrWideUnit.Wide.Set(in w));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(0);
        var cs = dbe._archetypeStates[Archetype<IwrWideUnit>.Metadata.ArchetypeId].ClusterState;
        Assert.That(cs.FenceIndexedFieldCount * cs.Layout.ClusterSize, Is.GreaterThan(ArchetypeClusterState.BatchedCaptureKeyBudget),
            "premise: a full cluster's keys exceed the stack budget");
        Span<IwrWide> values = stackalloc IwrWide[64];
        using (var tx = dbe.CreateQuickTransaction())
        {
            using (var accessor = tx.For<IwrWideUnit>())
            {
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    var all = cluster.OccupancyBits;
                    for (var rest = all; rest != 0; rest &= rest - 1)
                    {
                        var i = BitOperations.TrailingZeroCount(rest);
                        values[i] = cluster.GetReadOnly(IwrWideUnit.Wide, i);
                        values[i].F3 += 100;
                        values[i].Key += 10_000;
                    }

                    cluster.Set(IwrWideUnit.Wide, all, values);
                }
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        IndexDataOracle.AssertIndexAgreesWithData<IwrWideUnit>(dbe, "after the fence");
    }

    /// <summary>
    /// #1346: the batched <c>Set</c> refuses an empty slot and a values span that does not reach the highest slot, and under the Commit discipline it
    /// refuses before recording or marking anything.
    /// </summary>
    [Test]
    public void TheBatchedSet_RefusesBeforeAnyBookkeeping()
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe);
        var cs = ClusterState(dbe);
        var (deadChunk, deadSlot) = LocationOf(dbe, ids[3]);
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(ids[3]);
            tx.Commit();
        }

        dbe.WriteTickFence(1);   // the slot is free and nothing is pending
        Exception empty = null, shortSpan = null, commit = null;
        using (var tx = dbe.CreateQuickTransaction())
        {
            using (var accessor = tx.For<QfUnit>())
            {
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    if (cluster.ChunkId == deadChunk)
                    {
                        empty = TryBatch(cluster, 1UL << deadSlot, 64);
                    }

                    shortSpan ??= TryBatch(cluster, 1UL << 40, 8);
                }
            }

            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Deferred, CommitDiscipline.Commit))
        {
            using (var accessor = tx.For<QfUnit>())
            {
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    commit ??= TryBatch(cluster, cluster.OccupancyBits, 64, keyDelta: 1_000_001);
                }
            }

            tx.Commit();
        }

        Assert.That(empty, Is.InstanceOf<ArgumentException>(), "an empty slot");
        Assert.That(shortSpan, Is.InstanceOf<ArgumentException>(), "a values span short of the highest slot");
        Assert.That(commit, Is.InstanceOf<InvalidOperationException>().With.Message.Contains("Commit discipline"));
        Assert.That(cs.ClusterShadowBitmap.AnyTestAndSetSinceClear, Is.False, "nothing recorded");
        Assert.That(cs.ClusterDirtyBitmap.HasDirty, Is.False, "nothing marked");
    }

    // A ClusterRef is a ref struct, which no lambda can capture.
    private static Exception TryBatch(ClusterRef<QfUnit> cluster, ulong slots, int length, int keyDelta = 0)
    {
        try
        {
            Span<QfData> values = stackalloc QfData[64];
            for (var rest = cluster.OccupancyBits & slots; rest != 0; rest &= rest - 1)
            {
                var i = BitOperations.TrailingZeroCount(rest);
                if (i < length)
                {
                    values[i] = cluster.GetReadOnly(QfUnit.Data, i);
                    values[i].Key += keyDelta;
                }
            }

            cluster.Set(QfUnit.Data, slots, values[..length]);
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    /// <summary>
    /// #1342: two entities that swap their unique keys in one tick, and three that rotate theirs, move them at the fence: the final state holds each key
    /// once. The drain moved one key at a time and threw the #675 collision on the first, whichever came first.
    /// </summary>
    [VerifiesRule("IX-08")]
    [Test]
    public void UniqueKeysSwappedOrRotatedInOneTick_MoveAtTheFence()
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe);   // Key = 2 * i
        Write(dbe, ids[10], QfUnit.Data, d => d with { Key = 22 });   // 20 <-> 22
        Write(dbe, ids[11], QfUnit.Data, d => d with { Key = 20 });
        Write(dbe, ids[100], QfUnit.Data, d => d with { Key = 202 });   // 200 -> 202 -> 204 -> 200
        Write(dbe, ids[101], QfUnit.Data, d => d with { Key = 204 });
        Write(dbe, ids[102], QfUnit.Data, d => d with { Key = 200 });

        Assert.DoesNotThrow(() => dbe.WriteTickFence(1));
        IndexDataOracle.AssertIndexAgreesWithData<QfUnit>(dbe, "after the fence");
    }

    /// <summary>
    /// A unique collision (#1346): the collided entity leaves the key its data left and is not indexed under the one another entity holds — the index never
    /// names it under a key it does not hold, on any later tick, with no record left to cover it. The collision is raised once; the entity is indexed again
    /// by its next key-changing write.
    /// </summary>
    [Test]
    [VerifiesRule("IX-08")]
    public void AUniqueCollision_LeavesTheLoserOutOfTheIndex_UntilItsNextWrite()
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe);   // Key = 2 * i
        Write(dbe, ids[10], QfUnit.Data, d => d with { Key = 22 });   // 22 is entity 11's, which stays

        var first = Assert.Throws<InvalidOperationException>(() => dbe.WriteTickFence(1));
        Assert.That(first.Message, Does.Contain("unique index collision"));
        Assert.That(KeyThroughTheIndex(dbe, 20), Is.Empty, "entity 10 left 20");
        Assert.That(KeyThroughTheIndex(dbe, 22), Is.EquivalentTo(new[] { ids[11] }), "the holder keeps 22");
        Assert.DoesNotThrow(() => dbe.WriteTickFence(2), "raised once: nothing was left recorded");
        Assert.That(KeyThroughTheIndex(dbe, 20), Is.Empty, "still not under 20 on the next tick");

        Write(dbe, ids[11], QfUnit.Data, d => d with { Key = 23 });   // the holder moves away, and the loser is written again
        Write(dbe, ids[10], QfUnit.Data, d => d with { Key = 1_000_001 });
        Assert.DoesNotThrow(() => dbe.WriteTickFence(3));
        IndexDataOracle.AssertIndexAgreesWithData<QfUnit>(dbe, "after the loser's next write");
    }

    private static EntityId[] KeyThroughTheIndex(DatabaseEngine dbe, int key)
    {
        QueryPathProbe.Forced = ClusterScanPath.Selective;
        try
        {
            using var tx = dbe.CreateQuickTransaction();
            return [.. tx.Query<QfUnit>().WhereField<QfData>(d => d.Key == key).Execute()];
        }
        finally
        {
            QueryPathProbe.Reset();
        }
    }

    private delegate void ClusterWrite(ClusterRef<QfUnit> cluster, int slot);

    private static void OnItsCluster(DatabaseEngine dbe, EntityId id, ClusterWrite write)
    {
        using var tx = dbe.CreateQuickTransaction();
        using (var accessor = tx.For<QfUnit>())
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var entityIds = cluster.EntityIds;
                for (var slot = 0; slot < cluster.ClusterSize; slot++)
                {
                    if ((cluster.OccupancyBits & (1UL << slot)) != 0 && entityIds[slot] == (long)id.RawValue)
                    {
                        write(cluster, slot);
                    }
                }
            }
        }

        tx.Commit();
    }

    private static (int chunkId, int slot) LocationOf(DatabaseEngine dbe, EntityId id)
    {
        using var tx = dbe.CreateQuickTransaction();
        using var accessor = tx.For<QfUnit>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var entityIds = cluster.EntityIds;
            for (var slot = 0; slot < cluster.ClusterSize; slot++)
            {
                if ((cluster.OccupancyBits & (1UL << slot)) != 0 && entityIds[slot] == (long)id.RawValue)
                {
                    return (cluster.ChunkId, slot);
                }
            }
        }

        throw new InvalidOperationException($"{id} is in no cluster");
    }

    private static ref ClusterIndexField<PersistentStore> FieldAt(ArchetypeClusterState cs, int fieldOffset)
    {
        for (var s = 0; s < cs.IndexSlots.Length; s++)
        {
            for (var f = 0; f < cs.IndexSlots[s].Fields.Length; f++)
            {
                if (cs.IndexSlots[s].Fields[f].FieldOffset == fieldOffset)
                {
                    return ref cs.IndexSlots[s].Fields[f];
                }
            }
        }

        throw new InvalidOperationException($"no indexed field at offset {fieldOffset}");
    }
}
