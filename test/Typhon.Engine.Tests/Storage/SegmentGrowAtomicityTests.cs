using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// Rule <b>PS-11</b> (<c>rules/durability.md</c>): a segment grow publishes all of its pages or none.
/// <para>
/// The defect these tests lock down: <see cref="LogicalSegment{TStore}.CreateOrGrow"/> wrote the new pages into the segment's directory and linked
/// the old tail to them BEFORE it initialized them, and adopted them in memory only at the end. A throw in between (most likely a page-cache
/// back-pressure timeout while fetching a new page) left the segment at its old length in memory while its pages described the new one. Nothing
/// noticed while the process ran, because the next grow rewrote both; a shutdown in between persisted them, and the next open failed its
/// integrity check ("directory=900 chain=800" for a 300-page segment grown to 900 behind an 800-page cache). The failed attempt's pages leaked too.
/// </para>
/// <para>
/// <see cref="LogicalSegment{TStore}.GrowStepProbe"/> fails a grow at a chosen step: while initializing its new pages, while pinning the pages its
/// publish writes, or while latching them. Each must leave the segment exactly as it was.
/// </para>
/// </summary>
public sealed class SegmentGrowAtomicityTests
{
    private const int SegmentPages = 300;
    private const int GrowTo = 400;

    private readonly List<string> _bundles = [];

    [TearDown]
    public void TearDown() => SegmentGrowTestKit.DeleteBundles(_bundles);

    [Test]
    [VerifiesRule("PS-11")]
    public void AGrowThatFailsInitializingItsPages_LeavesTheSegmentAsItWas()
    {
        using var provider = SegmentGrowTestKit.CreateProvider(memPageCount: 800, "ps11_init", _bundles);
        using var scope = provider.CreateScope();
        var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = SegmentGrowTestKit.BuildSegment(pmmf, SegmentPages);
        var oldTail = segment.Pages[SegmentPages - 1];
        var allocatedBefore = SegmentGrowTestKit.AllocatedPages(pmmf);

        // Fail half-way through the new data pages, where a real grow waits for a free cache slot.
        var oldPages = new HashSet<int>(segment.Pages.ToArray());
        var newPagesSeen = 0;
        segment.GrowStepProbe = filePage =>
        {
            if (!oldPages.Contains(filePage) && ++newPagesSeen == (GrowTo - SegmentPages) / 2)
            {
                throw new SegmentGrowTestKit.InjectedGrowFault();
            }
        };

        SegmentGrowTestKit.FailGrow(pmmf, segment, GrowTo);

        SegmentGrowTestKit.AssertGrowLeftNoTrace(pmmf, segment, SegmentPages, oldTail, allocatedBefore, freshLoad: true);
        SegmentGrowTestKit.AssertStillGrows(pmmf, segment, GrowTo);
    }

    /// <summary>
    /// The old tail is the last page the grow pins (its first visit) and the last it latches (its second): every directory page is pinned, and on
    /// the second visit latched, before it. A failure there must release them all, and nothing of the publish may have happened yet.
    /// </summary>
    [TestCase(1)]
    [TestCase(2)]
    public void AGrowThatFailsPreparingItsPublish_LeavesTheSegmentAsItWas(int failingVisitOfTheOldTail)
    {
        using var provider = SegmentGrowTestKit.CreateProvider(memPageCount: 800, $"ps11_publish_{failingVisitOfTheOldTail}", _bundles);
        using var scope = provider.CreateScope();
        var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = SegmentGrowTestKit.BuildSegment(pmmf, SegmentPages);
        var oldTail = segment.Pages[SegmentPages - 1];
        var allocatedBefore = SegmentGrowTestKit.AllocatedPages(pmmf);

        var visits = 0;
        segment.GrowStepProbe = filePage =>
        {
            if (filePage == oldTail && ++visits == failingVisitOfTheOldTail)
            {
                throw new SegmentGrowTestKit.InjectedGrowFault();
            }
        };

        SegmentGrowTestKit.FailGrow(pmmf, segment, GrowTo);

        SegmentGrowTestKit.AssertGrowLeftNoTrace(pmmf, segment, SegmentPages, oldTail, allocatedBefore, freshLoad: true);

        // A latch the failed grow kept would belong to this thread and simply be re-entered here, so the next grow runs on another one.
        Task.Run(() => SegmentGrowTestKit.AssertStillGrows(pmmf, segment, GrowTo)).GetAwaiter().GetResult();
    }

