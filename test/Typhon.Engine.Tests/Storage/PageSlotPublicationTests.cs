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
    private const int CachePages = 1024;

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
                options.DatabaseCacheSize = CachePages * (ulong)PagedMMF.PageSize;
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

    /// <summary>
    /// #1201: a reclaim that backs off a slot another thread claimed after its first pass must not write the slot's "ready". The claimer sets it without
    /// the slot's lock; a back-off restoring the value it read before that write would erase it, and the page would stay published and never ready —
    /// every requester timing out on it.
    /// </summary>
    /// <remarks>
    /// Both requesters miss the same page, and both first try the slot after the one holding the page before it (the miss's adjacency heuristic), so
    /// they contend for one known slot. The reclaimer is held between its unlocked first pass and the lock while the other claims the slot, publishes
    /// it and pauses before preparing it. Released, the reclaimer meets an Allocating slot. Had it withdrawn "ready" there, it is held again until the
    /// owner has marked the slot ready, so its restore lands after the owner's write — the interleaving that loses it.
    /// </remarks>
    [Test]
    [CancelAfter(20_000)]
    [VerifiesRule("PS-15")]
    public void AReclaimThatBacksOffAClaimedSlot_LeavesItsOwnersReadyAlone()
    {
        var filePageIndex = StagePageOnDisk();

        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
        Assert.That(mmf.TryGetPageResidency(filePageIndex, out _, out _), Is.False, "precondition: the page is not in this cache yet");

        // The page before it, resident: both misses then try the slot right after its slot first.
        int previousSlot;
        using (EpochGuard.Enter(mmf.EpochManager))
        {
            Assert.That(mmf.RequestPageEpoch(filePageIndex - 1, mmf.EpochManager.GlobalEpoch, out previousSlot), Is.True);
        }
        var contended = previousSlot + 1;
        Assert.That(contended, Is.LessThan(CachePages), "precondition: a slot follows the previous page's");
        Assert.That(mmf.GetFilePageIndex(contended), Is.EqualTo(-1), "precondition: the contended slot holds no page");

        var ownerThread = new int[1];
        var reclaimerThread = new int[1];
        // Not `using`: the probes capture them and may still run on the requesters' threads; they are disposed once both requesters are joined.
        var reclaimerBeforeLock = new ManualResetEventSlim();
        var releaseReclaimer = new ManualResetEventSlim();
        var reclaimerWithdrew = new ManualResetEventSlim();
        var releaseReclaimerRestore = new ManualResetEventSlim();
        var reclaimerWaits = new ManualResetEventSlim();
        var ownerPublished = new ManualResetEventSlim();
        var releaseOwner = new ManualResetEventSlim();
        bool IsReclaimer() => Environment.CurrentManagedThreadId == Volatile.Read(ref reclaimerThread[0]);
        // The probes run on the requesters' threads, which are joined before the events are disposed (finally below).
        // ReSharper disable AccessToDisposedClosure
        mmf.ReclaimBeforeLockProbe = mem =>
        {
            if (mem == contended && IsReclaimer() && !reclaimerBeforeLock.IsSet)
            {
                reclaimerBeforeLock.Set();
                releaseReclaimer.Wait();
            }
        };
        mmf.ReclaimReadyWithdrawnProbe = mem =>
        {
            if (mem == contended && IsReclaimer())
            {
                reclaimerWithdrew.Set();
                releaseReclaimerRestore.Wait();
            }
        };
        mmf.MissAfterPublishProbe = fp =>
        {
            if (fp == filePageIndex && Environment.CurrentManagedThreadId == Volatile.Read(ref ownerThread[0]))
            {
                ownerPublished.Set();
                releaseOwner.Wait();
            }
        };
        mmf.SlotNotReadyWaitProbe = fp =>
        {
            if (fp == filePageIndex && IsReclaimer())
            {
                reclaimerWaits.Set();
            }
        };
        // ReSharper restore AccessToDisposedClosure

        var reclaimer = Task.Run(() =>
        {
            Volatile.Write(ref reclaimerThread[0], Environment.CurrentManagedThreadId);
            return RequestAndRead(mmf, filePageIndex);
        });
        Task<(int MemPageIndex, bool AllOnDisk)> owner = null;
        bool bothDone;
        try
        {
            Assert.That(reclaimerBeforeLock.Wait(5_000), Is.True, "precondition: the reclaimer found the contended slot free and paused before its lock");

            owner = Task.Run(() =>
            {
                Volatile.Write(ref ownerThread[0], Environment.CurrentManagedThreadId);
                return RequestAndRead(mmf, filePageIndex);
            });
            Assert.That(ownerPublished.Wait(5_000), Is.True, "precondition: the other requester claimed the slot, published it and paused before preparing it");

            // The reclaimer now meets an Allocating slot: it either backs off at once and waits for the owner's slot like any requester, or — the defect —
            // withdraws "ready" under the lock and is held there.
            releaseReclaimer.Set();
            Assert.That(WaitHandle.WaitAny([reclaimerWithdrew.WaitHandle, reclaimerWaits.WaitHandle], 5_000), Is.Not.EqualTo(WaitHandle.WaitTimeout),
                "precondition: the reclaimer either withdrew the slot's ready or is waiting for it");

            releaseOwner.Set();
            if (reclaimerWithdrew.IsSet)
            {
                // Its restore must land after the owner's "ready", as it would when the owner prepares the slot during the reclaimer's back-off.
                SpinWait.SpinUntil(() => mmf.IsSlotReadyForTests(contended), 5_000);
            }
            releaseReclaimerRestore.Set();

            // A slot left published and not ready releases its requesters only on the 5 s page-cache lock timeout, with an exception.
            bothDone = Task.WaitAll([owner, reclaimer], 3_000);
        }
        finally
        {
            releaseReclaimer.Set();
            releaseOwner.Set();
            releaseReclaimerRestore.Set();
            mmf.ReclaimBeforeLockProbe = null;
            mmf.ReclaimReadyWithdrawnProbe = null;
            mmf.MissAfterPublishProbe = null;
            mmf.SlotNotReadyWaitProbe = null;

            // Joined before the cache is disposed under them (the scope) and before the events they wait on are.
            Task[] started = owner != null ? [owner, reclaimer] : [reclaimer];
            Task.WhenAll(started).ContinueWith(static _ => { }).Wait(10_000);
            reclaimerBeforeLock.Dispose();
            releaseReclaimer.Dispose();
            reclaimerWithdrew.Dispose();
            releaseReclaimerRestore.Dispose();
            reclaimerWaits.Dispose();
            ownerPublished.Dispose();
            releaseOwner.Dispose();
        }

        Assert.That(bothDone, Is.True, "a requester is stuck on a slot that was published but never marked ready");
        Assert.That(owner.Result.AllOnDisk, Is.True, "the owner sees the page as it is on disk");
        Assert.That(reclaimer.Result.MemPageIndex, Is.EqualTo(owner.Result.MemPageIndex), "both requesters use the owner's slot");
        Assert.That(reclaimer.Result.AllOnDisk, Is.True, "the reclaimer sees the page as it is on disk");
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
