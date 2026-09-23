using System.Collections.Generic;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// P1-14a — the frame pool: size-class rounding, the budget ceiling, exhaustion that skips and counts, and the bytes an operator can see.
/// </summary>
/// <remarks>
/// <para>
/// The properties worth pinning here are the ones that keep untrusted client load from becoming a memory-exhaustion path: the pool commits nothing until
/// asked, never commits past its budget, reports both kinds of refusal by returning <see langword="false"/> rather than by throwing on a tick-path call, and
/// counts the two kinds apart — a bound budget is a sizing decision, an over-sized frame is a defect upstream.
/// </para>
/// <para>
/// These run against a real <see cref="ResourceRegistry"/>, <see cref="ResourceGraph"/> and <see cref="MemoryAllocator"/> rather than fakes, because two of
/// the things most worth testing — that the slabs are actually freed, and that the bytes reach a snapshot — are only observable through them.
/// </para>
/// </remarks>
[TestFixture]
unsafe class FramePoolTests
{
    /// <summary>The design's classes, in order. Duplicated here on purpose: a test that reads the table it is checking proves nothing.</summary>
    private static readonly int[] ExpectedClasses = [512, 2048, 8192, 32768, 131072, 262144];

    /// <summary>A budget large enough that the tests which are not about exhaustion never hit it.</summary>
    private const long AmpleBudget = 64L * 1024 * 1024;

