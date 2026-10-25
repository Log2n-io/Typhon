using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// A read that visits a whole segment holds one page of the cache at a time (EP-02, #1144): each page is acquired for reading, pinned rather than
/// epoch-tagged, and released before the next. The scans used to tag every page with the caller's epoch, so a segment larger than the cache made them
/// wait for evictions their own tags forbid. Here the segment is four times the cache.
/// </summary>
[TestFixture]
sealed class WholeSegmentScanTests
{
    private const int Stride = 64;
    private const int SegmentPages = 512;
    private const int SmallCachePages = 128;

    private readonly List<string> _bundles = [];

    [TearDown]
    public void TearDown() => SegmentGrowTestKit.DeleteBundles(_bundles);

    /// <summary>A chunk segment of <see cref="SegmentPages"/> pages, built and saved under a cache that holds it; returns its root.</summary>
    private int BuildChunkSegment(string name, bool full = false)
    {
        using var provider = SegmentGrowTestKit.CreateProvider(memPageCount: SegmentPages * 2, name, _bundles);
        using var scope = provider.CreateScope();
        var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        using var guard = EpochGuard.Enter(pmmf.EpochManager);
        var changeSet = pmmf.CreateChangeSet();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, SegmentPages, Stride, changeSet);
        for (var i = 0; i < SegmentPages; i++)
        {
            segment.AllocateChunk(false, changeSet);   // something in the bitmaps to count
        }

        if (full)
        {
            // Every chunk taken but the last page's last: a search from the first page has every other page to look at.
            var last = -1;
            while (segment.FreeChunkCount > 0)
            {
                last = segment.AllocateChunk(false, changeSet);
            }

            segment.FreeChunk(last);
        }

