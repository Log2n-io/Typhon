using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1205 at scale: one entity map pushed to hundreds of millions of entries, its lookup cost measured as it grows, a sample verified, then verified again
/// after a clean close and reopen. On demand only: it writes tens of GiB.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it proves.</b> The map's capacity (no wall below the bucket cap, ~2³⁰ buckets), constant lookup cost (one bucket chunk per lookup, whatever the
/// size — the directory it replaced cost ~25 ns per 4 032 buckets), and that splits, overflow moves and segment growth keep every entry findable under
/// concurrent inserts.
/// </para>
/// <para>
/// <b>Configuration</b> (environment, all optional): <c>TYPHON_EMSCALE_N</c> entries (500 000 000), <c>TYPHON_EMSCALE_VALUE</c> value size in bytes (4: the
/// smallest record, 20 entries per bucket, ~9 GiB at 500M; 23 is the Market item's entity record), <c>TYPHON_EMSCALE_CACHE_MIB</c> (12 288),
/// <c>TYPHON_EMSCALE_THREADS</c> inserting threads (8), <c>TYPHON_EMSCALE_DIR</c> (a directory under the temp folder). The database is deleted at the end,
/// pass or fail.
/// </para>
/// </remarks>
[TestFixture]
[Explicit("On-demand scale run: writes tens of GiB. Never run by CI.")]
[Category("Manual")]
[NonParallelizable]
unsafe class EntityMapScaleTests
{
    private const int N0 = 256;
    private const int Batch = 4096;

    /// <summary>
    /// Lookups per epoch guard. Every page a guard's reads touch stays pinned until it exits, so one guard over a whole verification pass pins as many
    /// pages as the pass reads — more than a cache smaller than the map holds. A reader holds an epoch per transaction, not per million reads.
    /// </summary>
    private const int LookupsPerEpoch = 1024;

    private static long Env(string name, long fallback)
        => long.TryParse(Environment.GetEnvironmentVariable("TYPHON_EMSCALE_" + name), out var v) ? v : fallback;

    private static void Log(string message) => TestContext.Progress.WriteLine($"[emscale {DateTime.Now:HH:mm:ss}] {message}");

    private static void WriteValue(long key, byte* value, int size)
    {
        *(long*)value = key;   // every value size the test takes is at least 4; the long overlaps a 4-byte value's neighbour only within the stack buffer
        for (var i = 8; i < size; i++)
        {
            value[i] = (byte)(key * 31 + i);
        }
    }

    private static bool ValueMatches(long key, byte* value, int size)
    {
        if (size >= 8 ? *(long*)value != key : *(int*)value != (int)key)
        {
            return false;
        }

        for (var i = 8; i < size; i++)
        {
            if (value[i] != (byte)(key * 31 + i))
            {
                return false;
            }
        }

        return true;
    }