    private ResourceRegistry _registry;
    private ResourceGraph _graph;
    private MemoryAllocator _allocator;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "FramePoolTests" });
        _graph = new ResourceGraph(_registry);
        _allocator = new MemoryAllocator(_registry, new MemoryAllocatorOptions { Name = "FramePoolTestAllocator" });
    }

    [TearDown]
    public void TearDown()
    {
        _allocator?.Dispose();
        _registry?.Dispose();
    }

    private FramePool NewPool(long budgetBytes, string id = "FramePool") =>
        new(id, _registry.Runtime, _allocator, new SubscriptionsOptions { FramePoolBudgetBytes = budgetBytes });

    [Test]
    public void TheClassTableIsTheOneTheDesignSpecifies()
    {
        Assert.That(FramePool.SizeClassCount, Is.EqualTo(ExpectedClasses.Length));

        Assert.Multiple(() =>
        {
            for (var i = 0; i < ExpectedClasses.Length; i++)
            {
                Assert.That(FramePool.ClassBytes(i), Is.EqualTo(ExpectedClasses[i]), $"class {i}");
            }

            Assert.That(FramePool.LargestClassBytes, Is.EqualTo(256 * 1024), "the largest class is SubscriptionsOptions.FrameBytes' own default");
        });
    }

    /// <summary>
    /// Rounding UP is the whole contract: a request served by a smaller block would overrun it, and a request served by the next class up wastes bytes an
    /// operator paid for. The boundaries are where an off-by-one lives, so every one of them is a case.
    /// </summary>
    [TestCase(1, 512)]
    [TestCase(511, 512)]
    [TestCase(512, 512)]
    [TestCase(513, 2048)]
    [TestCase(2047, 2048)]
    [TestCase(2048, 2048)]
    [TestCase(2049, 8192)]
    [TestCase(8192, 8192)]
    [TestCase(8193, 32768)]
    [TestCase(32768, 32768)]
    [TestCase(32769, 131072)]
    [TestCase(131072, 131072)]
    [TestCase(131073, 262144)]
    [TestCase(262144, 262144)]
    public void EveryRequestRoundsUpToTheSmallestClassThatHoldsIt(int byteCount, int expectedCapacity)
    {
        using var pool = NewPool(AmpleBudget);

        var classIndex = FramePool.ClassIndexFor(byteCount);
        Assert.That(classIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(FramePool.ClassBytes(classIndex), Is.EqualTo(expectedCapacity));

        Assert.That(pool.TryRent(byteCount, out var block), Is.True);
        Assert.That(block.Capacity, Is.EqualTo(expectedCapacity), "the block handed out is exactly its class, never a larger one");
    }

    /// <summary>
    /// 256 KiB is exported in the catalog so an SDK sizes its receive buffer from it once. Quietly allocating past it would hand a client more than the buffer
    /// it has already sized — a protocol violation dressed as a convenience — so the request is refused, and counted apart from budget exhaustion,
    /// because the two mean different things to whoever reads the counter.
    /// </summary>
    [TestCase(262145)]
    [TestCase(1 << 20)]
    [TestCase(int.MaxValue)]
    [TestCase(0)]
    [TestCase(-1)]
    public void ARequestNoClassCanHoldIsRefusedRatherThanSilentlyAllocated(int byteCount)
    {
        using var pool = NewPool(AmpleBudget);

        Assert.That(FramePool.ClassIndexFor(byteCount), Is.EqualTo(-1));
        Assert.That(pool.TryRent(byteCount, out var block), Is.False, "no class can hold it, so nothing may be handed out");

        Assert.Multiple(() =>
        {
            Assert.That(block.IsValid, Is.False);
            Assert.That(pool.OversizeRefusalCount, Is.EqualTo(1));
            Assert.That(pool.BudgetSkipCount, Is.Zero, "an over-sized request is a defect upstream, not a sizing problem — it must not read as exhaustion");
            Assert.That(pool.CommittedBytes, Is.Zero, "a refusal commits nothing");
            Assert.That(_allocator.PinnedLiveBlocks, Is.Zero);
        });
    }

    [Test]
    public void APoolCommitsNothingUntilItIsAskedForABlock()
    {
        using var pool = NewPool(AmpleBudget);

        Assert.Multiple(() =>
        {
            Assert.That(pool.CommittedBytes, Is.Zero, "the budget is a ceiling, not a reservation");
            Assert.That(pool.BlockCount, Is.Zero);
            Assert.That(pool.RentedCount, Is.Zero);
            Assert.That(_allocator.PinnedLiveBlocks, Is.Zero, "no slab should have been taken");
        });
    }

    [Test]
    public void TheFirstRentOfAClassTakesExactlyOneSlabAndOnlyForThatClass()
    {
        using var pool = NewPool(AmpleBudget);

        Assert.That(pool.TryRent(512, out _), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(_allocator.PinnedLiveBlocks, Is.EqualTo(1), "one slab, not one allocation per block");
            Assert.That(pool.BlocksInClass(0), Is.GreaterThan(1), "a slab is carved into many blocks");
            for (var i = 1; i < FramePool.SizeClassCount; i++)
            {
                Assert.That(pool.BlocksInClass(i), Is.Zero, $"class {i} was never asked for and must have committed nothing");
            }
        });
    }

    [Test]
    public void EverySlabReachesTheDocumentedFloorAndHoldsWholeBlocks()
    {
        using var pool = NewPool(AmpleBudget);

        var committed = 0L;
        for (var i = 0; i < FramePool.SizeClassCount; i++)
        {
            Assert.That(pool.TryRent(FramePool.ClassBytes(i), out _), Is.True);
            var slabBytes = pool.CommittedBytes - committed;
            committed = pool.CommittedBytes;

            var stride = 64 + FramePool.ClassBytes(i);
            Assert.Multiple(() =>
            {
                Assert.That(slabBytes, Is.GreaterThanOrEqualTo(64 * 1024), $"class {i}: a slab must reach the 64 KiB floor, not merely approach it");
                Assert.That(slabBytes % stride, Is.Zero, $"class {i}: a slab holds whole blocks");
                Assert.That(slabBytes / stride, Is.EqualTo(pool.BlocksInClass(i)), $"class {i}: every carved block is accounted for");
            });
        }
    }

    [Test]
    public void RentedBlocksAreDistinctAndCacheLineAligned()
    {
        using var pool = NewPool(AmpleBudget);
        var seen = new HashSet<nint>();

        for (var i = 0; i < 64; i++)
        {
            Assert.That(pool.TryRent(512, out var block), Is.True, $"rent {i} should succeed within an ample budget");

            var address = (nint)block.Bytes;
            // (long) rather than nint: NUnit's Is.Zero is typed, and an IntPtr zero does not equal an Int32 zero.
            Assert.That((long)address % 64, Is.Zero, $"block {i} must start on a cache line — a frame is memcpy'd in and then read by a socket");
            Assert.That(seen.Add(address), Is.True, $"block {i} was handed out twice");
        }
    }

    /// <summary>
    /// The one property the whole budget exists for. A pool bounded to two slabs of a class hands out exactly what those slabs hold and then refuses — no
    /// third slab, no throw, and every refusal counted, so an operator sees the wall rather than inferring it from a client complaint.
    /// </summary>
    [Test]
    public void ThePoolNeverCommitsPastItsBudgetAndSkipsInsteadOfAllocating()
    {
        // Rent one block against an ample budget purely to learn the slab size, then build a pool bounded to two slabs.
        long slabBytes;
        int blocksPerSlab;
        using (var probe = NewPool(AmpleBudget, "Probe"))
        {
            Assert.That(probe.TryRent(512, out _), Is.True);
            slabBytes = probe.CommittedBytes;
            blocksPerSlab = probe.BlocksInClass(0);
        }

        // + half a slab: the remainder must buy nothing, because growth is slab-granular.
        using var pool = NewPool((slabBytes * 2) + (slabBytes / 2));

        var rented = 0;
        while (pool.TryRent(512, out _))
        {
            rented++;
            Assert.That(rented, Is.LessThanOrEqualTo(blocksPerSlab * 2 + 1), "the pool handed out more than its budget can hold");
        }

        Assert.Multiple(() =>
        {
            Assert.That(rented, Is.EqualTo(blocksPerSlab * 2), "exactly two slabs' worth, and the half slab bought nothing");
            Assert.That(pool.CommittedBytes, Is.EqualTo(slabBytes * 2));
            Assert.That(pool.CommittedBytes, Is.LessThanOrEqualTo(pool.BudgetBytes));
            Assert.That(_allocator.PinnedLiveBlocks, Is.EqualTo(2), "a third slab would be the allocation this budget exists to prevent");
            Assert.That(pool.BudgetSkipCount, Is.EqualTo(1), "the refusal that ended the loop is counted");
            Assert.That(pool.OversizeRefusalCount, Is.Zero, "exhaustion is not an over-sized request");
        });

        // Skipping is repeatable and stays cheap: it must not throw, and it must not creep past the ceiling on the tenth attempt either.
        for (var i = 0; i < 10; i++)
        {
            Assert.That(pool.TryRent(512, out _), Is.False);
        }

        Assert.Multiple(() =>
        {
            Assert.That(pool.BudgetSkipCount, Is.EqualTo(11));
            Assert.That(pool.CommittedBytes, Is.EqualTo(slabBytes * 2));
            Assert.That(_allocator.PinnedLiveBlocks, Is.EqualTo(2));
        });
    }

    /// <summary>Zero is a legal budget and means exactly "produce no frames" — the subsystem is disabled rather than broken.</summary>
    [Test]
    public void AZeroBudgetPoolNeverRentsAndNeverThrows()
    {
        using var pool = NewPool(0);

        Assert.That(pool.TryRent(512, out var block), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(block.IsValid, Is.False);
            Assert.That(pool.BudgetSkipCount, Is.EqualTo(1));
            Assert.That(pool.CommittedBytes, Is.Zero);
            Assert.That(_allocator.PinnedLiveBlocks, Is.Zero);
        });
    }

    [Test]
    public void ANegativeBudgetIsRefusedAtConstruction() =>
        Assert.Throws<System.ArgumentOutOfRangeException>(() => _ = NewPool(-1));

    [Test]
    public void AReturnedBlockGoesBackToItsOwnClassAndIsHandedOutAgain()
    {
        using var pool = NewPool(AmpleBudget);

        Assert.That(pool.TryRent(2048, out var first), Is.True);
        var freeBefore = pool.FreeInClass(1);
        pool.Return(first);

        Assert.Multiple(() =>
        {
            Assert.That(pool.FreeInClass(1), Is.EqualTo(freeBefore + 1), "the block went back to class 1, not to some other list");
            Assert.That(pool.RentedCount, Is.Zero);
        });

        Assert.That(pool.TryRent(2048, out var second), Is.True);
        Assert.That((nint)second.Bytes, Is.EqualTo((nint)first.Bytes), "a LIFO free list hands the last block back first");
    }

    [Test]
    public void ReturningABlockTwiceIsRejected()
    {
        using var pool = NewPool(AmpleBudget);

        Assert.That(pool.TryRent(512, out var block), Is.True);
        pool.Return(block);

        // A programming error, not exhaustion: tolerating it silently would thread the free list through one block twice and hand one buffer to two sessions.
        Assert.Throws<System.ArgumentException>(() => pool.Return(block));
    }

    [Test]
    public void ReturningABlockThisPoolNeverHandedOutIsRejected()
    {
        using var pool = NewPool(AmpleBudget);

        Assert.That(pool.TryRent(512, out var block), Is.True);

        // Same memory, a capacity no class has: the header cannot describe it, so the pool refuses rather than corrupting a free list.
        var lie = new FrameBlock(block.Bytes, 1024);
        Assert.Throws<System.ArgumentException>(() => pool.Return(lie));
    }

    /// <summary>
    /// The method that keeps the lock off the per-frame path. A session's frame size is stable tick to tick, so the block already in its slot is nearly always
    /// the class the next frame needs — and keeping it must cost the pool nothing at all, which is asserted here by the counters not moving.
    /// </summary>
    [Test]
    public void KeepingABlockOfTheRightClassTouchesThePoolNotAtAll()
    {
        using var pool = NewPool(AmpleBudget);

        var block = default(FrameBlock);
        Assert.That(pool.TryRentOrKeep(400, ref block, out var previous), Is.True);
        Assert.That(previous.IsValid, Is.False, "there was nothing to replace");

        var rentsBefore = pool.RentCount;
        var address = (nint)block.Bytes;

        for (var i = 0; i < 100; i++)
        {
            // Anything inside the same class: the block is kept, whatever the exact length.
            Assert.That(pool.TryRentOrKeep(1 + (i % 512), ref block, out var recycled), Is.True);
            Assert.That(recycled.IsValid, Is.False);
        }

        Assert.Multiple(() =>
        {
            Assert.That((nint)block.Bytes, Is.EqualTo(address), "the same block, kept in place");
            Assert.That(pool.RentCount, Is.EqualTo(rentsBefore), "a kept block is not a rent — the pool was not involved");
            Assert.That(pool.RentedCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void ABlockOfTheWrongClassIsReplacedAndTheOldOneHandedBack()
    {
        using var pool = NewPool(AmpleBudget);

        var block = default(FrameBlock);
        Assert.That(pool.TryRentOrKeep(512, ref block, out _), Is.True);
        var small = block;

        Assert.That(pool.TryRentOrKeep(4096, ref block, out var previous), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(block.Capacity, Is.EqualTo(8192), "4 KiB needs the 8 KiB class");
            Assert.That((nint)previous.Bytes, Is.EqualTo((nint)small.Bytes), "the old block is handed back, not silently dropped");
            Assert.That(pool.RentedCount, Is.EqualTo(2), "both are out until the caller returns the old one");
        });

        pool.Return(previous);
        Assert.That(pool.RentedCount, Is.EqualTo(1));
    }

    /// <summary>
    /// A replacement that cannot be rented must leave the caller's block alone. Returning the old one first and then failing would cost a session the frame it
    /// already had, which is a skip caused by the pool's own bookkeeping rather than by the budget.
    /// </summary>
    [Test]
    public void AReplacementThatCannotBeRentedLeavesTheExistingBlockUntouched()
    {
        long slabBytes;
        using (var probe = NewPool(AmpleBudget, "Probe"))
        {
            Assert.That(probe.TryRent(512, out _), Is.True);
            slabBytes = probe.CommittedBytes;
        }

        // One slab's worth of budget: the 512 B class can take it, and then nothing is left for the 8 KiB class.
        using var pool = NewPool(slabBytes);

        var block = default(FrameBlock);
        Assert.That(pool.TryRentOrKeep(512, ref block, out _), Is.True);
        var held = block;

        Assert.That(pool.TryRentOrKeep(4096, ref block, out var previous), Is.False);

        Assert.Multiple(() =>
        {
            Assert.That((nint)block.Bytes, Is.EqualTo((nint)held.Bytes), "the caller keeps what it had");
            Assert.That(block.Capacity, Is.EqualTo(held.Capacity));
            Assert.That(previous.IsValid, Is.False, "nothing was replaced, so nothing is owed to the pool");
            Assert.That(pool.BudgetSkipCount, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// An operator has to be able to see these bytes without a debugger: the pool is the piece of engine memory sized by client behaviour, so "how much of the
    /// budget is gone" is the first question asked of it.
    /// </summary>
    [Test]
    public void ThePoolsBytesAppearInAResourceSnapshot()
    {
        using var pool = NewPool(AmpleBudget, "Frames");

        Assert.That(pool.TryRent(512, out _), Is.True);
        Assert.That(pool.TryRent(262144, out _), Is.True);
        Assert.That(pool.TryRent(300000, out _), Is.False);

        var snapshot = _graph.GetSnapshot();

        Assert.That(snapshot.Nodes.ContainsKey("Root/Runtime/Frames"), Is.True, "the pool registers under the resource graph's Runtime node");
        var node = snapshot.Nodes["Root/Runtime/Frames"];

        Assert.That(node.Memory, Is.Not.Null, "a pool holding native memory must report it");
        Assert.Multiple(() =>
        {
            Assert.That(node.Memory.Value.AllocatedBytes, Is.EqualTo(pool.CommittedBytes));
            Assert.That(node.Memory.Value.AllocatedBytes, Is.GreaterThan(0));
            Assert.That(node.Memory.Value.PeakBytes, Is.GreaterThanOrEqualTo(node.Memory.Value.AllocatedBytes));
            Assert.That(node.Capacity, Is.Not.Null);
            Assert.That(node.Capacity.Value.Current, Is.EqualTo(2), "two blocks are out");
            Assert.That(node.Capacity.Value.Maximum, Is.EqualTo(pool.BlockCount));
        });

        var counters = new Dictionary<string, long>();
        foreach (var t in node.Throughput)
        {
            counters[t.Name] = t.Count;
        }

        Assert.Multiple(() =>
        {
            Assert.That(counters["FramesRented"], Is.EqualTo(2));
            Assert.That(counters["OversizeRefusals"], Is.EqualTo(1));
            Assert.That(counters["BudgetSkips"], Is.Zero);
        });

        // The slabs are children of the pool, so the tree shows where the bytes actually live.
        Assert.That(snapshot.Nodes.ContainsKey("Root/Runtime/Frames/Slab-0-0"), Is.True);
    }

    [Test]
    public void DisposeFreesEverySlabAndLeavesTheTreeClean()
    {
        var pool = NewPool(AmpleBudget, "Frames");
        Assert.That(pool.TryRent(512, out _), Is.True);
        Assert.That(pool.TryRent(8192, out _), Is.True);
        Assert.That(_allocator.PinnedLiveBlocks, Is.EqualTo(2));

        pool.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(_allocator.PinnedLiveBlocks, Is.Zero, "every slab is freed");
            Assert.That(pool.CommittedBytes, Is.Zero);
            Assert.That(pool.BlockCount, Is.Zero);
            Assert.That(_graph.GetSnapshot().Nodes.ContainsKey("Root/Runtime/Frames"), Is.False, "a disposed pool leaves the tree");
        });

        // A rent after disposal is a lifecycle bug on the caller's side, but it must answer "no" rather than touch freed memory.
        Assert.That(pool.TryRent(512, out _), Is.False);
    }

    /// <summary>
    /// The whole point of renting native memory: the frame reaches a link as a <see cref="System.ReadOnlyMemory{T}"/> with no pointer over GC memory anywhere
    /// on the path, which is what <see cref="NativeFrameMemoryManager"/> exists for.
    /// </summary>
    [Test]
    public void ARentedBlockReachesALinkThroughTheNativeFrameView()
    {
        using var pool = NewPool(AmpleBudget);
        Assert.That(pool.TryRent(512, out var block), Is.True);

        for (var i = 0; i < 512; i++)
        {
            block.Bytes[i] = (byte)i;
        }

        var view = new NativeFrameMemoryManager(block.Bytes, 512);
        var memory = view.Memory;

        Assert.Multiple(() =>
        {
            Assert.That(memory.Length, Is.EqualTo(512));
            Assert.That(memory.Span[0], Is.EqualTo((byte)0));
            Assert.That(memory.Span[511], Is.EqualTo((byte)255));
        });

        fixed (byte* first = memory.Span)
        {
            Assert.That((nint)first, Is.EqualTo((nint)block.Bytes), "the view addresses the pooled block itself — nothing was copied and nothing was pinned");
        }

        view.Release();
        pool.Return(block);
    }
}
