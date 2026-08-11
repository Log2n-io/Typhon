using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// The offline chunk arithmetic agrees with the engine's own, for every stride a segment can have.
/// </summary>
/// <remarks>
/// <para>
/// This is the load-bearing test under the whole cross-structure check family. Those checks read engine-defined chunk
/// headers out of raw bytes, and they locate them by re-deriving the engine's layout arithmetic from the one fact the
/// page now records — the stride (format revision 7, #753). <b>A re-derivation that drifts from the original is worse
/// than none at all</b>: it does not fail, it reads plausible-looking headers out of the wrong offsets and reports
/// confident findings about them.
/// </para>
/// <para>
/// So the two are compared directly rather than reasoned about. The engine builds a real segment at each stride and is
/// asked where its chunks are; <see cref="ChunkGeometry"/> is asked the same question with nothing but the stride. Every
/// derived quantity and a wide sweep of chunk ids must match exactly.
/// </para>
/// <para>
/// The stride set is chosen to hit the arithmetic's discontinuities rather than to be tidy: 8 is the minimum a chunk-based
/// segment allows, 64 is the alignment ceiling (padding collapses to zero at and above it, because the 192-byte page
/// header is already a multiple of it), and the awkward ones in between are where alignment padding is non-zero and where
/// <c>PageRawDataSize</c> divides unevenly. Round numbers alone would have agreed for the wrong reason.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class ChunkGeometryAgreementTests
{
    private ServiceProvider _serviceProvider;

    private static string CurrentDatabaseName => $"Tcg_{TestContext.CurrentContext.Test.Name}";

    /// <summary>Strides that between them exercise every branch of the padding and capacity arithmetic.</summary>
    private static readonly int[] Strides =
    [
        8, 12, 16, 20, 24, 31, 32, 40, 48, 56, 63, 64, 65, 96, 100, 128, 129, 192, 256, 333, 512, 1000, 2048, 3960, 4000
    ];

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Error))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = CurrentDatabaseName;
                o.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 4;
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => _serviceProvider?.Dispose();

    [Test]
    [CancelAfter(60_000)]
    public void EveryDerivedQuantityMatchesTheEngine()
    {
        var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var epoch = _serviceProvider.GetRequiredService<EpochManager>();
        var depth = epoch.EnterScope();

        var mismatches = new List<string>();
        try
        {
            foreach (var stride in Strides)
            {
                var engine = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 8, stride);
                Assert.That(engine, Is.Not.Null, $"stride {stride}: the engine refused to allocate a segment");

                var mine = ChunkGeometry.ForStride(stride);
                Compare(mismatches, stride, "Stride", engine.Stride, mine.Stride);
                Compare(mismatches, stride, "ChunkCountRootPage", engine.ChunkCountRootPage, mine.ChunkCountRootPage);
                Compare(mismatches, stride, "ChunkCountPerPage", engine.ChunkCountPerPage, mine.ChunkCountPerPage);
                Compare(mismatches, stride, "RootDataOffset", engine.RootDataOffset, mine.RootDataOffset);
                Compare(mismatches, stride, "OtherDataOffset", engine.OtherDataOffset, mine.OtherDataOffset);
            }
        }
        finally
        {
            epoch.ExitScope(depth);
        }

        Assert.That(mismatches, Is.Empty,
            "the offline geometry disagrees with the engine's, so every cross-structure check would read chunk headers "
            + "from the wrong offsets:\n  " + string.Join("\n  ", mismatches));
    }

    [Test]
    [CancelAfter(60_000)]
    public void ChunkLocationMatchesTheEngineAcrossTheWholeAddressableRange()
    {
        var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        var epoch = _serviceProvider.GetRequiredService<EpochManager>();
        var depth = epoch.EnterScope();

        const int segmentPages = 12;
        var mismatches = new List<string>();
        var idsChecked = 0;

        try
        {
            foreach (var stride in Strides)
            {
                var engine = pmmf.AllocateChunkBasedSegment(PageBlockType.None, segmentPages, stride);
                var mine = ChunkGeometry.ForStride(stride);

                // The engine throws rather than returning a bad location past the segment's own pages, so stay inside
                // the capacity it actually has. That bound is itself part of the agreement: compare it too.
                Compare(mismatches, stride, "Capacity", engine.ChunkCapacity, mine.Capacity(segmentPages));

                for (var id = 0; id < engine.ChunkCapacity; id++)
                {
                    var (enginePage, engineOffset) = engine.GetChunkLocation(id);
                    if (!mine.TryLocate(id, out var myPage, out var myChunk))
                    {
                        mismatches.Add($"stride {stride} chunk {id}: offline locator refused an id the engine resolved");
                        break;
                    }

                    if (myPage != enginePage || myChunk != engineOffset)
                    {
                        mismatches.Add($"stride {stride} chunk {id}: engine ({enginePage},{engineOffset}) vs offline ({myPage},{myChunk})");
                        break;   // one report per stride is enough; the rest would be the same bug
                    }

                    idsChecked++;
                }
            }
        }
        finally
        {
            epoch.ExitScope(depth);
        }

        Assert.That(mismatches, Is.Empty, string.Join("\n  ", mismatches));

        // A locator that resolved nothing would satisfy every assertion above.
        Assert.That(idsChecked, Is.GreaterThan(10_000),
            $"only {idsChecked} chunk ids were compared; the sweep is not exercising the arithmetic it claims to");
    }

    private static void Compare(List<string> into, int stride, string what, int engine, int offline)
    {
        if (engine != offline)
        {
            into.Add($"stride {stride}: {what} engine={engine} offline={offline}");
        }
    }
}

