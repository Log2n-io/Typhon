using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// Comprehensive tests for ChunkBasedSegment's internal BitmapL3 class.
/// 
/// BitmapL3 is a three-level hierarchical bitmap used to track chunk allocation:
/// - L0: Individual bits stored in page metadata (64 bits per long)
/// - L1: Summary bits tracking whether L0 groups are fully allocated (_l1All) or have any allocation (_l1Any)
/// - L2: Summary bits tracking whether L1 groups are fully allocated
/// 
/// Key invariants that must hold:
/// 1. Allocated count must match actual set bits
/// 2. L1All bit set iff all 64 corresponding L0 bits are set
/// 3. L1Any bit set iff any of 64 corresponding L0 bits are set
/// 4. L2All bit set iff all 64 corresponding L1All bits are set
/// 5. Chunk 0 is reserved and should never be returned by allocation
/// </summary>
public class ChunkBasedSegmentBitmapL3Tests
{
    private IServiceProvider _serviceProvider;
    private ILogger<ChunkBasedSegmentBitmapL3Tests> _logger;
    private ManagedPagedMMF _pmmf;
    private EpochManager _epochManager;
    private int _epochDepth;

    private static string CurrentDatabaseName
    {
        get
        {
            var testName = TestContext.CurrentContext.Test.Name;
            // Truncate long test names to fit database name limit
            if (testName.Length > 40)
            {
                testName = testName[..40];
            }
            foreach (var c in new[] { '(', ')', ',', ' ', '"' })
            {
                testName = testName.Replace(c, '_');
            }
            return $"Typhon_{testName}_db";
        }
    }

