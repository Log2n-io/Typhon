using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// The page cache past 2 GiB, for real (#945). Nightly, not in the PR gate: these allocate gigabytes. Everything they cover at a small scale —
/// the window arithmetic, the window mapping, the run split — is in the gate (<see cref="PageCacheAddressingTests"/>,
/// <see cref="PageCacheWindowTests"/>); these are the only tests that put a byte past the old 2 GiB boundary.
/// </summary>
[TestFixture]
[Explicit("Allocates gigabytes; too heavy for the PR gate")]
[Category("Nightly")]
internal sealed unsafe class LargePageCacheTests : AllocatorTestBase
{
    private const long ThreeGib = 3L << 30;

    /// <summary>L1: a 3 GiB block from the allocator, written at both ends, freed back to the baseline. No database.</summary>
    [Test]
    public void AThreeGibBlock_IsPageAligned_WritableAtBothEnds_AndFreed()
    {
        var allocator = (MemoryAllocator)MemoryAllocator;
        var baseline = allocator.PinnedBytes;

        var block = allocator.AllocateLargePinned("test.large.3gib", AllocationResource, ThreeGib, 4096, LargeBlockContents.Undefined);
        try
        {
            Assert.That((long)block.DataAsPointer % 4096, Is.Zero);
            Assert.That(allocator.PinnedBytes, Is.EqualTo(baseline + ThreeGib));

            // Only the two pages touched become resident. Nothing is asserted about the content: Undefined means undefined.
            block.DataAsPointer[0] = 0xA1;
            block.DataAsPointer[ThreeGib - 1] = 0xB2;
            Assert.That(block.DataAsPointer[0], Is.EqualTo(0xA1));
            Assert.That(block.DataAsPointer[ThreeGib - 1], Is.EqualTo(0xB2));
        }
        finally
        {
            block.Dispose();
        }

        Assert.That(allocator.PinnedBytes, Is.EqualTo(baseline));
    }

    /// <summary>
    /// L2: a 3 GiB cache end to end. More than 262 144 distinct pages are written, so slots past the old 2 GiB boundary hold real pages; they are
    /// saved, then read back by a fresh cache of the same size, which verifies every page's checksum on the way in.
    /// </summary>
    [Test]
    [CancelAfter(600_000)]
    [VerifiesRule("PS-12")]
    [VerifiesRule("PS-13")]
    public void AThreeGibCache_WritesAndReadsBackPagesPastTheOldBoundary()
    {
        const int pageCount = 262_144 + 4_096;
        var name = $"Large_{TestContext.CurrentContext.Test.ID}";
        var sp = new ServiceCollection()
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = name;
                options.DatabaseDirectory = TestDatabaseDir;
                options.DatabaseCacheSize = (ulong)ThreeGib;
                options.PagesDebugPattern = false;
            })
            .BuildServiceProvider();
        try
        {
            sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
            int[] filePages;
            var highestSlot = 0;

            using (var scope = sp.CreateScope())
            {
                var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
                Assert.That(mmf.GetGaugeSnapshot().TotalPages, Is.EqualTo((int)(ThreeGib / PagedMMF.PageSize)), "a 3 GiB cache");
                using var guard = EpochGuard.Enter(mmf.EpochManager);
                var cs = mmf.CreateChangeSet();
                var segment = mmf.AllocateSegment(PageBlockType.None, pageCount + 1, cs);
                filePages = new int[pageCount];
                for (var i = 0; i < pageCount; i++)
                {
                    var addr = segment.GetPageAddressExclusive(i + 1, mmf.EpochManager.GlobalEpoch, out var memPageIndex);
                    cs.AddByMemPageIndex(memPageIndex);
                    filePages[i] = segment.Pages[i + 1];
                    NativeMemory.Fill(addr + PagedMMF.PageHeaderSize, PagedMMF.PageRawDataSize, Pattern(filePages[i]));
                    mmf.UnlatchPageExclusive(memPageIndex);
                    highestSlot = Math.Max(highestSlot, memPageIndex);
                }

                cs.SaveChanges();

                // PS-13 past the old boundary: the address of the highest slot maps back to it.
                Assert.That(PagedMMF.MemPageIndexOfRawData(mmf.GetMemPageAddress(highestSlot) + PagedMMF.PageHeaderSize, mmf.MemPagesBaseAddress),
                    Is.EqualTo(highestSlot));
            }

            Assert.That(highestSlot, Is.GreaterThanOrEqualTo(262_144), "precondition: pages lived in slots past the old 2 GiB boundary");

            using (var scope = sp.CreateScope())
            {
                var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
                var mismatches = 0;
                foreach (var fp in filePages)
                {
                    using var guard = EpochGuard.Enter(mmf.EpochManager);
                    Assert.That(mmf.RequestPageEpoch(fp, mmf.EpochManager.GlobalEpoch, out var memPageIndex), Is.True);   // verifies the CRC
                    var raw = new ReadOnlySpan<byte>(mmf.GetMemPageAddress(memPageIndex) + PagedMMF.PageHeaderSize, PagedMMF.PageRawDataSize);
                    mismatches += raw.IndexOfAnyExcept(Pattern(fp)) == -1 ? 0 : 1;
                }

                Assert.That(mismatches, Is.Zero, "every page came back as written");
            }
        }
        finally
        {
            (sp as IDisposable)?.Dispose();
        }
    }

    private static byte Pattern(int filePageIndex) => (byte)(filePageIndex * 37 + 11);
}
