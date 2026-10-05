using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// PS-15: a slot published in the page directory is used by no other thread until its owner has prepared it, and the loser of a concurrent miss
/// leaves the winner's slot alone (#1128).
/// </summary>
/// <remarks>
/// Both interleavings are staged with the page cache's test hooks rather than raced for. The page under test is written to disk in one cache and
/// requested from a fresh one, so the request is a genuine miss that reads it back.
/// </remarks>
[TestFixture]
class PageSlotPublicationTests
{
    private const byte OnDiskPattern = 0x3C;
    private const byte WrittenPattern = 0x7E;

    private IServiceProvider _serviceProvider;

    // Test names here exceed the 63-byte database-name limit, so the database is named after the test id.
    private static string CurrentDatabaseName => $"PageSlot_{TestContext.CurrentContext.Test.ID}";

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
                options.DatabaseCacheSize = 1024UL * PagedMMF.PageSize;
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
    [VerifiesRule("PS-15")]
    public void ARequesterOfAPublishedSlot_WaitsUntilItsOwnerHasStartedTheRead()
    {
        var filePageIndex = StagePageOnDisk();

        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        Assert.That(mmf.TryGetPageResidency(filePageIndex, out _, out _), Is.False, "precondition: the page is not in this cache yet");

        using var ownerPublished = new ManualResetEventSlim();
        using var releaseOwner = new ManualResetEventSlim();
        using var secondWaits = new ManualResetEventSlim();
        mmf.MissAfterPublishProbe = fp =>
        {
            if (fp == filePageIndex)
            {
                ownerPublished.Set();
                releaseOwner.Wait();
            }
        };
        mmf.SlotNotReadyWaitProbe = fp =>
        {
            if (fp == filePageIndex)
            {
                secondWaits.Set();
            }
        };

        var owner = Task.Run(() => RequestAndRead(mmf, filePageIndex));
        Task<(int MemPageIndex, bool AllOnDisk)> second = null;
        try
        {
            Assert.That(ownerPublished.Wait(5_000), Is.True, "precondition: the owner published the slot and paused before preparing it");

            second = Task.Run(() => RequestAndRead(mmf, filePageIndex));
            Assert.That(secondWaits.Wait(5_000), Is.True,
                () => "the second requester must wait for the slot to be ready, not use it" + (second.IsFaulted ? $": {second.Exception?.InnerException}" : ""));
            // Entering the wait is not enough: it must still be waiting while the owner is held. One-directional, so it cannot flake when correct.
            Assert.That(second.Wait(50), Is.False, "the second requester must not return before the owner has prepared the slot");
        }
        finally
        {
            releaseOwner.Set();
        }

        var ownerResult = owner.GetAwaiter().GetResult();
        var secondResult = second.GetAwaiter().GetResult();
        Assert.That(ownerResult.AllOnDisk, Is.True, "the owner sees the page as it is on disk");
        Assert.That(secondResult.MemPageIndex, Is.EqualTo(ownerResult.MemPageIndex), "both requesters use the one slot");
        Assert.That(secondResult.AllOnDisk, Is.True, "the second requester sees the page as it is on disk, not the slot's earlier content");
    }

    [Test]
    [CancelAfter(10_000)]
    [VerifiesRule("PS-15")]
    public unsafe void TheLoserOfAConcurrentMiss_LeavesTheWinnersSlotAlone()
    {
        var filePageIndex = StagePageOnDisk();

        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        Assert.That(mmf.TryGetPageResidency(filePageIndex, out _, out _), Is.False, "precondition: the page is not in this cache yet");

        var loserThread = new int[1];
        using var loserClaimed = new ManualResetEventSlim();
        using var releaseLoser = new ManualResetEventSlim();
        mmf.MissBeforePublishProbe = fp =>
        {
            if (fp == filePageIndex && Environment.CurrentManagedThreadId == Volatile.Read(ref loserThread[0]))
            {
                loserClaimed.Set();
                releaseLoser.Wait();
            }
        };

        var readsBefore = mmf.GetMetrics().ReadFromDiskCount;
        var loser = Task.Run(() =>
        {
            Volatile.Write(ref loserThread[0], Environment.CurrentManagedThreadId);
            return RequestAndRead(mmf, filePageIndex);
        });

        int winnerMemPageIndex;
        try
        {
            Assert.That(loserClaimed.Wait(5_000), Is.True, "precondition: the loser claimed a slot and paused before publishing it");

            // The winner misses the same page, publishes first, and its read completes. Then a writer changes the page.
            var winner = RequestAndRead(mmf, filePageIndex);
            Assert.That(winner.AllOnDisk, Is.True, "precondition: the winner read the page from disk");
            winnerMemPageIndex = winner.MemPageIndex;
            NativeMemory.Fill(mmf.GetMemPageAddress(winnerMemPageIndex) + PagedMMF.PageHeaderSize, PagedMMF.PageRawDataSize, WrittenPattern);
        }
        finally
        {
            releaseLoser.Set();
        }

        var loserResult = loser.GetAwaiter().GetResult();
        Assert.That(loserResult.MemPageIndex, Is.EqualTo(winnerMemPageIndex), "the loser takes the winner's slot");

        var rawData = new ReadOnlySpan<byte>(mmf.GetMemPageAddress(winnerMemPageIndex) + PagedMMF.PageHeaderSize, PagedMMF.PageRawDataSize);
        Assert.That(rawData.IndexOfAnyExcept(WrittenPattern), Is.EqualTo(-1), "the write made after the winner's read survives the loser");
        Assert.That(mmf.GetMetrics().ReadFromDiskCount - readsBefore, Is.EqualTo(1), "the page is read from disk once, by the winner");
    }

