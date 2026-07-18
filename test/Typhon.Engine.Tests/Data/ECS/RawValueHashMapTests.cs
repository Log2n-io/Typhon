using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Typhon.Engine.Tests;

unsafe class RawValuePagedHashMapTests
{
    private IServiceProvider _serviceProvider;
    private string CurrentDatabaseName => $"{TestContext.CurrentContext.Test.Name.Replace("(", "_").Replace(")", "_").Replace(",", "_")}_database";

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
                options.DatabaseCacheSize = PagedMMF.MinimumMemPageCount * PagedMMF.PageSize;
                options.PagesDebugPattern = true;
            });

        _serviceProvider = serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    // ═══════════════════════════════════════════════════════════════════════
    // RecommendedStride
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void RecommendedStride_SmallValue_256()
    {
        int stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(22);
        Assert.That(stride, Is.EqualTo(256));
    }

    [Test]
    public void RecommendedStride_LargeValue_512()
    {
        int stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(78);
        Assert.That(stride, Is.EqualTo(512));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Insert + TryGet
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void InsertAndGet_SingleEntry_RoundTrip()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(2); // 22 bytes
        int stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);

        var map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, 4, valueSize);

        // Build entity record
        byte* record = stackalloc byte[valueSize];
        EntityRecordAccessor.InitializeRecord(record, 2);
        EntityRecordAccessor.GetHeader(record).BornTSN = 42;
        EntityRecordAccessor.GetHeader(record).EnabledBits = 0b11;
        EntityRecordAccessor.SetLocation(record, 0, 100);
        EntityRecordAccessor.SetLocation(record, 1, 200);

        // Insert
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            map.Insert(1L, record, ref accessor, null);
            accessor.Dispose();
        }

        Assert.That(map.EntryCount, Is.EqualTo(1));

        // Read back
        byte* readBuf = stackalloc byte[valueSize];
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            bool found = map.TryGet(1L, readBuf, ref accessor);
            accessor.Dispose();
            Assert.That(found, Is.True);
        }

        Assert.That(EntityRecordAccessor.GetHeader(readBuf).BornTSN, Is.EqualTo(42));
        Assert.That(EntityRecordAccessor.GetHeader(readBuf).EnabledBits, Is.EqualTo(0b11));
        Assert.That(EntityRecordAccessor.GetLocation(readBuf, 0), Is.EqualTo(100));
        Assert.That(EntityRecordAccessor.GetLocation(readBuf, 1), Is.EqualTo(200));
    }

    // ── ClearForRebuild: the crash-recovery reset used to make the EntityMap derived-on-crash (03-recovery.md §7). Frees every bucket/overflow chunk by bitmap, resets the
    //    directory + meta to empty, then repopulates via the rebuild insert primitive. ──
    [Test]
    public void ClearForRebuild_EmptiesAndAllowsRebuild()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(2);
        int stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);

        var map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, 4, valueSize);

        // Populate enough to force several bucket splits (n0=4) so ClearForRebuild has real bucket/overflow chunks to free.
        const int n = 500;
        byte* record = stackalloc byte[valueSize];
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            for (int i = 1; i <= n; i++)
            {
                EntityRecordAccessor.InitializeRecord(record, 2);
                EntityRecordAccessor.GetHeader(record).BornTSN = i;
                EntityRecordAccessor.SetLocation(record, 0, i * 10);
                map.Insert(i, record, ref accessor, null);
            }
            accessor.Dispose();
        }
        Assert.That(map.EntryCount, Is.EqualTo(n));

        // Clear: free every bucket/overflow chunk by bitmap, reset directory + meta to empty.
        map.ClearForRebuild(null);
        Assert.That(map.EntryCount, Is.EqualTo(0), "ClearForRebuild must reset the entry count to 0");

        byte* readBuf = stackalloc byte[valueSize];
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            Assert.That(map.TryGet(1, readBuf, ref accessor), Is.False, "every pre-clear key must miss after ClearForRebuild");
            Assert.That(map.TryGet(n, readBuf, ref accessor), Is.False);
            accessor.Dispose();
        }

        // Rebuild into the cleared map via the recovery insert primitive, with different values to prove the new content is what is read back.
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            for (int i = 1; i <= n; i++)
            {
                EntityRecordAccessor.InitializeRecord(record, 2);
                EntityRecordAccessor.GetHeader(record).BornTSN = i * 2;
                EntityRecordAccessor.SetLocation(record, 0, i * 20);
                map.InsertDuringRebuild(i, record, ref accessor, null);
            }
            accessor.Dispose();
        }
        Assert.That(map.EntryCount, Is.EqualTo(n), "rebuild via InsertDuringRebuild must repopulate the cleared map");

        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            for (int i = 1; i <= n; i++)
            {
                Assert.That(map.TryGet(i, readBuf, ref accessor), Is.True, $"rebuilt entry {i} not found");
                Assert.That(EntityRecordAccessor.GetHeader(readBuf).BornTSN, Is.EqualTo(i * 2));
                Assert.That(EntityRecordAccessor.GetLocation(readBuf, 0), Is.EqualTo(i * 20));
            }
            accessor.Dispose();
        }
    }

    [Test]
    public void InsertAndGet_MultipleEntries()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(2);
        int stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);

        var map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, 4, valueSize);

        // Insert 10 entries
        byte* record = stackalloc byte[valueSize];
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            for (int i = 1; i <= 10; i++)
            {
                EntityRecordAccessor.InitializeRecord(record, 2);
                EntityRecordAccessor.GetHeader(record).BornTSN = i * 10;
                EntityRecordAccessor.SetLocation(record, 0, i * 100);
                EntityRecordAccessor.SetLocation(record, 1, i * 200);
                map.Insert(i, record, ref accessor, null);
            }
            accessor.Dispose();
        }

        Assert.That(map.EntryCount, Is.EqualTo(10));

        // Verify each entry
        byte* readBuf = stackalloc byte[valueSize];
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            for (int i = 1; i <= 10; i++)
            {
                bool found = map.TryGet(i, readBuf, ref accessor);
                Assert.That(found, Is.True, $"Entry {i} not found");
                Assert.That(EntityRecordAccessor.GetHeader(readBuf).BornTSN, Is.EqualTo(i * 10));
                Assert.That(EntityRecordAccessor.GetLocation(readBuf, 0), Is.EqualTo(i * 100));
            }
            accessor.Dispose();
        }
    }

    [Test]
    public void Insert_Duplicate_ReturnsFalse()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(1);
        int stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);

        var map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, 4, valueSize);

        byte* record = stackalloc byte[valueSize];
        EntityRecordAccessor.InitializeRecord(record, 1);

        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            Assert.That(map.Insert(1L, record, ref accessor, null), Is.True);
            Assert.That(map.Insert(1L, record, ref accessor, null), Is.False);
            accessor.Dispose();
        }

        Assert.That(map.EntryCount, Is.EqualTo(1));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Upsert
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Upsert_UpdatesExisting()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(2);
        int stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);

        var map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, 4, valueSize);

        byte* record = stackalloc byte[valueSize];
        EntityRecordAccessor.InitializeRecord(record, 2);
        EntityRecordAccessor.GetHeader(record).BornTSN = 10;

        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            map.Upsert(1L, record, ref accessor, null);

            EntityRecordAccessor.GetHeader(record).DiedTSN = 50;
            map.Upsert(1L, record, ref accessor, null);
            accessor.Dispose();
        }

        byte* readBuf = stackalloc byte[valueSize];
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            map.TryGet(1L, readBuf, ref accessor);
            accessor.Dispose();
        }

        Assert.That(EntityRecordAccessor.GetHeader(readBuf).BornTSN, Is.EqualTo(10));
        Assert.That(EntityRecordAccessor.GetHeader(readBuf).DiedTSN, Is.EqualTo(50));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Remove
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Remove_ExistingKey_ReturnsTrue()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(1);
        int stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);

        var map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, 4, valueSize);

        byte* record = stackalloc byte[valueSize];
        EntityRecordAccessor.InitializeRecord(record, 1);

        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            map.Insert(1L, record, ref accessor, null);
            accessor.Dispose();
        }

        Assert.That(map.EntryCount, Is.EqualTo(1));

        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            bool removed = map.Remove(1L, ref accessor, null);
            accessor.Dispose();
            Assert.That(removed, Is.True);
        }

        Assert.That(map.EntryCount, Is.EqualTo(0));
    }

    [Test]
    public void Remove_NonExistentKey_ReturnsFalse()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(1);
        int stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);

        var map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, 4, valueSize);

        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            bool removed = map.Remove(999L, ref accessor, null);
            accessor.Dispose();
            Assert.That(removed, Is.False);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Split behavior
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Insert_TriggersLinearHashSplit()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(2);
        int stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);

        var map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, 4, valueSize);
        int initialBucketCount = map.BucketCount;

        byte* record = stackalloc byte[valueSize];
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            for (int i = 1; i <= 100; i++)
            {
                EntityRecordAccessor.InitializeRecord(record, 2);
                EntityRecordAccessor.GetHeader(record).BornTSN = i;
                map.Insert(i, record, ref accessor, null);
            }
            accessor.Dispose();
        }

        Assert.That(map.EntryCount, Is.EqualTo(100));
        Assert.That(map.BucketCount, Is.GreaterThan(initialBucketCount));

        byte* readBuf = stackalloc byte[valueSize];
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            for (int i = 1; i <= 100; i++)
            {
                bool found = map.TryGet(i, readBuf, ref accessor);
                Assert.That(found, Is.True, $"Entry {i} not found after splits");
                Assert.That(EntityRecordAccessor.GetHeader(readBuf).BornTSN, Is.EqualTo(i));
            }
            accessor.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // EnsureCapacity correctness
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void EnsureCapacity_PreExistingEntries_RemainFindable()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(2);
        int stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, stride);

        // n0=4 so level advancement happens quickly
        var map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, 4, valueSize);

        // Insert 20 entries
        byte* record = stackalloc byte[valueSize];
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            for (int i = 1; i <= 20; i++)
            {
                EntityRecordAccessor.InitializeRecord(record, 2);
                EntityRecordAccessor.GetHeader(record).BornTSN = i * 10;
                EntityRecordAccessor.SetLocation(record, 0, i * 100);
                map.Insert(i, record, ref accessor, null);
            }
            accessor.Dispose();
        }

        int bucketsBefore = map.BucketCount;

        // EnsureCapacity for a much larger target — forces segment + directory pre-grow
        // (with the old buggy code, this would advance level and orphan entries)
        using (EpochGuard.Enter(em))
        {
            map.EnsureCapacity(500);
        }

        // All 20 entries must still be findable
        byte* readBuf = stackalloc byte[valueSize];
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            for (int i = 1; i <= 20; i++)
            {
                bool found = map.TryGet(i, readBuf, ref accessor);
                Assert.That(found, Is.True, $"Entry {i} not found after EnsureCapacity");
                Assert.That(EntityRecordAccessor.GetHeader(readBuf).BornTSN, Is.EqualTo(i * 10));
            }
            accessor.Dispose();
        }

        // Bucket count should NOT have changed — only segment/directory capacity grew
        Assert.That(map.BucketCount, Is.EqualTo(bucketsBefore));
    }

    [Test]
    public void EnsureCapacity_ThenInsert_SplitsCorrectly()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(2);
        int stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, stride);

        var map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, 4, valueSize);

        // Pre-allocate for 200 entries
        using (EpochGuard.Enter(em))
        {
            map.EnsureCapacity(200);
        }

        // Insert 150 entries (triggers organic splits with pre-allocated backing)
        byte* record = stackalloc byte[valueSize];
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            for (int i = 1; i <= 150; i++)
            {
                EntityRecordAccessor.InitializeRecord(record, 2);
                EntityRecordAccessor.GetHeader(record).BornTSN = i;
                map.Insert(i, record, ref accessor, null);
            }
            accessor.Dispose();
        }

        Assert.That(map.EntryCount, Is.EqualTo(150));
        Assert.That(map.BucketCount, Is.GreaterThan(4));

        // Verify all 150 entries are findable
        byte* readBuf = stackalloc byte[valueSize];
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            for (int i = 1; i <= 150; i++)
            {
                bool found = map.TryGet(i, readBuf, ref accessor);
                Assert.That(found, Is.True, $"Entry {i} not found");
                Assert.That(EntityRecordAccessor.GetHeader(readBuf).BornTSN, Is.EqualTo(i));
            }

            // Structural integrity check
            Assert.That(map.VerifyIntegrity(ref accessor), Is.True);
            accessor.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Various value sizes
    // ═══════════════════════════════════════════════════════════════════════

    [TestCase(1, Description = "1 component = 18 bytes")]
    [TestCase(4, Description = "4 components = 30 bytes")]
    [TestCase(8, Description = "8 components = 46 bytes")]
    [TestCase(16, Description = "16 components = 78 bytes")]
    public void InsertGet_VariousComponentCounts(int componentCount)
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(componentCount);
        int stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);

        var map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, 4, valueSize);

        byte* record = stackalloc byte[valueSize];
        EntityRecordAccessor.InitializeRecord(record, componentCount);
        EntityRecordAccessor.GetHeader(record).BornTSN = 999;
        EntityRecordAccessor.GetHeader(record).EnabledBits = (ushort)((1 << componentCount) - 1);
        for (int s = 0; s < componentCount; s++)
        {
            EntityRecordAccessor.SetLocation(record, s, (s + 1) * 1000);
        }

        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            map.Insert(42L, record, ref accessor, null);
            accessor.Dispose();
        }

        byte* readBuf = stackalloc byte[valueSize];
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            bool found = map.TryGet(42L, readBuf, ref accessor);
            accessor.Dispose();
            Assert.That(found, Is.True);
        }

        Assert.That(EntityRecordAccessor.GetHeader(readBuf).BornTSN, Is.EqualTo(999));
        Assert.That(EntityRecordAccessor.GetHeader(readBuf).EnabledBits, Is.EqualTo((1 << componentCount) - 1));
        for (int s = 0; s < componentCount; s++)
        {
            Assert.That(EntityRecordAccessor.GetLocation(readBuf, s), Is.EqualTo((s + 1) * 1000), $"Location[{s}]");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 48-bit TSN round-trip through HashMap
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void TSN_48Bit_SurvivesHashMapRoundTrip()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(1);
        int stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);

        var map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, 4, valueSize);

        long largeTsn = (1L << 47) - 1;

        byte* record = stackalloc byte[valueSize];
        EntityRecordAccessor.InitializeRecord(record, 1);
        EntityRecordAccessor.GetHeader(record).BornTSN = largeTsn;
        EntityRecordAccessor.GetHeader(record).DiedTSN = largeTsn - 1000;

        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            map.Insert(1L, record, ref accessor, null);
            accessor.Dispose();
        }

        byte* readBuf = stackalloc byte[valueSize];
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            map.TryGet(1L, readBuf, ref accessor);
            accessor.Dispose();
        }

        Assert.That(EntityRecordAccessor.GetHeader(readBuf).BornTSN, Is.EqualTo(largeTsn));
        Assert.That(EntityRecordAccessor.GetHeader(readBuf).DiedTSN, Is.EqualTo(largeTsn - 1000));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ForEachEntry (OLC enumeration)
    // ═══════════════════════════════════════════════════════════════════════

    // Collects every key the scan visits — used to assert completeness on a quiescent map.
    private struct KeyCollector : RawValuePagedHashMap<long, PersistentStore>.IEntryAction<long>
    {
        public List<long> Keys;
        public bool Process(long key, byte* value)
        {
            Keys.Add(key);
            return true;
        }
    }

    // Asserts every visited key is a real, in-range key seen exactly once: a torn read (the bug this guards) would surface an
    // out-of-range key or a duplicate.
    private struct RangeValidator : RawValuePagedHashMap<long, PersistentStore>.IEntryAction<long>
    {
        public long MaxKey;
        public HashSet<long> Seen;
        public bool Ok;
        public bool Process(long key, byte* value)
        {
            if (key < 1 || key > MaxKey || !Seen.Add(key))
            {
                Ok = false;
            }
            return true;
        }
    }

    [Test]
    public void ForEachEntry_Quiescent_VisitsEveryEntryOnce()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(2);
        int stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);
        var map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, 4, valueSize);

        const int total = 2000; // forces many splits + overflow chains from the n0=4 start
        byte* record = stackalloc byte[valueSize];
        EntityRecordAccessor.InitializeRecord(record, 2);
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            for (long k = 1; k <= total; k++)
            {
                map.Insert(k, record, ref accessor, null);
            }
            accessor.Dispose();
        }

        var collector = new KeyCollector { Keys = new List<long>(total) };
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            int visited = map.ForEachEntry(ref accessor, ref collector);
            accessor.Dispose();
            Assert.That(visited, Is.EqualTo(total));
        }

        Assert.That(collector.Keys, Has.Count.EqualTo(total));
        Assert.That(new HashSet<long>(collector.Keys), Has.Count.EqualTo(total), "no duplicates");
        Assert.That(collector.Keys, Is.All.InRange(1L, total));
    }

    // Regression for the Workbench Data Browser crash: ForEachEntry must honour the OLC read protocol so a scan racing a writer
    // (inserts that trigger overflow appends + bucket splits, freeing/reusing chunks) never reads a torn chunk — which before the
    // fix tripped the ValueAt capacity assert (`index N out of range`) and aborted the process.
    [Test]
    public void ForEachEntry_ConcurrentWriterScans_NoTornRead()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(2);
        int stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);
        var map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, 4, valueSize);

        const int total = 4000;
        Exception failure = null;
        var done = false;

        var writer = new Thread(() =>
        {
            try
            {
                byte* record = stackalloc byte[valueSize];
                EntityRecordAccessor.InitializeRecord(record, 2);
                for (long k = 1; k <= total; k++)
                {
                    using var guard = EpochGuard.Enter(em);
                    var accessor = segment.CreateChunkAccessor();
                    map.Insert(k, record, ref accessor, null);
                    accessor.Dispose();
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
            }
            finally
            {
                Volatile.Write(ref done, true);
            }
        });

        var readers = new Thread[3];
        for (var t = 0; t < readers.Length; t++)
        {
            readers[t] = new Thread(() =>
            {
                try
                {
                    while (!Volatile.Read(ref done))
                    {
                        using var guard = EpochGuard.Enter(em);
                        var accessor = segment.CreateChunkAccessor();
                        var v = new RangeValidator { MaxKey = total, Seen = new HashSet<long>(), Ok = true };
                        map.ForEachEntry(ref accessor, ref v);
                        accessor.Dispose();
                        if (!v.Ok)
                        {
                            Interlocked.CompareExchange(ref failure, new Exception("torn read: out-of-range or duplicate key"), null);
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref failure, ex, null);
                }
            });
        }

        writer.Start();
        foreach (var r in readers)
        {
            r.Start();
        }
        Assert.That(writer.Join(TimeSpan.FromSeconds(10)), Is.True, "writer did not finish");
        foreach (var r in readers)
        {
            Assert.That(r.Join(TimeSpan.FromSeconds(10)), Is.True, "reader did not finish");
        }

        Assert.That(failure, Is.Null, () => failure!.ToString());

        // Final quiescent scan sees the full set, exactly once.
        var collector = new KeyCollector { Keys = new List<long>(total) };
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            map.ForEachEntry(ref accessor, ref collector);
            accessor.Dispose();
        }
        Assert.That(new HashSet<long>(collector.Keys), Has.Count.EqualTo(total));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // CountEntries / AnyEntry (optimistic in-place, copy-free aggregation)
    // ═══════════════════════════════════════════════════════════════════════

    // Matches every entry — quiescent count must equal the live set size.
    private struct CountAll : RawValuePagedHashMap<long, PersistentStore>.IEntryPredicate<long>
    {
        public bool Matches(long key, byte* value) => true;
    }

    // Selective predicate — matches even keys only.
    private struct CountEven : RawValuePagedHashMap<long, PersistentStore>.IEntryPredicate<long>
    {
        public bool Matches(long key, byte* value) => (key & 1L) == 0L;
    }

    // Matches a single target key — used to assert AnyEntry existence semantics.
    private struct MatchKey : RawValuePagedHashMap<long, PersistentStore>.IEntryPredicate<long>
    {
        public long Target;
        public bool Matches(long key, byte* value) => key == Target;
    }

    [Test]
    public void CountEntries_Quiescent_CountsAllAndFiltered()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(2);
        int stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);
        var map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, 4, valueSize);

        const int total = 2000; // forces many splits + overflow chains from the n0=4 start
        byte* record = stackalloc byte[valueSize];
        EntityRecordAccessor.InitializeRecord(record, 2);
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            for (long k = 1; k <= total; k++)
            {
                map.Insert(k, record, ref accessor, null);
            }
            accessor.Dispose();
        }

        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            var all = new CountAll();
            var even = new CountEven();
            int countAll = map.CountEntries(ref accessor, ref all);
            int countEven = map.CountEntries(ref accessor, ref even);
            accessor.Dispose();

            Assert.That(countAll, Is.EqualTo(total), "CountAll must visit every live entry once");
            Assert.That(countEven, Is.EqualTo(total / 2), "CountEven must count only even keys");
        }
    }

    [Test]
    public void AnyEntry_Quiescent_ExistenceSemantics()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(1);
        int stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);
        var map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, 4, valueSize);

        // Empty map — nothing matches.
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            var all = new CountAll();
            Assert.That(map.AnyEntry(ref accessor, ref all), Is.False, "empty map has no entries");
            accessor.Dispose();
        }

        const int total = 500;
        byte* record = stackalloc byte[valueSize];
        EntityRecordAccessor.InitializeRecord(record, 1);
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            for (long k = 1; k <= total; k++)
            {
                map.Insert(k, record, ref accessor, null);
            }
            accessor.Dispose();
        }

        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            var all = new CountAll();
            var hit = new MatchKey { Target = 250 };
            var miss = new MatchKey { Target = total + 9999 };
            Assert.That(map.AnyEntry(ref accessor, ref all), Is.True, "non-empty map matches CountAll");
            Assert.That(map.AnyEntry(ref accessor, ref hit), Is.True, "existing key is found");
            Assert.That(map.AnyEntry(ref accessor, ref miss), Is.False, "absent key is not found");
            accessor.Dispose();
        }
    }

    // CountEntries shares ForEachEntry's OLC protocol but evaluates the predicate on the LIVE (pre-validation) bytes; range checks keep every access in-bounds and
    // the per-bucket version validation discards any tally taken from a torn snapshot. So a scan racing a writer must never crash (no ValueAt OOB) and must always
    // return a count within [0, total] — never a phantom over-count from a validation escape.
    [Test]
    public void CountEntries_ConcurrentWriterScans_NoTornReadSaneCount()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var em = _serviceProvider.GetRequiredService<EpochManager>();
        int valueSize = EntityRecordAccessor.RecordSize(2);
        int stride = RawValuePagedHashMap<long, PersistentStore>.RecommendedStride(valueSize);
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, stride);
        var map = RawValuePagedHashMap<long, PersistentStore>.Create(segment, 4, valueSize);

        const int total = 4000;
        Exception failure = null;
        var done = false;

        var writer = new Thread(() =>
        {
            try
            {
                byte* record = stackalloc byte[valueSize];
                EntityRecordAccessor.InitializeRecord(record, 2);
                for (long k = 1; k <= total; k++)
                {
                    using var guard = EpochGuard.Enter(em);
                    var accessor = segment.CreateChunkAccessor();
                    map.Insert(k, record, ref accessor, null);
                    accessor.Dispose();
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
            }
            finally
            {
                Volatile.Write(ref done, true);
            }
        });

        var readers = new Thread[3];
        for (var t = 0; t < readers.Length; t++)
        {
            readers[t] = new Thread(() =>
            {
                try
                {
                    while (!Volatile.Read(ref done))
                    {
                        using var guard = EpochGuard.Enter(em);
                        var accessor = segment.CreateChunkAccessor();
                        var all = new CountAll();
                        int c = map.CountEntries(ref accessor, ref all);
                        accessor.Dispose();
                        if (c < 0 || c > total)
                        {
                            Interlocked.CompareExchange(ref failure, new Exception($"insane count {c} (expected [0,{total}])"), null);
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref failure, ex, null);
                }
            });
        }

        writer.Start();
        foreach (var r in readers)
        {
            r.Start();
        }
        Assert.That(writer.Join(TimeSpan.FromSeconds(10)), Is.True, "writer did not finish");
        foreach (var r in readers)
        {
            Assert.That(r.Join(TimeSpan.FromSeconds(10)), Is.True, "reader did not finish");
        }

        Assert.That(failure, Is.Null, () => failure!.ToString());

        // Final quiescent count sees the full set exactly.
        using (EpochGuard.Enter(em))
        {
            var accessor = segment.CreateChunkAccessor();
            var all = new CountAll();
            int finalCount = map.CountEntries(ref accessor, ref all);
            accessor.Dispose();
            Assert.That(finalCount, Is.EqualTo(total));
        }
    }
}
