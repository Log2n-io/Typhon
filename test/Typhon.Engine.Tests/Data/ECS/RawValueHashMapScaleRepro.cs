using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tight reproducer for the corruption observed at scale during BulkLoad 17 M runs. Bypasses BulkLoadSession,
/// Transaction, and the ECS layer — drives <see cref="RawValuePagedHashMap{TKey,TStore}"/> directly with the
/// same parameters (N0=256, valueSize matching the first map to corrupt = 26 → bucketCapacity=7) and forces
/// checkpoint cycles between batches to expose the page-eviction race we suspect is the root cause.
/// </summary>
/// <remarks>
/// If the corruption reproduces here, we have a minimal-surface repro that runs in seconds (vs the 4-minute
/// 17 M fixture). That makes the bug iterable.
/// </remarks>
[TestFixture]
[Category("ScaleRepro")]
[Explicit("Entry point for the stability investigation initiative (residual lost-write at ~700K). Skipped from normal runs; opt-in via --filter Category=ScaleRepro or --filter ClassName=RawValueHashMapScaleRepro.")]
[NonParallelizable]
unsafe class RawValueHashMapScaleRepro
{
    private ServiceProvider _serviceProvider;
    private string _walDir;
    private string _dbDir;

    [SetUp]
    public void Setup()
    {
        _walDir = Path.Combine(Path.GetTempPath(), $"typhon_repro_wal_{Guid.NewGuid():N}");
        _dbDir = Path.Combine(Path.GetTempPath(), $"typhon_repro_db_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_walDir);
        Directory.CreateDirectory(_dbDir);

        var services = new ServiceCollection();
        services
            .AddLogging(builder =>
            {
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.TimestampFormat = "mm:ss.fff ";
                });
                builder.SetMinimumLevel(LogLevel.Warning);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddSingleton<IWalFileIO>(new WalFileIO())
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = "RawHashMapRepro";
                opts.DatabaseDirectory = _dbDir;
                // Small cache to force eviction pressure — same lever the 17 M fixture trips.
                // 32 × MinimumCacheSize — gives enough headroom for the 2 M-entry test to run without
                // backpressure timeout. The original bug (race) is fixed at the engine layer; cache
                // headroom here just lets the test complete in reasonable time.
                opts.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 32;
            })
            .AddScopedDatabaseEngine(opts =>
            {
                opts.Wal = new WalWriterOptions
                {
                    WalDirectory = _walDir,
                    GroupCommitIntervalMs = 5,
                    UseFUA = false,
                    SegmentSize = 4 * 1024 * 1024,
                    PreAllocateSegments = 1,
                };
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider?.Dispose();
        _serviceProvider = null;
        try { if (Directory.Exists(_walDir)) Directory.Delete(_walDir, true); } catch { /* ignored */ }
        try { if (Directory.Exists(_dbDir)) Directory.Delete(_dbDir, true); } catch { /* ignored */ }
        Log.CloseAndFlush();
    }

    /// <summary>
    /// Reproduce the scale-induced corruption by inserting many sequential keys into a single
    /// <see cref="RawValuePagedHashMap{TKey,TStore}"/> and forcing a checkpoint between batches.
    /// Verifies every inserted key is retrievable post-insertion; any missing key signals corruption.
    /// </summary>
    /// <remarks>
    /// Parameters mirror the engine's first-to-corrupt map observed at 1.7 M / 256 K entries:
    /// N0=256, valueSize=26 → bucketCapacity=7. Insert count: 500 000 (well past the 256 K threshold
    /// where the bug surfaces in production). Batch size 5 000 mirrors BulkLoadSession's transaction recycle.
    /// </remarks>
    [Test]
    [CancelAfter(120_000)]
    public void Insert_500K_SequentialKeys_AllRetrievable()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        // Engine must be initialised so its CheckpointManager is running.
        dbe.InitializeArchetypes();

        const int valueSize = 26;
        const int n0 = 256;
        // 5M entries pushes the hash map's overflow-directory chain heavily — the 17 M fixture corrupts at
        // ~1.1 M buckets (level 12 linear hashing); at 5 M entries we're well into level 14+ with thousands
        // of OverflowDirIndex chunks and tens of thousands of directory chunks. This is where the second-
        // class corruption surfaces (dir chunks losing all 64 slots — a content-write durability race).
        const int totalEntries = 5_000_000;
        // Smaller batches → epoch guard releases more often, checkpoint can drain the cache between bursts.
        const int batchSize = 1_000;

        int stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        Assert.That(stride, Is.EqualTo(256), "Stride is expected to be 256 for valueSize=26 — sanity check on engine recommendation.");

        var segment = dbe.MMF.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);
        // Single ChangeSet for the whole bulk — same lifetime model as BulkLoadSession.
        var cs = dbe.MMF.CreateChangeSet();
        var map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, n0, valueSize);

        // Each value record is just `key` written 26 / 8 ~= 3 times (with padding). Lets us verify retrieved
        // bytes match what we wrote. Pattern: [key:long][key:long][key:long][pad:short].
        byte* record = stackalloc byte[valueSize];
        var em = dbe.MMF.EpochManager;

        var sw = Stopwatch.StartNew();
        int loggedSlotZeroBefore = CountLogLines("[SLOT-ZERO]");
        int loggedZeroStartBefore = CountLogLines("[ZERO-START]");
        int loggedAppendCorruptBefore = CountLogLines("[APPEND-CORRUPT]");

        for (int batch = 0; batch * batchSize < totalEntries; batch++)
        {
            int batchStart = batch * batchSize;
            int batchEnd = Math.Min(batchStart + batchSize, totalEntries);

            using (EpochGuard.Enter(em))
            {
                var accessor = segment.CreateChunkAccessor(cs);
                try
                {
                    for (int i = batchStart; i < batchEnd; i++)
                    {
                        long key = i + 1; // skip 0 (would collide with sentinel handling in some maps)
                        long* recAsLong = (long*)record;
                        recAsLong[0] = key;
                        recAsLong[1] = key;
                        recAsLong[2] = key;
                        map.InsertNew(key, record, ref accessor, cs);
                    }
                }
                finally
                {
                    // Disposing accessor decrements ACW for every dirty slot — checkpoint can now snapshot
                    // those pages on its next cycle.
                    accessor.Dispose();
                }
            }

            // Cap DC at 1 across all pages this ChangeSet has touched — without this, hot pages accumulate
            // unbounded DC and the cache fills up (page cache backpressure). Mirrors BulkLoadSession's
            // periodic ReleaseDirtyMarksIfNeeded — but called per-batch here (5 000 ops) instead of every
            // 128 ops, which is plenty for our 500 K total.
            cs.ReleaseExcessDirtyMarks();

            // Force checkpoint synchronously — drives DC decrements 1→0, making pages evictable, which is
            // the suspected race trigger (eviction reload of a chunk whose init writes haven't been
            // captured in any checkpoint yet).
            dbe.CheckpointManager.ForceCheckpoint();

            if (batch % 10 == 0)
            {
                int slotZero = CountLogLines("[SLOT-ZERO]") - loggedSlotZeroBefore;
                int zeroStart = CountLogLines("[ZERO-START]") - loggedZeroStartBefore;
                int appendCorrupt = CountLogLines("[APPEND-CORRUPT]") - loggedAppendCorruptBefore;
                TestContext.Out.WriteLine(
                    $"[{sw.Elapsed:mm\\:ss}] batch={batch} inserted={batchEnd}/{totalEntries} " +
                    $"map.Entries={map.EntryCount} " +
                    $"SLOT-ZERO={slotZero} ZERO-START={zeroStart} APPEND-CORRUPT={appendCorrupt}");
                if (slotZero > 0 || zeroStart > 0 || appendCorrupt > 0)
                {
                    TestContext.Out.WriteLine($"  >>> CORRUPTION REPRODUCED at batch {batch} (entries={batchEnd}) <<<");
                    Assert.Fail($"Corruption reproduced at batch {batch}, entries={batchEnd}. " +
                                $"SLOT-ZERO={slotZero}, ZERO-START={zeroStart}, APPEND-CORRUPT={appendCorrupt}. " +
                                $"See {DiagLogPath} for details.");
                }
            }
        }

        sw.Stop();
        TestContext.Out.WriteLine($"All {totalEntries:N0} inserts complete in {sw.Elapsed:mm\\:ss}, " +
                                  $"map.EntryCount={map.EntryCount}");

        Assert.That((int)map.EntryCount, Is.EqualTo(totalEntries),
            "Map entry count must match the number of unique keys inserted.");

        // Verify every key is retrievable.
        byte* readBuf = stackalloc byte[valueSize];
        int notFound = 0;
        int mismatched = 0;
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            try
            {
                for (int i = 0; i < totalEntries; i++)
                {
                    long key = i + 1;
                    if (!map.TryGet(key, readBuf, ref accessor))
                    {
                        notFound++;
                        if (notFound <= 10)
                        {
                            TestContext.Out.WriteLine($"  MISSING key={key}");
                        }
                    }
                    else
                    {
                        long stored = ((long*)readBuf)[0];
                        if (stored != key)
                        {
                            mismatched++;
                            if (mismatched <= 10)
                            {
                                TestContext.Out.WriteLine($"  MISMATCH key={key} got={stored}");
                            }
                        }
                    }
                }
            }
            finally
            {
                accessor.Dispose();
            }
        }

        Assert.That(notFound, Is.EqualTo(0),
            $"{notFound} keys could not be retrieved post-insertion — corruption confirmed.");
        Assert.That(mismatched, Is.EqualTo(0),
            $"{mismatched} keys returned wrong values — corruption confirmed.");
    }

    private static readonly string DiagLogPath = @"C:\Users\loicb\AppData\Local\Temp\typhon-bulkload-17m.log";

    private static int CountLogLines(string needle)
    {
        try
        {
            if (!File.Exists(DiagLogPath)) return 0;
            int count = 0;
            using var fs = new FileStream(DiagLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            string line;
            while ((line = sr.ReadLine()) != null)
            {
                if (line.Contains(needle)) count++;
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }
}
