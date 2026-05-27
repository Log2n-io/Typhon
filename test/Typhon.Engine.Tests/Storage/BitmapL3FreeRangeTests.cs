using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for the contiguous-range overload <see cref="ManagedPagedMMF.FreePages(int,int,ChangeSet)"/> and its underlying primitive <c>BitmapL3.FreeRange</c>.
/// Added in P1 of the BulkLoad write path (see <c>claude/design/Durability/BulkLoad/02-write-path.md</c>) as the page-cleanup mechanism used by
/// <c>BulkLoadSession.Dispose-without-Complete</c> + the recovery Phase 3b.
/// </summary>
internal sealed class BitmapL3FreeRangeTests : TestBase<BitmapL3FreeRangeTests>
{
    private ManagedPagedMMF _pmmf;

    public override void Setup()
    {
        base.Setup();
        _pmmf = ServiceProvider.GetRequiredService<ManagedPagedMMF>();
    }

    [Test]
    public void FreePages_ContiguousRange_BitsFlippedAndRealloc()
    {
        // Allocate 100 pages — typically contiguous (or near-contiguous) at fresh startup.
        Span<int> allocated = new int[100];
        _pmmf.AllocatePages(ref allocated);

        var allocatedSet = new HashSet<int>(allocated.ToArray());
        Assert.That(allocatedSet.Count, Is.EqualTo(100), "100 unique page ids expected");

        // Find a contiguous sub-range of 50 inside the allocated set — most fresh allocations come out
        // in long contiguous runs (the L1 path of BitmapL3.Allocate). Sort, then look for the longest run.
        var sorted = allocatedSet.OrderBy(x => x).ToArray();
        int runStart = sorted[0], runEnd = sorted[0];
        int bestStart = sorted[0], bestLen = 1;
        for (int i = 1; i < sorted.Length; i++)
        {
            if (sorted[i] == runEnd + 1)
            {
                runEnd = sorted[i];
                if (runEnd - runStart + 1 > bestLen)
                {
                    bestStart = runStart;
                    bestLen = runEnd - runStart + 1;
                }
            }
            else
            {
                runStart = runEnd = sorted[i];
            }
        }
        Assert.That(bestLen, Is.GreaterThanOrEqualTo(50), "expected a contiguous run of at least 50 pages from a fresh allocator");

        // Free the first 50 of that contiguous run via the new range overload.
        var freeStart = bestStart;
        const int freeCount = 50;
        _pmmf.FreePages(freeStart, freeCount);

        // Reallocate 50 pages. The correctness invariant: the new allocation must not collide with pages
        // that are still allocated (i.e., the freed bits really did flip — otherwise the allocator would
        // either return collisions OR fail to allocate).
        //
        // Note: we do NOT assert the realloc IS a superset of the freed range. The L1-allocator path
        // prefers fully-free 64-page groups; freeing 50 out of a 64-group leaves it L1All-incomplete,
        // so the allocator may serve from elsewhere (e.g., the top of the bitmap). What matters is that
        // FreeRange flipped the bits — provable by absence-of-collision.
        Span<int> reallocated = new int[freeCount];
        _pmmf.AllocatePages(ref reallocated);

        var reallocSet = new HashSet<int>(reallocated.ToArray());
        var freedRange = Enumerable.Range(freeStart, freeCount).ToHashSet();
        var stillAllocated = allocatedSet.Except(freedRange).ToHashSet();

        Assert.That(reallocSet.Overlaps(stillAllocated), Is.False,
            "reallocated pages must not collide with pages that were never freed");
        Assert.That(reallocSet.Count, Is.EqualTo(freeCount),
            "allocator must produce exactly freeCount unique pages");
    }

    [Test]
    public void FreePages_RangeOverload_IsIdempotent()
    {
        // BL-04 invariant: FreeRange on already-free pages is a no-op (monotonic AND mask).
        // Allocate 64 pages, free a contiguous sub-range, free them AGAIN, then re-allocate — the second
        // free must not corrupt state.
        Span<int> allocated = new int[64];
        _pmmf.AllocatePages(ref allocated);

        var sorted = allocated.ToArray().OrderBy(x => x).ToArray();
        // Find a contiguous run of 32 if possible — guaranteed on a fresh DB.
        int runStart = sorted[0];
        for (int i = 1; i < sorted.Length; i++)
        {
            if (sorted[i] != sorted[i - 1] + 1)
            {
                runStart = sorted[i];
            }
        }

        const int count = 32;
        _pmmf.FreePages(runStart, count);
        Assert.DoesNotThrow(() => _pmmf.FreePages(runStart, count),
            "freeing an already-free range must be safe (BL-04 idempotency)");

        // Re-allocate; should succeed. (Can't wrap a ref-local Span in DoesNotThrow's lambda; call directly.)
        Span<int> realloc = new int[count];
        _pmmf.AllocatePages(ref realloc);
        Assert.That(realloc.ToArray(), Has.None.EqualTo(0), "every reallocated page must have a non-zero id");
    }
}
