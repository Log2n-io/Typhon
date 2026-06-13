using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Engine.Tests;

/// <summary>
/// CK-05 (C2) segment-directory A/B slot-pairing: each directory page (a segment's root + its map-extension chain) gets a
/// TWIN — a second physical slot it alternates between. Writes go to the non-current slot (gen+1 + CRC) then flip, so a torn
/// directory write can never corrupt a segment: reopen selects the valid sibling by highest generation. These tests drive the
/// raw <see cref="ManagedPagedMMF"/> (no engine/checkpoint thread, so <c>SaveChanges</c> is the only persist) and reopen via a
/// fresh DI scope on the same file — mirroring <see cref="MetaPairTests"/>'s corruption style for the meta pair.
/// </summary>
[TestFixture]
public class DirectoryPairTests
{
    private IServiceProvider _serviceProvider;

    private static string DbName => $"T_DirPair_{(uint)TestContext.CurrentContext.Test.Name.GetHashCode():X8}";

    [SetUp]
    public void Setup()
    {
        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = DbName;
                // Headroom for the multi-extension test (> 2000 data pages — the directory-only root holds 2000 entries — are
                // all dirty during Create, before SaveChanges drains them).
                options.DatabaseCacheSize = (ulong)(4096 * PagedMMF.PageSize);
                options.PagesDebugPattern = false;
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    private ManagedPagedMMF OpenScope(out IServiceScope scope)
    {
        scope = _serviceProvider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
    }

    private static ulong ReadSlotGeneration(ManagedPagedMMF mmf, int filePageIndex)
    {
        var buf = new byte[PagedMMF.PageSize];
        mmf.ReadPageDirect(filePageIndex, buf);
        return PageBaseHeader.ReadPairGeneration(buf);
    }

    private static byte[] GarbagePage()
    {
        var g = new byte[PagedMMF.PageSize];
        g.AsSpan().Fill(0xFF);   // all-0xFF → stored CRC ≠ computed CRC → slot invalid
        return g;
    }

    /// <summary>Finds the twin file-page index registered for <paramref name="primary"/> (the segment-directory page).</summary>
    private static int TwinOf(ManagedPagedMMF mmf, int primary)
    {
        foreach (var (p, twin) in mmf.DirectoryPairs)
        {
            if (p == primary)
            {
                return twin;
            }
        }

        return 0;
    }

    private static bool IsOccupied(ManagedPagedMMF mmf, int filePageIndex)
    {
        var words = new long[(Math.Max(mmf.OccupancyCapacityPages, filePageIndex + 1) + 63) / 64];
        mmf.ReadOccupancyBits(words);
        return (words[filePageIndex >> 6] & (1L << (filePageIndex & 0x3F))) != 0;
    }

    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("CK-05")]
    public void DirPair_RootGetsTwin_OccupancyMarkedAndAccounted()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

        var cs = mmf.CreateChangeSet();
        var seg = mmf.AllocateSegment(PageBlockType.None, 4, cs);
        cs.SaveChanges();

        var root = seg.RootPageIndex;
        var twin = TwinOf(mmf, root);

