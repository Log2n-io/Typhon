using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1127, R1: a slot's disk read is recorded in a side table sized by the reads not yet observed, not in the slot. The behaviour requesters see
/// is unchanged — they wait for the read, and a failed read is rethrown to each of them.
/// </summary>
/// <remarks>
/// The pages under test are written to disk in one cache and requested from a fresh one, so each request is a genuine miss that reads its page
/// back. Same harness as <see cref="PageSlotPublicationTests"/>.
/// </remarks>
[TestFixture]
class PageReadTableTests
{
    private const byte OnDiskPattern = 0x3C;
    private const int CachePages = 256;

    private IServiceProvider _serviceProvider;

    private static string CurrentDatabaseName => $"ReadTbl_{TestContext.CurrentContext.Test.ID}";

    [SetUp]
    public void Setup()
    {
        _serviceProvider = new ServiceCollection()
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = CachePages * PagedMMF.PageSize;
                options.PagesDebugPattern = false;
                options.TestMode = true;
            })
            .BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    [Test]
    [CancelAfter(10_000)]
    public void ReadsOnceObserved_LeaveNothingInTheReadTable()
    {
        var filePages = StagePagesOnDisk(8);

        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        foreach (var fp in filePages)
        {
            Assert.That(mmf.TryGetPageResidency(fp, out _, out _), Is.False, $"precondition: page {fp} is not in this cache yet");
        }

        foreach (var fp in filePages)
        {
            var lookups = PagedMMF.ReadTableLookupsForTests;
            var (memPageIndex, allOnDisk) = RequestAndRead(mmf, fp);
            Assert.That(allOnDisk, Is.True, $"page {fp} was read from disk");
            Assert.That(PagedMMF.ReadTableLookupsForTests - lookups, Is.EqualTo(1), $"page {fp}: the requester that missed looks its read up");
            Assert.That(mmf.IsReadPendingForTests(memPageIndex), Is.False, $"page {fp}: its requester observed the read and dropped it");

            // A second request is a hit with nothing left to wait for: no lookup at all.
            lookups = PagedMMF.ReadTableLookupsForTests;
            Assert.That(RequestAndRead(mmf, fp).AllOnDisk, Is.True);
            Assert.That(PagedMMF.ReadTableLookupsForTests - lookups, Is.Zero, $"page {fp}: a hit on an observed read never touches the table");
        }

        Assert.That(mmf.ReadTaskCountForTests, Is.Zero, "the table holds only reads not yet observed, never one per page read");
    }

    [Test]
    [CancelAfter(10_000)]
    public void AFailedRead_IsRethrownToEachRequester_AndLeavesWithItsSlot()
    {
        var filePageIndex = StagePagesOnDisk(1)[0];

        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

        // The recorded task fails once the real read has landed, so nothing still writes into the slot when it is reused.
        mmf.RecordedReadInterceptor = (fp, read) => fp == filePageIndex
            ? read.ContinueWith<int>(_ => throw new InjectedReadFailure(), TaskContinuationOptions.ExecuteSynchronously)
            : read;

        Assert.That(() => RequestAndRead(mmf, filePageIndex), Throws.TypeOf<InjectedReadFailure>(), "the requester that missed");
        Assert.That(() => RequestAndRead(mmf, filePageIndex), Throws.TypeOf<InjectedReadFailure>(), "and the next one");
        Assert.That(mmf.ReadTaskCountForTests, Is.EqualTo(1), "a failed read stays recorded for its slot");
        mmf.RecordedReadInterceptor = null;

        // Cycle the cache: the failed page's slot is reclaimed for another page, and its failed read goes with it.
        for (var k = 0; k < CachePages * 2; k++)
        {
            RequestAndRead(mmf, 100_000 + k);
        }
        Assert.That(mmf.ReadTaskCountForTests, Is.Zero, "a reused slot does not carry its previous page's failed read");

        Assert.That(RequestAndRead(mmf, filePageIndex).AllOnDisk, Is.True, "the page is read again, and this time it is fine");
    }

    [Test]
    [CancelAfter(10_000)]
    public void AnOwnerThatFailsAfterStartingItsRead_LeavesNoReadRecorded()
    {
        var filePageIndex = StagePagesOnDisk(1)[0];

        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();

        var thrown = false;
        mmf.MissAfterReadStartProbe = fp =>
        {
            if (fp == filePageIndex && !thrown)
            {
                thrown = true;
                throw new InjectedReadFailure();
            }
        };

        Assert.That(() => RequestAndRead(mmf, filePageIndex), Throws.TypeOf<InjectedReadFailure>());
        Assert.That(mmf.ReadTaskCountForTests, Is.Zero, "the abandoned slot leaves nothing in the read table");

        var (memPageIndex, allOnDisk) = RequestAndRead(mmf, filePageIndex);
        Assert.That(allOnDisk, Is.True, "the next requester reads the page itself");
        Assert.That(mmf.IsReadPendingForTests(memPageIndex), Is.False);
        Assert.That(mmf.ReadTaskCountForTests, Is.Zero);
    }

    private sealed class InjectedReadFailure : Exception;

    /// <summary>
    /// Writes <see cref="OnDiskPattern"/> over the raw data of <paramref name="count"/> data pages, persists them, and closes that cache.
    /// Returns their file indices.
    /// </summary>
    private unsafe int[] StagePagesOnDisk(int count)
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        using var guard = EpochGuard.Enter(mmf.EpochManager);

        var cs = mmf.CreateChangeSet();
        var segment = mmf.AllocateSegment(PageBlockType.None, count + 1, cs);
        var filePages = new int[count];
        for (var i = 0; i < count; i++)
        {
            var addr = segment.GetPageAddressExclusive(i + 1, mmf.EpochManager.GlobalEpoch, out var memPageIndex);
            cs.AddByMemPageIndex(memPageIndex);
            NativeMemory.Fill(addr + PagedMMF.PageHeaderSize, PagedMMF.PageRawDataSize, OnDiskPattern);
            mmf.UnlatchPageExclusive(memPageIndex);
            filePages[i] = segment.Pages[i + 1];
        }
        cs.SaveChanges();
        return filePages;
    }

    /// <summary>Requests the page in its own epoch scope and reports whether its raw data is still what was written to disk.</summary>
    private static unsafe (int MemPageIndex, bool AllOnDisk) RequestAndRead(ManagedPagedMMF mmf, int filePageIndex)
    {
        using var guard = EpochGuard.Enter(mmf.EpochManager);
        if (!mmf.RequestPageEpoch(filePageIndex, mmf.EpochManager.GlobalEpoch, out var memPageIndex))
        {
            throw new InvalidOperationException($"RequestPageEpoch({filePageIndex}) failed");
        }

        var rawData = new ReadOnlySpan<byte>(mmf.GetMemPageAddress(memPageIndex) + PagedMMF.PageHeaderSize, PagedMMF.PageRawDataSize);
        return (memPageIndex, rawData.IndexOfAnyExcept(OnDiskPattern) == -1);
    }
}
