using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Linq;

namespace Typhon.Engine.Tests;

// Covers the read-only storage-introspection surface (Module 15 Track A, A1): EnumerateStorageSegments,
// ClassifyAllPages, ReadOccupancyBits, StorageFilePageCount, GetWalTotalBytes — and the AC1 invariant that// building the coarse map performs zero page-body
// disk reads.
class StorageIntrospectionTests : TestBase<StorageIntrospectionTests>
{
    [Test]
    public void StorageFilePageCount_MatchesFileSize()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var pageCount = dbe.MMF.StorageFilePageCount;

        Assert.That(pageCount, Is.GreaterThan(0));
        Assert.That((long)pageCount * PagedMMF.PageSize, Is.EqualTo(dbe.MMF.FileSize));
    }

    [Test]
    public void EnumerateStorageSegments_ReportsComponentAndOccupancySegments()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var segments = dbe.EnumerateStorageSegments();

        Assert.That(segments, Is.Not.Empty);
        Assert.That(segments.Any(s => s.Kind == StorageSegmentKind.Occupancy), Is.True, "occupancy segment must be enumerated");
        Assert.That(segments.Any(s => s.Kind == StorageSegmentKind.Component), Is.True, "component segments must be enumerated");

        foreach (var seg in segments)
        {
            Assert.That(seg.Pages.Length, Is.GreaterThan(0), "an enumerated segment owns at least one page");
            Assert.That(seg.Pages.Span[0], Is.EqualTo(seg.RootPageIndex), "RootPageIndex is the first directory page");
        }
    }

    [Test]
    public void ClassifyAllPages_AssignsRootOccupancyAndComponentTypes()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var pageCount = dbe.MMF.StorageFilePageCount;
        var types = new StoragePageType[pageCount];
        dbe.ClassifyAllPages(types);

        Assert.That(types[0], Is.EqualTo(StoragePageType.Root), "page 0 is the reserved root header page");
        Assert.That(types[1], Is.EqualTo(StoragePageType.Occupancy), "page 1 is the occupancy-segment root");
        Assert.That(types, Does.Contain(StoragePageType.Component), "at least one component page is classified");
        Assert.That(types, Does.Contain(StoragePageType.Occupancy));
    }

    [Test]
    public void ClassifyAllPages_NoAllocatedPageIsUnknown()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var pageCount = dbe.MMF.StorageFilePageCount;
        var types = new StoragePageType[pageCount];
        dbe.ClassifyAllPages(types);

        var words = new long[(System.Math.Max(dbe.MMF.OccupancyCapacityPages, pageCount) + 63) / 64];
        dbe.MMF.ReadOccupancyBits(words);

        // The occupancy bitmap is authoritative: every allocated page must be attributable to a segment kind
        // (the registry-backed enumeration leaves none Unknown), and every unallocated page must read as Free.
        for (var p = 0; p < pageCount; p++)
        {
            var allocated = (words[p >> 6] & (1L << (p & 0x3F))) != 0;
            if (allocated)
            {
                Assert.That(types[p], Is.Not.EqualTo(StoragePageType.Unknown),
                    $"allocated page {p} must classify to a real segment kind, never Unknown");
            }
            else
            {
                Assert.That(types[p], Is.EqualTo(StoragePageType.Free), $"unallocated page {p} must classify as Free");
            }
        }
    }

    [Test]
    public void EnumerateStorageSegments_EveryEnumeratedSegmentHasAConcreteKind()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // Every registered segment self-reports a concrete kind from its persisted root header — never the
        // `Other` default, which would map back to an Unknown page.
        foreach (var seg in dbe.EnumerateStorageSegments())
        {
            Assert.That(seg.Kind, Is.Not.EqualTo(StorageSegmentKind.Other),
                $"segment at root page {seg.RootPageIndex} must carry a concrete persisted kind");
        }
    }

    [Test]
    public void ClassifyAllPages_RejectsUndersizedDestination()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var tooSmall = new StoragePageType[1];
        Assert.That(() => dbe.ClassifyAllPages(tooSmall), Throws.ArgumentException);
    }

    [Test]
    public void ReadOccupancyBits_ReportsReservedPagesAllocated()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var words = new long[(dbe.MMF.OccupancyCapacityPages + 63) / 64];
        dbe.MMF.ReadOccupancyBits(words);

        // The reserved root pages (0..3) are always allocated — their occupancy bits must be set.
        for (var p = 0; p < 4; p++)
        {
            Assert.That((words[p >> 6] & (1L << (p & 0x3F))), Is.Not.Zero, $"reserved page {p} must be allocated");
        }
    }

    [Test]
    public void Introspection_PerformsZeroDiskReads()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var before = dbe.MMF.GetMetrics().ReadFromDiskCount;

        var segments = dbe.EnumerateStorageSegments();
        var types = new StoragePageType[dbe.MMF.StorageFilePageCount];
        dbe.ClassifyAllPages(types);
        var words = new long[(dbe.MMF.OccupancyCapacityPages + 63) / 64];
        dbe.MMF.ReadOccupancyBits(words);

        var after = dbe.MMF.GetMetrics().ReadFromDiskCount;

        Assert.That(segments, Is.Not.Empty);
        Assert.That(after, Is.EqualTo(before), "coarse-map introspection must not read any page from disk");
    }

    [Test]
    public void GetWalTotalBytes_IsNonNegative()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        Assert.That(dbe.GetWalTotalBytes(), Is.GreaterThanOrEqualTo(0L));
    }

    [Test]
    public void GetOccupancyPageGovernedRange_RootGovernsFewerPagesThanSubsequent()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // Root page shares its body with the 2000-byte index section → 6000 B × 8 = 48,000 governed pages.
        // Every subsequent page has the full 8000 B data area → 8000 B × 8 = 64,000 governed pages.
        var root = dbe.GetOccupancyPageGovernedRange(0);
        Assert.That(root.FirstGovernedPage, Is.EqualTo(0L));
        Assert.That(root.GovernedCount, Is.EqualTo(48_000));

        var second = dbe.GetOccupancyPageGovernedRange(1);
        Assert.That(second.FirstGovernedPage, Is.EqualTo(48_000L));
        Assert.That(second.GovernedCount, Is.EqualTo(64_000));

        var third = dbe.GetOccupancyPageGovernedRange(2);
        Assert.That(third.FirstGovernedPage, Is.EqualTo(112_000L));
        Assert.That(third.GovernedCount, Is.EqualTo(64_000));

        // Ranges tile the file contiguously with no gap or overlap.
        Assert.That(second.FirstGovernedPage, Is.EqualTo(root.FirstGovernedPage + root.GovernedCount));
        Assert.That(third.FirstGovernedPage, Is.EqualTo(second.FirstGovernedPage + second.GovernedCount));
    }
}
