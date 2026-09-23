using System.Collections.Generic;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// #954 — the replication state block pool: budget behaviour, recycling, alignment and native-memory lifetime.
/// </summary>
/// <remarks>
/// <para>
/// The pool is sized by what the application replicates rather than by the database, so the properties worth pinning are the ones that keep an
/// untrusted load from becoming a memory-exhaustion path: it commits nothing until asked, never commits past its budget, and reports exhaustion by
/// returning <see langword="false"/> rather than by throwing on a tick-path call.
/// </para>
/// <para>
/// These run against a real <see cref="ResourceRegistry"/> and <see cref="MemoryAllocator"/> rather than a fake, because the thing most worth testing —
/// that the slabs are actually freed — is only observable through the allocator's own pinned-block counters.
/// </para>
/// </remarks>
[TestFixture]
unsafe class ReplicationBlockPoolTests
{
    private const int SlotCount = 21;

    /// <summary>A budget large enough that the tests that are not about exhaustion never hit it.</summary>
    private const long AmpleBudget = 16L * 1024 * 1024;

    private ResourceRegistry _registry;
    private MemoryAllocator _allocator;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "ReplicationBlockPoolTests" });
        _allocator = new MemoryAllocator(_registry, new MemoryAllocatorOptions { Name = "PoolTestAllocator" });
    }

    [TearDown]
    public void TearDown()
    {
        _allocator?.Dispose();
        _registry?.Dispose();
    }

    private ReplicationBlockPool NewPool(long budgetBytes, string id = "Pool") =>
        new(id, _registry.Runtime, _allocator, new ReplicationBlockLayout(SlotCount), new SubscriptionsOptions { StatePoolBudgetBytes = budgetBytes });

    [Test]
    public void APoolCommitsNothingUntilItIsAskedForABlock()
    {
        using var pool = NewPool(AmpleBudget);

        Assert.Multiple(() =>
        {
            Assert.That(pool.CommittedBytes, Is.Zero, "the budget is a ceiling, not a reservation");
            Assert.That(pool.BlockCount, Is.Zero);
            Assert.That(pool.FreeBlockCount, Is.Zero);
            Assert.That(_allocator.PinnedLiveBlocks, Is.Zero, "no slab should have been taken");
        });
    }

    [Test]
    public void TheFirstRentTakesExactlyOneSlab()
    {
        using var pool = NewPool(AmpleBudget);

        Assert.That(pool.TryRent(out var block), Is.True);
        Assert.That(block != null);

        Assert.Multiple(() =>
        {
            Assert.That(_allocator.PinnedLiveBlocks, Is.EqualTo(1), "one slab, not one allocation per block");
            Assert.That(pool.CommittedBytes, Is.GreaterThan(0));
            Assert.That(pool.BlockCount, Is.GreaterThan(1), "a slab is carved into many blocks");
            Assert.That(pool.FreeBlockCount, Is.EqualTo(pool.BlockCount - 1), "all but the rented one stay free");
        });
    }

    /// <summary>
    /// The slab floor is a minimum. Rounding the block count down instead of up yields a slab just under it — 31 blocks of 2 112 B is 65 472 B against a
    /// 64 KiB floor — which is a quiet divergence from the design rather than a visible failure.
    /// </summary>
    [Test]
    public void ASlabIsAtLeastTheDocumentedFloor()
    {
        using var pool = NewPool(AmpleBudget);
        Assert.That(pool.TryRent(out _), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(pool.CommittedBytes, Is.GreaterThanOrEqualTo(64 * 1024), "a slab must reach the 64 KiB floor, not merely approach it");
            Assert.That(pool.CommittedBytes % pool.BlockStride, Is.Zero, "a slab holds whole blocks");
        });
    }

    [Test]
    public void RentedBlocksAreDistinctAndCacheLineAligned()
    {
        using var pool = NewPool(AmpleBudget);
        var seen = new HashSet<nint>();

        for (var i = 0; i < 64; i++)
        {
            Assert.That(pool.TryRent(out var block), Is.True, $"rent {i} should succeed within an ample budget");

            var address = (nint)block;
            // (long) rather than nint: NUnit's Is.Zero is typed, and an IntPtr zero does not equal an Int32 zero.
            Assert.That((long)address % 64, Is.Zero, $"block {i} must start on a cache line so its hot region is aligned");
            Assert.That(seen.Add(address), Is.True, $"block {i} was handed out twice");
        }
    }

    [Test]
    public void ThePoolNeverCommitsPastItsBudgetAndReportsExhaustionWithoutThrowing()
    {
        // Rent one block against an ample budget purely to learn the slab size, then build a pool bounded to two slabs.
        long slabBytes;
        using (var probe = NewPool(AmpleBudget, "Probe"))
        {
            Assert.That(probe.TryRent(out _), Is.True);
            slabBytes = probe.CommittedBytes;
        }

        var budget = (slabBytes * 2) + (slabBytes / 2); // two whole slabs, and not quite a third
        using var pool = NewPool(budget, "Bounded");

        var rented = 0;
        while (pool.TryRent(out _))
        {
            rented++;
            Assert.That(pool.CommittedBytes, Is.LessThanOrEqualTo(pool.BudgetBytes), "the budget must hold at every step, not just at the end");
            Assert.That(rented, Is.LessThan(100_000), "the pool should have reported exhaustion long before this");
        }

        Assert.Multiple(() =>
        {
            Assert.That(rented, Is.GreaterThan(0));
            Assert.That(pool.CommittedBytes, Is.EqualTo(slabBytes * 2), "it should stop at the last slab that fits whole");
            Assert.That(pool.CommittedBytes, Is.LessThanOrEqualTo(budget));
            Assert.That(pool.FreeBlockCount, Is.Zero, "exhaustion means the free list is empty, not that blocks were lost");
            Assert.That(pool.TryRent(out var none), Is.False, "an exhausted pool keeps reporting false");
            Assert.That(none == null);
        });
    }

    [Test]
    public void AReturnedBlockIsReusedWithoutCommittingMoreMemory()
    {
        long slabBytes;
        using (var probe = NewPool(AmpleBudget, "Probe"))
        {
            Assert.That(probe.TryRent(out _), Is.True);
            slabBytes = probe.CommittedBytes;
        }

        using var pool = NewPool(slabBytes, "SingleSlab");

        var blocks = new List<nint>();
        while (pool.TryRent(out var block))
        {
            blocks.Add((nint)block);
        }

        var committedWhenFull = pool.CommittedBytes;
        Assert.That(blocks, Is.Not.Empty);

        pool.Return((ReplicationBlockHeader*)blocks[0]);
        Assert.That(pool.FreeBlockCount, Is.EqualTo(1));

        Assert.That(pool.TryRent(out var reused), Is.True, "a returned block must be rentable again");
        Assert.Multiple(() =>
        {
            Assert.That((nint)reused, Is.EqualTo(blocks[0]), "the free list is LIFO, so the block just returned comes back");
            Assert.That(pool.CommittedBytes, Is.EqualTo(committedWhenFull), "reuse must not commit more memory");
        });
    }

    /// <summary>
    /// Returning the same block twice would thread its <c>NextFree</c> to itself: every later rent then hands out that one block forever while the free
    /// count climbs. Nothing downstream could detect it, so the pool has to.
    /// </summary>
    [Test]
    public void ReturningTheSameBlockTwiceIsRejected()
    {
        using var pool = NewPool(AmpleBudget);
        Assert.That(pool.TryRent(out var block), Is.True);

        pool.Return(block);

        Assert.Multiple(() =>
        {
            Assert.Throws<System.ArgumentException>(() => pool.Return(block));
            Assert.That(pool.FreeBlockCount, Is.EqualTo(pool.BlockCount), "the rejected return must not have touched the count");
        });
    }

    [Test]
    public void RentingClearsTheHeaderSoAReusedBlockCarriesNoStaleState()
    {
        using var pool = NewPool(AmpleBudget);

        Assert.That(pool.TryRent(out var block), Is.True);
        block->WatchedMask = ulong.MaxValue;
        block->ChunkId = 4242;
        block->LastWatchedTick = 99;
        pool.Return(block);

        Assert.That(pool.TryRent(out var reused), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(reused->WatchedMask, Is.Zero, "a stale watched mask would make the projection pass read slots that are not watched");
            Assert.That(reused->ChunkId, Is.EqualTo(ReplicationBlockPool.UnassignedChunkId),
                "a stale chunk id is the recycled-id hazard this clear exists to close");
            Assert.That(reused->LastWatchedTick, Is.Zero);
        });
    }

    /// <summary>
    /// A budget too small for a single slab is legal configuration, not an error: the subsystem is disabled rather than broken, which is the right outcome
    /// for an operator who sized it to nothing.
    /// </summary>
    [Test]
    public void ABudgetTooSmallForOneSlabNeverRentsAndNeverThrows()
    {
        using var pool = NewPool(budgetBytes: 1);

        Assert.Multiple(() =>
        {
            Assert.That(pool.TryRent(out var block), Is.False);
            Assert.That(block == null);
            Assert.That(pool.CommittedBytes, Is.Zero);
            Assert.That(_allocator.PinnedLiveBlocks, Is.Zero);
        });
    }

    /// <summary>
    /// <c>IMemoryResource.EstimatedMemorySize</c> excludes children by contract, and the slabs are children. Counting them here would double-count them in
    /// every resource-graph snapshot.
    /// </summary>
    [Test]
    public void EstimatedMemorySizeCountsBookkeepingNotSlabBytes()
    {
        using var pool = NewPool(AmpleBudget);

        for (var i = 0; i < 64; i++)
        {
            Assert.That(pool.TryRent(out _), Is.True);
        }

        Assert.Multiple(() =>
        {
            Assert.That(pool.CommittedBytes, Is.GreaterThan(64 * 1024), "the test should have committed real memory");
            Assert.That(pool.EstimatedMemorySize, Is.LessThan(1024), "the slabs are children and are accounted separately");
        });
    }

    [Test]
    public void DisposingThePoolFreesEverySlab()
    {
        var pool = NewPool(AmpleBudget);
        for (var i = 0; i < 64; i++)
        {
            Assert.That(pool.TryRent(out _), Is.True);
        }

        Assert.That(_allocator.PinnedLiveBlocks, Is.GreaterThan(0));
        Assert.That(_allocator.PinnedBytes, Is.GreaterThan(0));

        pool.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(_allocator.PinnedLiveBlocks, Is.Zero, "every slab must be freed with the pool — this is native memory, not GC memory");
            Assert.That(_allocator.PinnedBytes, Is.Zero);
        });
    }

    /// <summary>
    /// A disposed pool that still reported its committed bytes would pass its own budget check on the next rent and commit a slab under a node whose
    /// children have already been cleared — an outright leak, from a pool the caller believes is gone.
    /// </summary>
    [Test]
    public void ADisposedPoolCommitsNothingMoreAndRentsNothing()
    {
        var pool = NewPool(AmpleBudget);
        Assert.That(pool.TryRent(out _), Is.True);

        pool.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(pool.CommittedBytes, Is.Zero, "a disposed pool holds no memory, so it must not claim to");
            Assert.That(pool.BlockCount, Is.Zero);
            Assert.That(pool.FreeBlockCount, Is.Zero);
            Assert.That(pool.TryRent(out var block), Is.False, "renting from a disposed pool must fail rather than allocate");
            Assert.That(block == null);
            Assert.That(_allocator.PinnedLiveBlocks, Is.Zero, "and must not have taken a fresh slab");
        });
    }

    /// <summary>
    /// Slabs are taken with <c>zeroed: false</c> and only the HEADER is cleared on rent — entries initialise lazily through the per-entry <c>EntityId</c>
    /// mismatch path. Nothing else pins that: flipping the allocator call to <c>zeroed: true</c> would break no other test here, while silently touching
    /// every entry byte of every slab.
    /// </summary>
    [Test]
    public void RentDoesNotZeroTheEntryRegion()
    {
        using var pool = NewPool(AmpleBudget);

        Assert.That(pool.TryRent(out var block), Is.True);
        var mark = (byte*)block + pool.Layout.HotOffset;
        *mark = 0xAB;

        pool.Return(block);
        Assert.That(pool.TryRent(out var again), Is.True);
        Assert.That((nint)again, Is.EqualTo((nint)block), "LIFO should hand back the same block, or this test proves nothing");

        Assert.That(*((byte*)again + pool.Layout.HotOffset), Is.EqualTo((byte)0xAB),
            "the entry region must survive a rent — clearing it would make the lazy-initialisation path dead weight");
    }

    [Test]
    public void ReturningNullIsRejected()
    {
        using var pool = NewPool(AmpleBudget);

        Assert.Throws<System.ArgumentNullException>(() => pool.Return(null));
    }

    /// <summary>An archetype declaring owner fields carves a wider block; the pool must stride by it, not by the owner-less size.</summary>
    [Test]
    public void APoolCarvesOwnerEntriesWhenTheArchetypeDeclaresThem()
    {
        var layout = new ReplicationBlockLayout(SlotCount, ownerEntrySize: 8);
        using var pool = new ReplicationBlockPool("Owned", _registry.Runtime, _allocator, layout,
            new SubscriptionsOptions { StatePoolBudgetBytes = AmpleBudget });

        Assert.That(pool.TryRent(out var first), Is.True);
        Assert.That(pool.TryRent(out var second), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(pool.BlockStride, Is.EqualTo(layout.BlockStride));
            Assert.That(pool.BlockStride, Is.GreaterThanOrEqualTo(layout.BlockSize));
            Assert.That(layout.BlockSize, Is.EqualTo(new ReplicationBlockLayout(SlotCount).BlockSize + (SlotCount * 8)));
            Assert.That((long)((nint)second - (nint)first) % pool.BlockStride, Is.Zero, "consecutive blocks must sit a whole stride apart");
        });
    }

    [Test]
    public void ALayoutWithNoSlotsIsRejected()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => _ = new ReplicationBlockLayout(0));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => _ = new ReplicationBlockLayout(SlotCount, ownerEntrySize: -1));
        });
    }

    [Test]
    public void ANegativeBudgetIsRejected()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            _ = new ReplicationBlockPool("Bad", _registry.Runtime, _allocator, new ReplicationBlockLayout(SlotCount),
                new SubscriptionsOptions { StatePoolBudgetBytes = -1 }));
    }

    /// <summary>
    /// The pool's double-return guard must not live in a field somebody else owns. <c>ReplicationDirectory.TryRemove</c> resets a block's <c>ChunkId</c>,
    /// so while the guard was stored there, a directory removal silently disarmed it and the next return corrupted the free list. The guard now lives in
    /// <c>PoolState</c>, which only the pool writes.
    /// </summary>
    [Test]
    public void ResettingTheChunkIdDoesNotDisarmTheDoubleReturnGuard()
    {
        using var pool = NewPool(AmpleBudget);
        Assert.That(pool.TryRent(out var block), Is.True);
        pool.Return(block);

        // Exactly what the directory does when an entry is removed.
        block->ChunkId = ReplicationBlockPool.UnassignedChunkId;

        Assert.Throws<System.ArgumentException>(() => pool.Return(block), "the guard must survive another owner writing ChunkId");

        // And the free list must still be a list, not a self-cycle.
        Assert.That(pool.TryRent(out var first), Is.True);
        Assert.That(pool.TryRent(out var second), Is.True);
        Assert.That((nint)first, Is.Not.EqualTo((nint)second), "two rents returned the same block — the free list is cyclic");
    }

