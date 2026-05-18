using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

// Covers the read-only page-body detail surface (Module 15 Track A, A2): TryReadPageBody, GetPageResidency,
// PageBodyReadCount — and the AC3 invariant that the detail tier reads page bodies through the no-clock-sweep
// path so introspection never perturbs the page-cache eviction heuristic.
class StorageDetailTests : TestBase<StorageDetailTests>
{
    [Test]
    public void TryReadPageBody_CopiesPageContent()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var page = new byte[dbe.MMF.StoragePageSize];
        // Page 1 is the occupancy-segment root — always allocated, never empty.
        var ok = dbe.TryReadPageBody(1, page);

        Assert.That(ok, Is.True);
        Assert.That(page, Has.Some.Not.EqualTo((byte)0), "an allocated page body is not all-zero");
    }

    [Test]
    public void TryReadPageBody_RejectsOutOfRangeIndex()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var page = new byte[dbe.MMF.StoragePageSize];

        Assert.That(dbe.TryReadPageBody(-1, page), Is.False);
        Assert.That(dbe.TryReadPageBody(dbe.MMF.StorageFilePageCount, page), Is.False);
    }

    [Test]
    public void TryReadPageBody_RejectsUndersizedDestination()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var tooSmall = new byte[64];
        Assert.That(() => dbe.TryReadPageBody(1, tooSmall), Throws.ArgumentException);
    }

    [Test]
    public void TryReadPageBody_DoesNotBumpClockSweep()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        // A resident page whose clock-sweep counter is below the cap — so an unchanged reading is meaningful.
        var page = -1;
        var before = 0;
        for (var p = 0; p < dbe.MMF.StorageFilePageCount; p++)
        {
            var c = dbe.MMF.GetClockSweepCounterForDiagnostic(p);
            if (c >= 0 && c < 5)
            {
                page = p;
                before = c;
                break;
            }
        }
        Assert.That(page, Is.GreaterThanOrEqualTo(0), "expected at least one resident page below the clock-sweep cap");

        var buffer = new byte[dbe.MMF.StoragePageSize];
        for (var i = 0; i < 4; i++)
        {
            Assert.That(dbe.TryReadPageBody(page, buffer), Is.True);
        }

        Assert.That(dbe.MMF.GetClockSweepCounterForDiagnostic(page), Is.EqualTo(before),
            "the no-clock-sweep read path must leave the eviction counter untouched");
    }

    [Test]
    public void GetPageResidency_ReportsResidentAfterRead()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var buffer = new byte[dbe.MMF.StoragePageSize];
        Assert.That(dbe.TryReadPageBody(1, buffer), Is.True);

        dbe.GetPageResidency(1, out var resident, out _);
        Assert.That(resident, Is.True, "a page just read is resident in the cache");
    }

    [Test]
    public void GetPageResidency_OutOfRangeIsNotResident()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        dbe.GetPageResidency(dbe.MMF.StorageFilePageCount, out var resident, out var dirty);

        Assert.That(resident, Is.False);
        Assert.That(dirty, Is.False);
    }

    [Test]
    public void PageBodyReadCount_IncrementsPerRead()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var before = dbe.PageBodyReadCount;
        var buffer = new byte[dbe.MMF.StoragePageSize];
        var reads = 0;
        for (var p = 0; p < 5 && p < dbe.MMF.StorageFilePageCount; p++)
        {
            if (dbe.TryReadPageBody(p, buffer))
            {
                reads++;
            }
        }

        Assert.That(reads, Is.GreaterThan(0));
        Assert.That(dbe.PageBodyReadCount - before, Is.EqualTo(reads), "each successful read increments the counter exactly once");
    }
}
