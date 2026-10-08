using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1204: a chunk-based segment maps a chunk id to (page, offset) by dividing by its chunks-per-page count. The 32-bit magic multiplier it used was exact
/// only up to a stride-dependent id — 6 100 999 at an 8-byte stride — past which ids with remainder <c>d − 1</c> resolved to the next page at offset −1,
/// inside that page's header.
/// </summary>
[VerifiesRule("PS-18")]
public sealed class ChunkAddressingTests
{
    /// <summary>The 32-bit multiplier the engine used before #1204, and its quotient: what the boundary ids below are chosen against.</summary>
    private static uint OldQuotient(uint n, uint d) => (uint)((n * ((0x1_0000_0000UL + d - 1) / d)) >> 32);

    /// <summary>The first id the old multiplier got wrong for <paramref name="d"/>, or null if it was exact for every 32-bit id.</summary>
    private static uint? OldFirstWrongId(uint d)
    {
        // Wrong ids first appear at remainder d − 1, and once a multiple is wrong every larger one is: binary search over the multiples.
        ulong lo = 1, hi = (uint.MaxValue / d) + 1;
        bool Wrong(ulong k) => k * d - 1 <= uint.MaxValue && OldQuotient((uint)(k * d - 1), d) != (uint)((k * d - 1) / d);
        if (!Wrong(hi - 1))
        {
            return null;
        }

        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (Wrong(mid))
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return (uint)(lo * d - 1);
    }

    [Test]
    public void TheDivider_IsExact_ForEveryChunksPerPageCount_AtEveryBoundaryTheOldMultiplierMissed()
    {
        var rng = new Random(1204);
        var wrongBefore = 0;
        for (var d = 1u; d <= 4000; d++)
        {
            var divider = new ChunkPageDivider((int)d);
            var probes = new System.Collections.Generic.List<uint> { 0, 1, d - 1, d, d + 1, uint.MaxValue, uint.MaxValue - 1, uint.MaxValue - d };
            if (OldFirstWrongId(d) is { } first)
            {
                for (var k = 0u; k < 4; k++)
                {
                    probes.Add(first + k * d);
                    probes.Add(first + k * d + 1);
                }

                if (OldQuotient(first, d) != first / d)
                {
                    wrongBefore++;
                }
            }

            for (var i = 0; i < 64; i++)
            {
                probes.Add((uint)rng.NextInt64(0, uint.MaxValue + 1L));
            }

            foreach (var n in probes)
            {
                Assert.That(divider.Divide(n), Is.EqualTo(n / d), $"{n} / {d}");
            }
        }

        // The boundaries exist: without them this test would pass against the old multiplier too.
        Assert.That(wrongBefore, Is.GreaterThan(3000), "the old multiplier is wrong somewhere for most divisors — the probes must include those ids");
    }

    [Test]
    public unsafe void ASegment_LocatesTheChunksTheOldDivisionPutInTheNextPagesHeader()
    {
        using var services = new ServiceCollection()
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .BuildServiceProvider();
        var allocator = services.GetRequiredService<IMemoryAllocator>();
        var epochManager = services.GetRequiredService<EpochManager>();
        var options = new TransientOptions { PagesPerBlock = 64, MaxMemoryBytes = 96L * 1024 * 1024 };
        var store = new TransientStore(options, allocator, epochManager, (IResource)allocator);
        try
        {
            // Stride 8: 1 000 chunks per page, the old multiplier's first wrong id is 6 100 999 — page 6 101 offset 999, which it read as page 6 102 offset
            // −1. The store is a struct whose page allocation reassigns its page table, so it allocates before the segment takes its copy.
            const int pages = 6_200;
            var ids = new int[pages];
            var span = ids.AsSpan();
            store.AllocatePages(ref span, 0, null);
            var segment = new ChunkBasedSegment<TransientStore>(epochManager, store, 8);
            using (EpochGuard.Enter(epochManager))
            {
                Assert.That(segment.Create(PageBlockType.None, StorageSegmentKind.Component, ids), Is.True);
            }

            var d = segment.ChunkCountPerPage;
            var root = segment.ChunkCountRootPage;
            Assert.That(d, Is.EqualTo(1000), "the premise: an 8-byte stride holds 1 000 chunks per page");
            var first = (int)OldFirstWrongId((uint)d)!.Value;
            Assert.That(first, Is.EqualTo(6_100_999), "the premise: the old multiplier's first wrong id at this geometry");

            foreach (var id in new[] { first, first + 1, first + d, first + 50 * d, segment.ChunkCapacity - 1 })
            {
                var (page, offset) = segment.GetChunkLocation(id);
                var adjusted = id - root;
                Assert.That((page, offset), Is.EqualTo((adjusted / d + 1, adjusted % d)), $"chunk {id}");
                Assert.That(offset, Is.InRange(0, d - 1), $"chunk {id}: an offset outside the page's chunk area addresses its header or another page");
            }
        }
        finally
        {
            store.Dispose();
        }
    }