/// <summary>
/// The same agreement, against a real closed database rather than a bare page file.
/// </summary>
/// <remarks>
/// <para>
/// The sibling fixture proves the arithmetic across every stride, but it proves it against segments that live only in
/// page memory: a bare <c>ManagedPagedMMF</c> has no checkpoint machinery, so nothing it allocates ever reaches the file.
/// That was worth discovering rather than working around — the offline claim means nothing about bytes that are not
/// there, and a first version of that test read zeros out of a five-page file and blamed the stride.
/// </para>
/// <para>
/// So the occupancy half is proved here instead, where the environment is the one the checks actually run in: a database
/// built through the engine, checkpointed, closed. The engine's own segment descriptors are captured while it is alive
/// and become the ground truth; the file is then decoded knowing nothing but each page's recorded stride.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class ChunkGeometryOnARealDatabaseTests : IntegrityFixtureBase
{
    [Test]
    [CancelAfter(30_000)]
    public void OfflineGeometryAndOccupancyMatchEverySegmentTheEngineReports()
    {
        var descriptors = new List<StorageSegmentDescriptor>();

        using (var scope = Provider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (var i = 0; i < 256; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var comp = new CompA(i + 1, i, i);
                    tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                    tx.Commit();
                }

                uow.Flush();
            }

            dbe.ForceCheckpoint();

            foreach (var seg in dbe.EnumerateStorageSegments())
            {
                if (seg.Stride > 0)
                {
                    descriptors.Add(seg);
                }
            }
        }

        CloseEngine();

        Assert.That(descriptors, Is.Not.Empty, "precondition: the fixture must produce chunk-based segments");

        var file = File.ReadAllBytes(DamageKit.DataPath(BundlePath));
        var problems = new List<string>();

        foreach (var seg in descriptors)
        {
            var pages = seg.Pages.Span;
            var rootPage = file.AsSpan(pages[0] * IntegrityConstants.PageSize, IntegrityConstants.PageSize);
            var geometry = ChunkGeometry.FromPage(rootPage);

            if (!geometry.IsUsable)
            {
                problems.Add($"segment @{seg.RootPageIndex}: its root page records no usable stride (engine says {seg.Stride})");
                continue;
            }

            Check(problems, seg, "Stride", seg.Stride, geometry.Stride);
            Check(problems, seg, "ChunkCountRootPage", seg.ChunkCountRootPage, geometry.ChunkCountRootPage);
            Check(problems, seg, "ChunkCountPerPage", seg.ChunkCountPerPage, geometry.ChunkCountPerPage);
            Check(problems, seg, "RootDataOffset", seg.RootDataOffset, geometry.RootDataOffset);
            Check(problems, seg, "OtherDataOffset", seg.OtherDataOffset, geometry.OtherDataOffset);
            Check(problems, seg, "ChunkCapacity", seg.ChunkCapacity, geometry.Capacity(pages.Length));

            // The occupancy half. Counting rather than comparing sets, because the engine exposes a count — but the
            // count is only reachable at all by locating every chunk correctly, so a wrong offset or a wrong bit order
            // moves it.
            var allocated = 0;
            for (var id = 0; id < seg.ChunkCapacity; id++)
            {
                if (!geometry.TryLocate(id, out var ordinal, out var chunkInPage) || ordinal >= pages.Length)
                {
                    continue;
                }

                var page = file.AsSpan(pages[ordinal] * IntegrityConstants.PageSize, IntegrityConstants.PageSize);
                if (geometry.IsChunkAllocated(page, ordinal == 0, chunkInPage))
                {
                    allocated++;
                }
            }

            Check(problems, seg, "AllocatedChunkCount", seg.AllocatedChunkCount, allocated);
        }

        Assert.That(problems, Is.Empty,
            "the offline decode disagrees with the engine about a real database:\n  " + string.Join("\n  ", problems));

        // A zero-occupancy database would satisfy the count comparison trivially.
        var total = 0;
        foreach (var seg in descriptors)
        {
            total += seg.AllocatedChunkCount;
        }

        Assert.That(total, Is.GreaterThan(100), $"only {total} chunks are allocated; the fixture is not exercising occupancy");
    }

    private static void Check(List<string> into, StorageSegmentDescriptor seg, string what, int engine, int offline)
    {
        if (engine != offline)
        {
            into.Add($"segment @{seg.RootPageIndex} ({seg.Kind}, stride {seg.Stride}): {what} engine={engine} offline={offline}");
        }
    }
}
