using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1127: a page-cache slot's state is a 64-byte native record with no managed reference, all-zero is a free slot, and the periodic paths no
/// longer visit every slot — the writeback-debt bitmap (PS-16) and the gauge's per-call budget.
/// </summary>
[TestFixture]
class PageSlotRecordTests
{
    private IServiceProvider _serviceProvider;

    private static string CurrentDatabaseName => $"SlotRec_{TestContext.CurrentContext.Test.ID}";

    [SetUp]
    public void Setup()
    {
        var pageCount = TestContext.CurrentContext.Test.Properties.ContainsKey("MemPageCount")
            ? (int)TestContext.CurrentContext.Test.Properties.Get("MemPageCount")!
            : 8;

        _serviceProvider = new ServiceCollection()
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = (ulong)pageCount * PagedMMF.PageSize;
                options.PagesDebugPattern = false;
                options.TestMode = true;
            })
            .BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<PagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    // ─── The record ──────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void ASlotRecord_IsOneCacheLineWithNoManagedReference()
    {
        Assert.That(Unsafe.SizeOf<PagedMMF.PageInfoData>(), Is.EqualTo(64), "one record per cache line");
        Assert.That(RuntimeHelpers.IsReferenceOrContainsReferences<PagedMMF.PageInfoData>(), Is.False,
            "a record holding a reference would give the GC one object graph edge per slot");

        var record = default(PagedMMF.PageInfoData);
        ref var start = ref Unsafe.As<PagedMMF.PageInfoData, byte>(ref record);
        foreach (var offset in new[]
                 {
                     Unsafe.ByteOffset(ref start, ref Unsafe.As<long, byte>(ref record.WritebackGen)),
                     Unsafe.ByteOffset(ref start, ref Unsafe.As<long, byte>(ref record.CapturedGen)),
                     Unsafe.ByteOffset(ref start, ref Unsafe.As<long, byte>(ref record.AccessEpoch)),
                 })
        {
            Assert.That((long)offset % 8, Is.Zero, "a 64-bit field updated by Interlocked must be 8-byte aligned");
        }
    }

    [Test]
    public unsafe void AZeroedSlotRecord_ReadsAsAFreeSlot()
    {
        var record = default(PagedMMF.PageInfoData);
        var pi = new PagedMMF.PageInfo(&record);

        Assert.That(pi.FilePageIndex, Is.EqualTo(-1), "no page");
        Assert.That(pi.PageState, Is.EqualTo(PagedMMF.PageState.Free));
        Assert.That(pi.StateSyncRoot.LockedByThreadId, Is.Zero, "state lock free");
        Assert.That(pi.PageExclusiveLatch.LockedByThreadId, Is.Zero, "exclusive latch free");
        Assert.That(pi.SlotReady, Is.False);
        Assert.That(pi.ReadPending, Is.False);
        Assert.That(pi.WritebackGen, Is.EqualTo(pi.CapturedGen), "owes no writeback");
        Assert.That(pi.DirtyCounter + pi.ActiveChunkWriters + pi.SlotRefCount + pi.ClockSweepCounter, Is.Zero);
    }

    [Test]
    public void EverySlotHandleMember_ExposesTheRecordByRef()
    {
        // A member returned by value would be a copy: a lock taken on it, or a counter bumped through it, would change nothing in the slot.
        string[] encoded = [nameof(PagedMMF.PageInfo.FilePageIndex), nameof(PagedMMF.PageInfo.ClockSweepCounter)];
        var byValue = typeof(PagedMMF.PageInfo).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => !encoded.Contains(p.Name) && !p.PropertyType.IsByRef)
            .Select(p => p.Name)
            .ToArray();

        Assert.That(byValue, Is.Empty, "slot handle members returned by value");
    }

    [Test]
    public unsafe void ALockTakenThroughOneHandleCopy_IsSeenThroughAnother()
    {
        var record = default(PagedMMF.PageInfoData);
        var taker = new PagedMMF.PageInfo(&record);
        var observer = taker;

        taker.StateSyncRoot.EnterExclusiveAccess(ref WaitContext.Null);
        try
        {
            Assert.That(observer.StateSyncRoot.LockedByThreadId, Is.EqualTo(Environment.CurrentManagedThreadId));
        }
        finally
        {
            taker.StateSyncRoot.ExitExclusiveAccess();
        }
        Assert.That(observer.StateSyncRoot.LockedByThreadId, Is.Zero);
    }

    [Test]
    [Property("MemPageCount", 8)]
    public unsafe void ANewCache_HoldsNoPageInAnySlot_AndItsRecordsAreAligned()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<PagedMMF>();

        Assert.That((long)((nuint)mmf.SlotTableForTests.Base % 64), Is.Zero, "records are cache-line aligned");
        for (var i = 0; i < mmf.PageCacheSlotCountForDiagnostics; i++)
        {
            // A never-used slot's record is zeroed memory; read as -1, not as file page 0 (the root page).
            Assert.That(mmf.GetFilePageIndex(i), Is.EqualTo(-1), $"slot {i}");
            Assert.That(mmf.GetPageState(i), Is.EqualTo(PagedMMF.PageState.Free), $"slot {i}");
        }
    }

    // ─── Lifetime ────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [Property("MemPageCount", 8)]
    public void AfterDispose_LateCallersSeeAnEmptyCache_AndTheTableIsFreed()
    {
        var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<PagedMMF>();
        var table = mmf.SlotTableForTests;
        scope.Dispose();

        Assert.That(table.IsFreed, Is.True, "the slot table's block is freed with the store, not left to a collection");

        // What may still arrive after the store is disposed — a transaction outliving it, a diagnostic — sees an empty cache and touches
        // nothing.
        Assert.That(mmf.WritebackDebtPercent(), Is.Zero);
        Assert.That(mmf.GetGaugeSnapshot().TotalPages, Is.Zero);
        Assert.That(mmf.CountUnevictablePages(), Is.EqualTo((0, 0, 0, 0, 0, 0)));
        Assert.That(mmf.CollectDirtyMemPageIndices(), Is.Empty);
        Assert.That(mmf.EstimatedMemorySize, Is.Zero);
        Assert.DoesNotThrow(() => mmf.DecrementDirtyByDelta(0, 1));

        // The page directory (#1136) is native too, and freed with the store: the residency diagnostics find nothing rather than reading it.
        Assert.That(mmf.TryGetPageResidency(0, out _, out _), Is.False);
        Assert.That(mmf.GetClockSweepCounterForDiagnostic(0), Is.EqualTo(-1));
    }

    // ─── Writeback debt (PS-10, PS-16) ───────────────────────────────────────────────────────────────────────────

    [Test]
    [CancelAfter(5000)]
    [Property("MemPageCount", 8)]
    [VerifiesRule("PS-16")]
    public void AWriterBetweenADischargesCheckAndItsClear_LeavesTheOwedPageWithItsBit()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<PagedMMF>();
        var page = ResidentPage(scope, 2);

        mmf.MarkPageModified(page);
        var sampled = mmf.WritebackGenOf(page);   // what a checkpoint copies, then publishes after its fsync

        // The discharge finds the page settled and is about to clear its bit; a writer modifies it right then, reads the bit as still set,
        // and leaves it. The discharge must notice the new debt and put the bit back.
        var raced = false;
        mmf.DebtSettleProbe = m =>
        {
            if (m == page && !raced)
            {
                raced = true;
                mmf.MarkPageModified(page);
            }
        };
        mmf.MarkCaptured(page, sampled);

        Assert.That(raced, Is.True, "precondition: the writer ran inside the discharge's window");
        Assert.That(mmf.HasWritebackDebt(page), Is.True, "precondition: the page owes the writer's modification");
        Assert.That(mmf.IsDebtBitSetForTests(page), Is.True, "an owed page keeps its bit");
        AssertCountMatchesBits(mmf, 1);
        Assert.That(mmf.CollectDirtyMemPageIndices(), Is.EqualTo(new[] { page }), "and the checkpoint collects it");
    }

    [Test]
    [CancelAfter(5000)]
    [Property("MemPageCount", 8)]
    [VerifiesRule("PS-16")]
    public void AWriterBetweenADischargesClearAndItsReCheck_LeavesTheOwedPageWithItsBit()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<PagedMMF>();
        var page = ResidentPage(scope, 2);

        mmf.MarkPageModified(page);
        var sampled = mmf.WritebackGenOf(page);

        // The other window: the bit is already cleared, so the writer sets it itself, and the re-check finds the debt as well.
        var raced = false;
        mmf.DebtReCheckProbe = m =>
        {
            if (m == page && !raced)
            {
                raced = true;
                mmf.MarkPageModified(page);
            }
        };
        mmf.MarkCaptured(page, sampled);

        Assert.That(raced, Is.True, "precondition: the writer ran between the clear and the re-check");
        Assert.That(mmf.HasWritebackDebt(page), Is.True);
        Assert.That(mmf.IsDebtBitSetForTests(page), Is.True);
        AssertCountMatchesBits(mmf, 1);
        Assert.That(mmf.CollectDirtyMemPageIndices(), Is.EqualTo(new[] { page }));
    }

    [Test]
    [CancelAfter(5000)]
    [Property("MemPageCount", 8)]
    [VerifiesRule("PS-16")]
    public void AModificationAfterTheSample_KeepsThePageOwedAndCollected()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<PagedMMF>();
        var page = ResidentPage(scope, 2);

        mmf.MarkPageModified(page);
        var sampled = mmf.WritebackGenOf(page);
        mmf.MarkPageModified(page);   // lands after the checkpoint's sample
        mmf.MarkCaptured(page, sampled);

        Assert.That(mmf.HasWritebackDebt(page), Is.True);
        Assert.That(mmf.IsDebtBitSetForTests(page), Is.True);
        AssertCountMatchesBits(mmf, 1);
        Assert.That(mmf.CollectDirtyMemPageIndices(), Is.EqualTo(new[] { page }));
    }

    [Test]
    [CancelAfter(5000)]
    [Property("MemPageCount", 8)]
    public void ASettledPage_HasNoBit_AndIsNotCounted()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<PagedMMF>();
        var page = ResidentPage(scope, 2);

        mmf.MarkPageModified(page);
        mmf.MarkPageModified(page);
        Assert.That(mmf.DebtPageCountForTests, Is.EqualTo(1), "precondition: one owed page, however many modifications");

        mmf.MarkCaptured(page, mmf.WritebackGenOf(page));

        Assert.That(mmf.IsDebtBitSetForTests(page), Is.False);
        AssertCountMatchesBits(mmf, 0);
        Assert.That(mmf.WritebackDebtPercent(), Is.Zero);
        Assert.That(mmf.CollectDirtyMemPageIndices(), Is.Empty);
    }

    [Test]
    [CancelAfter(5000)]
    [Property("MemPageCount", 8)]
    public void ABitWithNoDebtBehindIt_IsClearedByTheNextCollect()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<PagedMMF>();
        var page = ResidentPage(scope, 2);

        mmf.RaiseDebtBitForTests(page);   // what two discharges crossing can leave behind
        Assert.That(mmf.DebtPageCountForTests, Is.EqualTo(1), "precondition: a stale bit, counted");

        Assert.That(mmf.CollectDirtyMemPageIndices(), Is.Empty, "a bit is a hint; the generations decide");
        Assert.That(mmf.IsDebtBitSetForTests(page), Is.False, "the stale bit is cleared on the way");
        AssertCountMatchesBits(mmf, 0);
    }

    [Test]
    [CancelAfter(5000)]
    [Property("MemPageCount", 256)]
    public void Collecting_VisitsOnlyTheOwedSlots_AndFindsWhatAFullScanFinds()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<PagedMMF>();
        var epochManager = scope.ServiceProvider.GetRequiredService<EpochManager>();

        var resident = new List<int>();
        using (EpochGuard.Enter(epochManager))
        {
            for (var fp = 1; fp <= 100; fp++)
            {
                Assert.That(mmf.RequestPageEpoch(fp, epochManager.GlobalEpoch, out var m), Is.True);
                resident.Add(m);
            }
        }
        int[] owed = [resident[3], resident[40], resident[97]];
        foreach (var m in owed)
        {
            mmf.MarkPageModified(m);
        }

        var slotsBefore = mmf.DebtScanSlotVisits;
        var wordsBefore = mmf.DebtScanWordVisits;
        var dirty = mmf.CollectDirtyMemPageIndices();

        Assert.That(mmf.DebtScanSlotVisits - slotsBefore, Is.EqualTo(3), "one visit per owed slot, not one per slot");
        Assert.That(mmf.DebtScanWordVisits - wordsBefore, Is.EqualTo(256 / 64), "one bitmap word per 64 slots");
        Assert.That(dirty, Is.EqualTo(FullScanForDebt(mmf)), "same set, same ascending order as a scan of every slot");
        Assert.That(dirty, Is.EquivalentTo(owed));
        Assert.That(mmf.WritebackDebtPercent(), Is.EqualTo(3 * 100 / 256));
    }

    [Test]
    [CancelAfter(5000)]
    [Property("MemPageCount", 8)]
    [VerifiesRule("PS-10")]
    public void ACaptureLandingAfterTheSlotWasReused_LeavesItsNextPageClean()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<PagedMMF>();
        var epochManager = scope.ServiceProvider.GetRequiredService<EpochManager>();

        // Every slot holds a modified page that one writer (a structural SavePages) has already settled, while another (the checkpoint) has
        // sampled the same generation and not published it yet.
        var sampled = new Dictionary<int, long>();
        using (EpochGuard.Enter(epochManager))
        {
            for (var fp = 0; fp < 8; fp++)
            {
                Assert.That(mmf.RequestPageEpoch(fp, epochManager.GlobalEpoch, out var m), Is.True);
                mmf.MarkPageModified(m);
                sampled[m] = mmf.WritebackGenOf(m);
                mmf.MarkCaptured(m, sampled[m]);
            }
        }
        Assert.That(sampled, Has.Count.EqualTo(8), "precondition: every slot of the cache holds a settled page");

        int reused;
        using (EpochGuard.Enter(epochManager))
        {
            Assert.That(mmf.RequestPageEpoch(100, epochManager.GlobalEpoch, out reused), Is.True);
        }
        Assert.That(sampled, Does.ContainKey(reused), "precondition: the new page took a reused slot");

        mmf.MarkCaptured(reused, sampled[reused]);   // the checkpoint's capture, published late for the previous occupant

        Assert.That(mmf.CapturedGenOf(reused), Is.LessThanOrEqualTo(mmf.WritebackGenOf(reused)), "a capture never runs ahead of the generation");
        Assert.That(mmf.HasWritebackDebt(reused), Is.False, "the new page owes nothing: it was never modified");
        Assert.That(mmf.IsDebtBitSetForTests(reused), Is.False);
        AssertCountMatchesBits(mmf, 0);
    }

    // ─── Gauge ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [CancelAfter(5000)]
    [Property("MemPageCount", 64)]
    public void TheGauge_VisitsAtMostItsBudgetPerCall_AndAFullRotationMatchesAWholeScan()
    {
        using var scope = _serviceProvider.CreateScope();
        var mmf = scope.ServiceProvider.GetRequiredService<PagedMMF>();
        var epochManager = scope.ServiceProvider.GetRequiredService<EpochManager>();

        using (EpochGuard.Enter(epochManager))
        {
            for (var fp = 1; fp <= 20; fp++)
            {
                Assert.That(mmf.RequestPageEpoch(fp, epochManager.GlobalEpoch, out var m), Is.True);
                if (fp % 4 == 0)
                {
                    mmf.MarkPageModified(m);
                }
            }
        }

        // The whole cache fits the default budget: one call scans every slot, as before.
        var visits = mmf.GaugeSlotVisits;
        var whole = mmf.GetGaugeSnapshot();
        Assert.That(mmf.GaugeSlotVisits - visits, Is.EqualTo(64));
        Assert.That(whole.CleanUsedPages + whole.DirtyUsedPages, Is.EqualTo(20), "precondition: 20 resident pages");
        Assert.That(whole.DirtyUsedPages, Is.EqualTo(5));

        mmf.GaugeScanBudget = 16;
        PageCacheGaugeSnapshot last = default;
        for (var call = 0; call < 4; call++)
        {
            visits = mmf.GaugeSlotVisits;
            last = mmf.GetGaugeSnapshot();
            Assert.That(mmf.GaugeSlotVisits - visits, Is.EqualTo(16), $"call {call} visits one block");
            Assert.That(last.FreePages + last.CleanUsedPages + last.DirtyUsedPages + last.ExclusivePages, Is.EqualTo(64),
                $"call {call}: every slot in exactly one bucket");
        }

        Assert.That(
            (last.FreePages, last.CleanUsedPages, last.DirtyUsedPages, last.ExclusivePages, last.EpochProtectedPages, last.PendingIoReads),
            Is.EqualTo((whole.FreePages, whole.CleanUsedPages, whole.DirtyUsedPages, whole.ExclusivePages, whole.EpochProtectedPages,
                whole.PendingIoReads)),
            "after one rotation, the same picture as a whole scan of a quiet cache");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Brings a file page into the cache and returns its slot, idle and evictable once this returns.</summary>
    private static int ResidentPage(IServiceScope scope, int filePageIndex)
    {
        var mmf = scope.ServiceProvider.GetRequiredService<PagedMMF>();
        var epochManager = scope.ServiceProvider.GetRequiredService<EpochManager>();
        using var guard = EpochGuard.Enter(epochManager);
        Assert.That(mmf.RequestPageEpoch(filePageIndex, epochManager.GlobalEpoch, out var memPageIndex), Is.True);
        return memPageIndex;
    }

    /// <summary>PS-16 at quiescence: the maintained count is exactly the number of set bits.</summary>
    private static void AssertCountMatchesBits(PagedMMF mmf, int expected)
    {
        Assert.That(mmf.DebtPageCountForTests, Is.EqualTo(expected), "maintained count");
        Assert.That(mmf.CountSetDebtBitsForTests(), Is.EqualTo(expected), "set bits");
    }

    /// <summary>What <c>CollectDirtyMemPageIndices</c> computed before #1127: every slot, in order.</summary>
    private static int[] FullScanForDebt(PagedMMF mmf)
    {
        var dirty = new List<int>();
        for (var i = 0; i < mmf.PageCacheSlotCountForDiagnostics; i++)
        {
            if (mmf.HasWritebackDebt(i) && mmf.GetPageState(i) != PagedMMF.PageState.Free)
            {
                dirty.Add(i);
            }
        }
        return dirty.ToArray();
    }
}