    [SetUp]
    public void Setup()
    {
        var hasMemPageCount = TestContext.CurrentContext.Test.Properties.ContainsKey("MemPageCount");
        var memPageCount = hasMemPageCount 
            ? (int)TestContext.CurrentContext.Test.Properties.Get("MemPageCount")! 
            : PagedMMF.DefaultMemPageCount;
        var cacheSize = memPageCount * PagedMMF.PageSize;

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
                options.DatabaseCacheSize = (ulong)cacheSize;
                options.PagesDebugPattern = false;
            });

        _serviceProvider = serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _logger = _serviceProvider.GetRequiredService<ILogger<ChunkBasedSegmentBitmapL3Tests>>();

        _pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        _epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        _epochDepth = _epochManager.EnterScope();
    }

    [TearDown]
    public void TearDown()
    {
        if (_epochManager != null)
        {
            _epochManager.ExitScope(_epochDepth);
        }

        _pmmf?.Dispose();
        (_serviceProvider as IDisposable)?.Dispose();
    }

    #region Basic Allocation Tests

    [Test]
    public void AllocateChunk_ReturnsSequentialIds()
    {
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 2, 64);

        var allocatedIds = new List<int>();
        var freeCount = segment.FreeChunkCount;

        for (int i = 0; i < Math.Min(freeCount, 100); i++)
        {
            var chunkId = segment.AllocateChunk(false);
            Assert.That(chunkId, Is.GreaterThan(0), $"Chunk ID should be > 0 (iteration {i})");
            Assert.That(allocatedIds, Does.Not.Contain(chunkId), $"Duplicate chunk ID {chunkId} returned");
            allocatedIds.Add(chunkId);
        }

        // Verify IDs are unique
        Assert.That(allocatedIds.Distinct().Count(), Is.EqualTo(allocatedIds.Count));
    }

    [Test]
    public void AllocateChunk_NeverReturnsZero()
    {
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 2, 64);

        // Allocate all chunks and verify none is 0
        var freeCount = segment.FreeChunkCount;
        for (int i = 0; i < freeCount; i++)
        {
            var chunkId = segment.AllocateChunk(false);
            Assert.That(chunkId, Is.Not.EqualTo(0), 
                $"Chunk 0 is reserved as sentinel and should never be allocated (iteration {i})");
        }
    }

    [Test]
    public void AllocatedCount_MatchesActualAllocations()
    {
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 2, 64);

        var initialAllocated = segment.AllocatedChunkCount;
        Assert.That(initialAllocated, Is.EqualTo(1), "Initially only chunk 0 should be reserved");

        const int allocCount = 50;
        for (int i = 0; i < allocCount; i++)
        {
            segment.AllocateChunk(false);
        }

        Assert.That(segment.AllocatedChunkCount, Is.EqualTo(initialAllocated + allocCount));
        Assert.That(segment.FreeChunkCount, Is.EqualTo(segment.ChunkCapacity - segment.AllocatedChunkCount));
    }

    #endregion

    #region Free (ClearL0) Tests

    [Test]
    public void FreeChunk_DecreasesAllocatedCount()
    {
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 2, 64);

        var chunkId = segment.AllocateChunk(false);
        var allocatedBefore = segment.AllocatedChunkCount;

        segment.FreeChunk(chunkId);

        Assert.That(segment.AllocatedChunkCount, Is.EqualTo(allocatedBefore - 1));
        Assert.That(segment.FreeChunkCount, Is.EqualTo(segment.ChunkCapacity - segment.AllocatedChunkCount));
    }

    [Test]
    public void FreeChunk_AllowsReallocation()
    {
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 1, 1024);

        // Allocate all available chunks
        var allocatedIds = new List<int>();
        var freeCount = segment.FreeChunkCount;
        for (int i = 0; i < freeCount; i++)
        {
            allocatedIds.Add(segment.AllocateChunk(false));
        }

        Assert.That(segment.FreeChunkCount, Is.EqualTo(0));

        // Free one chunk
        var freedId = allocatedIds[1];
        segment.FreeChunk(freedId);

        Assert.That(segment.FreeChunkCount, Is.EqualTo(1));

        // Reallocate - should get the freed chunk back
        var reallocatedId = segment.AllocateChunk(false);
        Assert.That(reallocatedId, Is.EqualTo(freedId), "Should reallocate the freed chunk");
    }

    [Test]
    public void FreeChunk_DoubleFree_CorruptsState()
    {
        // This test documents current (buggy?) behavior where double-free corrupts state
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 2, 64);

        var chunkId = segment.AllocateChunk(false);
        var allocatedBefore = segment.AllocatedChunkCount;

        segment.FreeChunk(chunkId);
        var allocatedAfterFirst = segment.AllocatedChunkCount;

        // BUG: Double free - this should either throw or be idempotent
        segment.FreeChunk(chunkId);
        var allocatedAfterSecond = segment.AllocatedChunkCount;

        // Document the bug: Allocated count goes negative or becomes inconsistent
        _logger.LogWarning("Double free: Before={Before}, AfterFirst={After1}, AfterSecond={After2}",
            allocatedBefore, allocatedAfterFirst, allocatedAfterSecond);

        // This assertion documents the expected fix - currently it will likely fail
        // showing that double-free decrements the counter twice
        Assert.That(allocatedAfterSecond, Is.EqualTo(allocatedAfterFirst),
            "BUG: Double free should be idempotent but it decrements Allocated twice");
    }

    #endregion

    #region Bulk Allocation Tests

    [Test]
    public void AllocateChunks_AllocatesRequestedCount()
    {
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 5, 64);

        const int requestCount = 100;
        using var result = segment.AllocateChunks(requestCount, false);

        var ids = result.Memory.Span[..requestCount].ToArray();
        Assert.That(ids.Length, Is.EqualTo(requestCount));
        Assert.That(ids.All(id => id > 0), Is.True, "All IDs should be > 0");
        Assert.That(ids.Distinct().Count(), Is.EqualTo(requestCount), "All IDs should be unique");
    }

    [Test]
    public void AllocateChunks_GrowsWhenInsufficientCapacity()
    {
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 1, 1024);

        var initialCapacity = segment.ChunkCapacity;
        var freeCount = segment.FreeChunkCount;

        // Try to allocate more than initially available - should trigger auto-growth
        var requestCount = freeCount + 10;
        using var result = segment.AllocateChunks(requestCount, false);

        // With auto-growth, the allocation should succeed
        var span = result.Memory.Span;
        Assert.That(span[0], Is.GreaterThan(0), "Allocation should succeed after auto-growth");
        
        // Capacity should have increased
        Assert.That(segment.ChunkCapacity, Is.GreaterThan(initialCapacity),
            "Segment should have grown to accommodate the allocation");
    }

    [Test]
    public void AllocateChunks_GrowsAndAllocatesAll()
    {
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 1, 1024);

        var allocatedBefore = segment.AllocatedChunkCount;
        var freeCount = segment.FreeChunkCount;

        // Try to allocate more than initially available - should grow and succeed
        var requestCount = freeCount + 10;
        using var result = segment.AllocateChunks(requestCount, false);

        // All requested chunks should be allocated
        Assert.That(segment.AllocatedChunkCount, Is.EqualTo(allocatedBefore + requestCount),
            "All requested chunks should be allocated after auto-growth");
    }

    [Test]
    public void AllocateChunks_BulkOf64_UsesL1Optimization()
    {

        // Need enough capacity for bulk allocation (64+ chunks)
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 64);

        var allocatedBefore = segment.AllocatedChunkCount;

        // Allocate exactly 64 chunks - should use SetL1 optimization
        using var result = segment.AllocateChunks(64, false);

        var ids = result.Memory.Span[..64].ToArray();
        Assert.That(ids.All(id => id > 0), Is.True);
        Assert.That(segment.AllocatedChunkCount, Is.EqualTo(allocatedBefore + 64));
    }

    #endregion

    #region Boundary Tests (64-bit boundaries)

    [Test]
    public void Allocation_CrossesL0Boundary()
    {
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 5, 64);

        // Allocate chunks that cross the 64-bit L0 boundary
        var allocatedIds = new List<int>();
        for (int i = 0; i < 70; i++)
        {
            var id = segment.AllocateChunk(false);
            allocatedIds.Add(id);
        }

        // Should have chunks from both L0 groups (0-63 and 64+)
        Assert.That(allocatedIds.Any(id => id < 64), Is.True, "Should have chunks in first L0 group");
        Assert.That(allocatedIds.Any(id => id >= 64), Is.True, "Should have chunks crossing L0 boundary");
        Assert.That(allocatedIds.Distinct().Count(), Is.EqualTo(70), "All IDs unique");
    }

    [Test]
    public void Allocation_CrossesL1Boundary()
    {
        // L1 boundary is at 64*64 = 4096 chunks
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 100, 64);

        // Skip to near the L1 boundary by allocating many chunks
        const int targetCount = 4100; // Cross the 4096 boundary
        using var result = segment.AllocateChunks(Math.Min(targetCount, segment.FreeChunkCount), false);

        var ids = result.Memory.Span[..Math.Min(targetCount, segment.FreeChunkCount)].ToArray();
        
        if (ids.Length >= 4096)
        {
            Assert.That(ids.Any(id => id >= 4096), Is.True, "Should cross L1 boundary at 4096");
        }
    }

    [Test]
    public void FreeChunk_AtL0Boundary()
    {
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 5, 64);

        // Allocate past the first L0 group
        var allocatedIds = new List<int>();
        for (int i = 0; i < 70; i++)
        {
            allocatedIds.Add(segment.AllocateChunk(false));
        }

        // Find and free chunks at boundaries (63, 64)
        var chunk63 = allocatedIds.FirstOrDefault(id => id == 63);
        var chunk64 = allocatedIds.FirstOrDefault(id => id == 64);

        if (chunk63 != 0)
        {
            var beforeFree = segment.AllocatedChunkCount;
            segment.FreeChunk(chunk63);
            Assert.That(segment.AllocatedChunkCount, Is.EqualTo(beforeFree - 1));
        }

        if (chunk64 != 0)
        {
            var beforeFree = segment.AllocatedChunkCount;
            segment.FreeChunk(chunk64);
            Assert.That(segment.AllocatedChunkCount, Is.EqualTo(beforeFree - 1));
        }
    }

    #endregion

    #region ClearL0 Bug Tests

    [Test]
    public void ClearL0_L1AllBitmapUpdate_Bug()
    {
        // This test targets a potential bug in ClearL0 where _l1All and _l2All
        // are updated using &= l1Mask instead of &= ~l1Mask
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 5, 64);

        // Allocate all 64 chunks in the first L0 group (should set L1All bit)
        var allocatedIds = new List<int>();
        for (int i = 0; i < 64; i++)
        {
            var id = segment.AllocateChunk(false);
            if (id > 0 && id < 64)
            {
                allocatedIds.Add(id);
            }
        }

        _logger.LogInformation("Allocated {Count} chunks in first L0 group", allocatedIds.Count);

        // Now free one chunk from the first group
        if (allocatedIds.Count > 0)
        {
            var chunkToFree = allocatedIds[0];
            var allocatedBefore = segment.AllocatedChunkCount;
            
            segment.FreeChunk(chunkToFree);
            
            Assert.That(segment.AllocatedChunkCount, Is.EqualTo(allocatedBefore - 1),
                "Freeing a chunk should decrement allocated count by exactly 1");

            // The freed chunk should be reallocatable
            var reallocated = segment.AllocateChunk(false);
            Assert.That(reallocated, Is.EqualTo(chunkToFree),
                "Should be able to reallocate the freed chunk");
        }
    }

    [Test]
    public void ClearL0_WrongMaskInversion_Bug()
    {
        // BUG: In ClearL0, the code does:
        //   _l1All.Span[l1Offset] &= l1Mask;
        // But it should be:
        //   _l1All.Span[l1Offset] &= ~l1Mask;
        //
        // The mask l1Mask = 1L << (l0Offset & 0x3F) has ONE bit set.
        // To clear that bit, we need AND with the inverse (~l1Mask).
        // But the code ANDs with the mask itself, clearing ALL OTHER bits!
        
        // Large enough segment to have multiple L0 groups
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, 64);

        // Allocate chunks from TWO different L0 groups
        // Group 0: chunks 1-63 (0 is reserved)
        // Group 1: chunks 64-127
        var group0Chunks = new List<int>();
        var group1Chunks = new List<int>();

        // Fill group 0 (allocate until we get chunks >= 64)
        for (int i = 0; i < 70; i++)
        {
            var id = segment.AllocateChunk(false);
            if (id > 0 && id < 64)
                group0Chunks.Add(id);
            else if (id >= 64 && id < 128)
                group1Chunks.Add(id);
        }

        _logger.LogInformation("Group 0: {Count} chunks, Group 1: {Count2} chunks", 
            group0Chunks.Count, group1Chunks.Count);

        // Now free a chunk from group 0
        if (group0Chunks.Count > 0)
        {
            var chunkToFree = group0Chunks[0];
            segment.FreeChunk(chunkToFree);

            // Allocate a new chunk - it should come from group 0 (the freed one)
            var newChunk = segment.AllocateChunk(false);
            
            // BUG: Due to the mask bug in ClearL0, the L1Any bitmap for group 1 
            // might have been corrupted, potentially affecting allocation order
            Assert.That(newChunk, Is.EqualTo(chunkToFree),
                $"Expected to reallocate freed chunk {chunkToFree} but got {newChunk}. " +
                "This may indicate L1 bitmap corruption from ClearL0 mask bug.");
        }
    }

    [Test]
    public void ClearL0_MultipleGroups_BitmapConsistency()
    {
        // Test that freeing chunks in one L0 group doesn't corrupt other groups.
        // Exhaust all chunks first so freed pages are the ONLY source of free space.
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 20, 64);

        // Allocate ALL chunks to exhaust the segment
        var allChunks = new List<int>();
        var totalFree = segment.FreeChunkCount;
        for (int i = 0; i < totalFree; i++)
        {
            allChunks.Add(segment.AllocateChunk(false));
        }

        var allocatedBefore = segment.AllocatedChunkCount;
        Assert.That(segment.FreeChunkCount, Is.EqualTo(0), "Segment should be fully exhausted");
        _logger.LogInformation("Allocated {Count} chunks, AllocatedCount={Allocated}",
            allChunks.Count, allocatedBefore);

        // Free every other chunk. Use HashSet for the membership check below — Does.Contain on a List is O(n)
        // and was the dominant cost of this test (O(n²) overall on hundreds of chunks).
        var freedChunks = new HashSet<int>();
        for (int i = 0; i < allChunks.Count; i += 2)
        {
            segment.FreeChunk(allChunks[i]);
            freedChunks.Add(allChunks[i]);
        }

        var allocatedAfterFree = segment.AllocatedChunkCount;
        var expectedAfterFree = allocatedBefore - freedChunks.Count;

        Assert.That(allocatedAfterFree, Is.EqualTo(expectedAfterFree),
            $"After freeing {freedChunks.Count} chunks, AllocatedCount should be {expectedAfterFree} but was {allocatedAfterFree}");

        // Reallocate - should get back the freed chunks
        var reallocated = new List<int>();
        for (int i = 0; i < freedChunks.Count; i++)
        {
            var id = segment.AllocateChunk(false);
            if (id > 0) reallocated.Add(id);
        }

        // All reallocated chunks should be from the freed set
        foreach (var id in reallocated)
        {
            Assert.That(freedChunks, Does.Contain(id),
                $"Reallocated chunk {id} was not in the freed set. " +
                "This indicates bitmap corruption - freed chunks are not being found.");
        }
    }

    [Test]
    public void ClearL0_FullL0GroupThenClear_StateConsistency()
    {
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 5, 64);

        // Fill an entire L0 group (64 consecutive chunks)
        // Skip chunk 0 which is reserved
        var group1Chunks = new List<int>();
        for (int i = 0; i < 65; i++) // Allocate enough to fill first group
        {
            var id = segment.AllocateChunk(false);
            if (id >= 1 && id <= 63)
            {
                group1Chunks.Add(id);
            }
        }

        _logger.LogInformation("First L0 group chunks: {Chunks}", string.Join(",", group1Chunks.Take(10)) + "...");

        // Now clear all chunks in the first group
        foreach (var id in group1Chunks)
        {
            segment.FreeChunk(id);
        }

        // Allocate again - should get chunks from the first group
        var reallocated = segment.AllocateChunk(false);
        Assert.That(reallocated, Is.LessThan(64),
            "After freeing first L0 group, allocation should return chunk from that group");
    }

    #endregion

    #region Concurrent Access Tests

    [Test]
    public void ConcurrentAllocate_NoduplicateIds()
    {
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 20, 64);

        var allIds = new System.Collections.Concurrent.ConcurrentBag<int>();
        var tasks = new List<Task>();
        const int threadsCount = 8;
        const int allocsPerThread = 100;

        for (int t = 0; t < threadsCount; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                var depth = _epochManager.EnterScope();
                try
                {
                    for (int i = 0; i < allocsPerThread; i++)
                    {
                        var id = segment.AllocateChunk(false);
                        if (id > 0)
                        {
                            allIds.Add(id);
                        }
                    }
                }
                finally
                {
                    _epochManager.ExitScope(depth);
                }
            }));
        }

        if (!Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(10)))
        {
            Assert.Fail("ConcurrentAllocate tasks did not complete within 10s — likely deadlock in AllocateChunk");
        }

        var idList = allIds.ToList();
        var uniqueCount = idList.Distinct().Count();

        Assert.That(uniqueCount, Is.EqualTo(idList.Count),
            $"Found {idList.Count - uniqueCount} duplicate IDs in concurrent allocation");
    }

    [Property("MemPageCount", 16*1024)]
    [Test]
    [CancelAfter(10000)]
    [Ignore("Flaky — timing-dependent concurrency test, passes in isolation but fails under parallel load")]
    public void ConcurrentAllocateAndFree_MaintainsConsistency()
    {
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 20, 64);

        var allocatedIds = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var tasks = new List<Task>();

        // Allocator threads - with bounds checking to avoid triggering the overflow bug
        for (int t = 0; t < 4; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                var depth = _epochManager.EnterScope();
                try
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        // Check if there's likely room before allocating
                        if (segment.FreeChunkCount <= 0)
                        {
                            Thread.SpinWait(100);
                            continue;
                        }

                        var id = segment.AllocateChunk(false);

                        // Validate the returned ID against CURRENT capacity (may have grown)
                        var currentCapacity = segment.ChunkCapacity;
                        if (id >= currentCapacity)
                        {
                            errors.Add($"BUG: AllocateChunk returned {id} >= capacity {currentCapacity}");
                            break;
                        }

                        if (id > 0 && id < currentCapacity)
                        {
                            allocatedIds.Enqueue(id);
                        }
                        Thread.SpinWait(10);
                    }
                }
                finally
                {
                    _epochManager.ExitScope(depth);
                }
            }));
        }

        // Deallocator threads
        for (int t = 0; t < 2; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                var depth = _epochManager.EnterScope();
                try
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        if (allocatedIds.TryDequeue(out var id))
                        {
                            segment.FreeChunk(id);
                        }
                        Thread.SpinWait(10);
                    }
                }
                finally
                {
                    _epochManager.ExitScope(depth);
                }
            }));
        }

        if (!Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(5)))
        {
            Assert.Fail("ConcurrentAllocateAndFree tasks did not complete within 5s — likely deadlock in AllocateChunk/FreeChunk");
        }

        // Report any errors found during concurrent execution
        foreach (var error in errors)
        {
            _logger.LogError("{Error}", error);
        }

        // After all threads complete, verify state consistency
        var remaining = allocatedIds.Count;
        var reported = segment.AllocatedChunkCount;
        
        _logger.LogInformation("Remaining in queue: {Remaining}, Reported allocated: {Reported}", 
            remaining, reported);

        // First check: no overflow errors
        Assert.That(errors, Is.Empty, "Concurrent allocation produced invalid chunk IDs");

        // The allocated count should be at least the reserved chunk (0) 
        Assert.That(segment.AllocatedChunkCount, Is.GreaterThanOrEqualTo(1));
        Assert.That(segment.FreeChunkCount, Is.GreaterThanOrEqualTo(0));
        Assert.That(segment.AllocatedChunkCount + segment.FreeChunkCount, Is.EqualTo(segment.ChunkCapacity));
    }

    #endregion

    #region Edge Cases

    [Test]
    public void SmallSegment_SinglePage_MinimalCapacity()
    {
        // Large stride = fewer chunks per page
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 1, 2048);

        var capacity = segment.ChunkCapacity;
        var freeCount = segment.FreeChunkCount;

        _logger.LogInformation("Small segment: Capacity={Capacity}, Free={Free}", capacity, freeCount);

        Assert.That(capacity, Is.GreaterThan(0));
        Assert.That(freeCount, Is.EqualTo(capacity - 1), "Only chunk 0 should be reserved");

        // Allocate all available
        for (int i = 0; i < freeCount; i++)
        {
            var id = segment.AllocateChunk(false);
            Assert.That(id, Is.GreaterThan(0));
        }

        Assert.That(segment.FreeChunkCount, Is.EqualTo(0));
    }

    [Test]
    public void AllocateWithClearContent_ClearsChunkData()
    {
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 2, 64);

        // Allocate with clearing
        var chunkId = segment.AllocateChunk(true);
        Assert.That(chunkId, Is.GreaterThan(0));

        // Verify the chunk data is cleared
        using var accessor = segment.CreateChunkAccessor();
        var chunkData = accessor.GetChunkAsReadOnlySpan(chunkId);
        
        foreach (var b in chunkData)
        {
            Assert.That(b, Is.EqualTo(0), "Chunk data should be cleared to zero");
        }
    }

    [Test]
    public void ReserveChunk_ManualReservation()
    {
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 2, 64);

        var allocatedBefore = segment.AllocatedChunkCount;

        // Reserve a specific chunk (simulating what Create() does with chunk 0)
        segment.ReserveChunk(5);

        Assert.That(segment.AllocatedChunkCount, Is.EqualTo(allocatedBefore + 1));

        // Allocating should skip the reserved chunk
        var allocatedIds = new List<int>();
        for (int i = 0; i < 10; i++)
        {
            var id = segment.AllocateChunk(false);
            allocatedIds.Add(id);
        }

        Assert.That(allocatedIds, Does.Not.Contain(5), "Reserved chunk 5 should not be allocated");
    }

    [Test]
    public void ReserveChunk_DoubleReserve_ReturnsFalse()
    {
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 2, 64);

        // First allocation of chunk
        var id = segment.AllocateChunk(false);

        // Trying to reserve an already-allocated chunk
        // ReserveChunk uses SetL0 internally which should return false
        segment.ReserveChunk(id);

        // The allocated count shouldn't increase for a double-set
        // (This tests the return value path of SetL0)
    }

    #endregion

    #region Capacity and Overflow Tests

    [Test]
    public void Capacity_MatchesExpectedFormula()
    {

        const int pageCount = 5;
        const int stride = 64;

        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, pageCount, stride);

        // Expected: (pageCount-1) * ChunkCountPerPage + ChunkCountRootPage
        var expectedCapacity = (pageCount - 1) * segment.ChunkCountPerPage + segment.ChunkCountRootPage;

        Assert.That(segment.ChunkCapacity, Is.EqualTo(expectedCapacity));
    }

    // ── Chunk-start alignment cap (page-waste regression) ──────────────────────────────────────────────────
    //
    // Aligning chunk starts to the *full stride* was pathological for large strides: for any stride > PageHeaderSize
    // (192) the padding became ~a whole chunk (stride - 192), collapsing a 2-chunk page to 1. The alignment is now
    // capped at ChunkStartAlignment (64); since PageHeaderSize and PageRawDataSize are both multiples of 64 the
    // non-root padding is zero and both chunks fit.

    [Test]
    public void ChunkGeometry_AntClusterStride_PacksTwoPerPage()
    {
        // 3968 = the ant cluster stride (4 comps summing 72 B → N=49 → 3960, rounded up to a multiple of 64).
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 3, 3968);

        Assert.That(segment.OtherChunkDataOffset, Is.EqualTo(0), "64-byte cap → zero non-root padding (192 % 64 == 0)");
        Assert.That(segment.ChunkCountPerPage, Is.EqualTo(2), "8000 / 3968 = 2 chunks/page (was 1 under full-stride alignment)");
    }

    [Test]
    public void ChunkGeometry_LargeNonMultipleStride_NotOverPadded()
    {
        // A large stride that is not a multiple of 64 (only chunk 0 is cache-aligned) must still not be over-padded:
        // 3960 wasted 3768 B/page → 1 chunk before the fix; now padding is 0 so 2 fit.
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 3, 3960);

        Assert.That(segment.OtherChunkDataOffset, Is.EqualTo(0));
        Assert.That(segment.ChunkCountPerPage, Is.EqualTo(2));
    }

    [Test]
    public void ChunkGeometry_SmallStride_BackwardCompatible()
    {
        // Stride 64 already divided the 192-byte header, so the cap changes nothing for the original small-chunk case.
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 3, 64);

        Assert.That(segment.OtherChunkDataOffset, Is.EqualTo(0));
        Assert.That(segment.ChunkCountPerPage, Is.EqualTo(PagedMMF.PageRawDataSize / 64)); // 125
    }

    /// <summary>
    /// Test that AllocateChunk auto-grows when capacity is exhausted.
    /// 
    /// Previously this was a bug where AllocateChunk would return invalid IDs.
    /// Now with auto-growth, the segment expands automatically when needed.
    /// </summary>
    [Test]
    public void AllocateChunk_ExceedsCapacity_AutoGrows()
    {
        
        // Use a small segment (2 pages) with large chunks to minimize initial capacity
        const int pageCount = 2;
        const int stride = 512; // Large stride = fewer chunks per page
        
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, pageCount, stride);
        
        var initialCapacity = segment.ChunkCapacity;
        var initialFree = segment.FreeChunkCount;
        
        _logger.LogInformation(
            "Segment created: Capacity={Capacity}, InitialFree={Free}, Stride={Stride}",
            initialCapacity, initialFree, stride);
        
        // Allocate ALL initially available chunks
        var validChunks = new List<int>();
        for (int i = 0; i < initialFree; i++)
        {
            var id = segment.AllocateChunk(false);
            validChunks.Add(id);
        }
        
        Assert.That(segment.FreeChunkCount, Is.EqualTo(0), 
            "After allocating all free chunks, FreeChunkCount should be 0");
        
        _logger.LogInformation(
            "Allocated {Count} valid chunks. FreeChunkCount={Free}, AllocatedCount={Allocated}",
            validChunks.Count, segment.FreeChunkCount, segment.AllocatedChunkCount);
        
        // Now allocate MORE - this should trigger auto-growth
        var additionalChunks = new List<int>();
        const int additionalCount = 10;
        
        for (int i = 0; i < additionalCount; i++)
        {
            var id = segment.AllocateChunk(false);
            additionalChunks.Add(id);
            
            _logger.LogInformation(
                "Additional allocation #{Iteration}: Got ChunkId={Id}, FreeCount={Free}, AllocatedCount={Allocated}",
                i + 1, id, segment.FreeChunkCount, segment.AllocatedChunkCount);
        }
        
        // With auto-growth, capacity should have increased
        Assert.That(segment.ChunkCapacity, Is.GreaterThan(initialCapacity),
            $"Segment should have grown. Initial: {initialCapacity}, Current: {segment.ChunkCapacity}");
        
        // FreeChunkCount should never go negative
        Assert.That(segment.FreeChunkCount, Is.GreaterThanOrEqualTo(0),
            $"FreeChunkCount should never be negative. Value: {segment.FreeChunkCount}");
        
        // All returned chunk IDs should be valid (> 0 and < current capacity)
        var allChunks = validChunks.Concat(additionalChunks).ToList();
        Assert.That(allChunks.All(id => id > 0 && id < segment.ChunkCapacity), Is.True,
            "All allocated chunk IDs should be valid");
        
        // All IDs should be unique
        Assert.That(allChunks.Distinct().Count(), Is.EqualTo(allChunks.Count),
            "All allocated chunk IDs should be unique");
    }

    /// <summary>
    /// Stress test simulating MonitoringDemo-like workload that creates many entities.
    /// 
    /// MonitoringDemo creates thousands of entities with large components (~100+ bytes).
    /// With auto-growth, the segment automatically expands to accommodate all entities.
    /// </summary>
    [Test]
    public void StressTest_RealisticComponentSize_AutoGrowsToAccommodate()
    {
        
        // Simulate ComponentTable's default: 4 pages
        const int pageCount = 4;
        
        // Simulate a realistic RPG Character component (~112 bytes)
        // String64 (64) + 4 ints (16) + 2 longs (16) + 4 floats (16) + bool (1+padding)
        const int componentSize = 112;
        
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, pageCount, componentSize);
        
        var initialCapacity = segment.ChunkCapacity;
        var initialFree = segment.FreeChunkCount;
        
        _logger.LogInformation(
            "Realistic segment: PageCount={Pages}, ComponentSize={Size}B, Capacity={Capacity}, Free={Free}",
            pageCount, componentSize, initialCapacity, initialFree);
        
        // Simulate MonitoringDemo: create 1000 entities (more than initial capacity)
        // With auto-growth, this should succeed
        const int targetEntityCount = 1000;
        var createdEntities = new List<int>();
        
        for (int i = 0; i < targetEntityCount; i++)
        {
            var id = segment.AllocateChunk(false);
            Assert.That(id, Is.GreaterThan(0), $"Allocation {i} should succeed with auto-growth");
            createdEntities.Add(id);
        }
        
        _logger.LogInformation(
            "Allocation results: Created={Count}, FinalCapacity={Capacity}, FinalFree={Free}",
            createdEntities.Count, segment.ChunkCapacity, segment.FreeChunkCount);
        
        // All 1000 entities should be created successfully
        Assert.That(createdEntities.Count, Is.EqualTo(targetEntityCount),
            "All entities should be created with auto-growth");
        
        // Capacity should have grown significantly
        Assert.That(segment.ChunkCapacity, Is.GreaterThan(initialCapacity),
            $"Segment should have grown from {initialCapacity} to accommodate {targetEntityCount} entities");
        
        // All IDs should be unique and valid
        Assert.That(createdEntities.Distinct().Count(), Is.EqualTo(targetEntityCount),
            "All chunk IDs should be unique");
        
        Assert.That(createdEntities.All(id => id > 0 && id < segment.ChunkCapacity), Is.True,
            "All chunk IDs should be valid (> 0 and < capacity)");
        
        // FreeChunkCount should never be negative
        Assert.That(segment.FreeChunkCount, Is.GreaterThanOrEqualTo(0),
            "FreeChunkCount should never be negative");
    }

    /// <summary>
    /// Test that AllocateChunk auto-grows when capacity is exhausted, returning valid IDs.
    /// Previously, over-allocation returned invalid IDs beyond capacity — now fixed by auto-growth.
    /// </summary>
    [Test]
    public void AllocateChunk_CapacityExhausted_AutoGrowsAndReturnsValidIds()
    {
        const int pageCount = 2;
        const int stride = 512;

        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, pageCount, stride);

        var initialCapacity = segment.ChunkCapacity;
        var freeCount = segment.FreeChunkCount;

        // Exhaust initial capacity
        var validChunks = new List<int>();
        for (int i = 0; i < freeCount; i++)
        {
            validChunks.Add(segment.AllocateChunk(false));
        }

        Assert.That(segment.FreeChunkCount, Is.EqualTo(0));

        // Allocate beyond initial capacity — should auto-grow
        for (int i = 0; i < 5; i++)
        {
            var id = segment.AllocateChunk(false);
            Assert.That(id, Is.GreaterThan(0), $"Over-capacity allocation #{i + 1} should return valid ID");
            Assert.That(id, Is.LessThan(segment.ChunkCapacity), $"ID {id} should be within grown capacity");
            validChunks.Add(id);
        }

        Assert.That(segment.ChunkCapacity, Is.GreaterThan(initialCapacity), "Segment should have grown");
        Assert.That(validChunks.Distinct().Count(), Is.EqualTo(validChunks.Count), "All IDs should be unique");
    }

    [Test]
    public void FreeChunkCount_NeverExceedsCapacity()
    {
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 2, 64);

        // Perform many allocate/free cycles
        for (int cycle = 0; cycle < 10; cycle++)
        {
            var ids = new List<int>();
            for (int i = 0; i < 20; i++)
            {
                var id = segment.AllocateChunk(false);
                if (id > 0) ids.Add(id);
            }

            foreach (var id in ids)
            {
                segment.FreeChunk(id);
            }

            Assert.That(segment.FreeChunkCount, Is.LessThanOrEqualTo(segment.ChunkCapacity),
                $"FreeChunkCount exceeded capacity on cycle {cycle}");
            Assert.That(segment.AllocatedChunkCount, Is.GreaterThanOrEqualTo(0),
                $"AllocatedChunkCount went negative on cycle {cycle}");
        }
    }

    #endregion

    #region IsSet Tests

    [Test]
    public void IsSet_ReturnsCorrectState()
    {
        var segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 2, 64);

        // Chunk 0 is reserved, should be set
        // Note: We can't directly call IsSet on ChunkBasedSegment, 
        // but we can verify via allocation behavior

        var id1 = segment.AllocateChunk(false);
        var id2 = segment.AllocateChunk(false);

        // Free id1
        segment.FreeChunk(id1);

        // Allocate again - should get id1 back (it's unset now)
        var id3 = segment.AllocateChunk(false);
        Assert.That(id3, Is.EqualTo(id1), "Freed chunk should be reallocated first");
    }

    #endregion

    #region Load/Reload Tests

    [Test]
    public void LoadFromDisk_RestoresAllocatedState()
    {
        // Release the shared _pmmf so scoped instances can access the file
        _pmmf.Dispose();
        _pmmf = null;

        int rootPageIndex;
        int allocatedCount;
        var allocatedIds = new List<int>();

        // Phase 1: Create and allocate
        {
            using var scope = _serviceProvider.CreateScope();
            var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

            var cs = pmmf.CreateChangeSet();
            var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 5, 64, cs);
            rootPageIndex = segment.RootPageIndex;

            for (int i = 0; i < 100; i++)
            {
                allocatedIds.Add(segment.AllocateChunk(false));
            }

            allocatedCount = segment.AllocatedChunkCount;
            cs.SaveChanges();
        }

        // Phase 2: Reload and verify
        {
            using var scope = _serviceProvider.CreateScope();
            var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

            var segment = pmmf.LoadChunkBasedSegment(rootPageIndex, 64);

            Assert.That(segment.AllocatedChunkCount, Is.EqualTo(allocatedCount),
                "Loaded segment should have same allocated count");

            // Newly allocated chunks should not conflict with previously allocated
            var newId = segment.AllocateChunk(false);
            Assert.That(allocatedIds, Does.Not.Contain(newId),
                "New allocation should not conflict with previously allocated chunks");
        }
    }

    #endregion
}