    /// <summary>
    /// The allocator's room bits come in blocks of 4 096 pages, each with a summary word: a full segment past two blocks, with chunks freed on the last
    /// page of the first block, the first of the second and the last of the second, hands out exactly those three — through the summary's skips over
    /// full words and blocks — without growing, and records them in the summary a close would write.
    /// </summary>
    [Test]
    public void TheRoomBits_FindFreeChunksAcrossRoomBlocks()
    {
        using var services = new ServiceCollection()
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .BuildServiceProvider();
        var allocator = services.GetRequiredService<IMemoryAllocator>();
        var epochManager = services.GetRequiredService<EpochManager>();
        var options = new TransientOptions { PagesPerBlock = 64, MaxMemoryBytes = 96L * 1024 * 1024 };
        var store = new TransientStore(options, allocator, epochManager, (IResource)allocator);
        try
        {
            const int pages = 8_300;
            var ids = new int[pages];
            var span = ids.AsSpan();
            store.AllocatePages(ref span, 0, null);
            var segment = new ChunkBasedSegment<TransientStore>(epochManager, store, 4000);
            using (EpochGuard.Enter(epochManager))
            {
                Assert.That(segment.Create(PageBlockType.None, StorageSegmentKind.Component, ids), Is.True);
                while (segment.FreeChunkCount > 0)
                {
                    segment.AllocateChunk(false);
                }

                // The first chunk of each chosen page: the root holds none, so page p's first chunk is (p − 1) × chunks-per-page.
                var freed = new System.Collections.Generic.HashSet<int>();
                foreach (var page in new[] { 4_095, 4_096, 8_191 })
                {
                    var id = (page - 1) * segment.ChunkCountPerPage;
                    Assert.That(segment.GetChunkLocation(id), Is.EqualTo((page, 0)), $"premise: chunk {id} is page {page}'s first");
                    segment.FreeChunk(id);
                    freed.Add(id);
                }

                var summary = segment.CaptureSummary();
                foreach (var page in new[] { 4_095, 4_096, 8_191 })
                {
                    Assert.That(summary.HasRoom(page), Is.True, $"page {page}'s room is in the summary");
                }

                segment.RewindAllocationCursorForTest();
                var taken = new System.Collections.Generic.HashSet<int>();
                for (var i = 0; i < freed.Count; i++)
                {
                    taken.Add(segment.AllocateChunk(false));
                }

                Assert.That(taken, Is.EquivalentTo(freed), "each freed chunk, found across the blocks");
                Assert.That(segment.Length, Is.EqualTo(pages), "without growing");
                Assert.That(segment.FreeChunkCount, Is.Zero);
            }
        }
        finally
        {
            store.Dispose();
        }
    }

    /// <summary>
    /// A segment's chunk ids stay <c>int</c>s below <see cref="ChunkBasedSegment{TStore}.MaxChunkCount"/>: at the largest page count its capacity is
    /// within the space, and one page more is refused with <see cref="ResourceExhaustedException"/> rather than a capacity that wraps negative — at
    /// every geometry, the densest (one chunk per page is the extreme) included.
    /// </summary>
    [TestCase(1)]
    [TestCase(31)]
    [TestCase(1000)]
    [TestCase(8000)]
    public void TheCapacity_StopsAtTheChunkIdSpace(int chunksPerPage)
    {
        var max = ChunkBasedSegment<PersistentStore>.MaxChunkCount;
        var pages = ChunkBasedSegment<PersistentStore>.MaxPageCountOf(0, chunksPerPage);
        Assert.That(ChunkBasedSegment<PersistentStore>.CapacityOf(0, chunksPerPage, pages), Is.LessThanOrEqualTo(max));
        Assert.That(ChunkBasedSegment<PersistentStore>.CheckedCapacityOf(0, chunksPerPage, pages, 8), Is.LessThanOrEqualTo(max));
        Assert.That(ChunkBasedSegment<PersistentStore>.CapacityOf(0, chunksPerPage, pages + 1L), Is.GreaterThan((long)max),
            "the largest page count is the largest: one more page leaves the chunk-id space");
        if (pages < int.MaxValue)
        {
            Assert.That(() => ChunkBasedSegment<PersistentStore>.CheckedCapacityOf(0, chunksPerPage, pages + 1, 8),
                Throws.InstanceOf<ResourceExhaustedException>());
        }
    }
}