    /// <summary>
    /// A grow past the root's 2 000 directory entries allocates a map-extension page and pairs it with a twin before it initializes anything. When
    /// it then fails, both go back and the pair state is as it was.
    /// </summary>
    [Test]
    public void AGrowPastTheRootDirectoryThatFails_GivesBackItsMapPageAndTwin()
    {
        const int oldLength = 1990;
        const int newLength = 2100;
        using var provider = SegmentGrowTestKit.CreateProvider(memPageCount: 800, "ps11_map_extension", _bundles);
        using var scope = provider.CreateScope();
        var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        var segment = SegmentGrowTestKit.BuildSegment(pmmf, oldLength);
        var oldTail = segment.Pages[oldLength - 1];
        var allocatedBefore = SegmentGrowTestKit.AllocatedPages(pmmf);
        var pairsBefore = pmmf.DirectoryPairs.ToHashSet();

        var oldPages = new HashSet<int>(segment.Pages.ToArray());
        var newPagesSeen = 0;
        (int Primary, int Twin) newPair = default;
        segment.GrowStepProbe = filePage =>
        {
            if (oldPages.Contains(filePage))
            {
                return;
            }
            if (++newPagesSeen == 1)
            {
                newPair = pmmf.DirectoryPairs.Single(p => !pairsBefore.Contains(p));
            }
            if (newPagesSeen == (newLength - oldLength) / 2)
            {
                throw new SegmentGrowTestKit.InjectedGrowFault();
            }
        };

        SegmentGrowTestKit.FailGrow(pmmf, segment, newLength);

        Assert.That(newPair.Primary, Is.GreaterThan(0), "the grow must have allocated and paired a map-extension page before initializing any data page");
        Assert.That(pmmf.DirectoryPairs, Is.EquivalentTo(pairsBefore), "the failed grow's directory pair must be dropped with its pages");
        Assert.That(pmmf.IsPageAllocated(newPair.Primary) || pmmf.IsPageAllocated(newPair.Twin), Is.False,
            $"map-extension page {newPair.Primary} and its twin {newPair.Twin} must go back to the occupancy map");

        // A fresh load walks all 1 990 pages inside one epoch scope, which this 800-page cache cannot hold (EP-01): the directory and chain checks
        // cover what it would.
        SegmentGrowTestKit.AssertGrowLeftNoTrace(pmmf, segment, oldLength, oldTail, allocatedBefore, freshLoad: false);
    }

