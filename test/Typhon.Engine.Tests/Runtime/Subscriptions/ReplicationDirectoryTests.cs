using System.Collections.Generic;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// #954 — the replication directory: chunk id to state block, sized by the blocks it holds, never by the chunk-id space.
/// </summary>
/// <remarks>
/// The property worth defending here is SUB-13: the directory must cost what is <i>watched</i>, never what the database holds. A flat array indexed by chunk
/// id would pass every functional test below and still be the wrong structure, so <see cref="SparseChunkIdsCostOnlyWhatIsWatched"/> is the one that actually
/// pins the design decision. The rest guard the recycled-chunk-id hazard, which is the failure this directory is most likely to be wrong about.
/// </remarks>
[TestFixture]
unsafe class ReplicationDirectoryTests
{
    private const int SlotCount = 21;
    private const long AmpleBudget = 64L * 1024 * 1024;

    private ResourceRegistry _registry;
    private MemoryAllocator _allocator;
    private ReplicationBlockPool _pool;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "ReplicationDirectoryTests" });
        _allocator = new MemoryAllocator(_registry, new MemoryAllocatorOptions { Name = "DirTestAllocator" });
        _pool = new ReplicationBlockPool("Pool", _registry.Runtime, _allocator, new ReplicationBlockLayout(SlotCount),
            new SubscriptionsOptions { StatePoolBudgetBytes = AmpleBudget });
    }

    [TearDown]
    public void TearDown()
    {
        // The directory does not own its blocks and cannot outlive the pool — every test disposes its directory first, and so does this.
        _pool?.Dispose();
        _allocator?.Dispose();
        _registry?.Dispose();
    }

    private ReplicationBlockHeader* Rent()
    {
        Assert.That(_pool.TryRent(out var block), Is.True, "the test budget should always satisfy a rent");
        return block;
    }

    [Test]
    public void AnEmptyDirectoryFindsNothing()
    {
        using var dir = new ReplicationDirectory();

        Assert.Multiple(() =>
        {
            Assert.That(dir.Count, Is.Zero);
            Assert.That(dir.TryGetBlock(0, out var block), Is.False, "the common case in a large archetype is a miss");
            Assert.That(block == null);
        });
    }

    [Test]
    public void AnAddedBlockIsFoundAgainAndCarriesItsChunkId()
    {
        using var dir = new ReplicationDirectory();
        var block = Rent();

        Assert.That(dir.TryAdd(42, block), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(dir.Count, Is.EqualTo(1));
            Assert.That(dir.TryGetBlock(42, out var found), Is.True);
            Assert.That((nint)found, Is.EqualTo((nint)block), "the directory must hand back the same block");
            Assert.That(block->ChunkId, Is.EqualTo(42), "add stamps the header, so a block knows which cluster it describes");
        });
    }

    [Test]
    public void AddingATakenChunkIdIsRefusedAndLeavesTheFirstBlockInPlace()
    {
        using var dir = new ReplicationDirectory();
        var first = Rent();
        var second = Rent();

        Assert.That(dir.TryAdd(7, first), Is.True);
        Assert.That(dir.TryAdd(7, second), Is.False, "a second block for one cluster would silently orphan the first");

        Assert.Multiple(() =>
        {
            Assert.That(dir.Count, Is.EqualTo(1));
            Assert.That(dir.TryGetBlock(7, out var found), Is.True);
            Assert.That((nint)found, Is.EqualTo((nint)first));
            Assert.That(second->ChunkId, Is.EqualTo(ReplicationBlockPool.UnassignedChunkId), "a refused add must not stamp the rejected block");
        });
    }

    /// <summary>
    /// A block describes exactly one cluster. Registering one under a second id leaves the first entry naming a block that denies being its, and once the
    /// drain hook returns both entries' blocks to the pool it returns the same block twice — which corrupts the free list.
    /// </summary>
    [Test]
    public void ABlockCannotBeRegisteredUnderTwoChunkIds()
    {
        using var dir = new ReplicationDirectory();
        var block = Rent();

        Assert.That(dir.TryAdd(1, block), Is.True);

        Assert.Multiple(() =>
        {
            Assert.Throws<System.ArgumentException>(() => dir.TryAdd(2, block));
            Assert.That(dir.Count, Is.EqualTo(1), "the rejected add must leave the directory untouched");
            Assert.That(block->ChunkId, Is.EqualTo(1), "and must not re-stamp the block");
            Assert.That(dir.TryGetBlock(2, out _), Is.False);
        });
    }

    [Test]
    public void RemovingHandsTheBlockBackAndLeavesNoEntry()
    {
        using var dir = new ReplicationDirectory();
        var block = Rent();
        dir.TryAdd(13, block);

        Assert.That(dir.TryRemove(13, out var removed), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That((nint)removed, Is.EqualTo((nint)block), "the caller needs the block back to return it to the pool");
            Assert.That(removed->ChunkId, Is.EqualTo(ReplicationBlockPool.UnassignedChunkId), "a removed block must not still claim its old cluster");
            Assert.That(dir.Count, Is.Zero);
            Assert.That(dir.TryGetBlock(13, out _), Is.False);
            Assert.That(dir.TryRemove(13, out var again), Is.False, "removing twice is not an error, but finds nothing");
            Assert.That(again == null);
        });
    }

    /// <summary>
    /// The hazard the drain hook exists to close: a chunk id is freed and reissued to a different cluster. If the directory still held the old block, hits
    /// into the new cluster would find state describing a cluster that no longer exists.
    /// </summary>
    [Test]
    public void ARecycledChunkIdResolvesToTheNewBlockNotTheStaleOne()
    {
        using var dir = new ReplicationDirectory();
        var original = Rent();
        var replacement = Rent();
        Assert.That((nint)original, Is.Not.EqualTo((nint)replacement), "the two blocks must be distinct for this test to mean anything");

        dir.TryAdd(7, original);

        // The cluster drains: the hook removes the entry before FreeChunk hands id 7 back to the allocator.
        Assert.That(dir.TryRemove(7, out _), Is.True);

        // A new cluster is allocated and inherits id 7.
        Assert.That(dir.TryAdd(7, replacement), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(dir.TryGetBlock(7, out var found), Is.True);
            Assert.That((nint)found, Is.EqualTo((nint)replacement), "a hit into the reissued cluster must not reach the drained cluster's state");
            Assert.That(replacement->ChunkId, Is.EqualTo(7));
        });
    }

    /// <summary>
    /// SUB-13, and the reason this is a map rather than a flat array. Chunk ids are persisted file addresses, so a handful of watched clusters can sit
    /// anywhere in a space with tens of millions of entries. The directory must cost the watched count, not the span of the ids.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-13")]
    public void SparseChunkIdsCostOnlyWhatIsWatched()
    {
        using var dir = new ReplicationDirectory();

        int[] farApart = [0, 1_000, 1_000_000, 16_000_000, 48_000_000];
        foreach (var chunkId in farApart)
        {
            Assert.That(dir.TryAdd(chunkId, Rent()), Is.True);
        }

        Assert.Multiple(() =>
        {
            Assert.That(dir.Count, Is.EqualTo(farApart.Length));
            Assert.That(dir.Capacity, Is.LessThan(1024),
                "capacity must follow the watched count; a flat array over this id span would be hundreds of megabytes");

            foreach (var chunkId in farApart)
            {
                Assert.That(dir.TryGetBlock(chunkId, out _), Is.True, $"chunk {chunkId} must still resolve");
            }
        });
    }

    /// <summary>Growth past the load factor must not lose or alias entries — the probe sequence has to survive every rehash.</summary>
    [Test]
    public void EveryEntrySurvivesRepeatedGrowth()
    {
        using var dir = new ReplicationDirectory(initialCapacity: 4);
        const int count = 2_000;

        for (var i = 0; i < count; i++)
        {
            Assert.That(dir.TryAdd(i * 7, Rent()), Is.True, $"add {i} should be new");
        }

        Assert.That(dir.Count, Is.EqualTo(count));

        for (var i = 0; i < count; i++)
        {
            Assert.That(dir.TryGetBlock(i * 7, out _), Is.True, $"chunk {i * 7} was lost across a rehash");
        }

        // And nothing that was never added resolves.
        Assert.That(dir.TryGetBlock((count * 7) + 1, out _), Is.False);
    }

    /// <summary>
    /// The backing map nulls its storage on dispose, so an unguarded lookup afterwards would index off a null reference — and on a grown directory could
    /// probe into mapped memory and return garbage as a block pointer. Every entry point has to refuse instead.
    /// </summary>
    [Test]
    public void UsingADisposedDirectoryThrowsRatherThanReturningGarbage()
    {
        var dir = new ReplicationDirectory();
        var block = Rent();
        dir.TryAdd(5, block);
        dir.Dispose();

        Assert.Multiple(() =>
        {
            Assert.Throws<System.ObjectDisposedException>(() => dir.TryGetBlock(5, out _));
            Assert.Throws<System.ObjectDisposedException>(() => dir.TryAdd(6, block));
            Assert.Throws<System.ObjectDisposedException>(() => dir.TryRemove(5, out _));
            Assert.Throws<System.ObjectDisposedException>(() => _ = dir.Count);
            Assert.Throws<System.ObjectDisposedException>(() => _ = dir.Capacity);
            Assert.DoesNotThrow(() => dir.Dispose());
        });
    }

    /// <summary>
    /// Interleaved add and remove is the case the backing map's backward-shift deletion exists for, and the one nothing else here exercises: a removal
    /// moves later entries BACKWARDS along their probe runs, so a bug shows up as a neighbour becoming unreachable rather than as the removed key lingering.
    /// Growth on top of it is the classic break point.
    /// </summary>
    [Test]
    public void InterleavedAddAndRemoveKeepsEveryRemainingEntryReachable()
    {
        using var dir = new ReplicationDirectory(initialCapacity: 8);
        var live = new Dictionary<int, nint>();

        // Chunk ids deliberately share low bits so they collide into common probe runs, which is where backward-shift deletion actually does work.
        for (var round = 0; round < 8; round++)
        {
            for (var i = 0; i < 64; i++)
            {
                var chunkId = (i * 512) + round;
                var block = Rent();
                Assert.That(dir.TryAdd(chunkId, block), Is.True, $"round {round}: {chunkId} should be new");
                live[chunkId] = (nint)block;
            }

            // Drop every third entry, then confirm every survivor is still reachable and still names its own block.
            var doomed = new List<int>();
            foreach (var key in live.Keys)
            {
                if (key % 3 == 0)
                {
                    doomed.Add(key);
                }
            }

            foreach (var key in doomed)
            {
                Assert.That(dir.TryRemove(key, out _), Is.True, $"round {round}: {key} should have been present");
                live.Remove(key);
            }

            Assert.That(dir.Count, Is.EqualTo(live.Count), $"round {round}: count must track the live set");

            foreach (var (key, expected) in live)
            {
                Assert.That(dir.TryGetBlock(key, out var found), Is.True, $"round {round}: {key} became unreachable after a neighbour was removed");
                Assert.That((nint)found, Is.EqualTo(expected), $"round {round}: {key} resolves to the wrong block");
            }

            foreach (var key in doomed)
            {
                Assert.That(dir.TryGetBlock(key, out _), Is.False, $"round {round}: {key} was removed and must not resolve");
            }
        }
    }

    [Test]
    public void ANegativeChunkIdIsRejected()
    {
        using var dir = new ReplicationDirectory();
        var block = Rent();

        Assert.Throws<System.ArgumentOutOfRangeException>(() => dir.TryAdd(-1, block));
    }
}
