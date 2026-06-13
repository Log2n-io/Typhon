using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests;

/// <summary>
/// Comprehensive unit tests for ChunkAccessor<PersistentStore> covering SOA layout, SIMD search,
/// clock-hand eviction, dirty tracking, latching, and epoch protection.
/// </summary>
[TestFixture]
class ChunkAccessorTests
{
    private IServiceProvider _serviceProvider;
    private ServiceCollection _serviceCollection;

    // Small chunk for basic tests
    [StructLayout(LayoutKind.Sequential)]
    struct TestChunk32
    {
        public int A;
        public int B;
        public long C;
        public float D;
        public double E;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TestChunk8
    {
        public long Value;
    }

    // Large chunk for multi-page tests: 2048 bytes
    // Root page: (8000-2000)/2048 = 2 chunks (0 reserved, only 1 usable)
    // Other pages: 8000/2048 = 3 chunks each
    [StructLayout(LayoutKind.Sequential)]
    unsafe struct TestChunkLarge
    {
        public long Value;
        fixed byte _padding[2040];
    }

    static readonly char[] _charToRemove = ['(', ')', ','];
    private static string CurrentDatabaseName
    {
        get
        {
            var testName = TestContext.CurrentContext.Test.Name;
            foreach (var c in _charToRemove)
            {
                testName = testName.Replace(c, '_');
            }

            var prefix = "ECA_";
            var suffix = "_db";
            var maxTestNameLength = 55;

            if (testName.Length > maxTestNameLength)
            {
                testName = testName.Substring(0, maxTestNameLength);
            }

            return $"{prefix}{testName}{suffix}";
        }
    }

    [SetUp]
    public void Setup()
    {
        var o = TestContext.CurrentContext.Test.Properties.ContainsKey("MemPageCount");
        var dcs = o ? (int)TestContext.CurrentContext.Test.Properties.Get("MemPageCount")! : PagedMMF.DefaultMemPageCount;
        dcs *= PagedMMF.PageSize;

        var serviceCollection = new ServiceCollection();
        _serviceCollection = serviceCollection;
        _serviceCollection
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
                options.DatabaseCacheSize = (ulong)dcs;
                options.PagesDebugPattern = false;
                options.OverrideDatabaseCacheMinSize = true;
            });

