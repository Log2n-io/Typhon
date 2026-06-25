using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

class DeferredCleanupTests : TestBase<DeferredCleanupTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<CompAArch>.Touch();
    }

    [Test]
    public void LongRunningTail_UntouchedEntities_RevisionsCleanedOnTailCommit()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // T1 starts — becomes the tail (oldest active transaction)
        using var t1 = dbe.CreateQuickTransaction();

        // T2 creates entity E, commits and disposes
        EntityId entityId;
        {
            using var t2 = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            entityId = t2.Spawn<CompAArch>(CompAArch.A.Set(in a));
            t2.Commit();
        }

        // T3 updates entity E, commits and disposes
        {
            using var t3 = dbe.CreateQuickTransaction();
            ref var a = ref t3.OpenMut(entityId).Write(CompAArch.A);
            a = new CompA(20);
            t3.Commit();
        }

        // Entity E should have 2 revisions (create + update), because T1 is blocking cleanup
        {
            using var tCheck = dbe.CreateQuickTransaction();
            var revCount = tCheck.GetRevisionCount<CompA>((long)entityId.RawValue);
            Assert.That(revCount, Is.EqualTo(2), "Entity should have 2 revisions while tail is blocking cleanup");
        }

        // T1 commits — never accessed E, but deferred cleanup should process it
        t1.Commit();

        // After T1 commits, deferred cleanup should have removed old revisions
        {
            using var tVerify = dbe.CreateQuickTransaction();
            var revCount = tVerify.GetRevisionCount<CompA>((long)entityId.RawValue);
            Assert.That(revCount, Is.EqualTo(1), "Entity should have 1 revision after tail commits and deferred cleanup runs");
        }
    }

    // A1.8 (03-recovery.md §6): recovery SCRUB collapses a multi-revision chain to exactly its HEAD — the highest-TSN committed
    // value — and is idempotent. Built directly against ComponentRevisionManager.ScrubChainToHead (the per-chain primitive the
    // recovery Phase-4 loop invokes), using a held tail transaction to retain history the way a checkpointed pre-crash chain would.
    [Test]
    public void Scrub_CollapsesMultiRevisionChain_ToHeadValue_Idempotent()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // A held tail transaction blocks lazy cleanup, so the chain retains every committed revision (as a checkpointed chain can).
        using var tail = dbe.CreateQuickTransaction();

        EntityId e;
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            e = t.Spawn<CompAArch>(CompAArch.A.Set(in a));
            t.Commit();
        }
        foreach (var v in new[] { 20, 30, 40, 50 })
        {
            using var t = dbe.CreateQuickTransaction();
            ref var a = ref t.OpenMut(e).Write(CompAArch.A);
            a = new CompA(v);
            t.Commit();
        }

        // 5 committed revisions (10,20,30,40,50) — cleanup is blocked by the held tail.
        using (var tc = dbe.CreateQuickTransaction())
        {
            Assert.That(tc.GetRevisionCount<CompA>((long)e.RawValue), Is.EqualTo(5), "precondition: the held tail retains 5 revisions");
        }

        var table = dbe.GetComponentTable<CompA>();
        var archetypeId = (ushort)((long)e.RawValue & 0xFFF);
        var heads = ComponentRevisionManager.EnumerateVersionedChainHeads(table, archetypeId);
        Assert.That(heads.ContainsKey((long)e.RawValue), Is.True, "the shared scan must find the entity's chain head");
        var firstChunkId = heads[(long)e.RawValue];

        void Scrub()
        {
            using var guard = EpochGuard.Enter(dbe.EpochManager);
            var cs = dbe.MMF.CreateChangeSet();
            var rev = table.CompRevTableSegment.CreateChunkAccessor(cs);
            var content = table.ComponentSegment.CreateChunkAccessor(cs);
            ComponentRevisionManager.ScrubChainToHead(table, firstChunkId, ref rev, ref content, out _);
            rev.Dispose();
            content.Dispose();
            cs.SaveChanges();
        }

        Scrub();

        using (var tv = dbe.CreateQuickTransaction())
        {
            Assert.That(tv.GetRevisionCount<CompA>((long)e.RawValue), Is.EqualTo(1), "scrub collapses the chain to a single head revision");
            Assert.That(tv.Open(e).Read(CompAArch.A).A, Is.EqualTo(50), "the surviving head is the highest-TSN committed value");
        }

        // Idempotent (AP-12): scrubbing an already-collapsed chain is a no-op.
        Scrub();
        using (var tv = dbe.CreateQuickTransaction())
        {
            Assert.That(tv.GetRevisionCount<CompA>((long)e.RawValue), Is.EqualTo(1), "re-scrub is idempotent");
            Assert.That(tv.Open(e).Read(CompAArch.A).A, Is.EqualTo(50));
        }
    }

    // A1.8 (03-recovery.md §6): the recovery orphan sweep frees chunks leaked by an interrupted pre-crash op (allocated but
    // never linked into a chain), while leaving every reachable chunk — and the surviving entity's data — intact.
    [Test]
    public void SweepOrphans_FreesUnreachableChunks_KeepsReachableData()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId e;
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            e = t.Spawn<CompAArch>(CompAArch.A.Set(in a));
            t.Commit();
        }

        var table = dbe.GetComponentTable<CompA>();
        var archetypeId = (ushort)((long)e.RawValue & 0xFFF);

        int rootChunkId, reachableContent, orphanRev, orphanContent;
        System.Collections.Generic.HashSet<int> reachableRoots;
        {
            using var guard = EpochGuard.Enter(dbe.EpochManager);   // accessors require an ambient epoch scope (as ScrubVersionedChains holds)

            var heads = ComponentRevisionManager.EnumerateVersionedChainHeads(table, archetypeId);
            rootChunkId = heads[(long)e.RawValue];
            reachableRoots = new System.Collections.Generic.HashSet<int>(heads.Values);

            var acc = table.CompRevTableSegment.CreateChunkAccessor();
            acc.GetChunkAsSpan(rootChunkId).Split(out System.Span<CompRevStorageHeader> _, out System.Span<CompRevStorageElement> els);
            reachableContent = els[0].ComponentChunkId;
            acc.Dispose();

            // Simulate chunks leaked by an interrupted pre-crash op: allocate but never link them into a chain.
            var csAlloc = dbe.MMF.CreateChangeSet();
            orphanRev = table.CompRevTableSegment.AllocateChunk(false, csAlloc);
            orphanContent = table.ComponentSegment.AllocateChunk(false, csAlloc);
            csAlloc.SaveChanges();
            Assert.That(table.CompRevTableSegment.IsChunkAllocated(orphanRev), Is.True, "precondition: orphan rev chunk allocated");
            Assert.That(table.ComponentSegment.IsChunkAllocated(orphanContent), Is.True, "precondition: orphan content chunk allocated");

            var cs = dbe.MMF.CreateChangeSet();
            var rev = table.CompRevTableSegment.CreateChunkAccessor(cs);
            ComponentRevisionManager.SweepTableOrphans(table, reachableRoots, ref rev);
            rev.Dispose();
            cs.SaveChanges();
        }

        Assert.That(table.CompRevTableSegment.IsChunkAllocated(orphanRev), Is.False, "orphan rev chunk must be freed");
        Assert.That(table.ComponentSegment.IsChunkAllocated(orphanContent), Is.False, "orphan content chunk must be freed");
        Assert.That(table.CompRevTableSegment.IsChunkAllocated(rootChunkId), Is.True, "reachable chain root must be kept");
        Assert.That(table.ComponentSegment.IsChunkAllocated(reachableContent), Is.True, "reachable content chunk must be kept");

        using (var tv = dbe.CreateQuickTransaction())
        {
            Assert.That(tv.Open(e).Read(CompAArch.A).A, Is.EqualTo(10), "the surviving entity must still read its committed value");
        }
    }

    [Test]
    public void DeferredCleanup_QueueMechanics_EnqueueAndProcess()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // T1 starts — becomes the tail
        using var t1 = dbe.CreateQuickTransaction();

        // T2 creates entity E, commits
        EntityId entityId;
        {
            using var t2 = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            entityId = t2.Spawn<CompAArch>(CompAArch.A.Set(in a));
            t2.Commit();
        }

        // T3 updates entity E, commits
        {
            using var t3 = dbe.CreateQuickTransaction();
            ref var a = ref t3.OpenMut(entityId).Write(CompAArch.A);
            a = new CompA(20);
            t3.Commit();
        }

        // Queue should have entries
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.GreaterThan(0), "Queue should have entries after non-tail commits");

        // T1 commits — triggers deferred cleanup
        t1.Commit();

        // Queue should be empty after processing
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.EqualTo(0), "Queue should be empty after tail commits");
    }

    [Test]
    public void DeferredCleanup_DuplicateEntity_SingleQueueEntry()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // T1 starts — becomes the tail
        using var t1 = dbe.CreateQuickTransaction();

        // T2 creates entity E, commits
        EntityId entityId;
        {
            using var t2 = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            entityId = t2.Spawn<CompAArch>(CompAArch.A.Set(in a));
            t2.Commit();
        }

        var queueSizeAfterCreate = dbe.DeferredCleanupManager.QueueSize;

        // T3 updates the same entity E, commits
        {
            using var t3 = dbe.CreateQuickTransaction();
            ref var a = ref t3.OpenMut(entityId).Write(CompAArch.A);
            a = new CompA(20);
            t3.Commit();
        }

        // Dedup: queue size should not have grown beyond what T2 enqueued for this entity
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.EqualTo(queueSizeAfterCreate),
            "Queue should have only 1 entry per entity due to dedup");

        t1.Commit();
    }

    [Test]
    public void DeferredCleanup_EntityDeletedBeforeProcess_NoError()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // T1 starts — becomes the tail
        using var t1 = dbe.CreateQuickTransaction();

        // T2 creates entity E, commits
        EntityId entityId;
        {
            using var t2 = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            entityId = t2.Spawn<CompAArch>(CompAArch.A.Set(in a));
            t2.Commit();
        }

        // T3 deletes entity E, commits
        {
            using var t3 = dbe.CreateQuickTransaction();
            t3.Destroy(entityId);
            t3.Commit();
        }

        // T1 commits — should not crash even though entity was deleted
        Assert.DoesNotThrow(() => t1.Commit(), "Processing deferred cleanup for a deleted entity should not throw");

        // Queue should be empty
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.EqualTo(0), "Queue should be empty after processing");
    }

    [Test]
    public void DeferredCleanup_ManyEntities_AllCleaned()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        const int entityCount = 50;

        // T1 starts — becomes the tail
        using var t1 = dbe.CreateQuickTransaction();

        // Create many entities in separate transactions
        var entityIds = new EntityId[entityCount];
        for (int i = 0; i < entityCount; i++)
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(i);
            entityIds[i] = t.Spawn<CompAArch>(CompAArch.A.Set(in a));
            t.Commit();
        }

        // Update each entity once more
        for (int i = 0; i < entityCount; i++)
        {
            using var t = dbe.CreateQuickTransaction();
            ref var a = ref t.OpenMut(entityIds[i]).Write(CompAArch.A);
            a = new CompA(i + 1000);
            t.Commit();
        }

        // All entities should have 2 revisions (blocked by T1)
        {
            using var tCheck = dbe.CreateQuickTransaction();
            for (int i = 0; i < entityCount; i++)
            {
                var revCount = tCheck.GetRevisionCount<CompA>((long)entityIds[i].RawValue);
                Assert.That(revCount, Is.EqualTo(2), $"Entity {i} should have 2 revisions while tail is blocking");
            }
        }

        // T1 commits — triggers cleanup of all entities
        t1.Commit();

        // All entities should now have 1 revision
        {
            using var tVerify = dbe.CreateQuickTransaction();
            for (int i = 0; i < entityCount; i++)
            {
                var revCount = tVerify.GetRevisionCount<CompA>((long)entityIds[i].RawValue);
                Assert.That(revCount, Is.EqualTo(1), $"Entity {i} should have 1 revision after deferred cleanup");
            }
        }

        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.EqualTo(0), "Queue should be empty after processing all entities");
    }

    [Test]
    public void DeferredCleanup_TailTransaction_CleansSelfViaDeferred()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // Single transaction creates and commits — is its own tail.
        // It enqueues to deferred cleanup, which is processed after the commit loop.
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(42);
            t.Spawn<CompAArch>(CompAArch.A.Set(in a));
            t.Commit();
        }

        // Queue should be empty — tail enqueued and processed in the same Commit call
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.EqualTo(0), "Queue should be empty after tail transaction commits");
    }

    [Test]
    public void DeferredCleanup_EnqueueDuplicate_MigratesToOlderTick()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // T1 (tail, TSN=X), T2 (TSN=X+1) both active
        using var t1 = dbe.CreateQuickTransaction();
        using var t2 = dbe.CreateQuickTransaction();

        // T3 creates entity E, commits → enqueued with blockingTSN = T1.TSN
        EntityId entityId;
        {
            using var t3 = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            entityId = t3.Spawn<CompAArch>(CompAArch.A.Set(in a));
            t3.Commit();
        }

        var queueSizeAfterCreate = dbe.DeferredCleanupManager.QueueSize;

        // T4 updates same entity E, commits → same entity enqueued again
        // Should be a no-op because T1.TSN is already the oldest blocker
        {
            using var t4 = dbe.CreateQuickTransaction();
            ref var a = ref t4.OpenMut(entityId).Write(CompAArch.A);
            a = new CompA(20);
            t4.Commit();
        }

        // Queue size should not have grown — dedup keeps the oldest blocking TSN
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.EqualTo(queueSizeAfterCreate),
            "Queue should have only 1 entry per entity — duplicate enqueue for same entity is deduplicated");

        t2.Commit();
        t1.Commit();
    }

    [Test]
    public void DeferredCleanup_ProcessPartialQueue()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // Setup: create entities A and B, each with 1 revision (the create)
        EntityId idA, idB;
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(1);
            idA = t.Spawn<CompAArch>(CompAArch.A.Set(in a));
            t.Commit();
        }
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(2);
            idB = t.Spawn<CompAArch>(CompAArch.A.Set(in a));
            t.Commit();
        }

        // Now start T_a (tail blocker) — all subsequent updates will be blocked by T_a
        var t_a = dbe.CreateQuickTransaction();

        // Update entity A → creates 2nd revision, blocked by T_a
        {
            using var t = dbe.CreateQuickTransaction();
            ref var a = ref t.OpenMut(idA).Write(CompAArch.A);
            a = new CompA(11);
            t.Commit();
        }

        // Start T_b BEFORE updating entity B, so T_b has an intermediate TSN
        var t_b = dbe.CreateQuickTransaction();

        // Update entity B → creates 2nd revision, blocked by T_a (still the tail)
        {
            using var t = dbe.CreateQuickTransaction();
            ref var a = ref t.OpenMut(idB).Write(CompAArch.A);
            a = new CompA(22);
            t.Commit();
        }

        long pkA = (long)idA.RawValue;
        long pkB = (long)idB.RawValue;

        // Both entities should have 2 revisions
        {
            using var tCheck = dbe.CreateQuickTransaction();
            Assert.That(tCheck.GetRevisionCount<CompA>(pkA), Is.EqualTo(2),
                "Entity A should have 2 revisions while T_a is blocking");
            Assert.That(tCheck.GetRevisionCount<CompA>(pkB), Is.EqualTo(2),
                "Entity B should have 2 revisions while T_a is blocking");
        }

        // Queue should have entries
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.GreaterThan(0),
            "Queue should have entries for blocked entities");
        var queueSizeBefore = dbe.DeferredCleanupManager.QueueSize;

        // Commit T_a → processes deferred cleanup for all entries blocked by T_a.TSN.
        // nextMinTSN will be T_b.TSN:
        // - Entity A: both create (TSN < T_b) and update (TSN < T_b) are below cutoff
        //   → sentinel keeps the latest (update), so 1 revision.
        // - Entity B: create (TSN < T_b) is below cutoff, update (TSN > T_b) is above
        //   → sentinel preserves create (T_b's snapshot may need it), so 2 revisions.
        t_a.Commit();
        t_a.Dispose();

        {
            using var tCheck = dbe.CreateQuickTransaction();
            Assert.That(tCheck.GetRevisionCount<CompA>(pkA), Is.EqualTo(1),
                "Entity A should have 1 revision after T_a committed (both revisions below T_b cutoff)");
            Assert.That(tCheck.GetRevisionCount<CompA>(pkB), Is.EqualTo(2),
                "Entity B should have 2 revisions after T_a committed (sentinel preserves create for T_b)");
        }

        // Queue should be empty — T_a was the only blocker
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.EqualTo(0),
            "Queue should be empty after T_a committed");

        // Verify counters show processing occurred
        Assert.That(dbe.DeferredCleanupManager.ProcessedTotal, Is.GreaterThan(0),
            "ProcessedTotal should be > 0 after deferred cleanup ran");

        // Commit T_b — entity B is not re-enqueued because it was already dequeued by T_a's processing.
        // The sentinel revision will be cleaned on entity B's next mutation (natural lifecycle).
        t_b.Commit();
        t_b.Dispose();
    }

    [Test]
    public void DeferredCleanup_TailDispose_WithoutCommit_TriggersCleanup()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // T1 (tail), T2 creates entity + commits, T3 updates + commits → 2 revisions, queued
        var t1 = dbe.CreateQuickTransaction();

        EntityId entityId;
        {
            using var t2 = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            entityId = t2.Spawn<CompAArch>(CompAArch.A.Set(in a));
            t2.Commit();
        }
        {
            using var t3 = dbe.CreateQuickTransaction();
            ref var a = ref t3.OpenMut(entityId).Write(CompAArch.A);
            a = new CompA(20);
            t3.Commit();
        }

        long pk = (long)entityId.RawValue;

        // Verify 2 revisions and queue has entries
        {
            using var tCheck = dbe.CreateQuickTransaction();
            Assert.That(tCheck.GetRevisionCount<CompA>(pk), Is.EqualTo(2));
        }
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.GreaterThan(0));

        // Dispose T1 without committing — should trigger deferred cleanup
        t1.Dispose();

        // Verify entity has 1 revision and queue is empty
        {
            using var tVerify = dbe.CreateQuickTransaction();
            Assert.That(tVerify.GetRevisionCount<CompA>(pk), Is.EqualTo(1),
                "Entity should have 1 revision after tail is disposed without commit");
        }
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.EqualTo(0),
            "Queue should be empty after tail dispose triggers deferred cleanup");
    }

    [Test]
    public void DeferredCleanup_TailRollback_TriggersCleanup()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // T1 (tail) makes some changes, T2 creates entity + commits
        var t1 = dbe.CreateQuickTransaction();
        {
            // T1 does some work (writes something to make rollback meaningful)
            var a = new CompA(99);
            t1.Spawn<CompAArch>(CompAArch.A.Set(in a));
        }

        EntityId entityId;
        {
            using var t2 = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            entityId = t2.Spawn<CompAArch>(CompAArch.A.Set(in a));
            t2.Commit();
        }
        {
            using var t3 = dbe.CreateQuickTransaction();
            ref var a = ref t3.OpenMut(entityId).Write(CompAArch.A);
            a = new CompA(20);
            t3.Commit();
        }

        long pk = (long)entityId.RawValue;

        // Verify 2 revisions
        {
            using var tCheck = dbe.CreateQuickTransaction();
            Assert.That(tCheck.GetRevisionCount<CompA>(pk), Is.EqualTo(2));
        }

        // Explicitly rollback T1, then dispose — deferred cleanup should fire
        t1.Rollback();
        t1.Dispose();

        // Verify entity has 1 revision and queue is empty
        {
            using var tVerify = dbe.CreateQuickTransaction();
            Assert.That(tVerify.GetRevisionCount<CompA>(pk), Is.EqualTo(1),
                "Entity should have 1 revision after tail rollback+dispose triggers deferred cleanup");
        }
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.EqualTo(0),
            "Queue should be empty after tail rollback triggers deferred cleanup");
    }

    [Test]
    public void DeferredCleanup_ConcurrentCommits_ProcessCorrectEntries()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        const int entityCount = 20;

        // T_blocker is the tail
        using var tBlocker = dbe.CreateQuickTransaction();

        // Create N entities across parallel tasks (each task creates one entity)
        var entityIds = new EntityId[entityCount];
        for (int i = 0; i < entityCount; i++)
        {
            using var t = dbe.CreateQuickTransaction();
            var a = new CompA(i);
            entityIds[i] = t.Spawn<CompAArch>(CompAArch.A.Set(in a));
            t.Commit();
        }

        // Update all entities (creates second revision for each)
        Parallel.For(0, entityCount, i =>
        {
            using var t = dbe.CreateQuickTransaction();
            ref var a = ref t.OpenMut(entityIds[i]).Write(CompAArch.A);
            a = new CompA(i + 1000);
            t.Commit();
        });

        // All entities should have 2 revisions (blocked by T_blocker)
        {
            using var tCheck = dbe.CreateQuickTransaction();
            for (int i = 0; i < entityCount; i++)
            {
                Assert.That(tCheck.GetRevisionCount<CompA>((long)entityIds[i].RawValue), Is.EqualTo(2),
                    $"Entity {i} should have 2 revisions while blocker is active");
            }
        }

        // Commit + dispose the blocker — cleanup fires in Dispose for the tail transaction
        tBlocker.Commit();
        tBlocker.Dispose();
        dbe.FlushDeferredCleanups();

        // Verify with a fresh transaction: all entities have 1 revision, queue is empty
        {
            using var tVerify = dbe.CreateQuickTransaction();
            for (int i = 0; i < entityCount; i++)
            {
                Assert.That(tVerify.GetRevisionCount<CompA>((long)entityIds[i].RawValue), Is.EqualTo(1),
                    $"Entity {i} should have 1 revision after deferred cleanup");
            }
        }
        Assert.That(dbe.DeferredCleanupManager.QueueSize, Is.EqualTo(0),
            "Queue should be empty after all concurrent entries are cleaned");
    }

    // ═══════════════════════════════════════════════════════════════
    // RemoveFromList Coverage Tests — direct EnqueueBatch calls
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void DeferredCleanup_EnqueueBatch_MigratesToOlderBlockingTSN()
    {
        // Exercises DeferredCleanupManager.RemoveFromList (lines 304-325):
        // When the same entity is enqueued with a SMALLER blockingTSN than its existing entry,
        // the entry is migrated from the old bucket to the new (older) bucket.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var table = dbe.GetComponentTable<CompA>();
        var dcm = dbe.DeferredCleanupManager;
        var baselineEnqueued = dcm.EnqueuedTotal; // System schema registration may have enqueued cleanup entries

        // Enqueue entity pk=1 under blockingTSN=10
        dcm.EnqueueBatch(10, new List<DeferredCleanupManager.CleanupEntry>
        {
            new() { Table = table, PrimaryKey = 1 }
        });
        Assert.That(dcm.QueueSize, Is.EqualTo(1));
        Assert.That(dcm.EnqueuedTotal, Is.EqualTo(baselineEnqueued + 1));

        // Enqueue same entity under blockingTSN=5 (older) → triggers RemoveFromList(10, table, 1)
        // Entity moves from TSN=10 bucket to TSN=5 bucket
        dcm.EnqueueBatch(5, new List<DeferredCleanupManager.CleanupEntry>
        {
            new() { Table = table, PrimaryKey = 1 }
        });
        Assert.That(dcm.QueueSize, Is.EqualTo(1), "Queue should still have 1 entry after migration (not duplicated)");
        Assert.That(dcm.EnqueuedTotal, Is.EqualTo(baselineEnqueued + 2), "EnqueuedTotal should reflect both enqueue operations");
    }

    [Test]
    public void DeferredCleanup_EnqueueBatch_MigrationRemovesEmptyBucket()
    {
        // Tests the empty-list cleanup path in RemoveFromList (lines 320-324):
        // When migration removes the last entity from a TSN bucket, the empty bucket is removed
        // and its list is returned to the pool.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var table = dbe.GetComponentTable<CompA>();
        var dcm = dbe.DeferredCleanupManager;

        // Enqueue single entity pk=1 under blockingTSN=10 (creates TSN=10 bucket with 1 entry)
        dcm.EnqueueBatch(10, new List<DeferredCleanupManager.CleanupEntry>
        {
            new() { Table = table, PrimaryKey = 1 }
        });
        Assert.That(dcm.QueueSize, Is.EqualTo(1));

        // Migrate pk=1 to blockingTSN=5 → RemoveFromList removes pk=1 from TSN=10 bucket
        // TSN=10 bucket is now empty → should be removed, list returned to pool
        dcm.EnqueueBatch(5, new List<DeferredCleanupManager.CleanupEntry>
        {
            new() { Table = table, PrimaryKey = 1 }
        });
        Assert.That(dcm.QueueSize, Is.EqualTo(1), "Queue should have 1 entry in TSN=5 bucket");

        // Enqueue a new entity under TSN=10 → should reuse the pooled list
        dcm.EnqueueBatch(10, new List<DeferredCleanupManager.CleanupEntry>
        {
            new() { Table = table, PrimaryKey = 2 }
        });
        Assert.That(dcm.QueueSize, Is.EqualTo(2), "Queue should have 2 entries across two buckets");
    }

    [Test]
    public void DeferredCleanup_EnqueueBatch_MigrationWithMultipleEntitiesInBucket()
    {
        // Tests RemoveFromList when the bucket has multiple entries (only the migrated one is removed).
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var table = dbe.GetComponentTable<CompA>();
        var dcm = dbe.DeferredCleanupManager;

        // Enqueue two entities under blockingTSN=10
        dcm.EnqueueBatch(10, new List<DeferredCleanupManager.CleanupEntry>
        {
            new() { Table = table, PrimaryKey = 1 },
            new() { Table = table, PrimaryKey = 2 }
        });
        Assert.That(dcm.QueueSize, Is.EqualTo(2));

        // Migrate pk=1 to blockingTSN=5 → RemoveFromList removes pk=1 from TSN=10 bucket
        // TSN=10 bucket still has pk=2, so it stays
        dcm.EnqueueBatch(5, new List<DeferredCleanupManager.CleanupEntry>
        {
            new() { Table = table, PrimaryKey = 1 }
        });
        Assert.That(dcm.QueueSize, Is.EqualTo(2), "Queue should still have 2 entries (pk=2 in TSN=10, pk=1 in TSN=5)");

        // Migrate pk=2 to blockingTSN=3 → RemoveFromList removes pk=2 from TSN=10 bucket
        // TSN=10 bucket is now empty → removed, list returned to pool
        dcm.EnqueueBatch(3, new List<DeferredCleanupManager.CleanupEntry>
        {
            new() { Table = table, PrimaryKey = 2 }
        });
        Assert.That(dcm.QueueSize, Is.EqualTo(2), "Queue should have 2 entries (pk=1 in TSN=5, pk=2 in TSN=3)");
    }

    [Test]
    public void DeferredCleanup_EnqueueBatch_SameOrNewerTSN_NopDedup()
    {
        // Verifies the dedup path (lines 121-123): when blockingTSN >= existingTSN, the entry is
        // skipped (already queued under an older blocking TSN).
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var table = dbe.GetComponentTable<CompA>();
        var dcm = dbe.DeferredCleanupManager;
        var baselineEnqueued = dcm.EnqueuedTotal; // System schema registration may have enqueued cleanup entries

        // Enqueue entity pk=1 under blockingTSN=5
        dcm.EnqueueBatch(5, new List<DeferredCleanupManager.CleanupEntry>
        {
            new() { Table = table, PrimaryKey = 1 }
        });
        Assert.That(dcm.QueueSize, Is.EqualTo(1));
        Assert.That(dcm.EnqueuedTotal, Is.EqualTo(baselineEnqueued + 1));

        // Re-enqueue same entity under blockingTSN=10 (newer/larger) → should be skipped
        dcm.EnqueueBatch(10, new List<DeferredCleanupManager.CleanupEntry>
        {
            new() { Table = table, PrimaryKey = 1 }
        });
        Assert.That(dcm.QueueSize, Is.EqualTo(1), "Queue should still have 1 entry (dedup skipped)");
        Assert.That(dcm.EnqueuedTotal, Is.EqualTo(baselineEnqueued + 1), "EnqueuedTotal should not increment for skipped entries");

        // Re-enqueue under same blockingTSN=5 → should also be skipped
        dcm.EnqueueBatch(5, new List<DeferredCleanupManager.CleanupEntry>
        {
            new() { Table = table, PrimaryKey = 1 }
        });
        Assert.That(dcm.QueueSize, Is.EqualTo(1));
        Assert.That(dcm.EnqueuedTotal, Is.EqualTo(baselineEnqueued + 1));
    }
}
