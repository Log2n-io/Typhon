using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace Typhon.Engine.Tests;

/// <summary>
/// Regression for the transient-cluster-segment growth fault: a <see cref="ChunkBasedSegment{TStore}"/> backed by a
/// <see cref="TransientStore"/> must keep serving page addresses through accessors that were created BEFORE the store
/// grew past its initial page-array capacity.
/// <para>
/// <see cref="TransientStore"/> is a mutable struct whose <c>AllocatePages</c> REASSIGNS its internal <c>_pageAddresses</c>
/// array when growth exceeds the current length (first at <c>PagesPerBlock * 4</c> pages). <see cref="ChunkAccessor{TStore}"/>
/// holds a by-value copy of the store, so a copy taken before a grow referenced the OLD, now-undersized array; indexing a
/// freshly-grown page then threw <see cref="IndexOutOfRangeException"/>. The exact trigger is <c>AllocateChunk(true)</c>:
/// it snapshots the store into a local accessor, grows the segment when the free list is empty, then clears the new chunk
/// through that stale snapshot. The fix routes the accessor's page-address resolution through the live <c>_segment.Store</c>.
/// </para>
/// <para>
/// PersistentStore was never affected — its pages live in the global page cache addressed via a stable base pointer, so a
/// by-value copy aliases the same memory. Only transient cluster segments (an archetype with a Transient component minting
/// many clusters, e.g. a spatial archetype) grow far enough to reassign the array.
/// </para>
/// </summary>
public sealed class TransientSegmentGrowthRegressionTests
{
    private IServiceProvider _serviceProvider;
    private IMemoryAllocator _allocator;
    private EpochManager _epochManager;

    [SetUp]
    public void Setup()
    {
        var services = new ServiceCollection();
        services
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager();

        _serviceProvider = services.BuildServiceProvider();
        _allocator = _serviceProvider.GetRequiredService<IMemoryAllocator>();
        _epochManager = _serviceProvider.GetRequiredService<EpochManager>();
    }

    [TearDown]
    public void TearDown()
    {
        (_serviceProvider as IDisposable)?.Dispose();
    }

    [Test]
    public void TransientSegment_GrowsPastInitialPageArray_AccessorSeesNewPages()
    {
        // The MemoryAllocator is itself an IResource — use it as the parent for the heap-backed transient blocks.
        var parent = (IResource)_allocator;

        // PagesPerBlock = 1 → initial _pageAddresses length = PagesPerBlock * 4 = 4. The store's page-address array
        // therefore RESIZES the first time the segment grows past 4 pages — the exact condition that strands a pre-grow
        // accessor's by-value store snapshot.
        var options = new TransientOptions { PagesPerBlock = 1, MaxMemoryBytes = 64L * 1024 * 1024 };
        var store = new TransientStore(options, _allocator, _epochManager, parent);

        // Large stride → ~1 chunk per page, so each AllocateChunk forces a grow and crosses the 4 → 8 → 16 → 32 array
        // resize boundaries within a handful of iterations.
        var stride = PagedMMF.PageRawDataSize - 512;

        var epochDepth = _epochManager.EnterScope();
        try
        {
            Span<int> initialPages = stackalloc int[4];
            store.AllocatePages(ref initialPages, 0, null);

            var segment = new ChunkBasedSegment<TransientStore>(_epochManager, store, stride);
            segment.Create(PageBlockType.None, StorageSegmentKind.Cluster, initialPages, false);

            // AllocateChunk(true) is the faulting path: snapshot store → grow segment → ClearChunk the new page via the
            // snapshot. Pre-fix, the allocation that crossed page 4 threw IndexOutOfRange inside ClearChunk.
            var ids = new HashSet<int>();
            for (var i = 0; i < 40; i++)
            {
                var id = segment.AllocateChunk(true);
                Assert.That(ids.Add(id), Is.True, $"chunk id {id} (iteration {i}) must be unique across grows");
            }

            Assert.That(ids.Count, Is.EqualTo(40), "all 40 transient chunks survive multiple page-array resizes");

            store.Dispose();
        }
        finally
        {
            _epochManager.ExitScope(epochDepth);
        }
    }
}
