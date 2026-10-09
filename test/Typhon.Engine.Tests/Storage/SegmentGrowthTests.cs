using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1205: a segment's grow costs the pages it adds. It used to copy the whole page list twice and read every directory page — and pin and latch every one
/// of them — on every grow, which made a segment's growth quadratic in its size. A grow now reads only the directory pages it writes, from the one holding
/// the old terminator on, and keeps the in-memory directory list it appends to; these tests prove the result is still exactly the directory a full rebuild
/// would write.
/// </summary>
[VerifiesRule("PS-18")]
unsafe class SegmentGrowthTests
{
    private IServiceProvider _serviceProvider;
    private string CurrentDatabaseName => $"{TestContext.CurrentContext.Test.Name}_database";

    [SetUp]
    public void Setup()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = 32768UL * PagedMMF.PageSize;
            });

        _serviceProvider = serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        (_serviceProvider as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Grown to exact lengths across four directory pages — each landing mid-page, exactly at a page's end (the terminator then goes alone on the next
    /// page) or just past one — and persisted after every grow, so every directory page alternates between its slots: the directory matches the page list
    /// entry for entry after every grow, the forward chain links every page, and a reopen of the file loads the same list, through each page's current
    /// slot. A grow after the reopen appends to the list the load rebuilt.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public void ASegmentGrownAcrossDirectoryPages_KeepsItsDirectory()
    {
        int root;
        int[] pagesBefore;
        using (var scope = _serviceProvider.CreateScope())
        {
            var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
            using var guard = EpochGuard.Enter(mmf.EpochManager);
            var cs0 = mmf.CreateChangeSet();
            var segment = mmf.AllocateSegment(PageBlockType.None, 10, cs0);
            cs0.SaveChanges();
            root = segment.RootPageIndex;

            // 2 000 entries per directory page, the root's included.
            foreach (var length in new[] { 1_999, 2_000, 2_001, 3_999, 4_000, 4_001, 6_000 })
            {
                var cs = mmf.CreateChangeSet();
                segment.Grow(length, cs);
                cs.SaveChanges();
                Assert.That(segment.Length, Is.EqualTo(length));

                var expected = segment.Pages.ToArray();
                Assert.That(segment.VerifyDirectoryAgainst(expected), Is.EqualTo(length), $"directory after growing to {length} pages");
                Assert.That(segment.WalkForwardChainPageCount(), Is.EqualTo(length), $"forward chain after growing to {length} pages");
            }

            pagesBefore = segment.Pages.ToArray();
        }

        using var scope2 = _serviceProvider.CreateScope();
        var mmf2 = scope2.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        using var guard2 = EpochGuard.Enter(mmf2.EpochManager);
        var reloaded = mmf2.GetSegment(root);
        Assert.That(reloaded.Pages.ToArray(), Is.EqualTo(pagesBefore), "a reopen reads the list the grows wrote");

        var before = reloaded.Length;
        var cs2 = mmf2.CreateChangeSet();
        reloaded.Grow(before + 2_500, cs2);
        cs2.SaveChanges();
        Assert.That(reloaded.VerifyDirectoryAgainst(reloaded.Pages), Is.EqualTo(before + 2_500));
        Assert.That(reloaded.WalkForwardChainPageCount(), Is.EqualTo(before + 2_500));
    }

    /// <summary>
    /// PS-18: a grow costs the pages it adds. It latches and writes the directory page holding the old terminator, the new data pages and the old tail —
    /// never a directory page before the terminator's: a grow of one page on a three-page directory touches neither the root nor the first extension.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void AGrow_TouchesOnlyTheDirectoryPagesItWrites()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        using var guard = EpochGuard.Enter(mmf.EpochManager);
        var cs = mmf.CreateChangeSet();
        var segment = mmf.AllocateSegment(PageBlockType.None, 4_500, cs);
        cs.SaveChanges();
        var directory = segment.DirectoryPagesForTest.ToArray();
        Assert.That(directory, Has.Length.EqualTo(3), "premise: the root, entries 2 000 to 3 999, and the page holding the terminator");

        var touched = new HashSet<int>();
        segment.GrowStepProbe = page => touched.Add(page);
        var cs2 = mmf.CreateChangeSet();
        segment.Grow(4_501, cs2);
        cs2.SaveChanges();
        segment.GrowStepProbe = null;

        Assert.That(touched, Does.Not.Contain(directory[0]), "the root holds none of what the grow writes");
        Assert.That(touched, Does.Not.Contain(directory[1]), "nor does the first extension page");
        Assert.That(touched, Does.Contain(directory[2]), "the page holding the old terminator is the one the grow writes");
        Assert.That(segment.VerifyDirectoryAgainst(segment.Pages), Is.EqualTo(4_501));
    }

    /// <summary>
    /// A directory whose map-page chain ends before its entries do is refused at load: a full root whose link to the page holding the terminator is gone
    /// reads 2 000 entries and no end. Loaded as 2 000 pages, the next grow would append from a page list missing the directory page it must write.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void ADirectoryWhoseChainEndsEarly_IsRefusedAtLoad()
    {
        int root;
        using (var scope = _serviceProvider.CreateScope())
        {
            var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
            using var guard = EpochGuard.Enter(mmf.EpochManager);
            var cs = mmf.CreateChangeSet();
            var segment = mmf.AllocateSegment(PageBlockType.None, 2_000, cs);   // the root exactly full: the terminator alone on map page 1
            cs.SaveChanges();
            root = segment.RootPageIndex;

            mmf.RequestPageEpoch(root, guard.Epoch, out var rootMem);
            mmf.GetPage(rootMem).StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset).LogicalSegmentNextMapPBID = 0;
            var cs1 = mmf.CreateChangeSet();
            cs1.AddByMemPageIndex(rootMem);
            cs1.SaveChanges();
        }

        using var scope2 = _serviceProvider.CreateScope();
        var mmf2 = scope2.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        using var guard2 = EpochGuard.Enter(mmf2.EpochManager);
        Assert.That(() => mmf2.GetSegment(root), Throws.InstanceOf<InvalidOperationException>().With.Message.Contains("directory pages"));
    }
}