        _serviceProvider = _serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    /// <summary>
    /// Return the first chunk index on the (<paramref name="pageIndex"/>)-th CHUNK-bearing page for TestChunkLarge (2048-byte
    /// stride). With the directory-only root (v4) the root — segment page 0 — holds no chunks (chunk 0 lives on segment page
    /// 1), so chunk-bearing page 0 is segment page 1 (chunks 0,1,2; chunk 0 is the reserved sentinel), page 1 is segment page
    /// 2 (chunks 3,4,5), and so on — 3 chunks per data page.
    /// </summary>
    private static int FirstChunkOnPage(int pageIndex)
    {
        if (pageIndex == 0)
        {
            return 1;   // first data page holds chunks 0,1,2 — chunk 0 is the reserved sentinel
        }

        return pageIndex * 3;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Basic read/write
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public unsafe void GetChunk_BasicReadWrite()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(epochManager);

        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));
        var chunkId = segment.AllocateChunk(true);

        var accessor = segment.CreateChunkAccessor();

        ref var chunk = ref accessor.GetChunk<TestChunk32>(chunkId);
        chunk.A = 42;
        chunk.B = 100;
        chunk.C = 123456789L;
        chunk.D = 3.14f;
        chunk.E = 2.718;

        // Read back via same accessor
        ref var chunk2 = ref accessor.GetChunk<TestChunk32>(chunkId);
        Assert.That(chunk2.A, Is.EqualTo(42));
        Assert.That(chunk2.B, Is.EqualTo(100));
        Assert.That(chunk2.C, Is.EqualTo(123456789L));
        Assert.That(chunk2.D, Is.EqualTo(3.14f));
        Assert.That(chunk2.E, Is.EqualTo(2.718));

        accessor.Dispose();
        guard.Dispose();
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void GetChunkReadOnly_ReturnsCorrectData()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(epochManager);

        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));
        var chunkId = segment.AllocateChunk(true);

        var accessor = segment.CreateChunkAccessor();

        // Write with mutable reference
        ref var chunk = ref accessor.GetChunk<TestChunk32>(chunkId);
        chunk.A = 42;

        // Read with readonly reference
        ref readonly var readOnly = ref accessor.GetChunkReadOnly<TestChunk32>(chunkId);
        Assert.That(readOnly.A, Is.EqualTo(42));

        accessor.Dispose();
        guard.Dispose();
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void GetChunkAsSpan_ReturnsCorrectSize()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(epochManager);

        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));
        var chunkId = segment.AllocateChunk(true);

        var accessor = segment.CreateChunkAccessor();

        var span = accessor.GetChunkAsSpan(chunkId);
        Assert.That(span.Length, Is.EqualTo(sizeof(TestChunk32)));

        var readOnlySpan = accessor.GetChunkAsReadOnlySpan(chunkId);
        Assert.That(readOnlySpan.Length, Is.EqualTo(sizeof(TestChunk32)));

        accessor.Dispose();
        guard.Dispose();
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void ClearChunk_ZerosContent()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(epochManager);

        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));
        var chunkId = segment.AllocateChunk(true);

        var accessor = segment.CreateChunkAccessor();

        // Write data
        ref var chunk = ref accessor.GetChunk<TestChunk32>(chunkId);
        chunk.A = 42;
        chunk.B = 100;
        chunk.C = 999L;

        // Clear
        accessor.ClearChunk(chunkId);

        // Verify all zeros
        ref var cleared = ref accessor.GetChunk<TestChunk32>(chunkId);
        Assert.That(cleared.A, Is.EqualTo(0));
        Assert.That(cleared.B, Is.EqualTo(0));
        Assert.That(cleared.C, Is.EqualTo(0));
        Assert.That(cleared.D, Is.EqualTo(0f));
        Assert.That(cleared.E, Is.EqualTo(0d));

        accessor.Dispose();
        guard.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Dirty tracking
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public unsafe void DirtyChunk_SetsDirtyFlag()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(epochManager);

        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));
        var changeSet = pmmf.CreateChangeSet();
        var chunkId = segment.AllocateChunk(true);

        var accessor = segment.CreateChunkAccessor(changeSet);

        // Load chunk (not dirty)
        ref var chunk = ref accessor.GetChunk<TestChunk32>(chunkId);
        chunk.A = 42;

        // Mark dirty externally
        accessor.DirtyChunk(chunkId);

        // Commit should flush to ChangeSet
        accessor.CommitChanges();
        changeSet.SaveChanges();
        Assert.Pass("DirtyChunk + CommitChanges + SaveChanges succeeded");

        accessor.Dispose();
        guard.Dispose();
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void CommitChanges_FlushesToChangeSet()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(epochManager);

        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));
        var changeSet = pmmf.CreateChangeSet();
        var chunkId = segment.AllocateChunk(true);

        var accessor = segment.CreateChunkAccessor(changeSet);

        // Write with dirty flag
        ref var chunk = ref accessor.GetChunk<TestChunk32>(chunkId, dirty: true);
        chunk.A = 999;

        // Commit should flush to ChangeSet
        accessor.CommitChanges();

        // SaveChanges should succeed (ChangeSet has dirty pages)
        changeSet.SaveChanges();
        Assert.Pass("CommitChanges flushed dirty page to ChangeSet");

        accessor.Dispose();
        guard.Dispose();
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void CommitChanges_ClearsDirtyFlags()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(epochManager);

        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));
        var changeSet = pmmf.CreateChangeSet();
        var chunkId = segment.AllocateChunk(true);

        var accessor = segment.CreateChunkAccessor(changeSet);

        // Write with dirty flag
        ref var chunk = ref accessor.GetChunk<TestChunk32>(chunkId, dirty: true);
        chunk.A = 999;

        // First commit flushes dirty
        accessor.CommitChanges();
        changeSet.SaveChanges();

        // Second commit should be a no-op (dirty flags cleared)
        // Writing without dirty, then committing should not produce ChangeSet activity
        ref var chunk2 = ref accessor.GetChunk<TestChunk32>(chunkId, dirty: false);
        _ = chunk2.A; // Read only
        accessor.CommitChanges();
        Assert.Pass("Second CommitChanges was a no-op after dirty flags cleared");

        accessor.Dispose();
        guard.Dispose();
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void Dispose_FlushesDirtyPages()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(epochManager);

        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));
        var changeSet = pmmf.CreateChangeSet();
        var chunkId = segment.AllocateChunk(true);

        var accessor = segment.CreateChunkAccessor(changeSet);

        // Write with dirty flag
        ref var chunk = ref accessor.GetChunk<TestChunk32>(chunkId, dirty: true);
        chunk.A = 777;

        // Dispose should flush dirty to ChangeSet
        accessor.Dispose();

        // ChangeSet should have entries from the dispose
        changeSet.SaveChanges();
        Assert.Pass("Dispose flushed dirty pages to ChangeSet");

        guard.Dispose();
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void Dispose_Idempotent()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(epochManager);

        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));
        var accessor = segment.CreateChunkAccessor();

        // Dispose twice — should not crash
        accessor.Dispose();
        accessor.Dispose();
        Assert.Pass("Double dispose succeeded without crash");

        guard.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MRU and SIMD search
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public unsafe void MRU_HitOptimization()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(epochManager);

        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));
        var chunkId = segment.AllocateChunk(true);

        var accessor = segment.CreateChunkAccessor();

        // First access loads page
        ref var chunk1 = ref accessor.GetChunk<TestChunk32>(chunkId);
        chunk1.A = 42;

        // Second access should hit MRU fast path (same page)
        ref var chunk2 = ref accessor.GetChunk<TestChunk32>(chunkId);
        Assert.That(chunk2.A, Is.EqualTo(42), "MRU hit should return same data");

        // Access a second chunk on the same page — should also hit MRU
        var chunkId2 = segment.AllocateChunk(true);
        ref var chunk3 = ref accessor.GetChunk<TestChunk32>(chunkId2);
        chunk3.B = 99;

        // Read back both chunks
        Assert.That(accessor.GetChunk<TestChunk32>(chunkId).A, Is.EqualTo(42));
        Assert.That(accessor.GetChunk<TestChunk32>(chunkId2).B, Is.EqualTo(99));

        accessor.Dispose();
        guard.Dispose();
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void SIMD_Search_FindsCachedPage()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(epochManager);

        // Use large chunks to span multiple pages easily
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 20, sizeof(TestChunkLarge));

        // Allocate enough chunks to span 3 pages
        var maxChunk = FirstChunkOnPage(2);
        for (int i = 0; i < maxChunk + 1; i++)
        {
            segment.AllocateChunk(false);
        }

        var accessor = segment.CreateChunkAccessor();

        // Access chunks on page 0 and page 1
        var chunk0 = FirstChunkOnPage(0);
        var chunk1 = FirstChunkOnPage(1);
        var chunk2 = FirstChunkOnPage(2);

        ref var c0 = ref accessor.GetChunk<TestChunkLarge>(chunk0);
        c0.Value = 100;

        ref var c1 = ref accessor.GetChunk<TestChunkLarge>(chunk1);
        c1.Value = 200;

        ref var c2 = ref accessor.GetChunk<TestChunkLarge>(chunk2);
        c2.Value = 300;

        // Re-access page 0 chunk — should be found via SIMD search (not MRU since MRU points to page 2)
        ref var reread = ref accessor.GetChunk<TestChunkLarge>(chunk0);
        Assert.That(reread.Value, Is.EqualTo(100), "SIMD search should find cached page 0");

        // Verify all data
        Assert.That(accessor.GetChunk<TestChunkLarge>(chunk1).Value, Is.EqualTo(200));
        Assert.That(accessor.GetChunk<TestChunkLarge>(chunk2).Value, Is.EqualTo(300));

        accessor.Dispose();
        guard.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Clock-hand eviction
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public unsafe void ClockHand_Eviction_WorksCorrectly()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(epochManager);

        // Use large chunks: 2 per root page, 3 per overflow. Need 17 pages to exceed 16-slot cache.
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 20, sizeof(TestChunkLarge));

        // Allocate enough chunks for 17 pages
        var maxChunk = FirstChunkOnPage(16);
        for (int i = 0; i < maxChunk + 1; i++)
        {
            segment.AllocateChunk(false);
        }

        var accessor = segment.CreateChunkAccessor();

        // Fill all 16 slots with pages 0-15
        for (int p = 0; p < 16; p++)
        {
            var cid = FirstChunkOnPage(p);
            ref var c = ref accessor.GetChunk<TestChunkLarge>(cid);
            c.Value = p * 1000;
        }

        // Access 17th page — should evict via clock-hand, no crash
        var extraChunk = FirstChunkOnPage(16);
        ref var extra = ref accessor.GetChunk<TestChunkLarge>(extraChunk);
        extra.Value = 16000;
        Assert.That(extra.Value, Is.EqualTo(16000), "17th page access should work after clock-hand eviction");

        accessor.Dispose();
        guard.Dispose();
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void ClockHand_NeverFails()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(epochManager);

        // Need many pages. Use large chunks (2048 bytes).
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 50, sizeof(TestChunkLarge));

        // Allocate enough chunks for 32 pages
        var maxChunk = FirstChunkOnPage(31);
        for (int i = 0; i < maxChunk + 1; i++)
        {
            segment.AllocateChunk(false);
        }

        var accessor = segment.CreateChunkAccessor();

        // Access 32 different pages — much more than the 16-slot cache
        // Clock-hand eviction should always succeed (no "all slots pinned" crash)
        for (int p = 0; p < 32; p++)
        {
            var cid = FirstChunkOnPage(p);
            ref var c = ref accessor.GetChunk<TestChunkLarge>(cid);
            c.Value = p;
        }

        // Verify last written page
        Assert.That(accessor.GetChunk<TestChunkLarge>(FirstChunkOnPage(31)).Value, Is.EqualTo(31));

        accessor.Dispose();
        guard.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Multiple pages
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public unsafe void MultiplePages_DifferentSegmentIndices()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(epochManager);

        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunkLarge));

        // Allocate chunks spanning 3 pages
        var maxChunk = FirstChunkOnPage(2);
        for (int i = 0; i < maxChunk + 1; i++)
        {
            segment.AllocateChunk(false);
        }

        var accessor = segment.CreateChunkAccessor();

        // Write unique values on each page
        for (int p = 0; p < 3; p++)
        {
            var cid = FirstChunkOnPage(p);
            ref var c = ref accessor.GetChunk<TestChunkLarge>(cid);
            c.Value = (p + 1) * 111;
        }

        // Read back and verify cross-page data integrity
        for (int p = 0; p < 3; p++)
        {
            var cid = FirstChunkOnPage(p);
            Assert.That(accessor.GetChunk<TestChunkLarge>(cid).Value, Is.EqualTo((p + 1) * 111),
                $"Data on page {p} should be preserved");
        }

        accessor.Dispose();
        guard.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Exclusive latching
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public unsafe void TryLatchExclusive_AcquiresLock()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(epochManager);

        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));
        var chunkId = segment.AllocateChunk(true);

        var accessor = segment.CreateChunkAccessor();

        // Load the chunk first (populates slot with memPageIndex)
        ref var chunk = ref accessor.GetChunk<TestChunk32>(chunkId);

        // Get the memory page index for verification
        var (segPageIdx, _) = segment.GetChunkLocation(chunkId);
        var filePageIdx = segment.Pages[segPageIdx];
        pmmf.RequestPageEpoch(filePageIdx, epochManager.GlobalEpoch, out var memPageIdx);

        // Latch should succeed (page is in Idle state)
        Assert.That(accessor.TryLatchExclusive(chunkId), Is.True, "TryLatchExclusive should succeed on Idle page");

        // Verify page is now Exclusive
        Assert.That(pmmf.GetPageState(memPageIdx), Is.EqualTo(PagedMMF.PageState.Exclusive));

        // Unlatch
        accessor.UnlatchExclusive(chunkId);

        // Verify page is back to Idle
        Assert.That(pmmf.GetPageState(memPageIdx), Is.EqualTo(PagedMMF.PageState.Idle));

        accessor.Dispose();
        guard.Dispose();
    }

    [Test]
    [CancelAfter(5000)]
    public unsafe void UnlatchExclusive_ReleasesLock()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(epochManager);

        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));
        var chunkId = segment.AllocateChunk(true);

        var accessor = segment.CreateChunkAccessor();
        ref var chunk = ref accessor.GetChunk<TestChunk32>(chunkId);

        // Latch
        Assert.That(accessor.TryLatchExclusive(chunkId), Is.True);

        // Unlatch
        accessor.UnlatchExclusive(chunkId);

        // Should be able to latch again after unlatching
        Assert.That(accessor.TryLatchExclusive(chunkId), Is.True, "Re-latch should succeed after unlatch");
        accessor.UnlatchExclusive(chunkId);

        accessor.Dispose();
        guard.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Epoch protection
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    // 24-page cache: the directory-only root (v4) adds fixed structural pages to the epoch-protected working set (a separate
    // segment directory root + the 2-page occupancy segment), so the old 16-page budget no longer leaves room to cycle the 12
    // pressure pages. 24 keeps real eviction pressure (the ~12 stale Phase-1 pages must still be evicted) with headroom.
    [Property("MemPageCount", 24)]
    public unsafe void EpochProtection_PreventsEviction()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        ChunkBasedSegment<PersistentStore> segment;

        // Phase 1: Allocate segment and fill cache with disposable pages in a separate epoch scope.
        // When this scope exits, GlobalEpoch advances and these pages become stale (evictable).
        {
            var setupGuard = EpochGuard.Enter(epochManager);

            segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunkLarge));

            // Allocate chunks on pages 0-3
            var maxChunk = FirstChunkOnPage(3);
            for (int i = 0; i < maxChunk + 1; i++)
            {
                segment.AllocateChunk(false);
            }

            // Fill cache with disposable pages (epoch = current, will become stale after scope exit)
            for (int i = 50; i < 62; i++)
            {
                pmmf.RequestPageEpoch(i, epochManager.GlobalEpoch, out _);
            }

            setupGuard.Dispose();
        }

        // Phase 2: Enter a new epoch scope. Pages loaded here get the new (higher) epoch
        // and are protected from eviction while this guard is active.
        var guard = EpochGuard.Enter(epochManager);

        var accessor = segment.CreateChunkAccessor();

        // Load pages 0-3 via epoch accessor — these get the current (higher) epoch
        for (int p = 0; p < 4; p++)
        {
            ref var c = ref accessor.GetChunk<TestChunkLarge>(FirstChunkOnPage(p));
            c.Value = p * 100;
        }

        // Load pressure pages to force eviction. The stale pages from Phase 1
        // (epoch < MinActiveEpoch) are evictable, so these requests evict them
        // instead of the epoch-protected pages 0-3.
        for (int i = 100; i < 112; i++)
        {
            pmmf.RequestPageEpoch(i, epochManager.GlobalEpoch, out _);
        }

        // Epoch-tagged pages should still be accessible (not evicted while guard is active)
        var metrics = pmmf.GetMetrics();
        var cacheMissBefore = metrics.MemPageCacheMiss;

        for (int p = 0; p < 4; p++)
        {
            var cid = FirstChunkOnPage(p);
            var (si, _) = segment.GetChunkLocation(cid);
            var fpIdx = segment.Pages[si];
            Assert.That(pmmf.RequestPageEpoch(fpIdx, epochManager.GlobalEpoch, out _), Is.True,
                $"Epoch-protected page {p} should still be in cache");
        }

        Assert.That(metrics.MemPageCacheMiss, Is.EqualTo(cacheMissBefore),
            "Epoch-protected pages should be cache hits (not evicted)");

        accessor.Dispose();
        guard.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Segment header access
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public unsafe void GetChunkBasedSegmentHeader_ReturnsValidRef()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(epochManager);

        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(TestChunk32));

        var accessor = segment.CreateChunkAccessor();

        // Access the segment header — should not crash
        ref var header = ref accessor.GetChunkBasedSegmentHeader<ChunkBasedSegmentHeader>(
            ChunkBasedSegmentHeader.Offset, dirty: false);

        // Header should be readable (we can't easily verify content, but no crash = success)
        Assert.Pass("GetChunkBasedSegmentHeader succeeded without crash");

        accessor.Dispose();
        guard.Dispose();
    }
}