        changeSet.SaveChanges();
        return segment.RootPageIndex;
    }

    /// <summary>The same database reopened with a cache a quarter of the segment's size.</summary>
    private ServiceProvider ReopenSmall(string name) => SegmentGrowTestKit.CreateProvider(memPageCount: SmallCachePages, name, _bundles, fresh: false);

    /// <summary>
    /// A load without a chunk summary — every load after a crash — scans every page for the allocator's state. Inside one epoch scope, as the open runs it.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-02")]
    public void ALoadWithoutASummary_ScansASegmentLargerThanTheCache()
    {
        var root = BuildChunkSegment("ep02_load");
        using var provider = ReopenSmall("ep02_load");
        using var scope = provider.CreateScope();
        var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

        using var guard = EpochGuard.Enter(pmmf.EpochManager);
        var segment = pmmf.LoadChunkBasedSegment(root, Stride);

        Assert.That(segment, Is.Not.Null);
        Assert.That(segment.Length, Is.EqualTo(SegmentPages));
        Assert.That(segment.AllocatedChunkCount, Is.EqualTo(SegmentPages + 1), "the scan counted every page's chunks (one is reserved at creation)");
    }

    /// <summary>
    /// An allocation's search across room bits on full pages — set when a summary recorded them before the pages filled — on a segment larger than the
    /// cache, inside the allocating caller's epoch scope: each full page is looked at and released, and the free chunk on the last page is found.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-02")]
    public void ASearchAcrossFullPages_OnASegmentLargerThanTheCache_Completes()
    {
        var root = BuildChunkSegment("ep02_search", full: true);
        using var provider = ReopenSmall("ep02_search");
        using var scope = provider.CreateScope();
        var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.LoadChunkBasedSegment(root, Stride);

        using var guard = EpochGuard.Enter(pmmf.EpochManager);
        segment.MarkEveryPageForTest();
        var length = segment.Length;

        Assert.That(segment.AllocateChunk(false), Is.GreaterThanOrEqualTo(0), "the search reaches the one free chunk");
        Assert.That(segment.Length, Is.EqualTo(length), "found, not grown past");
    }

    /// <summary>The integrity check's walk of a segment's forward chain, on a segment larger than the cache, inside one epoch scope.</summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-02")]
    public void TheChainWalk_OnASegmentLargerThanTheCache_Completes()
    {
        var root = BuildChunkSegment("ep02_walk");
        using var provider = ReopenSmall("ep02_walk");
        using var scope = provider.CreateScope();
        var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = pmmf.LoadChunkBasedSegment(root, Stride);

        using var guard = EpochGuard.Enter(pmmf.EpochManager);
        Assert.That(segment.WalkForwardChainPageCount(), Is.EqualTo(SegmentPages));
    }

    /// <summary>A page acquired for reading stays in the cache whatever eviction runs, and is evictable again the moment it is released.</summary>
    [Test]
    public void APageAcquiredForReading_IsNotEvicted_UntilReleased()
    {
        var root = BuildChunkSegment("ep02_pin");
        using var provider = ReopenSmall("ep02_pin");
        using var scope = provider.CreateScope();
        var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        var filePage = pmmf.LoadChunkBasedSegment(root, Stride).Pages[7];
        pmmf.EvictEvictablePagesForTest();

        Assert.That(pmmf.AcquirePageForRead(filePage, out var memPageIndex), Is.True);
        pmmf.EvictEvictablePagesForTest();
        Assert.That(pmmf.GetFilePageIndex(memPageIndex), Is.EqualTo(filePage), "a page held for reading was evicted");

        pmmf.ReleasePageForRead(memPageIndex);
        pmmf.EvictEvictablePagesForTest();
        Assert.That(pmmf.GetFilePageIndex(memPageIndex), Is.Not.EqualTo(filePage), "a released page stayed pinned");
    }

    /// <summary>
    /// A read leaves no epoch tag, which is what would keep the page for the caller's scope, and does not mark the page recently used, so a scan does
    /// not push the working set out of the cache.
    /// </summary>
    [Test]
    public void AcquiringForRead_NeitherTagsNorWarmsThePage()
    {
        var root = BuildChunkSegment("ep02_cold");
        using var provider = ReopenSmall("ep02_cold");
        using var scope = provider.CreateScope();
        var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        var filePage = pmmf.LoadChunkBasedSegment(root, Stride).Pages[9];
        pmmf.EvictEvictablePagesForTest();

        Assert.That(pmmf.AcquirePageForRead(filePage, out var memPageIndex), Is.True);
        try
        {
            Assert.That(pmmf.GetPageInfoForDiagnostic(memPageIndex).AccessEpoch, Is.Zero, "the read tagged the page with an epoch");
            Assert.That(pmmf.GetClockSweepCounterForDiagnostic(filePage), Is.Zero, "the read marked the page recently used");
        }
        finally
        {
            pmmf.ReleasePageForRead(memPageIndex);
        }
    }

    /// <summary>
    /// Readers acquiring pages while another thread evicts everything it can: a reader always gets the page it asked for, never a slot that was being
    /// reclaimed, and no slot is reclaimed under a pin (the eviction path asserts it in Debug).
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public void ReadersRacingEviction_AlwaysGetThePageTheyAskedFor()
    {
        var root = BuildChunkSegment("ep02_race");
        using var provider = ReopenSmall("ep02_race");
        using var scope = provider.CreateScope();
        var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        var pages = pmmf.LoadChunkBasedSegment(root, Stride).Pages.ToArray();

        using var stop = new CancellationTokenSource();
        var evictor = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                pmmf.EvictEvictablePagesForTest();
            }
        });

        var wrong = 0;
        var readers = new Task[2];
        for (var r = 0; r < readers.Length; r++)
        {
            readers[r] = Task.Run(() =>
            {
                for (var pass = 0; pass < 20; pass++)
                {
                    foreach (var filePage in pages)
                    {
                        pmmf.AcquirePageForRead(filePage, out var memPageIndex);
                        if (pmmf.GetFilePageIndex(memPageIndex) != filePage)
                        {
                            Interlocked.Increment(ref wrong);
                        }

                        pmmf.ReleasePageForRead(memPageIndex);
                    }
                }
            });
        }

        Task.WaitAll(readers);
        stop.Cancel();
        evictor.Wait();

        Assert.That(wrong, Is.Zero, "a reader was handed a slot that no longer held its page");
    }

    /// <summary>
    /// The allocated-chunk cursor walks a segment four times the cache inside one epoch scope, as a crash open's recovery scans do, and returns every
    /// allocated chunk once, in order: it copies each page's bitmap under a read pin and releases it. The recovery scans asked the epoch-tagged
    /// <c>IsChunkAllocated</c> of every chunk and pinned the whole segment (#1183).
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-02")]
    public void TheAllocatedChunkCursor_WalksASegmentLargerThanTheCache()
    {
        var root = BuildChunkSegment("ep02_cursor", full: true);
        using var provider = ReopenSmall("ep02_cursor");
        using var scope = provider.CreateScope();
        var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

        using var guard = EpochGuard.Enter(pmmf.EpochManager);
        var segment = pmmf.LoadChunkBasedSegment(root, Stride);
        var count = 0;
        var previous = -1;
        foreach (var chunkId in segment.AllocatedChunks())
        {
            Assert.That(chunkId, Is.GreaterThan(previous), "in chunk order, once");
            previous = chunkId;
            count++;
        }

        Assert.That(count, Is.EqualTo(segment.AllocatedChunkCount), "every allocated chunk");
        Assert.That(segment.IsChunkAllocatedUnpinned(previous) && !segment.IsChunkAllocatedUnpinned(segment.ChunkCapacity - 1), Is.True,
            "the unpinned check agrees: the last chunk returned is allocated, the one freed is not");
    }

    /// <summary>The unpinned allocation check asked of every chunk of a segment four times the cache, inside one epoch scope: it pins nothing.</summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-02")]
    public void TheUnpinnedAllocationCheck_OfEveryChunk_HoldsNoPage()
    {
        var root = BuildChunkSegment("ep02_point", full: true);
        using var provider = ReopenSmall("ep02_point");
        using var scope = provider.CreateScope();
        var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

        using var guard = EpochGuard.Enter(pmmf.EpochManager);
        var segment = pmmf.LoadChunkBasedSegment(root, Stride);
        var allocated = 0;
        for (var chunkId = 0; chunkId < segment.ChunkCapacity; chunkId++)
        {
            allocated += segment.IsChunkAllocatedUnpinned(chunkId) ? 1 : 0;
        }

        Assert.That(allocated, Is.EqualTo(segment.AllocatedChunkCount), "every allocated chunk counted");
    }

    /// <summary>
    /// The crash open's clear of a shared index segment four times the cache, inside one epoch scope: it keeps only the directory chunks and empties the
    /// directory (#1463). The sparse segment has nothing to free past the directory, so it covers the bitmap walk; the full one covers the frees' writes.
    /// </summary>
    [TestCase(false, false, TestName = "SharedIndexClear_SparseIndex64")]
    [TestCase(false, true, TestName = "SharedIndexClear_FullIndex64")]
    [TestCase(true, false, TestName = "SharedIndexClear_SparseString64")]
    [TestCase(true, true, TestName = "SharedIndexClear_FullString64")]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-02")]
    public unsafe void TheSharedIndexClear_ReleasesASegmentLargerThanTheCache(bool string64, bool full)
    {
        var name = $"ep02_index_{string64}_{full}";
        var stride = string64 ? sizeof(IndexString64Chunk) : 256;
        int root;
        using (var buildProvider = SegmentGrowTestKit.CreateProvider(SegmentPages * 2, name, _bundles))
        using (var buildScope = buildProvider.CreateScope())
        {
            var mmf = buildScope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
            using var buildGuard = EpochGuard.Enter(mmf.EpochManager);
            var buildChanges = mmf.CreateChangeSet();
            var built = mmf.AllocateChunkBasedSegment(PageBlockType.None, SegmentPages, stride, buildChanges);
            var allocated = full ? built.ChunkCapacity : BTreeBase<PersistentStore>.DirectoryChunkCount;
            while (built.AllocatedChunkCount < allocated)
            {
                built.AllocateChunk(false, buildChanges);
            }

            var directory = built.CreateChunkAccessor(buildChanges);
            try
            {
                directory.GetChunk<BTreeDirectoryHeader>(0, true).EntryCount = 9;
            }
            finally
            {
                directory.Dispose();
            }

            buildChanges.SaveChanges();
            root = built.RootPageIndex;
        }

        using var provider = ReopenSmall(name);
        using var scope = provider.CreateScope();
        var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        using var guard = EpochGuard.Enter(pmmf.EpochManager);
        var segment = pmmf.LoadChunkBasedSegment(root, stride);
        var changeSet = pmmf.CreateChangeSet();
        // A bare storage layer has no checkpointer: back-pressure writes the freed bitmaps out, as the crash open's flush-only cycle does (CK-08).
        pmmf.OnBackpressure = () => pmmf.SavePages(pmmf.CollectDirtyMemPageIndices()).GetAwaiter().GetResult();

        BTreeBase<PersistentStore>.ClearSharedSegment(segment, changeSet);
        changeSet.SaveChanges();

        var remaining = new List<int>();
        foreach (var chunkId in segment.AllocatedChunks())
        {
            remaining.Add(chunkId);
        }

        Assert.That(remaining, Is.EqualTo(new[] { 0, 1, 2, 3 }), "the directory chunks stay, every node chunk is freed");
        Assert.That(segment.AllocatedChunkCount, Is.EqualTo(4));
        var accessor = segment.CreateScanAccessor();
        try
        {
            Assert.That(accessor.GetChunkReadOnly<BTreeDirectoryHeader>(0).EntryCount, Is.Zero, "the trees re-register from an empty directory");
        }
        finally
        {
            accessor.Dispose();
        }
    }
}
