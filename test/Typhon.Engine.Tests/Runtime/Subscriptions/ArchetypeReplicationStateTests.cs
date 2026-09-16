using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// #954 — the per-archetype owner: that renting and registering are one act, that removing and returning are one act, and that the pair is torn down in the
/// only order that is safe.
/// </summary>
/// <remarks>
/// Each property here is a leak or a dangling pointer that neither the pool nor the directory can prevent alone, which is the whole reason the owner exists.
/// The attach-failure cases matter most: a block rented but never registered is unreachable and unreturnable, and the pool cannot detect it.
/// </remarks>
[TestFixture]
unsafe class ArchetypeReplicationStateTests
{
    private const int SlotCount = 21;
    private const long AmpleBudget = 16L * 1024 * 1024;

    private ResourceRegistry _registry;
    private MemoryAllocator _allocator;
    private NetIdAllocator _netIds;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "ArchetypeReplicationStateTests" });
        _allocator = new MemoryAllocator(_registry, new MemoryAllocatorOptions { Name = "OwnerTestAllocator" });

        // One allocator shared by every state the fixture builds, which is the production shape: netIds are global, so a per-archetype allocator would make
        // the same number mean different entities to different readers and break the wire's encode-once property.
        _netIds = new NetIdAllocator("NetIds", _registry.Runtime);
    }

    [TearDown]
    public void TearDown()
    {
        _netIds?.Dispose();
        _allocator?.Dispose();
        _registry?.Dispose();
    }

    private ArchetypeReplicationState NewState(long budgetBytes = AmpleBudget, string id = "Creature") =>
        new(id, _registry.Runtime, _allocator, new ReplicationBlockLayout(SlotCount), new SubscriptionsOptions { StatePoolBudgetBytes = budgetBytes },
            _netIds);

    [Test]
    public void AFreshStateCommitsNothing()
    {
        using var state = NewState();

        Assert.Multiple(() =>
        {
            Assert.That(state.WatchedClusterCount, Is.Zero);
            Assert.That(state.Pool.CommittedBytes, Is.Zero);
            Assert.That(_allocator.PinnedLiveBlocks, Is.Zero);
        });
    }

    [Test]
    public void AttachingRentsAndRegistersInOneAct()
    {
        using var state = NewState();

        Assert.That(state.TryAttachBlock(11, out var block), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(block != null);
            Assert.That(block->ChunkId, Is.EqualTo(11));
            Assert.That(state.WatchedClusterCount, Is.EqualTo(1));
            Assert.That(state.Directory.TryGetBlock(11, out var found), Is.True);
            Assert.That((nint)found, Is.EqualTo((nint)block));
        });
    }

    /// <summary>
    /// The leak the owner exists to prevent: if registration fails after the rent, the block must go back to the pool. A block that reaches no directory is
    /// unreachable and unreturnable, and the pool has no way to notice.
    /// </summary>
    [Test]
    public void AttachingATakenClusterReturnsTheRentedBlockRatherThanLeakingIt()
    {
        using var state = NewState();
        Assert.That(state.TryAttachBlock(3, out _), Is.True);

        var freeBefore = state.Pool.FreeBlockCount;
        var committedBefore = state.Pool.CommittedBytes;

        Assert.That(state.TryAttachBlock(3, out var second), Is.False, "the cluster already has a block");

        Assert.Multiple(() =>
        {
            Assert.That(second == null);
            Assert.That(state.Pool.FreeBlockCount, Is.EqualTo(freeBefore), "the rented block must have gone straight back to the free list");
            Assert.That(state.Pool.CommittedBytes, Is.EqualTo(committedBefore), "and nothing more should have been committed");
            Assert.That(state.WatchedClusterCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void AttachingReportsFailureWhenTheBudgetBinds()
    {
        using var state = NewState(budgetBytes: 1, id: "Tiny");

        Assert.Multiple(() =>
        {
            Assert.That(state.TryAttachBlock(0, out var block), Is.False, "a budget below one slab can never attach");
            Assert.That(block == null);
            Assert.That(state.WatchedClusterCount, Is.Zero);
            Assert.That(_allocator.PinnedLiveBlocks, Is.Zero);
        });
    }

    [Test]
    public void ReleasingRemovesAndReturnsInOneAct()
    {
        using var state = NewState();
        Assert.That(state.TryAttachBlock(9, out _), Is.True);
        var freeAfterAttach = state.Pool.FreeBlockCount;

        Assert.That(state.TryReleaseBlock(9), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(state.WatchedClusterCount, Is.Zero);
            Assert.That(state.Directory.TryGetBlock(9, out _), Is.False);
            Assert.That(state.Pool.FreeBlockCount, Is.EqualTo(freeAfterAttach + 1), "the block must be back on the free list");
        });
    }

    [Test]
    public void ReleasingAClusterThatHasNoBlockIsNotAnError()
    {
        using var state = NewState();

        Assert.Multiple(() =>
        {
            Assert.That(state.TryReleaseBlock(404), Is.False);
            Assert.That(state.TryAttachBlock(404, out _), Is.True, "and leaves the cluster attachable");
            Assert.That(state.TryReleaseBlock(404), Is.True);
            Assert.That(state.TryReleaseBlock(404), Is.False, "releasing twice finds nothing the second time");
        });
    }

    /// <summary>
    /// The drain-and-reissue sequence end to end: release a cluster, then attach the recycled chunk id to a different cluster. The reissued id must resolve
    /// to the new block, and the old block must have been recycled rather than committed anew.
    /// </summary>
    [Test]
    public void ARecycledChunkIdAttachesCleanlyAfterRelease()
    {
        using var state = NewState();
        Assert.That(state.TryAttachBlock(7, out var original), Is.True);
        var committedAfterFirst = state.Pool.CommittedBytes;

        Assert.That(state.TryReleaseBlock(7), Is.True);
        Assert.That(state.TryAttachBlock(7, out var reissued), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That((nint)reissued, Is.EqualTo((nint)original), "the freed block should be recycled, not a fresh one");
            Assert.That(reissued->ChunkId, Is.EqualTo(7), "and must carry the reissued id, not the drained cluster's state");
            Assert.That(reissued->WatchedMask, Is.Zero, "a recycled block must not inherit a watched mask");
            Assert.That(state.Pool.CommittedBytes, Is.EqualTo(committedAfterFirst), "recycling must not commit more memory");
        });
    }

    /// <summary>
    /// Disposal order is the reason this type exists. The directory's values are pointers into the pool's slabs, so the pool must outlive it — and every
    /// slab must still be freed.
    /// </summary>
    [Test]
    public void DisposingTearsDownTheDirectoryBeforeThePoolAndFreesEverySlab()
    {
        var state = NewState();
        for (var chunkId = 0; chunkId < 40; chunkId++)
        {
            Assert.That(state.TryAttachBlock(chunkId, out _), Is.True);
        }

        Assert.That(_allocator.PinnedLiveBlocks, Is.GreaterThan(0));

        Assert.DoesNotThrow(() => state.Dispose());

        Assert.Multiple(() =>
        {
            Assert.That(_allocator.PinnedLiveBlocks, Is.Zero, "native slabs must be freed with the owner");
            Assert.That(_allocator.PinnedBytes, Is.Zero);
            Assert.Throws<System.ObjectDisposedException>(() => state.TryAttachBlock(1, out _));
            Assert.Throws<System.ObjectDisposedException>(() => state.TryReleaseBlock(1));
            Assert.DoesNotThrow(() => state.Dispose());
        });
    }

    /// <summary>
    /// The directory throws on a negative chunk id, so validating after the rent would strand a block: unreachable because no directory names it, and
    /// unreturnable because the caller never received it. Validation has to precede the rent.
    /// </summary>
    [Test]
    public void AttachingWithANegativeChunkIdThrowsWithoutRentingAnything()
    {
        using var state = NewState();
        Assert.That(state.TryAttachBlock(0, out _), Is.True, "commit a slab first, so the counters below are meaningful");

        var freeBefore = state.Pool.FreeBlockCount;
        var committedBefore = state.Pool.CommittedBytes;

        Assert.Throws<System.ArgumentOutOfRangeException>(() => state.TryAttachBlock(-1, out _));

        Assert.Multiple(() =>
        {
            Assert.That(state.Pool.FreeBlockCount, Is.EqualTo(freeBefore), "a rejected attach must not have taken a block off the free list");
            Assert.That(state.Pool.CommittedBytes, Is.EqualTo(committedBefore));
            Assert.That(state.WatchedClusterCount, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// Reaching past the paired operations to <see cref="ArchetypeReplicationState.Pool"/> is legal — the projection passes need it — so the sequence it
    /// enables has to fail loudly rather than corrupt the free list. Returning a block directly and then releasing its cluster is a double return.
    /// </summary>
    [Test]
    public void ReturningDirectlyThenReleasingIsRejectedRatherThanCorruptingTheFreeList()
    {
        using var state = NewState();
        Assert.That(state.TryAttachBlock(5, out var block), Is.True);

        // A caller bypasses the pairing and hands the block back itself, leaving the directory still naming it.
        state.Pool.Return(block);

        Assert.Throws<System.ArgumentException>(() => state.TryReleaseBlock(5), "the second return must be refused");

        // The free list must still be a list: two rents, two different blocks.
        Assert.That(state.Pool.TryRent(out var first), Is.True);
        Assert.That(state.Pool.TryRent(out var second), Is.True);
        Assert.That((nint)first, Is.Not.EqualTo((nint)second), "two rents returned the same block — the free list became a self-cycle");
    }

    [Test]
    public void AConstructorWithoutAParentIsRejected()
    {
        Assert.Throws<System.ArgumentNullException>(() =>
            _ = new ArchetypeReplicationState("NoParent", null, _allocator, new ReplicationBlockLayout(SlotCount), new SubscriptionsOptions(), _netIds));
    }

    /// <summary>
    /// The directory was previously in no snapshot and no budget, while growing with untrusted client input. This node reports it; the pool's slabs must stay
    /// excluded because the pool is a child, and the SHARED identity allocator must stay excluded because it is not this node's to report.
    /// </summary>
    [Test]
    public void ReportedMemoryCoversTheDirectoryButNotTheSlabsOrTheSharedIdentities()
    {
        using var state = NewState();
        var atRest = state.EstimatedMemorySize;

        for (var chunkId = 0; chunkId < 400; chunkId++)
        {
            Assert.That(state.TryAttachBlock(chunkId, out _), Is.True);
        }

        var afterGrowth = state.EstimatedMemorySize;

        // Grow the identity space hard, with no blocks attached. Counting it here is what N replicated archetypes would each do to the SAME allocator, so the
        // graph would report one shared array as many — the double-count IMemoryResource exists to forbid.
        for (var i = 0; i < 4_000; i++)
        {
            _netIds.Allocate();
        }

        var afterIdentityGrowth = state.EstimatedMemorySize;

        Assert.Multiple(() =>
        {
            Assert.That(atRest, Is.GreaterThan(0), "the structures exist from construction, so they are never free");
            Assert.That(afterGrowth, Is.GreaterThan(atRest), "the reported figure must follow the watched set — that is the whole point of reporting it");
            Assert.That(afterIdentityGrowth, Is.EqualTo(afterGrowth),
                "the shared allocator's bytes are reported by the allocator, exactly once, not per archetype");
            Assert.That(_netIds.EstimatedMemorySize, Is.GreaterThan(0), "and they are reported — excluding them here must not make them vanish");
            Assert.That(state.Pool.CommittedBytes, Is.GreaterThan(afterGrowth * 4L),
                "slab bytes dwarf the side structures and must not be counted here; the pool reports them as a child");
        });
    }

    [Test]
    public void IdentitiesComeFromTheSharedAllocator()
    {
        using var state = NewState();
        using var other = NewState(id: "Player");

        var first = state.NetIds.Allocate();
        state.NetIds.Release(first);
        state.NetIds.DrainQuarantine();
        var reused = state.NetIds.Allocate();

        Assert.Multiple(() =>
        {
            Assert.That(state.NetIds, Is.SameAs(other.NetIds), "two replicated archetypes share one identity space — a netId is global");
            Assert.That(first, Is.Not.EqualTo(NetIdAllocator.NoNetId));
            Assert.That(reused, Is.EqualTo(first), "identities recycle across the tick boundary, keeping the space dense");
            Assert.That(state.NetIds.GenerationOf(reused), Is.Not.Zero, "and a reused identity is observably a different entity");
        });
    }
}
