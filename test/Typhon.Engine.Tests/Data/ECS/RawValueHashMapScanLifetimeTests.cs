using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace Typhon.Engine.Tests;

/// <summary>
/// The scan buffer <see cref="RawValuePagedHashMap{TKey,TStore}.ForEachEntry{TAction}"/> copies each bucket into must stay alive for the whole scan.
/// </summary>
/// <remarks>
/// <para>It used to live on the pinned object heap, reached only through a raw pointer. Pinning stops the GC moving an array; it does not stop it freeing
/// one. In optimised code the array's local is dead from the line that took the pointer, so a gen2 collection during the scan — the callback is where
/// the caller allocates — freed the buffer while the scan kept copying bucket after bucket into it.</para>
/// <para>That is the SWG Tatooine x64 crash: <c>Internal CLR error (0x80131506)</c> inside the NEXT scan's pinned allocation, one run in three, with a
/// managed heap <c>verifyheap</c> finds intact — the stale copies land on freed memory, not on an object header.</para>
/// <para>The buffer is now on the stack, spilling to native memory for a long overflow chain; this test fails if it goes back to managed memory
/// reached by pointer.</para>
/// <para><b>Release only.</b> A Debug build of the engine keeps every local alive to the end of its method, so the old bug could not occur there and this
/// test passes either way. The merge gate runs Release, which is where it bites. The scan is long enough for on-stack replacement to move a first call
/// off tier 0 before the collection is forced.</para>
/// </remarks>
[TestFixture]
unsafe class RawValueHashMapScanLifetimeTests
{
    private ServiceProvider _serviceProvider;
    private string CurrentDatabaseName => $"{TestContext.CurrentContext.Test.Name.Replace("(", "_").Replace(")", "_").Replace(",", "_")}_database";

    [SetUp]
    public void Setup()
    {
        var services = new ServiceCollection();
        services
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 16;
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => _serviceProvider?.Dispose();

    /// <summary>
    /// Forces a full collection in the middle of a scan, then re-occupies freed pinned memory with a known pattern. A buffer freed by that collection
    /// would take some of the pattern arrays, and the scan's next copies would overwrite them.
    /// </summary>
    private struct CollectMidScan : RawValuePagedHashMap<long, PersistentStore>.IEntryAction<long>
    {
        public int Seen;
        public int CollectAt;
        public int WrongValues;
        public List<byte[]> Sentinels;

        public bool Process(long key, byte* value)
        {
            if (++Seen == CollectAt)
            {
                // A blocking gen2 collection is the one that sweeps the pinned object heap.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                for (var i = 0; i < 4096; i++)
                {
                    var sentinel = GC.AllocateArray<byte>(48, pinned: true);
                    sentinel.AsSpan().Fill(0xA5);
                    Sentinels.Add(sentinel);
                }
            }

            // Every record starts with its own key, so a value read out of recycled memory shows up here as well.
            if (*(long*)value != key)
            {
                WrongValues++;
            }

            return true;
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public void ForEachEntry_GcDuringScan_KeepsItsBufferAlive()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        const int valueSize = 26;
        const int total = 60_000;
        var stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);
        var map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, 256, valueSize);

        var record = stackalloc byte[valueSize];
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            for (long k = 1; k <= total; k++)
            {
                *(long*)record = k;
                map.Insert(k, record, ref accessor, null);
            }

            accessor.Dispose();
        }

