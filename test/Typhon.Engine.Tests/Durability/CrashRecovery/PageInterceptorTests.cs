using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;

namespace Typhon.Engine.Tests;

/// <summary>
/// Phase-A unit tests for the <see cref="PagedMMF"/> crash-injection interceptors and <see cref="ChaosPageIO"/> (P1.5, T-4).
/// Verifies the record-and-throw mechanism in isolation (no engine): writes are recorded in order, a crash count throws,
/// the null path is unaffected (real write→read round-trips), and <see cref="ChaosPageIO.DamagePageOnDisk"/> corrupts a real page.
/// </summary>
[TestFixture]
public class PageInterceptorTests : AllocatorTestBase
{
    private EpochManager _epochManager;
    private ManagedPagedMMFOptions _options;
    private ManagedPagedMMF _mmf;

    private static string DbName => $"T_PageIntercept_{(uint)TestContext.CurrentContext.Test.Name.GetHashCode():X8}";

    public override void Setup()
    {
        base.Setup();
        _epochManager = new EpochManager("PageInterceptEpoch", AllocationResource);
        _options = new ManagedPagedMMFOptions
        {
            DatabaseDirectory = TestDatabaseDir,
            DatabaseName = DbName,
            DatabaseCacheSize = PagedMMF.MinimumCacheSize,
        };
    }

    public override void TearDown()
    {
        _mmf?.Dispose();
        _mmf = null;
        try
        {
            _options.EnsureFileDeleted();
        }
        catch
        {
            // best-effort cleanup
        }

        base.TearDown();
    }

    private ManagedPagedMMF Open(bool fresh)
    {
        if (fresh)
        {
            _options.EnsureFileDeleted();
        }

        var logger = ServiceProvider.GetRequiredService<ILogger<PagedMMF>>();
        return new ManagedPagedMMF(ResourceRegistry, _epochManager, MemoryAllocator, _options, AllocationResource, "PageInterceptMMF", logger);
    }

    private static byte[] Page(byte fill)
    {
        var p = new byte[PagedMMF.PageSize];
        p.AsSpan().Fill(fill);
        return p;
    }

    [Test]
    [CancelAfter(5000)]
    public void Interceptor_RecordsWritesInPhysicalOrder()
    {
        _mmf = Open(fresh: true);
        var chaos = new ChaosPageIO();
        chaos.WireTo(_mmf);   // wire AFTER genesis so only our explicit writes are recorded

        _mmf.WritePageDirect(40, Page(0x11));
        _mmf.WritePageDirect(50, Page(0x22));
        _mmf.WritePageDirect(45, Page(0x33));

        Assert.That(chaos.WrittenPages, Is.EqualTo(new[] { 40, 50, 45 }), "interceptor records every physical write in order");
        Assert.That(chaos.HasCrashed, Is.False);
    }

    [Test]
    [CancelAfter(5000)]
    public void Interceptor_CrashAtNthWrite_ThrowsAndSkipsThatWrite()
    {
        _mmf = Open(fresh: true);
        var chaos = new ChaosPageIO();
        chaos.WireTo(_mmf);
        chaos.SetCrashAtPageWrite(2);   // the 2nd write must abort

        _mmf.WritePageDirect(40, Page(0x11));   // 1st — lands

        Assert.That(
            () => _mmf.WritePageDirect(50, Page(0x22)),
            Throws.TypeOf<ChaosSimulatedCrashException>(),
            "the 2nd physical write throws the simulated crash");

        Assert.That(chaos.HasCrashed, Is.True);
        Assert.That(chaos.WrittenPages, Is.EqualTo(new[] { 40 }), "only the pre-crash write was recorded; the crashing write never landed");
    }

    [Test]
    [CancelAfter(5000)]
    public void NullInterceptor_RealWriteReadRoundTrips()
    {
        _mmf = Open(fresh: true);
        // No ChaosPageIO wired → PageWriteInterceptor is null → the real RandomAccess path runs.
        var written = Page(0xAB);
        _mmf.WritePageDirect(60, written);

        var readBack = new byte[PagedMMF.PageSize];
        _mmf.ReadPageDirect(60, readBack);

        Assert.That(readBack, Is.EqualTo(written), "with no interceptor the real write/read path is unaffected");
    }

    [Test]
    [CancelAfter(5000)]
    public void DamagePageOnDisk_TornPage_CorruptsSecondHalf()
    {
        _mmf = Open(fresh: true);
        _mmf.WritePageDirect(70, Page(0x5A));
        _mmf.FlushToDisk();
        _mmf.Dispose();
        _mmf = null;

        var path = _options.BuildDatabasePathFileName();
        ChaosPageIO.DamagePageOnDisk(path, 70, PageDamageType.TornPage, PagedMMF.PageSize);

        var raw = File.ReadAllBytes(path);
        var pageStart = 70 * PagedMMF.PageSize;
        var half = PagedMMF.PageSize / 2;
        Assert.That(raw[pageStart], Is.EqualTo(0x5A), "first half is preserved");
        Assert.That(raw[pageStart + half], Is.EqualTo(0xFF), "second half is torn to 0xFF");
        Assert.That(raw[pageStart + PagedMMF.PageSize - 1], Is.EqualTo(0xFF), "torn through the end of the page");
    }
}