        Assert.That(twin, Is.GreaterThan(0), "the segment root is a directory page and must have a twin");
        Assert.That(seg.Pages.ToArray(), Does.Not.Contain(twin), "the twin is a shadow slot — never part of the segment's page list");
        Assert.That(IsOccupied(mmf, twin), Is.True, "the twin must be marked occupied so nothing else allocates it");
    }

    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("CK-05")]
    public void DirPair_AlternatesSlots_GenerationMonotonic()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

        var cs0 = mmf.CreateChangeSet();
        var seg = mmf.AllocateSegment(PageBlockType.None, 1, cs0);
        cs0.SaveChanges();                                     // persist #1: root → primary slot, gen 1

        var root = seg.RootPageIndex;
        var twin = TwinOf(mmf, root);

        // Each grow rewrites the root directory → re-persists it → alternates the slot and bumps the generation.
        var cs1 = mmf.CreateChangeSet();
        seg.Grow(4, true, cs1);
        cs1.SaveChanges();                                     // persist #2: root → twin slot, gen 2

        var cs2 = mmf.CreateChangeSet();
        seg.Grow(8, true, cs2);
        cs2.SaveChanges();                                     // persist #3: root → primary slot, gen 3

        var genPrimary = ReadSlotGeneration(mmf, root);
        var genTwin = ReadSlotGeneration(mmf, twin);

        Assert.That(Math.Abs((long)genPrimary - (long)genTwin), Is.EqualTo(1L), "the two slots hold consecutive generations");
        Assert.That(genPrimary, Is.GreaterThan(genTwin), "after an odd number of persists the primary slot holds the highest (current) generation");
    }

    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("CK-05")]
    public void DirPair_TornCurrentSlot_ReopenSelectsSibling()
    {
        const int length = 5;
        int root;
        int[] pagesBefore;
        {
            using var scope = _serviceProvider.CreateScope();
            var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

            var cs0 = mmf.CreateChangeSet();
            var seg = mmf.AllocateSegment(PageBlockType.None, length, cs0);
            cs0.SaveChanges();                                 // persist #1: the 5-page directory → one slot, gen 1
            root = seg.RootPageIndex;
            pagesBefore = seg.Pages.ToArray();                 // capture the exact directory contents to verify the sibling restores them
            var twin = TwinOf(mmf, root);

            // Re-persist the SAME 5-page directory a second time so BOTH slots are valid at the SAME page count. We do NOT
            // grow here on purpose: a grow would extend the data-page forward chain (which is NOT twinned — only directory
            // pages are), and falling back to an earlier, shorter directory would then disagree with the longer chain. The
            // directory twin only guarantees a COMPLETE root per slot; directory<->chain consistency across a grow is the
            // checkpoint's job, not the twin's. Holding the length constant isolates exactly the twin-fallback property.
            using (var guard = EpochGuard.Enter(mmf.EpochManager))
            {
                mmf.RequestPageEpoch(root, guard.Epoch, out var rootMem);
                var cs1 = mmf.CreateChangeSet();
                cs1.AddByMemPageIndex(rootMem);
                cs1.SaveChanges();                             // persist #2: same 5-page directory → the other slot, gen 2
            }

            // Tear the CURRENT (higher-generation) slot. Reopen must fall back to the still-valid sibling — the same 5-page
            // directory, consistent with the (unchanged) forward chain.
            var genRoot = ReadSlotGeneration(mmf, root);
            var genTwin = ReadSlotGeneration(mmf, twin);
            var currentSlot = genRoot >= genTwin ? root : twin;

            mmf.WritePageDirect(currentSlot, GarbagePage());
            mmf.FlushToDisk();
        }

        using var scope2 = _serviceProvider.CreateScope();
        var mmf2 = scope2.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        var reloaded = mmf2.GetSegment(root);

        Assert.That(reloaded, Is.Not.Null, "the segment must still load — the torn current slot falls back to the valid sibling");
        Assert.That(reloaded.Length, Is.EqualTo(length), "the reopened segment loads the sibling's directory, consistent with the forward chain");
        Assert.That(reloaded.Pages.ToArray(), Is.EqualTo(pagesBefore),
            "the sibling slot must restore the EXACT directory contents (every page-index entry) — a torn slot that preserved length but corrupted an interior entry would be caught here, not by the length check alone");
    }

    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("CK-05")]
    public void DirPair_BothSlotsCorrupt_OpenFailsLoudly()
    {
        int root;
        int twin;
        {
            using var scope = _serviceProvider.CreateScope();
            var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

            var cs0 = mmf.CreateChangeSet();
            var seg = mmf.AllocateSegment(PageBlockType.None, 1, cs0);
            cs0.SaveChanges();
            root = seg.RootPageIndex;
            twin = TwinOf(mmf, root);

            var cs1 = mmf.CreateChangeSet();
            seg.Grow(5, true, cs1);
            cs1.SaveChanges();                                 // make both slots valid, then corrupt both

            mmf.WritePageDirect(root, GarbagePage());
            mmf.WritePageDirect(twin, GarbagePage());
            mmf.FlushToDisk();
        }

        using var scope2 = _serviceProvider.CreateScope();
        var mmf2 = scope2.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

        Assert.That(() => mmf2.GetSegment(root),
            Throws.InstanceOf<InvalidOperationException>().With.Message.Contains("Both slots of directory page"),
            "both slots corrupt → the segment load must fail loudly, never silently return a garbage directory");
    }

    [Test]
    [CancelAfter(10000)]
    [VerifiesRule("CK-05")]
    public void MultiExtensionSegment_RoundTripsReopen()
    {
        // > 2000 directory entries forces map-extension directory pages (the directory-only root holds RootHeaderIndexSectionCount
        // = 2000 entries). Each extension page is a directory page that gets its own twin — this exercises the directory WALK in
        // ResolveDirectoryPairsForLoad (it must follow LogicalSegmentNextMapPBID through the current slot of each page and register
        // every pair before Load runs).
        const int length = 2100;
        int root;
        int[] pagesBefore;
        {
            using var scope = _serviceProvider.CreateScope();
            var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

            var cs = mmf.CreateChangeSet();
            var seg = mmf.AllocateSegment(PageBlockType.None, length, cs);
            cs.SaveChanges();
            root = seg.RootPageIndex;
            pagesBefore = seg.Pages.ToArray();

            Assert.That(seg.Length, Is.EqualTo(length));
        }

        using var scope2 = _serviceProvider.CreateScope();
        var mmf2 = scope2.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        var reloaded = mmf2.GetSegment(root);

        Assert.That(reloaded, Is.Not.Null, "a multi-extension segment must reopen — its root + every map-extension twin resolves");
        Assert.That(reloaded.Length, Is.EqualTo(length), "every directory page (root + extensions) round-trips through the slot-aware reopen");
        Assert.That(reloaded.Pages.ToArray(), Is.EqualTo(pagesBefore),
            "every one of the 2100 directory entries — spanning the root AND the map-extension page(s) — must round-trip exactly, not merely the count");
    }

    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("CK-05")]
    public void DirPair_DeleteSegment_FreesTwinAndClearsPairState()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

        var cs = mmf.CreateChangeSet();
        var seg = mmf.AllocateSegment(PageBlockType.None, 4, cs);
        cs.SaveChanges();
        var root = seg.RootPageIndex;
        var twin = TwinOf(mmf, root);

        Assert.That(twin, Is.GreaterThan(0), "precondition: the root directory page has a twin");
        Assert.That(IsOccupied(mmf, twin), Is.True, "precondition: the twin is occupancy-marked");
        Assert.That(mmf.DirectoryPairs.Any(p => p.Primary == root), Is.True, "precondition: PairState tracks the root pair");

        var cs2 = mmf.CreateChangeSet();
        mmf.DeleteSegment(root, cs2);
        cs2.SaveChanges();

        // The twin is a second physical page held only via PairState — DeleteSegment frees the segment's Pages but the twin is
        // NOT among them, so without explicit cleanup it leaks (its occupancy bit stays set forever). Worse, a stale PairState
        // entry would mis-route a cold read to the dead twin slot if the primary page is reallocated to a new segment.
        Assert.That(IsOccupied(mmf, twin), Is.False, "DeleteSegment must free the root's twin — otherwise it leaks");
        Assert.That(mmf.DirectoryPairs.Any(p => p.Primary == root), Is.False,
            "DeleteSegment must clear the PairState entry — a stale entry mis-routes a cold read after the primary is reallocated");
    }

    [Test]
    [CancelAfter(10000)]
    [VerifiesRule("CK-05")]
    public void DirPair_DeleteSegment_FreesMapExtensionTwins()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

        // A 2100-page segment overflows the 2000-entry directory-only root, forcing at least one map-extension directory page.
        // Each directory page (root + every map-ext) carries its OWN twin — the 4-page DeleteSegment test only covers the root,
        // so this exercises the map-extension twin + the map-ext page itself + PairState cleanup that the root-only case can't.
        var cs = mmf.CreateChangeSet();
        var seg = mmf.AllocateSegment(PageBlockType.None, 2100, cs);
        cs.SaveChanges();
        var root = seg.RootPageIndex;

        var dirPages = new List<int> { root };
        using (var guard = EpochGuard.Enter(mmf.EpochManager))
        {
            seg.CollectDirectoryMapExtensionPages(guard.Epoch, dirPages);
        }

        Assert.That(dirPages.Count, Is.GreaterThan(1), "a 2100-page segment must have at least one map-extension directory page beyond the root");

        var twins = dirPages.Select(p => TwinOf(mmf, p)).ToArray();
        for (int i = 0; i < dirPages.Count; i++)
        {
            Assert.That(twins[i], Is.GreaterThan(0), $"directory page {dirPages[i]} must have a twin");
            Assert.That(IsOccupied(mmf, twins[i]), Is.True, $"twin {twins[i]} of directory page {dirPages[i]} must be occupancy-marked");
        }

        // Map-extension primaries (everything after the root) are occupied directory infrastructure, outside seg.Pages.
        for (int i = 1; i < dirPages.Count; i++)
        {
            Assert.That(IsOccupied(mmf, dirPages[i]), Is.True, $"map-extension directory page {dirPages[i]} must be occupied");
        }

        var cs2 = mmf.CreateChangeSet();
        mmf.DeleteSegment(root, cs2);
        cs2.SaveChanges();

        for (int i = 0; i < dirPages.Count; i++)
        {
            Assert.That(IsOccupied(mmf, twins[i]), Is.False, $"DeleteSegment must free directory twin {twins[i]} (page {dirPages[i]}) — otherwise it leaks");
            Assert.That(mmf.DirectoryPairs.Any(p => p.Primary == dirPages[i]), Is.False, $"DeleteSegment must clear the PairState entry for directory page {dirPages[i]}");
        }
        for (int i = 1; i < dirPages.Count; i++)
        {
            Assert.That(IsOccupied(mmf, dirPages[i]), Is.False, $"DeleteSegment must free the map-extension directory page {dirPages[i]} itself");
        }
    }

    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("CK-05")]
    public void DirPair_MinTwoPages_LengthOneClampsToTwo()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

        // v4 directory-only root: a 1-page segment would have ChunkCountRootPage==0 → zero usable chunks. The allocators clamp a
        // requested length of 1 up to 2 (directory root + 1 data page); the choke-point Create assert also guards transient callers.
        var cs = mmf.CreateChangeSet();
        var plain = mmf.AllocateSegment(PageBlockType.None, 1, cs);
        var chunked = mmf.AllocateChunkBasedSegment(PageBlockType.None, 1, 64, cs);
        cs.SaveChanges();

        Assert.That(plain.Length, Is.EqualTo(2), "AllocateSegment must clamp a 1-page request to 2 (directory root + 1 data page)");
        Assert.That(chunked.Length, Is.EqualTo(2), "AllocateChunkBasedSegment must clamp a 1-page request to 2");
    }

    [Test]
    [VerifiesRule("CK-05")]
    public void DirPair_PairGenerationOffset_DoesNotCollideWithAnyDirectoryHeader()
    {
        // CK-05 stamps the pair generation at a FIXED offset, read uniformly on every page type. It must clear the header fields a
        // directory page already carries — the original offset 16 collided with LogicalSegmentHeader on directory pages (the bug
        // that forced the move to 40). This freezes the prose argument in PageBaseHeader so a future header field added in 16..43
        // fails HERE instead of silently corrupting the generation on directory pages.
        Assert.That(PageBaseHeader.PairGenerationOffset, Is.EqualTo(40), "the CK-05 pair-generation offset is load-bearing — see PageBaseHeader");
        Assert.That(PageBaseHeader.PairGenerationOffset % sizeof(ulong), Is.EqualTo(0), "must be 8-aligned for an atomic 64-bit read/write");
        Assert.That(PageBaseHeader.PairGenerationOffset + sizeof(ulong), Is.LessThanOrEqualTo(PagedMMF.PageHeaderSize),
            "the generation slot must sit within the 192-byte page header zone");

        Assert.That(LogicalSegmentHeader.Offset + LogicalSegmentHeader.Size, Is.LessThanOrEqualTo(PageBaseHeader.PairGenerationOffset),
            "a plain-directory page's LogicalSegmentHeader must not overlap the pair-generation slot");
        Assert.That(ChunkBasedSegmentHeader.Offset + ChunkBasedSegmentHeader.Size, Is.LessThanOrEqualTo(PageBaseHeader.PairGenerationOffset),
            "a chunk-segment directory page's ChunkBasedSegmentHeader must not overlap the pair-generation slot");
    }

    [Test]
    [CancelAfter(5000)]
    [VerifiesRule("CK-05")]
    public void DirPair_BothSlotsOfMapExtensionCorrupt_OpenFailsLoudlyAndBounded()
    {
        int root;
        int mapExt;
        {
            using var scope = _serviceProvider.CreateScope();
            var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

            var cs = mmf.CreateChangeSet();
            var seg = mmf.AllocateSegment(PageBlockType.None, 2100, cs);   // forces a map-extension directory page
            cs.SaveChanges();
            root = seg.RootPageIndex;

            var dirPages = new List<int> { root };
            using (var guard = EpochGuard.Enter(mmf.EpochManager))
            {
                seg.CollectDirectoryMapExtensionPages(guard.Epoch, dirPages);
            }
            Assert.That(dirPages.Count, Is.GreaterThan(1), "precondition: the segment has a map-extension directory page");
            mapExt = dirPages[1];

            // Smash BOTH slots of the MAP-EXTENSION page (mid-walk, not the root). ResolveDirectoryPairsForLoad must REACH it by
            // following the directory chain from the (intact) root, detect both-invalid, and throw loudly — and the walk must be
            // bounded by the cycle guard (CancelAfter proves no hang).
            mmf.WritePageDirect(mapExt, GarbagePage());
            mmf.WritePageDirect(TwinOf(mmf, mapExt), GarbagePage());
            mmf.FlushToDisk();
        }

        using var scope2 = _serviceProvider.CreateScope();
        var mmf2 = scope2.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

        Assert.That(() => mmf2.GetSegment(root),
            Throws.InstanceOf<InvalidOperationException>().With.Message.Contains("Both slots of directory page"),
            "both slots of a map-extension directory page corrupt → load must fail loudly mid-walk, never hang or silently truncate the directory");
    }
}
