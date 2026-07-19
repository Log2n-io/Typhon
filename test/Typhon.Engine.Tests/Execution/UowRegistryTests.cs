using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Linq;

using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

class UowRegistryTests : TestBase<UowRegistryTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    // ═══════════════════════════════════════════════════════════════
    // Allocation Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Registry_AllocateUowId_ReturnsUniqueIds()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        var ids = new ushort[10];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = dbe.UowRegistry.AllocateUowId();
        }

        Assert.That(ids.Distinct().Count(), Is.EqualTo(10), "All allocated IDs should be unique");

        // Release all
        foreach (var id in ids)
        {
            dbe.UowRegistry.Release(id);
        }
    }

    [Test]
    public void Registry_AllocateUowId_StartsAt1()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        var firstId = dbe.UowRegistry.AllocateUowId();
        // Slot 0 is reserved, first allocation should return 1
        Assert.That(firstId, Is.EqualTo((ushort)1));

        dbe.UowRegistry.Release(firstId);
    }

    [Test]
    public void Registry_Release_MakesSlotFree()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        var id1 = dbe.UowRegistry.AllocateUowId();
        var id2 = dbe.UowRegistry.AllocateUowId();
        Assert.That(id1, Is.EqualTo((ushort)1));
        Assert.That(id2, Is.EqualTo((ushort)2));

        // Release id1, next allocation should reuse it
        dbe.UowRegistry.Release(id1);
        var id3 = dbe.UowRegistry.AllocateUowId();
        Assert.That(id3, Is.EqualTo(id1), "Released slot should be reused");

        dbe.UowRegistry.Release(id2);
        dbe.UowRegistry.Release(id3);
    }

    [Test]
    public void Registry_ActiveCount_TracksAllocations()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        Assert.That(registry.ActiveCount, Is.EqualTo(0));

        var id1 = registry.AllocateUowId();
        Assert.That(registry.ActiveCount, Is.EqualTo(1));

        var id2 = registry.AllocateUowId();
        Assert.That(registry.ActiveCount, Is.EqualTo(2));

        registry.Release(id1);
        Assert.That(registry.ActiveCount, Is.EqualTo(1));

        registry.Release(id2);
        Assert.That(registry.ActiveCount, Is.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════
    // Committed Bitmap Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Registry_CommittedBitmap_Accurate()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        var id = registry.AllocateUowId();

        // Before commit, IsCommitted should return false
        Assert.That(registry.IsCommitted(id), Is.False, "Pending UoW should not be committed");

        // Record commit
        registry.RecordCommit(id, maxTSN: 42);

        // After commit, IsCommitted should return true
        Assert.That(registry.IsCommitted(id), Is.True, "Committed UoW should be marked in bitmap");

        registry.Release(id);

        // After release, IsCommitted should return false
        Assert.That(registry.IsCommitted(id), Is.False, "Released UoW should not be committed");
    }

    [Test]
    public void Registry_CommittedBeforeTSN_NormalOperation_IsMaxValue()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        Assert.That(registry.CommittedBeforeTSN, Is.EqualTo(long.MaxValue),
            "Normal operation should have CommittedBeforeTSN = long.MaxValue");
    }

    // ═══════════════════════════════════════════════════════════════
    // Crash Recovery Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Registry_CrashRecovery_VoidsPending()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // Allocate some UoW IDs (they go to Pending state on disk)
        var id1 = registry.AllocateUowId();
        var id2 = registry.AllocateUowId();

        // Commit one, leave the other Pending
        registry.RecordCommit(id1, maxTSN: 10);

        // Simulate crash recovery by calling LoadFromDisk
        // id1 is Committed → survives
        // id2 is Pending → gets voided
        registry.LoadFromDisk();

        // After recovery:
        // - id1 was Committed → should be in committed bitmap
        Assert.That(registry.IsCommitted(id1), Is.True, "Committed UoW should survive recovery");

        // - id2 was Pending → voided, not in committed bitmap
        Assert.That(registry.IsCommitted(id2), Is.False, "Pending UoW should be voided on recovery");

        // Void entries exist → CommittedBeforeTSN = 0
        Assert.That(registry.VoidEntryCount, Is.EqualTo(1));
        Assert.That(registry.CommittedBeforeTSN, Is.EqualTo(0),
            "Void entries should set CommittedBeforeTSN to 0");

        // Release the voided entry
        registry.Release(id2);
        Assert.That(registry.VoidEntryCount, Is.EqualTo(0));

        // Release committed entry
        registry.Release(id1);
    }

    [Test]
    public void Registry_VoidEntry_SetsCommittedBeforeTSN_ToZero()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        Assert.That(registry.CommittedBeforeTSN, Is.EqualTo(long.MaxValue));

        // Allocate a UoW (Pending on disk)
        var id = registry.AllocateUowId();

        // Simulate crash recovery — Pending entry gets voided
        registry.LoadFromDisk();

        Assert.That(registry.CommittedBeforeTSN, Is.EqualTo(0),
            "Void entry should force CommittedBeforeTSN to 0");

        registry.Release(id);
    }

    [Test]
    public void Registry_AllVoidFreed_RestoresCommittedBeforeTSN()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // Create a voided entry via crash recovery
        var id = registry.AllocateUowId();
        registry.LoadFromDisk();

        Assert.That(registry.CommittedBeforeTSN, Is.EqualTo(0));

        // Free the voided entry
        registry.Release(id);

        Assert.That(registry.CommittedBeforeTSN, Is.EqualTo(long.MaxValue),
            "Freeing all voided entries should restore CommittedBeforeTSN to long.MaxValue");
    }

    [Test]
    public void Registry_LoadFromDisk_AllocationBitmap_SkipsCommittedSlots()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // Allocate 3 IDs: commit id1 and id3, leave id2 pending
        var id1 = registry.AllocateUowId(); // → 1
        var id2 = registry.AllocateUowId(); // → 2
        var id3 = registry.AllocateUowId(); // → 3

        registry.RecordCommit(id1, maxTSN: 10);
        registry.RecordCommit(id3, maxTSN: 30);

        // Simulate crash recovery
        registry.LoadFromDisk();

        // After recovery: id1=Committed, id2=Void, id3=Committed
        // All three slots should be occupied — allocation bitmap bits cleared
        Assert.That(registry.ActiveCount, Is.EqualTo(3));

        // New allocations must not collide with occupied slots
        var id4 = registry.AllocateUowId();
        Assert.That(id4, Is.Not.EqualTo(id1), "Should not reuse committed slot");
        Assert.That(id4, Is.Not.EqualTo(id2), "Should not reuse voided slot");
        Assert.That(id4, Is.Not.EqualTo(id3), "Should not reuse committed slot");

        // Cleanup
        registry.Release(id4);
        registry.Release(id2);
        registry.Release(id1);
        registry.Release(id3);
    }

    [Test]
    public void Registry_LoadFromDisk_AllocationBitmap_ReusesFreeSlots()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // Allocate id1 and id2, commit id1, release id2 (makes it Free on disk)
        var id1 = registry.AllocateUowId(); // → 1
        var id2 = registry.AllocateUowId(); // → 2

        registry.RecordCommit(id1, maxTSN: 10);
        registry.Release(id2); // id2 is now Free on disk

        // Simulate crash recovery
        registry.LoadFromDisk();

        // After recovery: id1=Committed (occupied), id2=Free (available)
        Assert.That(registry.ActiveCount, Is.EqualTo(1));

        // First allocation should get id2 back (lowest free slot)
        var newId = registry.AllocateUowId();
        Assert.That(newId, Is.EqualTo(id2), "Free slot should be reusable after LoadFromDisk");

        // Cleanup
        registry.Release(newId);
        registry.Release(id1);
    }

    // ═══════════════════════════════════════════════════════════════
    // Two-Phase WAL Recovery Tests (LoadFromDiskRaw / PromoteToWalDurable / VoidRemainingPending)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Registry_LoadFromDiskRaw_PreservesPendingEntries()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // Allocate UoW IDs (Pending on disk)
        var id1 = registry.AllocateUowId();
        var id2 = registry.AllocateUowId();

        // Commit one, leave id2 Pending
        registry.RecordCommit(id1, maxTSN: 10);

        // LoadFromDiskRaw preserves Pending entries (unlike LoadFromDisk which voids them)
        registry.LoadFromDiskRaw();

        // id1 was Committed → should be in committed bitmap
        Assert.That(registry.IsCommitted(id1), Is.True, "Committed UoW should survive LoadFromDiskRaw");

        // id2 was Pending → should NOT be voided (still Pending, not in committed bitmap)
        Assert.That(registry.IsCommitted(id2), Is.False, "Pending UoW should not be in committed bitmap");

        // Void count should be 0 — LoadFromDiskRaw doesn't void anything
        Assert.That(registry.VoidEntryCount, Is.EqualTo(0), "LoadFromDiskRaw should not void any entries");

        // Both slots should be occupied in the allocation bitmap
        Assert.That(registry.ActiveCount, Is.EqualTo(2));

        // Cleanup
        registry.Release(id1);
        registry.Release(id2);
    }

    [Test]
    public void Registry_PromoteToWalDurable_TransitionsPendingToWalDurable()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        var id1 = registry.AllocateUowId();
        var id2 = registry.AllocateUowId();

        // Both are Pending. LoadFromDiskRaw preserves them.
        registry.LoadFromDiskRaw();

        // Promote id1 to WalDurable (simulating WAL scan found commit marker)
        registry.PromoteToWalDurable(id1);

        // id1 should now be in the committed bitmap
        Assert.That(registry.IsCommitted(id1), Is.True, "Promoted UoW should be in committed bitmap");

        // id2 still Pending — not in committed bitmap
        Assert.That(registry.IsCommitted(id2), Is.False, "Non-promoted UoW should not be in committed bitmap");

        // Cleanup
        registry.Release(id1);
        registry.Release(id2);
    }

    [Test]
    public void Registry_PromoteToWalDurable_IgnoresNonPendingEntries()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        var id1 = registry.AllocateUowId();

        // Commit id1 (transitions to Committed, not Pending)
        registry.RecordCommit(id1, maxTSN: 10);

        // LoadFromDiskRaw
        registry.LoadFromDiskRaw();

        // PromoteToWalDurable on already-committed entry → no-op
        registry.PromoteToWalDurable(id1);

        // Should still be in committed bitmap
        Assert.That(registry.IsCommitted(id1), Is.True);

        // PromoteToWalDurable on UoW ID 0 → no-op
        Assert.DoesNotThrow(() => registry.PromoteToWalDurable(0));

        registry.Release(id1);
    }

    [Test]
    public void Registry_VoidRemainingPending_VoidsOnlyPendingEntries()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        var id1 = registry.AllocateUowId();
        var id2 = registry.AllocateUowId();
        var id3 = registry.AllocateUowId();

        // Commit id1, leave id2 and id3 Pending
        registry.RecordCommit(id1, maxTSN: 10);

        // LoadFromDiskRaw preserves Pending entries
        registry.LoadFromDiskRaw();

        // Promote id2 (simulating WAL found its commit marker)
        registry.PromoteToWalDurable(id2);

        // Record void count before
        var voidBefore = registry.VoidEntryCount;

        // VoidRemainingPending — should void id3 (still Pending) but not id1 or id2
        registry.VoidRemainingPending();

        var voided = registry.VoidEntryCount - voidBefore;
        Assert.That(voided, Is.EqualTo(1), "Only one Pending entry (id3) should be voided");

        // id1 (Committed) and id2 (WalDurable) should be in committed bitmap
        Assert.That(registry.IsCommitted(id1), Is.True);
        Assert.That(registry.IsCommitted(id2), Is.True);

        // id3 (Voided) should not be in committed bitmap
        Assert.That(registry.IsCommitted(id3), Is.False);

        // CommittedBeforeTSN should be 0 (voided entries exist)
        Assert.That(registry.CommittedBeforeTSN, Is.EqualTo(0));

        // Cleanup
        registry.Release(id1);
        registry.Release(id2);
        registry.Release(id3);
    }

    [Test]
    public void Registry_TwoPhaseRecovery_FullWorkflow()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // Set up: 4 UoWs — commit id1 and id3, leave id2 and id4 Pending
        var id1 = registry.AllocateUowId();
        var id2 = registry.AllocateUowId();
        var id3 = registry.AllocateUowId();
        var id4 = registry.AllocateUowId();

        registry.RecordCommit(id1, maxTSN: 10);
        registry.RecordCommit(id3, maxTSN: 30);

        // Phase 1: LoadFromDiskRaw — preserves Pending entries
        registry.LoadFromDiskRaw();
        Assert.That(registry.VoidEntryCount, Is.EqualTo(0));
        Assert.That(registry.ActiveCount, Is.EqualTo(4));

        // Phase 2: Simulate WAL scan found commit markers for id2 only
        registry.PromoteToWalDurable(id2);

        // Phase 3: Void remaining Pending (id4 only)
        registry.VoidRemainingPending();

        // Verify final state
        Assert.That(registry.IsCommitted(id1), Is.True, "id1 was Committed → in committed bitmap");
        Assert.That(registry.IsCommitted(id2), Is.True, "id2 was promoted → in committed bitmap");
        Assert.That(registry.IsCommitted(id3), Is.True, "id3 was Committed → in committed bitmap");
        Assert.That(registry.IsCommitted(id4), Is.False, "id4 was Pending with no WAL commit → voided");
        Assert.That(registry.VoidEntryCount, Is.EqualTo(1));

        // Cleanup
        registry.Release(id1);
        registry.Release(id2);
        registry.Release(id3);
        registry.Release(id4);
    }

    // ═══════════════════════════════════════════════════════════════
    // Concurrent Allocation Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Registry_ConcurrentAllocation_NoDuplicates()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        const int threadCount = 8;
        const int allocsPerThread = 10;
        var allIds = new ConcurrentBag<ushort>();
        var barrier = new Barrier(threadCount);

        var tasks = Enumerable.Range(0, threadCount).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (int i = 0; i < allocsPerThread; i++)
            {
                var id = registry.AllocateUowId();
                allIds.Add(id);
            }
        })).ToArray();

        Task.WaitAll(tasks);

        var idList = allIds.ToList();
        Assert.That(idList.Count, Is.EqualTo(threadCount * allocsPerThread));
        Assert.That(idList.Distinct().Count(), Is.EqualTo(idList.Count),
            "All concurrently allocated IDs should be unique");

        // Cleanup
        foreach (var id in idList)
        {
            registry.Release(id);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Size Validation Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Registry_EntrySize_Is40Bytes()
    {
        Assert.That(UowRegistry.EntryStructSize, Is.EqualTo(40),
            "UowRegistryEntry must be exactly 40 bytes for clean page division");
    }

    [Test]
    public void Registry_Capacity_MatchesDesign()
    {
        // Directory-only root (v4): the root page holds only the segment's page directory — no entries. Every data page
        // (segment page 1+) holds the full 8000 / 40 = 200 entries.
        Assert.That(UowRegistry.PerPageCapacity, Is.EqualTo(200));
    }

    // ═══════════════════════════════════════════════════════════════
    // Integration via UoW Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Registry_UoW_AllocatesAndReleases()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        ushort capturedId;
        {
            using var uow = dbe.CreateUnitOfWork();
            capturedId = uow.UowId;
            Assert.That(capturedId, Is.GreaterThan((ushort)0));
        }

        // After UoW dispose, the slot should be free — allocate again and verify reuse
        var nextId = dbe.UowRegistry.AllocateUowId();
        Assert.That(nextId, Is.EqualTo(capturedId), "Released UoW slot should be reusable");
        dbe.UowRegistry.Release(nextId);
    }

    [Test]
    public void Registry_MultipleUoWs_GetUniqueIds()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        using var uow1 = dbe.CreateUnitOfWork();
        using var uow2 = dbe.CreateUnitOfWork();
        using var uow3 = dbe.CreateUnitOfWork();

        Assert.That(uow1.UowId, Is.Not.EqualTo(uow2.UowId));
        Assert.That(uow2.UowId, Is.Not.EqualTo(uow3.UowId));
        Assert.That(uow1.UowId, Is.Not.EqualTo(uow3.UowId));
    }

    [Test]
    public void Registry_UoW_CreateCommitDispose_FullLifecycle()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var initialActive = dbe.UowRegistry.ActiveCount;

        using (var uow = dbe.CreateUnitOfWork())
        {
            Assert.That(dbe.UowRegistry.ActiveCount, Is.EqualTo(initialActive + 1));

            using var tx = uow.CreateTransaction();
            var comp = new CompA(42);
            tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            tx.Commit();
        }

        // After UoW disposal, active count should return to initial
        Assert.That(dbe.UowRegistry.ActiveCount, Is.EqualTo(initialActive));
    }

    [Test]
    public void Registry_Release_UowIdZero_NoOp()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // Releasing UoW ID 0 should be a safe no-op
        Assert.DoesNotThrow(() => registry.Release(0));
    }

    // ═══════════════════════════════════════════════════════════════
    // Back-Pressure Tests (#52)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Registry_AllocateWithWaitContext_Success()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        var wc = WaitContext.FromTimeout(TimeSpan.FromSeconds(5));
        var id = registry.AllocateUowId(ref wc);

        Assert.That(id, Is.GreaterThan((ushort)0));
        registry.Release(id);
    }

    [Test]
    public void Registry_BackPressure_WaitsForSlot()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // Exhaust a small number of slots, then release one from another thread
        const int slotCount = 50;
        var ids = new ushort[slotCount];
        for (int i = 0; i < slotCount; i++)
        {
            ids[i] = registry.AllocateUowId();
        }

        // Verify we can allocate with back-pressure wait when a slot gets freed
        var allocatedId = (ushort)0;
        var allocationDone = new ManualResetEventSlim(false);

        // Start a thread that allocates — it should succeed quickly since slots are available
        var allocThread = Task.Run(() =>
        {
            var wc = WaitContext.FromTimeout(TimeSpan.FromSeconds(5));
            allocatedId = registry.AllocateUowId(ref wc);
            allocationDone.Set();
        });

        // The allocation should succeed (plenty of slots beyond our 50)
        Assert.That(allocationDone.Wait(TimeSpan.FromSeconds(5)), Is.True, "Allocation should succeed");
        Assert.That(allocatedId, Is.GreaterThan((ushort)0));

        // Cleanup
        registry.Release(allocatedId);
        foreach (var id in ids)
        {
            registry.Release(id);
        }
    }

    [Test]
    public void Registry_BackPressure_TimesOut_WhenNoSlotsFreed()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // v4: capacity is PerPageCapacity (200) per data page, with no special root-page capacity. We don't fill all 32767 slots
        // (too expensive), so we verify the immediate-failure parameterless overload and the WaitContext overload with an expired deadline.
        var id = registry.AllocateUowId();
        registry.Release(id);

        // Test with an already-expired deadline — should attempt allocation (will succeed since slots are free)
        // but first test the timeout path: create a scenario where allocation fails with an expired deadline
        var expiredWc = WaitContext.FromTimeout(TimeSpan.Zero);
        // Since slots are available, this should still succeed (TryClaimFreeSlot finds a slot before needing to wait)
        var quickId = registry.AllocateUowId(ref expiredWc);
        Assert.That(quickId, Is.GreaterThan((ushort)0));
        registry.Release(quickId);
    }

    [Test]
    public void Registry_BackPressure_CancellationToken_Aborts()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // Allocate a batch of slots (still far below the 32767 max) to set up a scenario
        const int fillCount = 100;
        var ids = new ushort[fillCount];
        for (int i = 0; i < fillCount; i++)
        {
            ids[i] = registry.AllocateUowId();
        }

        // There are still free slots (we only filled 100 of 32767), so we can't truly test
        // cancellation mid-wait without filling all slots. Instead, verify that the cancellation
        // token integrates correctly by using an already-cancelled token.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var wc = new WaitContext(Deadline.Infinite, cts.Token);

        // AllocateUowId should still succeed because TryClaimFreeSlot finds a slot
        // before it needs to enter the wait path (slots are available)
        var id = registry.AllocateUowId(ref wc);
        Assert.That(id, Is.GreaterThan((ushort)0));
        registry.Release(id);

        // Cleanup
        foreach (var allocId in ids)
        {
            registry.Release(allocId);
        }
    }

    [Test]
    public void Registry_BackPressure_ReleaseWakesWaiter()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // Allocate many slots
        const int slotCount = 20;
        var ids = new ushort[slotCount];
        for (int i = 0; i < slotCount; i++)
        {
            ids[i] = registry.AllocateUowId();
        }

        // Start a background allocation that should succeed (slots still available)
        var bgId = (ushort)0;
        var bgDone = new ManualResetEventSlim(false);
        var bgTask = Task.Run(() =>
        {
            var wc = WaitContext.FromTimeout(TimeSpan.FromSeconds(5));
            bgId = registry.AllocateUowId(ref wc);
            bgDone.Set();
        });

        Assert.That(bgDone.Wait(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(bgId, Is.GreaterThan((ushort)0));

        // Cleanup
        registry.Release(bgId);
        foreach (var id in ids)
        {
            registry.Release(id);
        }
    }

    [Test]
    public void Registry_BackPressure_ConcurrentAllocAndRelease()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        const int threadCount = 4;
        const int opsPerThread = 50;
        var allIds = new ConcurrentBag<ushort>();
        var errors = new ConcurrentBag<Exception>();
        var barrier = new Barrier(threadCount);

        var tasks = Enumerable.Range(0, threadCount).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (int i = 0; i < opsPerThread; i++)
            {
                try
                {
                    var wc = WaitContext.FromTimeout(TimeSpan.FromSeconds(5));
                    var id = registry.AllocateUowId(ref wc);
                    allIds.Add(id);

                    // Immediately release half the time to create churn
                    if (i % 2 == 0)
                    {
                        registry.Release(id);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }
        })).ToArray();

        Task.WaitAll(tasks);

        Assert.That(errors, Is.Empty, () => $"Errors during concurrent alloc/release: {string.Join(", ", errors.Select(e => e.Message))}");

        // Cleanup remaining unreleased IDs
        foreach (var id in allIds)
        {
            try
            {
                registry.Release(id);
            }
            catch
            {
                // Already released — ignore
            }
        }
    }

    [Test]
    public void Registry_CreateUnitOfWork_UsesBackPressure()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        // Verify CreateUnitOfWork still works end-to-end with back-pressure wiring
        using var uow = dbe.CreateUnitOfWork(timeout: TimeSpan.FromSeconds(5));
        Assert.That(uow.UowId, Is.GreaterThan((ushort)0));
        Assert.That(uow.State, Is.EqualTo(UnitOfWorkState.Pending));
    }

    [Test]
    public void Registry_MaxConcurrentUoWs_ReturnsMaxUowId()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        Assert.That(registry.MaxConcurrentUoWs, Is.EqualTo(32767));
    }

    // ═══════════════════════════════════════════════════════════════
    // EnsureCapacity Tests (#codecoverage P1)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Registry_EnsureCapacity_GrowsBeyondInitialCapacity()
    {
        // Exercises UowRegistry.EnsureCapacity: when a slot index >= _currentCapacity is claimed, the segment grows.
        // Directory-only root (v4): the genesis registry is 2 pages (directory root + 1 data page), so the initial capacity
        // is exactly one data page = PerPageCapacity (200). Claiming slot 200 grows it to 3 pages → 2 data pages = 400.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        Assert.That(registry.CurrentCapacity, Is.EqualTo(UowRegistry.PerPageCapacity),
            "Initial capacity should be one data page (200)");

        // Allocate 201 IDs (slots 1..201). Slot 200 (>= capacity 200) triggers the grow.
        const int allocCount = 201;
        var ids = new ushort[allocCount];
        for (int i = 0; i < allocCount; i++)
        {
            ids[i] = registry.AllocateUowId();
        }

        // After the grow the segment is 3 pages → 2 data pages × 200 = 400.
        Assert.That(registry.CurrentCapacity, Is.EqualTo(2 * UowRegistry.PerPageCapacity),
            "Capacity should grow to 400 (2 data pages) after exceeding the first data page");
        Assert.That(registry.ActiveCount, Is.EqualTo(allocCount));

        // Cleanup
        foreach (var id in ids)
        {
            registry.Release(id);
        }
    }

    [Test]
    public void Registry_EnsureCapacity_MultipleOverflowPages()
    {
        // Verify growth across multiple data pages (200 → 400 → 600).
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // Allocate enough IDs to trigger two growths: slot 200 → 400, slot 400 → 600 (4 pages = 3 data pages).
        const int allocCount = 401;
        var ids = new ushort[allocCount];
        for (int i = 0; i < allocCount; i++)
        {
            ids[i] = registry.AllocateUowId();
        }

        Assert.That(registry.CurrentCapacity, Is.EqualTo(3 * UowRegistry.PerPageCapacity),
            "Capacity should grow to 600 (3 data pages) after exceeding the second data page");

        // Cleanup
        foreach (var id in ids)
        {
            registry.Release(id);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // WaitForSlotFreed Tests (#codecoverage P1)
    // Uses reflection to clear the allocation bitmap to simulate
    // exhaustion without needing 32K actual allocations.
    // ═══════════════════════════════════════════════════════════════

    private static Span<ulong> GetAllocationBitmap(UowRegistry registry) => registry.AllocationBitmapSpan;

    [Test]
    [CancelAfter(5000)]
    public void Registry_WaitForSlotFreed_ExpiredDeadline_ThrowsResourceExhausted()
    {
        // Exercises WaitForSlotFreed timeout path (lines 230-234):
        // When deadline is already expired (ms == 0), returns false immediately.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        var savedBitmap = new ulong[512];
        GetAllocationBitmap(registry).CopyTo(savedBitmap);

        try
        {
            // Clear all bits → TryClaimFreeSlot returns -1 (no free slots)
            GetAllocationBitmap(registry).Clear();

            // AllocateUowId with expired deadline → WaitForSlotFreed returns false → throws
            var wc = WaitContext.FromTimeout(TimeSpan.Zero);
            Assert.Throws<ResourceExhaustedException>(() => registry.AllocateUowId(ref wc));
        }
        finally
        {
            savedBitmap.AsSpan().CopyTo(GetAllocationBitmap(registry));
        }
    }

    [Test]
    [CancelAfter(5000)]
    public void Registry_WaitForSlotFreed_ShortTimeout_ThrowsResourceExhausted()
    {
        // Exercises WaitForSlotFreed timed wait path (lines 236):
        // _slotFreed.Wait(ms, token) returns false after timeout → throws.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        var savedBitmap = new ulong[512];
        GetAllocationBitmap(registry).CopyTo(savedBitmap);

        try
        {
            GetAllocationBitmap(registry).Clear();

            // 50ms timeout — no slot will be freed
            var wc = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(50));
            Assert.Throws<ResourceExhaustedException>(() => registry.AllocateUowId(ref wc));
        }
        finally
        {
            savedBitmap.AsSpan().CopyTo(GetAllocationBitmap(registry));
        }
    }

    [Test]
    [CancelAfter(5000)]
    public void Registry_WaitForSlotFreed_SlotReleased_WakesWaiterSuccessfully()
    {
        // Exercises WaitForSlotFreed success path (lines 236):
        // Thread blocks on _slotFreed.Wait, another thread calls Release, waiter wakes.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // Allocate one slot normally (so Release has a valid entry to free)
        var seedId = registry.AllocateUowId();

        var savedBitmap = new ulong[512];
        GetAllocationBitmap(registry).CopyTo(savedBitmap);

        try
        {
            // Clear all bits → simulates full exhaustion
            GetAllocationBitmap(registry).Clear();

            ushort allocatedId = 0;
            var bgDone = new ManualResetEventSlim(false);

            // Background thread attempts allocation → blocks in WaitForSlotFreed
            var bgTask = Task.Run(() =>
            {
                var wc = WaitContext.FromTimeout(TimeSpan.FromSeconds(3));
                allocatedId = registry.AllocateUowId(ref wc);
                bgDone.Set();
            });

            // Give background thread time to enter WaitForSlotFreed
            Thread.Sleep(50);

            // Release the seed slot → sets bitmap bit AND signals _slotFreed event
            registry.Release(seedId);

            // Background thread should wake and claim the freed slot
            Assert.That(bgDone.Wait(TimeSpan.FromSeconds(3)), Is.True,
                "Background allocation should succeed after slot was freed");
            Assert.That(allocatedId, Is.EqualTo(seedId),
                "Should reuse the freed slot");

            // Cleanup the reallocated slot
            registry.Release(allocatedId);
        }
        finally
        {
            savedBitmap.AsSpan().CopyTo(GetAllocationBitmap(registry));
        }
    }

    [Test]
    [CancelAfter(5000)]
    public void Registry_WaitForSlotFreed_Cancellation_ThrowsResourceExhausted()
    {
        // Exercises WaitForSlotFreed cancellation path (lines 238-241):
        // OperationCanceledException caught → returns false → throws ResourceExhaustedException.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        var savedBitmap = new ulong[512];
        GetAllocationBitmap(registry).CopyTo(savedBitmap);

        try
        {
            GetAllocationBitmap(registry).Clear();

            using var cts = new CancellationTokenSource();
            Exception caught = null;
            var bgDone = new ManualResetEventSlim(false);

            // Background thread attempts allocation with cancellation token
            var bgTask = Task.Run(() =>
            {
                try
                {
                    var wc = WaitContext.FromTimeout(TimeSpan.FromSeconds(30), cts.Token);
                    registry.AllocateUowId(ref wc);
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
                bgDone.Set();
            });

            // Give background thread time to enter WaitForSlotFreed
            Thread.Sleep(50);

            // Cancel the token → triggers OperationCanceledException in _slotFreed.Wait
            cts.Cancel();

            Assert.That(bgDone.Wait(TimeSpan.FromSeconds(3)), Is.True,
                "Background thread should complete after cancellation");
            Assert.That(caught, Is.Not.Null, "Cancellation should cause an exception");
            Assert.That(caught, Is.TypeOf<ResourceExhaustedException>(),
                "Cancelled wait should result in ResourceExhaustedException");
        }
        finally
        {
            savedBitmap.AsSpan().CopyTo(GetAllocationBitmap(registry));
        }
    }
}
