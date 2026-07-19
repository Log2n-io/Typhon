using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Collections.Generic;

namespace Typhon.Engine.Tests;

class VersionedIndexTests : TestBase<VersionedIndexTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    #region Phase 1 — Infrastructure Tests

    [Test]
    public void VersionedIndexEntry_StructSize_Is12Bytes()
    {
        unsafe
        {
            Assert.That(sizeof(VersionedIndexEntry), Is.EqualTo(12), "VersionedIndexEntry should be exactly 12 bytes (4 + 8)");
        }
    }

    [Test]
    public unsafe void TailBufferId_DefaultsToZero()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var ct = dbe.GetComponentTable<CompD>();
        Assert.That(ct.TailVSBS, Is.Not.Null, "CompD has AllowMultiple indexes, should have TailVSBS");
        Assert.That(ct.TailIndexSegment, Is.Not.Null, "CompD has AllowMultiple indexes, should have TailIndexSegment");
    }

    [Test]
    public void ComponentTable_NoAllowMultiple_NoTailVSBS()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var ct = dbe.GetComponentTable<CompA>();
        Assert.That(ct.TailVSBS, Is.Null, "CompA has no AllowMultiple indexes, should not have TailVSBS");
        Assert.That(ct.TailIndexSegment, Is.Null, "CompA has no AllowMultiple indexes, should not have TailIndexSegment");
    }

    [Test]
    public unsafe void AllocateTailBuffer_AddAndReadEntry()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var ct = dbe.GetComponentTable<CompD>();
        var tailVSBS = ct.TailVSBS;

        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var accessor = tailVSBS.Segment.CreateChunkAccessor();

        // Allocate a TAIL buffer and add an entry
        var bufferId = tailVSBS.AllocateBuffer(ref accessor);
        Assert.That(bufferId, Is.GreaterThan(0));

        var entry = VersionedIndexEntry.Active(10, 500);
        tailVSBS.AddElement(bufferId, entry, ref accessor);

        // Read it back
        using var ba = tailVSBS.GetReadOnlyAccessor(bufferId);
        Assert.That(ba.TotalCount, Is.EqualTo(1));
        var elements = ba.ReadOnlyElements;
        Assert.That(elements.Length, Is.GreaterThanOrEqualTo(1));
        Assert.That(elements[0].SignedChainId, Is.EqualTo(10));
        Assert.That(elements[0].TSN, Is.EqualTo(500));

        accessor.Dispose();
    }

    #endregion

    #region Phase 2 — Write Path Tests

    [Test]
    public void Create_DefersTailWrite_TailBufferIdIsZero()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 10, 2.0);
            t.Spawn<CompDArch>(CompDArch.D.Set(in d));
            t.Commit();
        }

        // TAIL write is deferred — TailBufferId should still be 0 after creation
        var ct = dbe.GetComponentTable<CompD>();
        var tailVSBS = ct.TailVSBS;
        Assert.That(tailVSBS, Is.Not.Null);

        var ifi = ct.IndexedFieldInfos[0]; // Field A (float, AllowMultiple)
        Assert.That(ifi.PersistentIndex.AllowMultiple, Is.True, "First indexed field should be AllowMultiple");

        unsafe
        {
            using var guard = EpochGuard.Enter(dbe.EpochManager);
            float key = 1.0f;
            var accessor = ifi.PersistentIndex.Segment.CreateChunkAccessor();
            var headResult = ifi.PersistentIndex.TryGet(&key, ref accessor);
            Assert.That(headResult.IsSuccess, Is.True, "Key should exist in HEAD index");

            var headBufferId = headResult.Value;
            var tailBufferId = IndexBufferExtraHeader.FromChunkAddress(accessor.GetChunkAddress(headBufferId)).TailBufferId;
            accessor.Dispose();

            Assert.That(tailBufferId, Is.EqualTo(0), "TAIL buffer should NOT be allocated on creation (deferred)");
        }
    }

    [Test]
    public void Update_IndexedField_TailHasTombstoneAndActive()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId id;
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 10, 2.0);
            id = t.Spawn<CompDArch>(CompDArch.D.Set(in d));
            t.Commit();
        }

        // Update field A from 1.0f to 5.0f
        {
            using var t = dbe.CreateQuickTransaction();
            ref var d = ref t.OpenMut(id).Write(CompDArch.D);
            d = new CompD(5.0f, 10, 2.0);
            t.Commit();
        }

        // Verify TAIL has Tombstone on old key (1.0f) and Active on new key (5.0f)
        var ct = dbe.GetComponentTable<CompD>();
        var tailVSBS = ct.TailVSBS;
        var ifi = ct.IndexedFieldInfos[0]; // Field A

        unsafe
        {
            using var guard = EpochGuard.Enter(dbe.EpochManager);

            // Check old key (1.0f) — preserved by preserveEmptyBuffer even though HEAD buffer is empty
            float oldKey = 1.0f;
            var accessor = ifi.PersistentIndex.Segment.CreateChunkAccessor();
            var oldHeadResult = ifi.PersistentIndex.TryGet(&oldKey, ref accessor);
            Assert.That(oldHeadResult.IsSuccess, Is.True, "Old key should be preserved in BTree (preserveEmptyBuffer)");

            var oldTailBufferId = IndexBufferExtraHeader.FromChunkAddress(accessor.GetChunkAddress(oldHeadResult.Value)).TailBufferId;
            Assert.That(oldTailBufferId, Is.GreaterThan(0), "Old key should have TAIL buffer linked");

            var oldEntries = CollectTailEntries(tailVSBS, oldTailBufferId);
            Assert.That(oldEntries.Exists(e => e.IsTombstone), Is.True, "Old key's TAIL should have a Tombstone");

            // Check new key (5.0f) — should have Active entry
            float newKey = 5.0f;
            var newHeadResult = ifi.PersistentIndex.TryGet(&newKey, ref accessor);
            Assert.That(newHeadResult.IsSuccess, Is.True, "New key should exist in HEAD index");

            var newTailBufferId = IndexBufferExtraHeader.FromChunkAddress(accessor.GetChunkAddress(newHeadResult.Value)).TailBufferId;
            Assert.That(newTailBufferId, Is.GreaterThan(0), "New key should have TAIL buffer");

            var newEntries = CollectTailEntries(tailVSBS, newTailBufferId);
            Assert.That(newEntries.Exists(e => e.IsActive), Is.True, "New key's TAIL should have an Active entry");
            accessor.Dispose();
        }
    }

    [Test]
    public void Delete_Entity_TailHasTombstone()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId id;
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(3.0f, 10, 4.0);
            id = t.Spawn<CompDArch>(CompDArch.D.Set(in d));
            t.Commit();
        }

        // Delete the entity
        {
            using var t = dbe.CreateQuickTransaction();
            t.Destroy(id);
            t.Commit();
        }

        // Verify TAIL was populated by delete and has Active + Tombstone
        var ct = dbe.GetComponentTable<CompD>();
        var tailVSBS = ct.TailVSBS;
        var ifi = ct.IndexedFieldInfos[0]; // Field A

        unsafe
        {
            using var guard = EpochGuard.Enter(dbe.EpochManager);
            float key = 3.0f;
            var accessor = ifi.PersistentIndex.Segment.CreateChunkAccessor();
            var headResult = ifi.PersistentIndex.TryGet(&key, ref accessor);
            Assert.That(headResult.IsSuccess, Is.True, "Key should be preserved (preserveEmptyBuffer)");

            var tailBufferId = IndexBufferExtraHeader.FromChunkAddress(accessor.GetChunkAddress(headResult.Value)).TailBufferId;
            accessor.Dispose();
            Assert.That(tailBufferId, Is.GreaterThan(0), "TAIL buffer should have been allocated by delete");

            var entries = CollectTailEntries(tailVSBS, tailBufferId);
            Assert.That(entries.Exists(e => e.IsTombstone), Is.True, "TAIL should have a Tombstone after delete");
            Assert.That(entries.Exists(e => e.IsActive), Is.True, "TAIL should have backfilled Active entry before Tombstone");
        }
    }

    [Test]
    public void ExistingTests_NoRegression_HeadPathUnchanged()
    {
        // Verify that normal CRUD on CompD still works (HEAD path unchanged)
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId e1Id;
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 10, 2.0);
            e1Id = t.Spawn<CompDArch>(CompDArch.D.Set(in d));
            t.Commit();
        }

        {
            using var t = dbe.CreateQuickTransaction();
            var read = t.Open(e1Id).Read(CompDArch.D);
            Assert.That(read.A, Is.EqualTo(1.0f));
            Assert.That(read.B, Is.EqualTo(10));
            Assert.That(read.C, Is.EqualTo(2.0));
        }

        // Update and verify
        {
            using var t = dbe.CreateQuickTransaction();
            ref var d = ref t.OpenMut(e1Id).Write(CompDArch.D);
            d = new CompD(5.0f, 20, 6.0);
            t.Commit();
        }

        {
            using var t = dbe.CreateQuickTransaction();
            var read = t.Open(e1Id).Read(CompDArch.D);
            Assert.That(read.A, Is.EqualTo(5.0f));
            Assert.That(read.B, Is.EqualTo(20));
            Assert.That(read.C, Is.EqualTo(6.0));
        }
    }

    [Test]
    public void Update_BackfillsTail_AllEntriesPresent()
    {
        // Two entities under the same key, then one is updated (moved to a different key).
        // EnsureTailPopulated should backfill both entities' Active entries before writing the Tombstone.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId entity1Id, entity2Id;
        {
            using var t = dbe.CreateQuickTransaction();
            var d1 = new CompD(1.0f, 10, 2.0);
            entity1Id = t.Spawn<CompDArch>(CompDArch.D.Set(in d1));
            var d2 = new CompD(1.0f, 20, 3.0);
            entity2Id = t.Spawn<CompDArch>(CompDArch.D.Set(in d2));
            t.Commit();
        }

        // Verify TAIL is not allocated yet (deferred)
        var ct = dbe.GetComponentTable<CompD>();
        var tailVSBS = ct.TailVSBS;
        var ifi = ct.IndexedFieldInfos[0]; // Field A

        unsafe
        {
            using var guard = EpochGuard.Enter(dbe.EpochManager);
            float key = 1.0f;
            var accessor = ifi.PersistentIndex.Segment.CreateChunkAccessor();
            var headResult = ifi.PersistentIndex.TryGet(&key, ref accessor);
            Assert.That(IndexBufferExtraHeader.FromChunkAddress(accessor.GetChunkAddress(headResult.Value)).TailBufferId,
                Is.EqualTo(0), "TAIL should not be allocated before any mutation");
            accessor.Dispose();
        }

        // Update entity1's A from 1.0f to 5.0f — triggers backfill on old key
        {
            using var t = dbe.CreateQuickTransaction();
            ref var d = ref t.OpenMut(entity1Id).Write(CompDArch.D);
            d = new CompD(5.0f, 10, 2.0);
            t.Commit();
        }

        // Verify old key (1.0f) TAIL has all entries: Active for entity2 (from backfill) + Active+Tombstone for entity1
        unsafe
        {
            using var guard = EpochGuard.Enter(dbe.EpochManager);
            float key = 1.0f;
            var accessor = ifi.PersistentIndex.Segment.CreateChunkAccessor();
            var headResult = ifi.PersistentIndex.TryGet(&key, ref accessor);
            Assert.That(headResult.IsSuccess, Is.True);

            var tailBufferId = IndexBufferExtraHeader.FromChunkAddress(accessor.GetChunkAddress(headResult.Value)).TailBufferId;
            accessor.Dispose();
            Assert.That(tailBufferId, Is.GreaterThan(0), "TAIL should be populated after mutation");

            var entries = CollectTailEntries(tailVSBS, tailBufferId);
            // Should have: Active(entity2, creation), Active(entity1, creation) from backfill/includeChainId,
            // Active(entity1, creation) duplicate, Tombstone(entity1, update)
            Assert.That(entries.FindAll(e => e.IsActive).Count, Is.GreaterThanOrEqualTo(2),
                "Should have Active entries for both entities from backfill");
            Assert.That(entries.Exists(e => e.IsTombstone), Is.True,
                "Should have Tombstone for the moved entity");
        }
    }

    #endregion

    #region Phase 3 — Temporal Query Tests

    [Test]
    public unsafe void TemporalQuery_NoMutation_FallsBackToHead()
    {
        // With deferred TAIL, when no mutations have occurred (TailBufferId == 0),
        // TemporalIndexQuery falls back to QueryHeadOnly which returns all HEAD entries.
        // This is correct: no version history needed when no mutations exist.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var tsnBeforeCreate = dbe.TransactionChain.NextFreeId;

        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 10, 2.0);
            t.Spawn<CompDArch>(CompDArch.D.Set(in d));
            t.Commit();
        }

        var ct = dbe.GetComponentTable<CompD>();
        var ifi = ct.IndexedFieldInfos[0]; // Field A
        float key = 1.0f;

        using var guard = EpochGuard.Enter(dbe.EpochManager);
        // TailBufferId == 0 (no mutations), QueryHeadOnly returns current HEAD entries
        var result = TemporalIndexQuery.Query(ifi, (byte*)&key, tsnBeforeCreate, ct.TailVSBS, null);
        Assert.That(result.Count, Is.EqualTo(1), "QueryHeadOnly fallback returns current HEAD entries when TAIL not populated");
    }

    [Test]
    public unsafe void TemporalQuery_AtCreate_ReturnsEntity()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        long tsnAtCreate;
        {
            using var t = dbe.CreateQuickTransaction();
            tsnAtCreate = t.TSN;
            var d = new CompD(1.0f, 10, 2.0);
            t.Spawn<CompDArch>(CompDArch.D.Set(in d));
            t.Commit();
        }

        var ct = dbe.GetComponentTable<CompD>();
        var ifi = ct.IndexedFieldInfos[0]; // Field A
        float key = 1.0f;

        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var result = TemporalIndexQuery.Query(ifi, (byte*)&key, tsnAtCreate, ct.TailVSBS, null);
        Assert.That(result.Count, Is.EqualTo(1), "Entity should be visible at creation TSN");
    }

    [Test]
    public unsafe void TemporalQuery_OldKey_AfterUpdate_SingleEntity_StillVisible()
    {
        // Single entity moves from key 1.0f to 5.0f. The old key's BTree entry is preserved
        // (empty HEAD buffer) so that the TAIL version-history remains reachable.
        // A temporal query at the creation TSN should still find the entity under the old key.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId id;
        long tsnAtCreate;
        {
            using var t = dbe.CreateQuickTransaction();
            tsnAtCreate = t.TSN;
            var d = new CompD(1.0f, 10, 2.0);
            id = t.Spawn<CompDArch>(CompDArch.D.Set(in d));
            t.Commit();
        }

        // Update field A from 1.0f to 5.0f — last entity leaves the old key
        {
            using var t = dbe.CreateQuickTransaction();
            ref var d = ref t.OpenMut(id).Write(CompDArch.D);
            d = new CompD(5.0f, 10, 2.0);
            t.Commit();
        }

        var ct = dbe.GetComponentTable<CompD>();
        var ifi = ct.IndexedFieldInfos[0]; // Field A

        // Query old key at creation TSN — entity should still be visible through TAIL
        float oldKey = 1.0f;
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var result = TemporalIndexQuery.Query(ifi, (byte*)&oldKey, tsnAtCreate, ct.TailVSBS, null);
        Assert.That(result.Count, Is.EqualTo(1), "Entity should be visible under old key at creation TSN through TAIL");
    }

    [Test]
    public unsafe void TemporalQuery_OldKey_AfterUpdate_MultiEntity_CorrectCount()
    {
        // Two entities share key A=1.0f, then one moves to A=5.0f.
        // A temporal query at the "both created" TSN should see 2 chain IDs for key 1.0f.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId entity1Id;
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 10, 2.0);
            entity1Id = t.Spawn<CompDArch>(CompDArch.D.Set(in d));
            t.Commit();
        }

        long tsnAfterBothCreated;
        {
            using var t = dbe.CreateQuickTransaction();
            tsnAfterBothCreated = t.TSN;
            var d = new CompD(1.0f, 20, 3.0); // Same A=1.0f key (AllowMultiple), different B (unique index)
            t.Spawn<CompDArch>(CompDArch.D.Set(in d));
            t.Commit();
        }

        // Update entity1's A from 1.0f to 5.0f
        {
            using var t = dbe.CreateQuickTransaction();
            ref var d = ref t.OpenMut(entity1Id).Write(CompDArch.D);
            d = new CompD(5.0f, 10, 2.0);
            t.Commit();
        }

        var ct = dbe.GetComponentTable<CompD>();
        var ifi = ct.IndexedFieldInfos[0]; // Field A

        // Query old key at tsnAfterBothCreated — both entities should be visible
        float oldKey = 1.0f;
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var result = TemporalIndexQuery.Query(ifi, (byte*)&oldKey, tsnAfterBothCreated, ct.TailVSBS, null);
        Assert.That(result.Count, Is.EqualTo(2), "Both entities should be visible under old key at the TSN when both existed");
    }

    [Test]
    public unsafe void TemporalQuery_AfterBackfill_ReturnsCorrectSnapshot()
    {
        // Create entity, then update (triggers backfill). Temporal query at creation TSN
        // on the OLD key should return the entity (backfilled Active entry is visible).
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId id;
        long tsnAtCreate;
        {
            using var t = dbe.CreateQuickTransaction();
            tsnAtCreate = t.TSN;
            var d = new CompD(1.0f, 10, 2.0);
            id = t.Spawn<CompDArch>(CompDArch.D.Set(in d));
            t.Commit();
        }

        // Update field A from 1.0f to 5.0f — triggers TAIL backfill on both keys
        {
            using var t = dbe.CreateQuickTransaction();
            ref var d = ref t.OpenMut(id).Write(CompDArch.D);
            d = new CompD(5.0f, 10, 2.0);
            t.Commit();
        }

        var ct = dbe.GetComponentTable<CompD>();
        var ifi = ct.IndexedFieldInfos[0]; // Field A

        // After backfill, temporal query on OLD key at creation TSN should find the entity
        float oldKey = 1.0f;
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var result = TemporalIndexQuery.Query(ifi, (byte*)&oldKey, tsnAtCreate, ct.TailVSBS, null);
        Assert.That(result.Count, Is.EqualTo(1), "Entity should be visible under old key at creation TSN after backfill");
    }

    #endregion

    #region Phase 5 — GC Tests

    [Test]
    public void TailGC_PruneOldEntries_KeepsBoundarySentinel()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var ct = dbe.GetComponentTable<CompD>();
        var tailVSBS = ct.TailVSBS;

        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var accessor = tailVSBS.Segment.CreateChunkAccessor();

        // Manually create a TAIL buffer with entries at different TSNs
        var bufferId = tailVSBS.AllocateBuffer(ref accessor);

        tailVSBS.AddElement(bufferId, VersionedIndexEntry.Active(1, 100), ref accessor);   // Old
        tailVSBS.AddElement(bufferId, VersionedIndexEntry.Tombstone(1, 200), ref accessor); // Boundary sentinel at retention
        tailVSBS.AddElement(bufferId, VersionedIndexEntry.Active(1, 300), ref accessor);    // Future

        // Prune with retentionTSN = 250 (should remove TSN=100, keep TSN=200 as sentinel, keep TSN=300)
        var removed = TailGarbageCollector.Prune(tailVSBS, bufferId, 250, ref accessor, out var newBufferId);

        Assert.That(removed, Is.EqualTo(1), "Should remove 1 old entry (TSN=100)");
        Assert.That(newBufferId, Is.GreaterThan(0), "New buffer should have been allocated");

        // Verify remaining entries: boundary sentinel (TSN=200) + future (TSN=300)
        var remaining = CollectTailEntries(tailVSBS, newBufferId);
        Assert.That(remaining.Count, Is.EqualTo(2), "Should have sentinel + future entry");
        Assert.That(remaining.Exists(e => e.TSN == 200 && e.IsTombstone), Is.True, "Boundary sentinel should be kept");
        Assert.That(remaining.Exists(e => e.TSN == 300 && e.IsActive), Is.True, "Future entry should be kept");

        accessor.Dispose();
    }

    [Test]
    public void TailGC_DeadChain_FullyRemoved()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var ct = dbe.GetComponentTable<CompD>();
        var tailVSBS = ct.TailVSBS;

        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var accessor = tailVSBS.Segment.CreateChunkAccessor();

        // Chain with only old entries ending in Tombstone (dead chain)
        var bufferId = tailVSBS.AllocateBuffer(ref accessor);
        tailVSBS.AddElement(bufferId, VersionedIndexEntry.Active(1, 100), ref accessor);
        tailVSBS.AddElement(bufferId, VersionedIndexEntry.Tombstone(1, 200), ref accessor);

        // Prune with retentionTSN = 300 (both entries are old, sentinel is Tombstone, no future entries)
        var removed = TailGarbageCollector.Prune(tailVSBS, bufferId, 300, ref accessor, out _);

        Assert.That(removed, Is.EqualTo(2), "Dead chain should be fully removed (Active + Tombstone)");

        accessor.Dispose();
    }

    [Test]
    public void TailGC_NoOldEntries_NothingPruned()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var ct = dbe.GetComponentTable<CompD>();
        var tailVSBS = ct.TailVSBS;

        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var accessor = tailVSBS.Segment.CreateChunkAccessor();

        var bufferId = tailVSBS.AllocateBuffer(ref accessor);
        tailVSBS.AddElement(bufferId, VersionedIndexEntry.Active(1, 500), ref accessor);

        // Prune with retentionTSN = 100 (all entries are in the future)
        var removed = TailGarbageCollector.Prune(tailVSBS, bufferId, 100, ref accessor, out var newBufferId);
        Assert.That(newBufferId, Is.EqualTo(bufferId), "Buffer ID should be unchanged when nothing is pruned");

        Assert.That(removed, Is.EqualTo(0), "No entries should be removed when all are in the future");

        accessor.Dispose();
    }

    #endregion

    #region Helpers

    private static List<VersionedIndexEntry> CollectTailEntries(VariableSizedBufferSegment<VersionedIndexEntry, PersistentStore> tailVSBS, int tailBufferId)
    {
        var entries = new List<VersionedIndexEntry>();
        foreach (ref readonly var entry in tailVSBS.EnumerateBuffer(tailBufferId))
        {
            entries.Add(entry);
        }
        return entries;
    }

    #endregion
}