    /// <summary>
    /// The <see cref="RuleMutantAttribute"/> companion: builds exactly what the pre-fix order left behind when a page fetch failed — the directory
    /// already listing the new pages and the old tail already linking to the first, while the segment never adopted them — and requires the
    /// verifier's assertion to reject it.
    /// </summary>
    [Test]
    [RuleMutant("PS-11")]
    public void APublishThatRanBeforeThePagesWereInitialized_IsRejected()
    {
        RuleMutants.AssertDetects("PS-11", SegmentGrowTestKit.Ps11Marker, () =>
        {
            using var provider = SegmentGrowTestKit.CreateProvider(memPageCount: 800, "ps11_mutant", _bundles);
            using var scope = provider.CreateScope();
            var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
            var segment = SegmentGrowTestKit.BuildSegment(pmmf, SegmentPages);
            var oldTail = segment.Pages[SegmentPages - 1];
            var allocatedBefore = SegmentGrowTestKit.AllocatedPages(pmmf);

            using (var guard = EpochGuard.Enter(pmmf.EpochManager))
            {
                Span<int> fresh = stackalloc int[GrowTo - SegmentPages];
                pmmf.AllocatePages(ref fresh);

                pmmf.RequestPageEpoch(segment.RootPageIndex, guard.Epoch, out var rootMem);
                var directory = pmmf.GetPage(rootMem).RawData<int>(0, LogicalSegment<PersistentStore>.RootHeaderIndexSectionCount);
                fresh.CopyTo(directory[SegmentPages..]);
                directory[GrowTo] = 0;

                pmmf.RequestPageEpoch(oldTail, guard.Epoch, out var tailMem);
                ref var tail = ref pmmf.GetPage(tailMem).StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset);
                tail.LogicalSegmentNextRawDataPBID = fresh[0];
            }

            SegmentGrowTestKit.AssertGrowLeftNoTrace(pmmf, segment, SegmentPages, oldTail, allocatedBefore, freshLoad: true);
        });
    }

    /// <summary>
    /// Rule PS-05 on a grow's throw path: a <see cref="ChunkBasedSegment{TStore}"/> called without a ChangeSet grows with one of its own, whose marks
    /// it must release before returning — including when the grow throws, or the pages it had initialized stay unevictable for good.
    /// </summary>
    [Test]
    [VerifiesRule("PS-05")]
    public void AChunkSegmentGrowThatThrows_StillReleasesItsLocalChangeSet()
    {
        using var provider = SegmentGrowTestKit.CreateProvider(memPageCount: 800, "ps05_grow_throw", _bundles);
        using var scope = provider.CreateScope();
        var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

        ChunkBasedSegment<PersistentStore> segment;
        using (EpochGuard.Enter(pmmf.EpochManager))
        {
            segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 2, 64);
        }

        // The first grow initializes one new page, then fails on the second.
        var initialized = 0;
        segment.GrowStepProbe = _ =>
        {
            if (++initialized == 2)
            {
                throw new SegmentGrowTestKit.InjectedGrowFault();
            }
        };

        using (EpochGuard.Enter(pmmf.EpochManager))
        {
            Assert.Throws<SegmentGrowTestKit.InjectedGrowFault>(() =>
            {
                while (true)
                {
                    segment.AllocateChunk(false);
                }
            });
        }

        SegmentGrowTestKit.AssertNoMarksHeld(pmmf);

        segment.GrowStepProbe = null;
        using (EpochGuard.Enter(pmmf.EpochManager))
        {
            Assert.DoesNotThrow(() => segment.AllocateChunk(false), "the segment must still grow after a failed attempt");
        }
    }

    /// <summary>
    /// The PS-05 verifier's <see cref="RuleMutantAttribute"/> companion: what the grow did before the fix — its own ChangeSet took a mark and the
    /// throw skipped the release — must fail the verifier's assertion.
    /// </summary>
    [Test]
    [RuleMutant("PS-05")]
    public void AMarkLeftOnALocalChangeSet_IsRejected()
    {
        RuleMutants.AssertDetects("PS-05", SegmentGrowTestKit.Ps05Marker, () =>
        {
            using var provider = SegmentGrowTestKit.CreateProvider(memPageCount: 800, "ps05_mutant", _bundles);
            using var scope = provider.CreateScope();
            var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
            using (EpochGuard.Enter(pmmf.EpochManager))
            {
                pmmf.AllocateChunkBasedSegment(PageBlockType.None, 2, 64);
            }

            var changeSet = pmmf.CreateChangeSet();
            changeSet.AddByMemPageIndex(pmmf.FirstResidentPage());
            try
            {
                SegmentGrowTestKit.AssertNoMarksHeld(pmmf);
            }
            finally
            {
                changeSet.ReleaseDirtyMarks();
            }
        });
    }
}

/// <summary>
/// The trigger rule PS-11 exists for, end to end: a real page-cache back-pressure timeout part-way through a grow. A fixture of its own, marked
/// <see cref="NonParallelizableAttribute"/> at class level, because the test shortens the process-wide <c>TimeoutOptions.Current</c> and a
/// method-level attribute does not keep other fixtures from running beside it.
/// </summary>
[NonParallelizable]
public sealed class SegmentGrowBackpressureTests
{
    private readonly List<string> _bundles = [];

    [TearDown]
    public void TearDown() => SegmentGrowTestKit.DeleteBundles(_bundles);

    /// <summary>
    /// The segment's own pages are pinned by the enclosing scope, as a transaction's would be, so its 600 new pages cannot all fit in the 800-page
    /// cache, and no checkpoint runs to make room.
    /// </summary>
    [Test]
    public void AGrowThatRunsOutOfPageCache_LeavesTheSegmentAsItWas()
    {
        var saved = TimeoutOptions.Current;
        TimeoutOptions.Current = new TimeoutOptions { PageCacheBackpressureTimeout = TimeSpan.FromMilliseconds(100) };
        try
        {
            using var provider = SegmentGrowTestKit.CreateProvider(memPageCount: 800, "ps11_backpressure", _bundles);
            using var scope = provider.CreateScope();
            var pmmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
            var segment = SegmentGrowTestKit.BuildSegment(pmmf, 300);
            var oldTail = segment.Pages[299];
            var allocatedBefore = SegmentGrowTestKit.AllocatedPages(pmmf);

            using (var guard = EpochGuard.Enter(pmmf.EpochManager))
            {
                for (var i = 0; i < segment.Length; i++)
                {
                    segment.GetPage(i, guard.Epoch, out _);
                }

                var changeSet = pmmf.CreateChangeSet();
                Assert.Throws<PageCacheBackpressureTimeoutException>(() => segment.Grow(900, true, changeSet));
                changeSet.SaveChanges();
            }

            SegmentGrowTestKit.AssertGrowLeftNoTrace(pmmf, segment, 300, oldTail, allocatedBefore, freshLoad: true);
        }
        finally
        {
            TimeoutOptions.Current = saved;
        }
    }
}