    private static ServiceProvider BuildServices(string dir, long cacheMiB)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddSingleton<IWalFileIO>(new WalFileIO())
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = "EmScale";
                o.DatabaseDirectory = dir;
                o.DatabaseCacheSize = (ulong)cacheMiB << 20;
            })
            .AddScopedDatabaseEngine(o => o.Wal = new WalWriterOptions
            {
                WalDirectory = Path.Combine(dir, "wal"),
                SegmentSize = 16 << 20,
                PreAllocateSegments = 1,
            });
        return services.BuildServiceProvider();
    }

    [Test]
    public void AMapOfHundredsOfMillions_KeepsItsLookupCostAndEveryEntry()
    {
        var total = Env("N", 500_000_000);
        var valueSize = (int)Env("VALUE", 4);
        var cacheMiB = Env("CACHE_MIB", 12_288);
        var threads = (int)Env("THREADS", 8);
        var dir = Environment.GetEnvironmentVariable("TYPHON_EMSCALE_DIR") is { Length: > 0 } d ? d : Path.Combine(Path.GetTempPath(), "Typhon.EmScale");
        Assert.That(valueSize, Is.GreaterThanOrEqualTo(4));

        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }

        Directory.CreateDirectory(dir);
        Log($"entries={total:N0} value={valueSize} B cache={cacheMiB} MiB threads={threads} dir={dir}");

        var stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        int root;
        var sampleRng = new Random(1205);
        var sample = new long[1_000_000];
        try
        {
            var sp = BuildServices(dir, cacheMiB);
            sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
            using (var scope = sp.CreateScope())
            {
                var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
                dbe.InitializeArchetypes();
                var em = dbe.MMF.EpochManager;

                ChunkBasedSegment<PersistentStore> segment;
                RawValuePagedHashMap<long, PersistentStore> map;
                using (EpochGuard.Enter(em))
                {
                    segment = dbe.MMF.AllocateChunkBasedSegment(PageBlockType.None, 20, stride);
                    map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, N0, valueSize);
                }

                root = segment.RootPageIndex;
                Log($"bucket capacity {map.BucketCapacity}, stride {stride}");

                long nextBatch = 0;
                var lookupValue = stackalloc byte[Math.Max(8, valueSize)];
                long inserted = 0;
                foreach (var milestone in new[] { 1_000_000L, 10_000_000, 50_000_000, 100_000_000, 200_000_000, 300_000_000, 400_000_000, 500_000_000,
                             750_000_000, 1_000_000_000, 1_500_000_000, 2_000_000_000 })
                {
                    // Whole batches per phase: a batch cut at a phase's end would leave the rest of its keys to no one.
                    var target = Math.Min((milestone + Batch - 1) / Batch * Batch, total);
                    if (target <= inserted)
                    {
                        continue;
                    }

                    var sw = Stopwatch.StartNew();
                    var phaseStart = inserted;
                    var failure = (Exception)null;
                    var workers = new Thread[threads];
                    for (var t = 0; t < threads; t++)
                    {
                        workers[t] = new Thread(() =>
                        {
                            try
                            {
                                var value = stackalloc byte[Math.Max(8, valueSize)];
                                var cs = dbe.MMF.CreateChangeSet();
                                while (true)
                                {
                                    var b = Interlocked.Increment(ref nextBatch) - 1;
                                    var first = b * Batch + 1;
                                    if (first > target)
                                    {
                                        Interlocked.Decrement(ref nextBatch);
                                        return;
                                    }

                                    var last = Math.Min(target, first + Batch - 1);
                                    using (EpochGuard.Enter(em))
                                    {
                                        var accessor = segment.CreateChunkAccessor(cs);
                                        try
                                        {
                                            for (var k = first; k <= last; k++)
                                            {
                                                WriteValue(k, value, valueSize);
                                                map.InsertNew(k, value, ref accessor, cs);
                                            }
                                        }
                                        finally
                                        {
                                            accessor.Dispose();
                                        }
                                    }

                                    cs.ReleaseDirtyMarks();
                                }
                            }
                            catch (Exception e)
                            {
                                Volatile.Write(ref failure, e);
                            }
                        }) { IsBackground = true, Name = $"emscale-{t}" };
                        workers[t].Start();
                    }

                    while (!AllJoined(workers, TimeSpan.FromSeconds(30)))
                    {
                        var done = Math.Min(target, Interlocked.Read(ref nextBatch) * Batch);
                        Log($"  ~{done:N0} inserted, {map.BucketCount:N0} buckets, {(done - phaseStart) / sw.Elapsed.TotalSeconds:N0}/s");
                    }

                    if (failure != null)
                    {
                        Assert.Fail($"insert failed at ~{inserted:N0}: {failure}");
                    }

                    inserted = target;
                    var insertRate = (inserted - phaseStart) / sw.Elapsed.TotalSeconds;
                    Assert.That(map.EntryCount, Is.EqualTo(inserted), "every insert counted once");

                    // Lookup cost, single thread, random present keys.
                    var rng = new Random((int)(inserted % int.MaxValue));
                    const int lookups = 200_000;
                    long found = 0;
                    var lookup = Stopwatch.StartNew();
                    for (var done = 0; done < lookups; done += LookupsPerEpoch)
                    {
                        using var guard = EpochGuard.Enter(em);
                        var accessor = segment.CreateChunkAccessor();
                        var value = lookupValue;
                        for (var i = done; i < Math.Min(lookups, done + LookupsPerEpoch); i++)
                        {
                            var key = 1 + rng.NextInt64(inserted);
                            if (map.TryGet(key, value, ref accessor) && ValueMatches(key, value, valueSize))
                            {
                                found++;
                            }
                        }

                        accessor.Dispose();
                    }

                    var lookupNs = lookup.Elapsed.TotalMilliseconds * 1e6 / lookups;

                    // The same lookups over 4 096 keys, cycled: their chunks stay in the CPU's caches, so what is left is the map's own work per lookup —
                    // whatever the random lookups add on top is the memory hierarchy, which grows with the resident set and is no part of the structure.
                    var hot = new long[4096];
                    for (var i = 0; i < hot.Length; i++)
                    {
                        hot[i] = 1 + rng.NextInt64(inserted);
                    }

                    var hotWatch = Stopwatch.StartNew();
                    using (EpochGuard.Enter(em))
                    {
                        var accessor = segment.CreateChunkAccessor();
                        for (var i = 0; i < lookups; i++)
                        {
                            map.TryGet(hot[i & (hot.Length - 1)], lookupValue, ref accessor);
                        }

                        accessor.Dispose();
                    }

                    var hotNs = hotWatch.Elapsed.TotalMilliseconds * 1e6 / lookups;
                    var m = dbe.MMF.GetMetrics();
                    Log($"MILESTONE entries={inserted:N0} buckets={map.BucketCount:N0} load={map.LoadFactor:F3} lookup={lookupNs:F0} ns hot={hotNs:F0} ns "
                        + $"insert={insertRate:N0}/s "
                        + $"relocated={map._overflowChunksRelocated:N0} deferred={map._splitsDeferred:N0} pages={segment.Length:N0} "
                        + $"file={dbe.MMF.FileSize / (1024.0 * 1024 * 1024):F2} GiB read={m.ReadFromDiskCount:N0} written={m.PageWrittenToDiskCount:N0} "
                        + $"ws={Environment.WorkingSet / (1024.0 * 1024 * 1024):F2} GiB found={found}/{lookups}");
                    Assert.That(found, Is.EqualTo(lookups), $"at {inserted:N0} entries a present key was missing or held another value");

                    if (inserted >= total)
                    {
                        break;
                    }
                }

                // A sample to verify again after the reopen, and keys never inserted.
                for (var i = 0; i < sample.Length; i++)
                {
                    sample[i] = 1 + sampleRng.NextInt64(total);
                }

                VerifySample(map, segment, em, sample, total, valueSize, "before close");
                // The checkpoint's job in the engine: persist the entry count, through a change set so the close writes the page back.
                var flush = dbe.MMF.CreateChangeSet();
                using (EpochGuard.Enter(em))
                {
                    map.FlushMeta(flush);
                }

                flush.ReleaseDirtyMarks();

                Log("closing");
            }

            sp.Dispose();

            // Reopen: a clean close, so the map loads from its meta.
            var reopen = Stopwatch.StartNew();
            var sp2 = BuildServices(dir, cacheMiB);
            using (var scope = sp2.CreateScope())
            {
                var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
                dbe.InitializeArchetypes();
                var em = dbe.MMF.EpochManager;
                ChunkBasedSegment<PersistentStore> segment;
                RawValuePagedHashMap<long, PersistentStore> map;
                using (EpochGuard.Enter(em))
                {
                    segment = dbe.MMF.LoadChunkBasedSegment(root, stride);
                    map = RawValuePagedHashMap<long, PersistentStore>.Open(segment, N0, valueSize);
                }

                Log($"reopened in {reopen.Elapsed.TotalSeconds:F1} s: {map.EntryCount:N0} entries, {map.BucketCount:N0} buckets");
                Assert.That(map.EntryCount, Is.EqualTo(total));
                VerifySample(map, segment, em, sample, total, valueSize, "after reopen");
            }

            sp2.Dispose();
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
                Log("database deleted");
            }
            catch (Exception e)
            {
                Log($"could not delete {dir}: {e.Message}");
            }
        }
    }

    private static bool AllJoined(Thread[] workers, TimeSpan wait)
    {
        var deadline = Stopwatch.StartNew();
        foreach (var w in workers)
        {
            var left = wait - deadline.Elapsed;
            if (!w.Join(left < TimeSpan.Zero ? TimeSpan.Zero : left))
            {
                return false;
            }
        }

        return true;
    }

    private static void VerifySample(
        RawValuePagedHashMap<long, PersistentStore> map,
        ChunkBasedSegment<PersistentStore> segment,
        EpochManager em,
        long[] sample,
        long total,
        int valueSize,
        string when)
    {
        var sw = Stopwatch.StartNew();
        long bad = 0, absentFound = 0;
        var value = stackalloc byte[Math.Max(8, valueSize)];
        for (var done = 0; done < sample.Length; done += LookupsPerEpoch)
        {
            using var guard = EpochGuard.Enter(em);
            var accessor = segment.CreateChunkAccessor();
            for (var i = done; i < Math.Min(sample.Length, done + LookupsPerEpoch); i++)
            {
                if (!map.TryGet(sample[i], value, ref accessor) || !ValueMatches(sample[i], value, valueSize))
                {
                    bad++;
                }
            }

            accessor.Dispose();
        }

        for (var done = 0; done < 100_000; done += LookupsPerEpoch)
        {
            using var guard = EpochGuard.Enter(em);
            var accessor = segment.CreateChunkAccessor();
            for (var k = total + 1 + done; k <= total + Math.Min(100_000, done + LookupsPerEpoch); k++)
            {
                if (map.TryGet(k, value, ref accessor))
                {
                    absentFound++;
                }
            }

            accessor.Dispose();
        }

        Log($"verified {when}: {sample.Length:N0} present keys ({bad} bad), 100 000 absent ({absentFound} found) in {sw.Elapsed.TotalSeconds:F1} s");
        Assert.That(bad, Is.Zero, $"{when}: present keys missing or wrong");
        Assert.That(absentFound, Is.Zero, $"{when}: absent keys found");
    }
}
