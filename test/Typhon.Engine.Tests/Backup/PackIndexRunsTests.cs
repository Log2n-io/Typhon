using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// The external sort behind a pack's INDEX: whatever the arrival order and however many runs spill, the merge emits every entry once, ascending,
/// and refuses a duplicate; and the order capture actually produces — the scanner ascending, copy-before-write pre-images arriving ahead of it —
/// costs one run, not one per stray.
/// </summary>
[TestFixture]
internal sealed class PackIndexRunsTests
{
    private static List<PackPageEntry> Drain(PackIndexRuns runs)
    {
        var all = new List<PackPageEntry>();
        runs.Merge(batch =>
        {
            foreach (var e in batch)
            {
                all.Add(e);
            }
        });
        return all;
    }

    private static PackPageEntry Entry(int id) => new((uint)id, 100, 1000 + id, (uint)(id * 31));

    private static InMemorySparseFileSystem NewFs()
    {
        var fs = new InMemorySparseFileSystem();
        fs.CreateDirectory("/t");
        return fs;
    }

    /// <summary>
    /// P3: however many runs spill, a merge pass reads at most <see cref="PackIndexRuns.MaxMergeFanIn"/> of them at once (more are merged in passes
    /// into longer runs), so its open handles and read buffers stay bounded; the output is still every entry once, ascending.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void ManyRuns_MergeInPasses_NeverReadingMoreThanTheFanInAtOnce()
    {
        var fs = NewFs();
        const int n = 3_000;
        using var runs = new PackIndexRuns(fs, "/t/x.tpack.tmp.idx", 4);
        for (var id = n - 1; id >= 0; id--)
        {
            runs.Add(Entry(id));
        }

        var merged = Drain(runs);
        Assert.That(runs.SpilledRuns, Is.GreaterThan(PackIndexRuns.MaxMergeFanIn * 4), "premise: far more runs than one pass reads");
        Assert.That(runs.WidestMerge, Is.LessThanOrEqualTo(PackIndexRuns.MaxMergeFanIn));
        Assert.That(merged.Select(e => (int)e.PageId), Is.EqualTo(Enumerable.Range(0, n)));
    }

    /// <summary>
    /// P3: the sort's memory is native. Indexing 600 000 entries (one run spills, the rest is sorted in the heap's own memory) allocates almost nothing
    /// on the GC heap; it used to leave ~15 MiB of large-object garbage per point (the heap's array grown to 8 MiB, a 6 MiB copy of its rest, a 1 MiB
    /// output array, the merge's buffers).
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void SortingAPacksIndex_AllocatesAlmostNothingOnTheGcHeap()
    {
        var dir = System.IO.Path.Combine(TestStorage.ForFixture(nameof(PackIndexRunsTests)), Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            const int n = 600_000;
            long emitted = 0;
            var before = GC.GetAllocatedBytesForCurrentThread();
            using (var runs = new PackIndexRuns(StoreFileSystem.Instance, System.IO.Path.Combine(dir, "x.idx")))
            {
                for (var id = 0; id < n; id++)
                {
                    runs.Add(Entry(id));
                }

                runs.Merge(batch => emitted += batch.Length);
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(emitted, Is.EqualTo(n));
            Assert.That(allocated, Is.LessThan(1L << 20), $"{allocated:N0} managed bytes allocated sorting {emitted:N0} entries");
        }
        finally
        {
            System.IO.Directory.Delete(dir, true);
        }
    }

    [TestCase(1_000_000, TestName = "Everything in memory")]
    [TestCase(64, TestName = "Small heap, many runs")]
    [TestCase(1, TestName = "Heap of one, a run per descent")]
    [CancelAfter(10_000)]
    public void AnyArrivalOrder_MergesAscending_WithEveryEntryOnce(int heap)
    {
        var fs = NewFs();
        var random = TestSeed.Random(nameof(AnyArrivalOrder_MergesAscending_WithEveryEntryOnce) + heap);
        const int n = 5_000;
        var order = Enumerable.Range(0, n).OrderBy(_ => random.Next()).ToList();

        using var runs = new PackIndexRuns(fs, "/t/x.tpack.tmp.idx", heap);
        foreach (var id in order)
        {
            runs.Add(Entry(id));
        }

        var merged = Drain(runs);

        Assert.That(merged.Select(e => (int)e.PageId), Is.EqualTo(Enumerable.Range(0, n)), TestSeed.Repro);
        Assert.That(merged.All(e => e.DataOffset == 1000 + e.PageId && e.PageCrc == e.PageId * 31), Is.True, "entries carried intact");
    }

    [Test]
    [CancelAfter(10_000)]
    public void ScannerOrderWithForwardStrays_IsOneRun()
    {
        var fs = NewFs();
        const int n = 20_000;
        const int heap = 512;
        // Pre-images reach the sink before the scanner reaches their page: 300 pages, delivered in bursts ahead of the scan position.
        var random = TestSeed.Random(nameof(ScannerOrderWithForwardStrays_IsOneRun));
        var strays = Enumerable.Range(0, 300).Select(_ => random.Next(1_000, n)).Distinct().ToList();
        var delivered = new HashSet<int>();
        var order = new List<int>();
        var next = 0;
        for (var p = 0; p < n; p++)
        {
            if (p % 37 == 0)
            {
                for (var k = 0; k < 4 && next < strays.Count; next++)
                {
                    // A pre-image exists only for a page the scanner has not read yet.
                    if (strays[next] > p && delivered.Add(strays[next]))
                    {
                        order.Add(strays[next]);
                        k++;
                    }
                }
            }

            // The scanner skips a page whose pre-image was already captured.
            if (!delivered.Contains(p))
            {
                order.Add(p);
            }
        }

        Assert.That(order.Count, Is.EqualTo(n));

        using var runs = new PackIndexRuns(fs, "/t/x.tpack.tmp.idx", heap);
        foreach (var id in order)
        {
            runs.Add(Entry(id));
        }

        Assert.That(runs.SpilledRuns, Is.EqualTo(1), "the scan and its forward strays are one sorted run");
        Assert.That(Drain(runs).Select(e => (int)e.PageId), Is.EqualTo(Enumerable.Range(0, n)));
    }

    [Test]
    [CancelAfter(10_000)]
    public void Dispose_DeletesEverySidecar()
    {
        var fs = NewFs();
        var runs = new PackIndexRuns(fs, "/t/x.tpack.tmp.idx", 4);
        for (var i = 100; i > 0; i--)
        {
            runs.Add(Entry(i));
        }

        Assert.That(fs.AllFiles, Is.Not.Empty);
        runs.Dispose();
        Assert.That(fs.AllFiles, Is.Empty);
    }

    [TestCase(1_000_000)]
    [TestCase(3)]
    [CancelAfter(10_000)]
    public void ADuplicatePage_IsRefused(int heap)
    {
        var fs = NewFs();
        using var runs = new PackIndexRuns(fs, "/t/x.tpack.tmp.idx", heap);
        foreach (var id in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 3, 9 })
        {
            runs.Add(Entry(id));
        }

        var ex = Assert.Throws<InvalidOperationException>(() => Drain(runs));
        Assert.That(ex.Message, Does.Contain("page 3 added twice"));
    }
}
