using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// The <c>Set</c> primitive on <c>VariableSizedBufferSegment</c> (#389 Phase 2) — replacing a buffer's entire content.
/// </summary>
/// <remarks>
/// <para>
/// Nothing could do this before. The whole mutation surface of a <c>ComponentCollection</c> was <c>Add</c>, and the VSBS itself offered only
/// <c>AddElement</c> / <c>AddElements</c> (bulk APPEND, sole caller <c>CloneBuffer</c>) / <c>DeleteElement</c>. Recovery's fold-flush needs to write a
/// collection's final content in one shot, so the primitive had to exist first.
/// </para>
/// <para>
/// The cases are chosen around what <c>Set</c> can get wrong rather than around its happy path: shrinking and growing exercise the chunk chain in both
/// directions, a copy-on-write-shared buffer is the one that must NOT be written through, and <c>bufferId == 0</c> is the "no buffer yet" shape every
/// freshly-spawned collection starts in.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class VsbsSetElementsTests : TestBase<VsbsSetElementsTests>
{
    private DatabaseEngine _dbe;
    private VariableSizedBufferSegment<int, PersistentStore> _vsbs;

    [SetUp]
    public void SetUpSegment()
    {
        _dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(_dbe);
        _dbe.InitializeArchetypes();
        _vsbs = _dbe.GetComponentCollectionVSBS<int>();
    }

    [TearDown]
    public void TearDownSegment()
    {
        _dbe?.Dispose();
        _dbe = null;
    }

    /// <summary>Elements that fit in the root chunk, and enough to span three chunks — computed from the segment, never hardcoded.</summary>
    private int RootCapacity => _vsbs.ElementCountRootChunk;

    private int ThreeChunkCount => _vsbs.ElementCountRootChunk + (2 * _vsbs.ElementCountPerChunk);

    private static int[] Model(int count, int seed = 0)
    {
        var a = new int[count];
        for (var i = 0; i < count; i++)
        {
            a[i] = seed + i + 1;
        }

        return a;
    }

    // Every ChunkAccessor must be created inside an epoch scope (ChunkBasedSegment asserts it) — the epoch is what keeps a page alive under the pointer the
    // accessor hands out. Production satisfies this structurally: a transaction holds one, and RecoveryDriver.Run opens one around the whole apply pass.

    private int[] ReadBack(int bufferId)
    {
        using var guard = EpochGuard.Enter(_dbe.EpochManager);
        var accessor = _vsbs.Segment.CreateChunkAccessor();
        try
        {
            var count = _vsbs.GetElementCount(bufferId, ref accessor);
            var dest = new int[count];
            var read = _vsbs.ReadAllElements(bufferId, dest, ref accessor);
            Assert.That(read, Is.EqualTo(count), "ReadAllElements must return the buffer's own element count");
            return dest;
        }
        finally
        {
            accessor.CommitChanges();
            accessor.Dispose();
        }
    }

    private int Set(int bufferId, ReadOnlySpan<int> elements)
    {
        using var guard = EpochGuard.Enter(_dbe.EpochManager);
        var accessor = _vsbs.Segment.CreateChunkAccessor();
        try
        {
            return _vsbs.SetElements(bufferId, elements, ref accessor);
        }
        finally
        {
            accessor.CommitChanges();
            accessor.Dispose();
        }
    }

    private int Allocate(ReadOnlySpan<int> elements) => Set(0, elements);

    private int RefCounterOf(int bufferId)
    {
        using var guard = EpochGuard.Enter(_dbe.EpochManager);
        using var a = new VariableSizedBufferAccessor<int, PersistentStore>(_vsbs, bufferId);
        return a.RefCounter;
    }

    private int AllocatedChunkCount => _vsbs.Segment.AllocatedChunkCount;

    // ── AC 2.1: the five shapes ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Set_OnTheNullBuffer_AllocatesAndFills()
    {
        var id = Set(0, Model(5));

        Assert.That(id, Is.Not.Zero, "a non-empty Set over the null buffer must allocate one");
        Assert.That(ReadBack(id), Is.EqualTo(Model(5)).AsCollection);
        Assert.That(RefCounterOf(id), Is.EqualTo(1), "a freshly set buffer is solely owned");
    }

    [Test]
    public void Set_ToEmpty_ReleasesTheBufferAndReturnsTheNullHandle()
    {
        var before = AllocatedChunkCount;
        var id = Allocate(Model(5));

        var after = Set(id, []);

        // Zero, not a fresh empty root chunk: a collection that was never appended to has _bufferId == 0, so setting one to empty has to produce the same
        // shape. Anything else leaks one allocation per empty collection on every recovery, and makes "is this collection empty" have two answers.
        Assert.That(after, Is.Zero, "an empty Set must yield the null buffer");
        Assert.That(AllocatedChunkCount, Is.EqualTo(before), "the old buffer's chunks must be reclaimed, not orphaned");
    }

    [Test]
    public void Set_Smaller_ReplacesTheContentExactly()
    {
        var id = Allocate(Model(ThreeChunkCount));
        var next = Set(id, Model(3, seed: 100));

        Assert.That(ReadBack(next), Is.EqualTo(Model(3, seed: 100)).AsCollection, "no element of the longer previous content may survive");
        Assert.That(RefCounterOf(next), Is.EqualTo(1));
    }

    [Test]
    public void Set_Larger_SpansTheChunkChain()
    {
        var id = Allocate(Model(2));
        var next = Set(id, Model(ThreeChunkCount, seed: 500));

        Assert.That(ThreeChunkCount, Is.GreaterThan(RootCapacity), "the model must exceed one chunk, or this case tests nothing");
        Assert.That(ReadBack(next), Is.EqualTo(Model(ThreeChunkCount, seed: 500)).AsCollection);
    }

    /// <summary>
    /// A buffer shared across MVCC revisions must be left byte-for-byte alone: <c>Set</c> allocates a new one and drops one reference.
    /// </summary>
    /// <remarks>
    /// This is the case that makes allocate-then-release the right shape rather than truncate-in-place. Writing through a shared buffer would silently
    /// rewrite another revision's content — a snapshot-isolation violation with no exception and no diagnostic.
    /// </remarks>
    [Test]
    public void Set_OnACowSharedBuffer_LeavesTheSharerUntouched()
    {
        var shared = Allocate(Model(4));

        using (EpochGuard.Enter(_dbe.EpochManager))
        {
            var addRefAccessor = _vsbs.Segment.CreateChunkAccessor();
            _vsbs.BufferAddRef(shared, ref addRefAccessor);
            addRefAccessor.CommitChanges();
            addRefAccessor.Dispose();
        }

        Assert.That(RefCounterOf(shared), Is.EqualTo(2), "the second revision now shares the buffer");

        var mine = Set(shared, Model(2, seed: 900));

        Assert.That(mine, Is.Not.EqualTo(shared), "a shared buffer must never be mutated in place");
        Assert.That(ReadBack(shared), Is.EqualTo(Model(4)).AsCollection, "the sharing revision's content must be intact");
        Assert.That(RefCounterOf(shared), Is.EqualTo(1), "our reference is dropped; the sharer keeps its own");
        Assert.That(ReadBack(mine), Is.EqualTo(Model(2, seed: 900)).AsCollection);
        Assert.That(RefCounterOf(mine), Is.EqualTo(1));
    }

    // ── AC 2.2: refcount conservation and no orphaned chunks ────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Set_RepeatedlyOverOneCollection_LeaksNoChunks()
    {
        // The leak this catches is a released buffer whose chunk chain is only partly reclaimed: it would not change any value anyone reads, so it can only
        // be seen as allocation growth over a sequence that returns to its starting size.
        var baseline = AllocatedChunkCount;

        var id = Allocate(Model(ThreeChunkCount));
        var peak = AllocatedChunkCount;
        Assert.That(peak, Is.GreaterThan(baseline + 1), "the model must occupy several chunks, or the reclaim path is untested");

        for (var round = 0; round < 5; round++)
        {
            id = Set(id, Model(ThreeChunkCount, seed: round * 1000));
            Assert.That(AllocatedChunkCount, Is.LessThanOrEqualTo(peak),
                $"round {round}: allocation grew across a Set of identical size — the previous buffer's chunks were orphaned, not reclaimed");
        }

        id = Set(id, []);
        Assert.That(id, Is.Zero);
        Assert.That(AllocatedChunkCount, Is.EqualTo(baseline), "after the final Set-to-empty every chunk this test allocated must be back in the segment");
    }

    // ── AC 2.3: idempotence (AP-12) ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>Set(x)</c> twice must be indistinguishable from <c>Set(x)</c> once — in content AND in refcount.
    /// </summary>
    /// <remarks>
    /// AP-12 requires recovery to converge when the same window is applied more than once, which happens for real: a crash during recovery leaves the data
    /// file further along while <c>CheckpointLSN</c> still points before the window, so the re-run replays it. Only the buffer ID differs between the two,
    /// which AP-13 explicitly tolerates — placements chosen at apply may differ from pre-crash.
    /// </remarks>
    [Test]
    public void Set_AppliedTwice_ConvergesInContentAndRefcount()
    {
        var once = Allocate(Model(6));
        var onceContent = ReadBack(once);
        var onceRefCount = RefCounterOf(once);
        var onceAllocated = AllocatedChunkCount;

        var twice = Set(once, Model(6));

        Assert.That(ReadBack(twice), Is.EqualTo(onceContent).AsCollection, "re-applying the same Set must not change the content");
        Assert.That(RefCounterOf(twice), Is.EqualTo(onceRefCount), "re-applying the same Set must not change the refcount");
        Assert.That(AllocatedChunkCount, Is.EqualTo(onceAllocated), "re-applying the same Set must not grow the segment");
    }

    // ── guards ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void ReadAllElementsRaw_WithATooSmallDestination_Throws()
    {
        var id = Allocate(Model(4));
        using var guard = EpochGuard.Enter(_dbe.EpochManager);
        var accessor = _vsbs.Segment.CreateChunkAccessor();
        try
        {
            var tooSmall = new int[2];
            Assert.Throws<InvalidOperationException>(() => _vsbs.ReadAllElements(id, tooSmall, ref accessor),
                "a silent short read would truncate a logged collection rather than failing");
        }
        finally
        {
            accessor.CommitChanges();
            accessor.Dispose();
        }
    }
}
