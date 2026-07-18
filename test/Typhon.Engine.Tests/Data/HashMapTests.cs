using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

unsafe class PagedHashMapTests
{
    private IServiceProvider _serviceProvider;

    private string CurrentDatabaseName => $"{TestContext.CurrentContext.Test.Name}_database";

    [SetUp]
    public void Setup()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection
            .AddLogging(builder =>
            {
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.IncludeScopes = true;
                    options.TimestampFormat = "mm:ss.fff ";
                });
                builder.SetMinimumLevel(LogLevel.Information);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = (ulong)(PagedMMF.MinimumMemPageCount * PagedMMF.PageSize);
                options.PagesDebugPattern = true;
            });

        _serviceProvider = serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    // ═══════════════════════════════════════════════════════════════════════
    // Struct size verification
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void StructSizes_MetaAndDirectory_256Bytes()
    {
        Assert.That(sizeof(PagedHashMapMeta), Is.EqualTo(256));
        Assert.That(sizeof(PagedHashMapDirectory), Is.EqualTo(256));
        Assert.That(sizeof(OverflowDirIndex), Is.EqualTo(256));
        Assert.That(sizeof(PagedHashMapBucketHeader), Is.EqualTo(12));
    }

    [Test]
    public void BucketCapacity_Formula_MatchesExpected()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();

        var segment32 = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map32 = PagedHashMap<int, int, PersistentStore>.Create(segment32, 4);
        Assert.That(map32.BucketCapacity, Is.EqualTo(30), "L32: (256-12)/(4+4) = 30");

        var segment64 = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map64 = PagedHashMap<long, int, PersistentStore>.Create(segment64, 4);
        Assert.That(map64.BucketCapacity, Is.EqualTo(20), "L64: (256-12)/(8+4) = 20");
    }

    [Test]
    public void RecommendedStride_ReturnsCorrectStride()
    {
        Assert.That(PagedHashMap<int, int, PersistentStore>.RecommendedStride(), Is.EqualTo(256));
        Assert.That(PagedHashMap<long, int, PersistentStore>.RecommendedStride(), Is.EqualTo(256));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PackMeta / UnpackMeta
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void PackUnpackMeta_Roundtrip()
    {
        long packed = PagedHashMapBase<PersistentStore>.PackMeta(3, 100, 256);
        var (level, next, bucketCount) = PagedHashMapBase<PersistentStore>.UnpackMeta(packed);

        Assert.That(level, Is.EqualTo(3));
        Assert.That(next, Is.EqualTo(100));
        Assert.That(bucketCount, Is.EqualTo(256));
    }

    [Test]
    public void PackUnpackMeta_MaxValues()
    {
        // Level max: 255, Next max: 16,777,215 (24 bits), BucketCount max: int.MaxValue
        long packed = PagedHashMapBase<PersistentStore>.PackMeta(255, 0x00FFFFFF, int.MaxValue);
        var (level, next, bucketCount) = PagedHashMapBase<PersistentStore>.UnpackMeta(packed);

        Assert.That(level, Is.EqualTo(255));
        Assert.That(next, Is.EqualTo(0x00FFFFFF));
        Assert.That(bucketCount, Is.EqualTo(int.MaxValue));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ResolveBucket
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ResolveBucket_Level0_WithinN0()
    {
        // level=0, next=0, n0=64 → bucket = hash & 63
        int n0 = 64;
        for (uint hash = 0; hash < 1000; hash++)
        {
            int bucket = PagedHashMapBase<PersistentStore>.ResolveBucket(hash, 0, 0, n0);
            Assert.That(bucket, Is.GreaterThanOrEqualTo(0).And.LessThan(n0));
        }
    }

    [Test]
    public void ResolveBucket_WithSplit_RemapsCorrectly()
    {
        // level=0, next=32, n0=64 → buckets 0..31 already split, use finer modulus (0..127)
        int n0 = 64;
        int level = 0;
        int next = 32;
        int mod = n0 << level;       // 64
        int upperBound = mod << 1;   // 128

        for (uint hash = 0; hash < 10000; hash++)
        {
            int bucket = PagedHashMapBase<PersistentStore>.ResolveBucket(hash, level, next, n0);
            int baseBucket = (int)(hash & (uint)(mod - 1));

            if (baseBucket < next)
            {
                // Already split — should use finer modulus, result in [0, 128)
                Assert.That(bucket, Is.GreaterThanOrEqualTo(0).And.LessThan(upperBound));
            }
            else
            {
                // Not yet split — base modulus, result in [next, mod)
                Assert.That(bucket, Is.EqualTo(baseBucket));
                Assert.That(bucket, Is.GreaterThanOrEqualTo(next).And.LessThan(mod));
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Create / Open
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Create_L32_InitializesMeta()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment);

        Assert.That(map.N0, Is.EqualTo(64));
        Assert.That(map.BucketCount, Is.EqualTo(64));
        Assert.That(map.EntryCount, Is.EqualTo(0));
        Assert.That(map.LoadFactor, Is.EqualTo(0.0).Within(0.001));
        Assert.That(map.BucketCapacity, Is.EqualTo(30));
        Assert.That(map.Segment, Is.SameAs(segment));
    }

    [Test]
    public void CreateThenOpen_L32_PreservesMeta()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var created = PagedHashMap<int, int, PersistentStore>.Create(segment, 32);

        Assert.That(created.N0, Is.EqualTo(32));
        Assert.That(created.BucketCount, Is.EqualTo(32));

        var opened = PagedHashMap<int, int, PersistentStore>.Open(segment);

        Assert.That(opened.N0, Is.EqualTo(32));
        Assert.That(opened.BucketCount, Is.EqualTo(32));
        Assert.That(opened.EntryCount, Is.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Hash distribution
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void HashDistribution_L32_ReasonableSpread()
    {
        int bucketCount = 64;
        var counts = new int[bucketCount];

        for (int key = 0; key < 1000; key++)
        {
            uint hash = PagedHashMap<int, int, PersistentStore>.ComputeHashForTest(key);
            int bucket = PagedHashMapBase<PersistentStore>.ResolveBucket(hash, 0, 0, bucketCount);
            counts[bucket]++;
        }

        // Expect: ~15.6 per bucket on average (1000/64). Verify no bucket has 0 (dead bucket)
        // and no bucket has more than 4× the average (would indicate severe clustering).
        double avg = 1000.0 / bucketCount;
        int emptyBuckets = 0;
        int overloadedBuckets = 0;

        for (int i = 0; i < bucketCount; i++)
        {
            if (counts[i] == 0) emptyBuckets++;
            if (counts[i] > avg * 4) overloadedBuckets++;
        }

        Assert.That(emptyBuckets, Is.EqualTo(0), "Wang/Jenkins should not produce empty buckets for 1000 sequential ints into 64 buckets");
        Assert.That(overloadedBuckets, Is.EqualTo(0), "No bucket should have >4× average entries");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Bucket initialization verification
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Create_L32_BucketsInitializedCorrectly()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 8);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            // Verify each bucket has correct initial state
            for (int b = 0; b < 8; b++)
            {
                int chunkId = map.GetBucketChunkIdForTest(b, ref accessor);
                ref readonly var header = ref Unsafe.AsRef<PagedHashMapBucketHeader>(accessor.GetChunkAddress(chunkId));

                Assert.That(header.OlcVersion, Is.EqualTo(4), $"Bucket {b}: OlcVersion should be 4 (version=1)");
                Assert.That(header.EntryCount, Is.EqualTo(0), $"Bucket {b}: should be empty");
                Assert.That(header.OverflowChunkId, Is.EqualTo(-1), $"Bucket {b}: no overflow");
            }

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 2 — TryGet read path
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void TryGet_L32_EmptyMap_ReturnsFalse()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 8);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            bool found = map.TryGet(42, out int value, ref accessor);

            Assert.That(found, Is.False);
            Assert.That(value, Is.EqualTo(0));
            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void TryGet_L32_SingleEntry_FindsKey()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 8);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            map.SeedEntryForTest(100, 999, ref accessor);

            bool found = map.TryGet(100, out int value, ref accessor);

            Assert.That(found, Is.True);
            Assert.That(value, Is.EqualTo(999));
            Assert.That(map.EntryCount, Is.EqualTo(1));
            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void TryGet_L32_MissForDifferentKey()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 8);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            map.SeedEntryForTest(100, 999, ref accessor);

            bool found = map.TryGet(200, out int value, ref accessor);

            Assert.That(found, Is.False);
            Assert.That(value, Is.EqualTo(0));
            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void TryGet_L32_MultipleEntries_CorrectRouting()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 8);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            // Seed 5 entries — different keys will route to different buckets
            map.SeedEntryForTest(10, 100, ref accessor);
            map.SeedEntryForTest(20, 200, ref accessor);
            map.SeedEntryForTest(30, 300, ref accessor);
            map.SeedEntryForTest(40, 400, ref accessor);
            map.SeedEntryForTest(50, 500, ref accessor);

            // All 5 should be found with correct values
            Assert.That(map.TryGet(10, out int v1, ref accessor), Is.True);
            Assert.That(v1, Is.EqualTo(100));

            Assert.That(map.TryGet(20, out int v2, ref accessor), Is.True);
            Assert.That(v2, Is.EqualTo(200));

            Assert.That(map.TryGet(30, out int v3, ref accessor), Is.True);
            Assert.That(v3, Is.EqualTo(300));

            Assert.That(map.TryGet(40, out int v4, ref accessor), Is.True);
            Assert.That(v4, Is.EqualTo(400));

            Assert.That(map.TryGet(50, out int v5, ref accessor), Is.True);
            Assert.That(v5, Is.EqualTo(500));

            // Absent key should miss
            Assert.That(map.TryGet(99, out _, ref accessor), Is.False);

            Assert.That(map.EntryCount, Is.EqualTo(5));
            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void TryGet_L32_FullBucket_ScansAllEntries()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        int n0 = 8;
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, n0);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            // Find 30 keys that all map to the same bucket (bucket 0)
            int capacity = map.BucketCapacity;
            var sameBucketKeys = FindKeysForBucket(0, n0, capacity);

            // Seed all entries into the same bucket
            for (int i = 0; i < capacity; i++)
            {
                map.SeedEntryForTest(sameBucketKeys[i], i * 10, ref accessor);
            }

            // Find the last entry (worst-case: scan all slots)
            int lastKey = sameBucketKeys[capacity - 1];
            bool found = map.TryGet(lastKey, out int value, ref accessor);

            Assert.That(found, Is.True);
            Assert.That(value, Is.EqualTo((capacity - 1) * 10));

            // Also verify the first entry is still findable
            Assert.That(map.TryGet(sameBucketKeys[0], out int firstVal, ref accessor), Is.True);
            Assert.That(firstVal, Is.EqualTo(0));

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void TryGet_L32_OverflowChain_FindsInOverflow()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        int n0 = 8;
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, n0);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            // Pick a target bucket and find a key for it
            int targetBucket = 3;
            var keys = FindKeysForBucket(targetBucket, n0, 2);
            int primaryKey = keys[0];
            int overflowKey = keys[1];

            // Seed an entry in the primary bucket
            map.SeedEntryForTest(primaryKey, 111, ref accessor);

            // Manually create an overflow chunk and link it
            int primaryChunkId = map.GetBucketChunkIdForTest(targetBucket, ref accessor);
            int overflowChunkId = segment.AllocateChunk(true, null);

            // Initialize the overflow bucket via pointer arithmetic
            byte* ovAddr = accessor.GetChunkAddress(overflowChunkId, true);
            ref var ovHeader = ref Unsafe.AsRef<PagedHashMapBucketHeader>(ovAddr);
            ovHeader.OlcVersion = 4;
            ovHeader.EntryCount = 1;
            ovHeader.Flags = 0;
            ovHeader.Reserved = 0;
            ovHeader.OverflowChunkId = -1;
            int keysOffset = sizeof(PagedHashMapBucketHeader);
            int valuesOffset = keysOffset + map.BucketCapacity * sizeof(int);
            *(int*)(ovAddr + keysOffset) = overflowKey;
            *(int*)(ovAddr + valuesOffset) = 222;

            // Link primary → overflow
            byte* primaryAddr = accessor.GetChunkAddress(primaryChunkId, true);
            Unsafe.AsRef<PagedHashMapBucketHeader>(primaryAddr).OverflowChunkId = overflowChunkId;

            // Find the entry in the overflow chunk
            bool found = map.TryGet(overflowKey, out int value, ref accessor);

            Assert.That(found, Is.True);
            Assert.That(value, Is.EqualTo(222));

            // Primary entry still findable
            Assert.That(map.TryGet(primaryKey, out int pVal, ref accessor), Is.True);
            Assert.That(pVal, Is.EqualTo(111));

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void TryGet_L64_SingleEntry_FindsKey()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<long, int, PersistentStore>.Create(segment, 8);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            map.SeedEntryForTest(1000L, 42, ref accessor);

            bool found = map.TryGet(1000L, out int value, ref accessor);

            Assert.That(found, Is.True);
            Assert.That(value, Is.EqualTo(42));
            Assert.That(map.EntryCount, Is.EqualTo(1));

            // Miss on different key
            Assert.That(map.TryGet(2000L, out _, ref accessor), Is.False);

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void TryGet_L32_KeyZero_And_ValueZero()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 8);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            // key=0 with value=0 — must be distinguishable from not-found
            map.SeedEntryForTest(0, 0, ref accessor);
            // key=1 with value=0
            map.SeedEntryForTest(1, 0, ref accessor);

            bool found0 = map.TryGet(0, out int val0, ref accessor);
            Assert.That(found0, Is.True, "key=0 should be found");
            Assert.That(val0, Is.EqualTo(0));

            bool found1 = map.TryGet(1, out int val1, ref accessor);
            Assert.That(found1, Is.True, "key=1 with value=0 should be found");
            Assert.That(val1, Is.EqualTo(0));

            // key=2 not present — same default value but found=false
            bool found2 = map.TryGet(2, out int val2, ref accessor);
            Assert.That(found2, Is.False, "key=2 should not be found");
            Assert.That(val2, Is.EqualTo(0), "out value on miss should be default(0)");

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 3 — Write path
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Insert_L32_SingleEntry_ThenTryGet()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 8);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            bool inserted = map.Insert(42, 999, ref accessor, null);
            Assert.That(inserted, Is.True);
            Assert.That(map.EntryCount, Is.EqualTo(1));

            bool found = map.TryGet(42, out int value, ref accessor);
            Assert.That(found, Is.True);
            Assert.That(value, Is.EqualTo(999));

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void Insert_L32_DuplicateKey_ReturnsFalse()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 8);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            Assert.That(map.Insert(42, 100, ref accessor, null), Is.True);
            Assert.That(map.Insert(42, 200, ref accessor, null), Is.False);
            Assert.That(map.EntryCount, Is.EqualTo(1));

            map.TryGet(42, out int value, ref accessor);
            Assert.That(value, Is.EqualTo(100)); // original value preserved

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void Insert_L32_MultipleEntries_AllRetrievable()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 8);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            for (int i = 0; i < 20; i++)
            {
                Assert.That(map.Insert(i, i * 10, ref accessor, null), Is.True);
            }
            Assert.That(map.EntryCount, Is.EqualTo(20));

            for (int i = 0; i < 20; i++)
            {
                Assert.That(map.TryGet(i, out int val, ref accessor), Is.True, $"Key {i} not found");
                Assert.That(val, Is.EqualTo(i * 10));
            }

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void Insert_L32_OverflowChain_AllocatesOverflow()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        int n0 = 8;
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, n0);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            // 31 keys all hashing to bucket 0 — 30 fill primary, 31st triggers overflow
            var keys = FindKeysForBucket(0, n0, 31);
            for (int i = 0; i < 31; i++)
            {
                Assert.That(map.Insert(keys[i], i, ref accessor, null), Is.True);
            }

            // All 31 entries retrievable
            for (int i = 0; i < 31; i++)
            {
                Assert.That(map.TryGet(keys[i], out int val, ref accessor), Is.True, $"Key at index {i} not found");
                Assert.That(val, Is.EqualTo(i));
            }
            Assert.That(map.EntryCount, Is.EqualTo(31));

            // Verify overflow exists
            int chunkId = map.GetBucketChunkIdForTest(0, ref accessor);
            ref readonly var header = ref Unsafe.AsRef<PagedHashMapBucketHeader>(accessor.GetChunkAddress(chunkId));
            Assert.That(header.OverflowChunkId, Is.Not.EqualTo(-1), "Overflow should exist");

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void Remove_L32_ExistingKey_ReturnsTrue()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 8);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            map.Insert(42, 999, ref accessor, null);

            bool removed = map.Remove(42, out int value, ref accessor, null);
            Assert.That(removed, Is.True);
            Assert.That(value, Is.EqualTo(999));
            Assert.That(map.EntryCount, Is.EqualTo(0));
            Assert.That(map.TryGet(42, out _, ref accessor), Is.False);

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void Remove_L32_NonExistentKey_ReturnsFalse()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 8);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            bool removed = map.Remove(42, out _, ref accessor, null);
            Assert.That(removed, Is.False);
            Assert.That(map.EntryCount, Is.EqualTo(0));

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void Remove_L32_LastEntryInOverflow_FreesChunk()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        int n0 = 8;
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, n0);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            var keys = FindKeysForBucket(0, n0, 31);
            for (int i = 0; i < 31; i++)
            {
                map.Insert(keys[i], i, ref accessor, null);
            }

            // Remove the entry in overflow (31st entry = keys[30])
            Assert.That(map.Remove(keys[30], out int val, ref accessor, null), Is.True);
            Assert.That(val, Is.EqualTo(30));
            Assert.That(map.EntryCount, Is.EqualTo(30));

            // Overflow should be freed — primary now has 30 entries, no overflow
            int chunkId = map.GetBucketChunkIdForTest(0, ref accessor);
            ref readonly var bHeader = ref Unsafe.AsRef<PagedHashMapBucketHeader>(accessor.GetChunkAddress(chunkId));
            Assert.That(bHeader.OverflowChunkId, Is.EqualTo(-1), "Overflow should be freed");

            // All 30 remaining entries still retrievable
            for (int i = 0; i < 30; i++)
            {
                Assert.That(map.TryGet(keys[i], out _, ref accessor), Is.True, $"Key at index {i} lost after remove");
            }

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void Remove_L32_SwapWithLast_PreservesOtherEntries()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        int n0 = 8;
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, n0);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            var keys = FindKeysForBucket(0, n0, 3);
            map.Insert(keys[0], 100, ref accessor, null);
            map.Insert(keys[1], 200, ref accessor, null);
            map.Insert(keys[2], 300, ref accessor, null);

            // Remove middle entry — last entry swaps into its slot
            Assert.That(map.Remove(keys[1], out int val, ref accessor, null), Is.True);
            Assert.That(val, Is.EqualTo(200));
            Assert.That(map.EntryCount, Is.EqualTo(2));

            // Other two entries intact
            Assert.That(map.TryGet(keys[0], out int v0, ref accessor), Is.True);
            Assert.That(v0, Is.EqualTo(100));
            Assert.That(map.TryGet(keys[2], out int v2, ref accessor), Is.True);
            Assert.That(v2, Is.EqualTo(300));

            // Removed entry not found
            Assert.That(map.TryGet(keys[1], out _, ref accessor), Is.False);

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void Upsert_L32_NewKey_InsertsAndReturnsTrue()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 8);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            bool inserted = map.Upsert(42, 100, ref accessor, null);
            Assert.That(inserted, Is.True);
            Assert.That(map.EntryCount, Is.EqualTo(1));

            map.TryGet(42, out int val, ref accessor);
            Assert.That(val, Is.EqualTo(100));

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void Upsert_L32_ExistingKey_UpdatesAndReturnsFalse()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 8);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            map.Insert(42, 100, ref accessor, null);

            bool inserted = map.Upsert(42, 200, ref accessor, null);
            Assert.That(inserted, Is.False); // updated, not inserted
            Assert.That(map.EntryCount, Is.EqualTo(1));

            map.TryGet(42, out int val, ref accessor);
            Assert.That(val, Is.EqualTo(200)); // new value

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void Insert_L64_Basic_Roundtrip()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<long, int, PersistentStore>.Create(segment, 8);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            Assert.That(map.Insert(1000L, 42, ref accessor, null), Is.True);
            Assert.That(map.Insert(long.MaxValue, 99, ref accessor, null), Is.True);
            Assert.That(map.EntryCount, Is.EqualTo(2));

            Assert.That(map.TryGet(1000L, out int v1, ref accessor), Is.True);
            Assert.That(v1, Is.EqualTo(42));
            Assert.That(map.TryGet(long.MaxValue, out int v2, ref accessor), Is.True);
            Assert.That(v2, Is.EqualTo(99));

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 4 — Split
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Insert_L32_ExceedLoadFactor_TriggersSplit()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        int n0 = 4;
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, n0);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            // threshold = 4 * 30 * 0.75 = 90. Insert 100 to guarantee at least one split.
            for (int i = 0; i < 100; i++)
            {
                map.Insert(i, i, ref accessor, null);
            }

            Assert.That(map.BucketCount, Is.GreaterThan(n0), "Split should have increased bucket count");
            Assert.That(map._splitCount, Is.GreaterThan(0), "At least one split should have occurred");

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void Split_L32_AllEntriesPreserved()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        int n0 = 4;
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, n0);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            int count = 200;
            for (int i = 0; i < count; i++)
            {
                map.Insert(i, i * 10, ref accessor, null);
            }

            // All entries still retrievable after multiple splits
            for (int i = 0; i < count; i++)
            {
                Assert.That(map.TryGet(i, out int val, ref accessor), Is.True, $"Key {i} not found after splits");
                Assert.That(val, Is.EqualTo(i * 10));
            }
            Assert.That(map.EntryCount, Is.EqualTo(count));

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void Split_L32_MultipleSplits_CorrectBucketCount()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        int n0 = 4;
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, n0);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            for (int i = 0; i < 300; i++)
            {
                map.Insert(i, i, ref accessor, null);
            }

            Assert.That(map._splitCount, Is.GreaterThanOrEqualTo(5));
            Assert.That(map.BucketCount, Is.EqualTo(n0 + (int)map._splitCount));

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void Split_L32_LevelWrap_NextResetsToZero()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        int n0 = 4;
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, n0);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            for (int i = 0; i < 200; i++)
            {
                map.Insert(i, i, ref accessor, null);
            }

            // With N0=4, after 4 splits level wraps: bucketCount doubles from N0
            Assert.That(map._splitCount, Is.GreaterThanOrEqualTo(n0));
            Assert.That(map.BucketCount, Is.GreaterThanOrEqualTo(n0 * 2), "Level should have wrapped at least once");

            // Verify all entries still retrievable
            for (int i = 0; i < 200; i++)
            {
                Assert.That(map.TryGet(i, out int val, ref accessor), Is.True, $"Key {i} not found");
                Assert.That(val, Is.EqualTo(i));
            }

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void Split_L32_OverflowFreedDuringSplit()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        int n0 = 4;
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, n0);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            // Force 31 entries into bucket 0 (creates overflow)
            var bucket0Keys = FindKeysForBucket(0, n0, 31);
            for (int i = 0; i < 31; i++)
            {
                map.Insert(bucket0Keys[i], i, ref accessor, null);
            }

            // Verify overflow exists before split
            int chunkId = map.GetBucketChunkIdForTest(0, ref accessor);
            ref readonly var bHeader = ref Unsafe.AsRef<PagedHashMapBucketHeader>(accessor.GetChunkAddress(chunkId));
            Assert.That(bHeader.OverflowChunkId, Is.Not.EqualTo(-1), "Overflow should exist before split");

            // Insert more entries to trigger split (need total > 4*30*0.75 = 90)
            int inserted = 0;
            for (int candidate = 10000; inserted < 60; candidate++)
            {
                uint hash = PagedHashMap<int, int, PersistentStore>.ComputeHashForTest(candidate);
                int b = PagedHashMapBase<PersistentStore>.ResolveBucket(hash, 0, 0, n0);
                if (b != 0)
                {
                    map.Insert(candidate, candidate, ref accessor, null);
                    inserted++;
                }
            }

            // Split should have happened on bucket 0 (next=0 initially)
            Assert.That(map._splitCount, Is.GreaterThanOrEqualTo(1));

            // All bucket-0 entries still retrievable after redistribution
            for (int i = 0; i < 31; i++)
            {
                Assert.That(map.TryGet(bucket0Keys[i], out _, ref accessor), Is.True,
                    $"Bucket0 key {i} not found after split");
            }

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void Split_L32_DirectoryGrows_WhenNeeded()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 20, 256);
        int n0 = 4;
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, n0);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            // Need >64 buckets to force directory growth. With N0=4 and LF=0.75:
            // ~1500 entries → ~67 buckets → 2 directory chunks
            for (int i = 0; i < 1500; i++)
            {
                map.Insert(i, i, ref accessor, null);
            }

            Assert.That(map.BucketCount, Is.GreaterThan(64), "Should exceed single directory chunk");
            Assert.That(map.EntryCount, Is.EqualTo(1500));

            // Verify all entries retrievable after directory growth
            for (int i = 0; i < 1500; i++)
            {
                Assert.That(map.TryGet(i, out int val, ref accessor), Is.True, $"Key {i} not found");
                Assert.That(val, Is.EqualTo(i));
            }

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Test helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Find N keys (int) that all resolve to the same bucket via Wang/Jenkins hash at level=0, next=0.
    /// </summary>
    private static int[] FindKeysForBucket(int targetBucket, int n0, int count)
    {
        var result = new int[count];
        int found = 0;

        for (int candidate = 0; found < count; candidate++)
        {
            uint hash = PagedHashMap<int, int, PersistentStore>.ComputeHashForTest(candidate);
            int bucket = PagedHashMapBase<PersistentStore>.ResolveBucket(hash, 0, 0, n0);
            if (bucket == targetBucket)
            {
                result[found++] = candidate;
            }
        }

        return result;
    }

    /// <summary>
    /// Find N keys (long) that all resolve to the same bucket via xxHash32 at level=0, next=0.
    /// </summary>
    private static long[] FindKeysForBucket64(int targetBucket, int n0, int count)
    {
        var result = new long[count];
        int found = 0;

        for (long candidate = 0; found < count; candidate++)
        {
            uint hash = PagedHashMap<long, int, PersistentStore>.ComputeHashForTest(candidate);
            int bucket = PagedHashMapBase<PersistentStore>.ResolveBucket(hash, 0, 0, n0);
            if (bucket == targetBucket)
            {
                result[found++] = candidate;
            }
        }

        return result;
    }

    private void LogDiagnostics(PagedHashMapBase<PersistentStore> map)
    {
        TestContext.Out.WriteLine(
            $"Splits={map._splitCount} OlcRestarts={map._olcRestarts} WriteLockFails={map._writeLockFailures} " +
            $"Entries={map.EntryCount} Buckets={map.BucketCount} LoadFactor={map.LoadFactor:F3}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 5 — Recovery & Diagnostics
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void VerifyIntegrity_L32_EmptyMap_ReturnsTrue()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 64);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            Assert.That(map.VerifyIntegrity(ref accessor), Is.True);
            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void VerifyIntegrity_L32_AfterInserts_ReturnsTrue()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 8);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            for (int i = 0; i < 100; i++)
            {
                map.Insert(i, i * 10, ref accessor, null);
            }

            Assert.That(map.VerifyIntegrity(ref accessor), Is.True);
            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void GetStats_L32_EmptyMap_AllZeros()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 64);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            var stats = map.GetStats(ref accessor);
            Assert.That(stats.BucketCount, Is.EqualTo(64));
            Assert.That(stats.EntryCount, Is.EqualTo(0));
            Assert.That(stats.OverflowBucketCount, Is.EqualTo(0));
            Assert.That(stats.MaxChainLength, Is.EqualTo(1));
            Assert.That(stats.FillEmpty, Is.EqualTo(64));

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void GetStats_L32_AfterInserts_CorrectCounts()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        int n0 = 4;
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, n0);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            for (int i = 0; i < 200; i++)
            {
                map.Insert(i, i, ref accessor, null);
            }

            var stats = map.GetStats(ref accessor);
            Assert.That(stats.EntryCount, Is.EqualTo(200));
            Assert.That(stats.BucketCount, Is.GreaterThan(n0));
            Assert.That(stats.FillEmpty, Is.LessThan(stats.BucketCount), "Not all buckets should be empty");
            Assert.That(stats.LoadFactor, Is.GreaterThan(0));
            // Histogram should sum to BucketCount
            int histogramSum = stats.FillEmpty + stats.FillQuarter + stats.FillHalf + stats.FillThreeQuarter + stats.FillFull;
            Assert.That(histogramSum, Is.EqualTo(stats.BucketCount), "Histogram bins must sum to bucket count");

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void CreateAndPopulate_L32_AllRetrievable()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 20, 256);

        var sourceData = new (int Key, int Value)[500];
        for (int i = 0; i < 500; i++)
        {
            sourceData[i] = (i + 1, (i + 1) * 7);
        }

        var map = PagedHashMap<int, int, PersistentStore>.CreateAndPopulate(segment, sourceData, 16);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            Assert.That(map.EntryCount, Is.EqualTo(500));
            for (int i = 0; i < 500; i++)
            {
                Assert.That(map.TryGet(i + 1, out int val, ref accessor), Is.True, $"Key {i + 1} not found");
                Assert.That(val, Is.EqualTo((i + 1) * 7));
            }

            Assert.That(map.VerifyIntegrity(ref accessor), Is.True);
            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void Enumerator_L32_AllEntriesEnumerated()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 8);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            for (int i = 1; i <= 100; i++)
            {
                map.Insert(i, i * 10, ref accessor, null);
            }

            var keys = new HashSet<int>();
            var enumerator = map.GetEnumerator(ref accessor);
            while (enumerator.MoveNext())
            {
                keys.Add(enumerator.Current.Key);
                Assert.That(enumerator.Current.Value, Is.EqualTo(enumerator.Current.Key * 10));
            }

            Assert.That(keys.Count, Is.EqualTo(100));
            for (int i = 1; i <= 100; i++)
            {
                Assert.That(keys.Contains(i), Is.True, $"Key {i} missing from enumeration");
            }

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void Enumerator_L32_EmptyMap_NoEntries()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 8);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            var enumerator = map.GetEnumerator(ref accessor);
            Assert.That(enumerator.MoveNext(), Is.False);

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 6 — Concurrent Stress Tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public void Stress_ConcurrentReads_16Threads()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 64);

        // Pre-populate 500 entries
        var setupDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            for (int i = 1; i <= 500; i++)
            {
                map.Insert(i, i * 10, ref accessor, null);
            }
            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(setupDepth);
        }

        const int threadCount = 16;
        const int readsPerThread = 200;
        int errors = 0;
        using var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            var threadId = t;
            tasks[t] = Task.Factory.StartNew(() =>
            {
                var depth = epochManager.EnterScope();
                try
                {
                    var wa = segment.CreateChunkAccessor();
                    barrier.SignalAndWait();

                    var rng = new Random(threadId * 31);
                    for (int i = 0; i < readsPerThread; i++)
                    {
                        int key = rng.Next(1, 501);
                        if (!map.TryGet(key, out int val, ref wa) || val != key * 10)
                        {
                            Interlocked.Increment(ref errors);
                        }
                    }

                    wa.Dispose();
                }
                finally
                {
                    epochManager.ExitScope(depth);
                }
            }, TaskCreationOptions.LongRunning);
        }

        Task.WaitAll(tasks);
        LogDiagnostics(map);
        Assert.That(errors, Is.EqualTo(0), "All reads should return correct values");
    }

    [Test]
    [CancelAfter(5000)]
    public void Stress_ConcurrentInserts_16Threads()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 64);

        const int threadCount = 16;
        const int keysPerThread = 100;
        using var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            var threadId = t;
            tasks[t] = Task.Factory.StartNew(() =>
            {
                var depth = epochManager.EnterScope();
                try
                {
                    var wa = segment.CreateChunkAccessor();
                    barrier.SignalAndWait();

                    int baseKey = threadId * keysPerThread + 1;
                    for (int i = 0; i < keysPerThread; i++)
                    {
                        map.Insert(baseKey + i, baseKey + i, ref wa, null);
                    }

                    wa.Dispose();
                }
                finally
                {
                    epochManager.ExitScope(depth);
                }
            }, TaskCreationOptions.LongRunning);
        }

        Task.WaitAll(tasks);
        LogDiagnostics(map);

        Assert.That(map.EntryCount, Is.EqualTo(threadCount * keysPerThread));
        Assert.That(map._splitCount, Is.GreaterThan(0), "Concurrent inserts should cause splits");

        // Verify integrity
        var verifyDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            Assert.That(map.VerifyIntegrity(ref accessor), Is.True);
            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(verifyDepth);
        }
    }

    [Test]
    [CancelAfter(5000)]
    public void Stress_MixedReadWrite_16Threads()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 64);

        // Pre-populate keys 1-500
        var setupDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            for (int i = 1; i <= 500; i++)
            {
                map.Insert(i, i * 10, ref accessor, null);
            }
            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(setupDepth);
        }

        // 10 readers (keys 1-200 safe range) + 4 inserters (keys 100_000+) + 2 removers (keys 300-500)
        const int readerCount = 10;
        const int inserterCount = 4;
        const int removerCount = 2;
        const int totalThreads = readerCount + inserterCount + removerCount;
        int readErrors = 0;
        using var barrier = new Barrier(totalThreads);
        var tasks = new Task[totalThreads];

        // Readers
        for (int t = 0; t < readerCount; t++)
        {
            var threadId = t;
            tasks[t] = Task.Factory.StartNew(() =>
            {
                var depth = epochManager.EnterScope();
                try
                {
                    var wa = segment.CreateChunkAccessor();
                    barrier.SignalAndWait();

                    var rng = new Random(threadId * 31);
                    for (int i = 0; i < 100; i++)
                    {
                        int key = rng.Next(1, 201); // safe range: never removed
                        if (!map.TryGet(key, out int val, ref wa) || val != key * 10)
                        {
                            Interlocked.Increment(ref readErrors);
                        }
                    }

                    wa.Dispose();
                }
                finally
                {
                    epochManager.ExitScope(depth);
                }
            }, TaskCreationOptions.LongRunning);
        }

        // Inserters
        for (int t = 0; t < inserterCount; t++)
        {
            var threadId = t;
            tasks[readerCount + t] = Task.Factory.StartNew(() =>
            {
                var depth = epochManager.EnterScope();
                try
                {
                    var wa = segment.CreateChunkAccessor();
                    barrier.SignalAndWait();

                    int baseKey = 100_000 + threadId * 100;
                    for (int i = 0; i < 100; i++)
                    {
                        map.Insert(baseKey + i, baseKey + i, ref wa, null);
                    }

                    wa.Dispose();
                }
                finally
                {
                    epochManager.ExitScope(depth);
                }
            }, TaskCreationOptions.LongRunning);
        }

        // Removers
        for (int t = 0; t < removerCount; t++)
        {
            var threadId = t;
            tasks[readerCount + inserterCount + t] = Task.Factory.StartNew(() =>
            {
                var depth = epochManager.EnterScope();
                try
                {
                    var wa = segment.CreateChunkAccessor();
                    barrier.SignalAndWait();

                    int baseKey = 300 + threadId * 100; // 300-399, 400-499
                    for (int i = 0; i < 100; i++)
                    {
                        map.Remove(baseKey + i, out _, ref wa, null);
                    }

                    wa.Dispose();
                }
                finally
                {
                    epochManager.ExitScope(depth);
                }
            }, TaskCreationOptions.LongRunning);
        }

        Task.WaitAll(tasks);
        LogDiagnostics(map);

        Assert.That(readErrors, Is.EqualTo(0), "Readers on safe range should never fail");

        // Verify integrity
        var verifyDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            Assert.That(map.VerifyIntegrity(ref accessor), Is.True);
            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(verifyDepth);
        }
    }

    [Test]
    [CancelAfter(5000)]
    public void Stress_ConcurrentSplits_8Threads()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, 256);
        int n0 = 4; // small N0 forces many splits
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, n0);

        const int threadCount = 8;
        const int keysPerThread = 200;
        using var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            var threadId = t;
            tasks[t] = Task.Factory.StartNew(() =>
            {
                var depth = epochManager.EnterScope();
                try
                {
                    var wa = segment.CreateChunkAccessor();
                    barrier.SignalAndWait();

                    int baseKey = threadId * keysPerThread + 1;
                    for (int i = 0; i < keysPerThread; i++)
                    {
                        map.Insert(baseKey + i, baseKey + i, ref wa, null);
                    }

                    wa.Dispose();
                }
                finally
                {
                    epochManager.ExitScope(depth);
                }
            }, TaskCreationOptions.LongRunning);
        }

        Task.WaitAll(tasks);
        LogDiagnostics(map);

        Assert.That(map.EntryCount, Is.EqualTo(threadCount * keysPerThread));
        Assert.That(map._splitCount, Is.GreaterThan(10), "Small N0 with 1600 entries should cause many splits");

        // All entries retrievable
        var verifyDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            for (int t = 0; t < threadCount; t++)
            {
                int baseKey = t * keysPerThread + 1;
                for (int i = 0; i < keysPerThread; i++)
                {
                    Assert.That(map.TryGet(baseKey + i, out int val, ref accessor), Is.True,
                        $"Key {baseKey + i} not found after concurrent splits");
                    Assert.That(val, Is.EqualTo(baseKey + i));
                }
            }

            Assert.That(map.VerifyIntegrity(ref accessor), Is.True);
            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(verifyDepth);
        }
    }

    [Test]
    [CancelAfter(5000)]
    public void Stress_ConcurrentInsertRemove_8Threads()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 64);

        // Pre-populate keys 1-400 for removers to target
        var setupDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            for (int i = 1; i <= 400; i++)
            {
                map.Insert(i, i, ref accessor, null);
            }
            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(setupDepth);
        }

        // 4 inserters (disjoint ranges: 10_000+) + 4 removers (keys 1-400)
        const int inserterCount = 4;
        const int removerCount = 4;
        const int totalThreads = inserterCount + removerCount;
        const int keysPerThread = 100;
        using var barrier = new Barrier(totalThreads);
        var tasks = new Task[totalThreads];

        // Inserters
        for (int t = 0; t < inserterCount; t++)
        {
            var threadId = t;
            tasks[t] = Task.Factory.StartNew(() =>
            {
                var depth = epochManager.EnterScope();
                try
                {
                    var wa = segment.CreateChunkAccessor();
                    barrier.SignalAndWait();

                    int baseKey = 10_000 + threadId * keysPerThread;
                    for (int i = 0; i < keysPerThread; i++)
                    {
                        map.Insert(baseKey + i, baseKey + i, ref wa, null);
                    }

                    wa.Dispose();
                }
                finally
                {
                    epochManager.ExitScope(depth);
                }
            }, TaskCreationOptions.LongRunning);
        }

        // Removers
        for (int t = 0; t < removerCount; t++)
        {
            var threadId = t;
            tasks[inserterCount + t] = Task.Factory.StartNew(() =>
            {
                var depth = epochManager.EnterScope();
                try
                {
                    var wa = segment.CreateChunkAccessor();
                    barrier.SignalAndWait();

                    int baseKey = threadId * keysPerThread + 1;
                    for (int i = 0; i < keysPerThread; i++)
                    {
                        map.Remove(baseKey + i, out _, ref wa, null);
                    }

                    wa.Dispose();
                }
                finally
                {
                    epochManager.ExitScope(depth);
                }
            }, TaskCreationOptions.LongRunning);
        }

        Task.WaitAll(tasks);
        LogDiagnostics(map);

        // Verify structural integrity
        var verifyDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            Assert.That(map.VerifyIntegrity(ref accessor), Is.True);

            // All inserted keys should be present
            for (int t = 0; t < inserterCount; t++)
            {
                int baseKey = 10_000 + t * keysPerThread;
                for (int i = 0; i < keysPerThread; i++)
                {
                    Assert.That(map.TryGet(baseKey + i, out int val, ref accessor), Is.True,
                        $"Inserted key {baseKey + i} not found");
                    Assert.That(val, Is.EqualTo(baseKey + i));
                }
            }

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(verifyDepth);
        }
    }

    [Test, Explicit("Long-running torture test — run manually")]
    [CancelAfter(5000)]
    public void Stress_Torture_32Threads_AllOperations()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, 256);
        int n0 = 4; // small N0 → aggressive splits throughout the test
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, n0);

        // Pre-populate: keys 1-1000
        //   Safe range      1-200: read only, value = key (never modified/removed)
        //   Upsert range  201-500: upserted concurrently, never removed
        //   Churn range  501-1000: removed concurrently, no guarantees after test
        var setupDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            for (int i = 1; i <= 1000; i++)
            {
                map.Insert(i, i, ref accessor, null);
            }
            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(setupDepth);
        }

        const int threadCount = 32;
        const int opsPerThread = 500;
        const int insertsPerThread = opsPerThread / 4; // 125 — one per 4-op cycle
        int readErrors = 0;
        using var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            var threadId = t;
            tasks[t] = Task.Factory.StartNew(() =>
            {
                var depth = epochManager.EnterScope();
                try
                {
                    var wa = segment.CreateChunkAccessor();
                    barrier.SignalAndWait();

                    var rng = new Random(threadId * 37 + 7);
                    int insertBase = 100_000 + threadId * insertsPerThread;
                    int insertIdx = 0;

                    for (int i = 0; i < opsPerThread; i++)
                    {
                        switch (i % 4)
                        {
                            case 0: // Read safe range — must always succeed
                            {
                                int key = rng.Next(1, 201);
                                if (!map.TryGet(key, out int val, ref wa) || val != key)
                                {
                                    Interlocked.Increment(ref readErrors);
                                }
                                break;
                            }
                            case 1: // Insert per-thread disjoint key
                            {
                                int key = insertBase + insertIdx++;
                                map.Insert(key, key, ref wa, null);
                                break;
                            }
                            case 2: // Upsert range 201-500 (concurrent value updates)
                            {
                                int key = rng.Next(201, 501);
                                map.Upsert(key, threadId * 10000 + i, ref wa, null);
                                break;
                            }
                            case 3: // Remove from churn range 501-1000
                            {
                                int key = rng.Next(501, 1001);
                                map.Remove(key, out _, ref wa, null);
                                break;
                            }
                        }
                    }

                    wa.Dispose();
                }
                finally
                {
                    epochManager.ExitScope(depth);
                }
            }, TaskCreationOptions.LongRunning);
        }

        Task.WaitAll(tasks);
        LogDiagnostics(map);

        Assert.That(readErrors, Is.EqualTo(0), "Safe range reads must never fail");

        // Post-test verification
        var verifyDepth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            Assert.That(map.VerifyIntegrity(ref accessor), Is.True, "Structural integrity failed after torture");

            // Safe range: keys 1-200 must exist with original values
            for (int i = 1; i <= 200; i++)
            {
                Assert.That(map.TryGet(i, out int val, ref accessor), Is.True, $"Safe key {i} missing");
                Assert.That(val, Is.EqualTo(i), $"Safe key {i} has wrong value");
            }

            // Upsert range: keys 201-500 must exist (upserted, never removed)
            for (int i = 201; i <= 500; i++)
            {
                Assert.That(map.TryGet(i, out _, ref accessor), Is.True, $"Upsert key {i} missing");
            }

            // Per-thread inserts: all 32×125 = 4000 keys must exist
            for (int t = 0; t < threadCount; t++)
            {
                int insertBase = 100_000 + t * insertsPerThread;
                for (int i = 0; i < insertsPerThread; i++)
                {
                    int key = insertBase + i;
                    Assert.That(map.TryGet(key, out int val, ref accessor), Is.True,
                        $"Thread {t} insert key {key} missing");
                    Assert.That(val, Is.EqualTo(key));
                }
            }

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(verifyDepth);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 6 — Concurrent Enumeration
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ConcurrentEnumerator_L32_AllEntriesEnumerated()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 8);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            for (int i = 0; i < 100; i++)
            {
                map.Insert(i, i * 10, ref accessor, null);
            }

            var found = new HashSet<int>();
            using var enumerator = map.GetConcurrentEnumerator(ref accessor);
            while (enumerator.MoveNext())
            {
                found.Add(enumerator.Current.Key);
            }

            Assert.That(found.Count, Is.EqualTo(100));
            for (int i = 0; i < 100; i++)
            {
                Assert.That(found.Contains(i), Is.True, $"Key {i} missing from concurrent enumeration");
            }

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void ConcurrentEnumerator_L32_EmptyMap_NoEntries()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 8);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            using var enumerator = map.GetConcurrentEnumerator(ref accessor);
            Assert.That(enumerator.MoveNext(), Is.False);

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    [CancelAfter(5000)]
    public void ConcurrentEnumerator_L32_ConcurrentWriters()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 50, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 16);

        // Pre-populate with some data
        var preDepth = epochManager.EnterScope();
        var preAccessor = segment.CreateChunkAccessor();
        for (int i = 0; i < 200; i++)
        {
            map.Insert(i, i * 10, ref preAccessor, null);
        }
        preAccessor.Dispose();
        epochManager.ExitScope(preDepth);

        long writerErrors = 0;
        long readerErrors = 0;
        var barrier = new Barrier(5); // 4 writers + 1 reader

        // 4 writer threads inserting new keys
        var writers = new Task[4];
        for (int w = 0; w < 4; w++)
        {
            int writerId = w;
            writers[w] = Task.Factory.StartNew(() =>
            {
                barrier.SignalAndWait();
                var wDepth = epochManager.EnterScope();
                try
                {
                    var wAccessor = segment.CreateChunkAccessor();
                    for (int i = 0; i < 100; i++)
                    {
                        int key = 10000 + writerId * 1000 + i;
                        if (!map.Insert(key, key, ref wAccessor, null))
                        {
                            Interlocked.Increment(ref writerErrors);
                        }
                    }
                    wAccessor.Dispose();
                }
                finally
                {
                    epochManager.ExitScope(wDepth);
                }
            }, TaskCreationOptions.LongRunning);
        }

        // 1 reader thread enumerating
        var reader = Task.Factory.StartNew(() =>
        {
            barrier.SignalAndWait();
            var rDepth = epochManager.EnterScope();
            try
            {
                var rAccessor = segment.CreateChunkAccessor();
                var found = new HashSet<int>();
                using var enumerator = map.GetConcurrentEnumerator(ref rAccessor);
                while (enumerator.MoveNext())
                {
                    found.Add(enumerator.Current.Key);
                }
                // Verify at least the pre-populated entries are present
                for (int i = 0; i < 200; i++)
                {
                    if (!found.Contains(i))
                    {
                        Interlocked.Increment(ref readerErrors);
                    }
                }
                rAccessor.Dispose();
            }
            finally
            {
                epochManager.ExitScope(rDepth);
            }
        }, TaskCreationOptions.LongRunning);

        Task.WaitAll([..writers, reader]);

        Assert.That(writerErrors, Is.EqualTo(0), "Writer errors (duplicate key rejections)");
        Assert.That(readerErrors, Is.EqualTo(0), "Reader missed pre-populated entries");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 7 — AllowMultiple
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void AllowMultiple_L32_InsertDuplicateKey_AppendsToBuffer()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 8, allowMultiple: true);

        Assert.That(map.AllowMultiple, Is.True);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            // Insert same key with two different values
            Assert.That(map.Insert(42, 100, ref accessor, null), Is.True);
            Assert.That(map.Insert(42, 200, ref accessor, null), Is.True);

            // TryGetMultiple should return both values
            using var bufAccessor = map.TryGetMultiple(42, ref accessor);
            Assert.That(bufAccessor.TotalCount, Is.EqualTo(2));

            var values = new HashSet<int>();
            do
            {
                foreach (var v in bufAccessor.ReadOnlyElements)
                {
                    values.Add(v);
                }
            }
            while (bufAccessor.NextChunk());
            Assert.That(values.Contains(100), Is.True);
            Assert.That(values.Contains(200), Is.True);

            // EntryCount should be 1 (one key, two values in buffer)
            Assert.That(map.EntryCount, Is.EqualTo(1));

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void AllowMultiple_L32_RemoveValue_RemovesOneValue()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 8, allowMultiple: true);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            map.Insert(42, 100, ref accessor, null);
            map.Insert(42, 200, ref accessor, null);
            map.Insert(42, 300, ref accessor, null);

            // Remove one value
            Assert.That(map.RemoveValue(42, 200, ref accessor, null), Is.True);

            // Should have 2 remaining
            using var bufAccessor = map.TryGetMultiple(42, ref accessor);
            Assert.That(bufAccessor.TotalCount, Is.EqualTo(2));

            var values = new HashSet<int>();
            do
            {
                foreach (var v in bufAccessor.ReadOnlyElements)
                {
                    values.Add(v);
                }
            }
            while (bufAccessor.NextChunk());
            Assert.That(values.Contains(100), Is.True);
            Assert.That(values.Contains(300), Is.True);
            Assert.That(values.Contains(200), Is.False);

            // Key still exists
            Assert.That(map.TryGet(42, out _, ref accessor), Is.True);
            Assert.That(map.EntryCount, Is.EqualTo(1));

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void AllowMultiple_L32_RemoveValue_LastValue_RemovesKey()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 8, allowMultiple: true);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            map.Insert(42, 100, ref accessor, null);

            // Remove the only value
            Assert.That(map.RemoveValue(42, 100, ref accessor, null), Is.True);

            // Key should be gone
            Assert.That(map.TryGet(42, out _, ref accessor), Is.False);
            Assert.That(map.EntryCount, Is.EqualTo(0));

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void AllowMultiple_L32_Remove_DeletesEntireBuffer()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 8, allowMultiple: true);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            map.Insert(42, 100, ref accessor, null);
            map.Insert(42, 200, ref accessor, null);
            map.Insert(42, 300, ref accessor, null);

            // Remove the entire key
            Assert.That(map.Remove(42, out _, ref accessor, null), Is.True);

            // Key should be gone
            Assert.That(map.TryGet(42, out _, ref accessor), Is.False);
            Assert.That(map.EntryCount, Is.EqualTo(0));

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void AllowMultiple_L32_SplitPreservesBufferIds()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 50, 256);
        var map = PagedHashMap<int, int, PersistentStore>.Create(segment, 4, allowMultiple: true);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();

            // Insert enough multi-value entries to trigger multiple splits
            int keyCount = 100;
            for (int k = 0; k < keyCount; k++)
            {
                map.Insert(k, k * 10, ref accessor, null);
                map.Insert(k, k * 10 + 1, ref accessor, null);
            }

            Assert.That(map.BucketCount, Is.GreaterThan(4), "Splits should have occurred");

            // Verify all values accessible after splits
            for (int k = 0; k < keyCount; k++)
            {
                using var bufAccessor = map.TryGetMultiple(k, ref accessor);
                Assert.That(bufAccessor.TotalCount, Is.EqualTo(2), $"Key {k} should have 2 values");
            }

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    public void AllowMultiple_L32_OpenPreservesFlag()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 256);
        var created = PagedHashMap<int, int, PersistentStore>.Create(segment, 8, allowMultiple: true);

        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            created.Insert(42, 100, ref accessor, null);
            created.Insert(42, 200, ref accessor, null);
            created.FlushMetaForTest(ref accessor);
            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }

        // Reopen
        var opened = PagedHashMap<int, int, PersistentStore>.Open(segment);
        Assert.That(opened.AllowMultiple, Is.True);
        Assert.That(opened.EntryCount, Is.EqualTo(1));

        var depth2 = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            using var bufAccessor = opened.TryGetMultiple(42, ref accessor);
            Assert.That(bufAccessor.TotalCount, Is.EqualTo(2));
            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth2);
        }
    }

}