    [Test]
    [CancelAfter(10_000)]
    [VerifiesRule("PS-15")]
    public void AnOwnerThatFailsBeforeItsRead_LeavesNoWaiterStuck()
    {
        var filePageIndex = StagePageOnDisk();

        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        Assert.That(mmf.TryGetPageResidency(filePageIndex, out _, out _), Is.False, "precondition: the page is not in this cache yet");

        var failingThread = new int[1];
        using var ownerPublished = new ManualResetEventSlim();
        using var secondWaits = new ManualResetEventSlim();
        mmf.MissAfterPublishProbe = fp =>
        {
            if (fp == filePageIndex && Environment.CurrentManagedThreadId == Volatile.Read(ref failingThread[0]))
            {
                ownerPublished.Set();
                secondWaits.Wait(5_000);
                throw new InjectedOwnerFault();
            }
        };
        mmf.SlotNotReadyWaitProbe = fp =>
        {
            if (fp == filePageIndex)
            {
                secondWaits.Set();
            }
        };

        var owner = Task.Run(() =>
        {
            Volatile.Write(ref failingThread[0], Environment.CurrentManagedThreadId);
            return RequestAndRead(mmf, filePageIndex);
        });
        Assert.That(ownerPublished.Wait(5_000), Is.True, "precondition: the owner published the slot and is about to fail");

        var second = Task.Run(() => RequestAndRead(mmf, filePageIndex));

        Assert.That(() => owner.GetAwaiter().GetResult(), Throws.TypeOf<InjectedOwnerFault>(), "the owner's failure reaches its caller");
        Assert.That(secondWaits.IsSet, Is.True, "precondition: the second requester was waiting on the failed owner's slot");
        var secondResult = second.GetAwaiter().GetResult();
        Assert.That(secondResult.AllOnDisk, Is.True, "the waiter looks the page up again, reads it itself and sees it as it is on disk");
    }

    [Test]
    [CancelAfter(10_000)]
    [VerifiesRule("PS-15")]
    public void AnOwnerThatFailsAfterStartingItsRead_LeavesNoWaiterStuck()
    {
        var filePageIndex = StagePageOnDisk();

        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        Assert.That(mmf.TryGetPageResidency(filePageIndex, out _, out _), Is.False, "precondition: the page is not in this cache yet");

        // Stands in for a read-task wrapper failing to allocate: the read is in flight, not yet handed to the slot.
        var failingThread = new int[1];
        using var ownerReading = new ManualResetEventSlim();
        using var secondWaits = new ManualResetEventSlim();
        mmf.MissAfterReadStartProbe = fp =>
        {
            if (fp == filePageIndex && Environment.CurrentManagedThreadId == Volatile.Read(ref failingThread[0]))
            {
                ownerReading.Set();
                secondWaits.Wait(5_000);
                throw new InjectedOwnerFault();
            }
        };
        mmf.SlotNotReadyWaitProbe = fp =>
        {
            if (fp == filePageIndex)
            {
                secondWaits.Set();
            }
        };

        var readsBefore = mmf.GetMetrics().ReadFromDiskCount;
        var owner = Task.Run(() =>
        {
            Volatile.Write(ref failingThread[0], Environment.CurrentManagedThreadId);
            return RequestAndRead(mmf, filePageIndex);
        });
        Assert.That(ownerReading.Wait(5_000), Is.True, "precondition: the owner started its read and is about to fail");

        var second = Task.Run(() => RequestAndRead(mmf, filePageIndex));

        Assert.That(() => owner.GetAwaiter().GetResult(), Throws.TypeOf<InjectedOwnerFault>(), "the owner's failure reaches its caller");
        Assert.That(secondWaits.IsSet, Is.True, "precondition: the second requester was waiting on the failed owner's slot");
        // A slot left not ready would release the waiter only on the 5 s page-cache lock timeout, with an exception.
        Assert.That(second.Wait(3_000), Is.True, "the waiter is released once the failed owner gives its slot back");
        var secondResult = second.GetAwaiter().GetResult();
        Assert.That(secondResult.AllOnDisk, Is.True, "the waiter looks the page up again, reads it itself and sees it as it is on disk");
        Assert.That(mmf.GetMetrics().ReadFromDiskCount - readsBefore, Is.EqualTo(2), "the failed owner's read, then the waiter's own");
    }

    private sealed class InjectedOwnerFault : Exception;

    /// <summary>
    /// Writes <see cref="OnDiskPattern"/> over a data page's raw data, persists it, and closes that cache. Returns the page's file index.
    /// </summary>
    private unsafe int StagePageOnDisk()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        using var guard = EpochGuard.Enter(mmf.EpochManager);

        var cs = mmf.CreateChangeSet();
        var segment = mmf.AllocateSegment(PageBlockType.None, 4, cs);
        var addr = segment.GetPageAddressExclusive(1, mmf.EpochManager.GlobalEpoch, out var memPageIndex);
        cs.AddByMemPageIndex(memPageIndex);
        NativeMemory.Fill(addr + PagedMMF.PageHeaderSize, PagedMMF.PageRawDataSize, OnDiskPattern);
        mmf.UnlatchPageExclusive(memPageIndex);
        cs.SaveChanges();
        return segment.Pages[1];
    }

    /// <summary>Requests the page from this thread, inside its own epoch scope, and reports whether its raw data is still what was written to disk.</summary>
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
