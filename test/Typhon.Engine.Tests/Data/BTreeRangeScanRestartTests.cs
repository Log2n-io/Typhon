using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// What a range scan owes its caller when the tree changes underneath it.
//
// BTree.RangeEnumerator is an OLC reader: it snapshots a leaf's version, reads entries without any lock, and validates
// the version before it follows the leaf's next/previous pointer. The restart machinery, and the RestartCount field on
// the range-scan trace span, exist precisely because a writer is expected to modify a leaf mid-scan.
//
// The contract a reader of an ordered index must keep is narrow and testable: the keys it emits are STRICTLY
// increasing (or strictly decreasing in reverse). It may legitimately miss an entry inserted behind the cursor, and it
// may legitimately see one inserted ahead of it — an OLC scan is not a snapshot. It may never emit the same entry
// twice, because a caller applying Take(N) or writing rows into a result list has no way to tell a duplicate from a
// genuine second row.
//
// These tests drive the writer from the SAME thread as the reader, between MoveNext calls. That is not a weaker
// version of a concurrency test — it is a stronger one: it makes the interleaving deterministic, so a failure is a
// reproduction rather than a flake.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

class BTreeRangeScanRestartTests
{
    private ServiceProvider _serviceProvider;

    /// <summary>Short by necessity — the option validator caps a database name at 63 UTF-8 bytes and these test names are longer than that.</summary>
    private static string CurrentDatabaseName => $"BTreeRangeRestart_{TestContext.CurrentContext.Test.ID}";

    [SetUp]
    public void Setup()
    {
        var dcs = PagedMMF.MinimumMemPageCount * PagedMMF.PageSize;

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddScopedManagedPagedMemoryMappedFile(options =>
          {
              options.DatabaseName = CurrentDatabaseName;
              options.DatabaseCacheSize = (ulong)dcs;
              options.PagesDebugPattern = false;
          });

        _serviceProvider = sc.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => _serviceProvider?.Dispose();

    /// <summary>
    /// The forward scan must never emit an entry twice, even when the leaf it is standing on is modified before it
    /// steps off that leaf.
    /// </summary>
    /// <remarks>
    /// The tree is seeded with EVEN keys. During the scan, an ODD key is inserted just BEHIND the cursor, which lands
    /// in the leaf the cursor currently occupies and bumps that leaf's OLC version. When the cursor exhausts the leaf
    /// it validates the version, finds it changed, and must resume after the last key it emitted — not from wherever
    /// re-reading the leaf happens to leave it.
    /// </remarks>
    [Test]
    unsafe public void ForwardScan_LeafModifiedBehindTheCursor_NeverEmitsAKeyTwice()
    {
        RunScan(reverse: false, out var emitted, out var restartsObserved);

        AssertStrictlyOrdered(emitted, ascending: true);
        Assert.That(restartsObserved, Is.GreaterThan(0),
            "the writer never actually invalidated a leaf the cursor was standing on — the test would assert nothing");
    }

    /// <summary>Same contract in reverse: strictly decreasing, no entry twice.</summary>
    [Test]
    unsafe public void ReverseScan_LeafModifiedBehindTheCursor_NeverEmitsAKeyTwice()
    {
        RunScan(reverse: true, out var emitted, out var restartsObserved);

        AssertStrictlyOrdered(emitted, ascending: false);
        Assert.That(restartsObserved, Is.GreaterThan(0), "no leaf was invalidated under the reverse cursor");
    }

    /// <summary>
    /// The control. With no concurrent writer the scan must return every seeded key exactly once — this is what proves
    /// the fix for the two tests above did not buy its no-duplicates property by dropping entries instead.
    /// </summary>
    [Test]
    unsafe public void ForwardScan_Undisturbed_YieldsEverySeededKeyExactlyOnce()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);
            for (var i = 0; i < SeedCount; i++)
            {
                tree.Add(i * 2, i, ref accessor);
            }

            accessor.Dispose();

            var emitted = new List<int>();
            var e = tree.EnumerateRange(0, SeedCount * 2);
            try
            {
                while (e.MoveNext())
                {
                    emitted.Add(e.Current.Key);
                }
            }
            finally
            {
                e.Dispose();
            }

            Assert.That(emitted, Has.Count.EqualTo(SeedCount));
            AssertStrictlyOrdered(emitted, ascending: true);
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private const int SeedCount = 4_000;

    /// <summary>
    /// Scans the whole seeded range while inserting an odd key one below (forward) or one above (reverse) the cursor
    /// every <c>WriteEvery</c> steps. Reports how many of those writes actually landed on the cursor's own leaf, which
    /// is what makes the restart path run.
    /// </summary>
    private unsafe void RunScan(bool reverse, out List<int> emitted, out int restartsObserved)
    {
        const int writeEvery = 8;

        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 400, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        emitted = [];
        restartsObserved = 0;
        try
        {
            var writeAccessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);
            for (var i = 0; i < SeedCount; i++)
            {
                tree.Add(i * 2, i, ref writeAccessor);
            }

            var inserted = new HashSet<int>();
            var step = 0;

            var e = reverse ? tree.EnumerateRangeDescending(0, SeedCount * 2) : tree.EnumerateRange(0, SeedCount * 2);
            try
            {
                while (e.MoveNext())
                {
                    var key = e.Current.Key;
                    emitted.Add(key);

                    // Probe only off a SEEDED (even) key. Deriving a probe from a previously-inserted odd key would land on an even key that already exists
                    // and throw UniqueConstraintViolation, turning a reader bug into a writer error.
                    if (++step % writeEvery != 0 || (key & 1) != 0)
                    {
                        continue;
                    }

                    // One below the cursor going forward, one above going backward: either way it is BEHIND the scan,
                    // so a correct reader must not emit it, and it lands on the leaf the cursor is standing on.
                    var probe = reverse ? key + 1 : key - 1;
                    if (probe <= 0 || probe >= SeedCount * 2 || !inserted.Add(probe))
                    {
                        continue;
                    }

                    tree.Add(probe, -1, ref writeAccessor);
                    restartsObserved++;
                }
            }
            finally
            {
                e.Dispose();
            }

            writeAccessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    private static void AssertStrictlyOrdered(List<int> keys, bool ascending)
    {
        Assert.That(keys, Is.Not.Empty, "the scan emitted nothing — nothing below asserts anything");

        for (var i = 1; i < keys.Count; i++)
        {
            var ordered = ascending ? keys[i] > keys[i - 1] : keys[i] < keys[i - 1];
            if (!ordered)
            {
                var direction = ascending ? "increasing" : "decreasing";
                Assert.Fail(
                    $"range scan emitted keys out of order at position {i}: ...{keys[i - 1]}, {keys[i]}... — an ordered scan must be strictly {direction}. "
                    + $"An equal pair is the same entry emitted twice; a reversal is the scan restarting behind itself. "
                    + $"({keys.Count} keys emitted, first={keys[0]}, last={keys[^1]})");
            }
        }
    }
}
