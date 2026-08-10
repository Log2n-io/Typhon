using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;

namespace Typhon.Engine.Tests;

/// <summary>
/// The per-sector footer and the chunk-occupancy bitmap share a page's 128-byte metadata region — the bitmap growing up
/// from its start, the footer down from its end. This fixture pins the invariant that keeps them apart.
/// </summary>
/// <remarks>
/// <para>
/// The subtle part is <b>when</b> the geometry is decided. Page initialisation happens in the base logical segment,
/// which knows nothing about strides; the chunk bitmap's size is known only to the chunk-based subclass. An
/// implementation that let the base declare a default and had the subclass narrow it afterwards would be wrong in a way
/// that no single-threaded test detects: <c>CreateOrGrow</c> unlatches each page while it is still dirty, so a
/// checkpoint can persist it in the window between the two declarations. A page persisted while transiently declaring 16
/// sectors puts its footer at <c>[96,192)</c> — straight through the bitmap of any segment whose stride needs more than
/// 24 bytes of it.
/// </para>
/// <para>
/// The symptom of getting this wrong is not a checksum failure. It is silently corrupted chunk allocation: bits flipped
/// under the allocator, chunks handed out twice or lost, and — downstream — a B+Tree descent reaching a leaf it can
/// neither validate nor modify. So the invariant is asserted directly, on real pages, rather than trusted.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class SectorGeometryDeclarationTests
{
    private string _root;
    private ServiceProvider _serviceProvider;

    private static string DbName => "Tsgd_" + TestSeed.StableHash(TestContext.CurrentContext.Test.Name).ToString("X8");

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(SectorGeometryDeclarationTests), DbName);
        Directory.CreateDirectory(_root);

        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Error))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = DbName;
                opts.DatabaseDirectory = _root;
                opts.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 4;
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider?.Dispose();
        _serviceProvider = null;
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    /// <summary>
    /// Every page of a chunk-based segment, at every stride, must declare a footer that starts at or after the end of its
    /// own chunk bitmap — immediately after creation and again after a grow.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    [TestCase(8, TestName = "SectorGeometry_NeverOverlapsBitmap(stride 8 — bitmap fills the region)")]
    [TestCase(16, TestName = "SectorGeometry_NeverOverlapsBitmap(stride 16)")]
    [TestCase(24, TestName = "SectorGeometry_NeverOverlapsBitmap(stride 24)")]
    [TestCase(32, TestName = "SectorGeometry_NeverOverlapsBitmap(stride 32)")]
    [TestCase(64, TestName = "SectorGeometry_NeverOverlapsBitmap(stride 64)")]
    [TestCase(320, TestName = "SectorGeometry_NeverOverlapsBitmap(stride 320 — cluster-like)")]
    public void SectorGeometry_NeverOverlapsBitmap(int stride)
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

        var changeSet = mmf.CreateChangeSet();
        var segment = mmf.AllocateChunkBasedSegment(PageBlockType.None, 4, stride, changeSet, StorageSegmentKind.Component);
        changeSet.SaveChanges();

        AssertEveryPageIsSafe(mmf, segment, stride, "after Create");

        // Force a grow through the real path — allocating past capacity — since that is where the base initialises pages
        // the subclass then finishes, and therefore where a transient over-declaration would land.
        var growSet = mmf.CreateChangeSet();
        var target = (segment.ChunkCountPerPage * 5) + 16;
        for (var i = 0; i < target; i++)
        {
            segment.AllocateChunk(clearContent: false, growSet);
        }

        growSet.SaveChanges();
        Assert.That(segment.Length, Is.GreaterThan(4), "precondition: the segment actually grew");

        AssertEveryPageIsSafe(mmf, segment, stride, "after Grow");
    }

    private static void AssertEveryPageIsSafe(ManagedPagedMMF mmf, ChunkBasedSegment<PersistentStore> segment, int stride, string phase)
    {
        var buffer = new byte[PagedMMF.PageSize];

        for (var i = 0; i < segment.Length; i++)
        {
            var filePage = segment.Pages[i];
            mmf.ReadPageDirect(filePage, buffer);

            var declared = PageSectorFooter.ReadSectorCount(buffer);
            if (declared == 0)
            {
                continue;   // whole-page checksum: no footer, nothing to overlap
            }

            var chunkCount = i == 0 ? segment.ChunkCountRootPage : segment.ChunkCountPerPage;
            var bitmapBytes = ((chunkCount + 63) >> 6) * sizeof(long);
            var footerBase = PageSectorFooter.FooterBase(declared);

            Assert.That(footerBase, Is.GreaterThanOrEqualTo(PageSectorFooter.MetadataOffset + bitmapBytes),
                $"{phase}: page {filePage} (segment page {i}, stride {stride}) declares {declared} sectors, putting its "
                + $"footer at [{footerBase},192) — but its chunk bitmap occupies "
                + $"[{PageSectorFooter.MetadataOffset},{PageSectorFooter.MetadataOffset + bitmapBytes}). Stamping that "
                + "footer would write over the bitmap and silently corrupt chunk allocation.");
        }
    }

    /// <summary>
    /// The bitmap must survive a page being stamped, which is the property the overlap invariant exists to protect. This
    /// checks it by construction rather than by inference: fill a page's bitmap, stamp it, and compare.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void StampingAPage_LeavesItsChunkBitmapByteIdentical()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

        var changeSet = mmf.CreateChangeSet();
        var segment = mmf.AllocateChunkBasedSegment(PageBlockType.None, 4, 16, changeSet, StorageSegmentKind.Component);
        changeSet.SaveChanges();

        // Allocate a spread of chunks so the bitmap holds a distinctive pattern rather than zeros.
        for (var i = 0; i < 64; i++)
        {
            segment.AllocateChunk(clearContent: false);
        }

        var buffer = new byte[PagedMMF.PageSize];
        mmf.ReadPageDirect(segment.Pages[1], buffer);

        var bitmapBytes = ((segment.ChunkCountPerPage + 63) >> 6) * sizeof(long);
        var before = buffer.AsSpan(PageSectorFooter.MetadataOffset, bitmapBytes).ToArray();

        PagedMMF.StampPageForWrite(buffer, segment.Pages[1]);

        var after = buffer.AsSpan(PageSectorFooter.MetadataOffset, bitmapBytes).ToArray();
        Assert.That(after, Is.EqualTo(before), "stamping a page must never touch its chunk-occupancy bitmap");
        Assert.That(PagedMMF.VerifyPageImage(buffer, out _), Is.True, "and the stamped page must verify");
    }
}