#if DEBUG
    /// <summary>
    /// The concurrency guard has to be shown capable of firing, or it is a check nobody has ever seen reject anything. Driven directly and deterministically
    /// rather than by racing two threads, which would be flaky in exactly the way a guard test must not be.
    /// </summary>
    /// <remarks>
    /// <c>DEBUG</c> only: <c>Enter</c> and <c>Exit</c> are <c>[Conditional("DEBUG")]</c>, so in Release both calls vanish, nothing throws, and this test
    /// would fail for a reason that has nothing to do with the guard. The merge gate runs Release, which is why it is compiled out rather than skipped.
    /// </remarks>
    [Test]
    public void TheConcurrencyGuardRejectsASecondCallerAndClearsOnExit()
    {
        var affinity = default(ReplicationThreadAffinity);

        affinity.Enter(nameof(ReplicationBlockPool), "first");

        Assert.Throws<System.InvalidOperationException>(() => affinity.Enter(nameof(ReplicationBlockPool), "second"),
            "a second caller entering while the first is inside must be rejected — that is the whole point of the guard");

        affinity.Exit();

        Assert.DoesNotThrow(() => affinity.Enter(nameof(ReplicationBlockPool), "third"),
            "after Exit the guard must be clear again; a guard that stayed latched would report a false positive on every later call");
        affinity.Exit();
    }

    /// <summary>
    /// The guard must NOT reject a caller merely for being on a different thread. The cluster-drain path alternates between the driver thread and a pool
    /// worker from tick to tick, so thread-identity rejection would fail correct code — and would do it from inside a drain loop that cannot absorb a throw.
    /// </summary>
    [Test]
    public void TheConcurrencyGuardAllowsSequentialCallsFromDifferentThreads()
    {
        var pool = NewPool(AmpleBudget, "CrossThread");
        try
        {
            Assert.That(pool.TryRent(out var first), Is.True, "rented on the test thread");

            System.Exception fromOtherThread = null;
            var other = new System.Threading.Thread(() =>
            {
                try
                {
                    pool.Return(first);
                    Assert.That(pool.TryRent(out _), Is.True);
                }
                catch (System.Exception ex)
                {
                    fromOtherThread = ex;
                }
            });
            other.Start();
            other.Join();

            Assert.That(fromOtherThread, Is.Null,
                $"a later call from another thread is legal — the structures are serialized by phase barriers, not by thread identity. Got: {fromOtherThread}");
        }
        finally
        {
            pool.Dispose();
        }
    }
#endif

    [Test]
    public void DisposingTwiceIsHarmless()
    {
        var pool = NewPool(AmpleBudget);
        Assert.That(pool.TryRent(out _), Is.True);

        pool.Dispose();
        Assert.DoesNotThrow(() => pool.Dispose());
        Assert.That(_allocator.PinnedLiveBlocks, Is.Zero);
    }
}