/// <summary>Shared scaffolding and assertions for the segment-grow fixtures above.</summary>
internal static class SegmentGrowTestKit
{
    /// <summary>
    /// Distinctive substring of the PS-11 verifier's own rejection messages. <see cref="RuleMutants.AssertDetects"/> requires the mutant to fail on
    /// one of THESE assertions and not on unrelated scaffolding, which is what makes the mutant evidence.
    /// </summary>
    internal const string Ps11Marker = "PS-11 violated: a failed grow left a trace";

    /// <summary>Distinctive substring of the PS-05 verifier's own rejection message.</summary>
    internal const string Ps05Marker = "PS-05 violated: a ChangeSet mark outlived its owner";

    /// <summary>Upper bound of the file page indices these fixtures allocate, for counting allocated pages.</summary>
    private const int PageScanBound = 8192;

    /// <summary>Pages added per build step. Each step commits in its own scope, so what it touched becomes evictable again.</summary>
    private const int BuildStep = 300;

    internal sealed class InjectedGrowFault() : Exception("injected: the grow step failed");

    /// <summary>
    /// The shared assertion, used by the verifiers and the mutant so the mutant is guaranteed to fail on a verifier's own message: the segment is
    /// still <paramref name="length"/> pages long in memory, in its directory and along its forward chain (and to a fresh load), and the grow kept
    /// neither a page it allocated nor a slot reference.
    /// </summary>
    internal static void AssertGrowLeftNoTrace(ManagedPagedMMF pmmf, LogicalSegment<PersistentStore> segment, int length, int oldTail, int allocatedBefore,
        bool freshLoad)
    {
        using (var guard = EpochGuard.Enter(pmmf.EpochManager))
        {
            var directory = DirectoryLength(pmmf, segment.RootPageIndex, guard.Epoch);
            pmmf.RequestPageEpoch(oldTail, guard.Epoch, out var tailMem);
            var tailNext = pmmf.GetPage(tailMem).StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset).LogicalSegmentNextRawDataPBID;

            Assert.That(segment.Length == length && directory == length && tailNext == 0, Is.True,
                $"{Ps11Marker} in the segment's pages: the segment is {segment.Length} pages in memory, its directory lists {directory} and its old "
                + $"tail links to page {tailNext}; all three must still say {length} pages (a link of 0). The in-memory segment never adopted the new "
                + "pages, so a directory or chain that mentions them is what a shutdown persists and the next open's integrity check rejects.");
        }

        var allocated = AllocatedPages(pmmf);
        Assert.That(allocated, Is.EqualTo(allocatedBefore),
            $"{Ps11Marker} on the occupancy map: {allocated - allocatedBefore} of the pages it allocated were not given back, twins included");
        Assert.That(pmmf.CountUnevictablePages().SlotRef, Is.Zero, $"{Ps11Marker} in the page cache: a slot it pinned for its publish is still referenced");

