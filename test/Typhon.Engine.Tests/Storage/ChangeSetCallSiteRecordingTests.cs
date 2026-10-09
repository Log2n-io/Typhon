using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests;

/// <summary>
/// CP-04 through the real call sites rather than through <see cref="ChangeSet.AddByMemPageIndex"/> alone: a write that lands after a checkpoint
/// captured and settled its page must leave that page owed, whether it is the change set's first registration of the page or a re-dirty of one it
/// already tracks (#1245).
/// </summary>
/// <remarks>
/// The capture is staged by hand — <c>MarkCaptured</c> with the page's current generation is exactly what a cycle publishes after its fsync — so the
/// ordering under test is fixed rather than raced for.
/// </remarks>
[TestFixture]
class ChangeSetCallSiteRecordingTests
{
    private IServiceProvider _serviceProvider;

    [StructLayout(LayoutKind.Sequential)]
    private struct Chunk32
    {
        public int A;
        public int B;
        public long C;
        public double D;
        public double E;
    }

    [SetUp]
    public void Setup()
    {
        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = $"cscallsite_{Guid.NewGuid():N}";
                o.DatabaseCacheSize = (ulong)(PagedMMF.MinimumMemPageCount * PagedMMF.PageSize);
                o.PagesDebugPattern = false;
                o.TestMode = true;
            });
        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    private static int MemPageOf(ChunkBasedSegment<PersistentStore> segment, int chunkId, EpochManager em)
    {
        var (pageIndex, _) = segment.GetChunkLocation(chunkId);
        segment.GetPage(pageIndex, em.GlobalEpoch, out var memPageIndex);
        return memPageIndex;
    }

    /// <summary>What a checkpoint publishes after its fsync: the page's bytes, as of now, are durable.</summary>
    private static void Settle(ManagedPagedMMF pmmf, int memPageIndex)
    {
        pmmf.MarkCaptured(memPageIndex, pmmf.WritebackGenOf(memPageIndex));
        Assert.That(pmmf.HasWritebackDebt(memPageIndex), Is.False, "precondition: the page starts settled");
    }

    /// <summary>
    /// #301's double-alloc, at the allocator: the bit an allocation sets in a page's chunk bitmap must leave the page owed, on the change set's first
    /// registration of that page and on every later one. Otherwise the earlier capture's fsync settles the page, eviction drops the bit, and the
    /// same chunk id is handed out twice.
    /// </summary>
    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("CP-04")]
    public unsafe void AllocateChunk_AfterACapture_LeavesTheBitmapPageOwed_FirstRegistrationAndReDirty()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        using var guard = EpochGuard.Enter(em);

        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 4, sizeof(Chunk32));
        var seed = segment.AllocateChunk(false);
        var page = MemPageOf(segment, seed, em);
        var cs = pmmf.CreateChangeSet();

        Settle(pmmf, page);
        var first = segment.AllocateChunk(false, cs);
        Assert.That(MemPageOf(segment, first, em), Is.EqualTo(page), "precondition: the allocation lands on the settled page");
        Assert.That(pmmf.HasWritebackDebt(page), Is.True, "first registration: the allocation's bitmap bit must be owed a write");
        Assert.That(pmmf.DirtyCounterOf(page), Is.EqualTo(1), "first registration takes the change set's one mark on the page");

        Settle(pmmf, page);
        var second = segment.AllocateChunk(false, cs);
        Assert.That(MemPageOf(segment, second, em), Is.EqualTo(page), "precondition: the allocation lands on the settled page");
        Assert.That(pmmf.HasWritebackDebt(page), Is.True,
            "re-dirty: the page is already tracked, so no second mark — but the new bit is still owed a write, or the allocation reverts on reload");
        Assert.That(pmmf.DirtyCounterOf(page), Is.EqualTo(1), "and still exactly one mark");

        cs.ReleaseDirtyMarks();
        Assert.That(pmmf.DirtyCounterOf(page), Is.Zero);
    }

    /// <summary>
    /// A write through a <see cref="ChunkAccessor{TStore}"/> bound to a change set takes the page's mark and records the modification at the write
    /// (<c>MarkSlotDirty</c>'s eager registration), in the first rental and in a later one after a capture.
    /// </summary>
    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("CP-04")]
    public unsafe void AChunkWrite_IsRecordedAtTheWrite_FirstRentalAndReDirty()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        using var guard = EpochGuard.Enter(em);

        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 4, sizeof(Chunk32));
        var chunkId = segment.AllocateChunk(false);
        var page = MemPageOf(segment, chunkId, em);
        var cs = pmmf.CreateChangeSet();

        Settle(pmmf, page);
        var rental1 = segment.CreateChunkAccessor(cs);
        rental1.GetChunk<Chunk32>(chunkId, dirty: true).A = 1;
        Assert.That(pmmf.DirtyCounterOf(page), Is.EqualTo(1), "first rental: the write takes the page's mark");
        Assert.That(pmmf.HasWritebackDebt(page), Is.True, "first rental: the write is recorded");
        rental1.Dispose();

        Settle(pmmf, page);
        var rental2 = segment.CreateChunkAccessor(cs);
        rental2.GetChunk<Chunk32>(chunkId, dirty: true).A = 2;
        Assert.That(pmmf.HasWritebackDebt(page), Is.True, "re-dirty in a later rental: the write is recorded although the page is already tracked");
        Assert.That(pmmf.DirtyCounterOf(page), Is.EqualTo(1), "and takes no second mark");
        rental2.Dispose();

        cs.ReleaseDirtyMarks();
        Assert.That(pmmf.DirtyCounterOf(page), Is.Zero);
    }

    /// <summary>
    /// A change set releases its own mark and nobody else's: with two owners on one page, the first release leaves the second owner's mark in place.
    /// In Release the clamp in <c>DecrementDirtyByDelta</c> absorbs an over-release on a lone owner, so only a co-owned page shows it.
    /// </summary>
    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("PS-05")]
    public unsafe void ReleasingOneOwnersMarks_LeavesTheOtherOwnersMarkInPlace()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        using var guard = EpochGuard.Enter(em);

        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 4, sizeof(Chunk32));
        var page = MemPageOf(segment, segment.AllocateChunk(false), em);
        var baseline = pmmf.DirtyCounterOf(page);

        var a = pmmf.CreateChangeSet();
        var b = pmmf.CreateChangeSet();
        a.AddByMemPageIndex(page);
        a.AddByMemPageIndex(page);   // a re-dirty: still one mark
        b.AddByMemPageIndex(page);
        Assert.That(pmmf.DirtyCounterOf(page), Is.EqualTo(baseline + 2), "one mark per owner");

        a.ReleaseDirtyMarks();
        Assert.That(pmmf.DirtyCounterOf(page), Is.EqualTo(baseline + 1),
            "the other owner's mark survives — releasing it would make the page evictable under a writer still in flight (#385)");

        b.ReleaseDirtyMarks();
        Assert.That(pmmf.DirtyCounterOf(page), Is.EqualTo(baseline));
    }
}