        var probe = new CollectMidScan { CollectAt = total * 3 / 4, Sentinels = new List<byte[]>(4096) };
        int visited;
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            visited = map.ForEachEntry(ref accessor, ref probe);
            accessor.Dispose();
        }

        var overwritten = 0;
        foreach (var sentinel in probe.Sentinels)
        {
            if (sentinel.AsSpan().IndexOfAnyExcept((byte)0xA5) >= 0)
            {
                overwritten++;
            }
        }

        Assert.That(visited, Is.EqualTo(total));
        Assert.That(overwritten, Is.Zero, "the scan wrote into pinned arrays allocated after its buffer should have been freed — its buffer was");
        Assert.That(probe.WrongValues, Is.Zero, "the scan handed out values read from memory it no longer owned");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // A chain longer than the stack buffer: the scan spills to native memory
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Stride 256: 7 entries a chunk, so the scan's stack buffer holds 14, two chunks. A longer chain spills to native memory at the 15th entry and
    /// re-spills at the 29th and 57th.
    /// </summary>
    private const int ChainValueSize = 26;

    /// <summary>
    /// Keys whose hashes share their low 12 bits. A 256-bucket map this small addresses buckets with fewer bits than that, so the keys share one bucket and
    /// build one overflow chain, whatever splits happen.
    /// </summary>
    private static long[] CollidingKeys(int count)
    {
        var keys = new long[count];
        var lowBits = RawValuePagedHashMap<long, PersistentStore>.ComputeHashForTest(1) & 0xFFF;
        var found = 0;
        for (long k = 1; found < count; k++)
        {
            if ((RawValuePagedHashMap<long, PersistentStore>.ComputeHashForTest(k) & 0xFFF) == lowBits)
            {
                keys[found++] = k;
            }
        }

        return keys;
    }

    private static RawValuePagedHashMap<long, PersistentStore> BuildChain(ManagedPagedMMF mpmmf, EpochManager em, long[] keys,
        out ChunkBasedSegment<PersistentStore> segment)
    {
        segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(ChainValueSize));
        var map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, 256, ChainValueSize);
        var record = stackalloc byte[ChainValueSize];
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            foreach (var k in keys)
            {
                *(long*)record = k;
                map.Insert(k, record, ref accessor, null);
            }

            var bucket = map.BucketIndexOf(keys[0]);
            foreach (var k in keys)
            {
                Assert.That(map.BucketIndexOf(k), Is.EqualTo(bucket), "the keys were chosen to share one bucket");
            }

            var chunks = 0;
            for (var chunkId = map.GetBucketChunkIdForTest(bucket, ref accessor); chunkId >= 0;
                 chunkId = RawValuePagedHashMap<long, PersistentStore>.BucketOverflowChunkIdForTest(chunkId, ref accessor))
            {
                chunks++;
            }

            Assert.That(chunks, Is.GreaterThan(4), "the chain must outgrow the stack buffer, then the first native one");
            accessor.Dispose();
        }

        return map;
    }

    /// <summary>Checks every value against its key, and can stop or throw at a given call.</summary>
    private struct CheckValues : RawValuePagedHashMap<long, PersistentStore>.IEntryAction<long>
    {
        public int Seen;
        public int StopAt;
        public int ThrowAt;
        public int WrongValues;

        public bool Process(long key, byte* value)
        {
            if (++Seen == ThrowAt)
            {
                throw new InvalidOperationException("thrown by the callback");
            }

            if (*(long*)value != key)
            {
                WrongValues++;
            }

            return Seen != StopAt;
        }
    }

    [Test]
    public void ForEachEntry_LongChain_SpillsAndVisitsAll()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        var keys = CollidingKeys(100);
        var map = BuildChain(mpmmf, em, keys, out var segment);

        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            var probe = new CheckValues();
            Assert.That(map.ForEachEntry(ref accessor, ref probe), Is.EqualTo(keys.Length));
            Assert.That(probe.WrongValues, Is.Zero);
            accessor.Dispose();
        }
    }

    [Test]
    public void ForEachEntry_StopInSpilledChain_ReturnsCount()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        var keys = CollidingKeys(100);
        var map = BuildChain(mpmmf, em, keys, out var segment);

        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            var stop = new CheckValues { StopAt = 60 };
            Assert.That(map.ForEachEntry(ref accessor, ref stop), Is.EqualTo(59), "the entry the callback declined is not counted");

            var full = new CheckValues();
            Assert.That(map.ForEachEntry(ref accessor, ref full), Is.EqualTo(keys.Length), "a later scan starts over");
            Assert.That(full.WrongValues, Is.Zero);
            accessor.Dispose();
        }
    }

    [Test]
    public void ForEachEntry_ThrowInSpilledChain_Propagates()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        var keys = CollidingKeys(100);
        var map = BuildChain(mpmmf, em, keys, out var segment);

        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            var thrower = new CheckValues { ThrowAt = 60 };
            var threw = false;
            try
            {
                map.ForEachEntry(ref accessor, ref thrower);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            Assert.That(threw, Is.True);

            var full = new CheckValues();
            Assert.That(map.ForEachEntry(ref accessor, ref full), Is.EqualTo(keys.Length), "a later scan starts over");
            Assert.That(full.WrongValues, Is.Zero);
            accessor.Dispose();
        }
    }
}