        if (freshLoad)
        {
            Assert.That(LoadAfresh(pmmf, segment.RootPageIndex), Is.EqualTo(length), $"{Ps11Marker} for a reopen: a fresh load must see the old segment");
        }
    }

    internal static void AssertNoMarksHeld(ManagedPagedMMF pmmf) =>
        Assert.That(pmmf.CountPagesWithDirtyMarks(), Is.Zero,
            $"{Ps05Marker}: with nothing running, no page may hold a mark; one that does belongs to a ChangeSet nobody released, and stays pinned for good");

    /// <summary>
    /// Grows <paramref name="segment"/> to <paramref name="length"/> expecting the injected fault, then persists what it dirtied, as the next
    /// checkpoint would.
    /// </summary>
    internal static void FailGrow(ManagedPagedMMF pmmf, LogicalSegment<PersistentStore> segment, int length)
    {
        using (EpochGuard.Enter(pmmf.EpochManager))
        {
            var changeSet = pmmf.CreateChangeSet();
            Assert.Throws<InjectedGrowFault>(() => segment.Grow(length, true, changeSet));
            changeSet.SaveChanges();
        }
    }

    /// <summary>The next grow of the same segment must work, and be what a reopen sees.</summary>
    internal static void AssertStillGrows(ManagedPagedMMF pmmf, LogicalSegment<PersistentStore> segment, int length)
    {
        segment.GrowStepProbe = null;
        using (EpochGuard.Enter(pmmf.EpochManager))
        {
            var changeSet = pmmf.CreateChangeSet();
            segment.Grow(length, true, changeSet);
            changeSet.SaveChanges();
        }

        Assert.That(LoadAfresh(pmmf, segment.RootPageIndex), Is.EqualTo(length), "the next grow of the same segment must work and be what a reopen sees");
    }

    /// <summary>Pages allocated on the occupancy map below <see cref="PageScanBound"/>.</summary>
    internal static int AllocatedPages(ManagedPagedMMF pmmf)
    {
        var n = 0;
        for (var page = 0; page < PageScanBound; page++)
        {
            if (pmmf.IsPageAllocated(page))
            {
                n++;
            }
        }
        return n;
    }

    /// <summary>Loads the segment again from its root page, as a reopen would: the directory and the chain must agree (<c>LogicalSegment.Load</c>).</summary>
    internal static int LoadAfresh(ManagedPagedMMF pmmf, int root)
    {
        using (EpochGuard.Enter(pmmf.EpochManager))
        {
            var fresh = new LogicalSegment<PersistentStore>(new PersistentStore(pmmf));
            fresh.Load(root);
            return fresh.Length;
        }
    }

    /// <summary>Entries in the root's directory before its 0 terminator. Every segment here stays below the root's 2000 entries.</summary>
    private static int DirectoryLength(ManagedPagedMMF pmmf, int root, long epoch)
    {
        pmmf.RequestPageEpoch(root, epoch, out var memPageIndex);
        var entries = pmmf.GetPage(memPageIndex).RawDataReadOnly<int>(0, LogicalSegment<PersistentStore>.RootHeaderIndexSectionCount);
        var n = 0;
        while (n < entries.Length && entries[n] != 0)
        {
            n++;
        }
        return n;
    }

    /// <summary>
    /// A clean segment of <paramref name="pages"/> pages, built in <see cref="BuildStep"/>-page steps each in its own scope and saved, so nothing of
    /// it is dirty or epoch-pinned and a small cache can hold the build.
    /// </summary>
    internal static LogicalSegment<PersistentStore> BuildSegment(ManagedPagedMMF pmmf, int pages)
    {
        LogicalSegment<PersistentStore> segment;
        using (EpochGuard.Enter(pmmf.EpochManager))
        {
            var changeSet = pmmf.CreateChangeSet();
            segment = pmmf.AllocateSegment(PageBlockType.None, Math.Min(pages, BuildStep), changeSet);
            changeSet.SaveChanges();
        }

        while (segment.Length < pages)
        {
            using (EpochGuard.Enter(pmmf.EpochManager))
            {
                var changeSet = pmmf.CreateChangeSet();
                segment.Grow(Math.Min(segment.Length + BuildStep, pages), true, changeSet);
                changeSet.SaveChanges();
            }
        }

        return segment;
    }

    internal static ServiceProvider CreateProvider(int memPageCount, string databaseName, List<string> bundles)
    {
        var services = new ServiceCollection();
        services
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Error))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = $"Typhon_{databaseName}_db";
                options.DatabaseCacheSize = (ulong)memPageCount * PagedMMF.PageSize;
                options.PagesDebugPattern = false;
                // Permits a cache below the 8 MiB floor and skips the physical fsync — both wanted here.
                options.TestMode = true;
            });

        var provider = services.BuildServiceProvider();
        provider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        bundles.Add(provider.GetRequiredService<IOptions<ManagedPagedMMFOptions>>().Value.BundleDirectory);
        return provider;
    }

    /// <summary>Deletes the databases a fixture created. Best effort: a lingering handle must not fail a green test.</summary>
    internal static void DeleteBundles(List<string> bundles)
    {
        foreach (var bundle in bundles)
        {
            try
            {
                if (Directory.Exists(bundle))
                {
                    Directory.Delete(bundle, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }

        bundles.Clear();
    }
}
