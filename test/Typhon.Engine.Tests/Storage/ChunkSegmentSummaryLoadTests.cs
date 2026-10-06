using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// A chunk segment loaded from a chunk summary (#1143) whose recorded count is wrong cannot wedge the allocator: the count is trusted after a clean close,
/// and nothing compares it with the pages (CS-05), so the allocator has to survive one a faulty close got wrong.
/// </summary>
[TestFixture]
sealed class ChunkSegmentSummaryLoadTests
{
    private const int Stride = 64;

    private readonly List<string> _bundles = [];

    [TearDown]
    public void TearDown() => SegmentGrowTestKit.DeleteBundles(_bundles);

    /// <summary>
    /// The segment is full, and the summary says one chunk is free. The allocator used to rebuild its free list from the bitmaps, find nothing, see the
    /// count still below capacity, and rebuild again, for ever. It now grows, as for any full segment.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("CS-05")]
    public void ASummaryWithAShortCount_GrowsAFullSegment_InsteadOfSpinning()
    {
        using var provider = SegmentGrowTestKit.CreateProvider(memPageCount: 800, "cs05_short_count", _bundles);
        using var scope = provider.CreateScope();
        var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

        int root;
        int capacity;
        ChunkSegmentSummary shortCount;
        using (EpochGuard.Enter(pmmf.EpochManager))
        {
            var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 2, Stride);
            capacity = segment.ChunkCapacity;
            while (segment.AllocatedChunkCount < capacity)   // some chunks may already be reserved at creation
            {
                segment.AllocateChunk(false);
            }

            Assert.That(segment.ChunkCapacity, Is.EqualTo(capacity), "premise: the segment is exactly full, not grown");
            var exact = segment.CaptureSummary();
            shortCount = new ChunkSegmentSummary(exact.RootPageIndex, exact.PageCount, exact.AllocatedCount - 1, exact.PagesWithRoom);
            root = segment.RootPageIndex;
        }

        var fresh = new ChunkBasedSegment<PersistentStore>(pmmf.EpochManager, new PersistentStore(pmmf), Stride);
        using (EpochGuard.Enter(pmmf.EpochManager))
        {
            Assert.That(fresh.Load(root, shortCount), Is.True);
        }

        Assert.That(fresh.AllocatedChunkCount, Is.EqualTo(capacity - 1), "premise: the load adopted the summary's short count");

        var allocation = Task.Run(() =>
        {
            using var guard = EpochGuard.Enter(pmmf.EpochManager);
            return fresh.AllocateChunk(false);
        });
        Assert.That(allocation.Wait(TimeSpan.FromSeconds(20)), Is.True, "the allocator spun on a full segment whose count says a chunk is free");
        Assert.That(fresh.ChunkCapacity, Is.GreaterThan(capacity), "it grew, as a full segment does");
    }
}
